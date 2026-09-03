using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Diagnostics;
using NAudio.Wave;

namespace CPT.Core.Tts;

// Voice-cloning TTS via Chatterbox running in a persistent Python subprocess.
// Requires bootstrap_chatterbox.ps1 to have been run successfully (Python venv
// at tools/voiceclone/venv with chatterbox-tts installed).
//
// Cold start: ~5-15s (model load). Warm synthesis: ~1-3s per short utterance
// on a CUDA GPU, much slower on CPU.
public sealed class ChatterboxTts : ITtsEngine, IDisposable
{
    private readonly string _pythonExe;
    private readonly string _script;
    private Process? _proc;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _sampleRate = 24000;
    private bool _disposed;

    // Set by the host so warmup / first synthesis can stream model-load
    // progress to the UI. Optional.
    public IProgress<string>? Progress { get; set; }
    public bool IsWarm => _proc is not null && !_proc.HasExited;

    public string Engine => "chatterbox";
    public int SampleRate => _sampleRate;
    public int Channels => 1;
    public int BitsPerSample => 16;

    public ChatterboxTts(string pythonExe, string scriptPath)
    {
        _pythonExe = pythonExe;
        _script = scriptPath;
    }

    // Cached probe result keyed by (pythonExe, scriptPath). The import takes
    // ~8-12s cold (loads diffusers, transformers, torch); we don't want to pay
    // that on every editor open.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _availCache = new();

    /// <summary>
    /// How long the import probe gets.
    ///
    /// Measured at 26.5s warm on this machine: importing chatterbox.tts pulls in
    /// diffusers, transformers and torch. The old 30s budget was under the warm
    /// time, so a cold start timed out and cloning silently reported itself
    /// unavailable for the whole session.
    /// </summary>
    private static readonly TimeSpan AvailabilityProbeTimeout = TimeSpan.FromMinutes(2);

    public static bool IsAvailable(string pythonExe, string scriptPath)
    {
        if (!File.Exists(pythonExe) || !File.Exists(scriptPath)) return false;

        var key = pythonExe + "|" + scriptPath;
        if (_availCache.TryGetValue(key, out var cached)) return cached;

        // Probe Python env. Timeout generously — `import chatterbox.tts`
        // transitively loads diffusers + transformers + torch and routinely
        // takes 8-15s on a cold filesystem. -W ignore silences the pkg_resources
        // DeprecationWarning so PowerShell-style stderr-as-error doesn't matter.
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = "-W ignore -c \"import importlib; importlib.import_module('chatterbox.tts'); importlib.import_module('torch'); print('ok')\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            if (!p.WaitForExit((int)AvailabilityProbeTimeout.TotalMilliseconds))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                // NOT cached. A timeout is "we did not find out", not "no", and
                // caching it turned one slow cold start into cloning being
                // unavailable until the app was restarted.
                CptLog.Write("[tts] clone availability probe timed out; will try again later");
                return false;
            }

            var ok = p.ExitCode == 0;
            if (!ok) CptLog.Write("[tts] clone availability probe failed with exit code " + p.ExitCode);
            return _availCache[key] = ok;
        }
        catch { return _availCache[key] = false; }
    }

    public static void InvalidateAvailabilityCache() => _availCache.Clear();

    public async Task WarmAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await EnsureProcessAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task EnsureProcessAsync(CancellationToken ct)
    {
        if (_proc != null && !_proc.HasExited) return;

        Progress?.Report("Starting Chatterbox subprocess…");
        var psi = new ProcessStartInfo
        {
            FileName = _pythonExe,
            Arguments = $"-u \"{_script}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start chatterbox subprocess.");
        // Drain stderr (mostly noise: HuggingFace download bars, transformers warnings)
        // but echo each line to progress so a hang has a visible cause.
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await _proc.StandardError.ReadLineAsync()) is not null)
                {
                    if (line.Length > 0) Progress?.Report("[py] " + line);
                }
            }
            catch { }
        // The pump outlives this warm-up call: the subprocess stays alive between
        // utterances, so its stderr must keep draining even after ct is cancelled.
        }, CancellationToken.None);

        var startedAt = DateTime.UtcNow;
        while (true)
        {
            var line = await _proc.StandardOutput.ReadLineAsync(ct);
            if (line is null) throw new InvalidOperationException(
                "Chatterbox subprocess exited during startup.");
            JsonElement el;
            try { el = JsonDocument.Parse(line).RootElement; }
            catch { Progress?.Report(line); continue; }
            if (el.TryGetProperty("status", out var status))
            {
                var s = status.GetString();
                if (s == "loading")
                {
                    Progress?.Report("Loading Chatterbox model into VRAM (first run downloads ~3 GB)…");
                }
                else if (s == "ready")
                {
                    if (el.TryGetProperty("sr", out var sr)) _sampleRate = sr.GetInt32();
                    var elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
                    var dev = el.TryGetProperty("device", out var d) ? d.GetString() : "?";
                    Progress?.Report($"Chatterbox ready on {dev} ({elapsed:F1}s).");
                    return;
                }
                else if (s == "error")
                {
                    var err = el.GetProperty("error").GetString() ?? "unknown error";
                    Progress?.Report("ERROR: " + err);
                    throw new InvalidOperationException(err);
                }
            }
        }
    }

    public async IAsyncEnumerable<byte[]> SynthesizeStreamAsync(
        string text, string voiceRef, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureProcessAsync(ct);
            var outPath = Path.Combine(Path.GetTempPath(), $"cpt_clone_{Guid.NewGuid():N}.wav");
            var cmd = JsonSerializer.Serialize(new { op = "synth", text, @ref = voiceRef, @out = outPath });
            await _proc!.StandardInput.WriteLineAsync(cmd);
            await _proc.StandardInput.FlushAsync(ct);

            var line = await _proc.StandardOutput.ReadLineAsync(ct);
            if (line is null) throw new InvalidOperationException("Chatterbox subprocess closed unexpectedly.");
            var resp = JsonDocument.Parse(line).RootElement;
            if (!resp.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                throw new InvalidOperationException(resp.GetProperty("error").GetString() ?? "synth failed");
            if (resp.TryGetProperty("sr", out var sr)) _sampleRate = sr.GetInt32();

            // Read WAV file, yield raw PCM as a single chunk.
            byte[] pcm;
            using (var reader = new WaveFileReader(outPath))
            {
                using var ms = new MemoryStream();
                var buf = new byte[8192];
                int n;
                while ((n = reader.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                pcm = ms.ToArray();
            }
            try { File.Delete(outPath); } catch { }
            yield return pcm;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_proc != null && !_proc.HasExited)
            {
                try { _proc.StandardInput.WriteLine("{\"op\":\"quit\"}"); } catch { }
                if (!_proc.WaitForExit(1500)) _proc.Kill();
            }
        }
        catch { }
        finally { _proc?.Dispose(); _gate.Dispose(); }
    }
}
