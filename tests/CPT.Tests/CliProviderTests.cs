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

            // Every option must have a default that adds nothing, so a fresh
            // install behaves exactly like running the CLI by hand.
            foreach (var option in provider.Options)
                Assert.Empty(option.Resolve(null).Args);
        }
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
}
