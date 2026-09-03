using System;
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
}
