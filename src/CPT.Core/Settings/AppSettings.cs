using System;
using System.Collections.Generic;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using CPT.Core.Diagnostics;

namespace CPT.Core.Settings;


/// <summary>
/// Which CLI restates answers in the persona's voice, and how.
///
/// Separate from the agent's CLI on purpose: the agent should be the strongest
/// model available, and restating three sentences should be the cheapest.
/// </summary>
public sealed class RewriteSettings
{
    /// <summary>Provider id, or empty to use whatever the active agent uses.</summary>
    public string ProviderId { get; set; } = "";

    /// <summary>Per-provider option choices, exactly as CliSettings stores them.</summary>
    public Dictionary<string, Dictionary<string, string>> ProviderOptions { get; set; } = [];

    /// <summary>The stored choices for one provider. Never null.</summary>
    public Dictionary<string, string> OptionsFor(string providerId)
    {
        if (ProviderOptions.TryGetValue(providerId, out var stored)) return stored;

        var created = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ProviderOptions[providerId] = created;
        return created;
    }
}
/// <summary>
/// Which coding CLI CPT talks to, and how.
/// </summary>
public sealed class CliSettings
{
    /// <summary>Id from CliProviderCatalog, e.g. "claude-code".</summary>
    public string ProviderId { get; set; } = "claude-code";

    /// <summary>
    /// Install and verify the selected CLI on startup, so a first run needs
    /// nothing from the user beyond signing in.
    /// </summary>
    public bool AutoSetup { get; set; } = true;

    /// <summary>
    /// Directory CLI turns run in. Empty means the user's profile directory,
    /// which keeps an agent out of whatever folder CPT happened to launch from.
    /// </summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>Carry conversation context from one turn to the next.</summary>
    public bool KeepConversationContext { get; set; } = true;

    /// <summary>
    /// Per-provider option choices: provider id, then option id, then choice id.
    /// Kept per provider because switching CLIs should not silently carry a model
    /// or permission level across to one where it means something else.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ProviderOptions { get; set; } = [];

    /// <summary>The stored choices for one provider. Never null.</summary>
    public Dictionary<string, string> OptionsFor(string providerId)
    {
        if (ProviderOptions.TryGetValue(providerId, out var stored)) return stored;

        var created = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ProviderOptions[providerId] = created;
        return created;
    }
}

/// <summary>
/// Hands-free operation: CPT listens continuously, wakes on a phrase, and sends
/// the dictation that follows when it hears the send phrase.
/// </summary>
public sealed class StandbySettings
{
    /// <summary>Master switch. Off by default; the microphone stays idle.</summary>
    public bool Enabled { get; set; }

    /// <summary>Phrase that starts capturing a request, e.g. "hey agent".</summary>
    /// <summary>
    /// Phrases that wake standby.
    ///
    /// A list for the same reason an agent's is: what a recogniser produces from
    /// a given voice and microphone is not always what was typed, and recording
    /// a few removes the guesswork.
    /// </summary>
    public List<string> WakePhrases { get; set; } = ["hey agent"];

    /// <summary>The single phrase this used to hold, so older settings still load.</summary>
    public string WakePhrase
    {
        get => WakePhrases.Count > 0 ? WakePhrases[0] : "";
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!WakePhrases.Exists(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase)))
                WakePhrases.Insert(0, value.Trim());
        }
    }

    /// <summary>Phrase that ends capture and sends what was captured, e.g. "send it".</summary>
    public string SendPhrase { get; set; } = "send it";

    /// <summary>Phrase that throws the captured request away without sending.</summary>
    public string CancelPhrase { get; set; } = "never mind";

    /// <summary>
    /// Send automatically after this many seconds of silence, so a forgotten send
    /// phrase does not leave CPT recording forever. Zero disables the timeout.
    /// </summary>
    /// <summary>
    /// How long a pause ends a request.
    ///
    /// Two seconds, because this is how long the user waits after finishing a
    /// sentence before anything at all happens. It was eight, on a timer that
    /// ticked once a second, so asking a question in one breath was followed by
    /// up to nine seconds of silence -- which is indistinguishable from standby
    /// not working, and is exactly what it was reported as.
    /// </summary>
    public int SilenceTimeoutSeconds { get; set; } = 2;

    /// <summary>Hard cap on one dictated request, after which it is sent as-is.</summary>
    public int MaxRequestSeconds { get; set; } = 120;

    /// <summary>
    /// Loudness below which a frame counts as silence, 0 to 1. Raise it in a noisy
    /// room; lower it if quiet speech is being clipped.
    /// </summary>
    public float SilenceThreshold { get; set; } = 0.02f;

    /// <summary>Play a short tone when the wake phrase is recognised.</summary>
    public bool AudibleWakeConfirmation { get; set; } = true;
}

