using Temno.Core.Models;

namespace Temno.Core.Editing;

/// <summary>
/// Pure layout math for the timeline (Mac: Timeline/TimelineGeometry.swift).
/// Frames → X uses <see cref="PixelsPerFrame"/>; Y uses per-track heights.
/// </summary>
public sealed class TimelineGeometry
{
    public const double DefaultPixelsPerFrame = 4.0;
    /// <summary>Mac <c>Zoom.floor</c> — Fit on a multi-hour timeline must go below 0.05 px/frame.</summary>
    public const double MinPixelsPerFrame = 0.0001;
    public const double MaxPixelsPerFrame = 40.0;
    public const double DefaultTrackHeight = 50.0;
    public const double MinTrackHeight = 32.0;
    public const double MaxTrackHeight = 200.0;
    public const double TrackResizeHandleZone = 6.0;
    public const double DefaultRulerHeight = 24.0;
    public const double DefaultHeaderWidth = 100.0;
    public const double ClipVerticalInset = 2.0;
    public const double EndPaddingFrames = 90;

    public TimelineGeometry(
        double pixelsPerFrame,
        IReadOnlyList<double> trackHeights,
        double headerWidth = DefaultHeaderWidth,
        double rulerHeight = DefaultRulerHeight)
    {
        PixelsPerFrame = Math.Clamp(pixelsPerFrame, MinPixelsPerFrame, MaxPixelsPerFrame);
        HeaderWidth = headerWidth;
        RulerHeight = rulerHeight;
        TrackHeights = trackHeights.Count == 0 ? [DefaultTrackHeight] : trackHeights.ToArray();

        var cum = new double[TrackHeights.Count];
        var y = RulerHeight;
        for (var i = 0; i < TrackHeights.Count; i++)
        {
            cum[i] = y;
            y += TrackHeights[i];
        }
        CumulativeY = cum;
        ContentHeight = y;
    }

    public static TimelineGeometry FromTimeline(Timeline timeline, double pixelsPerFrame, double headerWidth = DefaultHeaderWidth, double rulerHeight = DefaultRulerHeight)
    {
        var heights = timeline.Tracks
            .Select(t => ClampTrackHeight(t.DisplayHeight > 0 ? t.DisplayHeight : DefaultTrackHeight))
            .ToArray();
        if (heights.Length == 0)
            heights = [DefaultTrackHeight];
        return new TimelineGeometry(pixelsPerFrame, heights, headerWidth, rulerHeight);
    }

    public double PixelsPerFrame { get; }
    public double HeaderWidth { get; }
    public double RulerHeight { get; }
    public IReadOnlyList<double> TrackHeights { get; }
    public IReadOnlyList<double> CumulativeY { get; }
    public double ContentHeight { get; }
    public int TrackCount => TrackHeights.Count;

    public static double ClampTrackHeight(double height)
        => double.IsFinite(height)
            ? Math.Clamp(height, MinTrackHeight, MaxTrackHeight)
            : DefaultTrackHeight;

    public double TrackHeightAt(int index)
        => index >= 0 && index < TrackHeights.Count ? TrackHeights[index] : DefaultTrackHeight;

    public double TrackYAt(int index)
        => index >= 0 && index < CumulativeY.Count ? CumulativeY[index] : RulerHeight;

    public double XForFrame(int frame) => HeaderWidth + frame * PixelsPerFrame;

    public int FrameAtX(double x)
    {
        if (PixelsPerFrame <= 0) return 0;
        return Math.Max(0, (int)Math.Floor((x - HeaderWidth) / PixelsPerFrame));
    }

    public int TrackAtY(double y)
    {
        for (var i = 0; i < CumulativeY.Count; i++)
        {
            if (y < CumulativeY[i] + TrackHeights[i])
                return i;
        }
        return Math.Max(0, TrackCount - 1);
    }

    /// <summary>Clip body rect in content coordinates (includes header offset on X).</summary>
    public (double X, double Y, double Width, double Height) ClipRect(Clip clip, int trackIndex)
    {
        var h = TrackHeightAt(trackIndex);
        var w = Math.Max(1.0, clip.DurationFrames * PixelsPerFrame);
        return (
            XForFrame(clip.StartFrame),
            TrackYAt(trackIndex) + ClipVerticalInset,
            w,
            Math.Max(1.0, h - ClipVerticalInset * 2));
    }

    public double ContentWidth(int totalFrames)
        => HeaderWidth + Math.Max(totalFrames + EndPaddingFrames, 30) * PixelsPerFrame;

    /// <summary>
    /// Frame span that covers <paramref name="viewLeft"/>..<paramref name="viewRight"/> (content X,
    /// header already subtracted) plus one major-tick pad. Mac TimelineRuler.
    /// </summary>
    public static (int StartFrame, int EndFrame) VisibleTickFrames(
        double viewLeft,
        double viewRight,
        double pixelsPerFrame,
        int majorIntervalFrames)
    {
        if (pixelsPerFrame <= 0 || !double.IsFinite(pixelsPerFrame))
            return (0, 0);
        var pad = Math.Max(1, majorIntervalFrames);
        var start = Math.Max(0, (int)Math.Floor(viewLeft / pixelsPerFrame) - pad);
        var end = Math.Max(start, (int)Math.Ceiling(Math.Max(viewLeft, viewRight) / pixelsPerFrame) + pad);
        return (start, end);
    }

