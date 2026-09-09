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
        AgentSetup.EnsureConfigured(agent);
        if (agent.ReceiveOfficialApp && agent.OfficialSessionId.Length == 0) agent.OfficialSessionId = OfficialWindowObserver.Session;
        Providers = providers;
        Personas = personas;

        Phrases = new PhraseList(agent.TriggerPhrases);
        if (Phrases.Rows.Count == 0) Phrases.Add("");
        Phrases.Rows.CollectionChanged += (_, _) => { if (Phrases.Rows.Count == 0) Phrases.Add(""); RefreshSetup(); };

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

    public sealed record ConnectionChoice(string Id, string DisplayName);
    public IReadOnlyList<ConnectionChoice> Connections => Providers
        .Select(p => new ConnectionChoice(p.Id, p.DisplayName))
        .Append(new ConnectionChoice("official-app", "ChatGPT / Codex visible chat (text only)"))
        .Concat(_agent.ReceiveOfficialApp && _agent.OfficialSessionId != OfficialWindowObserver.Session && _agent.OfficialSessionId.Length > 0
            ? new[] { new ConnectionChoice("official-events", "Codex events") } : []).ToArray();
    public string ConnectionId
    {
        get => !ReceiveOfficialApp ? ProviderId : OfficialSessionId.Length > 0 && OfficialSessionId != OfficialWindowObserver.Session ? "official-events" : "official-app";
        set
        {
            if (string.IsNullOrEmpty(value) || value == ConnectionId) return;
            if (value == "official-app") { _agent.ReceiveOfficialApp = true; _agent.OfficialSessionId = OfficialWindowObserver.Session; }
            else if (value == "official-events") _agent.ReceiveOfficialApp = true;
            else { _agent.ReceiveOfficialApp = false; ProviderId = value; }
            Raise(nameof(ConnectionId)); Raise(nameof(CliVisibility)); Raise(nameof(OfficialVisibility)); Raise(nameof(ExpandVoiceSettings));
            Raise(nameof(OfficialSessionId)); Raise(nameof(ReceiveOfficialApp)); RefreshSetup();
        }
    }
    public bool ExpandVoiceSettings => !ReceiveOfficialApp;
    public Visibility OfficialVisibility => ReceiveOfficialApp ? Visibility.Visible : Visibility.Collapsed;
    public string ConnectionStatus { get; private set; } = "Choose Use to activate this connection.";
    public void UpdateConnectionStatus(string status)
    {
        if (OfficialSessionId == OfficialWindowObserver.Session)
            status = status.StartsWith("Displayed reply received", StringComparison.Ordinal) ? "Connected - receiving replies"
                : status.Contains("is working", StringComparison.Ordinal) ? "Connected - the app is working"
                : status.StartsWith("Open the official", StringComparison.Ordinal) ? "Open the app to connect"
                : status.StartsWith("Waiting for", StringComparison.Ordinal) ? "Connected - waiting for a reply"
                : status.StartsWith("Screen observation unavailable", StringComparison.Ordinal) ? "Cannot read this chat. Try reopening the app."
                : status == "Screen observation is off." ? "Connecting..." : status;
        if (ConnectionStatus == status) return;
        ConnectionStatus = status; Raise(nameof(ConnectionStatus));
    }

    public bool ReceiveOfficialApp
    {
        get => _agent.ReceiveOfficialApp;
        set { _agent.ReceiveOfficialApp = value; Raise(); Raise(nameof(CliVisibility)); RefreshSetup(); }
    }
    public Visibility CliVisibility => ReceiveOfficialApp ? Visibility.Collapsed : Visibility.Visible;
    public string OfficialSessionId
    {
        get => _agent.OfficialSessionId;
        set { _agent.OfficialSessionId = value.Trim(); Raise(); RefreshSetup(); }
    }
    public bool SpeakOfficialReplies
    {
        get => _agent.SpeakOfficialReplies;
        set { _agent.SpeakOfficialReplies = value; Raise(); }
    }

    public string Name
    {
        get => _agent.Name;
        set { _agent.Name = value; Raise(); }
    }

    /// <summary>
    /// The project this agent works on.
    ///
    /// The single most consequential setting here, and it had no field at all:
    /// left blank the CLI runs in the user's home folder, where there is no code
    /// to read. An agent asked about a build, a test or a file then answers from
    /// general knowledge, which reads exactly like an agent that is not very
    /// bright.
    /// </summary>
    public string WorkingDirectory
    {
        get => _agent.WorkingDirectory;
        set { _agent.WorkingDirectory = value ?? ""; Raise(); }
    }

    /// <summary>Every phrase that summons this agent.</summary>
    public PhraseList Phrases { get; }

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
            AgentSetup.EnsureConfigured(_agent);
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

            if (ReceiveOfficialApp) return "Replies from the app, spoken by your persona.";
            if (!CliReady) return $"{Provider?.DisplayName ?? "The CLI"} is not linked yet.";

            return _agent.TriggerPhrases.Count == 0
                ? "Ready. Record a phrase to summon it by voice."
                : $"Ready. Say “{_agent.TriggerPhrases[0]}” to ask it something.";
        }
    }

    /// <summary>Whether the Fix button has anything to offer.</summary>
    public Visibility SetupVisibility =>
        Persona is null || (!ReceiveOfficialApp && !CliReady) ? Visibility.Visible : Visibility.Collapsed;

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
        Fill(RewriteOptions, CliProviderCatalog.Find(RewriteProviderId), _agent.RewriteOptions, rewriting: true);

    /// <summary>
    /// Builds pickers for whatever the provider declares, writing each choice
    /// straight back into the profile as it is made.
    /// </summary>
    private static void Fill(
        ObservableCollection<CliOptionRow> rows, CliProvider? provider, Dictionary<string, string> stored, bool rewriting = false)
    {
        rows.Clear();
        if (provider is null) return;

        foreach (var option in provider.Options)
        {
            if (rewriting && option.Id == "desktop") continue;
            stored.TryGetValue(option.Id, out var choiceId);
            var row = new CliOptionRow(option, choiceId);
            row.PropertyChanged += (_, _) => stored[option.Id] = row.SelectedId;
            rows.Add(row);
        }
    }
}
