using CPT.Core.Llm;
using Xunit;

namespace CPT.Tests;

/// <summary>
/// The persona guide travels inside the agent prompt, so it can come back inside
/// the answer. Observed: the agent read "Rewrite the user's text in the voice of
/// J.A.R.V.I.S., a sophisticated, unflappable British AI butler..." aloud to a
/// user who had asked it to do something else entirely.
/// </summary>
public class PersonaLeakTests
{
    private const string Break = "\n";

    private const string Guide =
        "Rewrite the user's text in the voice of J.A.R.V.I.S., a sophisticated, unflappable " +
        "British AI butler. Preserve the original meaning, facts, and intent. Use polished, " +
        "precise vocabulary, restrained warmth, and understated dry wit.";

    private const string RealAnswer =
        "The build is complete, sir. Two hundred and sixty tests passed.";

    [Fact]
    public void The_guide_is_not_read_aloud()
    {
        var spoken = PersonaRewrite.WithoutInstructions(Guide + Break + RealAnswer, Guide);

        Assert.DoesNotContain("unflappable British AI butler", spoken, System.StringComparison.Ordinal);
        Assert.Contains("Two hundred and sixty tests passed", spoken, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_answer_survives_untouched()
    {
        Assert.Equal(RealAnswer, PersonaRewrite.WithoutInstructions(RealAnswer, Guide));
    }

    [Fact]
    public void Voice_markers_are_never_spoken()
    {
        var answer = "<<<VOICE" + Break + "Complete, sir." + Break + "VOICE>>>";

        Assert.Equal("Complete, sir.", PersonaRewrite.WithoutInstructions(answer, Guide));
    }

    [Fact]
    public void A_short_line_that_happens_to_match_is_kept()
    {
        // Short overlaps are coincidence, not a leak, and dropping them would
        // silently delete real answers.
        Assert.Equal("Dry wit.", PersonaRewrite.WithoutInstructions("Dry wit.", Guide));
    }

    [Fact]
    public void An_answer_that_was_nothing_but_guide_leaves_nothing_to_say()
    {
        Assert.Equal("", PersonaRewrite.WithoutInstructions(Guide, Guide));
    }
}
