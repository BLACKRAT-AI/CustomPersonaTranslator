using System;
using CPT.Core.Media;
using Xunit;

namespace CPT.Tests;

public class TimelineMathTests
{
    private static readonly TimeSpan Media = TimeSpan.FromSeconds(100);
    private const double Width = 1000;      // ten pixels per second
    private const double EdgeGrab = 6;

    private static VoiceClip Clip(double start, double end) =>
        new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end));

    private static TimelineHit HitAt(double x, params VoiceClip[] clips) =>
        TimelineMath.HitTest(x, Media, Width, clips, EdgeGrab);

    // --- coordinates ------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(50, 500)]
    [InlineData(100, 1000)]
    public void Time_maps_onto_the_strip(double seconds, double expectedX)
    {
        Assert.Equal(expectedX, TimelineMath.XFor(TimeSpan.FromSeconds(seconds), Media, Width), 3);
    }

    [Fact]
    public void A_position_maps_back_to_a_time()
    {
        Assert.Equal(TimeSpan.FromSeconds(25), TimelineMath.TimeFor(250, Media, Width));
    }

    [Fact]
    public void Positions_outside_the_strip_clamp_to_its_ends()
    {
        Assert.Equal(TimeSpan.Zero, TimelineMath.TimeFor(-40, Media, Width));
        Assert.Equal(Media, TimelineMath.TimeFor(4000, Media, Width));
    }

    [Fact]
    public void Nothing_is_grabbable_before_a_video_is_loaded()
    {
        Assert.Equal(TimelineGrab.None, TimelineMath.HitTest(100, TimeSpan.Zero, Width, [], EdgeGrab).Grab);
    }

    // --- what a press grabs -----------------------------------------------

    [Fact]
    public void Empty_track_scrubs()
    {
        Assert.Equal(TimelineGrab.Scrub, HitAt(800, Clip(10, 20)).Grab);
    }

    [Fact]
    public void The_opening_edge_resizes_the_start()
    {
        var hit = HitAt(100, Clip(10, 20));

        Assert.Equal(TimelineGrab.ResizeStart, hit.Grab);
        Assert.Equal(0, hit.ClipIndex);
    }

    [Fact]
    public void The_closing_edge_resizes_the_end()
    {
        Assert.Equal(TimelineGrab.ResizeEnd, HitAt(200, Clip(10, 20)).Grab);
    }

    [Fact]
    public void An_edge_is_grabbable_from_either_side()
    {
        Assert.Equal(TimelineGrab.ResizeStart, HitAt(96, Clip(10, 20)).Grab);
        Assert.Equal(TimelineGrab.ResizeStart, HitAt(104, Clip(10, 20)).Grab);
    }

    [Fact]
    public void The_body_moves_the_whole_clip()
    {
        var hit = HitAt(150, Clip(10, 20));

        Assert.Equal(TimelineGrab.Move, hit.Grab);
        Assert.Equal(0, hit.ClipIndex);
    }

    [Fact]
    public void An_edge_wins_over_a_neighbouring_body()
    {
        // Two clips meeting at 20s: pressing the seam must offer an edge, not
        // silently pick up whichever body was listed first.
        var hit = HitAt(200, Clip(10, 20), Clip(20, 30));

        Assert.True(hit.Grab is TimelineGrab.ResizeEnd or TimelineGrab.ResizeStart);
    }

    [Fact]
    public void The_right_clip_is_reported_when_several_are_marked()
    {
        var hit = HitAt(450, Clip(10, 20), Clip(40, 50), Clip(70, 80));

        Assert.Equal(TimelineGrab.Move, hit.Grab);
        Assert.Equal(1, hit.ClipIndex);
    }

    // --- resizing ---------------------------------------------------------

    [Fact]
    public void Dragging_the_start_moves_only_the_start()
    {
        var resized = TimelineMath.Resize(Clip(10, 20), TimeSpan.FromSeconds(14), movingStart: true);

        Assert.Equal(TimeSpan.FromSeconds(14), resized.Start);
        Assert.Equal(TimeSpan.FromSeconds(20), resized.End);
    }

    [Fact]
    public void Dragging_the_end_moves_only_the_end()
    {
        var resized = TimelineMath.Resize(Clip(10, 20), TimeSpan.FromSeconds(26), movingStart: false);

        Assert.Equal(TimeSpan.FromSeconds(10), resized.Start);
        Assert.Equal(TimeSpan.FromSeconds(26), resized.End);
    }

    [Fact]
    public void A_start_dragged_past_the_end_stops_rather_than_inverting()
    {
        var resized = TimelineMath.Resize(Clip(10, 20), TimeSpan.FromSeconds(90), movingStart: true);

        Assert.True(resized.End > resized.Start);
        Assert.Equal(TimeSpan.FromSeconds(20) - VoiceClips.MinimumClipLength, resized.Start);
    }

    [Fact]
    public void An_end_dragged_past_the_start_stops_rather_than_inverting()
    {
        var resized = TimelineMath.Resize(Clip(10, 20), TimeSpan.Zero, movingStart: false);

        Assert.True(resized.End > resized.Start);
    }

    // --- sliding ----------------------------------------------------------

    [Fact]
    public void Sliding_keeps_the_clip_the_same_length()
    {
        var moved = TimelineMath.Slide(Clip(10, 20), TimeSpan.FromSeconds(60), Media);

        Assert.Equal(TimeSpan.FromSeconds(10), moved.Duration);
        Assert.Equal(TimeSpan.FromSeconds(60), moved.Start);
    }

    [Fact]
    public void A_clip_cannot_be_slid_off_the_front()
    {
        var moved = TimelineMath.Slide(Clip(10, 20), TimeSpan.FromSeconds(-30), Media);

        Assert.Equal(TimeSpan.Zero, moved.Start);
        Assert.Equal(TimeSpan.FromSeconds(10), moved.Duration);
    }

    [Fact]
    public void A_clip_cannot_be_slid_off_the_end()
    {
        var moved = TimelineMath.Slide(Clip(10, 20), TimeSpan.FromSeconds(95), Media);

        Assert.Equal(Media, moved.End);
        Assert.Equal(TimeSpan.FromSeconds(10), moved.Duration);
    }

    [Fact]
    public void A_clip_longer_than_the_video_is_pinned_to_the_start()
    {
        var moved = TimelineMath.Slide(Clip(0, 200), TimeSpan.FromSeconds(50), Media);

        Assert.Equal(TimeSpan.Zero, moved.Start);
    }
}
