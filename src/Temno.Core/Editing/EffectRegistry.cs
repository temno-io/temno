using System;
using System.Collections.Generic;
using System.Linq;
using Temno.Core.Models;

namespace Temno.Core.Editing;

public sealed class EffectParamSpec
{
    public string Key { get; }
    public double Min { get; }
    public double Max { get; }
    public double DefaultValue { get; }

    public EffectParamSpec(string key, double min, double max, double defaultValue)
    {
        Key = key;
        Min = min;
        Max = max;
        DefaultValue = defaultValue;
    }
}

public sealed class EffectDescriptor
{
    public string Id { get; }
    public List<EffectParamSpec> Params { get; }

    public EffectDescriptor(string id, List<EffectParamSpec> paramsList)
    {
        Id = id;
        Params = paramsList;
    }

    public Effect MakeEffect()
    {
        var effect = new Effect
        {
            Type = Id,
            Params = new Dictionary<string, EffectParam>()
        };
        foreach (var spec in Params)
        {
            effect.Params[spec.Key] = new EffectParam { Value = spec.DefaultValue };
        }
        return effect;
    }
}

public static class EffectRegistry
{
    public static readonly List<string> CanonicalOrder = new()
    {
        "color.exposure", "color.contrast", "color.highlightsShadows", "color.blacksWhites",
        "color.temperature", "color.vibrance", "color.saturation", "color.wheels", "color.curves",
        "color.hueCurves", "color.lut", "detail.clarity", "key.chroma", "blur.gaussian", "blur.sharpen",
        "blur.noiseReduction", "blur.motion", "stylize.grain", "stylize.vignette", "stylize.glow"
    };

    public static readonly Dictionary<string, EffectDescriptor> Descriptors = new()
    {
        { "color.exposure", new EffectDescriptor("color.exposure", new() { new("ev", -3, 3, 0) }) },
        { "color.contrast", new EffectDescriptor("color.contrast", new() { new("amount", 0.5, 1.5, 1) }) },
        { "color.saturation", new EffectDescriptor("color.saturation", new() { new("amount", 0, 2, 1) }) },
        { "color.temperature", new EffectDescriptor("color.temperature", new() { new("temperature", 2000, 11000, ColorTemperature.NeutralKelvin), new("tint", -100, 100, 0) }) },
        { "color.highlightsShadows", new EffectDescriptor("color.highlightsShadows", new() { new("highlights", -1, 1, 0), new("shadows", -1, 1, 0) }) },
        { "color.blacksWhites", new EffectDescriptor("color.blacksWhites", new() { new("blacks", -1, 1, 0), new("whites", -1, 1, 0) }) },
        { "color.vibrance", new EffectDescriptor("color.vibrance", new() { new("amount", -1, 1, 0) }) },
        { "color.wheels", new EffectDescriptor("color.wheels", new()
            {
                new("lift_x", -1, 1, 0), new("lift_y", -1, 1, 0), new("lift_m", -0.5, 0.5, 0),
                new("gamma_x", -1, 1, 0), new("gamma_y", -1, 1, 0), new("gamma_m", 0.5, 2, 1),
                new("gain_x", -1, 1, 0), new("gain_y", -1, 1, 0), new("gain_m", 0.5, 1.5, 1)
            })
        },
        { "color.curves", new EffectDescriptor("color.curves", new()) },
        { "color.hueCurves", new EffectDescriptor("color.hueCurves", new()) },
        { "color.lut", new EffectDescriptor("color.lut", new() { new("intensity", 0, 1, 1) }) },
        { "blur.gaussian", new EffectDescriptor("blur.gaussian", new() { new("radius", 0, 100, 8) }) },
        { "blur.sharpen", new EffectDescriptor("blur.sharpen", new() { new("amount", 0, 2, 0.4) }) },
        { "blur.noiseReduction", new EffectDescriptor("blur.noiseReduction", new() { new("amount", 0, 1, 0) }) },
        { "blur.motion", new EffectDescriptor("blur.motion", new() { new("radius", 0, 100, 0), new("angle", -180, 180, 0) }) },
        { "stylize.grain", new EffectDescriptor("stylize.grain", new() { new("amount", 0, 1, 0), new("size", 0.5, 4, 1.5) }) },
        { "stylize.vignette", new EffectDescriptor("stylize.vignette", new()
            {
                new("amount", -1, 1, 0), new("midpoint", 0, 1, 0.5), new("roundness", -1, 1, 0), new("feather", 0, 1, 0.5)
            })
        },
        { "stylize.glow", new EffectDescriptor("stylize.glow", new()
            {
                new("intensity", 0, 1, 0), new("radius", 0, 100, 20), new("threshold", 0, 1, 0.6), new("warmth", 0, 1, 0)
            })
        },
        { "detail.clarity", new EffectDescriptor("detail.clarity", new() { new("clarity", -1, 1, 0), new("dehaze", -1, 1, 0) }) },
        { "key.chroma", new EffectDescriptor("key.chroma", new()
            {
                new("keyHue", 0, 1, 0.333), new("tolerance", 0, 1, 0), new("softness", 0, 1, 0.5), new("spill", 0, 1, 0.5)
            })
        }
    };

    public static EffectDescriptor? GetDescriptor(string id)
    {
        return Descriptors.TryGetValue(id, out var desc) ? desc : null;
    }

    public static string GetDisplayName(string id) => id switch
    {
        "color.exposure" => "Exposure",
        "color.contrast" => "Contrast",
        "color.saturation" => "Saturation",
        "color.temperature" => "Temperature & Tint",
        "color.highlightsShadows" => "Highlights & Shadows",
        "color.blacksWhites" => "Levels",
        "color.vibrance" => "Vibrance",
        "color.wheels" => "Color Wheels",
        "color.hueCurves" => "Hue Curves",
        "color.lut" => "LUT",
        "color.curves" => "Curves",
        "blur.gaussian" => "Gaussian Blur",
        "blur.sharpen" => "Sharpen",
        "blur.noiseReduction" => "Noise Reduction",
        "blur.motion" => "Motion Blur",
        "stylize.grain" => "Film Grain",
        "stylize.vignette" => "Vignette",
        "stylize.glow" => "Glow",
        "detail.clarity" => "Clarity & Haze",
        "key.chroma" => "Chroma Key",
        _ => id,
    };

    public static string GetCategory(string id)
    {
        if (id.StartsWith("color.", StringComparison.Ordinal)) return "Color";
        if (id.StartsWith("blur.", StringComparison.Ordinal)) return "Blur & sharpen";
        if (id.StartsWith("stylize.", StringComparison.Ordinal)) return "Stylize";
        if (id.StartsWith("detail.", StringComparison.Ordinal)) return "Detail";
        if (id.StartsWith("key.", StringComparison.Ordinal)) return "Key";
        return "Other";
    }

    public static int GetInsertIndex(List<Effect> effects, string type)
    {
        var rank = CanonicalOrder.IndexOf(type);
        if (rank < 0) rank = int.MaxValue;
        for (int i = 0; i < effects.Count; i++)
        {
            var r = CanonicalOrder.IndexOf(effects[i].Type);
            if (r < 0) r = int.MaxValue;
            if (r > rank) return i;
        }
        return effects.Count;
    }
}
