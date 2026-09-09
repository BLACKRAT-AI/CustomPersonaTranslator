using System;
using System.Text;
using System.Collections.Generic;
using System.Linq;

namespace CPT.Core.Llm;

/// <summary>
/// Decides whether a persona rewrite may be spoken instead of the answer it was
/// supposed to be a rewrite OF.
///
/// This exists because of a failure that is worse than an ugly voice: the local
/// persona model is small, and given a system prompt written in the second
/// person ("you are the ship's computer") it will happily ANSWER the text
/// instead of restating it. The user then hears a fluent, in-character reply
/// that has nothing to do with what the coding agent actually said — which
/// looks exactly like the agent ignoring the question.
///
/// The prompt is written to stop that. This is the net underneath it: if the
/// rewrite has thrown away the answer's substance, the answer is spoken plainly.
/// A flat voice saying the right thing beats a perfect voice saying the wrong
/// thing.
/// </summary>
public static class PersonaRewrite
{
    /// <summary>
    /// Rewrites shorter than this fraction of the source have dropped content
    /// rather than tightened it.
    /// </summary>
    private const double MinLengthRatio = 0.25;

    /// <summary>
    /// How much of the source's distinctive vocabulary has to survive. A real
    /// rewrite changes the wording around the facts; it cannot change all the
    /// facts as well.
    /// </summary>
    private const double MinOverlap = 0.30;

    /// <summary>Words too common to prove anything about shared meaning.</summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "but", "if", "then", "than", "that", "this", "these", "those",
        "is", "are", "was", "were", "be", "been", "being", "am", "do", "does", "did", "done",
        "have", "has", "had", "will", "would", "can", "could", "should", "may", "might", "must",
        "i", "you", "he", "she", "it", "we", "they", "me", "him", "her", "us", "them",
        "my", "your", "his", "its", "our", "their", "to", "of", "in", "on", "at", "for", "with",
        "from", "by", "as", "so", "not", "no", "yes", "there", "here", "what", "which", "who",
        "when", "where", "how", "why", "all", "any", "some", "just", "now", "one", "two",
    };

    /// <summary>
    /// True when <paramref name="rewrite"/> still says what
    /// <paramref name="source"/> said, and may be spoken in its place.
    /// </summary>
    public static bool KeepsSubstance(string? source, string? rewrite)
    {
        if (string.IsNullOrWhiteSpace(rewrite)) return false;
        if (string.IsNullOrWhiteSpace(source)) return true;

        // Very short answers ("Done.", "Yes, 42 tests pass.") have almost no
        // vocabulary to overlap, so the ratio test would reject every valid
        // rewrite of them. Length alone is the only honest check there.
        var sourceWords = Meaningful(source);
        if (sourceWords.Count < 4)
            return rewrite.Trim().Length >= 2;

        if (rewrite.Trim().Length < source.Trim().Length * MinLengthRatio) return false;

        var kept = sourceWords.Count(w => Meaningful(rewrite).Contains(w));
        return (double)kept / sourceWords.Count >= MinOverlap;
    }

    /// <summary>The distinctive words in a passage, lower-cased and de-duplicated.</summary>
    private static HashSet<string> Meaningful(string text)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(
            [' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '—', '–'],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.Trim();
            if (word.Length < 3 || Noise.Contains(word)) continue;
            words.Add(word);
        }
        return words;
    }

    /// <summary>
    /// Removes any of the persona's own instructions that came back as answer.
    ///
    /// Asking one CLI turn to both do the work and speak in character means the
    /// character description travels in the prompt, and a model that mishandles
    /// it reads it out: observed here as the agent announcing "Rewrite the
    /// user's text in the voice of J.A.R.V.I.S., a sophisticated, unflappable
    /// British AI butler..." to a user who had asked it something else.
    ///
    /// Compared line by line rather than as a whole, because a leak is usually
    /// a paragraph of the guide followed by the real answer, and the real answer
    /// is worth keeping.
    /// </summary>
    public static string WithoutInstructions(string? answer, string? personaPrompt)
    {
        if (string.IsNullOrWhiteSpace(answer)) return "";
        if (string.IsNullOrWhiteSpace(personaPrompt)) return answer.Trim();

        var guide = Normalise(personaPrompt);
        var kept = new List<string>();

        foreach (var line in answer.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var text = line.Trim();
            if (text.Length == 0) continue;

            // Marker lines are never speech.
            if (text.StartsWith("<<<", StringComparison.Ordinal)
                || text.EndsWith(">>>", StringComparison.Ordinal))
            {
                continue;
            }

            // A short line can coincide with the guide by accident; a long one
            // that appears in it verbatim was copied from it.
            var normalised = Normalise(text);
            if (normalised.Length >= 40 && guide.Contains(normalised, StringComparison.Ordinal)) continue;

            kept.Add(text);
        }

        return string.Join(" ", kept).Trim();
    }

    /// <summary>Lower case, single spaces, no punctuation, for comparing wording.</summary>
    private static string Normalise(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c)) clean.Append(char.ToLowerInvariant(c));
            else if (clean.Length > 0 && clean[^1] != ' ') clean.Append(' ');
        }
        return clean.ToString().Trim();
    }
}
