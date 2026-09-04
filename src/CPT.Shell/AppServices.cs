using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Cli;
using CPT.Core.Agents;
using CPT.Core.Cli.Streaming;
using CPT.Core.Diagnostics;
using CPT.Core.Hardware;
using CPT.Core.Ipc;
using CPT.Core.Llm;
using CPT.Core.Models;
using CPT.Core.Personas;
using CPT.Core.Pipeline;
using CPT.Core.Settings;
using CPT.Core.Stt;
using CPT.Core.Tts;
using CPT.Core.Voice;

namespace CPT.Shell;

/// <summary>
/// Process-wide service container, constructed once in App.OnStartup.
///
/// It owns three loops that meet at the persona:
///   * input -- push-to-talk and standby, both of which end in a request string;
///   * the agent -- one of the four coding CLIs, driven by <see cref="CliOrchestrator"/>;
///   * output -- the persona pipeline, which rewrites the reply in character and speaks it.
///
/// Host adapters (the browser and VS Code extensions, and the UI Automation
/// watcher) remain wired in as a second input: when the user is talking to an
/// agent CPT does not own, their replies still get spoken in persona.
/// </summary>
public sealed class AppServices : IDisposable
{
    private readonly SemaphoreSlim _agentTurnLock = new(1, 1);
    private CancellationTokenSource? _currentTranslation;
    private string? _lastAdapter;
    private bool _disposed;

    public AppSettings Settings { get; }
    public GpuTier Gpu { get; }

    public IpcServer Ipc { get; }
    /// <summary>
    /// The CLI that restates answers in the persona's voice.
    ///
    /// Its own orchestrator, so it can run a cheap model while the agent runs
    /// a strong one -- and so the two never fight over one set of options.
    /// </summary>
    public CliOrchestrator RewriteCli { get; }
    public ITtsEngine Tts { get; }
    public ITtsEngine? CloneTts { get; private set; }
    public TranslationPipeline Pipeline { get; private set; }
    public PersonaStore Personas { get; }
    public Persona ActivePersona { get; private set; }
    public MicCapture Mic { get; } = new();
    public WhisperCpp Stt { get; }
    public UiaHostAdapter? Uia { get; private set; }

    /// <summary>The coding CLI CPT drives.</summary>
    public CliOrchestrator Cli { get; }

    public bool CloningAvailable => CloneTts is not null;

    // --- events the UI subscribes to --------------------------------------

    public event Action<string>? OnPersonaAppear;
    public event Action<string>? OnTranscriptChunk;
    public event Action<float>? OnAudioLevel;
    public event Action? OnTranslationDone;

    /// <summary>True while a request is out with the CLI and no reply has been
    /// spoken yet. Drives the spinner, so waiting looks like waiting.</summary>
    public event Action<bool>? OnAgentBusy;

    /// <summary>Brief user-visible status, shown as a toast on the hologram.</summary>
    public event Action<string>? OnNotification;

    public event Action<Persona>? OnActivePersonaChanged;
    public event Action<CliStatus>? OnCliStatusChanged;
    public event Action<StandbyUiState>? OnStandbyStateChanged;

    public AppServices()
    {
        Settings = AppSettings.Load();
        StreamingAudioPlayer.Volume = (float)Math.Clamp(Settings.SpeakingVolume, 0, 1);
        Gpu = HardwareProbe.DetectGpu();

        Personas = new PersonaStore();
        SeedBundledPersonas();
        ActivePersona = LoadActivePersona();

        Ipc = new IpcServer(Settings.IpcPort);

        // No local model server. Rewriting is a CLI turn now: nothing resident,
        // nothing to warm up, and nothing to leak.
        RewriteCli = new CliOrchestrator(
            RewriteProviderId(), Settings.ResolveCliWorkingDirectory());
        Tts = new PiperTts(
            piperPath: Settings.PiperPath,
            modelDir: string.IsNullOrEmpty(Settings.PiperModelsDir)
                ? Path.Combine(AppContext.BaseDirectory, "models", "piper")
                : Settings.PiperModelsDir);
        Stt = new WhisperCpp(Settings.WhisperPath, NullIfEmpty(Settings.WhisperModelPath));

        CptLog.Write("=== CPT.Shell starting ===");
        CloneTts = CreateCloneEngineIfConfigured();

        Pipeline = new TranslationPipeline(new CliPersonaRewriter(RewriteCli), Tts, CloneTts);
        WirePipeline(Pipeline);

        Ipc.MessageReceived += OnAdapterMessage;
        Ipc.Start();

        Uia = new UiaHostAdapter(LoadHostProfiles(), (adapter, text) =>
        {
            _lastAdapter = adapter;
            _ = SpeakInPersonaAsync(text);
        });

        Cli = new CliOrchestrator(Settings.Cli.ProviderId, Settings.ResolveCliWorkingDirectory());
        Cli.StatusChanged += status => OnCliStatusChanged?.Invoke(status);
        // An agent, if there is one, owns all of this; these are the fallback
        // for a machine with none configured yet.
        if (Settings.Agents.Active is { } startupAgent) ApplyAgentCli(startupAgent);
        else { ApplyCliOptions(); ApplyRewriteOptions(); }

        WarmCloneIfActivePersonaNeedsIt();
    }


