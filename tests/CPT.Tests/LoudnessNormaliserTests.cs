using CPT.Core.Tts;
using Xunit;

namespace CPT.Tests;

/// <summary>
/// Bringing every voice to the same loudness.
///
/// Measured through the real pipeline, a Piper preset peaks at 0.857 and a
/// Chatterbox clone at 0.051 — about 24 dB apart, which is the difference
/// between a reply you can hear and one you cannot.
/// </summary>
public class LoudnessNormaliserTests
{
    private static byte[] Tone(double amplitude, int samples = 4410)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(System.Math.Sin(i * 0.1) * amplitude * short.MaxValue);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }
        return pcm;
    }

    private static double PeakOf(byte[] pcm) => LoudnessNormaliser.Peak(pcm, 0, pcm.Length);

    [Fact]
    public void A_quiet_voice_is_lifted_to_the_target()
    {
        var normaliser = new LoudnessNormaliser();
        var quiet = Tone(0.051);                       // the measured clone level

        normaliser.Apply(quiet, 0, quiet.Length);

        Assert.InRange(PeakOf(quiet), 0.6, 0.75);      // -3 dBFS, give or take rounding
    }

    [Fact]
    public void A_loud_voice_is_left_about_where_it_is()
    {
        var normaliser = new LoudnessNormaliser();
        var loud = Tone(0.857);                        // the measured preset level

        normaliser.Apply(loud, 0, loud.Length);

        Assert.InRange(PeakOf(loud), 0.6, 0.9);
    }

    /// <summary>
    /// Both engines end up in the same place, which is the entire point.
    /// </summary>
    [Fact]
    public void Two_engines_twenty_four_decibels_apart_end_up_together()
    {
        var quiet = Tone(0.051);
        var loud = Tone(0.857);

        new LoudnessNormaliser().Apply(quiet, 0, quiet.Length);
        new LoudnessNormaliser().Apply(loud, 0, loud.Length);

        Assert.True(System.Math.Abs(PeakOf(quiet) - PeakOf(loud)) < 0.1);
    }

    /// <summary>
    /// Silence between words must not be amplified, or the gain would climb and
    /// then pump the moment the voice came back.
    /// </summary>
    [Fact]
    public void Silence_does_not_drive_the_gain_up()
    {
        var normaliser = new LoudnessNormaliser();
        var silence = new byte[8820];

        normaliser.Apply(silence, 0, silence.Length);

        Assert.Equal(0, normaliser.GainDb, 3);
        Assert.Equal(0, PeakOf(silence));
    }

    [Fact]
    public void Nothing_is_ever_lifted_beyond_the_limit()
    {
        var normaliser = new LoudnessNormaliser();
        var almostNothing = Tone(0.006);

        normaliser.Apply(almostNothing, 0, almostNothing.Length);

        Assert.True(normaliser.GainDb <= 28.01);
    }

    [Fact]
    public void A_new_reply_starts_from_no_gain()
    {
        var normaliser = new LoudnessNormaliser();
        var quiet = Tone(0.051);
        normaliser.Apply(quiet, 0, quiet.Length);

        normaliser.Reset();

        Assert.Equal(0, normaliser.GainDb, 3);
    }
}
