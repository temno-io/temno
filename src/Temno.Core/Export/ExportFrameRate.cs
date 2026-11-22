using System.Globalization;

namespace Temno.Core.Export;

/// <summary>
/// Output frame rate selection and validation for video export. Mirrors Premiere's standard
/// broadcast/web preset ladder plus a custom rate (1–120 fps), emitting exact NTSC rationals
/// (<c>24000/1001</c>, <c>30000/1001</c>, <c>60000/1001</c>) for FFmpeg.
/// </summary>
public static class ExportFrameRate
{
    public const double MinFps = 1;
    public const double MaxFps = 120;

    public readonly record struct Preset(double Fps, string Label, string FfmpegExpression);

    public static IReadOnlyList<Preset> Presets { get; } =
    [
        new(10, "10 fps", "10"),
        new(12, "12 fps", "12"),
        new(15, "15 fps", "15"),
        new(24000.0 / 1001.0, "23.976 fps", "24000/1001"),
        new(24, "24 fps", "24"),
        new(25, "25 fps", "25"),
        new(30000.0 / 1001.0, "29.97 fps", "30000/1001"),
        new(30, "30 fps", "30"),
        new(48, "48 fps", "48"),
        new(50, "50 fps", "50"),
        new(60000.0 / 1001.0, "59.94 fps", "60000/1001"),
        new(60, "60 fps", "60"),
        new(120, "120 fps", "120"),
    ];

    public static string MatchTimelineLabel(int timelineFps)
        => $"Match Timeline ({Math.Max(1, timelineFps)} fps)";

    public static bool TryValidate(double fps, out string? error)
    {
        if (!double.IsFinite(fps) || fps < MinFps || fps > MaxFps)
        {
            error = $"Frame rate must be between {(int)MinFps} and {(int)MaxFps} fps.";
            return false;
        }
        error = null;
        return true;
    }

    public static bool MatchesTimeline(double? targetFps, int timelineFps)
        => targetFps is null || Math.Abs(targetFps.Value - timelineFps) < 0.0005;

    /// <summary>
    /// Resolves <paramref name="customFps"/> against <paramref name="timelineFps"/>, returning
    /// <c>null</c> when the export matches the timeline's frame rate (no resampling required).
    /// </summary>
    public static double? ResolveOverride(double? customFps, int timelineFps)
    {
        if (customFps is not { } fps)
            return null;
        if (!TryValidate(fps, out var error))
            throw new InvalidOperationException(error);
        return MatchesTimeline(fps, timelineFps) ? null : fps;
    }

    public static string ToFfmpegExpression(double fps)
    {
        if (!TryValidate(fps, out var error))
            throw new ArgumentOutOfRangeException(nameof(fps), error);
        foreach (var preset in Presets)
        {
            if (Math.Abs(fps - preset.Fps) < 0.005)
                return preset.FfmpegExpression;
        }
        var rounded = Math.Round(fps);
        if (Math.Abs(fps - rounded) < 0.0005)
            return ((int)rounded).ToString(CultureInfo.InvariantCulture);
        return fps.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public static string FormatLabel(double fps)
    {
        foreach (var preset in Presets)
        {
            if (Math.Abs(fps - preset.Fps) < 0.005)
                return preset.Label;
        }
        var rounded = Math.Round(fps);
        if (Math.Abs(fps - rounded) < 0.0005)
            return $"{(int)rounded} fps";
        return $"{fps.ToString("0.###", CultureInfo.InvariantCulture)} fps";
    }
}
