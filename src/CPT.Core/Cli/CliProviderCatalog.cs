using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CPT.Core.Diagnostics;

namespace CPT.Core.Cli;

/// <summary>
/// The set of coding CLIs CPT knows how to drive.
///
/// Definitions are built in, but every one of them can be replaced at runtime by
/// dropping "&lt;id&gt;.json" into <see cref="OverrideDirectory"/>. Coding CLIs rename
/// their flags fairly often; an override file lets a user repair that without
/// waiting for a new CPT release.
/// </summary>
public static class CliProviderCatalog
{
    /// <summary>%LOCALAPPDATA%\CustomPersonaTranslator\cli-providers</summary>
    public static string OverrideDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CustomPersonaTranslator", "cli-providers");

    public const string ClaudeCodeId = "claude-code";
    public const string GeminiId = "gemini-cli";
    public const string CodexId = "codex-cli";
    public const string CopilotId = "copilot-cli";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    private static readonly IReadOnlyList<CliProvider> BuiltIn =
    [
        new CliProvider
        {
            Id = ClaudeCodeId,
            DisplayName = "Claude Code",
            Vendor = "Anthropic",
            Command = "claude",
            SortOrder = 0,
            Install = new CliInstallSpec
            {
                NpmPackage = "@anthropic-ai/claude-code",
                ManualUrl = "https://docs.claude.com/en/docs/claude-code/setup",
            },
            Run = new CliRunSpec
            {
                // --verbose is required for stream-json to emit per-message events.
                PromptArgs = ["-p", "{prompt}", "--output-format", "stream-json", "--verbose", "{options}"],
                ContinueArgs =
                    ["-p", "{prompt}", "--continue", "--output-format", "stream-json", "--verbose", "{options}"],
                OutputFormat = CliOutputFormat.AnthropicStreamJson,
            },
            Auth = new CliAuthSpec
            {
                // A bare invocation runs the first-run login flow.
                LoginArgs = [],
                CredentialPaths = [".claude/.credentials.json", ".claude.json"],
                ApiKeyEnvVar = "ANTHROPIC_API_KEY",
            },
            Options =
            [
                Model(
                    Choice("default", "Default"),
                    Choice("opus", "Opus", "--model", "opus"),
                    Choice("sonnet", "Sonnet", "--model", "sonnet"),
                    Choice("haiku", "Haiku", "--model", "haiku")),
                new CliOption
                {
                    Id = "effort",
                    Label = "Thinking effort",
                    Hint = "More effort means slower, more careful answers.",
                    DefaultChoiceId = "default",
                    Choices =
                    [
                        Choice("default", "Default"),
                        Choice("low", "Low", "--effort", "low"),
                        Choice("medium", "Medium", "--effort", "medium"),
                        Choice("high", "High", "--effort", "high"),
                        Choice("xhigh", "Very high", "--effort", "xhigh"),
                        Choice("max", "Maximum", "--effort", "max"),
                    ],
                },
                Permissions(
                    Choice("default", "Ask first"),
                    Choice("acceptEdits", "Auto-accept edits", "--permission-mode", "acceptEdits"),
                    Choice("plan", "Plan only", "--permission-mode", "plan"),
                    Choice("bypassPermissions", "Full access", "--permission-mode", "bypassPermissions")),
            ],
        },
        new CliProvider
        {
            Id = GeminiId,
            DisplayName = "Gemini CLI",
            Vendor = "Google",
            Command = "gemini",
            SortOrder = 1,
            Install = new CliInstallSpec
            {
                NpmPackage = "@google/gemini-cli",
                ManualUrl = "https://github.com/google-gemini/gemini-cli",
            },
            Run = new CliRunSpec
            {
                PromptArgs = ["-p", "{prompt}", "{options}"],
                OutputFormat = CliOutputFormat.Text,
            },
            Auth = new CliAuthSpec
            {
                // A bare invocation opens the browser consent flow.
                LoginArgs = [],
                CredentialPaths = [".gemini/oauth_creds.json", ".gemini/settings.json"],
                ApiKeyEnvVar = "GEMINI_API_KEY",
            },
            Options =
            [
                Model(
                    Choice("default", "Default"),
                    Choice("pro", "Gemini 2.5 Pro", "--model", "gemini-2.5-pro"),
                    Choice("flash", "Gemini 2.5 Flash", "--model", "gemini-2.5-flash")),
                Permissions(
                    Choice("default", "Ask first"),
                    Choice("auto_edit", "Auto-accept edits", "--approval-mode", "auto_edit"),
                    Choice("yolo", "Full access", "--approval-mode", "yolo")),
            ],
        },
        new CliProvider
        {
            Id = CodexId,
            DisplayName = "Codex CLI",
            Vendor = "OpenAI",
            Command = "codex",
            SortOrder = 2,
            Install = new CliInstallSpec
            {
                NpmPackage = "@openai/codex",
                ManualUrl = "https://github.com/openai/codex",
            },
            Run = new CliRunSpec
            {
                // "exec" is the non-interactive mode; --skip-git-repo-check keeps
                // it usable when CPT's working directory is not a repository.
                PromptArgs = ["exec", "--json", "--skip-git-repo-check", "{options}", "{prompt}"],
                ContinueArgs = ["exec", "resume", "--last", "--json", "--skip-git-repo-check", "{options}", "{prompt}"],
                OutputFormat = CliOutputFormat.CodexJsonLines,
            },
            Auth = new CliAuthSpec
            {
                LoginArgs = ["login"],
                StatusArgs = ["login", "status"],
                SignedInMarkers = ["logged in", "authenticated"],
                CredentialPaths = [".codex/auth.json"],
                ApiKeyEnvVar = "OPENAI_API_KEY",
            },
            Options =
            [
                Model(
                    Choice("default", "Default"),
                    Choice("gpt-5-codex", "GPT-5 Codex", "-m", "gpt-5-codex"),
                    Choice("gpt-5", "GPT-5", "-m", "gpt-5")),
                new CliOption
                {
                    Id = "effort",
                    Label = "Reasoning effort",
                    Hint = "More effort means slower, more careful answers.",
                    DefaultChoiceId = "default",
                    Choices =
                    [
                        Choice("default", "Default"),
                        Choice("low", "Low", "-c", "model_reasoning_effort=\"low\""),
                        Choice("medium", "Medium", "-c", "model_reasoning_effort=\"medium\""),
                        Choice("high", "High", "-c", "model_reasoning_effort=\"high\""),
                    ],
                },
                Permissions(
                    Choice("default", "Ask first"),
                    Choice("read-only", "Read only", "-s", "read-only"),
                    Choice("workspace-write", "Write in workspace", "-s", "workspace-write"),
                    Choice("danger-full-access", "Full access", "-s", "danger-full-access")),
            ],
        },
        new CliProvider
        {
            Id = CopilotId,
            DisplayName = "GitHub Copilot CLI",
            Vendor = "GitHub",
            Command = "copilot",
            SortOrder = 3,
            Install = new CliInstallSpec
            {
                NpmPackage = "@github/copilot",
                ManualUrl = "https://docs.github.com/en/copilot/concepts/agents/about-copilot-cli",
            },
            Run = new CliRunSpec
            {
                PromptArgs = ["-p", "{prompt}", "{options}"],
                OutputFormat = CliOutputFormat.Text,
            },
            Auth = new CliAuthSpec
            {
                // A bare invocation drops into the TUI, where /login runs the flow.
                LoginArgs = [],
                CredentialPaths = [".copilot/config.json", ".config/gh/hosts.yml"],
                ApiKeyEnvVar = "GITHUB_TOKEN",
            },
            Options =
            [
                Model(
                    Choice("default", "Default"),
                    Choice("claude-sonnet-4.5", "Claude Sonnet 4.5", "--model", "claude-sonnet-4.5"),
                    Choice("gpt-5", "GPT-5", "--model", "gpt-5")),
                Permissions(
                    Choice("default", "Ask first"),
                    Choice("allow-all", "Full access", "--allow-all-tools")),
            ],
        },
    ];

