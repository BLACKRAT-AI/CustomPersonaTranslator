using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace CPT.Core.Cli.Streaming;

/// <summary>
/// Reads a CLI that prints one JSON object once the turn is complete, which is
/// what Gemini CLI does under <c>--output-format json</c>.
///
/// The whole of stdout is buffered because the object is pretty-printed across
/// many lines. Nothing can be spoken until the process exits, so this format is
/// only selected when a provider offers no incremental alternative.
/// </summary>
public sealed class SingleJsonObjectReader : ICliTurnReader
{
    private static readonly string[] AnswerProperties = ["response", "output", "text", "content", "result"];
    private static readonly string[] ErrorProperties = ["error", "message"];

    private readonly StringBuilder _buffer = new();

    public IEnumerable<CliTurnEvent> Read(ProcessLine line)
    {
        if (line.Source == ProcessOutputSource.StandardOutput)
        {
            _buffer.Append(line.Text).Append('\n');
            yield break;
        }

        var text = AnsiEscape.Strip(line.Text);
        if (!AnsiEscape.IsNoise(text)) yield return CliTurnEvent.Notice(text);
    }

    public IEnumerable<CliTurnEvent> Flush()
    {
        var raw = _buffer.ToString().Trim();
        if (raw.Length == 0)
        {
            yield return CliTurnEvent.Error("The CLI produced no output for this turn.");
            yield break;
        }

        if (!JsonLine.TryParse(raw, out var document))
        {
            // Not JSON after all -- a banner, or an older build. The prose is still
            // the answer, so speak it rather than discarding the turn.
            yield return CliTurnEvent.Assistant(AnsiEscape.Strip(raw));
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error)
                && error.ValueKind is JsonValueKind.Object or JsonValueKind.String)
            {
                var message = error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : JsonLine.FirstStringOrNull(error, ErrorProperties);
                if (message is { Length: > 0 })
                {
                    yield return CliTurnEvent.Error(message);
                    yield break;
                }
            }

            var answer = JsonLine.FirstStringOrNull(root, AnswerProperties);
            yield return answer is { Length: > 0 }
                ? CliTurnEvent.Assistant(answer)
                : CliTurnEvent.Error("The CLI returned JSON with no recognisable answer field.");
        }
    }
}
