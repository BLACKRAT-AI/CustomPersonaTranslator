using System;

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


    /// <summary>
    /// The colour at position <paramref name="p"/> around the sweep, with the
    /// saturation and lightness the caller would have used for the prismatic
    /// version. Mirrors <c>palette.js</c>'s <c>at()</c> exactly, so the ring and
    /// the projection inside it are never running two different palettes.
    /// </summary>
    public static Color At(string? colour, double p, double saturation, double lightness)
    {
        if (IsPrismatic(colour)) return FromHsl(p, saturation, lightness);

        var (hue, baseSaturation) = HueOf(colour);
        var wave = Math.Sin(p * Math.PI * 2);
        return FromHsl(
            hue + wave * TintHueSpread,
            Math.Min(1, saturation * 0.35 + baseSaturation * 0.65),
            Math.Clamp(lightness + wave * TintLightSpread, 0.08, 0.96));
    }

    /// <summary>True when the persona wants the full spectrum rather than one colour.</summary>
    public static bool IsPrismatic(string? colour) =>
        string.IsNullOrWhiteSpace(colour) || colour.Trim().Equals("prismatic", StringComparison.OrdinalIgnoreCase);


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
