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
    /// Longer than the pause that ENDS a request: waking is a moment to draw
    /// breath, and recognition itself takes a couple of seconds, so the clock
    /// has already been running before the wake is even known about.
    /// </summary>
    private const int StartSpeakingSeconds = 8;

    private readonly ContinuousMicCapture _microphone = new();
    private readonly ITranscriber _transcriber;
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
    private Timer? _idleTimer;
    private bool _disposed;

    /// <summary>Raised when the wake phrase is heard.</summary>
    public event Action? Woke;

    /// <summary>Raised as the request grows, with everything captured so far.</summary>
    public event Action<string>? Captured;

    /// <summary>Raised with the finished request, ready to send to the agent.</summary>
    /// <summary>The dictated request, and the id of the agent addressed (null for the general phrase).</summary>
    public event Action<string, string?>? RequestReady;

    /// <summary>Raised when a request is abandoned, by phrase or by timeout.</summary>
    public event Action? Cancelled;

    /// <summary>Raised for every audio frame, for a level meter. 0 to 1.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>Raised when something went wrong that the user should see.</summary>
    public event Action<string>? Failed;

    public StandbyListener(ITranscriber transcriber, StandbySettings settings)
    {
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
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
        _lastSpeechAt = DateTime.UtcNow;

        _worker ??= Task.Run(() => ProcessTranscriptionsAsync(_stop.Token));
        // Four times a second: the pause that ends a request is measured in
        // seconds, so checking once a second added most of another one to it.
        _idleTimer ??= new Timer(
            _ => CheckIdle(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));

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

        lock (_audioGate)
        {
            _preRoll.Clear();
            _utterance.Clear();
        }
        CptLog.Write("[standby] stopped");
    }

    // --- audio path -------------------------------------------------------

    private void OnFrame(AudioFrame frame)
    {
        LevelChanged?.Invoke(frame.Level);
        ReportListening(frame.Level);

        var activity = _detector.Process(frame.Level);
        string? completedUtterance = null;

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
                    if (_utterance.Count == 0 && _preRoll.Count > 0)
                    {
                        _utterance.AddRange(_preRoll);
                        _preRoll.Clear();
                    }
                    _utterance.Add(frame.Pcm);
                    _lastSpeechAt = DateTime.UtcNow;
                    break;

                case VoiceActivity.UtteranceEnded:
                    _utterance.Add(frame.Pcm);
                    completedUtterance = WriteUtteranceToTemporaryFile();
                    _utterance.Clear();
                    _lastSpeechAt = DateTime.UtcNow;
                    break;
            }
        }

        if (completedUtterance is not null)
            _pendingTranscriptions.Writer.TryWrite(completedUtterance);
    }

    /// <summary>Writes the buffered utterance to a temp WAV. Caller holds the audio lock.</summary>
    private string? WriteUtteranceToTemporaryFile()
    {
        if (_utterance.Count == 0) return null;

        var path = Path.Combine(Path.GetTempPath(), $"cpt_standby_{Guid.NewGuid():N}.wav");
        try
        {
            using var writer = new WaveFileWriter(path, ContinuousMicCapture.Format);
            foreach (var frame in _utterance) writer.Write(frame, 0, frame.Length);
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

        _framesSinceReport = 0;
        _peakSinceReport = 0;
    }

    private void Apply(string transcript)
    {
        // The silence clock measures time since something was HEARD, not since the
        // audio arrived: recognition lags by a couple of seconds, and using the
        // audio's timestamp meant a wake phrase spoken alone was already
        // "silent for 2.4s" the instant it woke, and gave up immediately.
        _lastSpeechAt = DateTime.UtcNow;

        CptLog.Write($"[standby] heard ({_machine.State}): {transcript}");
        Publish(_machine.Consume(transcript));
    }

    private void Publish(StandbyStep step)
    {
        switch (step.Outcome)
        {
            case StandbyOutcome.Woke:
                _wokeAt = DateTime.UtcNow;
                Woke?.Invoke();
                if (step.Captured.Length > 0) Captured?.Invoke(step.Captured);
                break;

            case StandbyOutcome.Captured:
                Captured?.Invoke(step.Captured);
                break;

            case StandbyOutcome.Send:
                _wokeAt = DateTime.MinValue;
                if (step.Request is { Length: > 0 } request) RequestReady?.Invoke(request, step.WokeBy);
                break;

            case StandbyOutcome.Cancelled:
                _wokeAt = DateTime.MinValue;
                Cancelled?.Invoke();
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

        // Nothing dictated yet means the user has been woken and has not
        // started speaking. That deserves time to draw breath, not the pause
        // that ends a finished sentence -- with one timeout for both, waking on
        // a phrase spoken alone gave up before the question arrived.
        var allowed = _machine.Captured.Length == 0
            ? StartSpeakingSeconds
            : _settings.SilenceTimeoutSeconds;

        if (allowed <= 0 || (now - _lastSpeechAt).TotalSeconds < allowed) return;

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
