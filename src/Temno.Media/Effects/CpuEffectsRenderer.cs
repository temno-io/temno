using System.Diagnostics.CodeAnalysis;
using Temno.Core.Editing;
using Temno.Core.Models;

namespace Temno.Media.Effects;

/// <summary>
/// Portable RGBA/BGRA reference renderer for the v1 color effects. It works in
/// straight-alpha pixel space and never mutates the decoded source frame.
/// </summary>
public sealed class CpuEffectsRenderer : IFrameEffectsRenderer
{
    private const double RedLuma = 0.2126;
    private const double GreenLuma = 0.7152;
    private const double BlueLuma = 0.0722;

    public VideoFrame Apply(VideoFrame source, IReadOnlyList<Effect>? effects, int clipRelativeFrame)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (clipRelativeFrame < 0)
            throw new ArgumentOutOfRangeException(nameof(clipRelativeFrame));

        if (source.Width <= 0) throw new ArgumentOutOfRangeException(nameof(source), "Frame width must be positive.");
        if (source.Height <= 0) throw new ArgumentOutOfRangeException(nameof(source), "Frame height must be positive.");

        var requiredLength = checked(source.Width * source.Height * 4);
        if (source.Data.Length < requiredLength)
            throw new ArgumentException("Frame data is smaller than its declared RGBA/BGRA dimensions.", nameof(source));

        var format = source.PixelFormat.Trim();
        var isRgba = string.Equals(format, "rgba", StringComparison.OrdinalIgnoreCase);
        var isBgra = string.Equals(format, "bgra", StringComparison.OrdinalIgnoreCase);
        if (!isRgba && !isBgra)
            throw new NotSupportedException($"CPU effects require RGBA or BGRA frames, not '{source.PixelFormat}'.");

        // A stack with nothing enabled is a no-op, so hand the source frame straight back.
        // Cloning here cost one full-frame large-object allocation per layer per tick on the
        // preview clock and per frame on export. Callers treat returned frames as read-only and
        // dispose the source only when Apply handed back a different instance.
        if (!HasEnabledEffect(effects))
            return source;

        var output = (byte[])source.Data.Clone();
        var redIndex = isRgba ? 0 : 2;
        var blueIndex = isRgba ? 2 : 0;
        var w = source.Width;
        var h = source.Height;

        // Apply in stack order so spatial and color ops interleave correctly.
        foreach (var effect in effects)
        {
            if (!effect.Enabled) continue;
            switch (effect.Type)
            {
                case "color.exposure":
                case "color.contrast":
                case "color.saturation":
                case "color.temperature":
                case "color.vibrance":
                case "color.highlightsShadows":
                case "color.blacksWhites":
                case "color.curves":
                case "color.wheels":
                case "color.hueCurves":
                case "color.lut":
                    ApplyColorEffectInPlace(output, requiredLength, redIndex, blueIndex, effect, clipRelativeFrame);
                    break;
                case "blur.gaussian":
                    CpuSpatialEffects.GaussianBlur(output, w, h, redIndex, blueIndex,
                        ResolveParameter(effect, "radius", clipRelativeFrame));
                    break;
                case "blur.sharpen":
                    CpuSpatialEffects.Sharpen(output, w, h, redIndex, blueIndex,
                        ResolveParameter(effect, "amount", clipRelativeFrame));
                    break;
                case "blur.noiseReduction":
                    CpuSpatialEffects.NoiseReduction(output, w, h, redIndex, blueIndex,
                        ResolveParameter(effect, "amount", clipRelativeFrame));
                    break;
                case "blur.motion":
                    CpuSpatialEffects.MotionBlur(output, w, h, redIndex, blueIndex,
                        ResolveParameter(effect, "radius", clipRelativeFrame),
                        ResolveParameter(effect, "angle", clipRelativeFrame));
                    break;
                case "stylize.grain":
                    CpuSpatialEffects.Grain(output, w, h, redIndex, blueIndex,
                        ResolveParameter(effect, "amount", clipRelativeFrame),
                        ResolveParameter(effect, "size", clipRelativeFrame),
                        clipRelativeFrame);
                    break;
                case "stylize.vignette":
                    CpuSpatialEffects.Vignette(output, w, h, redIndex, blueIndex,
                        ResolveParameter(effect, "amount", clipRelativeFrame),
                        ResolveParameter(effect, "midpoint", clipRelativeFrame),
                        ResolveParameter(effect, "roundness", clipRelativeFrame),
                        ResolveParameter(effect, "feather", clipRelativeFrame));
                    break;
                case "stylize.glow":
                    CpuSpatialEffects.Glow(output, w, h, redIndex, blueIndex,
                        ResolveParameter(effect, "intensity", clipRelativeFrame),
                        ResolveParameter(effect, "radius", clipRelativeFrame),
                        ResolveParameter(effect, "threshold", clipRelativeFrame),
                        ResolveParameter(effect, "warmth", clipRelativeFrame));
                    break;
                case "detail.clarity":
                    CpuSpatialEffects.Clarity(output, w, h, redIndex, blueIndex,
                        ResolveParameter(effect, "clarity", clipRelativeFrame),
                        ResolveParameter(effect, "dehaze", clipRelativeFrame));
                    break;
                case "key.chroma":
                    CpuSpatialEffects.ChromaKey(output, w, h, redIndex, blueIndex,
                        ResolveParameter(effect, "keyHue", clipRelativeFrame),
                        ResolveParameter(effect, "tolerance", clipRelativeFrame),
                        ResolveParameter(effect, "softness", clipRelativeFrame),
                        ResolveParameter(effect, "spill", clipRelativeFrame));
                    break;
            }
        }

