using CPT.Core.Agents;
using Xunit;

namespace CPT.Tests;

public sealed class OfficialAppTests
{
    [Theory]
    [InlineData("garbage")]
    [InlineData("[]")]
    [InlineData("{\"session_id\":4}")]
    [InlineData("{\"session_id\":\"a\",\"hook_event_name\":\"Unknown\"}")]
    public void Malformed_or_unknown_events_are_ignored(string input) => Assert.Null(OfficialAppEvent.Parse(input));

    [Fact]
    public void Extracts_final_answer_without_tool_input_or_transcript()
    {
        var item = OfficialAppEvent.Parse("""{"session_id":"a","turn_id":"t","hook_event_name":"Stop","last_assistant_message":"Done","tool_input":{"secret":"discard"},"transcript_path":"private"}""");
        Assert.Equal(new OfficialAppEvent("a", "t", "Stop", "Done", "", ""), item);
    }

    [Fact]
    public void Only_linked_active_session_is_accepted_and_late_progress_is_ignored()
    {
        var gate = new OfficialEventGate();
        var agent = new AgentProfile { ReceiveOfficialApp = true, OfficialSessionId = "a" };
        var stop = new OfficialAppEvent("a", "turn1", "Stop", "Done", "", "");
        Assert.False(gate.Accept(agent, stop with { SessionId = "rewriter-session" }));
        Assert.False(gate.Accept(new AgentProfile(), stop));
        Assert.True(gate.Accept(agent, stop));
        Assert.False(gate.Accept(agent, stop));
        Assert.False(gate.Accept(agent, stop with { Kind = "PreToolUse" }));
        Assert.True(gate.Accept(agent, stop with { TurnId = "turn2" }));
    }

    [Fact]
    public void Missing_turn_id_does_not_silence_all_future_replies()
    {
        var gate = new OfficialEventGate();
        var agent = new AgentProfile { ReceiveOfficialApp = true, OfficialSessionId = "a" };
        var stop = new OfficialAppEvent("a", "", "Stop", "First", "", "");
        Assert.True(gate.Accept(agent, stop));
        Assert.False(gate.Accept(agent, stop));
        Assert.True(gate.Accept(agent, stop with { Text = "Second" }));
    }
}
