using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CPT.Core.Voice;

/// <summary>
/// Finds a spoken trigger phrase inside transcribed text.
///
/// Matching is done on normalised word sequences rather than raw substrings,
/// because a transcriber punctuates and capitalises unpredictably: "Hey, agent!"
/// and "hey agent" have to be the same phrase, while "agentic" must not match
/// "agent". Word-level comparison gives both properties for free.
/// </summary>
public static class PhraseMatcher
{
    /// <summary>Where a phrase was found, as a half-open word range.</summary>
    /// <param name="Found">False when the phrase is not present.</param>
    /// <param name="StartWord">Index of the phrase's first word.</param>
    /// <param name="EndWord">Index one past the phrase's last word.</param>
    public readonly record struct Match(bool Found, int StartWord, int EndWord)
    {
        public static Match NotFound => new(false, -1, -1);
    }

    /// <summary>
    /// Splits text into comparable words: lower-cased, stripped of punctuation and
    /// accents, with digits kept so "agent 2" still works.
    /// </summary>
    public static string[] Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var words = new List<string>();
        var current = new StringBuilder();

        foreach (var raw in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;

            if (char.IsLetterOrDigit(raw))
            {
                current.Append(char.ToLowerInvariant(raw));
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }
        if (current.Length > 0) words.Add(current.ToString());
        return words.ToArray();
    }

    /// <summary>Finds the first occurrence of <paramref name="phrase"/> in <paramref name="text"/>.</summary>
    public static Match Find(string? text, string? phrase) => Find(Tokenize(text), Tokenize(phrase));

    /// <summary>Finds the first occurrence of an already-tokenized phrase.</summary>
    public static Match Find(IReadOnlyList<string> words, IReadOnlyList<string> phrase)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(phrase);

        if (phrase.Count == 0 || words.Count < phrase.Count) return Match.NotFound;

        for (var start = 0; start + phrase.Count <= words.Count; start++)
        {
            var matched = true;
            for (var offset = 0; offset < phrase.Count; offset++)
            {
                if (!string.Equals(words[start + offset], phrase[offset], StringComparison.Ordinal))
                {
                    matched = false;
                    break;
                }
            }
            if (matched) return new Match(true, start, start + phrase.Count);
        }
        return Match.NotFound;
    }

    /// <summary>True when the phrase occurs anywhere in the text.</summary>
    public static bool Contains(string? text, string? phrase) => Find(text, phrase).Found;

    /// <summary>
    /// The words that follow the first occurrence of the phrase, rejoined with
    /// single spaces. Returns null when the phrase is absent -- distinct from an
    /// empty string, which means "the phrase was the whole utterance".
    /// </summary>
    public static string? TextAfter(string? text, string? phrase)
    {
        var words = Tokenize(text);
        var match = Find(words, Tokenize(phrase));
        return match.Found ? string.Join(' ', words, match.EndWord, words.Length - match.EndWord) : null;
    }

    /// <summary>
    /// The words that precede the first occurrence of the phrase. Returns null
    /// when the phrase is absent.
    /// </summary>
    public static string? TextBefore(string? text, string? phrase)
    {
        var words = Tokenize(text);
        var match = Find(words, Tokenize(phrase));
        return match.Found ? string.Join(' ', words, 0, match.StartWord) : null;
    }
}
