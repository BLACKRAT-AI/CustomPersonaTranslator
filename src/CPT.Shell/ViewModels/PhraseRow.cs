using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CPT.Shell.ViewModels;

/// <summary>One spoken phrase in an editable list.</summary>
public sealed class PhraseRow : ObservableObject
{
    private string _text;

    public PhraseRow(string text) => _text = text;

    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value)) Changed?.Invoke(); }
    }

    /// <summary>Raised when the text changes, so the owning list can persist.</summary>
    public event Action? Changed;
}

/// <summary>
/// An editable list of phrases bound straight to the strings it edits.
///
/// A list rather than one box because one phrase is a guess at what a
/// recogniser will produce from a particular voice and microphone, and the
/// guess is often wrong. Recording several -- including whatever it actually
/// heard -- is what removes the guess.
/// </summary>
public sealed class PhraseList : ObservableObject
{
    private readonly List<string> _stored;

    public PhraseList(List<string> stored)
    {
        _stored = stored;
        Rows = [];
        foreach (var phrase in stored) Add(phrase, persist: false);
        Rows.CollectionChanged += (_, _) => Persist();
    }

    public ObservableCollection<PhraseRow> Rows { get; }

    /// <summary>Adds a phrase, ignoring blanks and duplicates.</summary>
    public void Add(string phrase, bool persist = true)
    {
        var trimmed = (phrase ?? "").Trim();
        if (trimmed.Length > 0
            && Rows.Any(r => string.Equals(r.Text, trimmed, StringComparison.OrdinalIgnoreCase)))
            return;

        var row = new PhraseRow(trimmed);
        row.Changed += Persist;
        Rows.Add(row);
        if (persist) Persist();
    }

    public void Remove(PhraseRow row)
    {
        Rows.Remove(row);
        Persist();
    }

    /// <summary>Writes the rows back to the list the settings actually hold.</summary>
    private void Persist()
    {
        _stored.Clear();
        _stored.AddRange(Rows
            .Select(r => r.Text.Trim())
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }
}
