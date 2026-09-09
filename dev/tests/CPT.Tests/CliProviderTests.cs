using System.Collections.Generic;
using System.Linq;
using CPT.Core.Cli;
using CPT.Core.Pty;
using Xunit;

namespace CPT.Tests;

public class CliProviderTests
{
    [Fact]
    public void The_catalog_ships_the_four_supported_clis()
    {
        var ids = CliProviderCatalog.All().Select(p => p.Id).ToList();

        Assert.Contains(CliProviderCatalog.ClaudeCodeId, ids);
        Assert.Contains(CliProviderCatalog.GeminiId, ids);
        Assert.Contains(CliProviderCatalog.CodexId, ids);
        Assert.Contains(CliProviderCatalog.CopilotId, ids);
    }

    [Fact]
    public void Every_provider_can_be_installed_and_knows_how_to_be_prompted()
    {
        foreach (var provider in CliProviderCatalog.All())
        {
            Assert.True(provider.CanAutoInstall, provider.Id + " has no install channel");
            Assert.NotEmpty(provider.Run.PromptArgs);
            Assert.NotEmpty(provider.Auth.CredentialPaths);
        }
    }

    [Fact]
    public void An_unknown_id_falls_back_to_the_first_provider()
    {
        Assert.Null(CliProviderCatalog.Find("no-such-cli"));
        Assert.NotNull(CliProviderCatalog.FindOrDefault("no-such-cli"));
    }

    [Fact]
    public void Provider_lookup_ignores_case()
    {
        Assert.NotNull(CliProviderCatalog.Find("CLAUDE-CODE"));
    }
}

public class CliAgentTests
{
    private static CliProvider ProviderWith(params string[] promptArgs) => new()
    {
        Id = "test",
        DisplayName = "Test",
        Vendor = "Test",
        Command = "test",
        Run = new CliRunSpec { PromptArgs = promptArgs },
    };

    [Fact]
    public void The_prompt_replaces_its_placeholder_as_a_single_argument()
    {
        var agent = new CliAgent(ProviderWith("-p", "{prompt}", "--json"));

        var arguments = agent.BuildArguments("what does this do");

        Assert.Equal(["-p", "what does this do", "--json"], arguments);
    }

    [Fact]
    public void A_prompt_that_looks_like_an_option_stays_one_argument()
    {
        // Passing arguments as a list rather than a command line is what makes
        // this safe: nothing in the user's speech can become a flag.
        var agent = new CliAgent(ProviderWith("-p", "{prompt}"));

        var arguments = agent.BuildArguments("--dangerously-skip-permissions && rm -rf /");

        Assert.Equal(2, arguments.Count);
        Assert.Equal("--dangerously-skip-permissions && rm -rf /", arguments[1]);
    }

    private static CliProvider WithOptions(params string[] promptArgs) => new()
    {
        Id = "test",
        DisplayName = "Test",
        Vendor = "Test",
        Command = "test",
        Run = new CliRunSpec { PromptArgs = promptArgs },
        Options =
        [
            new CliOption
            {
                Id = "model",
                Label = "Model",
                DefaultChoiceId = "default",
                Choices =
                [
                    new CliOptionChoice { Id = "default", Label = "Default" },
                    new CliOptionChoice { Id = "fast", Label = "Fast", Args = ["--model", "fast"] },
                ],
            },
        ],
    };

    [Fact]
    public void Selected_options_land_where_the_template_puts_them()
    {
        var agent = new CliAgent(WithOptions("exec", "{options}", "{prompt}"))
        {
            Options = new Dictionary<string, string> { ["model"] = "fast" },
        };

        Assert.Equal(["exec", "--model", "fast", "hello"], agent.BuildArguments("hello"));
    }

    [Fact]
    public void The_default_choice_contributes_nothing()
    {
        var agent = new CliAgent(WithOptions("exec", "{options}", "{prompt}"));

        Assert.Equal(["exec", "hello"], agent.BuildArguments("hello"));
    }

