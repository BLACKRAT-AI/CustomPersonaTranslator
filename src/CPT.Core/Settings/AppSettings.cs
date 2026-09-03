using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CPT.Core.Diagnostics;

namespace CPT.Core.Settings;

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
    public string WakePhrase { get; set; } = "hey agent";

    /// <summary>Phrase that ends capture and sends what was captured, e.g. "send it".</summary>
    public string SendPhrase { get; set; } = "send it";

    /// <summary>Phrase that throws the captured request away without sending.</summary>
    public string CancelPhrase { get; set; } = "never mind";

    /// <summary>
    /// Send automatically after this many seconds of silence, so a forgotten send
    /// phrase does not leave CPT recording forever. Zero disables the timeout.
    /// </summary>
    public int SilenceTimeoutSeconds { get; set; } = 8;

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
    public string TtsEngine { get; set; } = "piper";   // piper | chatterbox
    public bool AutoDowngradeOnNoGpu { get; set; } = true;
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
