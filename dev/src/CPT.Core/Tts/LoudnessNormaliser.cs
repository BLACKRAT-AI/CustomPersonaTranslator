using System;

namespace CPT.Core.Tts;

/// <summary>
/// Brings every voice to the same loudness before it is played.
///
/// Engines disagree wildly about level. Measured through this app's own
/// pipeline, a Piper preset peaks at 0.857 and a Chatterbox clone of the same
/// length at 0.051 — about 24 dB quieter, which is the difference between a
/// reply you can hear across a room and one you cannot hear at all. Asking the
/// user to ride a volume slider between personas is asking them to do the
/// machine's job.
///
/// So the gain is chosen from the audio itself: measure what is arriving, work
/// out how far it is from where it should be, and move there smoothly. Smoothly
/// matters — a gain that jumps per block pumps audibly on speech, which sounds
/// worse than the quiet it fixed.
/// </summary>
public sealed class LoudnessNormaliser
{
    /// <summary>Where a reply should peak, in dBFS. Just under full scale, so nothing clips.</summary>
    private const double TargetPeakDb = -3.0;

    /// <summary>The most it will ever lift a signal. Beyond this it is amplifying a room, not a voice.</summary>
    private const double MaxBoostDb = 28.0;

    /// <summary>And the most it will ever pull one down.</summary>
    private const double MaxCutDb = -12.0;

    /// <summary>
    /// Below this a block is silence between words. Measuring it would drive the
    /// gain up and then pump when the voice returned.
    /// </summary>
    private const double SilencePeak = 0.005;

    /// <summary>How quickly the gain settles, as a fraction per block of audio.</summary>
    private const double Ease = 0.25;

    private double _gain = 1;
    private bool _measured;

    /// <summary>The gain currently applied, in dB. Reported so it can be logged and tested.</summary>
    public double GainDb => 20 * Math.Log10(Math.Max(0.0001, _gain));

    /// <summary>Forgets the level, for the start of a new reply.</summary>
    public void Reset()
    {
        _gain = 1;
        _measured = false;
    }

    /// <summary>
    /// Scales one block of 16-bit PCM toward the target loudness, in place.
    /// </summary>
    public void Apply(byte[] pcm, int offset, int count)
    {
        var peak = Peak(pcm, offset, count);

        if (peak > SilencePeak)
        {
            var wanted = Math.Clamp(
                Math.Pow(10, TargetPeakDb / 20) / peak,
                Math.Pow(10, MaxCutDb / 20),
                Math.Pow(10, MaxBoostDb / 20));

            // The first block of a reply jumps straight to where it belongs;
            // after that the gain eases, so a loud syllable does not audibly
            // duck the rest of the sentence.
            _gain = _measured ? _gain + (wanted - _gain) * Ease : wanted;
            _measured = true;
        }

        if (Math.Abs(_gain - 1) < 0.01) return;

        for (var i = offset; i + 1 < offset + count; i += 2)
        {
            var sample = (short)(pcm[i] | (pcm[i + 1] << 8));
            var scaled = (short)Math.Clamp(sample * _gain, short.MinValue, short.MaxValue);
            pcm[i] = (byte)(scaled & 0xFF);
            pcm[i + 1] = (byte)((scaled >> 8) & 0xFF);
        }
    }

    /// <summary>Loudest sample in a block, 0 to 1.</summary>
    internal static double Peak(byte[] pcm, int offset, int count)
    {
        var peak = 0;
        for (var i = offset; i + 1 < offset + count; i += 2)
        {
            var sample = Math.Abs((short)(pcm[i] | (pcm[i + 1] << 8)));
            if (sample > peak) peak = sample;
        }
        return peak / 32768.0;
    }
}
