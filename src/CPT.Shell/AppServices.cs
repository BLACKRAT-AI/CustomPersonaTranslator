using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CPT.Core.Cli;
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
    public LlamaCppServer LlmServer { get; }
    public LlamaCppClient Llm { get; }
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

    /// <summary>Brief user-visible status, shown as a toast on the hologram.</summary>
    public event Action<string>? OnNotification;

    public event Action<Persona>? OnActivePersonaChanged;
    public event Action<CliStatus>? OnCliStatusChanged;
    public event Action<StandbyUiState>? OnStandbyStateChanged;

    public AppServices()
    {
        Settings = AppSettings.Load();
        Gpu = HardwareProbe.DetectGpu();

        Personas = new PersonaStore();
        SeedBundledPersonas();
        ActivePersona = LoadActivePersona();

        Ipc = new IpcServer(Settings.IpcPort);

        LlmServer = new LlamaCppServer(
            ResolveOrDefault(Settings.LlamaCppExe,
                Path.Combine(AppContext.BaseDirectory, "tools", "llama", "llama-server.exe")),
            ResolveOrDefault(Settings.LlamaCppModel,
                Path.Combine(AppContext.BaseDirectory, "tools", "llama", "models", "default.gguf")),
            port: Settings.LlamaCppPort,
            ctxSize: Settings.LlamaCppCtxSize,
            nGpuLayers: Settings.LlamaCppGpuLayers);
        _ = Task.Run(StartLlmServerAsync);

        Llm = new LlamaCppClient(baseUrl: LlmServer.BaseUrl);
        Tts = new PiperTts(
            piperPath: Settings.PiperPath,
            modelDir: string.IsNullOrEmpty(Settings.PiperModelsDir)
                ? Path.Combine(AppContext.BaseDirectory, "models", "piper")
                : Settings.PiperModelsDir);
        Stt = new WhisperCpp(Settings.WhisperPath, NullIfEmpty(Settings.WhisperModelPath));

        CptLog.Write("=== CPT.Shell starting ===");
        CloneTts = CreateCloneEngineIfConfigured();

        Pipeline = new TranslationPipeline(Llm, Tts, CloneTts);
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
        ApplyCliOptions();

        WarmCloneIfActivePersonaNeedsIt();
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

    private async Task StartLlmServerAsync()
    {
        try
        {
            await LlmServer.StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            CptLog.Write("[llm] local server did not start: " + ex.Message);
            OnNotification?.Invoke("Local rewrite model unavailable: " + ex.Message);
        }
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
        finally
        {
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

    private async Task RouteRequestAsync(string request)
    {
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

        OnNotification?.Invoke($"Heard “{request}”, but no agent is linked yet.");
    }

    // --- standby ----------------------------------------------------------

    private StandbyListener? _standby;

    /// <summary>True while standby mode holds the microphone open.</summary>
    public bool IsStandbyRunning => _standby?.IsRunning == true;

    /// <summary>What the bar should currently show about standby.</summary>
    public StandbyUiState StandbyDisplayState =>
        _standby is not { IsRunning: true } ? StandbyUiState.Off
        : _standby.State == CPT.Core.Voice.StandbyState.Listening ? StandbyUiState.Listening
        : StandbyUiState.Sleeping;

    public string WakePhrase => Settings.Standby.WakePhrase;
    public string SendPhrase => Settings.Standby.SendPhrase;

    /// <summary>Turns hands-free listening on or off and remembers the choice.</summary>
    public void SetStandbyEnabled(bool enabled)
    {
        if (enabled == IsStandbyRunning) return;

        if (enabled)
        {
            if (!Stt.IsAvailable)
            {
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
        listener.RequestReady += request =>
        {
            OnStandbyStateChanged?.Invoke(StandbyUiState.Sleeping);
            _ = RouteRequestAsync(request);
        };

        return listener;
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

        var replacement = new TranslationPipeline(Llm, Tts, CloneTts);
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
        Llm.Dispose();
        LlmServer.Dispose();
    }
}
