using System.Collections.Generic;
using CPT.Core.Cli;

namespace CPT.Core.Agents;

/// <summary>Shared defaults and one-time repair for desktop-capable Codex agents.</summary>
public static class AgentSetup
{
    public const int CurrentVersion = 2;
    public const string VisibleChatSession = "desktop:visible-chat";

    public static AgentProfile Create(string name, string personaId, string providerId,
        IReadOnlyDictionary<string, string>? options = null)
    {
        var agent = new AgentProfile { Name = name, PersonaId = personaId, ProviderId = providerId,
            Options = options is null ? [] : new Dictionary<string, string>(options) };
        EnsureConfigured(agent);
        return agent;
    }

    public static void EnsureConfigured(AgentProfile agent)
    {
        if (agent.ProviderId != CliProviderCatalog.CodexId) return;
        if (agent.SetupVersion < 1)
        {
            // Earlier builds created inert profiles and routed computer requests
            // to a text-only visible composer. Repair once, not on every selection.
            if (agent.ReceiveOfficialApp && (agent.OfficialSessionId.Length == 0
                || agent.OfficialSessionId == VisibleChatSession))
            {
                agent.ReceiveOfficialApp = false;
                agent.OfficialSessionId = "";
            }
            agent.Options["desktop"] = "on";
            agent.Options["permissions"] = "danger-full-access";

        }
        // Older fast profiles inherited every external MCP server, including dead
        // endpoints. Keep our explicitly registered screen/mouse tools isolated.
        if (agent.SetupVersion < 2)
        {
            if (!agent.Options.TryGetValue("speed", out var speed) || speed == "fast")
                agent.Options["extensions"] = "off";
            agent.SetupVersion = CurrentVersion;
        }
        agent.Options.TryAdd("desktop", "on");
        agent.Options.TryAdd("permissions", "danger-full-access");
        agent.Options.TryAdd("model", "gpt-6-astra");
        agent.Options.TryAdd("effort", "low");
        agent.Options.TryAdd("speed", "fast");
        agent.Options.TryAdd("extensions", "off");
    }
}