    /// <summary>The rewriter, for anything that needs the persona's manner.</summary>
    public IPersonaRewriter Rewriter => new CliPersonaRewriter(RewriteCli);

    /// <summary>
    /// Which CLI rewrites. Empty means "the same one the agent uses", which is
    /// the right default: it is installed and signed in by definition.
    /// </summary>
    private string RewriteProviderId() =>
        string.IsNullOrWhiteSpace(Settings.Rewrite.ProviderId)
            ? Settings.Cli.ProviderId
            : Settings.Rewrite.ProviderId;

    /// <summary>Pushes the stored rewrite options into its orchestrator.</summary>
    private void ApplyRewriteOptions()
    {
        RewriteCli.Options = Settings.Rewrite.OptionsFor(RewriteProviderId());
    }

    /// <summary>Re-reads the rewrite settings after they have been edited.</summary>
    public void ReloadRewriteCli()
    {
        RewriteCli.Select(RewriteProviderId(), Settings.ResolveCliWorkingDirectory());
        ApplyRewriteOptions();
    }

    // --- startup ----------------------------------------------------------

    /// <summary>
    /// Brings the selected CLI up to date in the background: probe, install if
    /// missing, and report what is left for the user. Everything except signing
    /// in is done without asking, which is the whole point of automatic setup.
    /// </summary>
    public async Task RunStartupSetupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = Settings.Cli.AutoSetup
                ? await Cli.EnsureInstalledAsync(
                    new Progress<string>(line => CptLog.Write("[setup] " + line)),
                    cancellationToken).ConfigureAwait(false)
                : await Cli.RefreshAsync(cancellationToken).ConfigureAwait(false);

