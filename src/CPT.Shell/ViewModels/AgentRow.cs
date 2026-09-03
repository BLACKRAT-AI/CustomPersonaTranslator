using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CPT.Core.Agents;
using CPT.Core.Cli;
using CPT.Core.Models;

namespace CPT.Shell.ViewModels;

/// <summary>
/// One agent as edited in settings: everything it is, in one tile.
///
/// An agent is a complete unit -- which CLI answers, on what model and at what
/// effort; whose voice it answers in; which CLI does the rephrasing and on what
/// model; and what to say to summon it. Splitting those across tabs meant
/// picking an agent in one place and its settings in another, with no way to
/// give two agents different models at all.
///
/// The row writes straight through to the profile it wraps, so there is no Save
/// button to forget.
/// </summary>
public sealed class AgentRow : ObservableObject
{
    private readonly AgentProfile _agent;

    public AgentRow(AgentProfile agent, IReadOnlyList<CliProvider> providers, IReadOnlyList<Persona> personas)
    {
        _agent = agent;
        Providers = providers;
        Personas = personas;

        BuildOptions();
        BuildRewriteOptions();
    }

    /// <summary>The profile this row edits.</summary>
    public AgentProfile Agent => _agent;

    public IReadOnlyList<CliProvider> Providers { get; }
    public IReadOnlyList<Persona> Personas { get; }

    /// <summary>Per-turn choices for the agent's own CLI.</summary>
    public ObservableCollection<CliOptionRow> Options { get; } = [];

    /// <summary>Per-turn choices for the CLI that speaks in the persona's voice.</summary>
    public ObservableCollection<CliOptionRow> RewriteOptions { get; } = [];

    public string Name
    {
        get => _agent.Name;
        set { _agent.Name = value; Raise(); }
    }

    public string TriggerPhrase
    {
        get => _agent.TriggerPhrase;
        set { _agent.TriggerPhrase = value; Raise(); RefreshSetup(); }
    }

    /// <summary>
    /// The chosen ids, and what the pickers bind to.
    ///
    /// Bound by ID rather than by instance: a ComboBox inside a DataTemplate can
    /// have SelectedItem applied before ItemsSource, and an item that is not in
    /// the still-empty list makes WPF clear the selection.
    /// </summary>
    public string ProviderId
    {
        get => _agent.ProviderId;
        set
        {
            if (string.IsNullOrEmpty(value) || value == _agent.ProviderId) return;
            _agent.ProviderId = value;
            // A different CLI declares different options, so the old choices
            // are meaningless against it.
            _agent.Options.Clear();
            BuildOptions();
            Raise();
            RefreshSetup();
        }
    }

    public string PersonaId
    {
        get => _agent.PersonaId;
        set
        {
            if (string.IsNullOrEmpty(value) || value == _agent.PersonaId) return;
            _agent.PersonaId = value;
            Raise();
            RefreshSetup();
        }
    }

    /// <summary>
    /// The rewrite CLI. "" means "the same one the agent uses", which is the
    /// sensible default: it is installed and signed in by definition.
    /// </summary>
    public string RewriteProviderId
    {
        get => _agent.RewriteProviderId.Length == 0 ? _agent.ProviderId : _agent.RewriteProviderId;
        set
        {
            if (string.IsNullOrEmpty(value) || value == RewriteProviderId) return;
            _agent.RewriteProviderId = value;
            _agent.RewriteOptions.Clear();
            BuildRewriteOptions();
            Raise();
        }
    }

    /// <summary>What this agent still needs before it can answer, in plain words.</summary>
    public string SetupHint
    {
        get
        {
            if (Persona is null)
                return Personas.Count == 0
                    ? "No personas yet — this agent needs a voice."
                    : "Pick a persona for this agent's voice.";

            if (!CliReady) return $"{Provider?.DisplayName ?? "The CLI"} is not linked yet.";

            return string.IsNullOrWhiteSpace(TriggerPhrase)
                ? "Ready. Add a phrase to summon it by voice."
                : $"Ready. Say “{TriggerPhrase}” to ask it something.";
        }
    }

    /// <summary>Whether the Fix button has anything to offer.</summary>
    public Visibility SetupVisibility =>
        Persona is null || !CliReady ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>True when this agent's CLI is installed and signed in.</summary>
    public bool CliReady { get; set; }

    public CliProvider? Provider => CliProviderCatalog.Find(_agent.ProviderId);

    public Persona? Persona => Personas.FirstOrDefault(p => p.Id == _agent.PersonaId);

    /// <summary>Re-reads everything the row shows about setup state.</summary>
    public void RefreshSetup()
    {
        Raise(nameof(SetupHint));
        Raise(nameof(SetupVisibility));
    }

    private void BuildOptions() =>
        Fill(Options, CliProviderCatalog.Find(_agent.ProviderId), _agent.Options);

    private void BuildRewriteOptions() =>
        Fill(RewriteOptions, CliProviderCatalog.Find(RewriteProviderId), _agent.RewriteOptions);

    /// <summary>
    /// Builds pickers for whatever the provider declares, writing each choice
    /// straight back into the profile as it is made.
    /// </summary>
    private static void Fill(
        ObservableCollection<CliOptionRow> rows, CliProvider? provider, Dictionary<string, string> stored)
    {
        rows.Clear();
        if (provider is null) return;

        foreach (var option in provider.Options)
        {
            stored.TryGetValue(option.Id, out var choiceId);
            var row = new CliOptionRow(option, choiceId);
            row.PropertyChanged += (_, _) => stored[option.Id] = row.SelectedId;
            rows.Add(row);
        }
    }
}
