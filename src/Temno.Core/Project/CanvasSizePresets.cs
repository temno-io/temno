namespace Temno.Core.Project;

/// <summary>
/// Shared canvas aspect/quality/FPS matching and custom validation used by both the Preview header
/// canvas picker and the Inspector's Project Settings section.
/// </summary>
public static class CanvasSizePresets
{
    public const int MinCanvasDimension = 16;
    public const int MaxCanvasDimension = 7680;
    public const int MinProjectFps = 1;
    public const int MaxProjectFps = 120;

    public static IReadOnlyList<(string Tag, string Label)> Aspects { get; } =
    [
        ("16:9", "16:9 · Landscape"),
        ("9:16", "9:16 · Vertical"),
        ("1:1", "1:1 · Square"),
        ("4:3", "4:3 · Standard"),
        ("2.4:1", "2.4:1 · Cinema"),
    ];

    public static IReadOnlyList<(string Tag, int ShortEdge)> Qualities { get; } =
    [
        ("720p", 720),
        ("1080p", 1080),
        ("2K", 1440),
        ("4K", 2160),
    ];

    public static IReadOnlyList<int> ProjectFrameRates { get; } = [24, 25, 30, 50, 60];

    public static string? MatchAspectTag(int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var aspect = width / (double)height;
        if (Math.Abs(aspect - 16.0 / 9.0) < 0.02) return "16:9";
        if (Math.Abs(aspect - 9.0 / 16.0) < 0.02) return "9:16";
        if (Math.Abs(aspect - 1.0) < 0.02) return "1:1";
        if (Math.Abs(aspect - 4.0 / 3.0) < 0.02) return "4:3";
        if (Math.Abs(aspect - 2.4) < 0.05) return "2.4:1";
        return null;
    }

    public static string? MatchQualityTag(int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var shortEdge = Math.Min(width, height);
        foreach (var (tag, edge) in Qualities)
        {
            if (shortEdge == edge)
                return tag;
        }
        return null;
    }

    public static string MatchProjectFpsTag(int fps)
    {
        foreach (var rate in ProjectFrameRates)
        {
            if (rate == fps)
                return rate.ToString();
        }
        return "custom";
    }

    public static bool TryValidateCustomCanvas(int width, int height, out string? error)
    {
        if (width < MinCanvasDimension || width > MaxCanvasDimension
            || height < MinCanvasDimension || height > MaxCanvasDimension)
        {
            error = $"Width and height must be between {MinCanvasDimension} and {MaxCanvasDimension} px.";
            return false;
        }
        if ((width & 1) != 0 || (height & 1) != 0)
        {
            error = "Width and height must be even numbers.";
            return false;
        }
        error = null;
        return true;
    }

    public static bool TryValidateProjectFps(int fps, out string? error)
    {
        if (fps < MinProjectFps || fps > MaxProjectFps)
        {
            error = $"Frame rate must be between {MinProjectFps} and {MaxProjectFps} fps.";
            return false;
        }
        error = null;
        return true;
    }
}
