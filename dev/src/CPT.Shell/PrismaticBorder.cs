using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace CPT.Shell;

/// <summary>
/// SYNTAX's prismatic ring, around the panel's perimeter.
///
/// A direct port of SyntaxEngine's <c>platform/web/player/prismatic.js</c>, and
/// the behaviour that matters is carried over exactly:
///
///   • IDLE is a calm, static, dim grey outline. No motion, no colour. The panel
///     must never look like it is working when it is not.
///   • Only a real turn lights it, and turning off DISSOLVES the colour back
///     into the grey rather than snapping colourless — the rainbow's alpha is
///     multiplied by energy so it fades all the way out.
///   • Three passes: the grey base, a wide bloom pass, then a crisp core on top.
///     Two passes look like a flat stripe; the bloom is what makes it glow.
///   • Time-based easing and spin, so the frame cap does not slow either.
///   • It parks completely at rest and costs nothing.
///
/// WPF has no conic gradient, so the perimeter is walked as short segments, each
/// stroked with the colour at its own distance around the path. That IS a conic
/// gradient — colour as a function of angle around the shape — and unlike a
/// linear brush it travels around all four sides instead of sweeping across.
/// </summary>
internal sealed class PrismaticBorder : FrameworkElement
{
    /// <summary>
    /// Segments around the perimeter. High enough that the corner arcs read as
    /// arcs: at 160 a 14px corner got two segments and looked chamfered.
    /// </summary>
    private const int Segments = 420;

    /// <summary>Steps in the halo. More, fainter steps read as a glow; few, strong ones as rings.</summary>
    private const int GlowSteps = 9;

    /// <summary>Frame cap. A slowly spinning blurred ring does not need 60fps.</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 18);

    private readonly DropShadowEffect _bloom = new() { ShadowDepth = 0, BlurRadius = 3, Opacity = 0 };

    private string? _colour;
    private double _phase;
    private double _energy;
    private bool _lit;
    private TimeSpan _last;
    private bool _running;

    public PrismaticBorder()
    {
        IsHitTestVisible = false;

        // A single soft blur over the whole element, which spreads the strokes
        // below into a halo while each keeps its own colour.
        Effect = new BlurEffect { Radius = 2.5, KernelType = KernelType.Gaussian };
        Loaded += (_, _) => Kick();
        Unloaded += (_, _) => Stop();
    }

    /// <summary>Corner radius of the panel this traces.</summary>
    public double CornerRadius { get; set; } = 14;

    /// <summary>The persona's colour: "prismatic", or any hex.</summary>
    public void SetPalette(string? colour)
    {
        _colour = colour;
        Kick();
    }

    /// <summary>Lights the ring for a turn, or lets it dissolve back to grey.</summary>
    public void SetLit(bool lit)
    {
        _lit = lit;
        Kick();
    }

    /// <summary>
    /// Steps the animation by hand, for an offscreen render.
    /// <summary>CompositionTarget.Rendering</summary> never fires without a live
    /// window, so a headless check has to supply the frame itself.
    /// </summary>
    internal void AdvanceForTest(double energy, double phase)
    {
        _energy = energy;
        _phase = phase;
        InvalidateVisual();
    }

    private void Kick()
    {
        if (_running) return;
        _running = true;
        _last = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs args) return;

        var now = args.RenderingTime;
        if (_last != TimeSpan.Zero && now - _last < FrameInterval) return;
        var dt = _last == TimeSpan.Zero ? 1.0 / 60 : Math.Min(0.1, (now - _last).TotalSeconds);
        _last = now;

        // Time-based, so the frame cap above does not slow the fade or the spin.
        _energy += ((_lit ? 1.0 : 0.0) - _energy) * Math.Min(1, 4.2 * dt);
        _phase = (_phase + _energy * 0.24 * dt) % 1;

        InvalidateVisual();

        // Park once there is nothing left to animate.
        if (!_lit && _energy < 0.01)
        {
            _energy = 0;
            Stop();
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 8 || h < 8) return;

        var e = _energy;
        var geometry = Perimeter(w, h);

