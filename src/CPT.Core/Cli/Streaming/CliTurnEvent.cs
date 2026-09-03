using System.Collections.Generic;

namespace CPT.Core.Cli.Streaming;

/// <summary>What a <see cref="CliTurnEvent"/> carries.</summary>
public enum CliTurnEventKind
{
    /// <summary>A fragment of the assistant's reply, to be spoken.</summary>
    AssistantText,

    /// <summary>Progress or tool chatter. Shown in diagnostics, never spoken.</summary>
    Notice,

    /// <summary>The CLI reported a failure for this turn.</summary>
    Error,
}

/// <summary>One decoded event from a CLI's output stream.</summary>
public readonly record struct CliTurnEvent(CliTurnEventKind Kind, string Text)
{
    public static CliTurnEvent Assistant(string text) => new(CliTurnEventKind.AssistantText, text);
    public static CliTurnEvent Notice(string text) => new(CliTurnEventKind.Notice, text);
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
