using System.Text.Json.Serialization;

namespace Temno.Core.Models;

/// <summary>Mac: Models/Keyframe.swift</summary>
public enum Interpolation
{
    linear,
    hold,
    smooth,
}

public sealed class Keyframe<T>
{
    [JsonPropertyName("frame")]
    public int Frame { get; set; }

    [JsonPropertyName("value")]
    public T Value { get; set; } = default!;

    [JsonPropertyName("interpolationOut")]
    public Interpolation InterpolationOut { get; set; } = Interpolation.smooth;
}

public sealed class KeyframeTrack<T>
{
    [JsonPropertyName("keyframes")]
    public List<Keyframe<T>> Keyframes { get; set; } = [];

    [JsonIgnore]
    public bool IsActive => Keyframes.Count > 0;

    public void Upsert(Keyframe<T> kf)
    {
        var i = Keyframes.FindIndex(k => k.Frame == kf.Frame);
        if (i >= 0)
            Keyframes[i] = kf;
        else
        {
            var at = Keyframes.FindIndex(k => k.Frame > kf.Frame);
            if (at < 0) Keyframes.Add(kf);
            else Keyframes.Insert(at, kf);
        }
    }

    public KeyframeTrack<T> Clone(Func<T, T>? cloneValue = null)
    {
        var copy = new KeyframeTrack<T>();
        foreach (var kf in Keyframes)
        {
            var value = cloneValue is null ? kf.Value : cloneValue(kf.Value);
            copy.Keyframes.Add(new Keyframe<T>
            {
                Frame = kf.Frame,
                Value = value,
                InterpolationOut = kf.InterpolationOut,
            });
        }
        return copy;
    }
}

public sealed class AnimPair
{
    [JsonPropertyName("a")]
    public double A { get; set; }

    [JsonPropertyName("b")]
    public double B { get; set; }

    public AnimPair() { }
    public AnimPair(double a, double b) { A = a; B = b; }
    public AnimPair Clone() => new(A, B);
}

public static class KeyframeMath
{
    public static double Smoothstep(double t) => t * t * (3 - 2 * t);

    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public static AnimPair Lerp(AnimPair a, AnimPair b, double t)
        => new(Lerp(a.A, b.A, t), Lerp(a.B, b.B, t));

    public static Crop Lerp(Crop a, Crop b, double t) => new()
    {
        Left = Lerp(a.Left, b.Left, t),
        Top = Lerp(a.Top, b.Top, t),
        Right = Lerp(a.Right, b.Right, t),
        Bottom = Lerp(a.Bottom, b.Bottom, t),
    };

    public static double Sample(KeyframeTrack<double>? track, int frame, double fallback)
    {
        if (track is null || !track.IsActive) return fallback;
        return Sample(track, frame, fallback, static (a, b, t) => Lerp(a, b, t));
    }

    public static AnimPair Sample(KeyframeTrack<AnimPair>? track, int frame, AnimPair fallback)
    {
        if (track is null || !track.IsActive) return fallback;
        return Sample(track, frame, fallback, Lerp);
    }

    public static Crop Sample(KeyframeTrack<Crop>? track, int frame, Crop fallback)
    {
        if (track is null || !track.IsActive) return fallback;
        return Sample(track, frame, fallback, Lerp);
    }

    public static T Sample<T>(
        KeyframeTrack<T> track,
        int frame,
        T fallback,
        Func<T, T, double, T> interpolate)
    {
        var kfs = track.Keyframes;
        if (kfs.Count == 0) return fallback;
        if (kfs.Count == 1) return kfs[0].Value;
        if (frame <= kfs[0].Frame) return kfs[0].Value;
        if (frame >= kfs[^1].Frame) return kfs[^1].Value;

        var bIdx = kfs.FindIndex(k => k.Frame > frame);
        if (bIdx <= 0) return kfs[^1].Value;
        var a = kfs[bIdx - 1];
        var b = kfs[bIdx];
        var raw = (double)(frame - a.Frame) / (b.Frame - a.Frame);
        return a.InterpolationOut switch
        {
            Interpolation.hold => a.Value,
            Interpolation.linear => interpolate(a.Value, b.Value, raw),
            Interpolation.smooth => interpolate(a.Value, b.Value, Smoothstep(raw)),
            _ => interpolate(a.Value, b.Value, raw),
        };
    }
}

/// <summary>Mac InspectorView.VolumeScale — floor −60 dB, ceiling +15 dB.</summary>
public static class VolumeScale
{
    public const double FloorDb = -60.0;
    public const double CeilingDb = 15.0;

    /// <summary>Mac VolumeScale.linearFromDb — 10^(dB/20); floor maps to mute (0).</summary>
    public static double LinearFromDb(double dB)
    {
        if (double.IsNaN(dB) || dB <= FloorDb) return 0;
        var clamped = Math.Min(dB, CeilingDb);
        return Math.Pow(10.0, clamped / 20.0);
    }

    /// <summary>Mac VolumeScale.dbFromLinear — 20*log10(linear); 0 → floor (−∞ display).</summary>
    public static double DbFromLinear(double linear)
    {
        if (double.IsNaN(linear) || linear <= 0) return FloorDb;
        var db = 20.0 * Math.Log10(linear);
        return Math.Clamp(db, FloorDb, CeilingDb);
    }

    /// <summary>True when volume is at or below the mute floor.</summary>
    public static bool IsMuted(double linear) => linear <= 0 || DbFromLinear(linear) <= FloorDb;
}
