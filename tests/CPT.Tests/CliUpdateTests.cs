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
}
