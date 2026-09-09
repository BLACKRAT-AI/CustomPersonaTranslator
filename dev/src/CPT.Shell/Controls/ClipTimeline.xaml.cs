using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using CPT.Core.Media;

namespace CPT.Shell.Controls;

/// <summary>
/// The scrubber the user tags on: the whole video as one strip, the marked
/// sections highlighted on it, and a playhead showing where the video is.
///
/// It is directly manipulable. Dragging anywhere empty scrubs the video;
/// dragging a marked section's edge moves that edge; dragging its middle slides
/// the whole section. Marking by ear is never precise the first time, so being
/// able to nudge a boundary afterwards matters more than the marking itself.
///
/// Everything is drawn from code rather than bound, because each shape's
/// position depends on two things WPF cannot bind together -- the media duration
/// and the control's live pixel width.
/// </summary>
public partial class ClipTimeline : UserControl
{
    /// <summary>How close to an edge counts as grabbing it.</summary>
    private const double EdgeGrabPixels = 6;

    /// <summary>Movement below this is a click, not a drag.</summary>
    private const double DragThresholdPixels = 3;

    private static readonly Brush ClipFill = Frozen(0x2C, 0xB5, 0xD0, 0x66);
    private static readonly Brush ClipEdge = Frozen(0x6F, 0xC2, 0xD6, 0xFF);
    private static readonly Brush ClipHandle = Frozen(0x9F, 0xE2, 0xF0, 0xFF);
    private static readonly Brush PendingFill = Frozen(0xE8, 0xB3, 0x39, 0x4D);
    private static readonly Brush PendingEdge = Frozen(0xE8, 0xB3, 0x39, 0xFF);
    private static readonly Brush PlayheadFill = Frozen(0xFF, 0xFF, 0xFF, 0xE6);
    private static readonly Brush HoverFill = Frozen(0xFF, 0xFF, 0xFF, 0x33);
    private static readonly Brush TickFill = Frozen(0x55, 0x55, 0x55, 0xFF);
    private static readonly Brush LabelFill = Frozen(0x8C, 0x8C, 0x8C, 0xFF);

    private readonly List<VoiceClip> _clips = [];
    private TimeSpan _duration;
    private TimeSpan _position;
    private TimeSpan? _pendingStart;
    private double _hoverX = -1;
    private int _hoverClip = -1;
    private string? _hoverLabel;

    private TimelineGrab _drag = TimelineGrab.None;
    private int _dragClip = -1;
    private double _dragStartX;
    private TimeSpan _dragGrabOffset;
    private bool _dragMoved;

    public ClipTimeline() => InitializeComponent();

    /// <summary>Raised when the user seeks, by clicking or by scrubbing.</summary>
    public event Action<TimeSpan>? SeekRequested;

    /// <summary>Raised when a drag changed the marked sections.</summary>
    public event Action<IReadOnlyList<VoiceClip>>? ClipsEdited;

    /// <summary>Total length of the media. Setting it redraws the strip.</summary>
    public TimeSpan Duration
    {
        get => _duration;
        set { _duration = value; Redraw(); }
    }

    /// <summary>Where the playhead sits.</summary>
    public TimeSpan Position
    {
        get => _position;
        set { _position = value; Redraw(); }
    }

    /// <summary>
    /// Start of a mark the user has opened but not yet closed, drawn differently
    /// so it is obvious that a section is being captured right now.
    /// </summary>
    public TimeSpan? PendingStart
    {
        get => _pendingStart;
        set { _pendingStart = value; Redraw(); }
    }

    /// <summary>
    /// Replaces the marked sections. Ignored mid-drag, where this control is the
    /// one holding the authoritative version.
    /// </summary>
    public void SetClips(IEnumerable<VoiceClip> clips)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (_drag is TimelineGrab.ResizeStart or TimelineGrab.ResizeEnd or TimelineGrab.Move) return;

