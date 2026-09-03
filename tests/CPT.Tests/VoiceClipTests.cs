using System;
using System.Linq;
using CPT.Core.Media;
using Xunit;

namespace CPT.Tests;

public class VoiceClipTests
{
    private static readonly TimeSpan Media = TimeSpan.FromMinutes(10);

    private static VoiceClip Clip(double startSeconds, double endSeconds) =>
        new(TimeSpan.FromSeconds(startSeconds), TimeSpan.FromSeconds(endSeconds));

    [Fact]
    public void Clips_come_back_in_order_however_they_were_marked()
    {
        var result = VoiceClips.Normalize([Clip(30, 40), Clip(5, 10)], Media);

        Assert.Equal([Clip(5, 10), Clip(30, 40)], result);
    }

    [Fact]
    public void Overlapping_marks_are_merged()
    {
        // Re-marking a passage should widen the clip, not create a seam in it.
        var result = VoiceClips.Normalize([Clip(5, 12), Clip(10, 20)], Media);

        Assert.Equal([Clip(5, 20)], result);
    }

    [Fact]
    public void A_clip_entirely_inside_another_disappears_into_it()
    {
        Assert.Equal([Clip(5, 30)], VoiceClips.Normalize([Clip(5, 30), Clip(10, 12)], Media));
    }

    [Fact]
    public void Marks_a_hair_apart_are_joined()
    {
        var result = VoiceClips.Normalize([Clip(5, 10), Clip(10.05, 15)], Media);

        Assert.Equal([Clip(5, 15)], result);
    }

    [Fact]
    public void A_real_gap_is_preserved()
    {
        var result = VoiceClips.Normalize([Clip(5, 10), Clip(30, 35)], Media);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void A_double_tap_produces_nothing()
    {
        Assert.Empty(VoiceClips.Normalize([Clip(5, 5.05)], Media));
    }

    [Fact]
    public void Clips_are_clamped_to_the_length_of_the_video()
    {
        var result = VoiceClips.Normalize([Clip(590, 900)], Media);

        Assert.Equal(TimeSpan.FromMinutes(10), Assert.Single(result).End);
    }

    [Fact]
    public void Total_duration_counts_merged_time_once()
    {
        var total = VoiceClips.TotalDuration([Clip(5, 15), Clip(10, 20)], Media);

        Assert.Equal(TimeSpan.FromSeconds(15), total);
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(3725, "1:02:05")]
    public void Times_are_formatted_for_reading_aloud_to_yourself(double seconds, string expected)
    {
        Assert.Equal(expected, VoiceClip.Format(TimeSpan.FromSeconds(seconds)));
    }
}

public class AudioClipperTests
{
    private static VoiceClip Clip(double start, double end) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));

    [Fact]
    public void One_clip_becomes_a_single_trim_and_concat()
    {
        var graph = AudioClipper.BuildFilterGraph([Clip(1.5, 4.25)]);

        Assert.Equal("[0:a]atrim=start=1.5:end=4.25,asetpts=PTS-STARTPTS[c0];[c0]concat=n=1:v=0:a=1[out]", graph);
    }

    [Fact]
    public void Several_clips_are_concatenated_in_order()
    {
        var graph = AudioClipper.BuildFilterGraph([Clip(1, 2), Clip(10, 12)]);

        Assert.Contains("[c0][c1]concat=n=2:v=0:a=1[out]", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_fragment_resets_its_timestamps()
    {
        // Without asetpts, concat keeps the original offsets and pads the gaps
        // between clips with silence.
        var graph = AudioClipper.BuildFilterGraph([Clip(1, 2), Clip(10, 12)]);

        Assert.Equal(2, graph.Split("asetpts=PTS-STARTPTS", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Times_are_written_in_the_invariant_culture()
    {
        // A comma decimal separator would silently produce a broken filter graph.
        Assert.Contains("start=1.5", AudioClipper.BuildFilterGraph([Clip(1.5, 2)]), StringComparison.Ordinal);
    }

    [Fact]
    public void The_output_is_mono_sixteen_kilohertz_pcm()
    {
        var arguments = AudioClipper.BuildArguments("in.wav", [Clip(1, 2)], "out.wav");

        Assert.Contains("pcm_s16le", arguments);
        Assert.Contains("16000", arguments);
        Assert.Equal("out.wav", arguments[^1]);
    }
}

public class YoutubeMetadataTests
{
    [Fact]
    public void Reads_id_duration_and_title()
    {
        var info = YoutubeAudio.Parse("dQw4w9WgXcQ\t212\tA Video Title");

        Assert.NotNull(info);
        Assert.Equal("dQw4w9WgXcQ", info.VideoId);
        Assert.Equal(TimeSpan.FromSeconds(212), info.Duration);
        Assert.Equal("A Video Title", info.Title);
    }

    [Fact]
    public void A_live_stream_with_no_duration_still_parses()
    {
        var info = YoutubeAudio.Parse("abc12345678\tNA\tLive now");

        Assert.NotNull(info);
        Assert.Equal(TimeSpan.Zero, info.Duration);
    }

    [Fact]
    public void A_title_containing_a_tab_keeps_its_id()
    {
        var info = YoutubeAudio.Parse("abc12345678\t60\tTitle\twith tab");

        Assert.NotNull(info);
        Assert.Equal("abc12345678", info.VideoId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no tabs here")]
    public void Unusable_output_returns_null(string? line)
    {
        Assert.Null(YoutubeAudio.Parse(line));
    }

    [Theory]
    [InlineData("ERROR: unable to download video data: HTTP Error 403: Forbidden")]
    [InlineData("ERROR: Sign in to confirm you're not a bot")]
    [InlineData("ERROR: Requested format is not available")]
    [InlineData("WARNING: nsig extraction failed")]
    public void YouTube_side_blocks_are_recognised_as_a_stale_downloader(string failure)
    {
        // These read like a problem with the video, but they are what an
        // out-of-date yt-dlp produces after YouTube changes how it serves media.
        Assert.True(YoutubeAudio.LooksLikeStaleTool(failure));
    }

    [Theory]
    [InlineData("ERROR: Video unavailable. This video is private")]
    [InlineData("ERROR: Video unavailable")]
    [InlineData("the download timed out")]
    public void A_genuinely_unavailable_video_does_not_trigger_an_update(string failure)
    {
        Assert.False(YoutubeAudio.LooksLikeStaleTool(failure));
    }

    [Fact]
    public void Download_percentages_become_a_status_line()
    {
        Assert.Equal("Downloading audio… 42.3%",
            YoutubeAudio.DescribeProgress("[download]  42.3% of 5.00MiB at 1.00MiB/s"));
    }

    [Fact]
    public void Fragment_noise_is_not_reported()
    {
        Assert.Null(YoutubeAudio.DescribeProgress("[info] Downloading 1 format(s): 251"));
    }
}
