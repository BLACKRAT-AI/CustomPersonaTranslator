using System;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace CPT.Core.Tts;

/// <summary>
/// Buffered streaming player. PCM pushed through <see cref="Write"/> plays
/// continuously, and the level of whatever is <em>currently audible</em> is
/// reported so the hologram's mouth can move with the voice.
///
/// The level deliberately comes from <see cref="PlaybackLevelTap"/> rather than
/// from the bytes as they arrive: synthesis runs seconds ahead of playback, so
/// measuring at write time gave the mouth one amplitude per sentence, long
/// before that sentence could be heard.
/// </summary>
public sealed class StreamingAudioPlayer : IDisposable
{
    /// <summary>Level updates per second. Matches a display's frame rate.</summary>
    private const int LevelHz = 60;

    private readonly WaveOutEvent _out;

    /// <summary>
    /// Output volume for every player, 0 to 1.
    ///
    /// Static because a reply is spoken through a player built for that reply:
    /// a per-instance setting would be forgotten between sentences. Full by
    /// default -- a cloned voice is often quieter than a Piper preset, and a
    /// reply nobody can hear is the same as no reply.
    /// </summary>
    public static float Volume { get; set; } = 1.0f;

    /// <summary>Applies a new volume to the player that is speaking right now.</summary>
    public void ApplyVolume()
    {
        try { _out.Volume = Math.Clamp(Volume, 0f, 1f); }
        catch (ArgumentOutOfRangeException) { }
        catch (NAudio.MmException) { }
    }

    private readonly BufferedWaveProvider _buffer;
    private readonly PlaybackLevelTap _tap;
    private readonly LoudnessNormaliser _loudness = new();
    private readonly Timer _levelTimer;
    private readonly object _gate = new();
    private bool _disposed;

    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample { get; }

    /// <summary>Loudness of the audio being heard right now, 0 to 1.</summary>
    public float CurrentLevel { get; private set; }

    /// <summary>Raised at <see cref="LevelHz"/> while audio is playing.</summary>
    public event Action<float>? LevelChanged;

    public StreamingAudioPlayer(int sampleRate, int channels, int bitsPerSample)
    {
        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;

        _buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, bitsPerSample, channels))
        {
            BufferDuration = TimeSpan.FromSeconds(20),
            DiscardOnBufferOverflow = true,
        };
        _tap = new PlaybackLevelTap(_buffer);

        _out = new WaveOutEvent { DesiredLatency = 100, Volume = Math.Clamp(Volume, 0f, 1f) };
        _out.Init(_tap);
        _out.Play();

        var period = TimeSpan.FromMilliseconds(1000.0 / LevelHz);
        _levelTimer = new Timer(_ => PublishLevel(), null, period, period);
    }

    public void Write(byte[] pcm)
    {
        if (_disposed) return;

        // Levelled before it is buffered, so every voice arrives at the same
        // loudness whatever engine produced it.
        _loudness.Apply(pcm, 0, pcm.Length);

        lock (_gate) _buffer.AddSamples(pcm, 0, pcm.Length);
    }

    /// <summary>How much the current voice is being lifted, in dB.</summary>
    public double GainDb => _loudness.GainDb;

    /// <summary>
    /// Reports the level of the audio the sound card has actually reached.
    /// </summary>
    private void PublishLevel()
    {
        if (_disposed) return;

        float level;
        try
        {
            level = _tap.LevelAt(_out.GetPosition());
        }
        catch (Exception ex) when (ex is NAudio.MmException or ObjectDisposedException)
        {
            return;   // the device went away mid-utterance
        }

        // Silence needs no event: the hologram's own release handles the tail,
        // and this runs sixty times a second for the life of the app.
        if (level <= 0.0005f && CurrentLevel <= 0.0005f) return;

        CurrentLevel = level;
        LevelChanged?.Invoke(level);
    }

    public void StopAndFlush()
    {
        lock (_gate) _buffer.ClearBuffer();
        _out.Stop();
        _tap.Reset();
        _loudness.Reset();
        _out.Play();
    }

    /// <summary>
    /// Waits until the buffered samples have actually been played.
    ///
    /// Without this the pipeline reports "done" the moment the last sentence is
    /// queued, and with a 100 ms device latency plus the tail still buffered the
    /// listener hears the final word clipped as the hologram starts folding away.
    /// </summary>
    public async Task WaitForDrainAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;

        while (!_disposed)
        {
            int buffered;
            lock (_gate) buffered = _buffer.BufferedBytes;
            if (buffered <= 0) break;

            try { await Task.Delay(60, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }

        // One more latency-sized wait so the device's own queue empties.
        try { await Task.Delay(140, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _levelTimer.Dispose();
        try { _out.Stop(); }
        catch (NAudio.MmException) { /* already gone */ }
        _out.Dispose();
    }
}
