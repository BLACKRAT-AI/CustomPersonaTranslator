using System;
using System.Text;

namespace CPT.Core.Cli.Streaming;

/// <summary>
/// Strips terminal control sequences from CLI output.
///
/// NO_COLOR and TERM=dumb remove most of it, but the coding CLIs still emit
/// cursor moves and erase-line codes for spinners even when colour is off. Those
/// would otherwise be read aloud as gibberish, so they are removed here rather
/// than in each reader.
/// </summary>
public static class AnsiEscape
{
    private const char Escape = (char)0x1B;

    /// <summary>Returns <paramref name="text"/> with escape sequences removed.</summary>
    public static string Strip(string text)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf(Escape) < 0)
            return RemoveStrayControlCharacters(text);

        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != Escape) { result.Append(text[i]); continue; }
            i = SkipSequence(text, i);
        }
        return RemoveStrayControlCharacters(result.ToString());
    }

    /// <summary>
    /// Returns the index of the last character belonging to the escape sequence
    /// that starts at <paramref name="start"/>, so the caller's loop can continue
    /// after it.
    /// </summary>
    private static int SkipSequence(string text, int start)
    {
        var i = start + 1;
        if (i >= text.Length) return i;

        switch (text[i])
        {
            // CSI: ESC [ params intermediates final. The final byte is @ to ~.
            case '[':
                i++;
                while (i < text.Length && text[i] is >= ' ' and <= '?') i++;
                while (i < text.Length && text[i] is >= ' ' and <= '/') i++;
                return i < text.Length ? i : text.Length - 1;

            // OSC: ESC ] ... terminated by BEL or ST (ESC \).
            case ']':
                i++;
                while (i < text.Length && text[i] != '\a')
                {
                    if (text[i] == Escape && i + 1 < text.Length && text[i + 1] == '\\') return i + 1;
                    i++;
                }
                return i < text.Length ? i : text.Length - 1;

            // Two-character sequences such as ESC = , ESC > , ESC ( B.
            default:
                return i;
        }
    }

    private static string RemoveStrayControlCharacters(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        var needsWork = false;
        foreach (var c in text)
        {
            if (IsUnwantedControl(c)) { needsWork = true; break; }
        }
        if (!needsWork) return text;

        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!IsUnwantedControl(c)) result.Append(c);
        }
        return result.ToString();
    }

    // Tab and newline are meaningful; carriage return and the rest are terminal
    // bookkeeping that only exists to redraw a line in place.
    private static bool IsUnwantedControl(char c) =>
        (c < ' ' && c is not '\t' and not '\n') || c == (char)0x7F;

    /// <summary>
    /// True for a line that carries no readable content once stripped -- the
    /// residue of a spinner frame or a progress redraw.
    /// </summary>
    public static bool IsNoise(string strippedLine) =>
        strippedLine.AsSpan().Trim().IsEmpty;
}