/// <summary>
/// All persisted application settings.
/// Stored as JSON at %LOCALAPPDATA%\CustomPersonaTranslator\settings.json.
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public string ActivePersonaId { get; set; } = "default";

    // --- Local inference ---------------------------------------------------
    public string LlamaCppExe { get; set; } = "";
    public string LlamaCppModel { get; set; } = "";
    public int LlamaCppPort { get; set; } = 18080;
    public int LlamaCppGpuLayers { get; set; } = 32;
    public int LlamaCppCtxSize { get; set; } = 4096;

    // --- Speech ------------------------------------------------------------
    public string PiperPath { get; set; } = "piper";
    public string PiperModelsDir { get; set; } = "";
    public string WhisperPath { get; set; } = "whisper-cli";
    public string WhisperModelPath { get; set; } = "";

    /// <summary>
    /// Which microphone to listen to. -1 is the system default, which is not
    /// always a device that hears anything: this machine offers five and only
    /// one of them has a signal.
    /// </summary>
    public int MicrophoneDevice { get; set; } = -1;
    public string TtsEngine { get; set; } = "piper";   // piper | chatterbox
    public bool AutoDowngradeOnNoGpu { get; set; } = true;

    /// <summary>
    /// Speaking volume, 0 to 1. Full by default: a cloned voice is often much
    /// quieter than a Piper preset, and a reply nobody can hear is no reply.
    /// </summary>
    public double SpeakingVolume { get; set; } = 1.0;
    public string ChatterboxPython { get; set; } = "";
    public string ChatterboxScript { get; set; } = "";

    // --- Hosts and hotkeys -------------------------------------------------
    public int IpcPort { get; set; } = 17872;
    public string HotkeyToggleWindow { get; set; } = "ctrl+shift+space";
    public string HotkeyPushToTalk { get; set; } = "ctrl+shift+m";
    public string HotkeyStandby { get; set; } = "ctrl+shift+s";

    // --- Media tools -------------------------------------------------------
    public string YtDlpPath { get; set; } = "yt-dlp";
    public string FfmpegPath { get; set; } = "ffmpeg";

    // --- Feature areas -----------------------------------------------------
    public CliSettings Cli { get; set; } = new();
    public StandbySettings Standby { get; set; } = new();

    /// <summary>
    /// The configured agents. An agent is a CLI paired with a persona and a
    /// phrase, so several can be addressed by voice without opening settings.
    /// </summary>
    public Agents.AgentBook Agents { get; set; } = new();

    /// <summary>Which CLI restates answers in the persona's voice, and how.</summary>
    public RewriteSettings Rewrite { get; set; } = new();


    /// <summary>
    /// Brings forward settings whose defaults have changed for a good reason.
    ///
    /// Only exact old defaults are touched: a value the user chose is theirs.
    /// </summary>
    private void Migrate()
    {
        // Eight seconds of silence before a request was sent felt like standby
        // being broken. Anyone still on that value never picked it.
        if (Standby.SilenceTimeoutSeconds == 8) Standby.SilenceTimeoutSeconds = 2;
    }

    /// <summary>Location of the settings file.</summary>
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CustomPersonaTranslator", "settings.json");

    /// <summary>
    /// Loads settings, writing a default file when none exists. A corrupt file is
    /// reported and replaced rather than crashing startup, because the app is far
    /// more useful with default settings than not running at all.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(text, ReadOptions);
                if (loaded is not null)
                {
                    loaded.Migrate();

                    // Options added in a later version are simply absent from an
                    // older file, and take their default. Writing the file back
                    // whenever the round-trip differs keeps settings.json a
                    // complete, editable description of what this build supports.
                    if (!string.Equals(JsonSerializer.Serialize(loaded, WriteOptions), text, StringComparison.Ordinal))
                        loaded.Save();

                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            CptLog.Write("[settings] could not be read, falling back to defaults: " + ex.Message);
        }

        var defaults = new AppSettings();
        defaults.Save();
        return defaults;
    }

    /// <summary>
    /// Writes settings to disk. The write goes to a temporary file first so an
    /// interrupted save cannot leave a half-written settings file behind.
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, WriteOptions));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CptLog.Write("[settings] could not be saved: " + ex.Message);
        }
    }

    /// <summary>
    /// The directory CLI turns should run in: the configured one when it exists,
    /// otherwise the user profile.
    /// </summary>
    public string ResolveCliWorkingDirectory()
    {
        var configured = Cli.WorkingDirectory;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }
}
