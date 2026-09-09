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

    private string? _lastAgentMessage;
    private string? _error;

    public IEnumerable<CliTurnEvent> Read(ProcessLine line)
    {
        if (line.Source == ProcessOutputSource.StandardError)
        {
            var noise = AnsiEscape.Strip(line.Text);
            if (noise.Contains("blocked by policy", System.StringComparison.OrdinalIgnoreCase))
                _error = "A requested action was blocked by policy. No successful completion was verified.";
            if (!AnsiEscape.IsNoise(noise)) yield return CliTurnEvent.Notice(noise);
            yield break;
        }

        if (!JsonLine.TryParse(line.Text, out var document)) yield break;
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) yield break;

            var eventType = JsonLine.StringOrNull(root, "type");
            if (eventType == "thread.started"
                && JsonLine.StringOrNull(root, "thread_id") is { Length: > 0 } session)
            {
                yield return CliTurnEvent.Session(session);
                yield break;
            }
            if (eventType == "turn.failed")
            {
                _error = root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
                    ? JsonLine.StringOrNull(error, "message") ?? "The agent could not complete this request."
                    : "The agent could not complete this request.";
                yield break;
            }

            // Newer envelope: {"type":"item.completed","item":{...}}
            if (root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
            {
                var itemType = JsonLine.FirstStringOrNull(item, "type", "item_type");
                if (itemType is "command_execution" or "mcp_tool_call" or "web_search" or "file_change")
                {
                    var description = itemType switch
                    {
                        "command_execution" => "Running a command",
                        "mcp_tool_call" => "Using " + (JsonLine.StringOrNull(item, "tool") ?? "a connected tool"),
                        "web_search" => "Searching the web",
                        _ => "Updating files",
                    };
                    var failed = JsonLine.StringOrNull(item, "status") == "failed"
                        || (item.TryGetProperty("exit_code", out var exitCode) && exitCode.ValueKind == JsonValueKind.Number && exitCode.TryGetInt32(out var code) && code != 0);
                    yield return CliTurnEvent.Activity(eventType == "item.completed"
                        ? description + (failed ? " - failed" : " - finished") : description);
                    if (failed && item.TryGetProperty("error", out var toolError))
                    {
                        var reason = toolError.ValueKind == JsonValueKind.Object
                            ? JsonLine.StringOrNull(toolError, "message") : toolError.ToString();
                        if (!string.IsNullOrWhiteSpace(reason))
                        {
                            yield return CliTurnEvent.Notice(reason);
                            if (reason.Contains("approval", System.StringComparison.OrdinalIgnoreCase)
                                || reason.Contains("denied", System.StringComparison.OrdinalIgnoreCase)
                                || reason.Contains("blocked by policy", System.StringComparison.OrdinalIgnoreCase))
                                _error = "Desktop or tool access was blocked: " + reason;
                        }
                    }
                    if (JsonLine.StringOrNull(item, "text") is { Length: > 0 } detail)
                        yield return CliTurnEvent.Notice(detail);
                    yield break;
                }
                // Started and updated items may carry the same text as completed.
                if (eventType is "item.started" or "item.updated") yield break;
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
        if (_error is { Length: > 0 })
        {
            yield return CliTurnEvent.Error(_error);
            yield break;
        }

        // The LAST agent message, not all of them joined together. Codex
        // narrates its way through a task -- one message per step -- and
        // speaking the lot meant reading the whole working session aloud
        // instead of the answer it arrived at.
        if (_lastAgentMessage is { Length: > 0 })
            yield return CliTurnEvent.Assistant(_lastAgentMessage);
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
            _lastAgentMessage = text;

            // Spoken as it arrives. The last one is still held back for Flush,
            // so the answer is not read twice.
            yield return CliTurnEvent.Progress(text);
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
