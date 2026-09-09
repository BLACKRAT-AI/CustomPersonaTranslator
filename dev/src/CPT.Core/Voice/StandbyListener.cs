using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using CPT.Core.Diagnostics;
using CPT.Core.Settings;
using CPT.Core.Stt;
using NAudio.Wave;

namespace CPT.Core.Voice;

/// <summary>
/// Hands-free voice control: listens continuously, wakes on a spoken phrase, and
/// hands over the dictated request when the user says the send phrase.
///
/// The design keeps the microphone cheap and the transcriber expensive. Audio is
/// segmented locally by <see cref="VoiceActivityDetector"/>, and only complete
/// utterances are transcribed -- so the machine is idle between sentences rather
/// than running speech recognition on a silent room. Transcription happens on a
/// single background worker because the engine is a subprocess that must not be
/// run concurrently with itself.
/// </summary>
public sealed class StandbyListener : IAsyncDisposable
{
    private const int FrameMilliseconds = 50;
    /// <summary>
    /// Audio kept from BEFORE speech is detected, in frames of 50 ms.
    ///
    /// A wake phrase sits at the very start of an utterance, which is the part
    /// most easily lost: the gate needs a moment to open and the detector
    /// spends its first frames learning the room. At 300 ms the recogniser was
    /// hearing "agent, why did the build fail" -- the "hey" clipped off, so no
    /// configured phrase could ever match. A second of history costs 32 KB and
    /// keeps the whole phrase.
    /// </summary>
    private const int PreRollFrames = 20;

    /// <summary>
    /// How long the user has to start speaking after being woken.
    ///
    /// Much longer than the pause that ENDS a request: waking is a moment to
    /// draw breath, and the clock is measured from the audio, so recognition has
    /// already spent a second or two of it before the wake is even known about.
    /// </summary>
    private const int StartSpeakingSeconds = 10;

    /// <summary>
    /// How much speech must pile up before guessing at the wake phrase, and how
    /// often to guess again while the person keeps talking.
    ///
    /// Waiting for the utterance to END costs the detector's 700 ms of trailing
    /// silence before recognition even starts. Since the wake phrase sits at the
    /// front, there is no reason to wait: once about two-thirds of a second of
    /// speech exists, it can already contain "hey computer".
    /// </summary>
    private const int SpeculateAfterMilliseconds = 450;
    private const int SpeculateEveryMilliseconds = 200;

    /// <summary>
    /// How much of the recent past each guess looks at, in frames of 50 ms.
    ///
    /// A TRAILING window, not the whole utterance. Two things go wrong without
    /// it, and they pull in opposite directions. Guessing at the whole utterance
    /// means the recogniser is handed a steadily longer clip for as long as
    /// somebody keeps talking -- in a room with a television, forever. Capping
    /// it by total length instead means giving up on long utterances entirely,
    /// so a wake phrase spoken over the top of the television is never looked
    /// for at all.
    ///
    /// A window does both jobs: every guess costs the same, and the agent's name
    /// is always inside it, because it was just said.
    /// </summary>
    private const int SpeculateWindowFrames = 50;

    /// <summary>
    /// How much unheard audio may pile up while asleep. Two is enough to cover
    /// a phrase that arrives while one is being screened, and short enough that
    /// waiting for the queue can never cost more than about a second.
    /// </summary>
    private const int MaxQueuedWhileSleeping = 2;

    private readonly ContinuousMicCapture _microphone = new();
    private readonly ITranscriber _transcriber;

    /// <summary>
    /// The small, fast recogniser used ONLY to notice the wake phrase early.
    /// Null when no suitable model is installed, in which case waking simply
    /// happens the slow way.
    /// </summary>
    private readonly ITranscriber? _wakeSpotter;
    private readonly StandbySettings _settings;
    private readonly StandbyStateMachine _machine;
    private readonly VoiceActivityDetector _detector;

    private readonly Queue<byte[]> _preRoll = new();
    private readonly List<byte[]> _utterance = [];
    private readonly Channel<string> _pendingTranscriptions =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _stop = new();
    private readonly object _audioGate = new();
    private Task? _worker;
    private DateTime _lastSpeechAt = DateTime.UtcNow;