    [Fact]
    public void A_stale_choice_id_falls_back_to_the_default_rather_than_breaking_the_command()
    {
        var agent = new CliAgent(WithOptions("exec", "{options}", "{prompt}"))
        {
            Options = new Dictionary<string, string> { ["model"] = "a-model-that-was-removed" },
        };

        Assert.Equal(["exec", "hello"], agent.BuildArguments("hello"));
    }

    [Fact]
    public void Options_are_appended_when_the_template_has_no_options_placeholder()
    {
        var agent = new CliAgent(WithOptions("-p", "{prompt}"))
        {
            Options = new Dictionary<string, string> { ["model"] = "fast" },
        };

        Assert.Equal(["-p", "hello", "--model", "fast"], agent.BuildArguments("hello"));
    }

    [Fact]
    public void Every_shipped_provider_declares_a_model_and_a_permission_option()
    {
        foreach (var provider in CliProviderCatalog.All())
        {
            Assert.Contains(provider.Options, o => o.Id == "model");
            Assert.Contains(provider.Options, o => o.Id == "permissions");

            // Every option's default must add nothing, so a fresh install
            // behaves exactly like running the CLI by hand -- with one listed
            // exception, so that deviating from the user's own configuration is
            // always a deliberate, visible decision rather than a habit.
            foreach (var option in provider.Options)
            {
                if (DeliberateDefaults.Contains((provider.Id, option.Id))) continue;
                Assert.Empty(option.Resolve(null).Args);
            }
        }
    }

    /// <summary>
    /// Defaults that intentionally differ from the CLI's own behaviour.
    ///
    /// Codex loads the MCP servers in the user's config on every invocation and
    /// retries the ones it cannot reach. Measured on a machine with three
    /// configured and two of them dead, against the same trivial prompt:
    /// 58.7 s with them, 6.4 s without. A voice assistant cannot spend a minute
    /// connecting to a Unity endpoint that is not running, so CPT starts with
    /// them off and the user can turn them back on.
    /// </summary>
    private static readonly HashSet<(string Provider, string Option)> DeliberateDefaults =
        [(CliProviderCatalog.CodexId, "extensions")];

    [Fact]
    public void The_only_default_that_overrides_the_users_config_is_the_declared_one()
    {
        var overriding = new List<(string, string)>();

        foreach (var provider in CliProviderCatalog.All())
        foreach (var option in provider.Options)
        {
            if (option.Resolve(null).Args.Count > 0) overriding.Add((provider.Id, option.Id));
        }

        Assert.Equal(DeliberateDefaults.OrderBy(x => x.Provider).ToList(),
                     overriding.OrderBy(x => x.Item1).ToList());
    }

    [Fact]
    public void A_template_without_a_placeholder_still_carries_the_prompt()
    {
        // A hand-edited provider override could omit it; dropping the user's
        // words silently would be the worst possible response.
        var agent = new CliAgent(ProviderWith("exec"));

        Assert.Equal(["exec", "hello"], agent.BuildArguments("hello"));
    }
}

public class CommandLineQuotingTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("has space", "\"has space\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\path with space\", "\"C:\\path with space\\\\\"")]
    public void Arguments_are_quoted_the_way_CommandLineToArgvW_expects(string argument, string expected)
    {
        Assert.Equal(expected, PtySession.Quote(argument));
    }

    [Fact]
    public void Codex_offers_the_astra_model()
    {
        // Verified against codex-cli 0.153.3 on this machine: the id is
        // "gpt-6-astra", and it is what the user has in ~/.codex/config.toml.
        var codex = CliProviderCatalog.All().Single(p => p.Id == CliProviderCatalog.CodexId);
        var model = codex.Options.Single(o => o.Id == "model");
        var astra = model.Choices.Single(c => c.Id == "gpt-6-astra");

        Assert.Equal(["-m", "gpt-6-astra"], astra.Args);
    }

    [Fact]
    public void Codex_offers_the_extra_high_reasoning_effort()
    {
        var codex = CliProviderCatalog.All().Single(p => p.Id == CliProviderCatalog.CodexId);
        var effort = codex.Options.Single(o => o.Id == "effort");
        var xhigh = effort.Choices.Single(c => c.Id == "xhigh");

        // Codex takes reasoning effort as a config override, not a flag.
        Assert.Equal(["-c", "model_reasoning_effort=\"xhigh\""], xhigh.Args);
    }
}

