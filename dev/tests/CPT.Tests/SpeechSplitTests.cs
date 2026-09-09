using System.Linq;
using CPT.Core.Pipeline;
using Xunit;

namespace CPT.Tests;

/// <summary>
/// How a verified rewrite is cut up for synthesis. Short fragments are what
/// made Chatterbox clip the last phoneme of a reply, so the splitter's job is
/// to never hand the engine a scrap.
/// </summary>
public class SpeechSplitTests
{
    [Fact]
    public void Long_unpunctuated_answer_starts_speaking_before_all_text_is_synthesized()
    {
        var text = string.Join(" ", Enumerable.Repeat("screen", 100));
        var parts = TranslationPipeline.SplitForSpeech(text);
        Assert.True(parts.Count > 1);
        Assert.InRange(parts[0].Length, 160, 167);
        Assert.Equal(text, string.Concat(parts));
    }

    [Fact]
    public void Every_piece_is_long_enough_to_synthesise_cleanly()
    {
        const string text =
            "The build succeeded on the first attempt. Yes. No. "
            + "One hundred and forty three tests passed without a single failure. Good.";

        var parts = TranslationPipeline.SplitForSpeech(text);

        Assert.All(parts, p => Assert.True(p.Trim().Length >= 60, $"too short: '{p}'"));
    }

    [Fact]
    public void Nothing_is_lost_or_reordered()
    {
        const string text =
            "First sentence, long enough to stand on its own as a piece of speech. "
            + "Second sentence, also long enough to be its own piece of speech here.";

        Assert.Equal(text, string.Concat(TranslationPipeline.SplitForSpeech(text)));
    }

    [Fact]
    public void A_short_answer_stays_in_one_piece()
    {
        var parts = TranslationPipeline.SplitForSpeech("Done.");

        Assert.Equal(["Done."], parts.ToArray());
    }
}