    /// <summary>One diagnostic line roughly every ten seconds of 50 ms frames.</summary>
    private const int FramesPerReport = 200;

    private int _framesSinceReport;
    private float _peakSinceReport;
    private DateTime _wokeAt = DateTime.MinValue;

    /// <summary>
    /// When the request itself became known -- the moment the transcript that
    /// woke the machine was applied, not the moment the name was spotted.
    ///
    /// The two are seconds apart, and measuring the pause from the wrong one is
    /// what cut people off mid-sentence: recognition of "hey jarvis, I'm in FL
    /// Studio and" lands 3 s after that audio ended, so the request was already
    /// "silent for longer than the timeout" the instant it existed, and went to
    /// the agent 146 ms after waking -- half a sentence, twice measured.
    /// </summary>
    private DateTime _awakeSince = DateTime.MinValue;
    private Timer? _idleTimer;
    private bool _disposed;

    /// <summary>One speculative pass at a time; 1 while one is running.</summary>
    private int _speculating;

    /// <summary>How much of the current utterance is history rather than speech.</summary>
    private int _prerollFrames;

    /// <summary>Utterances waiting to be recognised.</summary>
    private int _queued;

    /// <summary>1 while an utterance is actually being recognised.</summary>
    private int _recognising;
    private DateTime _lastSpeculationAt = DateTime.MinValue;

    /// <summary>
    /// Set once the wake has been announced, so the early guess and the
    /// authoritative transcript that follows it do not chime twice.
    /// </summary>
    private bool _wakeAnnounced;



    /// <summary>
    /// Raised when a wake phrase is heard, with the id of the agent whose
    /// phrase it was (null for the general one).
    ///
    /// The id matters at THIS moment, not when a request finally arrives.
    /// Saying "hey jarvis" is how a person calls Jarvis up; it used to change
    /// nothing until a request was completed, so calling an agent by name and
    /// then pausing summoned nobody at all.
    /// </summary>
    public event Action<string?>? Woke;

    /// <summary>Raised as the request grows, with everything captured so far.</summary>
    public event Action<string>? Captured;

    /// <summary>Raised with the finished request, ready to send to the agent.</summary>
    /// <summary>The dictated request, and the id of the agent addressed (null for the general phrase).</summary>
    public event Action<string, string?>? RequestReady;

    /// <summary>Raised when a request is abandoned, by phrase or by timeout.</summary>
    public event Action? Cancelled;

    /// <summary>
    /// Raised when the user names the agent and tells it to stop -- while a turn
    /// is running, or while its answer is being spoken.
    /// </summary>
    public event Action? StopRequested;

    /// <summary>Raised for every audio frame, for a level meter. 0 to 1.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>Raised when something went wrong that the user should see.</summary>
    public event Action<string>? Failed;

    public StandbyListener(
        ITranscriber transcriber, StandbySettings settings, ITranscriber? wakeSpotter = null)
    {
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _wakeSpotter = wakeSpotter;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _machine = new StandbyStateMachine(settings);
        _detector = new VoiceActivityDetector(settings.SilenceThreshold, FrameMilliseconds);

        _microphone.FrameCaptured += OnFrame;
        _microphone.CaptureFailed += message => Failed?.Invoke(message);
    }

    /// <summary>True while the microphone is open.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Whether the listener is waiting for its wake phrase or dictating.</summary>
    public StandbyState State => _machine.State;

    /// <summary>
    /// Phrases that also wake it, beyond the one in settings: each agent's own,
    /// with its id. Saying an agent's name is how a person addresses it.
    /// </summary>
    public IReadOnlyList<(string Phrase, string Owner)> ExtraWakePhrases
    {
        get => _machine.ExtraWakePhrases;
        set => _machine.ExtraWakePhrases = value;
    }

    /// <summary>Starts listening. Does nothing when already running.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return;

        _machine.Reset();
        _detector.Reset();
        _wakeAnnounced = false;
        _lastSpeechAt = DateTime.UtcNow;

