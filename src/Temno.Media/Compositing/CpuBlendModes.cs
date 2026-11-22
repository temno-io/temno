namespace Temno.Media.Compositing;

/// <summary>
/// Straight-RGB blend functions used by <see cref="CpuCompositor"/>. The names and the
/// non-separable hue/saturation/color/luminosity rules follow the Mac Core Image contract in
/// Models/BlendMode.swift and Compositing/FrameRenderer.swift.
/// </summary>
internal static class CpuBlendModes
{
    public const string Normal = "normal";

    /// <summary>
    /// Resolved blend mode. The compositor resolves the stored string ONCE per layer and switches on
    /// this inside the pixel loop.
    /// <para>
    /// Previously the loop called <see cref="Apply(string?, double, double, double, double, double, double)"/>
    /// per pixel, which ran <c>mode?.Trim()</c> — a string allocation — plus a sixteen-arm string
    /// switch, two million times per 1080p frame. That, with the identity resample below it, was
    /// ~87% of export time.
    /// </para>
    /// </summary>
    public enum Kind
    {
        Normal, Darken, Multiply, ColorBurn, Lighten, Screen, ColorDodge, Overlay,
        SoftLight, HardLight, Difference, Exclusion, Hue, Saturation, Color, Luminosity, Add,
    }

    public static bool IsNormal(string? mode) => Normalize(mode) == Normal;

    public static string Normalize(string? mode) => mode?.Trim() switch
    {
        "darken" or "multiply" or "colorBurn" or "lighten" or "screen" or "colorDodge"
            or "overlay" or "softLight" or "hardLight" or "difference" or "exclusion"
            or "hue" or "saturation" or "color" or "luminosity" or "add" => mode!.Trim(),
        _ => Normal,
    };

    /// <summary>Resolves the stored mode string to <see cref="Kind"/>. Call once per layer.</summary>
    public static Kind Parse(string? mode) => mode?.Trim() switch
    {
        "darken" => Kind.Darken,
        "multiply" => Kind.Multiply,
        "colorBurn" => Kind.ColorBurn,
        "lighten" => Kind.Lighten,
        "screen" => Kind.Screen,
        "colorDodge" => Kind.ColorDodge,
        "overlay" => Kind.Overlay,
        "softLight" => Kind.SoftLight,
        "hardLight" => Kind.HardLight,
        "difference" => Kind.Difference,
        "exclusion" => Kind.Exclusion,
        "hue" => Kind.Hue,
        "saturation" => Kind.Saturation,
        "color" => Kind.Color,
        "luminosity" => Kind.Luminosity,
        "add" => Kind.Add,
        _ => Kind.Normal,
    };

    public static (double R, double G, double B) Apply(
        string? mode,
        double backgroundR,
        double backgroundG,
        double backgroundB,
        double sourceR,
        double sourceG,
        double sourceB)
        => Apply(Parse(mode), backgroundR, backgroundG, backgroundB, sourceR, sourceG, sourceB);

    public static (double R, double G, double B) Apply(
        Kind mode,
        double backgroundR,
        double backgroundG,
        double backgroundB,
        double sourceR,
        double sourceG,
        double sourceB)
    {
        if (mode == Kind.Normal) return (sourceR, sourceG, sourceB);

        return mode switch
        {
            Kind.Darken => (Math.Min(backgroundR, sourceR), Math.Min(backgroundG, sourceG), Math.Min(backgroundB, sourceB)),
            Kind.Multiply => (backgroundR * sourceR, backgroundG * sourceG, backgroundB * sourceB),
            Kind.ColorBurn => (ColorBurn(backgroundR, sourceR), ColorBurn(backgroundG, sourceG), ColorBurn(backgroundB, sourceB)),
            Kind.Lighten => (Math.Max(backgroundR, sourceR), Math.Max(backgroundG, sourceG), Math.Max(backgroundB, sourceB)),
            Kind.Screen => (Screen(backgroundR, sourceR), Screen(backgroundG, sourceG), Screen(backgroundB, sourceB)),
            Kind.ColorDodge => (ColorDodge(backgroundR, sourceR), ColorDodge(backgroundG, sourceG), ColorDodge(backgroundB, sourceB)),
            Kind.Overlay => (Overlay(backgroundR, sourceR), Overlay(backgroundG, sourceG), Overlay(backgroundB, sourceB)),
            Kind.SoftLight => (SoftLight(backgroundR, sourceR), SoftLight(backgroundG, sourceG), SoftLight(backgroundB, sourceB)),
            Kind.HardLight => (Overlay(sourceR, backgroundR), Overlay(sourceG, backgroundG), Overlay(sourceB, backgroundB)),
            Kind.Difference => (Math.Abs(backgroundR - sourceR), Math.Abs(backgroundG - sourceG), Math.Abs(backgroundB - sourceB)),
            Kind.Exclusion => (Exclusion(backgroundR, sourceR), Exclusion(backgroundG, sourceG), Exclusion(backgroundB, sourceB)),
            Kind.Hue => SetLum(SetSat((sourceR, sourceG, sourceB), Sat(backgroundR, backgroundG, backgroundB)), Lum(backgroundR, backgroundG, backgroundB)),
            Kind.Saturation => SetLum(SetSat((backgroundR, backgroundG, backgroundB), Sat(sourceR, sourceG, sourceB)), Lum(backgroundR, backgroundG, backgroundB)),
            Kind.Color => SetLum((sourceR, sourceG, sourceB), Lum(backgroundR, backgroundG, backgroundB)),
            Kind.Luminosity => SetLum((backgroundR, backgroundG, backgroundB), Lum(sourceR, sourceG, sourceB)),
            // Existing Windows Inspector option. It is intentionally not treated as a Mac parity
            // claim, but rendering it avoids a stored-only mode in Windows-authored projects.
            Kind.Add => (Math.Min(1, backgroundR + sourceR), Math.Min(1, backgroundG + sourceG), Math.Min(1, backgroundB + sourceB)),
            _ => (sourceR, sourceG, sourceB),
        };
    }

