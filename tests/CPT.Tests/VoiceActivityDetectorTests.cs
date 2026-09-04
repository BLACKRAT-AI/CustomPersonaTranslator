using CPT.Core.Voice;
using Xunit;

namespace CPT.Tests;

public class VoiceActivityDetectorTests
{
    private const float Loud = 0.2f;
    private const float Quiet = 0.001f;

    /// <summary>
    /// The detector learns the room before it will report anything, so every
    /// test starts the way a real session does: a moment of quiet.
    /// </summary>
    private static VoiceActivityDetector Create()
    {
        var detector = new VoiceActivityDetector(
            threshold: 0.02f,
            frameMilliseconds: 50,
            startMilliseconds: 100,
            endMilliseconds: 700,
            minimumSpeechMilliseconds: 300);

        Feed(detector, Quiet, 20);
        return detector;
    }


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

    /// <summary>
    /// The failure this adaptation exists for. A microphone whose room noise
    /// peaks at 0.0013 and whose speech reaches 0.01 is far below the old fixed
    /// gate of 0.02, so standby listened forever and never heard a word.
    /// </summary>
    [Fact]
    public void A_quiet_microphone_is_still_heard()
    {
        var detector = new VoiceActivityDetector(threshold: 0.02f, frameMilliseconds: 50);

        Feed(detector, 0.0013f, 40);                       // the room, as measured
        Assert.False(detector.IsInSpeech);

        Assert.Equal(VoiceActivity.Speech, Feed(detector, 0.01f, 4));
        Assert.True(detector.IsInSpeech);
    }

    /// <summary>
    /// And the opposite: a noisy room must not transcribe itself. The gate
    /// rises with the floor, so ambient noise never counts as speech however
    /// loud the room is.
    /// </summary>
    [Fact]
    public void A_noisy_room_does_not_trigger_on_itself()
    {
        var detector = new VoiceActivityDetector(threshold: 0.02f, frameMilliseconds: 50);

        Assert.Equal(VoiceActivity.Silence, Feed(detector, 0.03f, 200));
        Assert.False(detector.IsInSpeech);

        // Speech still has to be clearly above that room to register.
        Assert.Equal(VoiceActivity.Speech, Feed(detector, 0.3f, 4));
    }

    /// <summary>
    /// Opening the microphone mid-sentence must not take that sentence for the
    /// room, which is what learning the floor from the first frame would do.
    /// </summary>
    [Fact]
    public void Speech_during_calibration_does_not_become_the_noise_floor()
    {
        var detector = new VoiceActivityDetector(threshold: 0.02f, frameMilliseconds: 50);

        Feed(detector, 0.25f, 12);                         // calibration, all speech
        Feed(detector, 0.001f, 30);                        // the speaker stops

        Assert.Equal(VoiceActivity.Speech, Feed(detector, 0.05f, 4));
    }
}
