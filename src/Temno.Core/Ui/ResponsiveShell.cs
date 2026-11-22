namespace Temno.Core.Ui;

/// <summary>
/// WP-185. How the editor's side columns give way when the window is narrower than their
/// preferred widths plus the picture. Media (420) and Inspector (300) used to be fixed, so at
/// 1366 px the preview column was left with about 330 px and the transport row clipped its own
/// timecode. This takes the deficit out of both columns in proportion to the slack each has above
/// its floor, and reports whether it had to, so the caller can keep the user's preferred widths
/// intact and restore them when the window grows again.
/// </summary>
public static class ResponsiveShell
{
    /// <summary>
    /// Returns the widths the two side columns should take right now. A column passed as 0 is
    /// hidden and is left alone. <c>Fitted</c> is true when the result differs from the preferred
    /// widths because of the window, false when the preferences fit as they are.
    /// </summary>
    public static (double Media, double Inspector, bool Fitted) FitSideColumns(
        double availableWidth,
        double preferredMedia,
        double preferredInspector,
        double minMedia,
        double minInspector,
        double minPreview,
        double splitter = 5)
    {
        if (availableWidth <= 0 || double.IsNaN(availableWidth))
            return (preferredMedia, preferredInspector, false);

        var splitters = (preferredMedia > 0 ? splitter : 0) + (preferredInspector > 0 ? splitter : 0);
        var deficit = preferredMedia + preferredInspector + splitters + minPreview - availableWidth;
        if (deficit <= 0)
            return (preferredMedia, preferredInspector, false);

        var mediaSlack = preferredMedia > 0 ? Math.Max(0, preferredMedia - minMedia) : 0;
        var inspectorSlack = preferredInspector > 0 ? Math.Max(0, preferredInspector - minInspector) : 0;
        var slack = mediaSlack + inspectorSlack;
        if (slack <= 0)
            return (preferredMedia, preferredInspector, false);

        var take = Math.Min(deficit, slack);
        return (
            Math.Round(preferredMedia - take * mediaSlack / slack),
            Math.Round(preferredInspector - take * inspectorSlack / slack),
            true);
    }

    /// <summary>
    /// The timeline row height that shows every track without scrolling, bounded below by the
    /// default and above by a share of the window so the preview keeps its room.
    /// </summary>
    public static double FitTimelineHeight(double lanesHeight, double chromeHeight, double windowHeight,
        double defaultHeight, double maxShare = 0.42)
    {
        var wanted = chromeHeight + lanesHeight;
        var cap = Math.Max(defaultHeight, windowHeight * maxShare);
        return Math.Round(Math.Clamp(wanted, defaultHeight, cap));
    }
}
