using System.Collections.Generic;
using System.Text.Json;

namespace CPT.Core.Cli.Streaming;

/// <summary>
/// Reads Codex CLI's <c>exec --json</c> event stream.
///
/// Codex has shipped two event envelopes and still emits either depending on
/// version, so both are handled:
///
/// <code>
/// {"type":"item.completed","item":{"item_type":"agent_message","text":"..."}}
/// {"msg":{"type":"agent_message","message":"..."}}
/// </code>
///
/// Anything that is not an agent message -- reasoning, command execution, patch
/// application -- becomes a notice so it shows in diagnostics without being read
/// aloud.
/// </summary>
public sealed class CodexJsonLinesReader : ICliTurnReader
{
    private const string AgentMessageType = "agent_message";

    private bool _sawAssistantText;
    private string? _error;

    public IEnumerable<CliTurnEvent> Read(ProcessLine line)
    {
        if (line.Source == ProcessOutputSource.StandardError)
        {
            var noise = AnsiEscape.Strip(line.Text);
            if (!AnsiEscape.IsNoise(noise)) yield return CliTurnEvent.Notice(noise);
            yield break;
        }

        if (!JsonLine.TryParse(line.Text, out var document)) yield break;
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) yield break;

            // Newer envelope: {"type":"item.completed","item":{...}}
            if (root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
            {
                foreach (var e in FromPayload(item)) yield return e;
                yield break;
            }

            // Older envelope: {"msg":{"type":"agent_message","message":"..."}}
            if (root.TryGetProperty("msg", out var msg) && msg.ValueKind == JsonValueKind.Object)
            {
                foreach (var e in FromPayload(msg)) yield return e;
                yield break;
            }

            // Flat envelope, used by some builds for errors.
            foreach (var e in FromPayload(root)) yield return e;
        }
    }

    public IEnumerable<CliTurnEvent> Flush()
    {
        if (_error is { Length: > 0 } && !_sawAssistantText)
            yield return CliTurnEvent.Error(_error);
    }

    private IEnumerable<CliTurnEvent> FromPayload(JsonElement payload)
    {
        // Codex labels the payload "item_type" in some builds and "type" in
        // others, so both are accepted rather than guessing from the envelope.
        var type = JsonLine.FirstStringOrNull(payload, "item_type", "type");
        var text = JsonLine.FirstStringOrNull(payload, "text", "message", "content");

        if (type is null || text is null) yield break;

        if (type.Contains(AgentMessageType, System.StringComparison.Ordinal))
        {
            _sawAssistantText = true;
            yield return CliTurnEvent.Assistant(text);
        }
        else if (type.Contains("error", System.StringComparison.OrdinalIgnoreCase))
        {
            _error = text;
        }
        else
        {
            yield return CliTurnEvent.Notice(text);
        }
    }
}