        _worker ??= Task.Run(() => ProcessTranscriptionsAsync(_stop.Token));
        // Four times a second: the pause that ends a request is measured in
        // seconds, so checking once a second added most of another one to it.
        _idleTimer ??= new Timer(
            _ => CheckIdle(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

        ChooseMicrophone();

        _microphone.Start();
        IsRunning = _microphone.IsCapturing;
        // Every phrase it will actually answer to, not just the general one. A
        // listener that had never been told the agents' names logged a
        // confident "listening for hey agent" and ignored "Hey computer".
        if (IsRunning)
        {
            var known = _settings.WakePhrases
                .Concat(_machine.ExtraWakePhrases.Select(p => p.Phrase))
                .Select(p => "\"" + p + "\"");
            CptLog.Write("[standby] listening for: " + string.Join(", ", known));
        }
    }


    /// <summary>
    /// Pushes one frame of audio through exactly the path the microphone uses:
    /// the same gate, the same segmentation, the same temporary WAV and the same
    /// recognition.
    ///
    /// It exists because "standby does not work" was diagnosed twice from the
    /// parts and fixed twice without the whole ever being run. The microphone
    /// itself is the only thing this cannot prove, and it is the one part that
    /// can be measured directly.
    /// </summary>
    internal void InjectFrameForTest(byte[] pcm, float level) => OnFrame(new AudioFrame(pcm, level));

    /// <summary>Starts everything except the microphone, for an injected run.</summary>
    internal void StartWithoutMicrophoneForTest()
    {
        _machine.Reset();
        _detector.Reset();
        _wakeAnnounced = false;
        _lastSpeechAt = DateTime.UtcNow;
        _worker ??= Task.Run(() => ProcessTranscriptionsAsync(_stop.Token));

        // The idle timer is what SENDS a finished request; without it a test
        // would show a wake that never becomes anything.
        _idleTimer ??= new Timer(
            _ => CheckIdle(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
        IsRunning = true;
    }

    /// <summary>Stops listening and forgets any part-dictated request.</summary>
    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;

        _microphone.Stop();
        _machine.Reset();
        _detector.Reset();
        _wakeAnnounced = false;

        lock (_audioGate)
        {
            _preRoll.Clear();
            _utterance.Clear();
        }
        CptLog.Write("[standby] stopped");
    }

    /// <summary>
    /// Opens the microphone that is actually hearing something.
    ///
    /// "Device -1 means the system default" was never true: NAudio has no
    /// concept of a default, so -1 became device 0 -- the first one Windows
    /// happens to list. On this machine that is a webcam microphone, and when
    /// it went quiet standby sat there reporting "peak 0" every ten seconds
    /// while somebody talked to it. AudioInputs.Liveliest has existed the whole
    /// time to answer exactly this question; nothing ever called it.
    ///
    /// A device the user picked by hand is honoured as chosen, even if it is
    /// silent: overriding a deliberate choice would be worse than obeying it.
    /// </summary>
    private void ChooseMicrophone()
    {
        _silentReports = 0;
        _silenceReported = false;

        _chosenByHand = ContinuousMicCapture.DeviceIndex != AudioInputs.SystemDefault;
        if (_chosenByHand)
        {
            CptLog.Write("[standby] microphone: " + DeviceName(ContinuousMicCapture.DeviceIndex) + " (chosen)");
            return;
        }

        var liveliest = AudioInputs.Liveliest();
        if (liveliest != AudioInputs.SystemDefault) ContinuousMicCapture.DeviceIndex = liveliest;

        CptLog.Write("[standby] microphone: " + DeviceName(ContinuousMicCapture.DeviceIndex)
            + (liveliest == AudioInputs.SystemDefault ? " (nothing was audible on any device)" : " (liveliest)"));
    }

    private static string DeviceName(int index)
    {
        foreach (var device in AudioInputs.All())
            if (device.Index == Math.Max(0, index)) return device.Index + ": " + device.Name;

        return "device " + index;
    }

    /// <summary>Ten-second windows with nothing in them at all.</summary>
    private int _silentReports;
    private bool _chosenByHand;
    private bool _silenceReported;

    /// <summary>
    /// Says out loud, once, that the microphone is delivering nothing.
    ///
    /// Standby that hears silence looks exactly like standby that is working:
    /// the indicator is lit, the log says "listening", and the user talks to a
    /// machine that is not listening to them. Half a minute of digital silence
    /// is not a quiet room, it is a dead device.
    /// </summary>
    private void WatchForADeadMicrophone(float peak)
    {
        if (_silenceReported) return;

        _silentReports = peak < 0.0002f ? _silentReports + 1 : 0;
        if (_silentReports < 3) return;

        var was = DeviceName(ContinuousMicCapture.DeviceIndex);
        CptLog.Write("[standby] " + was + " has delivered silence for 30 s");

        // Try the others before complaining. The device is picked from a
        // quarter-second sample of a quiet room, so the loudest one at that
        // instant can easily be a webcam hearing the fan rather than the
        // headset the user actually speaks into -- and the first thing they
        // say is the thing that proves it, by not being heard.
        if (_chosenByHand)
        {
            _silenceReported = true;
            Failed?.Invoke("The microphone (" + was + ") is not picking anything up. "
                + "Pick another one in Settings.");
            return;
        }

        var better = AudioInputs.Liveliest(TimeSpan.FromMilliseconds(400));
        if (better != AudioInputs.SystemDefault && better != Math.Max(0, ContinuousMicCapture.DeviceIndex))
        {
            CptLog.Write("[standby] switching to " + DeviceName(better));
            ContinuousMicCapture.DeviceIndex = better;
            _silentReports = 0;
            _microphone.Stop();
            _microphone.Start();
            return;
        }

        _silenceReported = true;
        Failed?.Invoke("The microphone (" + was + ") is not picking anything up. "
            + "Pick another one in Settings.");
    }

    // --- audio path -------------------------------------------------------

    private void OnFrame(AudioFrame frame)
    {
        LevelChanged?.Invoke(frame.Level);
        ReportListening(frame.Level);

        var activity = _detector.Process(frame.Level);
        string? completedUtterance = null;
        string? speculateOn = null;
        var purgeBacklog = false;

        lock (_audioGate)
        {
            switch (activity)
            {
                case VoiceActivity.Silence:
                    // Keep a short rolling history so the first syllable of the
                    // wake phrase is not lost to the detector's start delay.
                    _preRoll.Enqueue(frame.Pcm);
                    while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
                    break;

                case VoiceActivity.Speech:
                    if (_utterance.Count == 0)
                    {
                        _prerollFrames = _preRoll.Count;
                        _utterance.AddRange(_preRoll);
                        _preRoll.Clear();

                        // Somebody has started talking. If that turns out to be
                        // the agent's name, the recogniser should be free to
                        // hear it -- not still working through whatever the
                        // television said ten seconds ago. Clearing now rather
                        // than when this utterance ENDS is the difference,
                        // because by then the queue is already being worked.
                        purgeBacklog = _machine.State == StandbyState.Sleeping;
                    }
                    _utterance.Add(frame.Pcm);
                    _lastSpeechAt = DateTime.UtcNow;
                    speculateOn = SnapshotForSpeculation();
                    break;

                case VoiceActivity.UtteranceEnded:
                    _utterance.Add(frame.Pcm);
                    completedUtterance = WriteUtteranceToTemporaryFile();
                    _utterance.Clear();
                    _lastSpeechAt = DateTime.UtcNow;
                    break;
            }
        }

        if (purgeBacklog) DropBacklog(0);
        if (completedUtterance is not null) Enqueue(completedUtterance);

        if (speculateOn is not null) _ = AnswerToNameAsync(speculateOn);
    }

    /// <summary>
    /// A copy of the utterance so far, when it is worth guessing at. Null the
    /// rest of the time. Caller holds the audio lock.
    ///
    /// Guessing only matters while asleep: once awake, the words are the request
    /// and the accurate model is the one that should hear them.
    /// </summary>
    private string? SnapshotForSpeculation()
    {
        if (_wakeSpotter is null) return null;
        if (_machine.State != StandbyState.Sleeping) return null;
        if (Volatile.Read(ref _speculating) != 0) return null;

        // Speech, not buffer. The utterance opens with a second of pre-roll
        // history, and counting that as speech meant the very first frame
        // already looked like a full phrase: the first guess fired immediately,
        // on a second of silence, and took the slot with it.
        var spoken = (_utterance.Count - _prerollFrames) * FrameMilliseconds;
        if (spoken < SpeculateAfterMilliseconds) return null;

        var now = DateTime.UtcNow;
        if ((now - _lastSpeculationAt).TotalMilliseconds < SpeculateEveryMilliseconds) return null;

        _lastSpeculationAt = now;
        return WriteToTemporaryFile(TrailingFrames(SpeculateWindowFrames));
    }

    /// <summary>
    /// Answers to the agent's name while the sentence is still being spoken.
    ///
    /// This ONLY chimes and lights the indicator. It deliberately changes no
    /// state and keeps no text, because half a sentence is not a safe thing to
    /// decide from: an earlier version consumed the partial transcript, and
    /// "hey computer, why did the build fail" woke on the first 600 ms and then
    /// replayed the whole utterance as the request, wake phrase included.
    ///
    /// So the fast model gets the one job it cannot get wrong in a costly way --
    /// saying "I heard you" -- and the utterance is still decided properly once
    /// it has actually finished.
    /// </summary>
    private async Task AnswerToNameAsync(string wavPath)
    {
        if (Interlocked.CompareExchange(ref _speculating, 1, 0) != 0)
        {
            TryDelete(wavPath);
            return;
        }

        try
        {
            var started = DateTime.UtcNow;
            var transcript = await _wakeSpotter!
                .TranscribeAsync(wavPath, _stop.Token).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(transcript)) return;
            if (_machine.State != StandbyState.Sleeping) return;
            if (_machine.MatchWake(transcript) is not { } heard) return;

            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            CptLog.Write($"[standby] name heard in {elapsed:0} ms on: {transcript}");
            AnnounceWake(heard.Owner);
        }
        catch (OperationCanceledException)
        {
            // Standby stopped mid-guess.
        }
        catch (Exception ex)
        {
            CptLog.Write("[standby] fast pass failed: " + ex.Message);
        }
        finally
        {
            TryDelete(wavPath);
            Volatile.Write(ref _speculating, 0);
        }
    }

    /// <summary>
    /// Queues an utterance, dropping the oldest if a backlog is building.
    ///
    /// Recognition is serial, so a queue is a delay: whatever is waiting has to
    /// finish before the phrase that was actually meant for the agent is even
    /// looked at. In a room with a television that queue never empties, and the
    /// wake phrase inherited every second of it.
    ///
    /// Old audio is the right thing to throw away. While asleep the only
    /// question is whether the agent was just addressed, and the answer to that
    /// is in the NEWEST audio; a discarded utterance from four seconds ago was
    /// somebody else talking. While awake nothing is dropped, because then every
    /// utterance is part of what the user is dictating.
    /// </summary>
    private void Enqueue(string wavPath)
    {
        // Depth is counted here rather than asked of the channel: an unbounded
        // channel does not report its own Count.
        if (_machine.State == StandbyState.Sleeping) DropBacklog(MaxQueuedWhileSleeping);

        if (_pendingTranscriptions.Writer.TryWrite(wavPath)) Interlocked.Increment(ref _queued);
    }

    /// <summary>
    /// Throws away queued audio until at most <paramref name="keep"/> remains.
    /// Depth is counted here because an unbounded channel does not report it.
    /// </summary>
    private void DropBacklog(int keep)
    {
        while (Volatile.Read(ref _queued) > keep
               && _pendingTranscriptions.Reader.TryRead(out var stale))
        {
            Interlocked.Decrement(ref _queued);
            CptLog.Write("[standby] dropped backlogged audio to keep the wake phrase prompt");
            TryDelete(stale);
        }
    }

    /// <summary>The last few frames of the utterance. Caller holds the audio lock.</summary>
    private List<byte[]> TrailingFrames(int count) =>
        _utterance.Count <= count
            ? _utterance
            : _utterance.GetRange(_utterance.Count - count, count);

    /// <summary>Writes the buffered utterance to a temp WAV. Caller holds the audio lock.</summary>
    private string? WriteUtteranceToTemporaryFile() => WriteToTemporaryFile(_utterance);

    /// <summary>Writes frames to a temp WAV. Caller holds the audio lock.</summary>
    private static string? WriteToTemporaryFile(List<byte[]> frames)
    {
        if (frames.Count == 0) return null;

        var path = Path.Combine(Path.GetTempPath(), $"cpt_standby_{Guid.NewGuid():N}.wav");
        try
        {
            using var writer = new WaveFileWriter(path, ContinuousMicCapture.Format);
            foreach (var frame in frames) writer.Write(frame, 0, frame.Length);
            return path;
        }
        catch (IOException ex)
        {
            CptLog.Write("[standby] could not buffer utterance: " + ex.Message);
            TryDelete(path);
            return null;
        }
    }

    // --- transcription path -----------------------------------------------

    private async Task ProcessTranscriptionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var wavPath in _pendingTranscriptions.Reader
                               .ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queued);

