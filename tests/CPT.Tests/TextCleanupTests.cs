using System.Linq;
using CPT.Core.Cli.Streaming;
using CPT.Core.Stt;
using Xunit;

namespace CPT.Tests;

public class AnsiEscapeTests
{
    private const char Esc = (char)0x1B;

    [Fact]
    public void Plain_text_is_returned_unchanged()
    {
        Assert.Equal("nothing to strip", AnsiEscape.Strip("nothing to strip"));
    }

    [Fact]
    public void Colour_codes_are_removed()
    {
        Assert.Equal("red text", AnsiEscape.Strip($"{Esc}[31mred text{Esc}[0m"));
    }

    [Fact]
    public void Cursor_movement_and_erase_codes_are_removed()
    {
        Assert.Equal("done", AnsiEscape.Strip($"{Esc}[2K{Esc}[1Gdone"));
    }

    [Fact]
    public void Window_title_sequences_are_removed()
    {
        Assert.Equal("after", AnsiEscape.Strip($"{Esc}]0;a title\adone".Replace("done", "after")));
    }

    [Fact]
    public void Carriage_returns_from_progress_redraws_are_removed()
    {
        Assert.Equal("50%75%", AnsiEscape.Strip("50%\r75%"));
    }

    [Fact]
    public void Tabs_and_newlines_survive()
    {
        Assert.Equal("a\tb\nc", AnsiEscape.Strip("a\tb\nc"));
    }

    [Fact]
    public void A_line_of_pure_control_codes_counts_as_noise()
    {
        Assert.True(AnsiEscape.IsNoise(AnsiEscape.Strip($"{Esc}[2K{Esc}[1G")));
        Assert.False(AnsiEscape.IsNoise(AnsiEscape.Strip($"{Esc}[2Kreal")));
    }
}

public class WhisperCommandTests
{
    [Fact]
    public void The_transcript_is_never_diverted_to_a_file()
    {
        // -otxt makes whisper-cli write the transcript beside the audio and print
        // nothing, so every transcription came back empty and both push-to-talk
        // and standby appeared to hear nothing at all.
        var arguments = WhisperCpp.BuildArguments("clip.wav", "model.bin", "en");

        Assert.DoesNotContain("-otxt", arguments);
        Assert.DoesNotContain("-of", arguments);
    }

    [Fact]
    public void Timestamps_are_suppressed_and_the_model_and_audio_are_passed()
    {
        var arguments = WhisperCpp.BuildArguments("clip.wav", "model.bin", "en");

        Assert.Contains("-nt", arguments);
        Assert.Equal("model.bin", arguments[arguments.ToList().IndexOf("-m") + 1]);
        Assert.Equal("clip.wav", arguments[arguments.ToList().IndexOf("-f") + 1]);
    }
}

public class WhisperOutputTests
{
    [Fact]
    public void Joins_lines_into_one_utterance()
    {
        Assert.Equal("hello there friend", WhisperCpp.Clean("hello there\nfriend\n"));
    }

    [Fact]
    public void Strips_timestamps_that_survive_the_no_timestamp_flag()
    {
        Assert.Equal("hello there", WhisperCpp.Clean("[00:00:00.000 --> 00:00:02.000]   hello there\n"));
    }

    [Fact]
    public void Drops_non_speech_annotations_entirely()
    {
        // Otherwise "[BLANK_AUDIO]" gets spoken, or matched against a wake phrase.
        Assert.Equal("", WhisperCpp.Clean("[BLANK_AUDIO]\n"));
        Assert.Equal("", WhisperCpp.Clean("(wind blowing)\n"));
    }

    [Fact]
    public void Keeps_speech_that_follows_an_annotation()
    {
        Assert.Equal("hey agent", WhisperCpp.Clean("(door closes) hey agent\n"));
    }

    [Fact]
    public void Empty_output_produces_an_empty_string()
    {
        Assert.Equal("", WhisperCpp.Clean("\n\n   \n"));
    }
}
