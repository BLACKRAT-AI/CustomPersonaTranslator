using CPT.Core.Voice;
using Xunit;

namespace CPT.Tests;

public class VoiceActivityDetectorTests
{
    private const float Loud = 0.2f;
    private const float Quiet = 0.001f;

    /// <summary>50 ms frames: two to start, fourteen to end, six for a real utterance.</summary>
    private static VoiceActivityDetector Create() => new(
        threshold: 0.02f,
        frameMilliseconds: 50,
        startMilliseconds: 100,
        endMilliseconds: 700,
        minimumSpeechMilliseconds: 300);

    private static VoiceActivity Feed(VoiceActivityDetector detector, float level, int frames)
    {
        var last = VoiceActivity.Silence;
        for (var i = 0; i < frames; i++) last = detector.Process(level);
        return last;
    }

    [Fact]
    public void Stays_silent_in_a_quiet_room()
    {
        var detector = Create();

        Assert.Equal(VoiceActivity.Silence, Feed(detector, Quiet, 100));
        Assert.False(detector.IsInSpeech);
    }

    [Fact]
    public void One_loud_frame_is_not_enough_to_start()
    {
        var detector = Create();

        Assert.Equal(VoiceActivity.Silence, detector.Process(Loud));
        Assert.False(detector.IsInSpeech);
    }

    [Fact]
    public void Sustained_loudness_starts_speech()
    {
        var detector = Create();

        Assert.Equal(VoiceActivity.Speech, Feed(detector, Loud, 2));
        Assert.True(detector.IsInSpeech);
    }

    [Fact]
    public void A_short_pause_does_not_end_the_utterance()
    {
        var detector = Create();
        Feed(detector, Loud, 10);

        // 300 ms of silence: a breath, not the end of a sentence.
        Assert.Equal(VoiceActivity.Speech, Feed(detector, Quiet, 6));
        Assert.True(detector.IsInSpeech);
    }

    [Fact]
    public void A_long_pause_ends_the_utterance()
    {
        var detector = Create();
        Feed(detector, Loud, 10);

        Assert.Equal(VoiceActivity.UtteranceEnded, Feed(detector, Quiet, 14));
        Assert.False(detector.IsInSpeech);
    }

    [Fact]
    public void A_burst_too_short_to_be_speech_is_discarded()
    {
        var detector = Create();

        // Three loud frames is 150 ms -- under the 300 ms minimum, so this is a
        // door closing, not a word, and must not trigger a transcription.
        Feed(detector, Loud, 3);
        Assert.Equal(VoiceActivity.Silence, Feed(detector, Quiet, 14));
    }

    [Fact]
    public void Reset_clears_an_utterance_in_progress()
    {
        var detector = Create();
        Feed(detector, Loud, 10);

        detector.Reset();

        Assert.False(detector.IsInSpeech);
    }

    [Fact]
    public void A_higher_threshold_ignores_quieter_sound()
    {
        var detector = new VoiceActivityDetector(threshold: 0.5f, frameMilliseconds: 50);

        Assert.Equal(VoiceActivity.Silence, Feed(detector, 0.3f, 20));
    }
}