        _clips.Clear();
        _clips.AddRange(clips);
        Redraw();
    }

    // --- drawing ----------------------------------------------------------

    private void Redraw()
    {
        if (Surface is null) return;

        Surface.Children.Clear();
        var width = Surface.ActualWidth;
        var height = Surface.ActualHeight;
        if (width <= 0 || height <= 0) return;

        DrawTicks(width, height);

        for (var i = 0; i < _clips.Count; i++)
        {
            DrawRegion(_clips[i].Start, _clips[i].End, width, height, ClipFill, ClipEdge);
            if (i == _hoverClip || i == _dragClip) DrawHandles(_clips[i], width, height);
        }

        if (_pendingStart is { } pending)
        {
            var end = _position > pending ? _position : pending;
            DrawRegion(pending, end, width, height, PendingFill, PendingEdge);
        }

        if (_hoverX >= 0 && _drag == TimelineGrab.None) DrawHoverMarker(height);

        DrawPlayhead(width, height);
    }

    /// <summary>A minute grid, so a long video does not read as an undifferentiated bar.</summary>
    private void DrawTicks(double width, double height)
    {
        if (_duration <= TimeSpan.Zero) return;

        var step = ChooseTickStep(_duration);
        for (var at = step; at < _duration; at += step)
        {
            var x = XFor(at, width);
            Add(new Rectangle { Width = 1, Height = height, Fill = TickFill }, x, 0);
            Add(new TextBlock
            {
                Text = VoiceClip.Format(at),
                Foreground = LabelFill,
                FontSize = 9,
            }, x + 3, height - 14);
        }
    }

    /// <summary>Picks a grid interval that yields roughly six to twelve ticks.</summary>
    internal static TimeSpan ChooseTickStep(TimeSpan duration)
    {
        ReadOnlySpan<int> candidateSeconds = [5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
        foreach (var seconds in candidateSeconds)
        {
            if (duration.TotalSeconds / seconds <= 12) return TimeSpan.FromSeconds(seconds);
        }
        return TimeSpan.FromHours(1);
    }

    private void DrawRegion(TimeSpan start, TimeSpan end, double width, double height, Brush fill, Brush edge)
    {
        if (_duration <= TimeSpan.Zero || end <= start) return;

        var left = XFor(start, width);
        var right = XFor(end, width);

        Add(new Rectangle
        {
            Width = Math.Max(2, right - left),
            Height = height,
            Fill = fill,
            Stroke = edge,
            StrokeThickness = 1,
        }, left, 0);
    }

    /// <summary>Grab bars on a section's edges, so it is visible that they move.</summary>
    private void DrawHandles(VoiceClip clip, double width, double height)
    {
        var inset = Math.Min(6, height / 4);
        foreach (var x in new[] { XFor(clip.Start, width), XFor(clip.End, width) })
        {
            Add(new Rectangle
            {
                Width = 3,
                Height = Math.Max(4, height - inset * 2),
                Fill = ClipHandle,
                RadiusX = 1.5,
                RadiusY = 1.5,
            }, x - 1.5, inset);
        }
    }

    /// <summary>The hover line and the time under it, drawn on the strip.</summary>
    private void DrawHoverMarker(double height)
    {
        Add(new Rectangle { Width = 1, Height = height, Fill = HoverFill }, _hoverX, 0);
        if (_hoverLabel is null) return;

        // Flip the label to the left near the right edge so it stays readable.
        var toLeft = _hoverX > Surface.ActualWidth - 40;
        Add(new TextBlock
        {
            Text = _hoverLabel,
            Foreground = PlayheadFill,
            FontSize = 10,
        }, toLeft ? _hoverX - 34 : _hoverX + 4, 2);
    }

    private void DrawPlayhead(double width, double height)
    {
        if (_duration <= TimeSpan.Zero) return;
        Add(new Rectangle { Width = 2, Height = height, Fill = PlayheadFill }, XFor(_position, width) - 1, 0);
    }

    private double XFor(TimeSpan at, double width) => TimelineMath.XFor(at, _duration, width);

    private TimeSpan TimeFor(double x) => TimelineMath.TimeFor(x, _duration, Surface.ActualWidth);

    private void Add(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        Surface.Children.Add(element);
    }

    // --- hit testing ------------------------------------------------------

    /// <summary>What a press at this x would grab.</summary>
    private TimelineHit HitTest(double x) =>
        TimelineMath.HitTest(x, _duration, Surface.ActualWidth, _clips, EdgeGrabPixels);

    // --- interaction ------------------------------------------------------

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void OnSurfaceMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_duration <= TimeSpan.Zero) return;

        var x = e.GetPosition(InputSurface).X;
        var hit = HitTest(x);
        _drag = hit.Grab;
        _dragClip = hit.ClipIndex;
        _dragStartX = x;
        _dragMoved = false;

        if (_drag == TimelineGrab.Move)
            _dragGrabOffset = TimeFor(x) - _clips[_dragClip].Start;

        if (_drag == TimelineGrab.Scrub) SeekRequested?.Invoke(TimeFor(x));

        InputSurface.CaptureMouse();
        Redraw();
        e.Handled = true;
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        var x = e.GetPosition(InputSurface).X;

        if (_drag == TimelineGrab.None)
        {
            _hoverX = x;
            UpdateHoverFeedback(x);
            Redraw();
            return;
        }

        if (Math.Abs(x - _dragStartX) > DragThresholdPixels) _dragMoved = true;

        switch (_drag)
        {
            case TimelineGrab.Scrub:
                SeekRequested?.Invoke(TimeFor(x));
                break;

            case TimelineGrab.ResizeStart:
            case TimelineGrab.ResizeEnd:
                ResizeClip(x);
                break;

            case TimelineGrab.Move:
                if (_dragMoved) MoveClip(x);
                break;
        }
        Redraw();
    }

    private void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag == TimelineGrab.None) return;

        var x = e.GetPosition(InputSurface).X;
        var wasEditing = _drag is TimelineGrab.ResizeStart or TimelineGrab.ResizeEnd or TimelineGrab.Move;

        // A press inside a section without movement is still a seek: the marked
        // parts are where you most want to jump to.
        var seekInstead = !_dragMoved && _drag == TimelineGrab.Move;

        _drag = TimelineGrab.None;
        _dragClip = -1;
        InputSurface.ReleaseMouseCapture();

        if (seekInstead) SeekRequested?.Invoke(TimeFor(x));
        else if (wasEditing) ClipsEdited?.Invoke(VoiceClips.Normalize(_clips, _duration));

        UpdateHoverFeedback(x);
        Redraw();
        e.Handled = true;
    }

    private void ResizeClip(double x) =>
        _clips[_dragClip] = TimelineMath.Resize(
            _clips[_dragClip], TimeFor(x), movingStart: _drag == TimelineGrab.ResizeStart);

    private void MoveClip(double x) =>
        _clips[_dragClip] = TimelineMath.Slide(_clips[_dragClip], TimeFor(x) - _dragGrabOffset, _duration);

    private void UpdateHoverFeedback(double x)
    {
        var hit = HitTest(x);
        var kind = hit.Grab;
        _hoverClip = kind is TimelineGrab.ResizeStart or TimelineGrab.ResizeEnd or TimelineGrab.Move ? hit.ClipIndex : -1;

        InputSurface.Cursor = kind switch
        {
            TimelineGrab.ResizeStart or TimelineGrab.ResizeEnd => Cursors.SizeWE,
            TimelineGrab.Move => Cursors.ScrollWE,
            _ => Cursors.Hand,
        };

        // Deliberately no ToolTip. WPF places one under the pointer, and that
        // popup then swallows the next press -- which made the strip look
        // completely dead: hover, then click, and nothing happened. The hovered
        // time is drawn on the strip itself instead, where it cannot intercept
        // anything.
        _hoverLabel = _duration <= TimeSpan.Zero ? null : VoiceClip.Format(TimeFor(x));
    }

    private void OnSurfaceMouseLeave(object sender, MouseEventArgs e)
    {
        if (_drag != TimelineGrab.None) return;

        _hoverX = -1;
        _hoverClip = -1;
        _hoverLabel = null;
        Redraw();
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b, byte a)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
