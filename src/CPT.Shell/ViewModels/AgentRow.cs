using System.Windows;
using System.Collections.Generic;
using CPT.Core.Agents;
using CPT.Core.Cli;
using CPT.Core.Models;

namespace CPT.Shell.ViewModels;

/// <summary>
/// One agent as edited in settings.
///
/// The row writes straight through to the <see cref="AgentProfile"/> it wraps,
/// so there is no Save button to forget: typing a phrase or picking a persona
/// is the change. The settings window persists on close and on every edit.
/// </summary>
public sealed class AgentRow : ObservableObject
{
    private readonly AgentProfile _agent;

    public AgentRow(AgentProfile agent, IReadOnlyList<CliProvider> providers, IReadOnlyList<Persona> personas)
    {
        _agent = agent;
        Providers = providers;
        Personas = personas;
    }

    /// <summary>The profile this row edits.</summary>
    public AgentProfile Agent => _agent;

    public IReadOnlyList<CliProvider> Providers { get; }
    public IReadOnlyList<Persona> Personas { get; }

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

    public CliProvider? Provider
    {
        get => CliProviderCatalog.Find(_agent.ProviderId);
        set
        {
            if (value is null) return;
            _agent.ProviderId = value.Id;
            RefreshSetup();
            Raise();
        }
    }

    public Persona? Persona
    {
        get
        {
            foreach (var persona in Personas)
                if (persona.Id == _agent.PersonaId) return persona;
            return null;
        }
        set
        {
            if (value is null) return;
            _agent.PersonaId = value.Id;
            RefreshSetup();
            Raise();
        }
    }

    /// <summary>
    /// What this agent still needs before it can answer, in plain words.
    ///
    /// An agent is a pair, and a half-made pair fails at the moment it is
    /// spoken to rather than at the moment it is made. The row says which half
    /// is missing while the user is still looking at it.
    /// </summary>
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

    /// <summary>Re-reads everything the row shows about setup state.</summary>
    public void RefreshSetup()
    {
        Raise(nameof(SetupHint));
        Raise(nameof(SetupVisibility));
    }
}
