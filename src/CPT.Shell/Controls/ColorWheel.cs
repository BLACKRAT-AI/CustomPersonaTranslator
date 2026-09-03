using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CPT.Shell.Controls;

/// <summary>
/// An HSV colour wheel: hue around, saturation outward, with a lightness slider
/// beside it.
///
/// A hex box alone is a fine way to type a colour you already know and a poor
/// way to choose one. The wheel is drawn once into a bitmap and only re-drawn
/// when the control is resized, so dragging around it costs nothing.
/// </summary>
public sealed class ColorWheel : Control
{
    private WriteableBitmap? _wheel;
    private double _wheelLightness = -1;
    private Point _centre;
    private double _radius;

    public static readonly DependencyProperty SelectedColorProperty =
        DependencyProperty.Register(
            nameof(SelectedColor), typeof(Color), typeof(ColorWheel),
            new FrameworkPropertyMetadata(Colors.Cyan,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedColorChanged));

    /// <summary>The chosen colour.</summary>
    public Color SelectedColor
    {
        get => (Color)GetValue(SelectedColorProperty);
        set => SetValue(SelectedColorProperty, value);
    }

    /// <summary>Raised whenever the user moves the marker or the slider.</summary>
    public event Action<Color>? ColorPicked;

    static ColorWheel()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(ColorWheel), new FrameworkPropertyMetadata(typeof(ColorWheel)));
    }

    public ColorWheel()
    {
        Focusable = false;
        MinHeight = 150;
    }

    private static void OnSelectedColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ColorWheel)d).InvalidateVisual();

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _wheel = null;                       // rebuilt at the new size on the next draw
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        PickAt(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton == MouseButtonState.Pressed && IsMouseCaptured) PickAt(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        ReleaseMouseCapture();
    }

    /// <summary>Reads hue and saturation off the wheel, keeping the current lightness.</summary>
    private void PickAt(Point point)
    {
        if (_radius <= 0) return;

        var dx = point.X - _centre.X;
        var dy = point.Y - _centre.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        // Outside the wheel still picks: clamping to the rim is far less
        // annoying than a drag that stops responding at the edge.
        var saturation = Math.Min(1, distance / _radius);
        var hue = ((Math.Atan2(dy, dx) / (Math.PI * 2)) + 1) % 1;

        var (_, _, lightness) = ToHsl(SelectedColor);
        SelectedColor = FromHsl(hue, saturation, lightness <= 0.02 || lightness >= 0.98 ? 0.5 : lightness);
        ColorPicked?.Invoke(SelectedColor);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 8) return;

        _radius = size / 2 - 2;
        _centre = new Point(ActualWidth / 2, size / 2);

        var (hue, saturation, lightness) = ToHsl(SelectedColor);
        if (_wheel is null || Math.Abs(_wheelLightness - lightness) > 0.02) BuildWheel(size, lightness);

        drawingContext.DrawImage(_wheel, new Rect(_centre.X - _radius - 2, 0, size, size));

        // The marker: a white ring with a dark one behind it, so it is visible
        // on both a pale and a saturated part of the wheel.
        var angle = hue * Math.PI * 2;
        var marker = new Point(
            _centre.X + Math.Cos(angle) * saturation * _radius,
            _centre.Y + Math.Sin(angle) * saturation * _radius);

        drawingContext.DrawEllipse(null, new Pen(Brushes.Black, 3), marker, 6, 6);
        drawingContext.DrawEllipse(null, new Pen(Brushes.White, 1.6), marker, 6, 6);
    }

    /// <summary>
    /// Paints the wheel into a bitmap at the current lightness. Redrawing this
    /// per frame while dragging would be visibly slow; it only changes when the
    /// lightness does.
    /// </summary>
    private void BuildWheel(double size, double lightness)
    {
        var pixels = Math.Max(8, (int)size);
        var bitmap = new WriteableBitmap(pixels, pixels, 96, 96, PixelFormats.Bgra32, null);
        var buffer = new byte[pixels * pixels * 4];
        var radius = pixels / 2.0 - 2;

        for (var y = 0; y < pixels; y++)
        {
            for (var x = 0; x < pixels; x++)
            {
                var dx = x - pixels / 2.0;
                var dy = y - pixels / 2.0;
                var distance = Math.Sqrt(dx * dx + dy * dy);
                var index = (y * pixels + x) * 4;

                if (distance > radius)
                {
                    buffer[index + 3] = 0;                       // outside the disc: transparent
                    continue;
                }

                var hue = ((Math.Atan2(dy, dx) / (Math.PI * 2)) + 1) % 1;
                var colour = FromHsl(hue, distance / radius, lightness);

                buffer[index + 0] = colour.B;
                buffer[index + 1] = colour.G;
                buffer[index + 2] = colour.R;
                // Feather the last pixel of the rim, or the disc has a stepped edge.
                buffer[index + 3] = (byte)(255 * Math.Min(1, (radius - distance) / 1.5));
            }
        }

        bitmap.WritePixels(new Int32Rect(0, 0, pixels, pixels), buffer, pixels * 4, 0);
        bitmap.Freeze();

        _wheel = bitmap;
        _wheelLightness = lightness;
    }

    /// <summary>Hue, saturation and lightness of a colour, all 0..1.</summary>
    public static (double Hue, double Saturation, double Lightness) ToHsl(Color colour)
    {
        double r = colour.R / 255.0, g = colour.G / 255.0, b = colour.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var lightness = (max + min) / 2;

        if (delta <= 0) return (0, 0, lightness);

        double hue;
        if (max == r) hue = ((g - b) / delta) % 6;
        else if (max == g) hue = (b - r) / delta + 2;
        else hue = (r - g) / delta + 4;

        var denominator = 1 - Math.Abs(2 * lightness - 1);
        return ((hue / 6 + 1) % 1, denominator <= 0 ? 0 : delta / denominator, lightness);
    }

    /// <summary>A colour from hue, saturation and lightness, all 0..1.</summary>
    public static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 1) + 1) % 1;
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h * 6 % 2 - 1));
        var m = l - c / 2;

        var (r, g, b) = (h * 6) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    private static byte Byte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
}
