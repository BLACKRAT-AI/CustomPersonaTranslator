using System.Collections.Generic;

namespace CPT.Core.Cli.Streaming;

/// <summary>
/// Reads a CLI that simply prints its answer, which is how Gemini CLI and Copilot
/// CLI behave in prompt mode.
///
/// Lines are emitted as they arrive so speech can start before the turn finishes.
/// Escape sequences are stripped first, and lines that are pure terminal residue
/// are dropped rather than spoken.
/// </summary>
public sealed class PlainTextReader : ICliTurnReader
{
    private bool _sawAssistantText;

    public IEnumerable<CliTurnEvent> Read(ProcessLine line)
    {
        var text = AnsiEscape.Strip(line.Text);
        if (AnsiEscape.IsNoise(text)) yield break;

        if (line.Source == ProcessOutputSource.StandardError)
        {
            // These CLIs put warnings and update notices on stderr. They are worth
            // logging and never worth speaking.
            yield return CliTurnEvent.Notice(text);
            yield break;
        }

        _sawAssistantText = true;
        yield return CliTurnEvent.Assistant(text + "\n");
    }

    public IEnumerable<CliTurnEvent> Flush()
    {
        if (!_sawAssistantText)
            yield return CliTurnEvent.Error("The CLI produced no output for this turn.");
    }
}
