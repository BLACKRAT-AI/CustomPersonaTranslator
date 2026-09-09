using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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

    /// <summary>Cancels the agent turn in flight, if there is one.</summary>
    private CancellationTokenSource? _currentTurn;

    /// <summary>
    /// Cancels background voice rendering, so a real reply never queues behind it.
    ///
    /// The clone is ONE subprocess and it is strictly serial. Pre-rendering two
    /// personas' acknowledgements is about forty clips at roughly four seconds
    /// each, and while that was running an actual answer waited its turn: the
    /// agent replied in the log and then said nothing for minutes. Warming is
    /// worth doing and worth abandoning the moment there is something real to
    /// say.
    /// </summary>
    private CancellationTokenSource? _warming;

    /// <summary>True while a turn is running, so the UI can offer to stop it.</summary>
    public bool IsAgentBusy => _agentTurnLock.CurrentCount == 0;
    private string? _lastAdapter;
    private bool _disposed;
    private readonly bool _startBackgroundServices;

    public AppSettings Settings { get; }
    public GpuTier Gpu { get; }

    public OfficialAppBridge OfficialBridge { get; } = new();
    public OfficialWindowObserver? OfficialScreen { get; private set; }
    private readonly OfficialEventGate _officialGate = new();
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

    /// <summary>Pre-rendered lines the agent says while it starts work.</summary>
    public AcknowledgementCache Acknowledgements { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CustomPersonaTranslator", "acknowledgements"));

    public bool CloningAvailable => CloneTts is not null;

    // --- events the UI subscribes to --------------------------------------

    public event Action<string>? OnPersonaAppear;
    public event Action<string>? OnTranscriptChunk;
    public event Action<float>? OnAudioLevel;
    public event Action? OnTranslationDone;

    /// <summary>True while a request is out with the CLI and no reply has been
    /// spoken yet. Drives the spinner, so waiting looks like waiting.</summary>
    public event Action<bool>? OnAgentBusy;
    public event Action<string, string>? OnConversationMessage;
    public event Action<string>? OnAgentActivity;

    /// <summary>Brief user-visible status, shown as a toast on the hologram.</summary>
    public event Action<string>? OnNotification;

    public event Action<Persona>? OnActivePersonaChanged;
    public event Action<CliStatus>? OnCliStatusChanged;
    public event Action<StandbyUiState>? OnStandbyStateChanged;

    public AppServices(bool startBackgroundServices = true)
    {
        _startBackgroundServices = startBackgroundServices;
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

        // Which build this is, and where it came from.
        //
        // "Are you sure the exe was updated?" is not answerable from a log that
        // only says the app started, and answering it wrongly costs an hour:
        // there is a published build in Programs and a Debug build in the repo,
        // and they are not the same code.
        CptLog.Write("=== CPT.Shell starting ===");
        CptLog.Write("[build] " + BuildStamp());
        CloneTts = CreateCloneEngineIfConfigured();

        Pipeline = new TranslationPipeline(new CliPersonaRewriter(RewriteCli), Tts, CloneTts);
        WirePipeline(Pipeline);

        Ipc.MessageReceived += OnAdapterMessage;
        if (startBackgroundServices) Ipc.Start();

        if (startBackgroundServices) Uia = new UiaHostAdapter(LoadHostProfiles(), (adapter, text) =>
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

        ContinuousMicCapture.DeviceIndex = Settings.MicrophoneDevice;
        if (startBackgroundServices)
        {
            OfficialBridge.Received += HandleOfficialEvent;
            OfficialBridge.Start();
            OfficialScreen = new OfficialWindowObserver(() => ActiveAgent?.ReceiveOfficialApp == true && ActiveAgent.OfficialSessionId == OfficialWindowObserver.Session, HandleOfficialEvent);
            OfficialScreen.StatusChanged += status => {
                if (ActiveAgent?.ReceiveOfficialApp == true && ActiveAgent.OfficialSessionId == OfficialWindowObserver.Session && !IsAgentBusy)
                    OnAgentActivity?.Invoke(status);
            };
            StartCloneWatchdog();
            WarmEveryAgentVoice();
        }
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
    /// One request runs at a time. A busy request gets explicit feedback rather
    /// than waiting in a queue that could execute after the user presses Stop.
    /// </summary>
    internal void HandleOfficialEvent(OfficialAppEvent item)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var agent = ActiveAgent;
            if (agent is null || !_officialGate.Accept(agent, item)) return;
            if (item.Kind == "UserPromptSubmit") OnConversationMessage?.Invoke("You (official app)", item.Text);
            OnAgentActivity?.Invoke(item.Kind switch {
                "PreToolUse" => "Official app: using " + item.Tool,
                "PostToolUse" => "Official app: tool returned - " + item.Tool,
                "PermissionRequest" => "Approval requested in official app",
                "Stop" => "Official app answered",
                "Interrupt" => "Official app interrupted",
                _ => "Official app working"
            });
            if (item.Kind == "Stop" && item.Text.Length > 0) _ = ReportOfficialAnswerAsync(agent, item.Text);
        });
    }

    private async Task ReportOfficialAnswerAsync(AgentProfile agent, string source)
    {
        OnConversationMessage?.Invoke("Official app", source);
        if (!agent.SpeakOfficialReplies) return;
        if (!await _agentTurnLock.WaitAsync(0).ConfigureAwait(false))
        {
            OnNotification?.Invoke("The official app answered; narration is busy.");
            return;
        }
        var persona = ActivePersona;
        using var turn = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _currentTurn = turn;
        OnAgentBusy?.Invoke(true);
        try
        {
            PauseVoiceWarming();
            using var rewriter = new CliOrchestrator(string.IsNullOrEmpty(agent.RewriteProviderId) ? agent.ProviderId : agent.RewriteProviderId, RewriteWorkspace());
            var options = new Dictionary<string, string>(agent.RewriteOptions);
            options.Remove("desktop");
            options.Remove("permissions");
            rewriter.Options = options;
            // Fresh context for every narration: no accumulated persona drift or task conversation.
            var result = new StringBuilder();
            OnAgentActivity?.Invoke("Preparing persona narration");
            await foreach (var part in new CliPersonaRewriter(rewriter).StreamRewriteAsync(persona, source, turn.Token).ConfigureAwait(false)) result.Append(part);
            if (result.Length == 0) { OnNotification?.Invoke("Persona rewrite failed; the answer is available in the official app."); return; }
            var answer = result.ToString();
            if (!PersonaRewrite.KeepsSubstance(source, answer)) { OnNotification?.Invoke("Persona rewrite omitted details; the answer is available in the official app."); return; }
            OnConversationMessage?.Invoke(persona.Name, answer);
            await Pipeline.SpeakAsync(persona, answer, turn.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { OnNotification?.Invoke("Local narration stopped. Official app tasks are controlled there."); }
        catch (Exception ex) { OnNotification?.Invoke("Narration failed: " + ex.Message); }
        finally { _currentTurn = null; _agentTurnLock.Release(); OnAgentBusy?.Invoke(false); OnAgentActivity?.Invoke("Waiting for official app"); }
    }

    public async Task AskAgentAsync(string request, CancellationToken cancellationToken = default)
    {
        if (ActiveAgent?.ReceiveOfficialApp == true)
        {
            if (ActiveAgent.OfficialSessionId != OfficialWindowObserver.Session)
            {
                OnNotification?.Invoke("This Codex events connection only receives replies. Choose ChatGPT / Codex app to send spoken requests.");
                return;
            }
            if (!await _agentTurnLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            { OnNotification?.Invoke("Wait for the current narration or stop it first."); return; }
            using var submission = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            submission.CancelAfter(TimeSpan.FromSeconds(20));
            _currentTurn = submission;
            OnAgentBusy?.Invoke(true);
            try
            {
                OnAgentActivity?.Invoke("Sending to the official app");
                await OfficialWindowController.SendAsync(request, submission.Token).ConfigureAwait(false);
                CptLog.Write("[official-app] request submitted for " + ActiveAgent.Name);
                OnNotification?.Invoke("Sent to ChatGPT / Codex");
            }
            catch (OperationCanceledException) { OnNotification?.Invoke("Submission stopped. Check the official app for a draft or running request."); }
            catch (Exception ex) { CptLog.Write("[official-app] submission failed: " + ex.Message); OnNotification?.Invoke(ex.Message); }
            finally { _currentTurn = null; _agentTurnLock.Release(); OnAgentBusy?.Invoke(false); }
            return;
        }
        if (string.IsNullOrWhiteSpace(request)) return;
        if (!await _agentTurnLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            OnNotification?.Invoke("An agent is still working. Stop it before sending another request.");
            return;
        }
        var persona = ActivePersona;
        var keepContext = ActiveAgent?.KeepContext ?? Settings.Cli.KeepConversationContext;
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var acknowledgementCancellation = CancellationTokenSource.CreateLinkedTokenSource(turn.Token);
        _currentTurn = turn;
        cancellationToken = turn.Token;
        var clock = Stopwatch.StartNew();
        var outcome = "Finished";
        Task acknowledged = Task.CompletedTask;
        string? openingResponse = null;
        var openingStarted = false;
        PauseVoiceWarming();
        try
        {
            // Apply live settings on every request, including edits made while
            // Settings is open. Selecting the same agent must not skip tool setup.
            if (ActiveAgent is { } selectedAgent) ApplyAgentCli(selectedAgent);
            OnAgentBusy?.Invoke(true);
            OnConversationMessage?.Invoke("You", request);
            OnAgentActivity?.Invoke("Sending to " + Cli.Provider.DisplayName);
            if (!keepContext) Cli.ResetConversation();
            var asked = CliPersonaRewriter.InVoiceOf(persona, request);
            var reply = new StringBuilder();
            string? failure = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                reply.Clear();
                failure = null;
                await foreach (var item in Cli.AskAsync(asked, cancellationToken).ConfigureAwait(false))
                {
                    switch (item.Kind)
                    {
                        case CliTurnEventKind.AssistantText: reply.Append(item.Text); break;
                        case CliTurnEventKind.Progress:
                            if (!openingStarted && item.Text.Trim().Length is > 0 and <= 240)
                            {
                                openingResponse = PersonaRewrite.WithoutInstructions(item.Text.Trim(), persona.SystemPrompt);
                                openingStarted = true;
                                CptLog.Write($"[timing] opening text ready in {clock.Elapsed.TotalSeconds:F1}s; starting voice immediately");
                                acknowledged = Pipeline.SpeakAsync(persona, openingResponse, acknowledgementCancellation.Token);
                            }
                            OnAgentActivity?.Invoke(item.Text.Trim());
                            break;
                        case CliTurnEventKind.Activity:
                            if (item.Kind == CliTurnEventKind.Activity && item.Text.EndsWith(" - failed", StringComparison.Ordinal))
                                outcome = "Finished with tool errors";
                            OnAgentActivity?.Invoke(item.Text.Trim());
                            break;
                        case CliTurnEventKind.Notice: CptLog.Write("[agent] " + item.Text); break;
                        case CliTurnEventKind.Error: failure ??= item.Text; break;
                    }
                }
                if (reply.Length > 0 || _cliUpdateAttempted || !CliInstaller.LooksOutOfDate(failure)) break;
                _cliUpdateAttempted = true;
                OnAgentActivity?.Invoke("Updating " + Cli.Provider.DisplayName);
                var updateFailure = await Cli.UpdateAsync(
                    new Progress<string>(line => CptLog.Write("[cli] " + line)), cancellationToken).ConfigureAwait(false);
                if (updateFailure is not null) { failure = updateFailure; break; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var answer = PersonaRewrite.WithoutInstructions(reply.ToString().Trim(), persona.SystemPrompt);
            CptLog.Write($"[timing] answer ready in {clock.Elapsed.TotalSeconds:F1}s ({answer.Length} chars)");
            // Publish text before any synthesis or playback can delay it.
            if (answer.Length > 0) OnConversationMessage?.Invoke(persona.Name, answer);
            if (failure is not null || answer.Length == 0)
            {
                outcome = "Failed";
                OnConversationMessage?.Invoke("Problem", failure ?? "The agent returned no answer. Please try again.");
            }
            // Providers may emit a direct answer as both progress and final text.
            // Let that already-started audio finish instead of cancelling and cloning it twice.
            var alreadySpeakingAnswer = openingStarted && string.Equals(answer, openingResponse, StringComparison.Ordinal);
            if (!alreadySpeakingAnswer) acknowledgementCancellation.Cancel();
            await FinishSpeaking(acknowledged).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (answer.Length > 0 && (!alreadySpeakingAnswer || !acknowledged.IsCompletedSuccessfully))
            {
                OnAgentActivity?.Invoke("Answer ready — preparing voice");
                await Pipeline.SpeakAsync(persona, answer, cancellationToken).ConfigureAwait(false);
            }
            else if (answer.Length == 0)
            {
                var message = CliInstaller.Explain(failure);
                OnNotification?.Invoke(message);
                await Pipeline.SpeakWithPresetAsync(persona, message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            outcome = "Stopped";
            OnConversationMessage?.Invoke("Stopped", "Request cancelled.");
        }
        catch (Exception ex)
        {
            outcome = "Failed";
            CptLog.Write("[agent] turn failed: " + ex);
            OnConversationMessage?.Invoke("Problem", ex.Message);
            OnNotification?.Invoke("That turn failed: " + ex.Message);
        }
        finally
        {
            acknowledgementCancellation.Cancel();
            await FinishSpeaking(acknowledged).ConfigureAwait(false);
            Interlocked.CompareExchange(ref _currentTurn, null, turn);
            OnAgentBusy?.Invoke(false);
            OnAgentActivity?.Invoke($"{outcome} - {clock.Elapsed.TotalSeconds:F1}s");
            _agentTurnLock.Release();
            WarmEveryAgentVoice();
        }
    }

    /// <summary>
    /// Lets the acknowledgement finish before the answer starts.
    ///
    /// Its failure is never the caller's problem: an acknowledgement that could
    /// not be spoken must not stop the answer that follows it.
    /// </summary>
    private static async Task FinishSpeaking(Task acknowledgement)
    {
        try { await acknowledgement.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { CptLog.Write("[ack] " + ex.Message); }
    }

    /// <summary>Stops background voice rendering. Whatever finished is kept.</summary>
    /// <summary>
    /// The token that background rendering runs under, so standing it down
    /// stands ALL of it down -- the pre-rendered acknowledgements and the clone
    /// warm-up alike. The warm-up used to run under no token at all, which made
    /// it the one job that could not be interrupted for the user.
    /// </summary>
    private CancellationToken WarmingToken() => (_warming ??= new CancellationTokenSource()).Token;

    private void PauseVoiceWarming()
    {
        var warming = Interlocked.Exchange(ref _warming, null);
        if (warming is null) return;

        try { warming.Cancel(); } catch (ObjectDisposedException) { }
        warming.Dispose();
    }

    /// <summary>
    /// Stops whatever the agent is doing: the turn in flight and anything still
    /// being spoken.
    ///
    /// Both, because they are one thing to the user. Stopping the CLI while a
    /// minute of synthesised speech is still queued would look like the button
    /// did nothing.
    /// </summary>
    public void CancelAgent()
    {
        if (ActiveAgent?.ReceiveOfficialApp == true) OnNotification?.Invoke("Stopping local narration only. Stop the task in the official app.");
        var turn = Interlocked.Exchange(ref _currentTurn, null);
        var speech = Interlocked.Exchange(ref _currentTranslation, null);

        if (turn is null && speech is null) return;

        CptLog.Write("[agent] cancelled by the user");

        try { turn?.Cancel(); } catch (ObjectDisposedException) { }
        try { speech?.Cancel(); } catch (ObjectDisposedException) { }

        // Async operation owners dispose their cancellation sources.

        Pipeline.StopSpeaking();
    }


    /// <summary>
    /// Says something before the work starts.
    ///
    /// A coding turn takes a minute, and silence for a minute after being
    /// spoken to is indistinguishable from not having been heard.
    ///
    /// It plays a PRE-RENDERED line. Synthesising one takes 3.6 s on a warm
    /// clone and 47 s on a cold one, which would delay the very thing the
    /// acknowledgement exists to prevent; a cached line costs a file read.
    /// </summary>
    private async Task AcknowledgeAsync(string request, CancellationToken cancellationToken)
    {
        var line = Acknowledgements.ReadyFor(ActivePersona, request);
        if (line is not null)
        {
            try
            {
                await Pipeline.SpeakCachedAsync(line, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { CptLog.Write("[ack] " + ex.Message); }
        }

        // Uncached speech would occupy the clone worker and delay the real answer.
        // The wake tone and activity indicator already acknowledge the request.

    }

    /// <summary>
    /// Renders this persona's acknowledgements in the background, if they are
    /// missing. Runs at startup and whenever the persona changes.
    /// </summary>
    public void WarmAcknowledgements() => WarmAcknowledgementsFor(ActivePersona);

    /// <summary>
    /// Gets every agent's voice ready, not just the one in front.
    ///
    /// An agent is a loadout -- a CLI, a persona and a cloned voice -- and
    /// calling one by name switches all three at once. If only the active
    /// agent's lines had been rendered, the moment you said another agent's
    /// name it would answer in the preset voice while its own was still being
    /// built, which is the one moment it most obviously should sound like
    /// itself. Rendering is serialised inside the cache, so this is a queue
    /// rather than a stampede, and each line is rendered once ever.
    /// </summary>
    public void WarmEveryAgentVoice()
    {
        if (!_startBackgroundServices || _disposed || IsAgentBusy) return;
        WarmAcknowledgements();

        foreach (var agent in Settings.Agents.Agents)
        {
            if (string.IsNullOrWhiteSpace(agent.PersonaId)) continue;
            if (string.Equals(agent.PersonaId, ActivePersona.Id, StringComparison.Ordinal)) continue;
            if (Personas.Get(agent.PersonaId) is { } persona) WarmAcknowledgementsFor(persona);
        }
    }

    private void WarmAcknowledgementsFor(Persona persona)
    {
        // Turbo speaks task-specific openings; stock acknowledgement generation
        // would compete with those requests and warm the wrong model.
        if (persona.Voice.CloneModel == "turbo") return;
        if (!_startBackgroundServices || _disposed || IsAgentBusy || IsStandbyRunning) return;
        if (persona.Voice.Engine.Equals("chatterbox", StringComparison.OrdinalIgnoreCase) && CloneTts is null) return;
        var engine = string.Equals(persona.Voice.Engine, "chatterbox", StringComparison.OrdinalIgnoreCase)
                     && CloneTts is not null
            ? CloneTts
            : Tts;

        var voiceRef = engine is ChatterboxTts && !string.IsNullOrEmpty(persona.Voice.VoiceSampleFile)
            ? persona.Voice.VoiceSampleFile!
            : persona.Voice.VoiceRef;

        var warming = _warming ??= new CancellationTokenSource();
        var token = warming.Token;

        _ = Task.Run(async () =>
        {
            // Words first, then voice. Rendering the plain English wording and
            // then discovering the persona says it differently would mean
            // synthesising every line twice.
            await Acknowledgements.EnsureVoicedAsync(persona, Rewriter, token).ConfigureAwait(false);


            // The small model listens back to each rendered line. It is the
            // same one that spots the wake phrase, so it is already installed
            // and costs half a second per check.
            var checker = WhisperCpp.ForWakeSpotting(
                Settings.WhisperPath, NullIfEmpty(Settings.WhisperModelPath));

            await Acknowledgements.BuildAsync(persona, engine, voiceRef, checker, token)
                .ConfigureAwait(false);
        }, token);
    }

    /// <summary>
    /// Rewrites text in the active persona's voice and speaks it. This is the
    /// output half of the app, shared by CLI replies and host-adapter captures.
    /// </summary>
    public async Task RepeatMessageAsync(CPT.Core.Models.Persona persona, string text)
    {
        if (!await _agentTurnLock.WaitAsync(0).ConfigureAwait(false))
        {
            OnNotification?.Invoke("Wait for the current reply or stop it before repeating a message.");
            return;
        }
        using var cts = new CancellationTokenSource();
        _currentTurn = cts;
        try
        {
            OnAgentBusy?.Invoke(true);
            OnAgentActivity?.Invoke("Repeating message");
            await Pipeline.SpeakAsync(persona, text, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            Interlocked.CompareExchange(ref _currentTurn, null, cts);
            _agentTurnLock.Release();
            OnAgentBusy?.Invoke(false);
            OnAgentActivity?.Invoke("Ready");
        }
    }

    public async Task SpeakInPersonaAsync(string sourceMarkdown, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceMarkdown)) return;

        // A new reply supersedes whatever is still being spoken.
        var previous = Interlocked.Exchange(ref _currentTranslation, null);
        previous?.Cancel();

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
        finally
        {
            Interlocked.CompareExchange(ref _currentTranslation, null, cts);
            cts.Dispose();
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
            OnAgentActivity?.Invoke("Transcribing your request");
            transcript = await Stt.TranscribeAsync(wavPath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CptLog.Write("[stt] " + ex);
            OnNotification?.Invoke("Could not transcribe the recording: " + ex.Message);
            return;
        }
        finally
        {
            TryDelete(wavPath);
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            OnNotification?.Invoke("No speech was detected. Try again or type your request.");
            return;
        }
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

    public async Task RouteRequestAsync(string request)
    {
        if (IsAgentBusy)
        {
            OnNotification?.Invoke("An agent is still working. Stop it before sending another request.");
            return;
        }
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

        if (Cli.IsReady || ActiveAgent is not null || _lastAdapter is null)
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

    /// <summary>Version, build time and path of the binary that is running.</summary>
    private static string BuildStamp()
    {
        var assembly = System.Reflection.Assembly.GetEntryAssembly();
        var version = assembly?.GetName().Version?.ToString() ?? "unknown";
        var path = Environment.ProcessPath ?? AppContext.BaseDirectory;

        var built = "";
        try { built = "  built " + File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return version + built + "  " + path;
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
        if (agent is null || (agent.Id == ActiveAgent?.Id && agent.PersonaId == ActivePersona.Id)) return;
        if (IsAgentBusy)
        {
            OnNotification?.Invoke("Stop the current request before switching agents.");
            return;
        }

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
        AgentSetup.EnsureConfigured(agent);
        var directory = string.IsNullOrWhiteSpace(agent.WorkingDirectory)
            ? Settings.ResolveCliWorkingDirectory()
            : agent.WorkingDirectory;

        Cli.Select(agent.ProviderId, directory, agent.Id);
        Cli.Options = agent.Options;
        Cli.ExtraArguments = agent.ProviderId == CliProviderCatalog.CodexId
            && agent.Options.TryGetValue("desktop", out var desktop) && desktop == "on"
            ? DesktopToolServer.CodexArguments() : [];


        var rewriteProvider = string.IsNullOrWhiteSpace(agent.RewriteProviderId)
            ? agent.ProviderId
            : agent.RewriteProviderId;

        // The rewrite runs in an EMPTY directory, with no inherited permissions.
        //
        // It was running in the agent's own working directory with the agent's
        // "never ask" permissions, so a request to rephrase three sentences
        // became a repository investigation: it read files, took eight seconds
        // and answered about the project instead. A text transformation has no
        // business seeing a codebase.
        RewriteCli.Select(rewriteProvider, RewriteWorkspace());

        var rewriteOptions = new Dictionary<string, string>(
            agent.RewriteOptions.Count > 0 ? agent.RewriteOptions : agent.Options,
            StringComparer.OrdinalIgnoreCase);
        rewriteOptions.Remove("permissions");
        rewriteOptions.Remove("desktop");
        RewriteCli.Options = rewriteOptions;

        CptLog.Write($"[agent] {agent.Name}: answers on {agent.ProviderId}, speaks via {rewriteProvider}");
    }


    /// <summary>
    /// An empty folder for the rewrite CLI to run in.
    ///
    /// Deliberately empty and outside any project: a coding CLI given a
    /// repository will use it, and the rewrite has nothing to look up. This is
    /// the difference between a three-second rephrase and an eight-second
    /// investigation that answers the wrong question.
    /// </summary>
    private static string RewriteWorkspace()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CustomPersonaTranslator", "voice");

        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Re-reads the agent list after it has been edited in settings.</summary>
    public void ReloadAgents()
    {
        PublishWakePhrases();
        if (!IsAgentBusy && Settings.Agents.Active is { } agent) ApplyAgentCli(agent);

        // A newly added agent has no voice rendered yet, and the moment it is
        // worth having is the first time its name is said.
        WarmEveryAgentVoice();
        OnAgentsChanged?.Invoke();
    }

    // --- standby ----------------------------------------------------------

    /// <summary>One automatic CLI update per session, so a real failure cannot loop.</summary>
    private bool _cliUpdateAttempted;
    private readonly SemaphoreSlim _warmLock = new(1, 1);
    private Timer? _cloneWatchdog;
    private string? _warmedFor;


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

            // Before Start, every time. Without this the listener knows only the
            // general phrase from settings, so an agent's own name is heard
            // perfectly and ignored -- which is exactly what the log showed:
            //     heard (Sleeping): Hey computer.
            PublishWakePhrases();

            _standby.Start();
            CptLog.Write($"[standby] running={_standby.IsRunning}");
        }
        else
        {
            CptLog.Write("[standby] switched off");
            _standby?.Stop();
        }

        Settings.Standby.Enabled = IsStandbyRunning;
        Settings.Save();
        OnStandbyStateChanged?.Invoke(StandbyDisplayState);
    }

    private StandbyListener CreateStandbyListener()
    {
        // Two recognisers, because the two jobs want opposite things. Hearing a
        // request accurately wants the largest model installed; noticing your
        // own name wants the fastest, and with small.en that difference was the
        // three seconds between being called and answering.
        var spotter = WhisperCpp.ForWakeSpotting(
            Settings.WhisperPath, NullIfEmpty(Settings.WhisperModelPath));

        CptLog.Write(spotter is null
            ? "[standby] no small model installed; waking uses " + Stt.ModelPath
            : "[standby] wake spotter: " + Path.GetFileName(spotter.ModelPath));

        var listener = new StandbyListener(Stt, Settings.Standby, spotter);

        listener.Woke += addressed =>
        {
            // Give recognition the machine, now.
            //
            // Warming the voices renders one line after another on the same GPU
            // whisper needs, and it was only ever stood down when a TURN began
            // -- which is after the request has been recognised, so it competed
            // with exactly the words it was waiting for. Measured on this
            // machine: a 65 s clone warm-up held the request "open up FL Studio
            // and play the theme" for 23 s, by which time the user had given up
            // and switched standby off. Being named is the signal that words
            // are coming; nothing pre-rendered matters more than hearing them.
            PauseVoiceWarming();

            // Whoever was named is who is now here. Doing this on the WAKE, not
            // on the finished request, is what makes calling an agent by name
            // work: saying "hey jarvis" and then pausing used to summon nobody,
            // because the switch waited for a request that never came.
            if (addressed is { Length: > 0 }) SetActiveAgent(addressed);

            // A tone, so waking is something you hear rather than something you
            // have to test for by speaking.
            ReadyChime.PlayReady();
            OnStandbyStateChanged?.Invoke(StandbyUiState.Listening);
        };
        listener.Captured += text =>
        {
            OnTranscriptChunk?.Invoke(text);
            OnAgentActivity?.Invoke("Heard: " + text);
        };
        listener.Cancelled += () =>
        {
            // Abandoning a half-dictated request also stops whatever the previous
            // one is still doing -- from across the room those are one act.
            CancelAgent();
            OnStandbyStateChanged?.Invoke(StandbyUiState.Sleeping);
        };

        // "Jarvis, stop" while it is working or talking. Nothing is dictated and
        // nothing is answered: the turn in flight and the speech are cut.
        listener.StopRequested += () =>
        {
            CancelAgent();
            Pipeline.StopSpeaking();
            OnStandbyStateChanged?.Invoke(StandbyUiState.Sleeping);
        };
        listener.LevelChanged += level => OnAudioLevel?.Invoke(level);
        listener.Failed += message => OnNotification?.Invoke(message);
        listener.RequestReady += (request, addressed) =>
        {
            OnStandbyStateChanged?.Invoke(StandbyUiState.Sleeping);
            ReadyChime.PlayTaken();

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

    public void RefreshSavedPersona(Persona persona)
    {
        if (ActivePersona.Id != persona.Id) return;
        ActivePersona = persona;
        OnActivePersonaChanged?.Invoke(persona);
    }

    public void SetActivePersona(Persona persona)
    {
        ActivePersona = persona;
        Settings.ActivePersonaId = persona.Id;
        Settings.Save();
        WarmAcknowledgements();
        _ = EnsureCloneReadyAsync(WarmingToken());   // a new persona means a new reference
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

        chatterbox.CloneModel = persona.Voice.CloneModel;
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

    /// <summary>Loads the persistent voice process while idle. Real replies take priority.</summary>
    private async Task EnsureCloneReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed || IsAgentBusy) return;
        if (CloneTts is not ChatterboxTts chatterbox) return;

        var persona = ActivePersona;
        if (!string.Equals(persona.Voice.Engine, "chatterbox", StringComparison.OrdinalIgnoreCase)) return;

        var reference = persona.Voice.VoiceSampleFile;
        if (string.IsNullOrEmpty(reference) || !File.Exists(reference)) return;

        // Keyed on the reference, because a different clip means a different
        // analysis and the old warm-up counts for nothing.
        var key = persona.Id + "|" + reference + "|" + persona.Voice.CloneModel;
        chatterbox.CloneModel = persona.Voice.CloneModel;
        if (_warmedFor == key && chatterbox.IsWarm) return;

        await _warmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_warmedFor == key && chatterbox.IsWarm) return;

            var clock = Stopwatch.StartNew();
            await chatterbox.WarmAsync(cancellationToken).ConfigureAwait(false);

            _warmedFor = key;
            CptLog.Write($"[tts] clone warm for {persona.Name} in {clock.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException)
        {
            // Shutting down, or the persona changed under us.
        }
        catch (Exception ex)
        {
            _warmedFor = null;
            CptLog.Write("[tts] clone warm-up failed: " + ex.Message);
        }
        finally
        {
            _warmLock.Release();
        }
    }

    /// <summary>
    /// Checks every couple of minutes that the clone is still up, and brings it
    /// back if the subprocess has died -- so the next reply is never the one
    /// that pays the forty-seven seconds.
    /// </summary>
    private void StartCloneWatchdog()
    {
        _cloneWatchdog = new Timer(
            _ => _ = EnsureCloneReadyAsync(WarmingToken()),
            null,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMinutes(2));
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
        var id = Settings.Agents.Active?.PersonaId;
        if (string.IsNullOrWhiteSpace(id)) id = Settings.ActivePersonaId;
        return all.FirstOrDefault(p => p.Id == id) ?? all[0];
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
        CancelAgent();
        PauseVoiceWarming();
        _cloneWatchdog?.Dispose();

        _currentTranslation?.Cancel();
        _currentTranslation?.Dispose();

        if (_standby is not null) _standby.DisposeAsync().AsTask().GetAwaiter().GetResult();

        Mic.Dispose();
        Uia?.Dispose();
        OfficialScreen?.Dispose();
        OfficialBridge.Dispose();
        Ipc.Dispose();
        Cli.Dispose();
        Pipeline.Dispose();
        RewriteCli.Dispose();
        Acknowledgements.Dispose();
        _cloneWatchdog?.Dispose();
        (CloneTts as IDisposable)?.Dispose();
    }
}
