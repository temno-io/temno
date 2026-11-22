using System.Globalization;

namespace Temno.Core.Editing;

/// <summary>
/// Parses Adobe .cube 3D LUTs (Mac: LUTLoader.swift) and samples via trilinear interpolation.
/// </summary>
public sealed class CubeLut
{
    public int Dimension { get; }
    /// <summary>RGB triples, size dim³×3, blue-slowest / red-fastest (common .cube order).</summary>
    public float[] Rgb { get; }

    public CubeLut(int dimension, float[] rgb)
    {
        Dimension = dimension;
        Rgb = rgb;
    }

    public static CubeLut? TryLoadFile(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            return TryParse(text);
        }
        catch
        {
            return null;
        }
    }

    public static CubeLut? TryParse(string text)
    {
        var dimension = 0;
        var domainMin = new[] { 0f, 0f, 0f };
        var domainMax = new[] { 1f, 1f, 1f };
        var values = new List<float>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;
            switch (parts[0].ToUpperInvariant())
            {
                case "TITLE":
                    break;
                case "LUT_1D_SIZE":
                    return null;
                case "LUT_3D_SIZE":
                    if (parts.Length >= 2 && int.TryParse(parts[^1], out var d))
                        dimension = d;
                    break;
                case "DOMAIN_MIN":
                    if (!TryParseDomain(parts, out domainMin)) return null;
                    break;
                case "DOMAIN_MAX":
                    if (!TryParseDomain(parts, out domainMax)) return null;
                    break;
                default:
                    if (parts.Length < 3) continue;
                    if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)
                        || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var g)
                        || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
                        || !float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b))
                        return null;
                    values.Add(r);
                    values.Add(g);
                    values.Add(b);
                    break;
            }
        }

        if (dimension is <= 1 or > 128) return null;
        var expected = dimension * dimension * dimension * 3;
        if (values.Count != expected) return null;
        if (domainMin.Length != 3 || domainMax.Length != 3) return null;

        // Reject zero or reversed channel spans; do not invent a floor that posterises the grade.
        for (var c = 0; c < 3; c++)
        {
            if (!(domainMax[c] > domainMin[c])) return null;
        }

        // Normalize domain into 0–1
        for (var i = 0; i < values.Count; i++)
        {
            var c = i % 3;
            var span = domainMax[c] - domainMin[c];
            values[i] = Math.Clamp((values[i] - domainMin[c]) / span, 0f, 1f);
        }

        return new CubeLut(dimension, values.ToArray());
    }

    /// <summary>Trilinear sample. Input/output RGB in 0–1.</summary>
    public void Apply(ref double red, ref double green, ref double blue, double intensity = 1)
    {
        intensity = Math.Clamp(intensity, 0, 1);
        if (intensity <= 0) return;

        Sample(red, green, blue, out var or, out var og, out var ob);
        if (intensity >= 1)
        {
            red = or;
            green = og;
            blue = ob;
            return;
        }
        red = red + (or - red) * intensity;
        green = green + (og - green) * intensity;
        blue = blue + (ob - blue) * intensity;
    }

    public void Sample(double r, double g, double b, out double or, out double og, out double ob)
    {
        var dim = Dimension;
        var max = dim - 1;
        r = Math.Clamp(r, 0, 1) * max;
        g = Math.Clamp(g, 0, 1) * max;
        b = Math.Clamp(b, 0, 1) * max;

        var r0 = (int)Math.Floor(r);
        var g0 = (int)Math.Floor(g);
        var b0 = (int)Math.Floor(b);
        var r1 = Math.Min(r0 + 1, max);
        var g1 = Math.Min(g0 + 1, max);
        var b1 = Math.Min(b0 + 1, max);
        var tr = r - r0;
        var tg = g - g0;
        var tb = b - b0;

        // .cube order: red fastest, then green, then blue
        Get(r0, g0, b0, out var c000r, out var c000g, out var c000b);
        Get(r1, g0, b0, out var c100r, out var c100g, out var c100b);
        Get(r0, g1, b0, out var c010r, out var c010g, out var c010b);
        Get(r1, g1, b0, out var c110r, out var c110g, out var c110b);
        Get(r0, g0, b1, out var c001r, out var c001g, out var c001b);
        Get(r1, g0, b1, out var c101r, out var c101g, out var c101b);
        Get(r0, g1, b1, out var c011r, out var c011g, out var c011b);
        Get(r1, g1, b1, out var c111r, out var c111g, out var c111b);

        static double Lerp(double a, double b, double t) => a + (b - a) * t;

        var c00r = Lerp(c000r, c100r, tr);
        var c00g = Lerp(c000g, c100g, tr);
        var c00b = Lerp(c000b, c100b, tr);
        var c01r = Lerp(c001r, c101r, tr);
        var c01g = Lerp(c001g, c101g, tr);
        var c01b = Lerp(c001b, c101b, tr);
        var c10r = Lerp(c010r, c110r, tr);
        var c10g = Lerp(c010g, c110g, tr);
        var c10b = Lerp(c010b, c110b, tr);
        var c11r = Lerp(c011r, c111r, tr);
        var c11g = Lerp(c011g, c111g, tr);
        var c11b = Lerp(c011b, c111b, tr);

        var c0r = Lerp(c00r, c10r, tg);
        var c0g = Lerp(c00g, c10g, tg);
        var c0b = Lerp(c00b, c10b, tg);
        var c1r = Lerp(c01r, c11r, tg);
        var c1g = Lerp(c01g, c11g, tg);
        var c1b = Lerp(c01b, c11b, tg);

        or = Lerp(c0r, c1r, tb);
        og = Lerp(c0g, c1g, tb);
        ob = Lerp(c0b, c1b, tb);
    }

    private void Get(int ri, int gi, int bi, out double r, out double g, out double b)
    {
        var idx = ((bi * Dimension + gi) * Dimension + ri) * 3;
        r = Rgb[idx];
        g = Rgb[idx + 1];
        b = Rgb[idx + 2];
    }

    /// <summary>
    /// Requires exactly three finite components. Truncated or padded domains used to corrupt grades.
    /// Absent DOMAIN_* directives keep the 0–1 defaults in <see cref="TryParse"/>.
    /// </summary>
    private static bool TryParseDomain(string[] parts, out float[] domain)
    {
        domain = Array.Empty<float>();
        if (parts.Length != 4) return false;
        var vals = new float[3];
        for (var i = 0; i < 3; i++)
        {
            if (!float.TryParse(parts[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out vals[i])
                || !float.IsFinite(vals[i]))
                return false;
        }
        domain = vals;
        return true;
    }
}
