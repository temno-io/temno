using Temno.Core.Models;

namespace Temno.Core.Export;

/// <summary>
/// Output raster for a video export. Named sizes set the short side and keep the timeline's
/// shape: a fixed landscape frame stretches every other timeline, because the compositor maps
/// width and height independently. A custom box of another shape is the file size; the picture
/// is fitted inside it.
/// </summary>
public static class ExportOutputSize
{
    public const int MinDimension = 2;
    /// <summary>Hard ceiling even when upscale is allowed, so a typo cannot queue 99999×99999.</summary>
    public const int MaxDimension = 7680;

    /// <summary>File size, the fitted picture inside it, and where that picture sits.</summary>
    public readonly record struct FittedExport(
        int Width,
        int Height,
        int ContentWidth,
        int ContentHeight,
        int PadX,
        int PadY)
    {
        public bool IsPadded => ContentWidth != Width || ContentHeight != Height;

        public static FittedExport Exact(int width, int height) => new(width, height, width, height, 0, 0);
    }

    public static (int Width, int Height) EvenSize(int width, int height)
    {
        var w = Math.Max(MinDimension, (width / 2) * 2);
        var h = Math.Max(MinDimension, (height / 2) * 2);
        return (w, h);
    }

    public static int ShortSide(ExportResolution resolution) => resolution switch
    {
        ExportResolution.R720p => 720,
        ExportResolution.R1080p => 1080,
        ExportResolution.R1440p => 1440,
        ExportResolution.R4k => 2160,
        _ => 0,
    };

    public static string ResolutionName(ExportResolution resolution) => resolution switch
    {
        ExportResolution.R720p => "720p",
        ExportResolution.R1080p => "1080p",
        ExportResolution.R1440p => "2K",
        ExportResolution.R4k => "4K",
        ExportResolution.MatchTimeline => "Match Timeline",
        _ => resolution.ToString(),
    };

    /// <summary>Combo label for this timeline, such as "1080p · 1080 × 1920".</summary>
    public static string ChoiceLabel(ExportResolution resolution, int timelineWidth, int timelineHeight)
    {
        var name = ResolutionName(resolution);
        if (!TryResolve(timelineWidth, timelineHeight, new ExportPreset(name, resolution: resolution), out var w, out var h, out _))
            return w > MaxDimension || h > MaxDimension
                ? $"{name} · {w} × {h}, over the limit"
                : $"{name} · unavailable";
        return $"{name} · {w} × {h}";
    }

    public static (int Width, int Height) Resolve(int timelineWidth, int timelineHeight, ExportPreset preset)
    {
        if (!TryResolve(timelineWidth, timelineHeight, preset, out var w, out var h, out var error))
            throw new InvalidOperationException(error ?? "That export size is not available.");
        return (w, h);
    }

