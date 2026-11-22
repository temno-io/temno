namespace Temno.Core.Ui;

/// <summary>Reserve picture height before allocating the automatic timeline row (DESIGN §6).</summary>
public static class PreviewPictureBudget
{
    public const double CompactTransportBreakpoint = 480;
    public const double CompactTransportExtraHeight = 28;

    // Title/status, root and panel insets, divider, preview tabs/scrub/transport.
    // These are layout geometry in DIPs, independent of project canvas pixels and OS scaling.
    private const double ShellAndPreviewChrome = 164;

    public static double MinimumColumnWidth(double windowHeight)
        => Math.Max(CompactTransportBreakpoint, PictureHeight(windowHeight, false) * 16 / 9 + 6);

    public static double TimelineHeight(double windowHeight, double previewWidth, double lanesHeight,
        double timelineChromeHeight, double defaultHeight, double minimumHeight)
    {
        if (!double.IsFinite(windowHeight) || windowHeight <= 0) return defaultHeight;
        var compact = previewWidth < CompactTransportBreakpoint;
        var chrome = ShellAndPreviewChrome + (compact ? CompactTransportExtraHeight : 0);
        var budget = Math.Max(minimumHeight, windowHeight - chrome - PictureHeight(windowHeight, compact));
        return ResponsiveShell.FitTimelineHeight(lanesHeight, timelineChromeHeight, windowHeight,
            Math.Min(defaultHeight, budget), Math.Min(0.42, budget / windowHeight));
    }

    private static double PictureHeight(double windowHeight, bool compact)
    {
        if (!double.IsFinite(windowHeight)) return 0;
        var chrome = ShellAndPreviewChrome + (compact ? CompactTransportExtraHeight : 0);
        return Math.Clamp((windowHeight - chrome) * 0.62, 0, 540);
    }
}
