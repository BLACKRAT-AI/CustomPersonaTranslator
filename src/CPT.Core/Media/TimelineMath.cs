using System;
using System.Collections.Generic;

namespace CPT.Core.Media;

/// <summary>What a press on the timeline would take hold of.</summary>
public enum TimelineGrab
{
    /// <summary>Nothing, because there is no media loaded.</summary>
    None,

    /// <summary>Empty track: dragging moves the playhead.</summary>
    Scrub,

    /// <summary>The opening edge of a marked section.</summary>
    ResizeStart,

    /// <summary>The closing edge of a marked section.</summary>
    ResizeEnd,

    /// <summary>The body of a marked section: dragging slides the whole thing.</summary>
    Move,
}

/// <summary>The result of a hit test, naming the clip when one was hit.</summary>
public readonly record struct TimelineHit(TimelineGrab Grab, int ClipIndex)
{
    public static TimelineHit Nothing => new(TimelineGrab.None, -1);
}

/// <summary>
/// The arithmetic behind the clip timeline: where a time sits on the strip, what
/// a pixel means, and what a press at a given position takes hold of.
///
/// It lives apart from the control so the behaviour that decides whether a drag
/// trims a clip or scrubs the video can be tested without a window.
/// </summary>
public static class TimelineMath
{
    /// <summary>Horizontal position, in pixels, of a moment in the media.</summary>
    public static double XFor(TimeSpan at, TimeSpan duration, double width)
    {
        if (duration <= TimeSpan.Zero || width <= 0) return 0;
        return Math.Clamp(at.TotalSeconds / duration.TotalSeconds, 0, 1) * width;
    }

    /// <summary>The moment in the media under a horizontal position.</summary>
    public static TimeSpan TimeFor(double x, TimeSpan duration, double width)
    {
        if (duration <= TimeSpan.Zero || width <= 0) return TimeSpan.Zero;
        return TimeSpan.FromSeconds(Math.Clamp(x / width, 0, 1) * duration.TotalSeconds);
    }

    /// <summary>
    /// What a press at <paramref name="x"/> grabs.
    ///
    /// Edges are checked across every clip before any body, so two sections that
    /// touch still both offer a grabbable boundary rather than the first one
    /// swallowing the press.
    /// </summary>
    public static TimelineHit HitTest(
        double x,
        TimeSpan duration,
        double width,
        IReadOnlyList<VoiceClip> clips,
        double edgeGrabPixels)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (duration <= TimeSpan.Zero || width <= 0) return TimelineHit.Nothing;

        for (var i = 0; i < clips.Count; i++)
        {
            if (Math.Abs(x - XFor(clips[i].Start, duration, width)) <= edgeGrabPixels)
                return new TimelineHit(TimelineGrab.ResizeStart, i);
            if (Math.Abs(x - XFor(clips[i].End, duration, width)) <= edgeGrabPixels)
                return new TimelineHit(TimelineGrab.ResizeEnd, i);
        }

        for (var i = 0; i < clips.Count; i++)
        {
            if (x >= XFor(clips[i].Start, duration, width) && x <= XFor(clips[i].End, duration, width))
                return new TimelineHit(TimelineGrab.Move, i);
        }

        return new TimelineHit(TimelineGrab.Scrub, -1);
    }

    /// <summary>
    /// Moves one edge of a clip, never letting it pass the other edge: a drag
    /// through the far side should stop, not turn the clip inside out.
    /// </summary>
    public static VoiceClip Resize(VoiceClip clip, TimeSpan at, bool movingStart) =>
        movingStart
            ? new VoiceClip(Min(at, clip.End - VoiceClips.MinimumClipLength), clip.End)
            : new VoiceClip(clip.Start, Max(at, clip.Start + VoiceClips.MinimumClipLength));

    /// <summary>Slides a clip whole, clamped so it stays inside the media.</summary>
    public static VoiceClip Slide(VoiceClip clip, TimeSpan newStart, TimeSpan mediaDuration)
    {
        var start = newStart < TimeSpan.Zero ? TimeSpan.Zero : newStart;

        if (mediaDuration > TimeSpan.Zero && start + clip.Duration > mediaDuration)
            start = mediaDuration - clip.Duration;
        if (start < TimeSpan.Zero) start = TimeSpan.Zero;

        return new VoiceClip(start, start + clip.Duration);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
