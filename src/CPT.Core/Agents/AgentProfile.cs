using System;
using System.Collections.Generic;
using System.Linq;

namespace CPT.Core.Agents;

/// <summary>
/// One agent: a coding CLI paired with a persona, and the phrase that summons
/// it.
///
/// The point is to have more than one on hand at once. With standby listening,
/// saying an agent's phrase before a question routes that question to THAT
/// agent's CLI and has it answered in THAT agent's voice — so a Claude agent
/// and a Codex agent can be asked different questions in the same breath,
/// without opening settings between them.
/// </summary>
public sealed class AgentProfile
{
    /// <summary>Stable id, used to remember which agent was last active.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("n")[..8];

    /// <summary>What the user calls it. Shown in the bar's picker.</summary>
    public string Name { get; set; } = "Agent";

    /// <summary>Provider id from the CLI catalog, e.g. "claude-code".</summary>
    public string ProviderId { get; set; } = "claude-code";

    /// <summary>Persona id supplying the voice and manner of the reply.</summary>
    public string PersonaId { get; set; } = "";

    /// <summary>
    /// Spoken phrase that routes the rest of the sentence to this agent, e.g.
    /// "ask claude". Empty means the agent can only be picked by hand.
    /// </summary>
    public string TriggerPhrase { get; set; } = "";

    /// <summary>
    /// Directory this agent's CLI runs turns in. Empty follows the global
    /// setting — an agent per repository is the reason this is per agent.
    /// </summary>
    public string WorkingDirectory { get; set; } = "";

    /// <summary>Carry conversation context between this agent's turns.</summary>
    public bool KeepContext { get; set; } = true;
}

/// <summary>
/// The configured agents, and which one is answering.
/// </summary>
public sealed class AgentBook
{
    public List<AgentProfile> Agents { get; set; } = [];

    /// <summary>Id of the agent currently answering, or empty for the first one.</summary>
    public string ActiveId { get; set; } = "";

    /// <summary>The active agent, or null when none are configured.</summary>
    public AgentProfile? Active =>
        Agents.FirstOrDefault(a => a.Id == ActiveId) ?? Agents.FirstOrDefault();

    /// <summary>Looks one up by id.</summary>
    public AgentProfile? ById(string? id) =>
        string.IsNullOrEmpty(id) ? null : Agents.FirstOrDefault(a => a.Id == id);
}
