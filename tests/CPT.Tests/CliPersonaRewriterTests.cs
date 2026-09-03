using System;
using System.Reflection;
using CPT.Core.Llm;
using CPT.Core.Models;
using Xunit;

namespace CPT.Tests;

/// <summary>
/// The prompt that asks a CLI to restate an answer, and the tidying of what
/// comes back. Both matter because the failure they prevent is the app
/// confidently speaking something that is not the answer.
/// </summary>
public class CliPersonaRewriterTests
{
    private static string BuildPrompt(Persona persona, string text) =>
        (string)typeof(CliPersonaRewriter)
            .GetMethod("BuildPrompt", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [persona, text])!;

    private static string Unfence(string text) =>
        (string)typeof(CliPersonaRewriter)
            .GetMethod("Unfence", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [text])!;

    private static Persona Computer() => new()
    {
        Name = "Computer",
        SystemPrompt = "You are the ship's computer. Answer queries precisely.",
        FewShotQuotes = { "Working.", "Unable to comply." },
    };

    /// <summary>
    /// The task has to come after the persona, because a persona prompt written
    /// in the second person invites the model to answer rather than restate.
    /// </summary>
    [Fact]
    public void The_task_is_stated_after_the_persona()
    {
        var prompt = BuildPrompt(Computer(), "The build passed.");

        Assert.True(prompt.IndexOf("TASK.", StringComparison.Ordinal)
                  > prompt.IndexOf("ship's computer", StringComparison.Ordinal));
    }

    [Fact]
    public void The_answer_is_fenced_so_it_cannot_read_as_a_question_to_answer()
    {
        var prompt = BuildPrompt(Computer(), "Why did the build fail?");

        Assert.Contains("<<<ANSWER", prompt, StringComparison.Ordinal);
        Assert.Contains("ANSWER>>>", prompt, StringComparison.Ordinal);
        Assert.Contains("do not answer it", prompt, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A coding CLI will otherwise go and read the repository to "check" an
    /// answer it was only asked to rephrase.
    /// </summary>
    [Fact]
    public void The_rewriter_is_told_not_to_use_tools()
    {
        Assert.Contains("Use no tools", BuildPrompt(Computer(), "Done."), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Style_quotes_are_included_but_capped()
    {
        var persona = Computer();
        for (var i = 0; i < 20; i++) persona.FewShotQuotes.Add("quote " + i);

        var prompt = BuildPrompt(persona, "Done.");

        Assert.Contains("Working.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("quote 9", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("```\nAffirmative.\n```", "Affirmative.")]
    [InlineData("```text\nAffirmative.\n```", "Affirmative.")]
    [InlineData("Affirmative.", "Affirmative.")]
    public void A_code_fence_is_stripped_because_it_cannot_be_spoken(string reply, string expected)
    {
        Assert.Equal(expected, Unfence(reply));
    }
}
