using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temno.Core.Editing;

public sealed class CurvePoint
{
    [JsonPropertyName("x")]
    public double X { get; set; }

    [JsonPropertyName("y")]
    public double Y { get; set; }

    public CurvePoint() { }
    public CurvePoint(double x, double y)
    {
        X = x;
        Y = y;
    }
}

public sealed class GradeCurve
{
    [JsonPropertyName("master")]
    public List<CurvePoint> Master { get; set; } = new();

    [JsonPropertyName("red")]
    public List<CurvePoint> Red { get; set; } = new();

    [JsonPropertyName("green")]
    public List<CurvePoint> Green { get; set; } = new();

    [JsonPropertyName("blue")]
    public List<CurvePoint> Blue { get; set; } = new();

    private static readonly List<CurvePoint> IdentityPoints = new() { new(0, 0), new(1, 1) };

    [JsonIgnore]
    public bool IsIdentity
    {
        get
        {
            return IsPointsIdentity(Master) &&
                   IsPointsIdentity(Red) &&
                   IsPointsIdentity(Green) &&
                   IsPointsIdentity(Blue);
        }
    }

    private static bool IsPointsIdentity(List<CurvePoint> pts)
    {
        if (pts.Count == 0) return true;
        if (pts.Count != IdentityPoints.Count) return false;
        for (int i = 0; i < pts.Count; i++)
        {
            if (Math.Abs(pts[i].X - IdentityPoints[i].X) > 1e-4 ||
                Math.Abs(pts[i].Y - IdentityPoints[i].Y) > 1e-4)
            {
                return false;
            }
        }
        return true;
    }

    public string? Encoded()
    {
        try
        {
            return JsonSerializer.Serialize(this);
        }
        catch
        {
            return null;
        }
    }

    public static GradeCurve? Decode(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<GradeCurve>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Piecewise-linear sample of a curve. Empty / null points = identity (y = x).
    /// X is clamped to [0, 1]; result is clamped to [0, 1].
    /// </summary>
    public static double Sample(IReadOnlyList<CurvePoint>? points, double x)
    {
        x = Math.Clamp(x, 0.0, 1.0);
        if (points is null || points.Count == 0)
            return x;
        if (points.Count == 1)
            return Math.Clamp(points[0].Y, 0.0, 1.0);

        // Assume roughly sorted; sort a copy only when needed for safety on small N.
        List<CurvePoint> sorted;
        var needsSort = false;
        for (var i = 1; i < points.Count; i++)
        {
            if (points[i].X < points[i - 1].X)
            {
                needsSort = true;
                break;
            }
        }
        if (needsSort)
            sorted = points.OrderBy(p => p.X).ToList();
        else
            sorted = points as List<CurvePoint> ?? points.ToList();

        if (x <= sorted[0].X)
            return Math.Clamp(sorted[0].Y, 0.0, 1.0);
        if (x >= sorted[^1].X)
            return Math.Clamp(sorted[^1].Y, 0.0, 1.0);

        for (var i = 1; i < sorted.Count; i++)
        {
            var a = sorted[i - 1];
            var b = sorted[i];
            if (x > b.X) continue;
            var span = b.X - a.X;
            if (span < 1e-9)
                return Math.Clamp(b.Y, 0.0, 1.0);
            var t = (x - a.X) / span;
            return Math.Clamp(a.Y + t * (b.Y - a.Y), 0.0, 1.0);
        }

        return x;
    }

    /// <summary>Sample master (then optional per-channel) for one linear-ish channel value in 0–1.</summary>
    public double MapChannel(double value, IReadOnlyList<CurvePoint>? channel)
    {
        var v = Sample(Master, value);
        if (channel is { Count: > 0 })
            v = Sample(channel, v);
        return v;
    }
}

public sealed class HueCurves
{
    [JsonPropertyName("hueVsHue")]
    public List<CurvePoint> HueVsHue { get; set; } = new();

    [JsonPropertyName("hueVsSat")]
    public List<CurvePoint> HueVsSat { get; set; } = new();

    [JsonPropertyName("hueVsLum")]
    public List<CurvePoint> HueVsLum { get; set; } = new();

    public const double NeutralY = 0.5;

    [JsonIgnore]
    public bool IsIdentity
    {
        get
        {
            return IsNeutral(HueVsHue) && IsNeutral(HueVsSat) && IsNeutral(HueVsLum);
        }
    }

    private static bool IsNeutral(List<CurvePoint> pts)
    {
        if (pts.Count == 0) return true;
        return pts.All(p => Math.Abs(p.Y - NeutralY) < 1e-4);
    }

    public string? Encoded()
    {
        try
        {
            return JsonSerializer.Serialize(this);
        }
        catch
        {
            return null;
        }
    }

    public static HueCurves? Decode(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<HueCurves>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Apply hue-vs-hue / sat / lum curves. Neutral Y = 0.5.
    /// Hue-vs-hue: Δhue = (sample(y) − 0.5) * 2 (full turns scale).
    /// Hue-vs-sat: multiplier = sample * 2 (0.5 → 1).
    /// Hue-vs-lum: additive luma = (sample − 0.5).
    /// </summary>
    public void Apply(ref double red, ref double green, ref double blue)
    {
        RgbToHsl(red, green, blue, out var h, out var s, out var l);

        if (HueVsHue is { Count: > 0 })
        {
            var y = GradeCurve.Sample(HueVsHue, h);
            h = h + (y - NeutralY) * 2.0;
            h -= Math.Floor(h); // wrap 0..1
        }
        if (HueVsSat is { Count: > 0 })
        {
            var y = GradeCurve.Sample(HueVsSat, h);
            s = Math.Clamp(s * (y * 2.0), 0, 1);
        }
        if (HueVsLum is { Count: > 0 })
        {
            var y = GradeCurve.Sample(HueVsLum, h);
            l = Math.Clamp(l + (y - NeutralY), 0, 1);
        }

        HslToRgb(h, s, l, out red, out green, out blue);
    }

    private static void RgbToHsl(double r, double g, double b, out double h, out double s, out double l)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        l = (max + min) / 2;
        if (Math.Abs(max - min) < 1e-9)
        {
            h = 0;
            s = 0;
            return;
        }
        var d = max - min;
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (max == r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
        else if (max == g) h = ((b - r) / d + 2) / 6;
        else h = ((r - g) / d + 4) / 6;
    }

    private static void HslToRgb(double h, double s, double l, out double r, out double g, out double b)
    {
        if (s <= 1e-9)
        {
            r = g = b = l;
            return;
        }
        double Hue2Rgb(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        r = Hue2Rgb(p, q, h + 1.0 / 3);
        g = Hue2Rgb(p, q, h);
        b = Hue2Rgb(p, q, h - 1.0 / 3);
        r = Math.Clamp(r, 0, 1);
        g = Math.Clamp(g, 0, 1);
        b = Math.Clamp(b, 0, 1);
    }
}
