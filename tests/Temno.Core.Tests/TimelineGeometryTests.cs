using Temno.Core.Editing;
using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

public sealed class TimelineGeometryTests
{
    [Fact]
    public void XForFrame_And_FrameAtX_RoundTrip()
    {
        var geo = new TimelineGeometry(pixelsPerFrame: 4, trackHeights: [50], headerWidth: 100);
        Assert.Equal(100, geo.XForFrame(0));
        Assert.Equal(100 + 40, geo.XForFrame(10));
        Assert.Equal(10, geo.FrameAtX(100 + 40));
        Assert.Equal(0, geo.FrameAtX(50)); // in header
    }

    [Fact]
    public void ClipRect_UsesStartAndDuration()
    {
        var geo = new TimelineGeometry(2, [50, 50], headerWidth: 100);
        var clip = new Clip { StartFrame = 10, DurationFrames = 20 };
        var (x, y, w, h) = geo.ClipRect(clip, trackIndex: 1);
        Assert.Equal(100 + 20, x);
        Assert.Equal(geo.TrackYAt(1) + TimelineGeometry.ClipVerticalInset, y);
        Assert.Equal(40, w);
        Assert.Equal(50 - TimelineGeometry.ClipVerticalInset * 2, h);
    }

    [Fact]
    public void TrackAtY_UsesCumulativeHeights()
    {
        var geo = new TimelineGeometry(4, [40, 60], headerWidth: 0, rulerHeight: 24);
        Assert.Equal(0, geo.TrackAtY(24 + 10));
        Assert.Equal(1, geo.TrackAtY(24 + 40 + 10));
    }

    [Fact]
    public void FormatTimecode_Basic()
    {
        Assert.Equal("00:00:00", TimelineGeometry.FormatTimecode(0, 30));
        Assert.Equal("00:01:00", TimelineGeometry.FormatTimecode(30, 30));
        Assert.Equal("00:01:15", TimelineGeometry.FormatTimecode(45, 30));
    }

    [Fact]
    public void MonitorPictureFrame_HoldsTheLastFrameAtTheOutPoint()
    {
        Assert.Equal(12, TimelineGeometry.MonitorPictureFrame(12, 90));
        Assert.Equal(89, TimelineGeometry.MonitorPictureFrame(90, 90));
        Assert.Equal(89, TimelineGeometry.MonitorPictureFrame(120, 90));
        Assert.Equal(0, TimelineGeometry.MonitorPictureFrame(0, 90));
    }

    [Fact]
    public void VisibleTickFrames_PadsByOneMajorInterval()
    {
        var (start, end) = TimelineGeometry.VisibleTickFrames(
            viewLeft: 400,
            viewRight: 1200,
            pixelsPerFrame: 4,
            majorIntervalFrames: 30);
        Assert.Equal(100 - 30, start);
        Assert.Equal(300 + 30, end);
    }

    [Fact]
    public void VisibleTickFrames_DoesNotEnumerateAnEightHourTimeline()
    {
        // 8h @ 30fps, default 4 px/frame, 1200px viewport at scroll 0.
        const int eightHours = 8 * 3600 * 30;
        var ppf = TimelineGeometry.DefaultPixelsPerFrame;
        var major = TimelineGeometry.MajorTickIntervalFrames(ppf, fps: 30);
        var (start, end) = TimelineGeometry.VisibleTickFrames(0, 1200, ppf, major);
        Assert.True(end - start < eightHours / 10);
        Assert.True(end < 2_000);
    }

    [Fact]
    public void IntersectHorizontal_ClipsToViewport()
    {
        var hit = TimelineGeometry.IntersectHorizontal(x: 0, width: 3_456_360, viewLeft: 800, viewRight: 2000);
        Assert.NotNull(hit);
        Assert.Equal(800, hit.Value.X);
        Assert.Equal(1200, hit.Value.Width);
        Assert.Null(TimelineGeometry.IntersectHorizontal(0, 100, 200, 400));
    }

    [Fact]
    public void FitZoom_ForEightHourTimeline_IsBelowLegacyMin()
    {
        const int eightHours = 8 * 3600 * 30;
        var fit = 1200.0 / (eightHours + TimelineGeometry.EndPaddingFrames * 0.25);
        Assert.True(fit < 0.05);
        Assert.True(fit >= TimelineGeometry.MinPixelsPerFrame);
        var clamped = Math.Clamp(fit, TimelineGeometry.MinPixelsPerFrame, TimelineGeometry.MaxPixelsPerFrame);
        Assert.Equal(fit, clamped);
    }

