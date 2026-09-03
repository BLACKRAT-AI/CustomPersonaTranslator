using System;
using System.Collections.Generic;
using System.Linq;

namespace CPT.Core.Media;

/// <summary>
/// A stretch of a recording the user marked as being the voice they want cloned.
/// </summary>
/// <param name="Start">Offset from the beginning of the recording.</param>
/// <param name="End">Offset at which the clip stops. Always after <paramref name="Start"/>.</param>
public readonly record struct VoiceClip(TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;

    /// <summary>Formatted as m:ss – m:ss, for the clip list.</summary>
    public string Range => $"{Format(Start)} – {Format(End)}";

    public static string Format(TimeSpan value) =>
        value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
}

/// <summary>
/// Tidying rules for a set of marked clips.
///
/// Marking is done by ear against a moving playhead, so the raw set is never
/// clean: clips arrive out of order, overlap where the user re-marked a passage,
/// and sometimes collapse to nothing from a double-tap. Normalising here means
/// the extractor, the timeline and the total-duration readout all agree.
/// </summary>
public static class VoiceClips
{
    /// <summary>Anything shorter than this was a slip of the finger, not a clip.</summary>
    public static TimeSpan MinimumClipLength { get; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Clips closer together than this are joined rather than left as a seam.</summary>
    public static TimeSpan JoinGap { get; } = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Returns the clips sorted, clamped to the recording, with too-short ones
    /// dropped and overlapping or near-touching ones merged.
    /// </summary>
    public static IReadOnlyList<VoiceClip> Normalize(IEnumerable<VoiceClip> clips, TimeSpan mediaDuration)
    {
        ArgumentNullException.ThrowIfNull(clips);

        var ordered = clips
            .Select(c => Clamp(c, mediaDuration))
            .Where(c => c.Duration >= MinimumClipLength)
            .OrderBy(c => c.Start)
            .ToList();

        var merged = new List<VoiceClip>(ordered.Count);
        foreach (var clip in ordered)
        {
            if (merged.Count > 0 && clip.Start - merged[^1].End <= JoinGap)
            {
                var previous = merged[^1];
                merged[^1] = previous with { End = Max(previous.End, clip.End) };
                continue;
            }
            merged.Add(clip);
        }
        return merged;
    }

    /// <summary>Total marked time, after normalising.</summary>
    public static TimeSpan TotalDuration(IEnumerable<VoiceClip> clips, TimeSpan mediaDuration) =>
        Normalize(clips, mediaDuration).Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Duration);

    private static VoiceClip Clamp(VoiceClip clip, TimeSpan mediaDuration)
    {
        var start = clip.Start < TimeSpan.Zero ? TimeSpan.Zero : clip.Start;
        var end = clip.End;

        if (mediaDuration > TimeSpan.Zero)
        {
            if (start > mediaDuration) start = mediaDuration;
            if (end > mediaDuration) end = mediaDuration;
        }
        return end > start ? new VoiceClip(start, end) : new VoiceClip(start, start);
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
