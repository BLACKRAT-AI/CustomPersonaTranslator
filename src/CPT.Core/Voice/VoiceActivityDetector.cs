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
///
/// The gate ADAPTS to the microphone. A fixed threshold is a guess about
/// hardware, and on this machine it was wrong by fifteen times: the default of
/// 0.02 against a microphone whose ambient level peaks at 0.0013, so standby
/// listened forever and never once heard anything. The detector now learns the
/// room's noise floor while it is quiet and triggers on speech RELATIVE to it,
/// with a small absolute floor so a silent room cannot make it hair-trigger.
/// </summary>
public sealed class VoiceActivityDetector
{
    /// <summary>How much louder than the noise floor a frame must be to count as speech.</summary>
    private const float SpeechOverNoise = 3.5f;

    /// <summary>
    /// The quietest gate ever used, whatever the room. Below this, a fan or a
    /// hard drive would start transcribing.
    /// </summary>
    private const float AbsoluteFloor = 0.0012f;

    /// <summary>How fast the noise floor follows the room, per quiet frame.</summary>
    private const float FloorRise = 0.02f;
    private const float FloorFall = 0.25f;

    /// <summary>
    /// Frames spent learning the room before the gate can fire.
    ///
    /// Without this the very first frame sets the noise floor, so a detector
    /// that opens mid-sentence would take that speech for the room and deafen
    /// itself for as long as the sentence lasted.
    /// </summary>
    private const int CalibrationFrames = 12;

    private readonly float _sensitivity;
    private readonly int _framesToStart;
    private readonly int _framesToEnd;
    private readonly int _minimumSpeechFrames;

    private float _noiseFloor = -1;
    private int _calibrating = CalibrationFrames;
    private int _consecutiveLoud;
    private int _consecutiveQuiet;
    private int _speechFrames;


    /// <param name="threshold">
    /// Sensitivity, kept for the settings slider. It scales the gate rather than
    /// setting it outright, so a stored value tuned for one microphone cannot
    /// silence another.
    /// </param>
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

        // 0.02 was the old default and means "normal": higher demands more, lower
        // demands less, and neither can push the gate somewhere unusable.
        _sensitivity = Math.Clamp(threshold / 0.02f, 0.25f, 4f);
        _framesToStart = Math.Max(1, startMilliseconds / frameMilliseconds);
        _framesToEnd = Math.Max(1, endMilliseconds / frameMilliseconds);
        _minimumSpeechFrames = Math.Max(1, minimumSpeechMilliseconds / frameMilliseconds);
    }

    /// <summary>True while an utterance is being spoken.</summary>
    public bool IsInSpeech { get; private set; }

    /// <summary>
    /// The loudest a "room" is allowed to be. Anything above this is somebody
    /// talking, not background, and letting the floor climb there would raise
    /// the gate above the very voice it is meant to hear.
    /// </summary>
    private const float MaxNoiseFloor = 0.02f;

    /// <summary>The level a frame must currently exceed to count as speech.</summary>
    public float Gate =>
        Math.Max(AbsoluteFloor, Math.Clamp(_noiseFloor, 0, MaxNoiseFloor) * SpeechOverNoise) * _sensitivity;

    /// <summary>What the detector believes the room's quiet level to be.</summary>
    public float NoiseFloor => Math.Clamp(_noiseFloor, 0, MaxNoiseFloor);

    /// <summary>Feeds one frame's loudness in and returns what it means.</summary>
    public VoiceActivity Process(float level)
    {
        if (_noiseFloor < 0) _noiseFloor = level;

        // Learn the room first, and report nothing while doing it.
        //
        // The MINIMUM, not an average: if someone is already speaking when this
        // opens, an average is dragged up by their voice and the gate ends up
        // above it -- measured at 0.52 against speech of 0.2, which is deaf.
        // The quietest moment in the window is the room whatever else is
        // happening.
        if (_calibrating > 0)
        {
            _calibrating--;
            _noiseFloor = Math.Min(_noiseFloor, level);
            return VoiceActivity.Silence;
        }

        var loud = level >= Gate;

        // The floor is learned from quiet frames only: adapting during speech
        // would let a long sentence raise the gate above its own voice.
        if (!IsInSpeech && !loud)
        {
            var rate = level > _noiseFloor ? FloorRise : FloorFall;
            _noiseFloor += (level - _noiseFloor) * rate;
        }


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
        EndUtterance();
        return wasLongEnough ? VoiceActivity.UtteranceEnded : VoiceActivity.Silence;
    }

    /// <summary>Forgets the current utterance, as after a manual stop.</summary>
    public void Reset()
    {
        EndUtterance();
        _noiseFloor = -1;                 // re-learn the room from scratch
        _calibrating = CalibrationFrames;
    }

    private void EndUtterance()
    {
        IsInSpeech = false;
        _consecutiveLoud = 0;
        _consecutiveQuiet = 0;
        _speechFrames = 0;
    }
}