    [Theory]
    [InlineData(10, TimelineGeometry.MinTrackHeight)]
    [InlineData(50, 50)]
    [InlineData(500, TimelineGeometry.MaxTrackHeight)]
    [InlineData(double.NaN, TimelineGeometry.DefaultTrackHeight)]
    [InlineData(double.NegativeInfinity, TimelineGeometry.DefaultTrackHeight)]
    public void ClampTrackHeight_EnforcesMacRange(double input, double expected)
    {
        Assert.Equal(expected, TimelineGeometry.ClampTrackHeight(input));
    }

    [Fact]
    public void FromTimeline_ClampsDisplayHeights()
    {
        var timeline = new Timeline
        {
            Tracks =
            [
                new Track { Type = ClipType.video, DisplayHeight = 10 },
                new Track { Type = ClipType.audio, DisplayHeight = 400 },
            ],
        };
        // Track setter already clamps; force raw out-of-range geometry path via constructed heights list.
        var geo = TimelineGeometry.FromTimeline(timeline, pixelsPerFrame: 4, headerWidth: 0);
        Assert.Equal(TimelineGeometry.MinTrackHeight, geo.TrackHeightAt(0));
        Assert.Equal(TimelineGeometry.MaxTrackHeight, geo.TrackHeightAt(1));
        Assert.Equal(TimelineGeometry.DefaultRulerHeight, geo.TrackYAt(0));
    }

    [Fact]
    public void RulerLabel_FullTimecodeWhenItFits()
    {
        Assert.Equal("00:00:00", TimelineGeometry.RulerLabel(0, 30, 80));
        Assert.Equal("00:01:15", TimelineGeometry.RulerLabel(45, 30, 80));
        Assert.Equal("1:02:03:04", TimelineGeometry.RulerLabel((1 * 3600 + 2 * 60 + 3) * 30 + 4, 30, 80));
    }

    [Fact]
    public void RulerLabel_AbbreviatesByAvailableSpacing()
    {
        // Compact "0:05" is 4 chars (28 px); full "00:00:05" needs 52.
        Assert.Equal("0:05", TimelineGeometry.RulerLabel(5 * 30, 30, 40));
        Assert.Equal("1:00:00", TimelineGeometry.RulerLabel(3600 * 30, 30, 50));
        // Total seconds is shorter than the compact form past ten minutes.
        Assert.Equal("600s", TimelineGeometry.RulerLabel(600 * 30, 30, 30));
        // Nothing fits: tick only, never an overlap.
        Assert.Equal("", TimelineGeometry.RulerLabel(600 * 30, 30, 9));
        Assert.Equal("", TimelineGeometry.RulerLabel(0, 30, double.NaN));
    }

    [Fact]
    public void CompactTimecode_DropsFramesAndLeaders()
    {
        Assert.Equal("0:00", TimelineGeometry.CompactTimecode(0, 30));
        Assert.Equal("1:15", TimelineGeometry.CompactTimecode((60 + 15) * 30, 30));
        Assert.Equal("1:02:03", TimelineGeometry.CompactTimecode((3600 + 123) * 30, 30));
        Assert.Equal("0:00", TimelineGeometry.CompactTimecode(-5, 0));
    }

    [Fact]
    public void ViewportX_FrameConversion_UsesLiveHorizontalOffset()
    {
        // Horizontal offset 200 px + viewport X 22 px = 222 px @ 4 px/f => 55.5 frames
        Assert.Equal(55, TimelineGeometry.FrameAtViewportX(viewX: 22, horizontalOffset: 200, pixelsPerFrame: 4));
        Assert.Equal(56, TimelineGeometry.DropFrameAtViewportX(viewX: 22, horizontalOffset: 200, pixelsPerFrame: 4));
        // Negative viewport X left of scroll origin clamps to 0.
        Assert.Equal(0, TimelineGeometry.FrameAtViewportX(viewX: -50, horizontalOffset: 10, pixelsPerFrame: 4));
        Assert.Equal(0, TimelineGeometry.DropFrameAtViewportX(viewX: -50, horizontalOffset: 10, pixelsPerFrame: 4));
        // Non-finite inputs return 0 safely.
        Assert.Equal(0, TimelineGeometry.FrameAtViewportX(double.NaN, 100, 4));
        Assert.Equal(0, TimelineGeometry.DropFrameAtViewportX(double.PositiveInfinity, 100, 4));
    }