    /// <summary>Overlap of [x, x+width] with [viewLeft, viewRight], or null if none.</summary>
    public static (double X, double Width)? IntersectHorizontal(
        double x,
        double width,
        double viewLeft,
        double viewRight)
    {
        var left = Math.Max(x, viewLeft);
        var right = Math.Min(x + width, viewRight);
        if (right <= left) return null;
        return (left, right - left);
    }

    /// <summary>Major tick interval in frames targeting ~80px between labels.</summary>
    public static int MajorTickIntervalFrames(double pixelsPerFrame, int fps)
    {
        if (pixelsPerFrame <= 0 || fps <= 0) return fps > 0 ? fps : 30;
        var rawFrames = 80.0 / pixelsPerFrame;
        int[] secondMultiples = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200, 1800, 3600];
        foreach (var sec in secondMultiples)
        {
            var frames = sec * fps;
            if (frames >= rawFrames)
                return frames;
        }
        return secondMultiples[^1] * fps;
    }

    public static int MinorSubdivisions(int framesPerMajor, double pixelsPerFrame)
    {
        var majorPixels = framesPerMajor * pixelsPerFrame;
        foreach (var divisions in new[] { 10, 5, 4, 2 })
        {
            if (majorPixels / divisions >= 12)
                return divisions;
        }
        return 0;
    }

    public static string FormatTimecode(int frame, int fps)
    {
        if (fps <= 0) fps = 30;
        frame = Math.Max(0, frame);
        var ff = frame % fps;
        var totalSeconds = frame / fps;
        var ss = totalSeconds % 60;
        var mm = (totalSeconds / 60) % 60;
        var hh = totalSeconds / 3600;
        return hh > 0
            ? $"{hh}:{mm:D2}:{ss:D2}:{ff:D2}"
            : $"{mm:D2}:{ss:D2}:{ff:D2}";
    }

    /// <summary>
    /// WP-184. Always <c>HH:MM:SS:FF</c>, the Mac transport's shape. <see cref="FormatTimecode"/>
    /// drops the hours below one hour, so "01:15:00" read as an hour and a quarter to anyone
    /// arriving from another editor; the transport now uses this and keeps one shape. The ruler and
    /// every existing caller keep <see cref="FormatTimecode"/>.
    /// </summary>
    /// <summary>
    /// The frame the monitor should draw. Playback parks on the exclusive out-point
    /// (<paramref name="totalFrames"/>), and no clip contains that frame, so the preview
    /// would otherwise say the playhead is empty. The out-point still shows the last frame.
    /// </summary>
    public static int MonitorPictureFrame(int playhead, int totalFrames)
    {
        if (totalFrames <= 0) return Math.Max(0, playhead);
        if (playhead >= totalFrames) return totalFrames - 1;
        return Math.Max(0, playhead);
    }

    public static string FormatTimecodeFull(int frame, int fps)
    {
        if (fps <= 0) fps = 30;
        frame = Math.Max(0, frame);
        var ff = frame % fps;
        var totalSeconds = frame / fps;
        var ss = totalSeconds % 60;
        var mm = (totalSeconds / 60) % 60;
        var hh = totalSeconds / 3600;
        return $"{hh:D2}:{mm:D2}:{ss:D2}:{ff:D2}";
    }

    /// <summary>
    /// WP-200. Consolas advance at the ruler's <c>AppTheme.FontSize.Xs</c> (10), used only to
    /// decide which label form fits — never for layout, which WinUI still owns.
    /// </summary>
    public const double RulerLabelCharPx = 6.0;

    /// <summary>WP-200. <c>DrawRuler</c> draws each label at tick x + this offset.</summary>
    public const double RulerLabelOffsetPx = 3.0;

    /// <summary>WP-200. Breathing room kept before the next tick or the viewport edge.</summary>
    public const double RulerLabelGapPx = 4.0;

    /// <summary>
    /// WP-200. Ruler label that abbreviates by available spacing: the full timecode when it fits,
    /// then the time without frames, then total seconds, then no label at all. The tick still
    /// draws in every case, so labels never overlap and never drop to one every N ticks.
    /// </summary>
    public static string RulerLabel(int frame, int fps, double availablePx)
    {
        if (!double.IsFinite(availablePx) || availablePx < RulerLabelCharPx + RulerLabelGapPx)
            return "";
        var full = FormatTimecode(frame, fps);
        if (LabelFits(full, availablePx)) return full;
        var compact = CompactTimecode(frame, fps);
        if (LabelFits(compact, availablePx)) return compact;
        if (fps <= 0) fps = 30;
        var seconds = $"{Math.Max(0, frame) / fps}s";
        if (LabelFits(seconds, availablePx)) return seconds;
        return "";
    }

    private static bool LabelFits(string text, double availablePx)
        => text.Length * RulerLabelCharPx + RulerLabelGapPx <= availablePx;

    /// <summary>WP-200. <see cref="FormatTimecode"/> without frames and without padded leaders.</summary>
    public static string CompactTimecode(int frame, int fps)
    {
        if (fps <= 0) fps = 30;
        frame = Math.Max(0, frame);
        var totalSeconds = frame / fps;
        var ss = totalSeconds % 60;
        var mm = (totalSeconds / 60) % 60;
        var hh = totalSeconds / 3600;
        return hh > 0 ? $"{hh}:{mm:D2}:{ss:D2}" : $"{mm}:{ss:D2}";
    }

    /// <summary>Timeline frame under live viewport X (floor, for scrub/seek).</summary>
    public static int FrameAtViewportX(double viewX, double horizontalOffset, double pixelsPerFrame)
    {
        if (!double.IsFinite(viewX) || !double.IsFinite(horizontalOffset) || !double.IsFinite(pixelsPerFrame))
            return 0;
        return Math.Max(0, (int)Math.Floor((horizontalOffset + viewX) / Math.Max(pixelsPerFrame, MinPixelsPerFrame)));
    }

    /// <summary>Timeline frame under live viewport X (rounded, for clip drop placement).</summary>
    public static int DropFrameAtViewportX(double viewX, double horizontalOffset, double pixelsPerFrame)
    {
        if (!double.IsFinite(viewX) || !double.IsFinite(horizontalOffset) || !double.IsFinite(pixelsPerFrame))
            return 0;
        return Math.Max(0, (int)Math.Round(
            (horizontalOffset + viewX) / Math.Max(pixelsPerFrame, MinPixelsPerFrame),
            MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// Next horizontal scroll offset when scrubbing or dragging near the left/right viewport edge,
    /// or <see langword="null"/> when no scroll is needed.
    /// </summary>
    public static double? AutoScrollHorizontalOffset(
        double viewX,
        double currentOffset,
        double viewportWidth,
        double contentWidth,
        double scrollableWidth,
        double edgePx = 36)
    {
        if (!double.IsFinite(viewX) || !double.IsFinite(currentOffset) || !double.IsFinite(viewportWidth) || viewportWidth <= 0)
            return null;
        var offset = Math.Max(0, currentOffset);
        if (viewX < edgePx)
        {
            var next = Math.Max(0, offset - (edgePx - viewX));
            return next < offset - 0.01 ? next : null;
        }
        if (viewX > viewportWidth - edgePx)
        {
            var safeContent = double.IsFinite(contentWidth) ? contentWidth : 0;
            var safeScrollable = double.IsFinite(scrollableWidth) ? scrollableWidth : 0;
            var maxOffset = Math.Max(safeScrollable, Math.Max(0, safeContent - viewportWidth));
            var next = Math.Min(maxOffset, offset + (viewX - (viewportWidth - edgePx)));
            return next > offset + 0.01 ? next : null;
        }
        return null;
    }

    /// <summary>
    /// Extra vertical height appended to the track-header canvas so a header ScrollViewer
    /// (horizontal scrollbar disabled) shares identical <c>ScrollableHeight</c> with the body
    /// ScrollViewer when a non-overlay horizontal scrollbar shrinks the body's viewport height.
    /// </summary>
    public static double HeaderScrollHeightAllowance(
        double headerViewportHeight,
        double bodyViewportHeight,
        double bodyActualHeight)
    {
        if (double.IsFinite(headerViewportHeight) && headerViewportHeight > 1
            && double.IsFinite(bodyViewportHeight) && bodyViewportHeight > 1)
        {
            return Math.Max(0, headerViewportHeight - bodyViewportHeight);
        }
        if (double.IsFinite(bodyViewportHeight) && bodyViewportHeight > 1
            && double.IsFinite(bodyActualHeight) && bodyActualHeight > 1)
        {
            return Math.Max(0, bodyActualHeight - bodyViewportHeight);
        }
        return 0;
    }

    /// <summary>
    /// Lane-Y threshold at which dragging audio below the last track creates a new audio track.
    /// Strictly below the last track when at least <paramref name="bottomDropLip"/> DIPs of empty
    /// gutter exist below the tracks; otherwise reserves a small bottom lip so new-track creation
    /// remains reachable when tracks fill or overflow the viewport.
    /// </summary>
    public static double BottomNewAudioTrackThreshold(
        double contentHeight,
        double viewportHeight,
        double scrollableHeight,
        double bottomDropLip)
    {
        var content = double.IsFinite(contentHeight) ? Math.Max(0, contentHeight) : 0;
        var lip = double.IsFinite(bottomDropLip) ? Math.Max(0, bottomDropLip) : 0;
        var fillsViewport = (double.IsFinite(scrollableHeight) && scrollableHeight > 1)
            || (double.IsFinite(viewportHeight) && viewportHeight > 1 && content + lip > viewportHeight);
        return fillsViewport ? Math.Max(0, content - lip) : content;
    }
}