            if (status.Readiness == CliReadiness.NeedsSignIn)
                OnNotification?.Invoke($"Sign in to {status.Provider.DisplayName} to link it.");
            else if (status.Readiness == CliReadiness.RuntimeMissing)
                OnNotification?.Invoke(status.Detail);
        }
        catch (OperationCanceledException)
        {
            // The app is shutting down.
        }

        if (Settings.Standby.Enabled) SetStandbyEnabled(true);
    }

    /// <summary>
    /// Pushes the stored per-turn choices (model, effort, permissions) for the
    /// selected provider into the orchestrator. Call after changing either.
    /// </summary>
    public void ApplyCliOptions() => Cli.Options = Settings.Cli.OptionsFor(Cli.Provider.Id);

    // --- the agent conversation -------------------------------------------

    /// <summary>
    /// Sends one request to the selected CLI and speaks its reply in persona.
    ///
    /// Turns are serialised so two overlapping requests -- a hotkey press landing
    /// on top of a standby phrase, say -- queue rather than interleave.
    /// </summary>
    public async Task AskAgentAsync(string request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request)) return;

        await _agentTurnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            OnAgentBusy?.Invoke(true);
            if (!Settings.Cli.KeepConversationContext) Cli.ResetConversation();

            var reply = new StringBuilder();
            string? failure = null;

            await foreach (var turnEvent in Cli.AskAsync(request, cancellationToken).ConfigureAwait(false))
            {
                switch (turnEvent.Kind)
                {
                    case CliTurnEventKind.AssistantText: reply.Append(turnEvent.Text); break;
                    case CliTurnEventKind.Notice: CptLog.Write("[agent] " + turnEvent.Text); break;
                    case CliTurnEventKind.Error: failure ??= turnEvent.Text; break;
                }
            }

            var answer = reply.ToString().Trim();
            CptLog.Write($"[cli] turn returned {answer.Length} chars"
                + (failure is null ? "" : " (error: " + failure + ")"));

            // The CLI refusing to run because it is too old is a fixable
            // failure, and the app used to just report its complaint verbatim
            // with no way to act on it. Update once per session and retry.
            if (answer.Length == 0 && !_cliUpdateAttempted
                && (CliInstaller.LooksOutOfDate(failure) || CliInstaller.LooksOutOfDate(answer)))
            {
                _cliUpdateAttempted = true;
                OnNotification?.Invoke($"{Cli.Provider.DisplayName} is out of date — updating it now.");

                var updateFailure = await Cli.UpdateAsync(
                    new Progress<string>(line => CptLog.Write("[cli] " + line)), cancellationToken)
                    .ConfigureAwait(false);

                if (updateFailure is not null)
                {
                    OnNotification?.Invoke("Could not update automatically: " + updateFailure);
                }
                else
                {
                    OnNotification?.Invoke($"{Cli.Provider.DisplayName} updated. Retrying.");
                    _agentTurnLock.Release();
                    try { await AskAgentAsync(request, cancellationToken).ConfigureAwait(false); }
                    finally { await _agentTurnLock.WaitAsync(CancellationToken.None).ConfigureAwait(false); }
                    return;
                }
            }

            if (answer.Length > 0)
            {
                await SpeakInPersonaAsync(answer, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var message = failure ?? "The agent returned nothing.";
                CptLog.Write("[agent] " + message);
                OnNotification?.Invoke(message);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer request, or shutting down.
        }
        catch (Exception ex)
        {
            // Nothing below here may fail silently. A turn that throws and says
            // nothing is indistinguishable from the app ignoring the question.
            CptLog.Write("[agent] turn failed: " + ex);
            OnNotification?.Invoke("That turn failed: " + ex.Message);
        }
        finally
        {
            OnAgentBusy?.Invoke(false);
            _agentTurnLock.Release();
        }
    }

    /// <summary>
    /// Rewrites text in the active persona's voice and speaks it. This is the
    /// output half of the app, shared by CLI replies and host-adapter captures.
    /// </summary>
    public async Task SpeakInPersonaAsync(string sourceMarkdown, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceMarkdown)) return;

        // A new reply supersedes whatever is still being spoken.
        var previous = Interlocked.Exchange(ref _currentTranslation, null);
        previous?.Cancel();
        previous?.Dispose();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _currentTranslation = cts;

        try
        {
            await Pipeline.TranslateAsync(ActivePersona, sourceMarkdown, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer reply.
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                      or System.Net.Http.HttpRequestException)
        {
            CptLog.Write("[pipeline] " + ex);
            OnNotification?.Invoke("Could not speak the reply: " + ex.Message);
        }
    }

    // --- voice input ------------------------------------------------------

    /// <summary>Begins a push-to-talk recording.</summary>
    public void StartListening()
    {
        try { Mic.Start(); }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            OnNotification?.Invoke("Microphone unavailable: " + ex.Message);
        }
    }

    /// <summary>
    /// Ends a push-to-talk recording, transcribes it, and routes the result: to
    /// the CLI when one is linked, otherwise back into whichever host adapter
    /// last spoke, so the feature still works without a linked CLI.
    /// </summary>
    public async Task StopListeningAndSendAsync()
    {
        var wavPath = Mic.Stop();
        if (string.IsNullOrEmpty(wavPath)) return;

        string transcript;
        try
        {
            transcript = await Stt.TranscribeAsync(wavPath).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(wavPath);
        }

        if (string.IsNullOrWhiteSpace(transcript)) return;
        await RouteRequestAsync(transcript).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a spoken request to the right place.
    ///
    /// An agent's trigger phrase at the front wins: it switches the active agent
    /// and the rest of the sentence is the question. That is what makes it
    /// possible to leave standby listening and address a different agent just by
    /// naming it.
    /// </summary>

    /// <summary>
    /// Stops the microphone and returns what was said, without sending it
    /// anywhere.
    ///
    /// Used when the user is recording a PHRASE rather than asking a question:
    /// the point is to capture exactly what recognition produces from their
    /// voice, so it can be stored and matched against later.
    /// </summary>
    public async Task<string> TranscribeListeningAsync()
    {
        var wavPath = Mic.Stop();
        if (string.IsNullOrEmpty(wavPath)) return "";

        try { return await Stt.TranscribeAsync(wavPath).ConfigureAwait(false); }
        finally { TryDelete(wavPath); }
    }

    private async Task RouteRequestAsync(string request)
    {
        var route = AgentRouter.Route(request, Settings.Agents.Agents);
        if (route.Agent is not null)
        {
            CptLog.Write($"[route] \"{route.Agent.TriggerPhrase}\" -> agent {route.Agent.Name}");
            SetActiveAgent(route.Agent.Id);
            request = route.Request;

            if (string.IsNullOrWhiteSpace(request))
            {
                OnNotification?.Invoke($"{route.Agent.Name} is listening.");
                return;
            }
        }

        if (Cli.IsReady)
        {
            await AskAgentAsync(request).ConfigureAwait(false);
            return;
        }

        if (_lastAdapter is { Length: > 0 } adapter)
        {
            await Ipc.SendAsync(adapter, new OutboundInject { Text = request, Submit = true }).ConfigureAwait(false);
            return;
        }

        CptLog.Write("[route] no agent linked; request dropped: " + request);
        OnNotification?.Invoke($"Heard “{request}”, but no agent is linked yet.");
    }

    // --- agents -----------------------------------------------------------

    /// <summary>Raised when the agent list or the active agent changes.</summary>
    public event Action? OnAgentsChanged;

    /// <summary>The agent currently answering, or null when none are configured.</summary>
    public AgentProfile? ActiveAgent => Settings.Agents.Active;

    /// <summary>
    /// Makes one agent the one that answers: its CLI runs the turn and its
    /// persona speaks the reply.
    /// </summary>
    public void SetActiveAgent(string? agentId)
    {
        var agent = Settings.Agents.ById(agentId);
        if (agent is null) return;

        Settings.Agents.ActiveId = agent.Id;
        Settings.Save();

        if (Personas.Get(agent.PersonaId) is { } persona) SetActivePersona(persona);
        ApplyAgentCli(agent);
        OnAgentsChanged?.Invoke();
    }

    /// <summary>
    /// Points both orchestrators at this agent's choices.
    ///
    /// Both, because an agent owns the whole pipeline: which CLI answers and on
    /// what model, and which CLI phrases the answer and on what model. They used
    /// to be global, so two agents could not differ in anything but name.
    /// </summary>
    private void ApplyAgentCli(AgentProfile agent)
    {
        var directory = string.IsNullOrWhiteSpace(agent.WorkingDirectory)
            ? Settings.ResolveCliWorkingDirectory()
            : agent.WorkingDirectory;

        Cli.Select(agent.ProviderId, directory);
        Cli.Options = agent.Options;

        var rewriteProvider = string.IsNullOrWhiteSpace(agent.RewriteProviderId)
            ? agent.ProviderId
            : agent.RewriteProviderId;

        RewriteCli.Select(rewriteProvider, directory);
        RewriteCli.Options = agent.RewriteOptions;

        CptLog.Write($"[agent] {agent.Name}: answers on {agent.ProviderId}, speaks via {rewriteProvider}");
    }

    /// <summary>Re-reads the agent list after it has been edited in settings.</summary>
    public void ReloadAgents()
    {
        PublishWakePhrases();
        if (Settings.Agents.Active is { } agent) ApplyAgentCli(agent);
        OnAgentsChanged?.Invoke();
    }

    // --- standby ----------------------------------------------------------

    /// <summary>One automatic CLI update per session, so a real failure cannot loop.</summary>
    private bool _cliUpdateAttempted;

    private StandbyListener? _standby;

    /// <summary>True while standby mode holds the microphone open.</summary>
    public bool IsStandbyRunning => _standby?.IsRunning == true;

    /// <summary>What the bar should currently show about standby.</summary>
    public StandbyUiState StandbyDisplayState =>
        _standby is not { IsRunning: true } ? StandbyUiState.Off
        : _standby.State == CPT.Core.Voice.StandbyState.Listening ? StandbyUiState.Listening
        : StandbyUiState.Sleeping;

    /// <summary>
    /// The phrase to say to wake THIS agent.
    ///
    /// Its own if it has one: the bar telling the user to say "hey agent" when
    /// they had configured "hey computer" was telling them to say the wrong
    /// thing.
    /// </summary>
    public string WakePhrase =>
        ActiveAgent?.TriggerPhrases.Find(p => !string.IsNullOrWhiteSpace(p))
        ?? Settings.Standby.WakePhrase;
    public string SendPhrase => Settings.Standby.SendPhrase;

    /// <summary>Turns hands-free listening on or off and remembers the choice.</summary>
    public void SetStandbyEnabled(bool enabled)
    {
        if (enabled == IsStandbyRunning) return;

        if (enabled)
        {
            if (!Stt.IsAvailable)
            {
                CptLog.Write("[standby] refused to start: speech recognition is unavailable");
                OnNotification?.Invoke(
                    "Standby needs speech recognition. Set the whisper binary and model in Settings.");
                OnStandbyStateChanged?.Invoke(StandbyUiState.Off);
                return;
            }

            _standby ??= CreateStandbyListener();
            _standby.Start();
        }
        else
        {
            _standby?.Stop();
        }

        Settings.Standby.Enabled = IsStandbyRunning;
        Settings.Save();
        OnStandbyStateChanged?.Invoke(StandbyDisplayState);
    }

    private StandbyListener CreateStandbyListener()
    {
        var listener = new StandbyListener(Stt, Settings.Standby);

        listener.Woke += () =>
        {
            OnPersonaAppear?.Invoke(ActivePersona.Name);
            OnStandbyStateChanged?.Invoke(StandbyUiState.Listening);
        };
        listener.Captured += text => OnTranscriptChunk?.Invoke(text);
        listener.Cancelled += () => OnStandbyStateChanged?.Invoke(StandbyUiState.Sleeping);
        listener.LevelChanged += level => OnAudioLevel?.Invoke(level);
        listener.Failed += message => OnNotification?.Invoke(message);
        listener.RequestReady += (request, addressed) =>
        {
            OnStandbyStateChanged?.Invoke(StandbyUiState.Sleeping);

            // Whoever was named is who answers. Without this, saying an agent's
            // own phrase woke the app and then handed the question to whichever
            // agent happened to be selected.
            if (addressed is { Length: > 0 }) SetActiveAgent(addressed);
            _ = RouteRequestAsync(request);
        };

        return listener;
    }

    /// <summary>
    /// Tells the listener every phrase that should wake it: the general one and
    /// each agent's own. Called whenever the agent list changes.
    /// </summary>
    private void PublishWakePhrases()
    {
        if (_standby is null) return;

        var phrases = Settings.Agents.Agents
            .SelectMany(agent => agent.TriggerPhrases
                .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
                .Select(phrase => (Phrase: phrase, Owner: agent.Id)))
            .ToList();

        _standby.ExtraWakePhrases = phrases;
        CptLog.Write($"[standby] wake phrases: \"{Settings.Standby.WakePhrase}\""
            + string.Concat(phrases.Select(p => $", \"{p.Phrase}\"")));
    }

    /// <summary>
    /// Rebuilds the standby listener after its settings change, so a new wake
    /// phrase or threshold takes effect without restarting the app.
    /// </summary>
    public async Task ReloadStandbyAsync()
    {
        var wasRunning = IsStandbyRunning;
        if (_standby is not null)
        {
            await _standby.DisposeAsync().ConfigureAwait(false);
            _standby = null;
        }
        if (wasRunning) SetStandbyEnabled(true);
    }


    // --- speaking volume --------------------------------------------------

    /// <summary>How loud replies are spoken, 0 to 1.</summary>
    public double SpeakingVolume
    {
        get => Settings.SpeakingVolume;
        set
        {
            var clamped = Math.Clamp(value, 0, 1);
            if (Math.Abs(clamped - Settings.SpeakingVolume) < 0.001) return;

            Settings.SpeakingVolume = clamped;
            StreamingAudioPlayer.Volume = (float)clamped;
            Pipeline.ApplyVolume();
            Settings.Save();
            OnVolumeChanged?.Invoke(clamped);
        }
    }

    /// <summary>Raised when the volume changes, so every control showing it agrees.</summary>
    public event Action<double>? OnVolumeChanged;

    // --- personas ---------------------------------------------------------

    public void SetActivePersona(Persona persona)
    {
        ActivePersona = persona;
        Settings.ActivePersonaId = persona.Id;
        Settings.Save();
        OnActivePersonaChanged?.Invoke(persona);
    }

    /// <summary>
    /// Loads the cloning model into VRAM without generating audio, so the first
    /// real utterance is not preceded by a multi-second stall.
    /// </summary>
    public async Task WarmCloneAsync(Persona persona, IProgress<string>? progress, CancellationToken ct = default)
    {
        if (CloneTts is not ChatterboxTts chatterbox)
            throw new InvalidOperationException("Voice cloning is not set up.");
        if (string.IsNullOrEmpty(persona.Voice.VoiceSampleFile) || !File.Exists(persona.Voice.VoiceSampleFile))
            throw new InvalidOperationException("Persona has no voice sample file.");

        chatterbox.Progress = progress;
        try { await chatterbox.WarmAsync(ct).ConfigureAwait(false); }
        finally { chatterbox.Progress = null; }
    }

    /// <summary>
    /// Speaks an in-character confirmation after setup. The seed is deliberately
    /// neutral so the persona's own prompt and quotes shape the wording.
    /// </summary>
    public Task SpeakConfirmationAsync(Persona persona, CancellationToken ct = default) =>
        Pipeline.TranslateAsync(
            persona,
            $"You are {persona.Name}. The user has just finished setting you up. " +
            "Greet them and confirm that you are active and ready to help. " +
            "Keep it short -- one or two sentences. Stay fully in character.",
            ct);

    /// <summary>Picks up a cloning engine that was configured after startup.</summary>
    public void ReloadCloningEngine()
    {
        var fresh = AppSettings.Load();
        Settings.ChatterboxPython = fresh.ChatterboxPython;
        Settings.ChatterboxScript = fresh.ChatterboxScript;

        if (CloneTts is not null) return;
        if (!ChatterboxTts.IsAvailable(Settings.ChatterboxPython, Settings.ChatterboxScript)) return;

        CloneTts = new ChatterboxTts(Settings.ChatterboxPython, Settings.ChatterboxScript);

        var replacement = new TranslationPipeline(new CliPersonaRewriter(RewriteCli), Tts, CloneTts);
        WirePipeline(replacement);
        Pipeline.Dispose();
        Pipeline = replacement;
    }

    // --- host adapters ----------------------------------------------------

    private void OnAdapterMessage(string adapter, InboundMessage message)
    {
        _lastAdapter = adapter;
        if (message.Type == "final" && !string.IsNullOrWhiteSpace(message.Text))
            _ = SpeakInPersonaAsync(message.Text);
    }

    private static IEnumerable<HostProfile> LoadHostProfiles()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "adapters");
        if (!Directory.Exists(directory)) yield break;

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            HostProfile? profile = null;
            try
            {
                profile = JsonSerializer.Deserialize<HostProfile>(File.ReadAllText(file), HostProfileJson);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                CptLog.Write($"[uia] ignoring bad adapter profile {file}: {ex.Message}");
            }
            if (profile is not null) yield return profile;
        }
    }

    private static readonly JsonSerializerOptions HostProfileJson = new() { PropertyNameCaseInsensitive = true };

    // --- construction helpers ---------------------------------------------

    private ChatterboxTts? CreateCloneEngineIfConfigured()
    {
        var available = ChatterboxTts.IsAvailable(Settings.ChatterboxPython, Settings.ChatterboxScript);
        CptLog.Write($"[tts] voice cloning available: {available}");
        return available ? new ChatterboxTts(Settings.ChatterboxPython, Settings.ChatterboxScript) : null;
    }

    private void WarmCloneIfActivePersonaNeedsIt()
    {
        if (CloneTts is not ChatterboxTts chatterbox) return;
        if (!string.Equals(ActivePersona.Voice.Engine, "chatterbox", StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrEmpty(ActivePersona.Voice.VoiceSampleFile)) return;
        if (!File.Exists(ActivePersona.Voice.VoiceSampleFile)) return;

        _ = Task.Run(async () =>
        {
            try { await chatterbox.WarmAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                OnNotification?.Invoke("Voice clone warmup failed: " + ex.Message);
            }
        });
    }

    private void WirePipeline(TranslationPipeline pipeline)
    {
        pipeline.OnAppear += name => OnPersonaAppear?.Invoke(name);
        pipeline.OnSpokenChunk += chunk => OnTranscriptChunk?.Invoke(chunk);
        pipeline.OnAudioLevel += level => OnAudioLevel?.Invoke(level);
        pipeline.OnDone += () => OnTranslationDone?.Invoke();
        pipeline.OnEngineFallback += message => OnNotification?.Invoke(message);
    }

    /// <summary>
    /// Copies bundled personas and their assets into the user's persona folder,
    /// one file at a time.
    ///
    /// The all-or-nothing check this replaced skipped every bundled persona as
    /// soon as a single default.json existed from an earlier install, so newly
    /// shipped personas never appeared after an upgrade. Nothing the user has
    /// touched is ever overwritten.
    /// </summary>
    private void SeedBundledPersonas()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "personas");
        if (!Directory.Exists(bundled)) return;

        foreach (var sourceDirectory in Directory.EnumerateDirectories(bundled))
        {
            var destinationDirectory = Path.Combine(Personas.Dir, Path.GetFileName(sourceDirectory));
            Directory.CreateDirectory(destinationDirectory);

            foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(
                    destinationDirectory, Path.GetRelativePath(sourceDirectory, source));
                if (File.Exists(destination)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                TryCopy(source, destination);
            }
        }

        foreach (var source in Directory.EnumerateFiles(bundled, "*.json"))
        {
            var destination = Path.Combine(Personas.Dir, Path.GetFileName(source));
            if (File.Exists(destination)) continue;
            SeedPersonaFile(source, destination);
        }
    }

    private void SeedPersonaFile(string source, string destination)
    {
        try
        {
            var persona = JsonSerializer.Deserialize<Persona>(File.ReadAllText(source), HostProfileJson);
            if (persona is null) { TryCopy(source, destination); return; }

            // Bundled personas reference their assets relatively; downstream code
            // calls File.Exists, so the paths must be absolute once installed.
            persona.Voice.VoiceSampleFile = ToAbsoluteAssetPath(persona.Voice.VoiceSampleFile);
            persona.Visual.ImageFile = ToAbsoluteAssetPath(persona.Visual.ImageFile);

            File.WriteAllText(destination, JsonSerializer.Serialize(persona, IndentedJson));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            CptLog.Write($"[personas] could not seed {source}: {ex.Message}");
            TryCopy(source, destination);
        }
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private string? ToAbsoluteAssetPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Personas.Dir, path));
    }

    private Persona LoadActivePersona()
    {
        var all = Personas.LoadAll().ToList();
        if (all.Count == 0)
        {
            var fallback = new Persona { Id = "default", Name = "Default" };
            Personas.Save(fallback);
            return fallback;
        }
        return all.FirstOrDefault(p => p.Id == Settings.ActivePersonaId) ?? all[0];
    }

    private static void TryCopy(string source, string destination)
    {
        try { File.Copy(source, destination, overwrite: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CptLog.Write($"[personas] could not copy {source}: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string ResolveOrDefault(string configured, string fallback) =>
        string.IsNullOrWhiteSpace(configured) ? fallback : configured;

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _currentTranslation?.Cancel();
        _currentTranslation?.Dispose();

        if (_standby is not null) _standby.DisposeAsync().AsTask().GetAwaiter().GetResult();

        _agentTurnLock.Dispose();
        Mic.Dispose();
        Uia?.Dispose();
        Ipc.Dispose();
        Cli.Dispose();
        Pipeline.Dispose();
        RewriteCli.Dispose();
    }
}
