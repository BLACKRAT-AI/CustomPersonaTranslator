using System;
using System.Collections.Generic;
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
    /// Vowel sounds that recognition confuses, keyed by spelling.
    ///
    /// A wake phrase opens with a throwaway syllable and recognition guesses at
    /// its consonant. Measured on this machine, "hey computer" came back as
    /// "A computer." and, under noise, as "Pay computer." -- the distinctive
    /// word perfect every time and the interjection replaced by something that
    /// rhymes with it. A list of specific words could never keep up with that;
    /// the rule is the rhyme.
    /// </summary>
    private static readonly Dictionary<string, string> VowelClass = new(StringComparer.OrdinalIgnoreCase)
    {
        ["a"] = "A", ["ay"] = "A", ["ey"] = "A", ["ei"] = "A", ["eigh"] = "A", ["aye"] = "A",
        ["i"] = "I", ["y"] = "I", ["igh"] = "I", ["ie"] = "I", ["eye"] = "I",
        ["o"] = "O", ["oh"] = "O", ["ow"] = "O", ["owe"] = "O",
        ["ello"] = "ELLO", ["allo"] = "ELLO",
        ["ay,"] = "A",
    };

    /// <summary>
    /// True when two short words rhyme closely enough that recognition would
    /// swap one for the other.
    ///
    /// Leading consonants are dropped and the remaining vowel is classified, so
    /// hey / pay / say / they / a all agree, and "the" does not: its vowel is a
    /// different sound and "the computer is over there" must never wake
    /// anything.
    /// </summary>
    private static bool SoundsLike(string spoken, string expected)
    {
        if (string.Equals(spoken, expected, StringComparison.OrdinalIgnoreCase)) return true;

        // Only the throwaway syllable at the front of a phrase is ever forgiven,
        // and only a short word can be one.
        if (spoken.Length > 5 || expected.Length > 5) return false;

        return VowelOf(spoken) is { } a && VowelOf(expected) is { } b && a == b;
    }

    private static string? VowelOf(string word)
    {
        var start = 0;
        while (start < word.Length && !"aeiouy".Contains(char.ToLowerInvariant(word[start]))) start++;
        if (start >= word.Length) return null;

        return VowelClass.TryGetValue(word[start..], out var vowel) ? vowel : null;
    }

    /// <summary>
    /// What speech recognition tends to turn a wake phrase's opening word into.
    ///
    /// Not a general list of filler: only words that SOUND like the one they
    /// replace. "hey" and "a" are both /eɪ/, which is exactly why "hey computer"
    /// came back as "A computer." on this machine. "the" sounds like neither, so
    /// "the computer is over there" must not wake anything.
    /// </summary>
    private static readonly Dictionary<string, string[]> Confusable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hey"] = ["hey", "hay", "hi", "a", "eh", "ay", "ey", "he"],
        ["hi"] = ["hi", "high", "hey", "a", "eye"],
        ["hello"] = ["hello", "hallo", "yellow"],
        ["ok"] = ["ok", "okay", "kay"],
        ["okay"] = ["okay", "ok", "kay"],
        ["yo"] = ["yo", "you", "jo"],
        ["computer"] = ["computer"],
    };

    /// <summary>
    /// Like <see cref="TextAfter"/>, but forgiving about the opening word.
    ///
    /// A wake phrase is the first thing said, before the speaker has settled,
    /// and its opening word is a throwaway that recognition mangles. On this
    /// machine "hey computer" was transcribed as "A computer." -- the
    /// distinctive word heard perfectly, the interjection turned into something
    /// that sounds identical -- so an exact match could never fire however good
    /// the audio was.
    ///
    /// The exact phrase is tried first. Failing that, the opening word may be
    /// replaced by one that sounds like it, and by nothing else: the rest of the
    /// phrase still has to be heard, in order, at the very start.
    /// </summary>
    public static string? TextAfterLenient(string? text, string? phrase)
    {
        if (TextAfter(text, phrase) is { } exact) return exact;

        var phraseWords = Tokenize(phrase);
        if (phraseWords.Length < 2) return null;              // nothing to be lenient about
        // Only an opening word that is a throwaway may be forgiven: a phrase
        // whose first word carries meaning must be heard in full.
        if (!Confusable.ContainsKey(phraseWords[0])) return null;

        var words = Tokenize(text);
        if (words.Length == 0) return null;

        // The opening word may be missing entirely, or misheard as something
        // that sounds like it. Nothing else counts.
        var skip = SoundsLike(words[0], phraseWords[0]) ? 1 : 0;

        var remainder = phraseWords[1..];
        var candidate = words[skip..];
        var match = Find(candidate, remainder);
        if (!match.Found || match.StartWord != 0) return null;

        return string.Join(' ', candidate, match.EndWord, candidate.Length - match.EndWord);
    }

    /// <summary>
    /// Whether the text is the phrase and nothing else, once punctuation and
    /// casing are set aside -- so "Stop." is the word "stop".
    /// </summary>
    public static bool IsExactly(string? text, string? phrase)
    {
        var words = Tokenize(text);
        var wanted = Tokenize(phrase);
        if (words.Length == 0 || words.Length != wanted.Length) return false;

        for (var i = 0; i < words.Length; i++)
            if (!string.Equals(words[i], wanted[i], StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }
}
