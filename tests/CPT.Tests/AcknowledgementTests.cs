using CPT.Core.Tts;
using Xunit;

namespace CPT.Tests;

/// <summary>
/// What the agent says while it starts work.
///
/// "Working on it" every time tells the user nothing and sounds like something
/// that did not listen, so the line is chosen from what was actually asked.
/// </summary>
public class AcknowledgementTests
{
    [Theory]
    [InlineData("why did the build fail", "Checking the build.")]
    [InlineData("run the tests again", "Running through the tests.")]
    [InlineData("what is in this file", "Looking at the files.")]
    [InlineData("show me the last commit", "Checking the repository.")]
    [InlineData("read the log", "Reading the log.")]
    [InlineData("add a method to the parser", "Making the change.")]
    public void The_line_follows_what_was_asked(string request, string expected)
    {
        Assert.Equal(expected, AcknowledgementCache.LineFor(request));
    }

    /// <summary>
    /// Anything that matches nothing in particular still gets an answer rather
    /// than silence.
    /// </summary>
    [Fact]
    public void An_unclassifiable_request_still_gets_a_line()
    {
        Assert.False(string.IsNullOrWhiteSpace(AcknowledgementCache.LineFor("tell me a joke about penguins")));
    }

    [Fact]
    public void Nothing_at_all_still_gets_a_line()
    {
        Assert.False(string.IsNullOrWhiteSpace(AcknowledgementCache.LineFor("")));
    }

    /// <summary>
    /// Every line the chooser can return must be one that actually gets
    /// rendered, or the agent would pick a line it has no audio for.
    /// </summary>
    [Theory]
    [InlineData("the build broke")]
    [InlineData("run tests")]
    [InlineData("open that file")]
    [InlineData("git status")]
    [InlineData("something else entirely")]
    public void Every_line_offered_is_one_that_gets_rendered(string request)
    {
        Assert.Contains(AcknowledgementCache.LineFor(request), AcknowledgementCache.Lines);
    }

    [Theory]
    // The request that exposed this: it matched nothing, so a specific
    // instruction was answered with "working on it" and the user could not
    // tell whether it had understood a word of it.
    [InlineData("open my onshape form tab", "Opening that now.")]
    [InlineData("launch chrome", "Opening that now.")]
    [InlineData("check my email", "Checking your messages.")]
    [InlineData("what is on my calendar", "Checking the calendar.")]
    [InlineData("deploy to production", "Starting the deployment.")]
    [InlineData("install the npm package", "Installing that now.")]
    [InlineData("delete that folder", "Removing that now.")]
    [InlineData("why did the build fail", "Checking the build.")]
    [InlineData("run the tests", "Running through the tests.")]
    public void The_acknowledgement_reflects_what_was_asked(string request, string expected)
    {
        Assert.Equal(expected, AcknowledgementCache.LineFor(request));
    }

    [Fact]
    public void Every_situation_has_a_line_that_can_be_synthesised()
    {
        // Under four words the voice clone mangles the line -- it comes back
        // with the first sound missing, or as silence.
        foreach (var line in AcknowledgementCache.Lines)
            Assert.True(line.Split(' ').Length >= 3, line + " is too short to synthesise");
    }
}
