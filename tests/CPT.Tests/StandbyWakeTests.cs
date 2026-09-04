using CPT.Core.Settings;
using CPT.Core.Voice;
using Xunit;

namespace CPT.Tests;

/// <summary>
/// Waking by name.
///
/// Requiring a general "hey agent" first and the agent's own phrase second was
/// two passwords for one door, and the bar told the user to say a phrase they
/// had not configured. Any of the phrases wakes it, and the machine reports
/// which one did so the right agent answers.
/// </summary>
public class StandbyWakeTests
{
    private static StandbyStateMachine Machine(params (string Phrase, string Owner)[] extra)
    {
        var machine = new StandbyStateMachine(new StandbySettings
        {
            WakePhrase = "hey agent",
            SendPhrase = "send it",
            CancelPhrase = "never mind",
        });
        machine.ExtraWakePhrases = extra;
        return machine;
    }

    [Fact]
    public void An_agents_own_phrase_wakes_it()
    {
        var machine = Machine(("hey computer", "a1"));

        var step = machine.Consume("hey computer what is the build status");

        Assert.Equal(StandbyOutcome.Woke, step.Outcome);
        Assert.Equal("a1", step.WokeBy);
        Assert.Equal("what is the build status", step.Captured);
    }

    [Fact]
    public void The_general_phrase_still_wakes_it_and_names_nobody()
    {
        var machine = Machine(("hey computer", "a1"));

        var step = machine.Consume("hey agent what is the build status");

        Assert.Equal(StandbyOutcome.Woke, step.Outcome);
        Assert.Null(step.WokeBy);
    }

    /// <summary>
    /// The addressee survives to the point the request is sent, which is when
    /// the host needs it to choose who answers.
    /// </summary>
    [Fact]
    public void The_agent_addressed_is_reported_when_the_request_is_sent()
    {
        var machine = Machine(("hey computer", "a1"));

        machine.Consume("hey computer");
        machine.Consume("why did the build fail");
        var sent = machine.Consume("send it");

        Assert.Equal(StandbyOutcome.Send, sent.Outcome);
        Assert.Equal("a1", sent.WokeBy);
        Assert.Equal("why did the build fail", sent.Request);
    }

    /// <summary>The most specific address wins, not merely the first configured.</summary>
    [Fact]
    public void The_longest_matching_phrase_wins()
    {
        var machine = Machine(("hey", "short"), ("hey computer", "long"));

        Assert.Equal("long", machine.Consume("hey computer run the tests").WokeBy);
    }

    [Fact]
    public void Nothing_wakes_it_when_no_phrase_is_spoken()
    {
        var machine = Machine(("hey computer", "a1"));

        Assert.Equal(StandbyOutcome.Ignored, machine.Consume("the computer is over there").Outcome);
    }

    [Fact]
    public void An_agent_with_no_phrase_contributes_nothing()
    {
        var machine = Machine(("", "a1"), ("   ", "a2"));

        Assert.Equal(StandbyOutcome.Ignored, machine.Consume("hello there").Outcome);
        Assert.Equal(StandbyOutcome.Woke, machine.Consume("hey agent hello").Outcome);
    }

    /// <summary>
    /// Taken verbatim from the log of a session that would not wake: the user
    /// said "hey computer" and recognition returned "A computer.".
    /// </summary>
    [Theory]
    [InlineData("A computer.")]
    [InlineData("Hey computer")]
    [InlineData("hey, computer!")]
    [InlineData("Computer")]
    public void A_mangled_interjection_still_wakes_the_agent(string heard)
    {
        var machine = Machine(("hey computer", "a1"));

        Assert.Equal(StandbyOutcome.Woke, machine.Consume(heard).Outcome);
    }

    [Fact]
    public void The_rest_of_the_sentence_survives_a_mangled_interjection()
    {
        var machine = Machine(("hey computer", "a1"));

        var step = machine.Consume("A computer, why did the build fail?");

        Assert.Equal("a1", step.WokeBy);
        Assert.Equal("why did the build fail", step.Captured);
    }

    /// <summary>
    /// Leniency at the FRONT only. The distinctive word appearing later in a
    /// sentence is someone talking about the agent, not to it.
    /// </summary>
    [Theory]
    [InlineData("ask the computer why it failed")]
    [InlineData("I told my computer to stop")]
    [InlineData("the build ran on that computer")]
    public void The_distinctive_word_alone_does_not_wake_it_mid_sentence(string heard)
    {
        var machine = Machine(("hey computer", "a1"));

        Assert.Equal(StandbyOutcome.Ignored, machine.Consume(heard).Outcome);
    }

    /// <summary>
    /// Only words that SOUND like the interjection are forgiven. "okay" does not
    /// sound like "hey", and forgiving everything short would wake the agent on
    /// half the sentences in a room.
    /// </summary>
    [Theory]
    [InlineData("my computer")]
    [InlineData("that computer")]
    public void A_word_that_sounds_nothing_like_the_interjection_is_not_forgiven(string heard)
    {
        var machine = Machine(("hey computer", "a1"));

        Assert.Equal(StandbyOutcome.Ignored, machine.Consume(heard).Outcome);
    }
}