    public static (int Width, int Height) Resolve(Timeline timeline, ExportPreset preset)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return Resolve(timeline.Width, timeline.Height, preset);
    }

    public static FittedExport ResolveFrame(int timelineWidth, int timelineHeight, ExportPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var (width, height) = Resolve(timelineWidth, timelineHeight, preset);
        var custom = preset.CustomWidth is int cw && preset.CustomHeight is int ch && cw > 0 && ch > 0;
        if (!custom || timelineWidth <= 0 || timelineHeight <= 0)
            return FittedExport.Exact(width, height);
        return FitInside(timelineWidth, timelineHeight, width, height);
    }

    public static FittedExport ResolveFrame(Timeline timeline, ExportPreset preset)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return ResolveFrame(timeline.Width, timeline.Height, preset);
    }

    /// <summary>
    /// UI-side refusal. Odd sizes are rejected here so the encoder is not asked to silently floor
    /// them; <see cref="EvenSize"/> remains the last-chance floor if a preset is constructed off the dialog.
    /// A different shape is allowed: the picture is fitted inside the box.
    /// </summary>
    public static bool TryValidateCustom(
        int width,
        int height,
        int timelineWidth,
        int timelineHeight,
        bool allowUpscale,
        bool requireEven,
        out string? error)
    {
        error = null;
        if (width < MinDimension || height < MinDimension)
        {
            error = $"Width and height must be at least {MinDimension}×{MinDimension}.";
            return false;
        }

        if (width > MaxDimension || height > MaxDimension)
        {
            error = $"Width and height cannot exceed {MaxDimension}×{MaxDimension}.";
            return false;
        }

        if (requireEven && ((width & 1) != 0 || (height & 1) != 0))
        {
            error = "Width and height must be even for this codec.";
            return false;
        }

        if (!allowUpscale && (width > timelineWidth || height > timelineHeight))
        {
            error = $"Custom size cannot exceed the timeline ({timelineWidth}×{timelineHeight}) unless upscale is allowed.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// File size for <paramref name="preset"/>. On failure <paramref name="width"/> and
    /// <paramref name="height"/> are still the size that was refused, so the dialog can name it.
    /// A named size past <see cref="MaxDimension"/> is refused rather than clamped, which would
    /// change the shape.
    /// </summary>
    public static bool TryResolve(
        int timelineWidth,
        int timelineHeight,
        ExportPreset preset,
        out int width,
        out int height,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(preset);
        error = null;
        width = 0;
        height = 0;

        if (preset.CustomWidth is int cw && preset.CustomHeight is int ch && cw > 0 && ch > 0)
        {
            (width, height) = EvenSize(cw, ch);
            if (width > MaxDimension || height > MaxDimension)
            {
                error = $"Width and height cannot exceed {MaxDimension}×{MaxDimension}.";
                return false;
            }

            return true;
        }

        if (preset.Resolution == ExportResolution.MatchTimeline)
        {
            (width, height) = EvenSize(timelineWidth, timelineHeight);
            return true;
        }

        var shortSide = ShortSide(preset.Resolution);
        if (shortSide < MinDimension)
        {
            error = "That export size is not available.";
            return false;
        }

        if (timelineWidth < 1 || timelineHeight < 1)
        {
            error = "The timeline has no picture size.";
            return false;
        }

        if (timelineWidth <= timelineHeight)
        {
            width = shortSide;
            height = NearestEvenRatio(shortSide, timelineHeight, timelineWidth);
        }
        else
        {
            height = shortSide;
            width = NearestEvenRatio(shortSide, timelineWidth, timelineHeight);
        }

        if (width > MaxDimension || height > MaxDimension)
        {
            error = $"{width}×{height} is past the {MaxDimension} pixel limit, so this size is not available.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Largest even picture of the timeline's shape that fits inside the box, centred.
    /// The same shape returns the box itself, so a matching custom size is not padded.
    /// </summary>
    public static FittedExport FitInside(int timelineWidth, int timelineHeight, int boxWidth, int boxHeight)
    {
        if (timelineWidth < 1 || timelineHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(timelineWidth));
        if (boxWidth < MinDimension || boxHeight < MinDimension)
            throw new ArgumentOutOfRangeException(nameof(boxWidth));

        if ((long)timelineWidth * boxHeight == (long)timelineHeight * boxWidth)
            return FittedExport.Exact(boxWidth, boxHeight);

        int contentW;
        int contentH;
        // The limiting axis is the one where the timeline is relatively larger.
        if ((long)boxWidth * timelineHeight <= (long)boxHeight * timelineWidth)
        {
            contentW = boxWidth;
            contentH = FloorEvenRatio(boxWidth, timelineHeight, timelineWidth);
            if (contentH > boxHeight) contentH = boxHeight - (boxHeight & 1);
        }
        else
        {
            contentH = boxHeight;
            contentW = FloorEvenRatio(boxHeight, timelineWidth, timelineHeight);
            if (contentW > boxWidth) contentW = boxWidth - (boxWidth & 1);
        }

        contentW = Math.Clamp(contentW, MinDimension, boxWidth);
        contentH = Math.Clamp(contentH, MinDimension, boxHeight);
        if ((contentW & 1) != 0) contentW = Math.Max(MinDimension, contentW - 1);
        if ((contentH & 1) != 0) contentH = Math.Max(MinDimension, contentH - 1);

        return new FittedExport(
            boxWidth,
            boxHeight,
            contentW,
            contentH,
            (boxWidth - contentW) / 2,
            (boxHeight - contentH) / 2);
    }

    /// <summary>Even integer closest to <c>scale * numer / denom</c>.</summary>
    private static int NearestEvenRatio(int scale, int numer, int denom)
    {
        var num = (long)scale * numer;
        var q = num / denom;
        var rem = num % denom;
        if (rem * 2 >= denom) q++;
        if ((q & 1) != 0)
        {
            var down = q - 1;
            var up = q + 1;
            var distDown = Abs(num - down * denom);
            var distUp = Abs(num - up * denom);
            q = distDown <= distUp ? down : up;
        }

        if (q < MinDimension) return MinDimension;
        if (q > int.MaxValue) return int.MaxValue;
        return (int)q;
    }

    /// <summary>Even integer at or below <c>scale * numer / denom</c>, so the picture stays inside the box.</summary>
    private static int FloorEvenRatio(int scale, int numer, int denom)
    {
        var q = (long)scale * numer / denom;
        if ((q & 1) != 0) q--;
        if (q < MinDimension) return MinDimension;
        if (q > int.MaxValue) return int.MaxValue;
        return (int)q;
    }

    private static long Abs(long value) => value < 0 ? -value : value;
}