        // The calm grey base is ALWAYS drawn, so the colour above it can fade to
        // nothing without the outline disappearing.
        var grey = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0x3B, 0x3D, 0x44)), 1.6);
        grey.Freeze();
        drawingContext.DrawGeometry(null, grey, geometry);
        if (e < 0.01) return;

        // SYNTAX's own saturation and lightness ramp, unchanged.
        var saturation = 0.12 + e * 0.76;
        var lightness = 0.46 + e * 0.30;

        // The glow is built from the ring itself: wide, faint strokes UNDER the
        // core, each segment carrying its own colour. It used to be a drop
        // shadow on the whole element, which has exactly one colour -- so a
        // spectrum ring cast a single-hue halo that did not follow it.
        // Many thin steps rather than three thick ones: each is nearly
        // invisible on its own and together they fall off smoothly, where three
        // stacked into visible concentric bands.
        for (var step = 0; step < GlowSteps; step++)
        {
            var distance = 1 - (double)step / GlowSteps;          // 1 at the outside
            DrawSweep(drawingContext, w, h, saturation, lightness,
                width: (1.6 + distance * 16) * (0.5 + e * 0.5),
                alpha: 0.10 * e * (1 - distance) * (1 - distance));
        }

        DrawSweep(drawingContext, w, h, saturation, lightness,
            width: 1.4 + e * 1.6, alpha: (0.7 + e * 0.3) * e);
    }

    /// <summary>
    /// One pass of the ring: the perimeter in short segments, each the colour at
    /// its own position around the path, scrolling with the phase.
    ///
    /// The segments are drawn opaque into a group and the pass's transparency is
    /// applied to the GROUP. Applying it per segment instead double-blends every
    /// place two round caps overlap, which beads the ring into a dotted line.
    /// </summary>
    private void DrawSweep(DrawingContext dc, double w, double h, double saturation, double lightness,
                           double width, double alpha)
    {
        if (alpha <= 0.004) return;

        var radius = Math.Min(CornerRadius, Math.Min(w, h) / 2);
        var points = PerimeterPoints(w, h, radius, width / 2 + 0.5);

        var group = new DrawingGroup { Opacity = Math.Min(1, alpha) };
        using (var inner = group.Append())
        {
            for (var i = 0; i < points.Length - 1; i++)
            {
                var position = (double)i / (points.Length - 1);
                var hue = ((position - _phase) % 1 + 1) % 1;

                var brush = new SolidColorBrush(PersonaPalette.At(_colour, hue, saturation, lightness));
                brush.Freeze();
                var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();
                inner.DrawLine(pen, points[i], points[i + 1]);
            }
        }

        group.Freeze();
        dc.DrawDrawing(group);
    }

    private RectangleGeometry Perimeter(double w, double h)
    {
        var radius = Math.Min(CornerRadius, Math.Min(w, h) / 2);
        var geometry = new RectangleGeometry(new Rect(1, 1, w - 2, h - 2), radius, radius);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Points around a rounded rectangle, starting at top centre and going
    /// clockwise, so segment index maps to distance travelled around the panel.
    /// </summary>
    private static Point[] PerimeterPoints(double w, double h, double radius, double inset)
    {
        double left = inset, top = inset, right = w - inset, bottom = h - inset;
        var r = Math.Max(0, Math.Min(radius, Math.Min(right - left, bottom - top) / 2));

        // Straight runs and corner arcs, measured so segments are evenly spaced
        // by DISTANCE rather than by side — otherwise the colour would race
        // along the short edges and crawl along the long ones.
        var sideX = right - left - 2 * r;
        var sideY = bottom - top - 2 * r;
        var arc = Math.PI * r / 2;
        var total = 2 * sideX + 2 * sideY + 4 * arc;
        if (total <= 0) return [new Point(left, top), new Point(right, top)];

        var points = new Point[Segments + 1];
        for (var i = 0; i <= Segments; i++)
        {
            // Start at top centre so the sweep is symmetric about the panel.
            var d = (total * i / Segments + sideX / 2) % total;
            points[i] = PointAt(d, left, top, right, bottom, r, sideX, sideY, arc);
        }
        return points;
    }

    private static Point PointAt(double d, double left, double top, double right, double bottom,
                                 double r, double sideX, double sideY, double arc)
    {
        // Top edge, left to right.
        if (d < sideX) return new Point(left + r + d, top);
        d -= sideX;

        if (d < arc) return Corner(right - r, top + r, -Math.PI / 2, d / arc, r);
        d -= arc;

        if (d < sideY) return new Point(right, top + r + d);
        d -= sideY;

        if (d < arc) return Corner(right - r, bottom - r, 0, d / arc, r);
        d -= arc;

        if (d < sideX) return new Point(right - r - d, bottom);
        d -= sideX;

        if (d < arc) return Corner(left + r, bottom - r, Math.PI / 2, d / arc, r);
        d -= arc;

        if (d < sideY) return new Point(left, bottom - r - d);
        d -= sideY;

        return Corner(left + r, top + r, Math.PI, Math.Min(1, d / arc), r);
    }

    private static Point Corner(double cx, double cy, double startAngle, double fraction, double r)
    {
        var angle = startAngle + fraction * Math.PI / 2;
        return new Point(cx + Math.Cos(angle) * r, cy + Math.Sin(angle) * r);
    }
}