/// <summary>
/// The second turn of a conversation, which is where every agent was failing.
///
/// A multi-line prompt is piped, and from the second turn on the resume template
/// is used; codex accepts a different set of flags there than it does for a first
/// turn, and reads a piped prompt only when told to. Both facts are checked here
/// against the real catalog entry, because both were wrong at once and the
/// symptom -- "Codex CLI exited with code 2" -- named neither.
/// </summary>
public class CodexResumeTests
{
    private static CliProvider Codex =>
        CliProviderCatalog.All().Single(p => p.Id == CliProviderCatalog.CodexId);

    private static IReadOnlyList<string> ResumeArguments(string permissions)
    {
        var agent = new CliAgent(Codex)
        {
            SessionId = "cpt-owned-session",
            Options = new Dictionary<string, string>
            {
                ["model"] = "gpt-6-astra",
                ["effort"] = "low",
                ["permissions"] = permissions,
            },
        };

        // Every persona prompt is multi-line, so every real turn is piped.
        return agent.BuildArguments("why did the build fail?\nAnswer in one line.", resuming: true);
    }

    [Theory]
    [InlineData("read-only")]
    [InlineData("workspace-write")]
    [InlineData("danger-full-access")]
    public void Resuming_never_passes_the_sandbox_flag_that_resume_rejects(string permissions)
    {
        // "codex exec resume" has no -s/--sandbox: it answers "unexpected
        // argument '-s' found" and exits 2, so the turn produced no reply at all.
        Assert.DoesNotContain("-s", ResumeArguments(permissions));
        Assert.Contains($"sandbox_mode=\"{permissions}\"", ResumeArguments(permissions));
    }

    [Fact]
    public void A_piped_prompt_is_asked_for_by_name_when_resuming()
    {
        var arguments = ResumeArguments("danger-full-access");

        // Without the "-", resume takes its prompt positionally and gets none.
        Assert.Equal(["exec", "resume", "cpt-owned-session"], arguments.Take(3));
        Assert.Equal("-", arguments[^1]);
    }

    [Fact]
    public void Every_resume_flag_is_one_the_resume_subcommand_accepts()
    {
        // Verified against codex-cli 0.153.4: "codex exec resume --help" lists
        // -c, -m, -i, --last, --all, --json, --skip-git-repo-check,
        // --ignore-user-config and the bypass flags -- and nothing else.
        string[] accepted =
            ["exec", "resume", "--last", "--all", "--json", "--skip-git-repo-check",
             "--ignore-user-config", "--ignore-rules", "--ephemeral", "-c", "-m", "-i", "-o", "-"];

        foreach (var permissions in new[] { "default", "read-only", "workspace-write", "danger-full-access" })
        foreach (var argument in ResumeArguments(permissions))
        {
            if (!argument.StartsWith('-') || argument == "-") continue;
            Assert.Contains(argument, accepted);
        }
    }

    [Fact]
    public void A_provider_with_no_stdin_token_still_just_omits_the_prompt()
    {
        // Claude and Gemini read a piped prompt on their own; adding a "-" would
        // become a prompt that says "-".
        var claude = CliProviderCatalog.All().Single(p => p.Id == CliProviderCatalog.ClaudeCodeId);
        var arguments = new CliAgent(claude).BuildArguments("line one\nline two", resuming: false);

        Assert.DoesNotContain("-", arguments);
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose"], arguments);
    }
}
