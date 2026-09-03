using System;
using System.Text.Json;

namespace CPT.Core.Cli.Streaming;

/// <summary>
/// Small helpers shared by the JSON turn readers.
///
/// CLI output streams are not guaranteed to be well formed -- a warning, a
/// progress line or a stray banner can appear between JSON records -- so parsing
/// is always attempted, never assumed.
/// </summary>
internal static class JsonLine
{
    /// <summary>
    /// Parses one line as a JSON document. Returns false (and no document) for a
    /// blank line or anything that is not JSON, which callers simply skip.
    /// </summary>
    public static bool TryParse(string line, out JsonDocument document)
    {
        document = null!;
        var trimmed = line.AsSpan().Trim();
        if (trimmed.IsEmpty) return false;
        if (trimmed[0] is not '{' and not '[') return false;

        try
        {
            document = JsonDocument.Parse(trimmed.ToString());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The named string property, or null when it is absent or not a string.</summary>
    public static string? StringOrNull(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The first of <paramref name="propertyNames"/> present as a non-empty string.</summary>
    public static string? FirstStringOrNull(JsonElement element, params string[] propertyNames)
    {
        foreach (var name in propertyNames)
        {
            if (StringOrNull(element, name) is { Length: > 0 } value) return value;
        }
        return null;
    }
}
