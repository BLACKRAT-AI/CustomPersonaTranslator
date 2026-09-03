using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace CPT.Shell;

/// <summary>
/// The persona's colour, as WPF brushes.
///
/// This is the C# half of the same rule the hologram page follows, and the two
/// have to agree or the bar's border and the projection above it would be
/// running different palettes:
///
///   • "prismatic" is the full spectrum.
///   • Any other colour is never drawn flat. A single hue makes a moving
///     gradient look like a static band, so a chosen colour is given a narrow
///     hue spread and a lightness sweep around itself -- still recognisably
///     that colour, but you can see it travel.
/// </summary>
internal static class PersonaPalette
{
    /// <summary>Hue drift either side of a chosen colour, in turns (about ±22°).</summary>
    private const double TintHueSpread = 0.06;

    /// <summary>Lightness swing either side, so movement stays legible.</summary>
    private const double TintLightSpread = 0.16;

    /// <summary>Stops around the sweep. Enough to read as continuous, few enough to stay cheap.</summary>
    private const int Steps = 12;

    /// <summary>True when the persona wants the full spectrum rather than one colour.</summary>
    public static bool IsPrismatic(string? colour) =>
        string.IsNullOrWhiteSpace(colour) || colour.Trim().Equals("prismatic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A horizontal gradient that repeats, so animating it sideways sweeps the
    /// colour along whatever it is painting.
    /// </summary>
    public static LinearGradientBrush CreateSweep(string? colour)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(0.5, 0),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            SpreadMethod = GradientSpreadMethod.Repeat,
        };

        foreach (var (offset, stop) in Stops(colour))
            brush.GradientStops.Add(new GradientStop(stop, offset));

        return brush;
    }

    /// <summary>The colour to bloom around the border with — the palette's brightest point.</summary>
    public static Color Bloom(string? colour) =>
        IsPrismatic(colour) ? FromHsl(0.55, 0.85, 0.62) : Sweep(colour, 0.25);

    private static IEnumerable<(double Offset, Color Color)> Stops(string? colour)
    {
        for (var i = 0; i <= Steps; i++)
        {
            var p = (double)i / Steps;
            yield return (p, Sweep(colour, p));
        }
    }

    private static Color Sweep(string? colour, double p)
    {
        if (IsPrismatic(colour)) return FromHsl(p, 0.85, 0.62);

        var (hue, saturation) = HueOf(colour);
        // A full turn of p sweeps once through the narrow band and back, so the
        // colour cycles smoothly instead of jumping at the wrap point.
        var wave = Math.Sin(p * Math.PI * 2);
        return FromHsl(
            hue + wave * TintHueSpread,
            Math.Min(1, 0.30 + saturation * 0.70),
            Math.Clamp(0.62 + wave * TintLightSpread, 0.10, 0.94));
    }

    private static (double Hue, double Saturation) HueOf(string? colour)
    {
        Color rgb;
        try { rgb = (Color)ColorConverter.ConvertFromString(colour); }
        catch (FormatException) { return (0.51, 0.8); }
        catch (NotSupportedException) { return (0.51, 0.8); }

        double r = rgb.R / 255.0, g = rgb.G / 255.0, b = rgb.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        if (delta <= 0) return (0.51, 0);

        double hue;
        if (max == r) hue = ((g - b) / delta) % 6;
        else if (max == g) hue = (b - r) / delta + 2;
        else hue = (r - g) / delta + 4;

        var light = (max + min) / 2;
        var denominator = 1 - Math.Abs(2 * light - 1);
        return ((hue / 6 + 1) % 1, denominator <= 0 ? 1 : delta / denominator);
    }

    private static Color FromHsl(double h, double s, double l)
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
