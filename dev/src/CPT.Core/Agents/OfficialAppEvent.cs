using System.Text.Json;

namespace CPT.Core.Agents;

/// <summary>Only the fields needed for narration, never tool arguments or transcripts.</summary>
public sealed record OfficialAppEvent(string SessionId, string TurnId, string Kind, string Text, string Tool, string Directory)
{
    public string DisplayLabel => SessionId + " | " + Directory + " | " + Kind;

    public static OfficialAppEvent? Parse(string json)
    {
        if (json.Length > 1_048_576) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string Read(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
            var session = Read("session_id");
            var kind = Read("hook_event_name");
            if (session.Length == 0 || kind is not ("UserPromptSubmit" or "PreToolUse" or "PostToolUse" or "Stop" or "Interrupt" or "PermissionRequest")) return null;
            return new(session, Read("turn_id"), kind,
                kind == "Stop" ? Read("last_assistant_message") : kind == "UserPromptSubmit" ? Read("prompt") : "",
                Read("tool_name"), Read("cwd"));
        }
        catch (JsonException) { return null; }
    }
}