                Interlocked.Increment(ref _recognising);
                try
                {
                    // An utterance that is nothing but the agent's name needs no
                    // accurate transcription: there are no words in it to get right,
                    // and the large model would spend two and a half seconds
                    // confirming what the small one already read correctly.
                    var decided = await ScreenWhileSleepingAsync(wavPath, cancellationToken)
                        .ConfigureAwait(false);

                    if (decided != Screened.NeedsAccurateTranscription)
                    {
                        TryDelete(wavPath);
                        if (decided == Screened.NotForUs) _wakeAnnounced = false;
                        continue;
                    }

                    string transcript;
                    try
                    {
                        transcript = await _transcriber.TranscribeAsync(wavPath, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        CptLog.Write("[standby] transcription failed: " + ex.Message);
                        Failed?.Invoke("Speech recognition failed: " + ex.Message);
                        continue;
                    }
                    finally
                    {
                        TryDelete(wavPath);
                    }

                    if (!string.IsNullOrWhiteSpace(transcript)) Apply(transcript);

                    // A guess that chimed and then turned out to be somebody saying
                    // something else must not leave the indicator lit, or swallow
                    // the chime for the summons that really comes.
                    if (_machine.State == StandbyState.Sleeping) _wakeAnnounced = false;
                }
                finally
                {
                    Interlocked.Decrement(ref _recognising);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }


    /// <summary>
    /// Says what standby is hearing, once every few seconds, while nothing has
    /// triggered.
    ///
    /// "Standby does not work" is otherwise unanswerable from a log: it can mean
    /// no audio, audio below the gate, or recognition returning nothing. One
    /// line with the level and the gate in it distinguishes all three.
    /// </summary>
    private void ReportListening(float level)
    {
        _framesSinceReport++;
        if (level > _peakSinceReport) _peakSinceReport = level;
        if (_framesSinceReport < FramesPerReport) return;

        CptLog.Write($"[standby] listening — peak {_peakSinceReport:0.####}, "
            + $"gate {_detector.Gate:0.####}, room {_detector.NoiseFloor:0.####}");

        WatchForADeadMicrophone(_peakSinceReport);

        _framesSinceReport = 0;
        _peakSinceReport = 0;
    }

    private void Apply(string transcript)
    {
        // Note what is NOT here: the silence clock is not restarted.
        //
        // It used to be, because recognition lags by seconds and a wake phrase
        // spoken alone was already "silent for 2.4s" the instant it woke, and
        // gave up immediately. But paying that lag twice -- once waiting for the
        // transcript, then the full pause again on top -- is what made finishing
        // a request take five seconds after the user had stopped talking.
        //
        // The clock is audio-driven instead, and the two timeouts differ enough
        // to absorb the lag: a pause that ENDS a request is measured from the
        // end of the audio, where the pause really happened, and the window to
        // START speaking is long enough that recognition can eat some of it.
        CptLog.Write($"[standby] heard ({_machine.State}): {transcript}");
        Publish(_machine.Consume(transcript));
    }

    /// <summary>
    /// Handles a finished utterance that contains the wake phrase and nothing
    /// else, using only the fast model. True when it did.
    ///
    /// This is the common case by far -- someone says the agent's name and waits
    /// -- and it is the case where the accurate model has nothing to offer,
    /// because there are no request words in the audio at all. Anything with
    /// words after the name is left alone and heard properly.
    /// </summary>
    private async Task<Screened> ScreenWhileSleepingAsync(
        string wavPath, CancellationToken cancellationToken)
    {
        if (_wakeSpotter is null) return Screened.NeedsAccurateTranscription;
        if (_machine.State != StandbyState.Sleeping) return Screened.NeedsAccurateTranscription;

        string transcript;
        try
        {
            transcript = await _wakeSpotter.TranscribeAsync(wavPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            CptLog.Write("[standby] fast pass failed: " + ex.Message);
            return Screened.NeedsAccurateTranscription;
        }

        // Not addressed to us. This is the overwhelming majority of what an
        // always-on microphone hears -- a television, a conversation across the
        // room -- and it used to be handed to the large model anyway. Measured
        // in one three-minute stretch: twenty background utterances, each
        // costing 2.6 s of recognition, on a machine that was at that moment
        // also running the agent's turn and warming a voice clone. Nothing was
        // gained by any of it; while asleep the only question is whether the
        // agent's name was said, and the small model has already answered it.
        if (_machine.MatchWake(transcript) is not { } match)
        {
            CptLog.Write("[standby] not for us: " + transcript.Trim());
            return Screened.NotForUs;
        }

        // Named, with nothing after it: the small model heard everything there
        // was to hear, so the large one has nothing to add.
        if (match.Remainder.Length == 0)
        {
            CptLog.Write("[standby] name only, heard fast: " + transcript);
            Apply(transcript);
            return Screened.Handled;
        }

        // Named, and told to stop. Handled here rather than sent to the large
        // model because the point of saying it is that something should stop
        // NOW, and the accurate pass costs about 2.6 s -- during which the
        // agent keeps talking over the person telling it to be quiet.
        if (_machine.IsStopRequest(match.Remainder))
        {
            CptLog.Write("[standby] stop heard fast: " + transcript.Trim());
            Apply(transcript);
            return Screened.Handled;
        }

        // Named, and then asked something. Those words are the request and
        // deserve the better model.
        return Screened.NeedsAccurateTranscription;
    }

    /// <summary>What the fast screen decided about one utterance.</summary>
    private enum Screened
    {
        /// <summary>Nobody addressed the agent; drop it.</summary>
        NotForUs,

        /// <summary>The wake phrase, and only that. Already applied.</summary>
        Handled,

        /// <summary>Worth the large model: a request, or no screen available.</summary>
        NeedsAccurateTranscription,
    }

    /// <summary>
    /// Says "I heard my name", once per waking.
    ///
    /// Both the fast guess and the accurate transcript can arrive at the same
    /// wake, and the user should hear one chime for one summons.
    /// </summary>
    private void AnnounceWake(string? owner)
    {
        if (_wakeAnnounced) return;

        _wakeAnnounced = true;
        _wokeAt = DateTime.UtcNow;
        Woke?.Invoke(owner);
    }

    private void Publish(StandbyStep step)
    {
        switch (step.Outcome)
        {
            case StandbyOutcome.Woke:
                AnnounceWake(step.WokeBy);
                _awakeSince = DateTime.UtcNow;
                if (step.Captured.Length > 0) Captured?.Invoke(step.Captured);
                break;

            case StandbyOutcome.Captured:
                Captured?.Invoke(step.Captured);
                break;

            case StandbyOutcome.Send:
                _wokeAt = DateTime.MinValue;
                _awakeSince = DateTime.MinValue;
                _wakeAnnounced = false;
                if (step.Request is { Length: > 0 } request) RequestReady?.Invoke(request, step.WokeBy);
                break;

            case StandbyOutcome.Cancelled:
                _wokeAt = DateTime.MinValue;
                _awakeSince = DateTime.MinValue;
                _wakeAnnounced = false;
                Cancelled?.Invoke();
                break;

            case StandbyOutcome.Stop:
                _wokeAt = DateTime.MinValue;
                _awakeSince = DateTime.MinValue;
                _wakeAnnounced = false;
                StopRequested?.Invoke();
                break;

            case StandbyOutcome.Ignored:
                break;
        }
    }

    // --- timeouts ---------------------------------------------------------

    private void CheckIdle()
    {
        if (!IsRunning) return;
        if (_machine.State != StandbyState.Listening) return;

        var now = DateTime.UtcNow;

        if (_settings.MaxRequestSeconds > 0
            && _wokeAt != DateTime.MinValue
            && (now - _wokeAt).TotalSeconds >= _settings.MaxRequestSeconds)
        {
            CptLog.Write("[standby] request hit the length cap; sending what was captured");
            Publish(_machine.OnSilenceTimeout());
            return;
        }

        // Nothing can be concluded from silence while there is still audio
        // waiting to be recognised: the user has stopped talking, but what they
        // said is not known yet. Sending here is how the last thing somebody
        // said went missing from their own request -- the pause is measured
        // from the end of the audio, and recognition takes longer than the
        // pause does. The length cap above still applies, so a recogniser that
        // wedges cannot hold a request forever.
        if (Volatile.Read(ref _queued) > 0 || Volatile.Read(ref _recognising) > 0) return;

        // Nothing dictated yet means the user has been woken and has not
        // started speaking. That deserves time to draw breath, not the pause
        // that ends a finished sentence -- with one timeout for both, waking on
        // a phrase spoken alone gave up before the question arrived.
        var allowed = _machine.Captured.Length == 0
            ? StartSpeakingSeconds
            : _settings.SilenceTimeoutSeconds;

        // A request that trails off on "and" is half a sentence. The speaker is
        // thinking, not finished, and answering it wastes a whole turn saying
        // "your message appears to have cut off" -- which is exactly what came
        // back, twice, from "hey jarvis, I'm in FL Studio and".
        if (allowed > 0 && StandbyStateMachine.EndsMidThought(_machine.Captured)) allowed *= 2;

        if (allowed <= 0) return;

        // The pause is measured from the later of the audio ending and the wake
        // being ANNOUNCED, because those can be seconds apart.
        //
        // Recognition of the waking utterance takes a couple of seconds, so when
        // somebody says "hey jarvis, how is the build" in one breath, the audio
        // has been over for longer than the whole silence timeout by the time
        // the wake is known about. Measured from the audio alone the request was
        // sent 99 ms after waking -- before the chime had finished, and long
        // before the user could add the words the recogniser had mangled. The
        // pause only means anything once the user has been told it is listening.
        //
        // And from the moment the request became KNOWN, which is later still:
        // the words that woke it were recognised seconds after they were said,
        // and a pause the user never took cannot be the reason to stop them.
        var since = _lastSpeechAt;
        if (_wokeAt > since) since = _wokeAt;
        if (_awakeSince > since) since = _awakeSince;
        if ((now - since).TotalSeconds < allowed) return;

        CptLog.Write(_machine.Captured.Length == 0
            ? "[standby] nothing was said after waking; going back to sleep"
            : "[standby] silence timeout; sending what was captured");
        Publish(_machine.OnSilenceTimeout());
    }

    private static void TryDelete(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (IOException) { /* it will be cleaned up with the temp directory */ }
        catch (UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        await _stop.CancelAsync().ConfigureAwait(false);
        _pendingTranscriptions.Writer.TryComplete();

        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        if (_idleTimer is not null) await _idleTimer.DisposeAsync().ConfigureAwait(false);
        _microphone.Dispose();
        _stop.Dispose();
    }
}
