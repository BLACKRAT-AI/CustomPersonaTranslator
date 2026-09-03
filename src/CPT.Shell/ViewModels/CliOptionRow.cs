using System.Windows;
using CPT.Core.Cli;

namespace CPT.Shell.ViewModels;

/// <summary>
/// One CLI option — model, effort, permissions — bound to a picker in settings.
///
/// The rows are built from whatever the selected provider declares, so the Agent
/// tab shows exactly the settings that CLI actually supports and nothing else.
/// </summary>
public sealed class CliOptionRow : ObservableObject
{
    private CliOptionChoice _selected;

    public CliOptionRow(CliOption option, string? selectedChoiceId)
    {
        Option = option;
        _selected = option.Resolve(selectedChoiceId);
    }

    public CliOption Option { get; }

    public string Label => Option.Label;
    public string Hint => Option.Hint ?? "";

    /// <summary>Collapses the hint line entirely when the option has none.</summary>
    public Visibility HintVisibility => Option.Hint is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public System.Collections.Generic.IReadOnlyList<CliOptionChoice> Choices => Option.Choices;

    public CliOptionChoice Selected
    {
        get => _selected;
        set => Set(ref _selected, value ?? Option.Resolve(null));
    }

    /// <summary>
    /// The chosen id, and what the picker binds to.
    ///
    /// Bound by ID rather than by instance because a ComboBox inside a
    /// DataTemplate can have SelectedItem applied before ItemsSource: the item
    /// is then not in the (still empty) list, WPF clears the selection, and the
    /// picker comes up blank with the stored choice lost. A value has no such
    /// ordering problem.
    /// </summary>
    public string SelectedId
    {
        get => _selected.Id;
        set
        {
            if (value is null || value == _selected.Id) return;
            Selected = Option.Resolve(value);
            Raise();
        }
    }
}
