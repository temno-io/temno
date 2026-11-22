using System.Globalization;

namespace Temno.Core.Preview;

/// <summary>
/// User-selectable cap on interactive frame preview decode and compositing.
/// <see cref="MaxDimension"/> is the longest-edge limit in pixels, or <c>null</c> for
/// <see cref="Full"/> (match the timeline canvas, or native source size in the source monitor).
/// Export never reads this setting.
/// </summary>
public sealed record PreviewResolution(
    string Tag,
    string Label,
    int? MaxDimension,
    bool IsCustom = false)
{
    /// <summary>Default interactive preview long-edge limit (WP-163 baseline).</summary>
    public const int DefaultMaxDimension = 1280;

    public const int MinCustomDimension = 160;
    public const int MaxCustomDimension = 7680;

    public const string FullTag = "full";
    public const string DefaultTag = "720p";

    public static PreviewResolution Full { get; } = new(FullTag, "Full", null);
    public static PreviewResolution R2K { get; } = new("2k", "2K", 2560);
    public static PreviewResolution R1080p { get; } = new("1080p", "1080p", 1920);
    public static PreviewResolution R720p { get; } = new("720p", "720p", DefaultMaxDimension);
    public static PreviewResolution R540p { get; } = new("540p", "540p", 960);
    public static PreviewResolution R360p { get; } = new("360p", "360p", 640);

    public static PreviewResolution Default => R720p;

    public static IReadOnlyList<PreviewResolution> Presets { get; } =
    [
        Full,
        R2K,
        R1080p,
        R720p,
        R540p,
        R360p,
    ];

    public bool IsFull => MaxDimension is null;

    public static PreviewResolution Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return Default;

        var trimmed = raw.Trim();
        if (trimmed.Equals(FullTag, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("canvas", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("native", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("original", StringComparison.OrdinalIgnoreCase))
        {
            return Full;
        }

        if (trimmed.Equals("1440p", StringComparison.OrdinalIgnoreCase))
            return R2K;

        foreach (var preset in Presets)
        {
            if (trimmed.Equals(preset.Tag, StringComparison.OrdinalIgnoreCase))
                return preset;
        }

        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= MinCustomDimension
            && parsed <= MaxCustomDimension)
        {
            return FromCustomMaxDimension(parsed);
        }

        return Default;
    }

    public static PreviewResolution FromCustomMaxDimension(int maxDimension)
    {
        var clamped = Math.Clamp(maxDimension, MinCustomDimension, MaxCustomDimension);
        var even = Math.Max(MinCustomDimension, clamped & ~1);
        foreach (var preset in Presets)
        {
            if (preset.MaxDimension == even)
                return preset;
        }

        return new PreviewResolution(
            even.ToString(CultureInfo.InvariantCulture),
            "Custom",
            even,
            IsCustom: true);
    }

    public static bool TryValidateCustomMaxDimension(int maxDimension, out string? error)
    {
        if (maxDimension < MinCustomDimension || maxDimension > MaxCustomDimension)
        {
            error = $"Max edge must be between {MinCustomDimension} and {MaxCustomDimension} px.";
            return false;
        }

        if ((maxDimension & 1) != 0)
        {
            error = "Max edge must be an even number.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Preview raster dimensions for a canvas or frame of <paramref name="width"/> × <paramref name="height"/>.
    /// Never upscales past the source size; downscaled results match <c>PreviewRaster.Fit</c> even rounding.
    /// </summary>
    public (int Width, int Height) FitDimensions(int width, int height)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

        if (MaxDimension is not int cap || (width <= cap && height <= cap))
            return (width, height);

        var scale = cap / (double)Math.Max(width, height);
        return (
            Even(Math.Max(2, (int)Math.Round(width * scale))),
            Even(Math.Max(2, (int)Math.Round(height * scale))));
    }

    /// <summary>
    /// Effective longest-edge limit handed to the preview decoder and scrub-proxy selector.
    /// In <see cref="Full"/> mode on a timeline monitor this tracks the active timeline canvas;
    /// without a canvas (e.g. source monitor) it allows native source decode up to
    /// <see cref="MaxCustomDimension"/>.
    /// </summary>
    public int ResolveDecodeMaxDimension(int? canvasWidth, int? canvasHeight)
    {
        if (MaxDimension is int cap)
            return cap;

        if (canvasWidth is > 0 and var cw && canvasHeight is > 0 and var ch)
            return Math.Clamp(Math.Max(cw, ch), MinCustomDimension, MaxCustomDimension);

        return MaxCustomDimension;
    }

    /// <summary>Menu label for a preset item given the active monitor's canvas/source size, if known.</summary>
    public string MenuLabel(int? canvasWidth, int? canvasHeight)
    {
        var hasCanvas = canvasWidth is > 0 && canvasHeight is > 0;
        if (MaxDimension is not int cap)
            return hasCanvas ? $"{Label} · {canvasWidth}×{canvasHeight}" : Label;

        if (!hasCanvas)
            return $"{Label} · {cap} px";

        var cw = canvasWidth!.Value;
        var ch = canvasHeight!.Value;
        if (Math.Max(cw, ch) >= cap)
        {
            var (w, h) = FitDimensions(cw, ch);
            return $"{Label} · {w}×{h}";
        }

        return $"{Label} · up to {cap} px";
    }

    /// <summary>Label for the "Custom…" menu entry.</summary>
    public string CustomMenuLabel(int? canvasWidth, int? canvasHeight)
    {
        if (!IsCustom || MaxDimension is not int cap)
            return "Custom…";

        if (canvasWidth is > 0 and var cw && canvasHeight is > 0 and var ch && Math.Max(cw, ch) >= cap)
        {
            var (w, h) = FitDimensions(cw, ch);
            return $"Custom ({w}×{h})…";
        }

        return $"Custom ({cap} px)…";
    }

    /// <summary>Compact or full text shown on the Preview transport bar resolution button.</summary>
    public string ButtonLabel(
        int? presentedWidth,
        int? presentedHeight,
        int? canvasWidth,
        int? canvasHeight,
        bool compact)
    {
        (int Width, int Height)? size = null;
        if (presentedWidth is > 0 and var pw && presentedHeight is > 0 and var ph)
            size = (pw, ph);
        else if (canvasWidth is > 0 and var cw && canvasHeight is > 0 and var ch)
            size = FitDimensions(cw, ch);

        if (size is (var w, var h))
            return compact ? $"{w}×{h}" : $"Frame preview · {w}×{h}";

        var summary = IsCustom && MaxDimension is int cap ? $"{cap} px" : Label;
        return compact ? summary : $"Frame preview · {summary}";
    }

    private static int Even(int value) => Math.Max(2, value & ~1);
}
