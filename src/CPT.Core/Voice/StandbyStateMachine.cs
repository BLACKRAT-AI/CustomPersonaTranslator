using System;
using System.Collections.Generic;
using CPT.Core.Diagnostics;
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
/// <param name="WokeBy">Which phrase woke it, so the caller knows who was addressed.</param>
public readonly record struct StandbyStep(
    StandbyOutcome Outcome, string? Request, string Captured, string? WokeBy = null);

/// <summary>
/// The conversation rules of standby mode, with no audio or timers attached.
///
/// The user speaks a wake phrase to start, dictates, and speaks a send phrase to
/// finish. ANY of several phrases can wake it: the general one from settings,
/// and each agent's own. Saying an agent's name is the natural way to address
/// it, and requiring "hey agent" first and the agent's name second was two
/// passwords for one door.
///
/// Everything is decided from transcript text alone, which keeps the rules
/// testable and means a mis-heard word can never leave the machine in a state the
/// user cannot talk their way out of.
/// </summary>
public sealed class StandbyStateMachine
{
    private readonly StandbySettings _settings;
    private readonly StringBuilder _captured = new();
    private string? _wokeBy;

    public StandbyStateMachine(StandbySettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Extra phrases that also wake it, each with the id of whoever owns it.
    /// Set by the host whenever the agent list changes.
    /// </summary>
    public IReadOnlyList<(string Phrase, string Owner)> ExtraWakePhrases { get; set; } = [];

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
        _wokeBy = null;
    }

    /// <summary>
    /// Looks for any wake phrase at all.
    ///
    /// The longest match wins, so an agent called with "hey computer" is not
    /// swallowed by a general "hey" — the more specific address is the one the
    /// user meant.
    /// </summary>
    private StandbyStep ConsumeWhileSleeping(string transcript)
    {
        if (MatchWake(transcript) is not { } match) return Step(StandbyOutcome.Ignored);
        var (bestPhrase, bestOwner, bestRemainder) = match;

        State = StandbyState.Listening;
        _captured.Clear();
        _wokeBy = bestOwner;

        CptLog.Write($"[standby] woke on \"{bestPhrase}\""
            + (bestOwner is null ? "" : " for agent " + bestOwner));

        // The wake phrase and the request usually arrive in one breath -- "hey
        // computer, what changed in this file" -- so whatever followed it is
        // already the beginning of the request.
        return string.IsNullOrWhiteSpace(bestRemainder)
            ? Step(StandbyOutcome.Woke)
            : ApplyDictation(bestRemainder, StandbyOutcome.Woke);
    }

    /// <summary>
    /// Whether a transcript addresses this machine, and what was said after.
    ///
    /// Separate from consuming it, because the fast recogniser looks for the
    /// wake phrase in speech that is still being spoken, and must be able to ask
    /// "was I called?" without changing anything if the answer turns out to be
    /// yes but the rest of the sentence is better heard by the accurate model.
    ///
    /// The longest match wins, so an agent called with "hey computer" is not
    /// swallowed by a general "hey" -- the more specific address is the one the
    /// user meant.
    /// </summary>
    public (string Phrase, string? Owner, string Remainder)? MatchWake(string? transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript)) return null;

        string? bestRemainder = null;
        string? bestPhrase = null;
        string? bestOwner = null;
        var bestLength = 0;

        void Consider(string? phrase, string? owner)
        {
            if (string.IsNullOrWhiteSpace(phrase)) return;

            var remainder = PhraseMatcher.TextAfterLenient(transcript, phrase);
            if (remainder is null) return;

            var length = PhraseMatcher.Tokenize(phrase).Length;
            if (length <= bestLength) return;

            bestRemainder = remainder;
            bestPhrase = phrase;
            bestOwner = owner;
            bestLength = length;
        }

        foreach (var phrase in _settings.WakePhrases) Consider(phrase, null);
        foreach (var (phrase, owner) in ExtraWakePhrases) Consider(phrase, owner);

        return bestPhrase is null ? null : (bestPhrase, bestOwner, bestRemainder ?? "");
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
        var owner = _wokeBy;
        Reset();
        return new StandbyStep(StandbyOutcome.Send, request, request, owner);
    }

    private StandbyStep Cancel()
    {
        Reset();
        _wokeBy = null;
        return new StandbyStep(StandbyOutcome.Cancelled, null, "");
    }

    private StandbyStep Step(StandbyOutcome outcome) => new(outcome, null, Captured, _wokeBy);
}
