using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CPT.Core.Llm;

// Spawns llama-server.exe (from llama.cpp) bound to 127.0.0.1 and keeps it
// alive for the lifetime of the app. The HTTP API is OpenAI-compatible
// (/v1/chat/completions), so LlamaCppClient can stream against it the same
// way OllamaClient streamed against Ollama.
//
// Why not just call llama-cli per request? Cold load of a 2 GB model is 3-8s.
// A persistent server keeps it warm across translations.
public sealed class LlamaCppServer : IDisposable
{
    private readonly string _exe;
    private readonly string _model;
    private readonly int _port;
    private readonly int _ctxSize;
    private readonly int _ngl;
    private Process? _proc;
    private bool _disposed;
    private readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(2) };

    public string BaseUrl => $"http://127.0.0.1:{_port}";
    public bool IsRunning => _proc is not null && !_proc.HasExited;

    public LlamaCppServer(string exe, string modelPath, int port = 18080, int ctxSize = 4096, int nGpuLayers = 32)
    {
        _exe = exe; _model = modelPath; _port = port; _ctxSize = ctxSize; _ngl = nGpuLayers;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;
        if (!File.Exists(_exe))   throw new FileNotFoundException("llama-server not found", _exe);
        if (!File.Exists(_model)) throw new FileNotFoundException("LLM model not found",     _model);

        var args =
            $"--model \"{_model}\" " +
            $"--host 127.0.0.1 --port {_port} " +
            $"--ctx-size {_ctxSize} " +
            $"--n-gpu-layers {_ngl} " +    // ignored on CPU-only builds
            "--no-webui --log-disable";

        var psi = new ProcessStartInfo
        {
            FileName = _exe, Arguments = args,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_exe) ?? "",
        };
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start llama-server.");
        // Drain pipes so the process doesn't block on full buffers.
        _ = _proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        _ = _proc.StandardError.ReadToEndAsync(CancellationToken.None);

        // Poll /health until ready or 60s timeout.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (_proc.HasExited) throw new InvalidOperationException("llama-server exited during startup.");
            try
            {
                using var resp = await _probe.GetAsync($"{BaseUrl}/health", ct);
                if (resp.IsSuccessStatusCode) return;
            }
            catch { }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("llama-server did not become healthy within 60s.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_proc is not null && !_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(2000);
            }
        }
        catch { }
        finally { _proc?.Dispose(); _probe.Dispose(); }
    }
}
