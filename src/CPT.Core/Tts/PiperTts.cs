using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CPT.Core.Tts;

// Wraps the piper CLI. Expects `piper.exe` on PATH or via PiperPath config,
// and a voice model `.onnx` (+ `.onnx.json`) available locally.
//
// Streaming model: we feed text via stdin and read 16-bit PCM little-endian
// from stdout in chunks. AudioPlayer consumes the resulting stream.
public sealed class PiperTts : ITtsEngine
{
    private readonly string _piperPath;
    private readonly string _modelDir;

    public PiperTts(string piperPath = "piper", string modelDir = ".")
    {
        _piperPath = piperPath;
        _modelDir = modelDir;
    }

    public string Engine => "piper";

    public async IAsyncEnumerable<byte[]> SynthesizeStreamAsync(
        string text, string voiceRef, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var modelPath = Path.IsPathRooted(voiceRef)
            ? voiceRef
            : Path.Combine(_modelDir, voiceRef.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ? voiceRef : voiceRef + ".onnx");

        var psi = new ProcessStartInfo
        {
            FileName = _piperPath,
            Arguments = $"--model \"{modelPath}\" --output_raw",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start piper");
        try
        {
            await proc.StandardInput.WriteAsync(text.AsMemory(), ct);
            proc.StandardInput.Close();

            var buf = new byte[4096];
            var stdout = proc.StandardOutput.BaseStream;
            while (true)
            {
                int n = await stdout.ReadAsync(buf.AsMemory(0, buf.Length), ct);
                if (n <= 0) yield break;
                var chunk = new byte[n];
                Buffer.BlockCopy(buf, 0, chunk, 0, n);
                yield return chunk;
            }
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
        }
    }

    public int SampleRate => 22050; // piper default for medium models
    public int Channels => 1;
    public int BitsPerSample => 16;
}