        return new VideoFrame(source.Width, source.Height, output, source.PixelFormat);
    }

    /// <summary>True when at least one effect in the stack is enabled, i.e. Apply is not identity.</summary>
    public static bool HasEnabledEffect([NotNullWhen(true)] IReadOnlyList<Effect>? effects)
    {
        if (effects is null) return false;
        for (var i = 0; i < effects.Count; i++)
        {
            if (effects[i].Enabled) return true;
        }

        return false;
    }

    // NOTE: splitting this loop across cores with Parallel.For was measured and reverted. On a
    // 14-core machine it changed a 4K exposure pass from 134.3 ms to 131.3 ms (best of 5, idle) —
    // the loop is bound by streaming 33 MB in and 33 MB out per frame, not by arithmetic, so more
    // cores buy nothing. The real wins are upstream: stop allocating a fresh frame buffer per stage
    // (16-127 MB per exported frame, all on the large-object heap) and move the work onto the GPU.
    private static void ApplyColorEffectInPlace(
        byte[] output, int requiredLength, int redIndex, int blueIndex, Effect effect, int clipRelativeFrame)
    {
        var ops = ResolveOperations([effect], clipRelativeFrame);
        if (ops.Count == 0) return;
        for (var offset = 0; offset < requiredLength; offset += 4)
        {
            var red = output[offset + redIndex] / 255.0;
            var green = output[offset + 1] / 255.0;
            var blue = output[offset + blueIndex] / 255.0;
            foreach (var operation in ops)
            {
                switch (operation.Kind)
                {
                    case ColorOperationKind.Exposure:
                        ApplyExposure(ref red, ref green, ref blue, operation.Value);
                        break;
                    case ColorOperationKind.Contrast:
                        ApplyContrast(ref red, ref green, ref blue, operation.Value);
                        break;
                    case ColorOperationKind.Saturation:
                        ApplySaturation(ref red, ref green, ref blue, operation.Value);
                        break;
                    case ColorOperationKind.Temperature:
                        ApplyTemperature(ref red, ref green, ref blue, operation.Value, operation.Value2);
                        break;
                    case ColorOperationKind.Vibrance:
                        ApplyVibrance(ref red, ref green, ref blue, operation.Value);
                        break;
                    case ColorOperationKind.HighlightsShadows:
                        ApplyHighlightsShadows(ref red, ref green, ref blue, operation.Value, operation.Value2);
                        break;
                    case ColorOperationKind.BlacksWhites:
                        ApplyBlacksWhites(ref red, ref green, ref blue, operation.Value, operation.Value2);
                        break;
                    case ColorOperationKind.MasterCurve:
                        if (operation.CurveLut is { Length: > 0 } lut)
                            ApplyMasterCurveLut(ref red, ref green, ref blue, lut);
                        break;
                    case ColorOperationKind.Wheels:
                        if (operation.Wheels is { } wheels)
                            ColorWheelsMath.Apply(ref red, ref green, ref blue, wheels);
                        break;
                    case ColorOperationKind.HueCurves:
                        operation.HueCurves?.Apply(ref red, ref green, ref blue);
                        break;
                    case ColorOperationKind.Lut:
                        if (operation.CubeLut is { } cube)
                            cube.Apply(ref red, ref green, ref blue, operation.Value);
                        break;
                }
            }
            output[offset + redIndex] = ToByte(red);
            output[offset + 1] = ToByte(green);
            output[offset + blueIndex] = ToByte(blue);
        }
    }

    private static List<ColorOperation> ResolveOperations(IReadOnlyList<Effect>? effects, int clipRelativeFrame)
    {
        var operations = new List<ColorOperation>();
        if (effects is null) return operations;

        foreach (var effect in effects)
        {
            if (!effect.Enabled) continue;

            switch (effect.Type)
            {
                case "color.exposure":
                    operations.Add(new ColorOperation(
                        ColorOperationKind.Exposure,
                        ResolveParameter(effect, "ev", clipRelativeFrame)));
                    break;
                case "color.contrast":
                    operations.Add(new ColorOperation(
                        ColorOperationKind.Contrast,
                        ResolveParameter(effect, "amount", clipRelativeFrame)));
                    break;
                case "color.saturation":
                    operations.Add(new ColorOperation(
                        ColorOperationKind.Saturation,
                        ResolveParameter(effect, "amount", clipRelativeFrame)));
                    break;
                case "color.temperature":
                    operations.Add(new ColorOperation(
                        ColorOperationKind.Temperature,
                        ResolveParameter(effect, "temperature", clipRelativeFrame),
                        ResolveParameter(effect, "tint", clipRelativeFrame)));
                    break;
                case "color.vibrance":
                    operations.Add(new ColorOperation(
                        ColorOperationKind.Vibrance,
                        ResolveParameter(effect, "amount", clipRelativeFrame)));
                    break;
                case "color.highlightsShadows":
                    operations.Add(new ColorOperation(
                        ColorOperationKind.HighlightsShadows,
                        ResolveParameter(effect, "highlights", clipRelativeFrame),
                        ResolveParameter(effect, "shadows", clipRelativeFrame)));
                    break;
                case "color.blacksWhites":
                    operations.Add(new ColorOperation(
                        ColorOperationKind.BlacksWhites,
                        ResolveParameter(effect, "blacks", clipRelativeFrame),
                        ResolveParameter(effect, "whites", clipRelativeFrame)));
                    break;
                case "color.curves":
                {
                    if (effect.Params is not null
                        && effect.Params.TryGetValue("curve", out var cv)
                        && cv.String is { Length: > 0 } json)
                    {
                        var grade = GradeCurve.Decode(json);
                        if (grade is not null && !grade.IsIdentity)
                        {
                            var lut = BakeMasterCurveLut(grade);
                            if (lut is not null)
                                operations.Add(new ColorOperation(ColorOperationKind.MasterCurve, 0, 0, lut));
                        }
                    }
                    break;
                }
                case "color.wheels":
                {
                    var lx = ResolveParameter(effect, "lift_x", clipRelativeFrame);
                    var ly = ResolveParameter(effect, "lift_y", clipRelativeFrame);
                    var lm = ResolveParameter(effect, "lift_m", clipRelativeFrame);
                    var gx = ResolveParameter(effect, "gamma_x", clipRelativeFrame);
                    var gy = ResolveParameter(effect, "gamma_y", clipRelativeFrame);
                    var gm = ResolveParameter(effect, "gamma_m", clipRelativeFrame);
                    var hx = ResolveParameter(effect, "gain_x", clipRelativeFrame);
                    var hy = ResolveParameter(effect, "gain_y", clipRelativeFrame);
                    var hm = ResolveParameter(effect, "gain_m", clipRelativeFrame);
                    if (!ColorWheelsMath.IsNeutral(lx, ly, lm, gx, gy, gm, hx, hy, hm))
                    {
                        var coef = ColorWheelsMath.FromParams(lx, ly, lm, gx, gy, gm, hx, hy, hm);
                        operations.Add(new ColorOperation(ColorOperationKind.Wheels, 0, 0, null, coef));
                    }
                    break;
                }
                case "color.hueCurves":
                {
                    if (effect.Params is not null
                        && effect.Params.TryGetValue("curves", out var hc)
                        && hc.String is { Length: > 0 } hjson)
                    {
                        var curves = HueCurves.Decode(hjson);
                        if (curves is not null && !curves.IsIdentity)
                            operations.Add(new ColorOperation(ColorOperationKind.HueCurves, 0, 0, null, null, curves));
                    }
                    break;
                }
                case "color.lut":
                {
                    if (effect.Params is not null
                        && effect.Params.TryGetValue("path", out var pathParam)
                        && pathParam.String is { Length: > 0 } path)
                    {
                        var cube = CubeLutCache.Get(path);
                        if (cube is not null)
                        {
                            var intensity = ResolveParameter(effect, "intensity", clipRelativeFrame);
                            operations.Add(new ColorOperation(ColorOperationKind.Lut, intensity, 0, null, null, null, cube));
                        }
                    }
                    break;
                }
            }
        }

        return operations;
    }

    /// <summary>Per-channel 256 LUTs: [0]=R, [1]=G, [2]=B after master then channel curve.</summary>
    private static float[]? BakeMasterCurveLut(GradeCurve grade)
    {
        if (grade.IsIdentity) return null;

        var lut = new float[256 * 3];
        for (var i = 0; i < 256; i++)
        {
            var x = i / 255.0;
            lut[i] = (float)grade.MapChannel(x, grade.Red);
            lut[256 + i] = (float)grade.MapChannel(x, grade.Green);
            lut[512 + i] = (float)grade.MapChannel(x, grade.Blue);
        }
        return lut;
    }

    private static void ApplyMasterCurveLut(ref double red, ref double green, ref double blue, float[] lut)
    {
        if (lut.Length >= 768)
        {
            red = SampleLutChannel(lut, 0, red);
            green = SampleLutChannel(lut, 1, green);
            blue = SampleLutChannel(lut, 2, blue);
            return;
        }
        // Legacy single-channel LUT
        red = SampleLut(lut, red);
        green = SampleLut(lut, green);
        blue = SampleLut(lut, blue);
    }

    private static double SampleLutChannel(float[] lut, int channel, double value)
    {
        value = Clamp01(value);
        var baseIdx = channel * 256;
        var scaled = value * 255;
        var i0 = (int)scaled;
        if (i0 >= 255) return lut[baseIdx + 255];
        var t = scaled - i0;
        return lut[baseIdx + i0] * (1 - t) + lut[baseIdx + i0 + 1] * t;
    }

    private static double SampleLut(float[] lut, double value)
    {
        value = Clamp01(value);
        var scaled = value * (lut.Length - 1);
        var i0 = (int)scaled;
        if (i0 >= lut.Length - 1) return lut[^1];
        var t = scaled - i0;
        return lut[i0] * (1 - t) + lut[i0 + 1] * t;
    }

    private static double ResolveParameter(Effect effect, string key, int clipRelativeFrame)
    {
        var descriptor = EffectRegistry.GetDescriptor(effect.Type)
            ?? throw new InvalidOperationException($"Missing effect descriptor for '{effect.Type}'.");
        var spec = descriptor.Params.Find(candidate => candidate.Key == key)
            ?? throw new InvalidOperationException($"Missing parameter '{key}' for effect '{effect.Type}'.");

        var value = effect.Params is not null && effect.Params.TryGetValue(key, out var parameter)
            ? parameter.Resolved(clipRelativeFrame, spec.DefaultValue)
            : spec.DefaultValue;
        return Math.Clamp(value, spec.Min, spec.Max);
    }

    private static void ApplyExposure(ref double red, ref double green, ref double blue, double ev)
    {
        var multiplier = Math.Pow(2.0, ev);
        red = Clamp01(LinearToSrgb(SrgbToLinear(red) * multiplier));
        green = Clamp01(LinearToSrgb(SrgbToLinear(green) * multiplier));
        blue = Clamp01(LinearToSrgb(SrgbToLinear(blue) * multiplier));
    }

    private static void ApplyContrast(ref double red, ref double green, ref double blue, double amount)
    {
        red = Clamp01((red - 0.5) * amount + 0.5);
        green = Clamp01((green - 0.5) * amount + 0.5);
        blue = Clamp01((blue - 0.5) * amount + 0.5);
    }

    private static void ApplySaturation(ref double red, ref double green, ref double blue, double amount)
    {
        var luma = red * RedLuma + green * GreenLuma + blue * BlueLuma;
        red = Clamp01(luma + (red - luma) * amount);
        green = Clamp01(luma + (green - luma) * amount);
        blue = Clamp01(luma + (blue - luma) * amount);
    }

    /// <summary>Map Kelvin-ish temperature (2000–11000, D65 6500 identity) + tint (−100…100) to RGB bias.</summary>
    private static void ApplyTemperature(ref double red, ref double green, ref double blue, double temperature, double tint)
        => ColorTemperature.ApplyRgb(ref red, ref green, ref blue, temperature, tint);

    /// <summary>Vibrance: boost low-saturation pixels more than already-saturated ones.</summary>
    private static void ApplyVibrance(ref double red, ref double green, ref double blue, double amount)
    {
        var max = Math.Max(red, Math.Max(green, blue));
        var min = Math.Min(red, Math.Min(green, blue));
        var sat = max <= 1e-6 ? 0 : (max - min) / max;
        var strength = 1.0 + amount * (1.0 - sat);
        ApplySaturation(ref red, ref green, ref blue, strength);
    }

    private static void ApplyHighlightsShadows(ref double red, ref double green, ref double blue, double highlights, double shadows)
    {
        var luma = red * RedLuma + green * GreenLuma + blue * BlueLuma;
        var shadowW = 1.0 - Smoothstep(0.0, 0.5, luma);
        var highW = Smoothstep(0.5, 1.0, luma);
        var lift = shadows * 0.25 * shadowW + highlights * 0.25 * highW;
        red = Clamp01(red + lift);
        green = Clamp01(green + lift);
        blue = Clamp01(blue + lift);
    }

    private static void ApplyBlacksWhites(ref double red, ref double green, ref double blue, double blacks, double whites)
    {
        // Pivot levels: compress/expand near black and white.
        var blackPoint = blacks * 0.15;
        var whitePoint = 1.0 - whites * 0.15;
        if (Math.Abs(whitePoint - blackPoint) < 1e-4) return;
        red = Clamp01((red - blackPoint) / (whitePoint - blackPoint));
        green = Clamp01((green - blackPoint) / (whitePoint - blackPoint));
        blue = Clamp01((blue - blackPoint) / (whitePoint - blackPoint));
    }

    private static double Smoothstep(double edge0, double edge1, double x)
    {
        var t = Clamp01((x - edge0) / (edge1 - edge0));
        return t * t * (3 - 2 * t);
    }

    private static double SrgbToLinear(double value)
    {
        value = Clamp01(value);
        return value <= 0.04045
            ? value / 12.92
            : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static double LinearToSrgb(double value)
    {
        return value <= 0.0031308
            ? value * 12.92
            : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
    }

    private static byte ToByte(double value)
        => (byte)Math.Clamp(
            (int)Math.Round(Clamp01(value) * 255.0, MidpointRounding.AwayFromZero),
            byte.MinValue,
            byte.MaxValue);

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);

    private enum ColorOperationKind
    {
        Exposure,
        Contrast,
        Saturation,
        Temperature,
        Vibrance,
        HighlightsShadows,
        BlacksWhites,
        MasterCurve,
        Wheels,
        HueCurves,
        Lut,
    }

    private readonly record struct ColorOperation(
        ColorOperationKind Kind,
        double Value,
        double Value2 = 0,
        float[]? CurveLut = null,
        ColorWheelsMath.Coefficients? Wheels = null,
        HueCurves? HueCurves = null,
        CubeLut? CubeLut = null);
}

/// <summary>Process-wide .cube cache keyed by path + last write time.</summary>
file static class CubeLutCache
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (long Mtime, CubeLut Lut)> Map = new(StringComparer.OrdinalIgnoreCase);

    public static CubeLut? Get(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var mtime = File.GetLastWriteTimeUtc(path).Ticks;
            lock (Gate)
            {
                if (Map.TryGetValue(path, out var entry) && entry.Mtime == mtime)
                    return entry.Lut;
            }
            var lut = CubeLut.TryLoadFile(path);
            if (lut is null) return null;
            lock (Gate)
            {
                Map[path] = (mtime, lut);
            }
            return lut;
        }
        catch
        {
            return null;
        }
    }
}
