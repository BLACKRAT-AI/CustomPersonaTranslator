using CPT.Core.Cli;
using CPT.Core.Cli.Streaming;
using Xunit;

namespace CPT.Tests;

public class DesktopAndSpeedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fast_mode_and_desktop_registration_reach_initial_and_resumed_turns(bool resume)
    {
        using var agent = new CliAgent(CliProviderCatalog.Find(CliProviderCatalog.CodexId)!);
        agent.SessionId = "owned-session";
        agent.Options = new Dictionary<string, string> { ["speed"] = "fast", ["extensions"] = "on" };
        agent.ExtraArguments = ["-c", "mcp_servers.cpt_desktop.enabled=true"];
        var args = agent.BuildArguments("request\nwith context", resume);
        Assert.Contains("service_tier=\"fast\"", args);
        Assert.Contains("features.fast_mode=true", args);
        Assert.Contains("mcp_servers.cpt_desktop.enabled=true", args);
        Assert.DoesNotContain("--ignore-user-config", args);
        Assert.Equal("-", args[^1]);
    }

    [Fact]
    public void Policy_denial_is_not_hidden_by_an_assistant_success_claim()
    {
        var reader = new CodexJsonLinesReader();
        _ = reader.Read(new ProcessLine(ProcessOutputSource.StandardError, "tool rejected: blocked by policy")).ToList();
        _ = reader.Read(new ProcessLine(ProcessOutputSource.StandardOutput,
            """{"type":"item.completed","item":{"type":"agent_message","text":"Done"}}""")).ToList();
        Assert.Equal(CliTurnEventKind.Error, Assert.Single(reader.Flush()).Kind);
    }

    [Theory]
    [InlineData("""{"type":"command_execution","exit_code":1}""")]
    [InlineData("""{"type":"mcp_tool_call","status":"failed"}""")]
    public void Failed_tools_are_labelled_failed(string item)
    {
        var reader = new CodexJsonLinesReader();
        var activity = Assert.Single(reader.Read(new ProcessLine(ProcessOutputSource.StandardOutput,
            "{\"type\":\"item.completed\",\"item\":" + item + "}")));
        Assert.EndsWith(" - failed", activity.Text);
    }
}
