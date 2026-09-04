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
}
