using CPT.Core.Settings;
using CPT.Core.Voice;
using Xunit;

namespace CPT.Tests;

public class StandbyStateMachineTests
{
    private static StandbyStateMachine Create() => new(new StandbySettings
    {
        WakePhrase = "hey agent",
        CancelPhrases = ["stop", "cancel", "never mind"],
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

    /// <summary>
    /// There is no send phrase. Being asked to say one after having already
    /// finished speaking is the moment standby feels broken, so a pause is the
    /// only thing that ends a request -- and the words that used to end one are
    /// now simply part of it.
    /// </summary>
    [Fact]
    public void The_words_that_used_to_send_are_now_part_of_the_request()
    {
        var machine = Create();
        machine.Consume("hey agent");

        var step = machine.Consume("send it to the printer");

        Assert.Equal(StandbyOutcome.Captured, step.Outcome);
        Assert.Equal(StandbyState.Listening, machine.State);
        Assert.Equal("send it to the printer", machine.OnSilenceTimeout().Request);
    }

    [Fact]
    public void A_whole_request_in_one_breath_is_sent_by_the_pause_that_follows()
    {
        var machine = Create();
        machine.Consume("hey agent what does this function do");

        var step = machine.OnSilenceTimeout();

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

    [Theory]
    [InlineData("stop")]
    [InlineData("cancel")]
    [InlineData("never mind")]
    public void Any_stop_word_abandons_what_is_being_dictated(string word)
    {
        var machine = Create();
        machine.Consume("hey agent");
        machine.Consume("rewrite the whole file");

        Assert.Equal(StandbyOutcome.Cancelled, machine.Consume(word).Outcome);
    }

    /// <summary>
    /// The agent's name and then "stop", said while it is working or talking.
    ///
    /// It must not wake for this: waking would make "stop" the next request and
    /// leave the thing the user wanted stopped still running, which is to say
    /// the only way to interrupt an agent would be to wait for it to finish.
    /// </summary>
    [Theory]
    [InlineData("hey agent stop")]
    [InlineData("hey agent, stop.")]
    [InlineData("hey agent cancel")]
    [InlineData("hey agent never mind")]
    public void Naming_it_and_saying_stop_interrupts_without_waking(string said)
    {
        var machine = Create();

        var step = machine.Consume(said);

        Assert.Equal(StandbyOutcome.Stop, step.Outcome);
        Assert.Equal(StandbyState.Sleeping, machine.State);
    }

    [Fact]
    public void The_agent_told_to_stop_is_reported()
    {
        var machine = Create();
        machine.ExtraWakePhrases = [("hey jarvis", "j1")];

        Assert.Equal("j1", machine.Consume("hey jarvis stop").WokeBy);
    }

    /// <summary>
    /// A question is not an interruption. "Why did the build stop" contains the
    /// word, and answering it is the whole point of asking.
    /// </summary>
    [Fact]
    public void A_question_that_merely_contains_a_stop_word_is_still_a_question()
    {
        var machine = Create();

        var step = machine.Consume("hey agent why did the build stop");

        Assert.Equal(StandbyOutcome.Woke, step.Outcome);
        Assert.Equal("why did the build stop", step.Captured);
    }

    [Fact]
    public void A_pause_with_nothing_captured_cancels_rather_than_sending_an_empty_request()
    {
        var machine = Create();
        machine.Consume("hey agent");

        var step = machine.OnSilenceTimeout();

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
            CancelPhrases = ["belay that"],
        });

        machine.Consume("computer plot a course");

        Assert.Equal(StandbyOutcome.Cancelled, machine.Consume("belay that").Outcome);
        Assert.Equal(StandbyState.Sleeping, machine.State);
    }

    /// <summary>
    /// Half a sentence is not a question. Sent anyway, it costs a whole turn and
    /// comes back as "your message appears to have cut off".
    /// </summary>
    [Theory]
    [InlineData("I'm in FL Studio and", true)]
    [InlineData("open the settings file and", true)]
    [InlineData("what happened to the", true)]
    [InlineData("tell me why it", false)]
    [InlineData("why did the build fail", false)]
    [InlineData("check the logs", false)]
    [InlineData("", false)]
    public void A_request_that_trails_off_mid_sentence_is_not_finished(string said, bool expected)
    {
        Assert.Equal(expected, StandbyStateMachine.EndsMidThought(said));
    }
}
