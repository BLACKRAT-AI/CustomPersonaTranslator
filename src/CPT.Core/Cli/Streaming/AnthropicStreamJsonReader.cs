using System.Collections.Generic;
using System.Text.Json;

namespace CPT.Core.Cli.Streaming;

/// <summary>
/// Reads Claude Code's <c>--output-format stream-json</c>: one JSON object per
/// line, of which we care about two shapes.
///
/// <code>
/// {"type":"assistant","message":{"content":[{"type":"text","text":"..."}]}}
/// {"type":"result","subtype":"success","result":"..."}
/// </code>
///
/// The assistant events arrive as the turn is generated, so they are what we
/// speak. The trailing result event repeats the same prose, and is used only as a
/// fallback for the case where the schema changed and no assistant event matched.
/// </summary>
public sealed class AnthropicStreamJsonReader : ICliTurnReader
{
    private string? _resultFallback;
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

            switch (JsonLine.StringOrNull(root, "type"))
            {
                case "assistant":
                    // An API error is delivered as an assistant message with a
                    // flag on the envelope. Without honouring that flag the app
                    // treats "API Error: 400 ... version 2.1.251 or newer is
                    // required" as the agent's ANSWER and reads it out in
                    // persona -- which is exactly what asking a question and
                    // getting nothing useful looked like.
                    if (JsonLine.BoolOrDefault(root, "is_api_error_message"))
                    {
                        foreach (var text in AssistantTextBlocks(root)) _error ??= text;
                        break;
                    }

                    foreach (var text in AssistantTextBlocks(root))
                    {
                        _sawAssistantText = true;
                        yield return CliTurnEvent.Assistant(text);
                    }
                    break;

                case "result":
                    // is_error marks a turn the CLI itself considers failed,
                    // whatever its subtype says.
                    if (JsonLine.BoolOrDefault(root, "is_error"))
                        _error ??= JsonLine.StringOrNull(root, "result") ?? "The CLI reported an error.";
                    else if (JsonLine.StringOrNull(root, "subtype") is { } subtype && subtype != "success")
                        _error = JsonLine.StringOrNull(root, "result") ?? subtype;
                    else
                        _resultFallback = JsonLine.StringOrNull(root, "result");
                    break;

                case "system":
                    // init / tool bookkeeping. Interesting for logs, never spoken.
                    break;
            }
        }
    }

    public IEnumerable<CliTurnEvent> Flush()
    {
        if (_error is { Length: > 0 })
        {
            yield return CliTurnEvent.Error(_error);
            yield break;
        }
        if (!_sawAssistantText && _resultFallback is { Length: > 0 })
            yield return CliTurnEvent.Assistant(_resultFallback);
    }

    private static IEnumerable<string> AssistantTextBlocks(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message)) yield break;
        if (!message.TryGetProperty("content", out var content)) yield break;

        if (content.ValueKind == JsonValueKind.String)
        {
            if (content.GetString() is { Length: > 0 } plain) yield return plain;
            yield break;
        }

        if (content.ValueKind != JsonValueKind.Array) yield break;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (JsonLine.StringOrNull(block, "type") != "text") continue;
            if (JsonLine.StringOrNull(block, "text") is { Length: > 0 } text) yield return text;
        }
    }
}
