using System;
using System.Text;
using CPT.Core.Settings;

namespace CPT.Core.Voice;

/// <summary>Whether standby mode is waiting for its wake phrase or taking dictation.</summary>
public enum StandbyState
{
    /// <summary>Listening only for the wake phrase.</summary>
    Sleeping,

    /// <summary>Awake, collecting the request until the send phrase.</summary>
    Listening,
}

/// <summary>What one transcript did to the conversation.</summary>
public enum StandbyOutcome
{
    /// <summary>Nothing relevant was said.</summary>
    Ignored,

    /// <summary>The wake phrase was heard; dictation has begun.</summary>
    Woke,

    /// <summary>More of the request was captured.</summary>
    Captured,

    /// <summary>The request is complete and should be sent.</summary>
    Send,

    /// <summary>The request was abandoned.</summary>
    Cancelled,
}

/// <summary>The result of feeding one transcript to the state machine.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Request">The complete request, set only when <paramref name="Outcome"/> is Send.</param>
/// <param name="Captured">Everything captured so far, for live display.</param>
public readonly record struct StandbyStep(StandbyOutcome Outcome, string? Request, string Captured);

/// <summary>
/// The conversation rules of standby mode, with no audio or timers attached.
///
/// The user speaks a wake phrase to start ("hey agent"), dictates, and speaks a
/// send phrase to finish ("send it"). Both phrases are configurable, so nothing
/// here may assume particular words.
///
/// Everything is decided from transcript text alone, which keeps the rules
/// testable and means a mis-heard word can never leave the machine in a state the
/// user cannot talk their way out of.
/// </summary>
public sealed class StandbyStateMachine
{
    private readonly StandbySettings _settings;
    private readonly StringBuilder _captured = new();

    public StandbyStateMachine(StandbySettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public StandbyState State { get; private set; } = StandbyState.Sleeping;

    /// <summary>Everything dictated since waking.</summary>
    public string Captured => _captured.ToString();

    /// <summary>
    /// Applies one transcribed utterance.
    /// </summary>
    public StandbyStep Consume(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return Step(StandbyOutcome.Ignored);

        return State == StandbyState.Sleeping
            ? ConsumeWhileSleeping(transcript)
            : ConsumeWhileListening(transcript);
    }

    /// <summary>
    /// Called when the user has stopped talking for longer than the configured
    /// timeout. Whatever has been dictated is sent, so that forgetting the send
    /// phrase costs a pause rather than the whole request.
    /// </summary>
    public StandbyStep OnSilenceTimeout()
    {
        if (State != StandbyState.Listening) return Step(StandbyOutcome.Ignored);
        return _captured.Length > 0 ? Complete() : Cancel();
    }

    /// <summary>Returns to sleep, discarding anything captured.</summary>
    public void Reset()
    {
        State = StandbyState.Sleeping;
        _captured.Clear();
    }

    private StandbyStep ConsumeWhileSleeping(string transcript)
    {
        var afterWake = PhraseMatcher.TextAfter(transcript, _settings.WakePhrase);
        if (afterWake is null) return Step(StandbyOutcome.Ignored);

        State = StandbyState.Listening;
        _captured.Clear();

        // The wake phrase and the request usually arrive in one breath -- "hey
        // agent, what changed in this file" -- so whatever followed it is already
        // the beginning of the request.
        return string.IsNullOrWhiteSpace(afterWake)
            ? Step(StandbyOutcome.Woke)
            : ApplyDictation(afterWake, StandbyOutcome.Woke);
    }

    private StandbyStep ConsumeWhileListening(string transcript)
    {
        if (PhraseMatcher.Contains(transcript, _settings.CancelPhrase))
        {
            // Anything said before "never mind" was part of the abandoned request.
            return Cancel();
        }
        return ApplyDictation(transcript, StandbyOutcome.Captured);
    }

    /// <summary>
    /// Adds one utterance to the request, honouring a send phrase inside it.
    /// </summary>
    private StandbyStep ApplyDictation(string transcript, StandbyOutcome outcomeIfIncomplete)
    {
        var beforeSend = PhraseMatcher.TextBefore(transcript, _settings.SendPhrase);
        if (beforeSend is null)
        {
            Append(transcript);
            return Step(outcomeIfIncomplete);
        }

        // The send phrase itself is an instruction, not part of the request.
        Append(beforeSend);
        return _captured.Length > 0 ? Complete() : Cancel();
    }

    private void Append(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return;
        if (_captured.Length > 0) _captured.Append(' ');
        _captured.Append(trimmed);
    }

    private StandbyStep Complete()
    {
        var request = _captured.ToString();
        Reset();
        return new StandbyStep(StandbyOutcome.Send, request, request);
    }

    private StandbyStep Cancel()
    {
        Reset();
        return new StandbyStep(StandbyOutcome.Cancelled, null, "");
    }

    private StandbyStep Step(StandbyOutcome outcome) => new(outcome, null, Captured);
}
