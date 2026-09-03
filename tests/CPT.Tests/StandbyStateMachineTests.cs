using CPT.Core.Settings;
using CPT.Core.Voice;
using Xunit;

namespace CPT.Tests;

public class StandbyStateMachineTests
{
    private static StandbyStateMachine Create() => new(new StandbySettings
    {
        WakePhrase = "hey agent",
        SendPhrase = "send it",
        CancelPhrase = "never mind",
    });

    [Fact]
    public void Ignores_speech_before_the_wake_phrase()
    {
        var machine = Create();

        var step = machine.Consume("so anyway I told him the build was broken");

        Assert.Equal(StandbyOutcome.Ignored, step.Outcome);
        Assert.Equal(StandbyState.Sleeping, machine.State);
    }

    [Fact]
    public void Wakes_on_the_wake_phrase()
    {
        var machine = Create();

        var step = machine.Consume("hey agent");

        Assert.Equal(StandbyOutcome.Woke, step.Outcome);
        Assert.Equal(StandbyState.Listening, machine.State);
    }

    [Fact]
    public void Keeps_the_request_spoken_in_the_same_breath_as_the_wake_phrase()
    {
        var machine = Create();

        var step = machine.Consume("hey agent, summarise the last commit");

        Assert.Equal(StandbyOutcome.Woke, step.Outcome);
        Assert.Equal("summarise the last commit", step.Captured);
    }

    [Fact]
    public void Accumulates_dictation_across_several_utterances()
    {
        var machine = Create();
        machine.Consume("hey agent");

        machine.Consume("open the settings file");
        var step = machine.Consume("and add a timeout");

        Assert.Equal(StandbyOutcome.Captured, step.Outcome);
        Assert.Equal("open the settings file and add a timeout", step.Captured);
    }

    [Fact]
    public void Sends_on_the_send_phrase_and_strips_it_from_the_request()
    {
        var machine = Create();
        machine.Consume("hey agent");
        machine.Consume("run the tests");

        var step = machine.Consume("send it");

        Assert.Equal(StandbyOutcome.Send, step.Outcome);
        Assert.Equal("run the tests", step.Request);
        Assert.Equal(StandbyState.Sleeping, machine.State);
    }

    [Fact]
    public void Handles_a_whole_request_and_send_phrase_in_one_utterance()
    {
        var machine = Create();

        var step = machine.Consume("hey agent what does this function do send it");

        Assert.Equal(StandbyOutcome.Send, step.Outcome);
        Assert.Equal("what does this function do", step.Request);
    }

    [Fact]
    public void Cancel_phrase_discards_the_request()
    {
        var machine = Create();
        machine.Consume("hey agent");
        machine.Consume("delete everything");

        var step = machine.Consume("actually never mind");

        Assert.Equal(StandbyOutcome.Cancelled, step.Outcome);
        Assert.Null(step.Request);
        Assert.Equal(StandbyState.Sleeping, machine.State);
    }

    [Fact]
    public void Send_phrase_with_nothing_captured_cancels_rather_than_sending_an_empty_request()
    {
        var machine = Create();
        machine.Consume("hey agent");

        var step = machine.Consume("send it");

        Assert.Equal(StandbyOutcome.Cancelled, step.Outcome);
    }

    [Fact]
    public void Silence_timeout_sends_whatever_was_captured()
    {
        var machine = Create();
        machine.Consume("hey agent, check the logs");

        var step = machine.OnSilenceTimeout();

        Assert.Equal(StandbyOutcome.Send, step.Outcome);
        Assert.Equal("check the logs", step.Request);
    }

    [Fact]
    public void Silence_timeout_while_asleep_does_nothing()
    {
        var machine = Create();

        Assert.Equal(StandbyOutcome.Ignored, machine.OnSilenceTimeout().Outcome);
    }

    [Fact]
    public void Blank_transcripts_are_ignored()
    {
        var machine = Create();
        machine.Consume("hey agent");

        var step = machine.Consume("   ");

        Assert.Equal(StandbyOutcome.Ignored, step.Outcome);
        Assert.Equal(StandbyState.Listening, machine.State);
    }

    [Fact]
    public void Custom_phrases_are_honoured()
    {
        var machine = new StandbyStateMachine(new StandbySettings
        {
            WakePhrase = "computer",
            SendPhrase = "engage",
            CancelPhrase = "belay that",
        });

        machine.Consume("computer plot a course");
        var step = machine.Consume("engage");

        Assert.Equal(StandbyOutcome.Send, step.Outcome);
        Assert.Equal("plot a course", step.Request);
    }
}
