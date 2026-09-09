using CPT.Core.Voice;
using Xunit;

namespace CPT.Tests;

public class PhraseMatcherTests
{
    [Theory]
    [InlineData("hey agent what time is it", "hey agent")]
    [InlineData("Hey, Agent! What time is it?", "hey agent")]
    [InlineData("okay... HEY AGENT", "hey agent")]
    public void Finds_the_phrase_regardless_of_case_and_punctuation(string transcript, string phrase)
    {
        Assert.True(PhraseMatcher.Contains(transcript, phrase));
    }

    [Fact]
    public void Does_not_match_a_word_that_merely_starts_with_the_phrase()
    {
        // The whole point of matching whole words: "agentic" is not "agent".
        Assert.False(PhraseMatcher.Contains("hey agentic framework", "hey agent"));
    }

    [Fact]
    public void Does_not_match_when_the_words_are_not_adjacent()
    {
        Assert.False(PhraseMatcher.Contains("hey there agent", "hey agent"));
    }

    [Fact]
    public void TextAfter_returns_the_rest_of_the_utterance()
    {
        Assert.Equal("what changed in this file",
            PhraseMatcher.TextAfter("Hey agent, what changed in this file?", "hey agent"));
    }

    [Fact]
    public void TextAfter_returns_empty_when_the_phrase_ends_the_utterance()
    {
        Assert.Equal("", PhraseMatcher.TextAfter("okay hey agent", "hey agent"));
    }

    [Fact]
    public void TextAfter_returns_null_when_the_phrase_is_absent()
    {
        // Null and empty mean different things: absent versus present-with-nothing-after.
        Assert.Null(PhraseMatcher.TextAfter("what time is it", "hey agent"));
    }

    [Theory]
    [InlineData("stop", true)]
    [InlineData("Stop.", true)]
    [InlineData("  stop  ", true)]
    [InlineData("stop the build", false)]
    [InlineData("please stop", false)]
    [InlineData("", false)]
    public void IsExactly_matches_the_whole_utterance_and_nothing_less(string said, bool expected)
    {
        // What separates an interruption from a request: "stop" is one, "stop
        // the build" is the other.
        Assert.Equal(expected, PhraseMatcher.IsExactly(said, "stop"));
    }

    [Fact]
    public void Accents_are_normalised_away()
    {
        Assert.True(PhraseMatcher.Contains("héy àgent", "hey agent"));
    }

    [Fact]
    public void An_empty_phrase_never_matches()
    {
        Assert.False(PhraseMatcher.Contains("anything at all", ""));
    }

    [Fact]
    public void Digits_are_kept_so_numbered_phrases_work()
    {
        Assert.True(PhraseMatcher.Contains("wake up agent 2 please", "agent 2"));
    }
}
