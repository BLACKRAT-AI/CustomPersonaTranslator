using CPT.Core.Cli;
using CPT.Core.Cli.Streaming;
using Xunit;

namespace CPT.Tests;

public class ConversationIsolationTests
{
    [Fact]
    public void Tool_access_change_refreshes_only_that_conversation_and_survives_switching()
    {
        using var cli = new CliOrchestrator(CliProviderCatalog.CodexId);
        cli.Select(CliProviderCatalog.CodexId, "project", "one");
        var options = new Dictionary<string, string> { ["desktop"] = "off" };
        cli.Options = options;
        var first = cli.GetAgent();
        first.SessionId = "old-no-tools";
        // This mutation mirrors editing a live AgentRow, not replacing a dictionary.
        options["desktop"] = "on";
        cli.ExtraArguments = ["-c", "mcp_servers.cpt_desktop.enabled=true"];
        Assert.Null(cli.GetAgent().SessionId);
        first.SessionId = "tools-ready";
        cli.Select(CliProviderCatalog.CodexId, "project", "two");
        Assert.Empty(cli.ExtraArguments);
        cli.GetAgent().SessionId = "second-session";
        cli.Select(CliProviderCatalog.CodexId, "project", "one");
        cli.Options = options;
        cli.ExtraArguments = ["-c", "mcp_servers.cpt_desktop.enabled=true"];
        Assert.Equal("tools-ready", cli.GetAgent().SessionId);
        options["model"] = "another-model";
        Assert.Equal("tools-ready", cli.GetAgent().SessionId);
        options["permissions"] = "read-only";
        Assert.Null(cli.GetAgent().SessionId);
        cli.Select(CliProviderCatalog.CodexId, "project", "two");
        Assert.Equal("second-session", cli.GetAgent().SessionId);
    }

    [Fact]
    public void Agents_in_the_same_folder_keep_separate_conversations()
    {
        using var cli = new CliOrchestrator(CliProviderCatalog.CodexId);
        cli.Select(CliProviderCatalog.CodexId, "C:\\project", "computer");
        var computer = cli.GetAgent();
        computer.SessionId = "computer-session";
        cli.Select(CliProviderCatalog.CodexId, "C:\\project", "jarvis");
        var jarvis = cli.GetAgent();
        Assert.NotSame(computer, jarvis);
        Assert.Null(jarvis.SessionId);
        cli.Select(CliProviderCatalog.CodexId, "C:\\project", "computer");
        Assert.Same(computer, cli.GetAgent());
        Assert.Equal("computer-session", cli.GetAgent().SessionId);
    }

    [Fact]
    public void Selection_changes_provider_synchronously()
    {
        using var cli = new CliOrchestrator(CliProviderCatalog.ClaudeCodeId);
        cli.Select(CliProviderCatalog.CodexId, "C:\\project", "jarvis");
        Assert.Equal(CliProviderCatalog.CodexId, cli.Provider.Id);
        Assert.Equal(CliProviderCatalog.CodexId, cli.GetAgent().Provider.Id);
    }

    [Theory]
    [InlineData(CliProviderCatalog.CodexId)]
    [InlineData(CliProviderCatalog.ClaudeCodeId)]
    public void Missing_session_starts_fresh_instead_of_resuming_an_unrelated_turn(string provider)
    {
        using var agent = new CliAgent(CliProviderCatalog.Find(provider)!);
        var args = agent.BuildArguments("line one\nline two", resuming: true);
        Assert.DoesNotContain("--last", args);
        Assert.DoesNotContain("--continue", args);
        Assert.DoesNotContain("--resume", args);
        Assert.DoesNotContain("resume", args);
        Assert.DoesNotContain("{session}", args);
    }

    [Fact]
    public void Reset_only_forgets_the_selected_agent()
    {
        using var cli = new CliOrchestrator(CliProviderCatalog.CodexId);
        cli.Select(CliProviderCatalog.CodexId, "C:\\project", "one");
        cli.GetAgent().SessionId = "one-session";
        cli.Select(CliProviderCatalog.CodexId, "C:\\project", "two");
        cli.GetAgent().SessionId = "two-session";
        cli.ResetConversation();
        Assert.Null(cli.GetAgent().SessionId);
        cli.Select(CliProviderCatalog.CodexId, "C:\\project", "one");
        Assert.Equal("one-session", cli.GetAgent().SessionId);
    }

    [Theory]
    [InlineData(CliProviderCatalog.CodexId, "{\"type\":\"thread.started\",\"thread_id\":\"owned-session\"}")]
    [InlineData(CliProviderCatalog.ClaudeCodeId, "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"owned-session\"}")]
    public void Readers_capture_the_session_the_cli_actually_created(string provider, string json)
    {
        var reader = CliTurnReaderFactory.Create(CliProviderCatalog.Find(provider)!.Run.OutputFormat);
        Assert.Contains(CliTurnEvent.Session("owned-session"), reader.Read(new ProcessLine(ProcessOutputSource.StandardOutput, json)));
    }

    [Fact]
    public void Failed_turn_does_not_report_earlier_narration_as_success()
    {
        var reader = new CodexJsonLinesReader();
        Read(reader, """{"type":"item.completed","item":{"type":"agent_message","text":"I will do that."}}""");
        Read(reader, """{"type":"turn.failed","error":{"message":"Permission denied"}}""");
        var final = Assert.Single(reader.Flush());
        Assert.Equal(CliTurnEvent.Error("Permission denied"), final);
    }

    [Fact]
    public void Updated_messages_are_not_spoken_multiple_times()
    {
        var reader = new CodexJsonLinesReader();
        Assert.Empty(Read(reader, """{"type":"item.started","item":{"type":"agent_message","text":"Hello"}}"""));
        Assert.Empty(Read(reader, """{"type":"item.updated","item":{"type":"agent_message","text":"Hello"}}"""));
        Assert.Single(Read(reader, """{"type":"item.completed","item":{"type":"agent_message","text":"Hello"}}"""));
    }

    [Fact]
    public void Tool_activity_is_visible_without_exposing_command_output_as_speech()
    {
        var reader = new CodexJsonLinesReader();
        var events = Read(reader, """{"type":"item.started","item":{"type":"command_execution","command":"do-work","aggregated_output":"private output"}}""");
        Assert.Equal(CliTurnEvent.Activity("Running a command"), Assert.Single(events));
        Assert.Empty(reader.Flush());
    }

    private static List<CliTurnEvent> Read(CodexJsonLinesReader reader, string json) =>
        reader.Read(new ProcessLine(ProcessOutputSource.StandardOutput, json)).ToList();
}