    [Fact]
    public void AutoScrollHorizontalOffset_ScrollsNearEdgesAndClampsBounds()
    {
        // Interior viewport X triggers no scroll.
        Assert.Null(TimelineGeometry.AutoScrollHorizontalOffset(viewX: 200, currentOffset: 100, viewportWidth: 600, contentWidth: 2000, scrollableWidth: 1400));

        // Left edge zone (< 36 px) scrolls left by (36 - viewX), clamped at 0.
        Assert.Equal(84, TimelineGeometry.AutoScrollHorizontalOffset(viewX: 20, currentOffset: 100, viewportWidth: 600, contentWidth: 2000, scrollableWidth: 1400));
        Assert.Equal(0, TimelineGeometry.AutoScrollHorizontalOffset(viewX: -20, currentOffset: 10, viewportWidth: 600, contentWidth: 2000, scrollableWidth: 1400));
        Assert.Null(TimelineGeometry.AutoScrollHorizontalOffset(viewX: 10, currentOffset: 0, viewportWidth: 600, contentWidth: 2000, scrollableWidth: 1400));

        // Right edge zone (> viewportWidth - 36) scrolls right and clamps to maxOffset even with NaN contentWidth.
        Assert.Equal(116, TimelineGeometry.AutoScrollHorizontalOffset(viewX: 580, currentOffset: 100, viewportWidth: 600, contentWidth: 2000, scrollableWidth: 1400));
        Assert.Equal(1400, TimelineGeometry.AutoScrollHorizontalOffset(viewX: 620, currentOffset: 1390, viewportWidth: 600, contentWidth: double.NaN, scrollableWidth: 1400));
        Assert.Null(TimelineGeometry.AutoScrollHorizontalOffset(viewX: 590, currentOffset: 1400, viewportWidth: 600, contentWidth: 2000, scrollableWidth: 1400));
    }

    [Fact]
    public void HeaderScrollHeightAllowance_AlignsMaxScrollableHeightWithNonOverlayScrollbar()
    {
        // Overlay scrollbars (equal viewport heights): zero allowance.
        Assert.Equal(0, TimelineGeometry.HeaderScrollHeightAllowance(headerViewportHeight: 250, bodyViewportHeight: 250, bodyActualHeight: 250));

        // Non-overlay horizontal scrollbar shrinks BodyScroll.ViewportHeight by 16 DIP:
        // adding the 16 DIP allowance to HeaderCanvas.Height equalizes ScrollableHeight.
        const double contentH = 560;
        const double headerViewport = 250;
        const double bodyViewport = 234;
        var allowance = TimelineGeometry.HeaderScrollHeightAllowance(headerViewport, bodyViewport, bodyActualHeight: 250);
        Assert.Equal(16, allowance);
        var bodyScrollable = contentH - bodyViewport;
        var headerScrollable = (contentH + allowance) - headerViewport;
        Assert.Equal(bodyScrollable, headerScrollable);

        // Fallback when HeaderScroll is not measured yet uses BodyScroll.ActualHeight - BodyScroll.ViewportHeight.
        Assert.Equal(14, TimelineGeometry.HeaderScrollHeightAllowance(headerViewportHeight: 0, bodyViewportHeight: 236, bodyActualHeight: 250));
        Assert.Equal(0, TimelineGeometry.HeaderScrollHeightAllowance(0, 0, 250));
        Assert.Equal(0, TimelineGeometry.HeaderScrollHeightAllowance(double.NaN, 234, double.NaN));
    }

    [Fact]
    public void BottomNewAudioTrackThreshold_HandlesEmptyGutterTightFitAndOverflow()
    {
        const double lip = 4;
        // Ample gutter below tracks: threshold is strictly below the last track.
        Assert.Equal(112, TimelineGeometry.BottomNewAudioTrackThreshold(contentHeight: 112, viewportHeight: 260, scrollableHeight: 0, bottomDropLip: lip));
        // Exact boundary (4 DIP gutter available): still strictly below the last track.
        Assert.Equal(220, TimelineGeometry.BottomNewAudioTrackThreshold(contentHeight: 220, viewportHeight: 224, scrollableHeight: 0, bottomDropLip: lip));
        // Tight fit (0..3 DIP gutter, ScrollableHeight == 0): bottom lip activates so DropTarget stays reachable.
        Assert.Equal(220, TimelineGeometry.BottomNewAudioTrackThreshold(contentHeight: 224, viewportHeight: 224, scrollableHeight: 0, bottomDropLip: lip));
        Assert.Equal(218, TimelineGeometry.BottomNewAudioTrackThreshold(contentHeight: 222, viewportHeight: 224, scrollableHeight: 0, bottomDropLip: lip));
        // Overflowing viewport (ScrollableHeight > 1): bottom lip activates.
        Assert.Equal(332, TimelineGeometry.BottomNewAudioTrackThreshold(contentHeight: 336, viewportHeight: 200, scrollableHeight: 136, bottomDropLip: lip));
    }
}
