using CPT.Core.Media;
using Xunit;

namespace CPT.Tests;

/// <summary>
/// The clipper remembers a link and its marks. It has to remember them PER
/// PERSONA: with one shared file, opening the clipper for any persona restored
/// whichever one was edited last, which quietly offers the wrong voice.
/// </summary>
public class VoiceClipSessionTests
{
    [Fact]
    public void Two_personas_keep_separate_links()
    {
        Assert.NotEqual(
            VoiceClipSession.PathFor("startrek_computer"),
            VoiceClipSession.PathFor("jarvis"));
    }

    [Fact]
    public void A_persona_keeps_the_same_file_across_visits()
    {
        Assert.Equal(
            VoiceClipSession.PathFor("startrek_computer"),
            VoiceClipSession.PathFor("startrek_computer"));
    }

    [Fact]
    public void No_persona_falls_back_to_the_shared_file()
    {
        Assert.EndsWith("clip-session.json", VoiceClipSession.PathFor(null), System.StringComparison.Ordinal);
        Assert.EndsWith("clip-session.json", VoiceClipSession.PathFor("  "), System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("a/b")]
    [InlineData("nasty:name*")]
    public void An_id_from_a_file_cannot_escape_the_folder(string id)
    {
        // Persona ids come out of a JSON file on disk, so they are not trusted
        // to be safe path segments.
        var path = VoiceClipSession.PathFor(id);
        Assert.Equal(
            System.IO.Path.GetDirectoryName(VoiceClipSession.PathFor(null)),
            System.IO.Path.GetDirectoryName(path));
    }

    [Theory]
    // The real message seen when a video would not load. yt-dlp never ran, so
    // updating it is the wrong response and retrying is the right one.
    [InlineData("[PYI-152220:ERROR] Failed to extract api-ms-win-core-errorhandling-l1-1-0.dll: decompression resulted in return code -1!")]
    public void A_tool_that_never_started_is_retried_not_updated(string failure)
    {
        Assert.True(YoutubeAudio.LooksLikeToolDidNotStart(failure));
        Assert.False(YoutubeAudio.LooksLikeStaleTool(failure));
    }

    [Theory]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden")]
    [InlineData("ERROR: requested format is not available")]
    public void A_stale_tool_is_updated_not_merely_retried(string failure)
    {
        Assert.True(YoutubeAudio.LooksLikeStaleTool(failure));
        Assert.False(YoutubeAudio.LooksLikeToolDidNotStart(failure));
    }
}
