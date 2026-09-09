using System.Text.Json;
using CPT.Core.Agents;
using CPT.Core.Cli;
using Xunit;

namespace CPT.Tests;

public class AgentSetupTests
{
    [Fact]
    public void Existing_fast_profile_isolates_external_tools_once()
    {
        var agent = new AgentProfile { ProviderId = CliProviderCatalog.CodexId, SetupVersion = 1,
            Options = new() { ["speed"] = "fast", ["extensions"] = "on", ["desktop"] = "on" } };
        AgentSetup.EnsureConfigured(agent);
        Assert.Equal("off", agent.Options["extensions"]);
        Assert.Equal("on", agent.Options["desktop"]);
        agent.Options["extensions"] = "on";
        AgentSetup.EnsureConfigured(agent);
        Assert.Equal("on", agent.Options["extensions"]);
    }

    [Fact]
    public void New_codex_agent_has_working_defaults_without_visiting_settings()
    {
        var agent = AgentSetup.Create("Test", "c3po", CliProviderCatalog.CodexId);
        Assert.False(agent.ReceiveOfficialApp);
        Assert.Equal("on", agent.Options["desktop"]);
        Assert.Equal("danger-full-access", agent.Options["permissions"]);
        Assert.Equal("fast", agent.Options["speed"]);
        Assert.Equal("low", agent.Options["effort"]);
        Assert.Equal("off", agent.Options["extensions"]);
        Assert.Equal("c3po", agent.PersonaId);
    }

    [Fact]
    public void Old_visible_chat_agent_is_repaired_once_and_keeps_identity_and_custom_model()
    {
        var agent = JsonSerializer.Deserialize<AgentProfile>("""
            {"Id":"old", "Name":"Jarvis", "ProviderId":"codex-cli", "PersonaId":"jarvis",
             "ReceiveOfficialApp":true,"OfficialSessionId":"desktop:visible-chat",
             "TriggerPhrases":["hey jarvis"],"WorkingDirectory":"project",
             "Options":{"model":"custom-model","effort":"high","desktop":"off"}}
            """)!;
        AgentSetup.EnsureConfigured(agent);
        Assert.False(agent.ReceiveOfficialApp);
        Assert.Equal("on", agent.Options["desktop"]);
        Assert.Equal("custom-model", agent.Options["model"]);
        Assert.Equal("high", agent.Options["effort"]);
        Assert.Equal("old", agent.Id);
        Assert.Equal("jarvis", agent.PersonaId);
        Assert.Equal("hey jarvis", Assert.Single(agent.TriggerPhrases));
        Assert.Equal("project", agent.WorkingDirectory);
        // Subsequent deliberate choices must survive restart and selection.
        agent.Options["desktop"] = "off";
        agent.Options["permissions"] = "read-only";
        agent.ReceiveOfficialApp = true;
        agent.OfficialSessionId = AgentSetup.VisibleChatSession;
        var reloaded = JsonSerializer.Deserialize<AgentProfile>(JsonSerializer.Serialize(agent))!;
        AgentSetup.EnsureConfigured(reloaded);
        Assert.True(reloaded.ReceiveOfficialApp);
        Assert.Equal("off", reloaded.Options["desktop"]);
        Assert.Equal("read-only", reloaded.Options["permissions"]);
    }

    [Fact]
    public void Custom_event_links_and_non_codex_providers_are_not_repurposed()
    {
        var hook = new AgentProfile { ProviderId = CliProviderCatalog.CodexId,
            ReceiveOfficialApp = true, OfficialSessionId = "custom-hook" };
        AgentSetup.EnsureConfigured(hook);
        Assert.True(hook.ReceiveOfficialApp);
        Assert.Equal("custom-hook", hook.OfficialSessionId);
        var other = new AgentProfile { ProviderId = CliProviderCatalog.ClaudeCodeId };
        AgentSetup.EnsureConfigured(other);
        Assert.Empty(other.Options);
    }

    [Fact]
    public void New_agent_owns_its_options_and_provider_switch_can_fill_missing_defaults()
    {
        var template = new Dictionary<string, string> { ["model"] = "custom-model" };
        var agent = AgentSetup.Create("New", "jarvis", CliProviderCatalog.CodexId, template);
        agent.Options["model"] = "different";
        Assert.Equal("custom-model", template["model"]);
        agent.Options.Clear();
        AgentSetup.EnsureConfigured(agent);
        Assert.Equal("on", agent.Options["desktop"]);
        Assert.Equal("fast", agent.Options["speed"]);
    }
}
