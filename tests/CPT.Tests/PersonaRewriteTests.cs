using CPT.Core.Llm;
using Xunit;

namespace CPT.Tests;

/// <summary>
/// The guard that stops the persona model answering the agent instead of
/// repeating it. The reported symptom was the app confidently saying something
/// in character that had nothing to do with the question asked.
/// </summary>
public class PersonaRewriteTests
{
    private const string Answer =
        "The build succeeded. 143 tests passed and the settings window opens from PersonaWindow.xaml.";

    [Fact]
    public void A_genuine_rewrite_is_accepted()
    {
        const string rewrite =
            "Compilation complete. All 143 tests passed, and the settings window opens from PersonaWindow.xaml.";

        Assert.True(PersonaRewrite.KeepsSubstance(Answer, rewrite));
    }

    [Fact]
    public void An_in_character_reply_that_answers_instead_of_repeating_is_rejected()
    {
        const string reply =
            "Working. Please specify the parameters of your query, and I will access the relevant records.";

        Assert.False(PersonaRewrite.KeepsSubstance(Answer, reply));
    }

    [Fact]
    public void An_empty_rewrite_is_rejected()
    {
        Assert.False(PersonaRewrite.KeepsSubstance(Answer, "   "));
    }

    [Fact]
    public void A_rewrite_that_drops_most_of_the_answer_is_rejected()
    {
        Assert.False(PersonaRewrite.KeepsSubstance(Answer, "Affirmative."));
    }

    /// <summary>
    /// A two-word answer has no vocabulary to overlap, so demanding overlap
    /// would reject every valid rewrite of it.
    /// </summary>
    [Fact]
    public void A_very_short_answer_is_judged_on_length_alone()
    {
        Assert.True(PersonaRewrite.KeepsSubstance("Done.", "It is done."));
        Assert.False(PersonaRewrite.KeepsSubstance("Done.", ""));
    }

    [Fact]
    public void Nothing_to_rewrite_means_nothing_to_reject()
    {
        Assert.True(PersonaRewrite.KeepsSubstance("", "anything at all"));
    }
}
