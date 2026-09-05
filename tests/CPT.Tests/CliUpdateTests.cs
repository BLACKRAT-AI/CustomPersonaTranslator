using CPT.Core.Cli;
using Xunit;

namespace CPT.Tests;

/// <summary>
/// Recognising a CLI that is too old to run.
///
/// The app could install a missing CLI but had nothing for an installed one
/// that refused to run, so a turn just failed with the vendor's own wording and
/// no way to act on it.
/// </summary>
public class CliUpdateTests
{
    [Theory]
    [InlineData("Your version of Claude Code is out of date. Please update to continue.")]
    [InlineData("This version is no longer supported.")]
    [InlineData("Update required before you can run this command.")]
    [InlineData("Unsupported version. Please upgrade.")]
    [InlineData("codex: your version is too old")]
    // The real one, from Claude Code 2.1.220 refusing a model. None of the
    // obvious phrases appear in it, which is why the first check never fired.
    [InlineData("API Error: 400 Claude Code 2.1.220 does not support this model; "
              + "version 2.1.251 or newer is required. Run 'claude update', or update "
              + "the Claude desktop app, then try again.")]
    public void A_vendors_out_of_date_message_is_recognised(string failure)
    {
        Assert.True(CliInstaller.LooksOutOfDate(failure));
    }

    [Theory]
    [InlineData("Not signed in. Run `claude login`.")]
    [InlineData("Error: ENOENT no such file or directory")]
    [InlineData("The model returned an empty response.")]
    [InlineData("")]
    [InlineData(null)]
    public void An_ordinary_failure_does_not_trigger_an_update(string? failure)
    {
        Assert.False(CliInstaller.LooksOutOfDate(failure));
    }

    /// <summary>
    /// "up to date" is the SUCCESS message and must never be read as a failure
    /// worth reinstalling over.
    /// </summary>
    [Fact]
    public void Up_to_date_is_not_out_of_date()
    {
        Assert.False(CliInstaller.LooksOutOfDate("Everything is up to date."));
    }
    [Theory]
    // The real failure from the log: a turn died and the agent said nothing.
    [InlineData("error 400: the gpt-6-astra model requires a newer version of Codex", "newer version")]
    [InlineData("npm error EBUSY: resource busy or locked, copyfile", "could not be updated")]
    [InlineData("There is not enough space on the disk.", "disk space")]
    [InlineData("HTTP 429 rate limit exceeded", "rate limited")]
    public void A_failure_is_explained_in_words_a_person_can_act_on(string failure, string expected)
    {
        Assert.Contains(expected, CliInstaller.Explain(failure), System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_turn_with_no_failure_still_says_something()
    {
        Assert.False(string.IsNullOrWhiteSpace(CliInstaller.Explain(null)));
        Assert.False(string.IsNullOrWhiteSpace(CliInstaller.Explain("")));
    }
}
