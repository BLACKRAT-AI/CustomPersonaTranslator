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
        set { _agent.TriggerPhrase = value; Raise(); }
    }

    public CliProvider? Provider
    {
        get => CliProviderCatalog.Find(_agent.ProviderId);
        set
        {
            if (value is null) return;
            _agent.ProviderId = value.Id;
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
            Raise();
        }
    }
}