    // --- shorthand used by the definitions above --------------------------

    private static CliOptionChoice Choice(string id, string label, params string[] args) =>
        new() { Id = id, Label = label, Args = args };

    private static CliOption Model(params CliOptionChoice[] choices) => new()
    {
        Id = "model",
        Label = "Model",
        DefaultChoiceId = "default",
        Choices = choices,
    };

    private static CliOption Permissions(params CliOptionChoice[] choices) => new()
    {
        Id = "permissions",
        Label = "What it may do on its own",
        Hint = "Full access lets it edit files and run commands without asking.",
        DefaultChoiceId = "default",
        Choices = choices,
    };

    /// <summary>
    /// Every known provider, sorted for display, with any on-disk override applied.
    /// </summary>
    public static IReadOnlyList<CliProvider> All()
    {
        var overrides = LoadOverrides();
        return BuiltIn
            .Select(p => overrides.TryGetValue(p.Id, out var o) ? o : p)
            .Concat(overrides.Values.Where(o => !BuiltIn.Any(b => b.Id == o.Id)))
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The provider with this id, or null when it is unknown.</summary>
    public static CliProvider? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : All().FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The provider with this id, falling back to the first known one.</summary>
    public static CliProvider FindOrDefault(string? id) => Find(id) ?? All()[0];

    /// <summary>
    /// Writes the built-in definition into <see cref="OverrideDirectory"/> so the
    /// user has a correct starting point to edit. Returns the file path.
    /// </summary>
    public static string ExportTemplate(string providerId)
    {
        var provider = BuiltIn.FirstOrDefault(p => p.Id == providerId)
            ?? throw new ArgumentException("Unknown provider id: " + providerId, nameof(providerId));
        Directory.CreateDirectory(OverrideDirectory);
        var path = Path.Combine(OverrideDirectory, providerId + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(provider, JsonOptions));
        return path;
    }

    private static Dictionary<string, CliProvider> LoadOverrides()
    {
        var result = new Dictionary<string, CliProvider>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(OverrideDirectory)) return result;

        foreach (var file in Directory.EnumerateFiles(OverrideDirectory, "*.json"))
        {
            try
            {
                var provider = JsonSerializer.Deserialize<CliProvider>(File.ReadAllText(file), JsonOptions);
                if (provider is not null && !string.IsNullOrWhiteSpace(provider.Id))
                    result[provider.Id] = provider;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken override must never take the app down: log it and keep
                // the built-in definition.
                CptLog.Write("[cli] ignoring bad provider override " + file + ": " + ex.Message);
            }
        }
        return result;
    }
}
