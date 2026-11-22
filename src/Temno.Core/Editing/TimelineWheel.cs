namespace Temno.Core.Editing;

public enum TimelineWheelAction
{
    VerticalScroll,
    Zoom,
    HorizontalPan,
}

/// <summary>
/// Pointer-wheel mapping and zoom-anchor math for the timeline (WP-146).
/// WinUI-free so the cursor-stay-put offset can be locked in Core.Tests.
/// </summary>
public static class TimelineWheel
{
    /// <summary>Per-notch scale. Same factor the Windows handler used before this package.</summary>
    public const double WheelZoomFactor = 1.15;

    public static TimelineWheelAction Resolve(bool control, bool shift, bool alt, bool horizontalWheel)
    {
        if (control)
            return TimelineWheelAction.Zoom;
        if (shift || alt || horizontalWheel)
            return TimelineWheelAction.HorizontalPan;
        return TimelineWheelAction.VerticalScroll;
    }

    /// <summary>
    /// Horizontal offset that keeps <paramref name="contentX"/> (document X under the cursor)
    /// at the same viewport X after pixels-per-frame changes.
    /// </summary>
    /// <remarks>
    /// The previous handler snapped to <c>(int)(contentX / ppf)</c>, so a cursor sitting between
    /// frames jumped — which reads as zooming around the viewport edge.
    /// </remarks>
    public static double ZoomHorizontalOffset(
        double contentX,
        double oldPixelsPerFrame,
        double newPixelsPerFrame,
        double currentOffset)
    {
        if (oldPixelsPerFrame <= 0)
            return Math.Max(0, currentOffset);
        var viewportX = contentX - currentOffset;
        var newContentX = contentX * (newPixelsPerFrame / oldPixelsPerFrame);
        return Math.Max(0, newContentX - viewportX);
    }

    /// <summary>
    /// The pre-WP-146 integer-frame offset. Tests use it as the counterexample.
    /// </summary>
    public static double LegacyIntegerFrameOffset(
        double contentX,
        double oldPixelsPerFrame,
        double newPixelsPerFrame,
        double currentOffset)
    {
        if (oldPixelsPerFrame <= 0)
            return Math.Max(0, currentOffset);
        var frameUnder = Math.Max(0, (int)(contentX / oldPixelsPerFrame));
        var newX = frameUnder * newPixelsPerFrame;
        return Math.Max(0, currentOffset + (newX - contentX));
    }
}
