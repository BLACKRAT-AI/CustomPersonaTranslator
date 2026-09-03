using System;

namespace CPT.Core.Voice;

/// <summary>What the detector concluded about the frame it was just given.</summary>
public enum VoiceActivity
{
    /// <summary>Still quiet.</summary>
    Silence,

    /// <summary>Speech is in progress.</summary>
    Speech,

    /// <summary>Speech has just ended: a complete utterance is ready.</summary>
    UtteranceEnded,
}

/// <summary>
/// Decides where one spoken utterance stops and the next begins.
///
/// A bare loudness threshold cuts a sentence in half at every pause for breath,
/// so this uses hysteresis in both directions: speech has to stay loud briefly
/// before it counts as started, and stay quiet noticeably longer before it counts
/// as finished. The asymmetry is deliberate -- a false start costs one wasted
/// transcription, a false end truncates what the user was saying.
/// </summary>
public sealed class VoiceActivityDetector
{
    private readonly float _threshold;
    private readonly int _framesToStart;
    private readonly int _framesToEnd;
    private readonly int _minimumSpeechFrames;

    private int _consecutiveLoud;
    private int _consecutiveQuiet;
    private int _speechFrames;

    /// <param name="threshold">RMS level above which a frame counts as loud, 0 to 1.</param>
    /// <param name="frameMilliseconds">Duration of one frame, used to convert the settings below.</param>
    /// <param name="startMilliseconds">How long it must stay loud before speech starts.</param>
    /// <param name="endMilliseconds">How long it must stay quiet before speech ends.</param>
    /// <param name="minimumSpeechMilliseconds">Utterances shorter than this are discarded as noise.</param>
    public VoiceActivityDetector(
        float threshold = 0.02f,
        int frameMilliseconds = 50,
        int startMilliseconds = 100,
        int endMilliseconds = 700,
        int minimumSpeechMilliseconds = 300)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frameMilliseconds);

        _threshold = Math.Clamp(threshold, 0.0005f, 1f);
        _framesToStart = Math.Max(1, startMilliseconds / frameMilliseconds);
        _framesToEnd = Math.Max(1, endMilliseconds / frameMilliseconds);
        _minimumSpeechFrames = Math.Max(1, minimumSpeechMilliseconds / frameMilliseconds);
    }

    /// <summary>True while an utterance is being spoken.</summary>
    public bool IsInSpeech { get; private set; }

    /// <summary>Feeds one frame's loudness in and returns what it means.</summary>
    public VoiceActivity Process(float level)
    {
        var loud = level >= _threshold;

        if (!IsInSpeech)
        {
            _consecutiveLoud = loud ? _consecutiveLoud + 1 : 0;
            if (_consecutiveLoud < _framesToStart) return VoiceActivity.Silence;

            IsInSpeech = true;
            _consecutiveQuiet = 0;
            _speechFrames = _consecutiveLoud;
            return VoiceActivity.Speech;
        }

        _speechFrames++;

        if (loud)
        {
            _consecutiveQuiet = 0;
            return VoiceActivity.Speech;
        }

        _consecutiveQuiet++;
        if (_consecutiveQuiet < _framesToEnd) return VoiceActivity.Speech;

        // The trailing silence is part of the frame count but not of the speech,
        // so measure the utterance without it before deciding it was real.
        var wasLongEnough = _speechFrames - _consecutiveQuiet >= _minimumSpeechFrames;
        Reset();
        return wasLongEnough ? VoiceActivity.UtteranceEnded : VoiceActivity.Silence;
    }

    /// <summary>Forgets the current utterance, as after a manual stop.</summary>
    public void Reset()
    {
        IsInSpeech = false;
        _consecutiveLoud = 0;
        _consecutiveQuiet = 0;
        _speechFrames = 0;
    }
}