    private static double Screen(double background, double source) => 1 - (1 - background) * (1 - source);
    private static double ColorDodge(double background, double source) => source >= 1 ? 1 : Math.Min(1, background / (1 - source));
    private static double ColorBurn(double background, double source) => source <= 0 ? 0 : 1 - Math.Min(1, (1 - background) / source);
    private static double Overlay(double background, double source) => background <= 0.5 ? 2 * background * source : 1 - 2 * (1 - background) * (1 - source);
    private static double Exclusion(double background, double source) => background + source - 2 * background * source;

    private static double SoftLight(double background, double source)
    {
        if (source <= 0.5)
            return background - (1 - 2 * source) * background * (1 - background);

        var d = background <= 0.25
            ? ((16 * background - 12) * background + 4) * background
            : Math.Sqrt(background);
        return background + (2 * source - 1) * (d - background);
    }

    private static double Lum(double r, double g, double b) => 0.3 * r + 0.59 * g + 0.11 * b;
    private static double Sat(double r, double g, double b) => Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));

    private static (double R, double G, double B) SetLum((double R, double G, double B) color, double lum)
    {
        var delta = lum - Lum(color.R, color.G, color.B);
        return ClipColor((color.R + delta, color.G + delta, color.B + delta));
    }

    private static (double R, double G, double B) ClipColor((double R, double G, double B) color)
    {
        var lum = Lum(color.R, color.G, color.B);
        var min = Math.Min(color.R, Math.Min(color.G, color.B));
        var max = Math.Max(color.R, Math.Max(color.G, color.B));
        if (min < 0)
            color = (lum + ((color.R - lum) * lum) / (lum - min), lum + ((color.G - lum) * lum) / (lum - min), lum + ((color.B - lum) * lum) / (lum - min));
        if (max > 1)
            color = (lum + ((color.R - lum) * (1 - lum)) / (max - lum), lum + ((color.G - lum) * (1 - lum)) / (max - lum), lum + ((color.B - lum) * (1 - lum)) / (max - lum));
        return color;
    }

    private static (double R, double G, double B) SetSat((double R, double G, double B) color, double saturation)
    {
        var values = new[] { color.R, color.G, color.B };

        // Pick distinct extremes. Array.IndexOf(min) and Array.IndexOf(max) both return 0 for an
        // achromatic pixel (R == G == B), which made midIndex = 3 - 0 - 0 = 3 and threw
        // IndexOutOfRangeException — so `hue` and `saturation` blend crashed the render on the first
        // grey, black or white pixel it met.
        var minIndex = 0;
        var maxIndex = 0;
        for (var i = 1; i < 3; i++)
        {
            if (values[i] < values[minIndex]) minIndex = i;
            if (values[i] > values[maxIndex]) maxIndex = i;
        }

        // All three equal: saturation is zero by definition, and there is no distinct mid channel.
        if (minIndex == maxIndex) return (0, 0, 0);

        var midIndex = 3 - minIndex - maxIndex;
        var min = values[minIndex];
        var max = values[maxIndex];

        if (max > min)
        {
            values[midIndex] = ((values[midIndex] - min) * saturation) / (max - min);
            values[maxIndex] = saturation;
        }
        else
        {
            values[midIndex] = 0;
            values[maxIndex] = 0;
        }
        values[minIndex] = 0;
        return (values[0], values[1], values[2]);
    }
}
