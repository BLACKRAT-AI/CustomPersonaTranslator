using System.Collections.Generic;
using System.Linq;
using CPT.Core.Cli;
using CPT.Core.Cli.Streaming;
using Xunit;

namespace CPT.Tests;

public class CliTurnReaderTests
{
    private static List<CliTurnEvent> Run(ICliTurnReader reader, params string[] stdoutLines)
    {
        var events = new List<CliTurnEvent>();
        foreach (var line in stdoutLines)
            events.AddRange(reader.Read(new ProcessLine(ProcessOutputSource.StandardOutput, line)));
        events.AddRange(reader.Flush());
        return events;
    }

    private static string AssistantText(IEnumerable<CliTurnEvent> events) =>
        string.Concat(events.Where(e => e.Kind == CliTurnEventKind.AssistantText).Select(e => e.Text));

    // --- Claude Code -------------------------------------------------------

    [Fact]
    public void Anthropic_reader_collects_assistant_text_blocks()
    {
        var events = Run(new AnthropicStreamJsonReader(),
            """{"type":"system","subtype":"init"}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Hello "}]}}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"there."}]}}""",
            """{"type":"result","subtype":"success","result":"Hello there."}""");

        Assert.Equal("Hello there.", AssistantText(events));
    }

    [Fact]
    public void Anthropic_reader_ignores_tool_use_blocks()
    {
        var events = Run(new AnthropicStreamJsonReader(),
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read"},{"type":"text","text":"Done."}]}}""");

        Assert.Equal("Done.", AssistantText(events));
    }

    [Fact]
    public void Anthropic_reader_falls_back_to_the_result_when_no_assistant_event_arrived()
    {
        // Guards against a schema change silently producing a silent persona.
        var events = Run(new AnthropicStreamJsonReader(),
            """{"type":"result","subtype":"success","result":"The answer."}""");

        Assert.Equal("The answer.", AssistantText(events));
    }

    [Fact]
    public void Anthropic_reader_reports_a_failed_result_as_an_error()
    {
        var events = Run(new AnthropicStreamJsonReader(),
            """{"type":"result","subtype":"error_max_turns","result":"hit the turn limit"}""");

        Assert.Contains(events, e => e.Kind == CliTurnEventKind.Error);
    }

    [Fact]
    public void Anthropic_reader_skips_lines_that_are_not_json()
    {
        var events = Run(new AnthropicStreamJsonReader(),
            "npm notice a new version is available",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Fine."}]}}""");

        Assert.Equal("Fine.", AssistantText(events));
    }

    // --- Codex -------------------------------------------------------------

    [Fact]
    public void Codex_reader_understands_the_item_envelope()
    {
        var events = Run(new CodexJsonLinesReader(),
            """{"type":"item.completed","item":{"item_type":"agent_message","text":"Patched it."}}""");

        Assert.Equal("Patched it.", AssistantText(events));
    }

    [Fact]
    public void Codex_reader_understands_the_shipped_item_type_spelling()
    {
        // Captured from `codex exec --json` 0.151.0: the item labels itself with
        // "type", not "item_type", and the surrounding events are thread and turn
        // bookkeeping that must not be spoken.
        var events = Run(new CodexJsonLinesReader(),
            """{"type":"thread.started","thread_id":"01a0652c"}""",
            """{"type":"turn.started"}""",
            """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"pipeline ok"}}""",
            """{"type":"turn.completed","usage":{"input_tokens":15102}}""");

        Assert.Equal("pipeline ok", AssistantText(events));
    }

    [Fact]
    public void Codex_reader_understands_the_older_msg_envelope()
    {
        var events = Run(new CodexJsonLinesReader(),
            """{"msg":{"type":"agent_message","message":"Patched it."}}""");

        Assert.Equal("Patched it.", AssistantText(events));
    }

    [Fact]
    public void Codex_reader_reports_tool_chatter_as_a_notice_not_as_speech()
    {
        var events = Run(new CodexJsonLinesReader(),
            """{"type":"item.completed","item":{"item_type":"command_execution","text":"ran: ls"}}""",
            """{"type":"item.completed","item":{"item_type":"agent_message","text":"Listed."}}""");

        Assert.Equal("Listed.", AssistantText(events));
        Assert.Contains(events, e => e.Kind == CliTurnEventKind.Notice);
    }

    // --- plain text --------------------------------------------------------

    [Fact]
    public void Plain_reader_emits_each_line_and_strips_escape_codes()
    {
        const char esc = (char)0x1B;
        var events = Run(new PlainTextReader(), $"{esc}[32mAll good.{esc}[0m");

        Assert.Equal("All good.\n", AssistantText(events));
    }

    [Fact]
    public void Plain_reader_reports_an_error_when_nothing_was_produced()
    {
        var events = Run(new PlainTextReader());

        Assert.Contains(events, e => e.Kind == CliTurnEventKind.Error);
    }

    [Fact]
    public void Plain_reader_treats_stderr_as_a_notice()
    {
        var reader = new PlainTextReader();
        var events = reader.Read(new ProcessLine(ProcessOutputSource.StandardError, "warning: old version")).ToList();

        Assert.Equal(CliTurnEventKind.Notice, Assert.Single(events).Kind);
    }

    // --- single JSON object ------------------------------------------------

    [Fact]
    public void Json_object_reader_extracts_the_response_field()
    {
        var events = Run(new SingleJsonObjectReader(), """{"response":"Forty-two."}""");

        Assert.Equal("Forty-two.", AssistantText(events));
    }

    [Fact]
    public void Json_object_reader_reads_a_pretty_printed_object_spread_over_lines()
    {
        var events = Run(new SingleJsonObjectReader(), "{", """  "response": "Forty-two." """, "}");

        Assert.Equal("Forty-two.", AssistantText(events));
    }

    [Fact]
    public void Json_object_reader_speaks_plain_prose_when_the_output_is_not_json()
    {
        // An older build, or a banner: the prose is still the answer.
        var events = Run(new SingleJsonObjectReader(), "Forty-two.");

        Assert.Equal("Forty-two.", AssistantText(events));
    }

    [Fact]
    public void Json_object_reader_surfaces_an_error_object()
    {
        var events = Run(new SingleJsonObjectReader(), """{"error":{"message":"quota exceeded"}}""");

        var error = Assert.Single(events, e => e.Kind == CliTurnEventKind.Error);
        Assert.Equal("quota exceeded", error.Text);
    }

    // --- factory -----------------------------------------------------------

    [Theory]
    [InlineData(CliOutputFormat.AnthropicStreamJson, typeof(AnthropicStreamJsonReader))]
    [InlineData(CliOutputFormat.CodexJsonLines, typeof(CodexJsonLinesReader))]
    [InlineData(CliOutputFormat.SingleJsonObject, typeof(SingleJsonObjectReader))]
    [InlineData(CliOutputFormat.Text, typeof(PlainTextReader))]
    public void Factory_maps_every_format_to_its_reader(CliOutputFormat format, System.Type expected)
    {
        Assert.IsType(expected, CliTurnReaderFactory.Create(format));
    }
}
