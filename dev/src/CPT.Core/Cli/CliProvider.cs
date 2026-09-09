using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CPT.Core.Cli;

/// <summary>
/// How a provider frames the assistant's reply on stdout when it is driven
/// head-less. Each value maps to one <see cref="Streaming.ICliTurnReader"/>.
/// </summary>
public enum CliOutputFormat
{
    /// <summary>Plain prose on stdout, possibly carrying ANSI colour codes.</summary>
    Text = 0,

    /// <summary>One JSON object per line, Anthropic <c>stream-json</c> shape.</summary>
    AnthropicStreamJson,

    /// <summary>One JSON event per line, Codex <c>exec --json</c> shape.</summary>
    CodexJsonLines,

    /// <summary>A single JSON object printed once the turn completes.</summary>
    SingleJsonObject,
}

/// <summary>
/// Everything CPT needs to know to install one coding CLI.
/// </summary>
public sealed class CliInstallSpec
{
    /// <summary>npm package installed with <c>npm install -g</c>. Primary channel for all four CLIs.</summary>
    public string? NpmPackage { get; init; }

    /// <summary>Optional winget id, tried when npm is unavailable.</summary>
    public string? WingetId { get; init; }

    /// <summary>Arguments that make the CLI print its version and exit 0.</summary>
    public IReadOnlyList<string> VersionArgs { get; init; } = ["--version"];

    /// <summary>Human-readable fallback shown when automatic installation is not possible.</summary>
    public string? ManualUrl { get; init; }
}

/// <summary>
/// How to run one non-interactive prompt turn and read the answer back.
/// </summary>
public sealed class CliRunSpec
{
    /// <summary>
    /// Argument template for a single head-less turn. The literal token
    /// <c>{prompt}</c> is replaced with the user's text; it is passed as its own
    /// argv entry, so no shell quoting is involved and no injection is possible.
    /// </summary>
    public IReadOnlyList<string> PromptArgs { get; init; } = [];

    /// <summary>
    /// Complete argument template used instead of <see cref="PromptArgs"/> when the
    /// turn should continue the previous conversation. It is a replacement rather
    /// than a suffix because providers place their resume verb in different
    /// positions. Empty when the provider has no resume support, in which case
    /// every turn starts a fresh conversation.
    /// </summary>
    public IReadOnlyList<string> ContinueArgs { get; init; } = [];

    /// <summary>
    /// What to pass in place of <c>{prompt}</c> when the prompt is piped to
    /// stdin instead of being an argument.
    ///
    /// Most CLIs read stdin as soon as no prompt argument is present, so this is
    /// empty for them. Codex only does that for <c>exec</c>: its <c>exec resume</c>
    /// takes <c>[SESSION_ID] [PROMPT]</c> positionally and reads stdin only for
    /// the literal <c>-</c>, so without this every resumed turn arrived empty.
    /// </summary>
    public string? StdinPromptToken { get; init; }

    /// <summary>Shape of the data the CLI writes to stdout.</summary>
    public CliOutputFormat OutputFormat { get; init; } = CliOutputFormat.Text;

    /// <summary>Seconds to wait for one turn before giving up.</summary>
    public int TurnTimeoutSeconds { get; init; } = 300;
}

/// <summary>
/// How to detect, and how to obtain, a signed-in state.
/// </summary>
public sealed class CliAuthSpec
{
    /// <summary>
    /// Command run inside a pseudo-terminal to start the interactive sign-in.
    /// Empty means "just launch the CLI and let its first-run flow take over".
    /// </summary>
    public IReadOnlyList<string> LoginArgs { get; init; } = [];

    /// <summary>Optional cheap command whose exit code / output reports auth state.</summary>
    public IReadOnlyList<string> StatusArgs { get; init; } = [];

    /// <summary>Substrings in the status output that prove the user is signed in.</summary>
    public IReadOnlyList<string> SignedInMarkers { get; init; } = [];

    /// <summary>Substrings in any output that prove the user is NOT signed in.</summary>
    public IReadOnlyList<string> SignedOutMarkers { get; init; } =
        ["not logged in", "not authenticated", "please log in", "please sign in", "/login", "unauthorized"];

    /// <summary>
    /// Credential files, relative to the user profile directory. Existence of any
    /// one of them is treated as evidence of a completed sign-in. This is the
    /// signal the sign-in window polls, so it can close itself automatically.
    /// </summary>
    public IReadOnlyList<string> CredentialPaths { get; init; } = [];

    /// <summary>Environment variable that supplies a key instead of an interactive login.</summary>
    public string? ApiKeyEnvVar { get; init; }
}

/// <summary>
/// A coding CLI that CPT can install, authenticate and drive.
/// Instances are immutable and come either from <see cref="CliProviderCatalog"/>
/// or from a user-supplied JSON override, so a CLI that changes its flags can be
/// corrected without shipping a new build.
/// </summary>
public sealed class CliProvider
{
    /// <summary>Stable machine id, e.g. <c>claude-code</c>. Used in settings and IPC.</summary>
    public required string Id { get; init; }

    /// <summary>Name shown in the UI, e.g. <c>Claude Code</c>.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Vendor shown under the name, e.g. <c>Anthropic</c>.</summary>
    public required string Vendor { get; init; }

    /// <summary>Executable name as it appears on PATH, without extension.</summary>
    public required string Command { get; init; }

    public CliInstallSpec Install { get; init; } = new();
    public CliRunSpec Run { get; init; } = new();
    public CliAuthSpec Auth { get; init; } = new();

    /// <summary>
    /// Per-turn settings this CLI exposes -- model, effort, permissions. Rendered
    /// as pickers in the settings window and applied at the <c>{options}</c>
    /// position in the argument template.
    /// </summary>
    public IReadOnlyList<CliOption> Options { get; init; } = [];

    /// <summary>Ordering hint for the provider picker.</summary>
    public int SortOrder { get; init; }

    [JsonIgnore]
    public bool CanAutoInstall => Install.NpmPackage is not null || Install.WingetId is not null;

    public override string ToString() => $"{DisplayName} ({Id})";
}
