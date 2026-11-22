using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

public sealed class PopoutPlacementTests
{
    [Fact]
    public void TopHeaderAnchor_InCompactWindow_OpensBelowAndFitsInsideWindow()
    {
        // Mimics Preview's 1920x1080 button in an unmaximized ~720px-tall Temno window.
        var layout = PopoutPlacement.Resolve(
            anchorTop: 66,
            anchorBottom: 90,
            windowHeight: 720,
            preferredSide: PopoutSide.Bottom,
            margin: 24);

        Assert.Equal(PopoutSide.Bottom, layout.Side);
        Assert.Equal(606, layout.MaxHeight);
    }

    [Fact]
    public void TopHeaderAnchor_InVeryShortWindow_NeverFlipsAboveWhenMoreRoomBelow()
    {
        var layout = PopoutPlacement.Resolve(
            anchorTop: 66,
            anchorBottom: 90,
            windowHeight: 240,
            preferredSide: PopoutSide.Bottom,
            margin: 24);

        Assert.Equal(PopoutSide.Bottom, layout.Side);
        Assert.Equal(126, layout.MaxHeight);
    }

    [Fact]
    public void BottomBarAnchor_OpensAboveAndCapsHeightToRoomInsideWindow()
    {
        // Mimics Preview's Guides button in a compact window (~520px full menu vs room below title bar).
        var layout = PopoutPlacement.Resolve(
            anchorTop: 480,
            anchorBottom: 504,
            windowHeight: 720,
            preferredSide: PopoutSide.Top,
            margin: 24,
            topMargin: 44);

        Assert.Equal(PopoutSide.Top, layout.Side);
        Assert.Equal(436, layout.MaxHeight);
    }

    [Fact]
    public void BottomAnchoredPopover_RespectsExplicitCapAndShrinksWhenShort()
    {
        var comfortable = PopoutPlacement.Resolve(
            anchorTop: 460,
            anchorBottom: 484,
            windowHeight: 720,
            preferredSide: PopoutSide.Top,
            margin: 24,
            maxCap: 360);
        Assert.Equal(PopoutSide.Top, comfortable.Side);
        Assert.Equal(360, comfortable.MaxHeight);

        var tight = PopoutPlacement.Resolve(
            anchorTop: 260,
            anchorBottom: 284,
            windowHeight: 500,
            preferredSide: PopoutSide.Top,
            margin: 24,
            maxCap: 360);
        Assert.Equal(PopoutSide.Top, tight.Side);
        Assert.Equal(236, tight.MaxHeight);
    }

    [Fact]
    public void NearBottomPreferredBottomAnchor_FlipsUpIntoWindowWhenCrampedBelow()
    {
        // Mimics Timeline's ToolOverflowButton near bottom in normal layout.
        var normalLayout = PopoutPlacement.Resolve(
            anchorTop: 505,
            anchorBottom: 533,
            windowHeight: 720,
            preferredSide: PopoutSide.Bottom,
            margin: 24);
        Assert.Equal(PopoutSide.Top, normalLayout.Side);
        Assert.Equal(481, normalLayout.MaxHeight);

        // When Timeline is maximized, the same toolbar sits at the top of the window.
        var maximizedTimeline = PopoutPlacement.Resolve(
            anchorTop: 44,
            anchorBottom: 72,
            windowHeight: 720,
            preferredSide: PopoutSide.Bottom,
            margin: 24);
        Assert.Equal(PopoutSide.Bottom, maximizedTimeline.Side);
        Assert.Equal(624, maximizedTimeline.MaxHeight);
    }

    [Fact]
    public void PreferredTopAnchorNearTopOfShortWindow_FlipsBelowIntoWindow()
    {
        var layout = PopoutPlacement.Resolve(
            anchorTop: 110,
            anchorBottom: 134,
            windowHeight: 420,
            preferredSide: PopoutSide.Top,
            margin: 24,
            maxCap: 360);

        Assert.Equal(PopoutSide.Bottom, layout.Side);
        Assert.Equal(262, layout.MaxHeight);
    }

    [Fact]
    public void ContextMaxHeight_LeavesTopAndBottomMarginAndHonorsCap()
    {
        Assert.Equal(672, PopoutPlacement.ContextMaxHeight(windowHeight: 720, margin: 24));
        Assert.Equal(400, PopoutPlacement.ContextMaxHeight(windowHeight: 720, margin: 24, maxCap: 400));
        Assert.Equal(352, PopoutPlacement.ContextMaxHeight(windowHeight: 400, margin: 24, maxCap: 504));
    }

    [Fact]
    public void PopoverWidth_ClampsToNarrowWindow()
    {
        Assert.Equal(304, PopoutPlacement.PopoverWidth(preferredWidth: 304, windowWidth: 1000, margin: 24));
        Assert.Equal(232, PopoutPlacement.PopoverWidth(preferredWidth: 304, windowWidth: 280, margin: 24));
    }

    [Fact]
    public void NonPositiveWindowSize_FallsBackSafely()
    {
        var layout = PopoutPlacement.Resolve(0, 24, 0, PopoutSide.Bottom, 24, maxCap: 360);
        Assert.Equal(PopoutSide.Bottom, layout.Side);
        Assert.Equal(360, layout.MaxHeight);
        Assert.Equal(304, PopoutPlacement.PopoverWidth(304, 0, 24));
    }
}
