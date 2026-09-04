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

    /// <summary>
    /// This agent's per-turn choices for its CLI: model, effort, thinking.
    /// Keyed by option id, exactly as the catalog declares them.
    /// </summary>
    public Dictionary<string, string> Options { get; set; } = [];

    /// <summary>
    /// The CLI that restates this agent's answers in its persona's voice, and
    /// its own per-turn choices.
    ///
    /// Separate from the agent's CLI because the two jobs want opposite things:
    /// the agent should be the strongest model available, and rephrasing three
    /// sentences on every single reply should be the cheapest. Empty follows
    /// the agent's own CLI.
    /// </summary>
    public string RewriteProviderId { get; set; } = "";

    public Dictionary<string, string> RewriteOptions { get; set; } = [];


    /// <summary>Persona id supplying the voice and manner of the reply.</summary>
    public string PersonaId { get; set; } = "";

    /// <summary>
    /// Spoken phrases that summon this agent, e.g. "hey computer".
    ///
    /// A list, because one phrase is a guess at what a recogniser will produce
    /// from one person's voice and microphone, and the guess is often wrong.
    /// Recording several — including whatever it actually heard — is how you
    /// stop guessing. Empty means the agent can only be picked by hand.
    /// </summary>
    public List<string> TriggerPhrases { get; set; } = [];

    /// <summary>
    /// The single phrase this used to hold. Kept so older settings still load,
    /// and folded into the list on the way in.
    /// </summary>
    public string TriggerPhrase
    {
        get => TriggerPhrases.Count > 0 ? TriggerPhrases[0] : "";
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (!TriggerPhrases.Exists(p => string.Equals(p, value, StringComparison.OrdinalIgnoreCase)))
                TriggerPhrases.Insert(0, value.Trim());
        }
    }


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
