using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace CPT.Core.Tts;

/// <summary>
/// Measures how loud the audio is <em>at the moment it is heard</em>.
///
/// This exists because a mouth has to move with the sound, not with the work
/// that produced it. Measuring a chunk when it is written to the buffer gives
/// one number per synthesised sentence, seconds before that sentence is
/// audible — which is no use to a lip-sync at all.
///
/// So this sits between the buffer and the sound card. Every block the device
/// actually reads is split into short frames, each frame's loudness recorded
/// against its absolute position in the output stream, and the frames are
/// handed back later keyed by how far the device has genuinely played. The
/// result is the same signal a browser's analyser node gives: the amplitude of
/// what is coming out of the speaker right now.
/// </summary>
public sealed class PlaybackLevelTap : IWaveProvider
{
    /// <summary>Frame length. Short enough to catch a syllable, long enough to be stable.</summary>
    private const double FrameSeconds = 0.020;

    private readonly IWaveProvider _source;
    private readonly Queue<Frame> _frames = new();
    private readonly object _gate = new();
    private readonly int _bytesPerFrame;

    private long _writtenBytes;
    private float _current;

    public PlaybackLevelTap(IWaveProvider source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));

        var format = source.WaveFormat;
        if (format.BitsPerSample != 16)
            throw new ArgumentException("Only 16-bit PCM is supported.", nameof(source));

        _bytesPerFrame = Math.Max(2, (int)(format.SampleRate * FrameSeconds) * format.Channels * 2);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Loudness of the audio the device has played up to <paramref name="playedBytes"/>.</summary>
    public float LevelAt(long playedBytes)
    {
        lock (_gate)
        {
            // Discard every frame the device is already past, keeping the last
            // one: that is the audio being heard right now.
            while (_frames.Count > 0 && _frames.Peek().EndByte <= playedBytes)
                _current = _frames.Dequeue().Level;

            // Nothing queued yet means the device has caught up with synthesis;
            // let the mouth fall shut rather than holding the last vowel open.
            if (_frames.Count == 0) _current *= 0.5f;

            return _current;
        }
    }

    /// <summary>Forgets the measured timeline, after the buffer is cleared.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _frames.Clear();
            _current = 0;
            _writtenBytes = 0;
        }
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);
        if (read <= 0) return read;

        lock (_gate)
        {
            for (var start = 0; start < read; start += _bytesPerFrame)
            {
                var length = Math.Min(_bytesPerFrame, read - start);
                _writtenBytes += length;
                _frames.Enqueue(new Frame(_writtenBytes, Rms(buffer, offset + start, length)));
            }

            // A few seconds of frames is ample; the device is never that far
            // behind, and an unbounded queue would grow for the whole utterance.
            while (_frames.Count > 1000) _frames.Dequeue();
        }
        return read;
    }

    /// <summary>
    /// Root-mean-square amplitude of 16-bit samples, scaled so ordinary speech
    /// lands in the upper half of the range rather than hugging zero.
    /// </summary>
    internal static float Rms(byte[] pcm, int offset, int count)
    {
        var samples = count / 2;
        if (samples <= 0) return 0;

        double sumOfSquares = 0;
        for (var i = 0; i + 1 < count; i += 2)
        {
            var sample = (short)(pcm[offset + i] | (pcm[offset + i + 1] << 8));
            var normalized = sample / 32768.0;
            sumOfSquares += normalized * normalized;
        }

        return (float)Math.Min(1.0, Math.Sqrt(sumOfSquares / samples) * 2.0);
    }

    private readonly record struct Frame(long EndByte, float Level);
}
