using System;
using System.Threading;
using NAudio.Wave;

namespace CPT.Core.Tts;

// Buffered streaming player. PCM bytes pushed via Write() play continuously.
// Exposes a running RMS level for the waveform visualizer.
public sealed class StreamingAudioPlayer : IDisposable
{
    private readonly WaveOutEvent _out;
    private readonly BufferedWaveProvider _buf;
    private readonly object _lock = new();
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }

    public float CurrentLevel { get; private set; } // 0..1 RMS approx
    public event Action<float>? LevelChanged;

    public StreamingAudioPlayer(int sampleRate, int channels, int bitsPerSample)
    {
        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
        _buf = new BufferedWaveProvider(new WaveFormat(sampleRate, bitsPerSample, channels))
        {
            BufferDuration = TimeSpan.FromSeconds(20),
            DiscardOnBufferOverflow = true,
        };
        _out = new WaveOutEvent { DesiredLatency = 100 };
        _out.Init(_buf);
        _out.Play();
    }

    public void Write(byte[] pcm)
    {
        if (_disposed) return;
        lock (_lock) _buf.AddSamples(pcm, 0, pcm.Length);
        var level = ComputeRms16Bit(pcm);
        CurrentLevel = level;
        LevelChanged?.Invoke(level);
    }

    public void StopAndFlush()
    {
        lock (_lock) _buf.ClearBuffer();
        _out.Stop();
        _out.Play();
    }

    // Block until the buffered samples have actually been played. Without
    // this, the pipeline's OnDone fires the instant the last sentence is
    // queued — but with a 100 ms WaveOut latency + tail pad still in the
    // buffer, the listener heard the final word get clipped because the
    // hologram's "sending" animation started before audio finished.
    public async System.Threading.Tasks.Task WaitForDrainAsync(System.Threading.CancellationToken ct = default)
    {
        if (_disposed) return;
        // Drain check: BufferedWaveProvider exposes BufferedBytes.
        while (!_disposed)
        {
            int buffered;
            lock (_lock) buffered = _buf.BufferedBytes;
            if (buffered <= 0) break;
            try { await System.Threading.Tasks.Task.Delay(60, ct); }
            catch { break; }
        }
        // One more latency-sized wait so the WaveOut's own queue empties.
        try { await System.Threading.Tasks.Task.Delay(140, ct); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _out.Stop(); } catch { }
        _out.Dispose();
    }

    private static float ComputeRms16Bit(byte[] pcm)
    {
        if (pcm.Length < 2) return 0;
        double sumSq = 0;
        int samples = pcm.Length / 2;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            double v = s / 32768.0;
            sumSq += v * v;
        }
        return (float)Math.Min(1.0, Math.Sqrt(sumSq / samples) * 2.0);
    }
}
