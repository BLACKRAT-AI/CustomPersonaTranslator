using System.Collections.Generic;

namespace CPT.Core.Cli.Streaming;

/// <summary>What a <see cref="CliTurnEvent"/> carries.</summary>
public enum CliTurnEventKind
{
    /// <summary>The exact conversation created or resumed by this process.</summary>
    SessionStarted,

    /// <summary>A tool started or finished. Display immediately; never synthesize it.</summary>
    Activity,
    /// <summary>A fragment of the assistant's reply, to be spoken.</summary>
    AssistantText,

    /// <summary>Progress or tool chatter. Shown in diagnostics, never spoken.</summary>
    Notice,

    /// <summary>
    /// Something the assistant said on its way to the answer, worth speaking
    /// now.
    ///
    /// A coding CLI narrates a long task -- "FL Studio is open, sir; I am
    /// preparing the arrangement" -- and those messages used to be filed with
    /// the tool chatter and thrown away. A three-minute task was three minutes
    /// of silence followed by one sentence, which from across the room is
    /// indistinguishable from an app that has stopped working.
    /// </summary>
    Progress,

    /// <summary>The CLI reported a failure for this turn.</summary>
    Error,
}

/// <summary>One decoded event from a CLI's output stream.</summary>
public readonly record struct CliTurnEvent(CliTurnEventKind Kind, string Text)
{
    public static CliTurnEvent Session(string id) => new(CliTurnEventKind.SessionStarted, id);
    public static CliTurnEvent Activity(string text) => new(CliTurnEventKind.Activity, text);
    public static CliTurnEvent Assistant(string text) => new(CliTurnEventKind.AssistantText, text);
    public static CliTurnEvent Notice(string text) => new(CliTurnEventKind.Notice, text);
    public static CliTurnEvent Progress(string text) => new(CliTurnEventKind.Progress, text);
    public static CliTurnEvent Error(string text) => new(CliTurnEventKind.Error, text);
}

/// <summary>
/// Turns one CLI's raw stdout/stderr lines into provider-neutral events.
///
/// Implementations are deliberately pure line-in / events-out state machines with
/// no process or I/O of their own, which is what makes them straightforward to
/// test against captured transcripts.
/// </summary>
public interface ICliTurnReader
{
    /// <summary>Decodes one line of process output. May yield zero or more events.</summary>
    IEnumerable<CliTurnEvent> Read(ProcessLine line);

    /// <summary>
    /// Called once the process has exited, to emit anything the reader was still
    /// holding back (a buffered final answer, or a "nothing came out" error).
    /// </summary>
    IEnumerable<CliTurnEvent> Flush();
}
