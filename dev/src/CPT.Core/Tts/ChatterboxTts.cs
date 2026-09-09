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
    private bool _ready;
    private string _loadedModel = "";
    public string CloneModel { get; set; } = "original";


    /// <summary>
    /// How much intonation the clone adds, 0 to 1.
    ///
    /// Chatterbox's own default of 0.5 performs rather than reads, which is why
    /// a deliberately monotone reference came back with intonation it never had.
    /// Lower keeps the delivery of the reference clip, which is the point of
    /// cloning a voice at all. Set per persona before each reply.
    /// </summary>
    public double Expressiveness { get; set; } = 0.3;

    // Set by the host so warmup / first synthesis can stream model-load
    // progress to the UI. Optional.
    public IProgress<string>? Progress { get; set; }
    public bool IsWarm => _loadedModel == (CloneModel == "turbo" ? "turbo" : "original") && _ready && _proc is not null && !_proc.HasExited;

    public string Engine => "chatterbox";
    public int SampleRate => _sampleRate;
    public int Channels => 1;
    public int BitsPerSample => 16;

    public ChatterboxTts(string pythonExe, string scriptPath)
    {
        _pythonExe = pythonExe;
        _script = scriptPath;
    }

    // Module discovery checks installation without importing the inference
    // stack into an extra process on the UI thread. Runtime failures are
    // reported when the persistent server starts.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _availCache = new();
    private static readonly TimeSpan AvailabilityProbeTimeout = TimeSpan.FromSeconds(10);

    public static bool IsAvailable(string pythonExe, string scriptPath)
    {
        if (!File.Exists(pythonExe) || !File.Exists(scriptPath)) return false;

        var key = pythonExe + "|" + scriptPath;
        if (_availCache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = "-W ignore -c \"import importlib.util,sys; sys.exit(0 if all(importlib.util.find_spec(m) for m in ('chatterbox','torch')) else 1)\"",
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

    public Task WarmAsync(CancellationToken ct = default) => WarmCoreAsync(ct).WaitAsync(ct);

    private async Task WarmCoreAsync(CancellationToken ct)
    {
        var model = CloneModel == "turbo" ? "turbo" : "original";
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(ResponseTimeoutSeconds));
            await EnsureReadyAsync(model, startup.Token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureProcessAsync(string model, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_proc != null && !_proc.HasExited && _loadedModel == model) return;
        if (_proc is not null)
        {
            if (!_proc.HasExited) { _proc.Kill(entireProcessTree: true); await _proc.WaitForExitAsync(ct).ConfigureAwait(false); }
            _proc.Dispose(); _proc = null;
        }
        _loadedModel = model;
        _ready = false;

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
        psi.Environment["CPT_CLONE_MODEL"] = model;
        _proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start chatterbox subprocess.");
        // Drain stderr (mostly noise: HuggingFace download bars, transformers warnings)
        // but echo each line to progress so a hang has a visible cause.
        var process = _proc;
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
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
                    _ready = true;
                    CptLog.Write($"[tts] clone model ready: {_loadedModel}");
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

    private async Task EnsureReadyAsync(string model, CancellationToken ct)
    {
        try { await EnsureProcessAsync(model, ct).ConfigureAwait(false); }
        catch
        {
            _ready = false;
            try { if (_proc is not null && !_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _proc?.Dispose();
            _proc = null;
            throw;
        }
    }

    public async IAsyncEnumerable<byte[]> SynthesizeStreamAsync(
        string text, string voiceRef, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // The caller can stop immediately, while the owned exchange drains its
        // response before releasing the pipe to the next request.
        var job = SynthesizeAsync(text, voiceRef, ct);
        _ = job.ContinueWith(t => CptLog.Write("[tts] " + t.Exception!.GetBaseException().Message),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        yield return await job.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<byte[]> SynthesizeAsync(string text, string voiceRef, CancellationToken ct)
    {
        var model = CloneModel == "turbo" ? "turbo" : "original";
        var expressiveness = Math.Clamp(Expressiveness, 0, 1);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var outPath = Path.Combine(Path.GetTempPath(), $"cpt_clone_{Guid.NewGuid():N}.wav");
        try
        {
            ct.ThrowIfCancellationRequested();
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(ResponseTimeoutSeconds));
            await EnsureReadyAsync(model, startup.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var cmd = JsonSerializer.Serialize(new { op = "synth", text, @ref = voiceRef, @out = outPath, exaggeration = expressiveness });
            await _proc!.StandardInput.WriteLineAsync(cmd).ConfigureAwait(false);
            await _proc.StandardInput.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            var sampleRate = await ReadSynthAnswerAsync(outPath).ConfigureAwait(false);
            if (sampleRate > 0) _sampleRate = sampleRate;
            using var reader = new WaveFileReader(outPath);
            using var pcm = new MemoryStream();
            reader.CopyTo(pcm);
            var audio = pcm.ToArray();
            CptLog.Write($"[tts] clone generated {audio.Length} PCM bytes, {reader.TotalTime.TotalSeconds:F2}s, level={PlaybackLevelTap.Rms(audio, 0, audio.Length):F4}, reference={Path.GetFileName(voiceRef)}");
            return audio;
        }
        finally
        {
            try { File.Delete(outPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _gate.Release();
        }
    }

    /// <summary>
    /// The answer to THIS synthesis, skipping everything else on the pipe.
    ///
    /// The server writes one JSON line per EVENT, not one per request: model
    /// load status, and occasionally a line that is not JSON at all. Reading
    /// exactly one line and demanding "ok" therefore took a status line as an
    /// answer -- "'l' is an invalid start of a value" -- and from that moment
    /// every reply read the previous reply's answer and went looking for a file
    /// that had not been written yet: "could not find file cpt_clone_....wav",
    /// over and over, with no voice for the rest of the session.
    ///
    /// The server echoes the output path it was given, so this request's answer
    /// is identifiable. Anything else is logged and skipped -- which also
    /// clears the answer to a request whose caller has gone, so one cancelled
    /// line cannot silence everything after it.
    /// </summary>
    private async Task<int> ReadSynthAnswerAsync(string outPath)
    {
        var deadline = DateTime.UtcNow.AddSeconds(ResponseTimeoutSeconds);

        while (true)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;

            var read = _proc!.StandardOutput.ReadLineAsync();
            if (await Task.WhenAny(read, Task.Delay(remaining)).ConfigureAwait(false) != (Task)read) break;

            var line = await read.ConfigureAwait(false);
            if (line is null) throw new InvalidOperationException("Chatterbox subprocess closed unexpectedly.");
            if (line.Length == 0) continue;

            JsonElement answer;
            try { answer = JsonDocument.Parse(line).RootElement; }
            catch (JsonException) { Progress?.Report(line); continue; }

            if (!answer.TryGetProperty("ok", out var ok))
            {
                // A status line from the model loading. Not an answer.
                Progress?.Report(line);
                continue;
            }

            // An answer to somebody else's request: whoever asked for it is no
            // longer waiting. Drop its file and keep reading.
            if (answer.TryGetProperty("out", out var wrote)
                && !string.Equals(wrote.GetString(), outPath, StringComparison.OrdinalIgnoreCase))
            {
                CptLog.Write("[tts] skipped a stale clone answer for " + Path.GetFileName(wrote.GetString() ?? ""));
                try { File.Delete(wrote.GetString()!); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                continue;
            }

            if (!ok.GetBoolean())
            {
                var message = answer.TryGetProperty("error", out var err) ? err.GetString() : null;
                throw new InvalidOperationException(message ?? "synth failed");
            }

            return answer.TryGetProperty("sr", out var sr) ? sr.GetInt32() : 0;
        }

        CptLog.Write($"[tts] the voice clone did not answer in {ResponseTimeoutSeconds}s; restarting it");
        try { if (_proc is not null && !_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        _proc = null;                                   // EnsureProcessAsync starts a new one
        throw new InvalidOperationException("The voice clone stopped responding and will be restarted.");
    }

    /// <summary>
    /// Long enough for a slow synthesis on a cold CPU, short enough that a
    /// wedged server is noticed within one reply.
    /// </summary>
    private const int ResponseTimeoutSeconds = 120;

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
        finally { _proc?.Dispose(); }
    }
}
