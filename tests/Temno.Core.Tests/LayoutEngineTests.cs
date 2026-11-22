using Temno.Core.Editing;
using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

public class LayoutEngineTests
{
    private const int Hd = 10; // decimal places for hand-computed doubles

    private static void AssertSlot(LayoutSlot slot, string name, double x, double y, double w, double h, int z = 0)
    {
        Assert.Equal(name, slot.Name);
        Assert.Equal(x, slot.Rect.X, 12);
        Assert.Equal(y, slot.Rect.Y, 12);
        Assert.Equal(w, slot.Rect.W, 12);
        Assert.Equal(h, slot.Rect.H, 12);
        Assert.Equal(z, slot.Z);
    }

    // -------- Slot tables (Mac: VideoLayout.slots) --------

    [Fact]
    public void Full_SingleMainSlot()
    {
        var slots = LayoutEngine.Slots(LayoutKind.Full);
        Assert.Single(slots);
        AssertSlot(slots[0], "main", 0, 0, 1, 1);
    }

    [Fact]
    public void SideBySide_TwoHalves()
    {
        var slots = LayoutEngine.Slots(LayoutKind.SideBySide);
        Assert.Equal(2, slots.Count);
        AssertSlot(slots[0], "left", 0, 0, 0.5, 1);
        AssertSlot(slots[1], "right", 0.5, 0, 0.5, 1);
    }

    [Fact]
    public void TopBottom_TwoHalves()
    {
        var slots = LayoutEngine.Slots(LayoutKind.TopBottom);
        Assert.Equal(2, slots.Count);
        AssertSlot(slots[0], "top", 0, 0, 1, 0.5);
        AssertSlot(slots[1], "bottom", 0, 0.5, 1, 0.5);
    }

    [Theory]
    [InlineData(LayoutKind.PipBottomRight, 1 - 0.035 - 0.28, 1 - 0.035 - 0.28)]
    [InlineData(LayoutKind.PipBottomLeft, 0.035, 1 - 0.035 - 0.28)]
    [InlineData(LayoutKind.PipTopRight, 1 - 0.035 - 0.28, 0.035)]
    [InlineData(LayoutKind.PipTopLeft, 0.035, 0.035)]
    public void Pip_MainPlusInset(LayoutKind kind, double insetX, double insetY)
    {
        var slots = LayoutEngine.Slots(kind);
        Assert.Equal(2, slots.Count);
        AssertSlot(slots[0], "main", 0, 0, 1, 1, z: 0);
        AssertSlot(slots[1], "inset", insetX, insetY, 0.28, 0.28, z: 1);
    }

    [Fact]
    public void Grid2x2_FourQuadrants()
    {
        var slots = LayoutEngine.Slots(LayoutKind.Grid2x2);
        Assert.Equal(4, slots.Count);
        AssertSlot(slots[0], "top_left", 0, 0, 0.5, 0.5);
        AssertSlot(slots[1], "top_right", 0.5, 0, 0.5, 0.5);
        AssertSlot(slots[2], "bottom_left", 0, 0.5, 0.5, 0.5);
        AssertSlot(slots[3], "bottom_right", 0.5, 0.5, 0.5, 0.5);
    }

    [Fact]
    public void MainSidebar_SeventyThirty()
    {
        var slots = LayoutEngine.Slots(LayoutKind.MainSidebar);
        Assert.Equal(2, slots.Count);
        AssertSlot(slots[0], "main", 0, 0, 0.7, 1);
        AssertSlot(slots[1], "sidebar", 0.7, 0, 0.3, 1);
    }

    [Fact]
    public void ThreeUp_Thirds()
    {
        var third = 1.0 / 3.0;
        var slots = LayoutEngine.Slots(LayoutKind.ThreeUp);
        Assert.Equal(3, slots.Count);
        AssertSlot(slots[0], "left", 0, 0, third, 1);
        AssertSlot(slots[1], "center", third, 0, third, 1);
        AssertSlot(slots[2], "right", third * 2, 0, third, 1);
    }

    [Fact]
    public void SlotNames_MatchMacOrdering()
    {
        Assert.Equal(new[] { "main" }, LayoutEngine.SlotNames(LayoutKind.Full));
        Assert.Equal(new[] { "left", "right" }, LayoutEngine.SlotNames(LayoutKind.SideBySide));
        Assert.Equal(new[] { "top", "bottom" }, LayoutEngine.SlotNames(LayoutKind.TopBottom));
        Assert.Equal(new[] { "main", "inset" }, LayoutEngine.SlotNames(LayoutKind.PipBottomRight));
        Assert.Equal(new[] { "top_left", "top_right", "bottom_left", "bottom_right" }, LayoutEngine.SlotNames(LayoutKind.Grid2x2));
        Assert.Equal(new[] { "main", "sidebar" }, LayoutEngine.SlotNames(LayoutKind.MainSidebar));
        Assert.Equal(new[] { "left", "center", "right" }, LayoutEngine.SlotNames(LayoutKind.ThreeUp));
    }

    [Fact]
    public void ParseKind_RoundTripsAllRawValues()
    {
        var raws = new[]
        {
            "full", "side_by_side", "top_bottom", "pip_bottom_right", "pip_bottom_left",
            "pip_top_right", "pip_top_left", "grid_2x2", "main_sidebar", "three_up",
        };
        Assert.Equal(raws, LayoutEngine.AllKinds.Select(LayoutEngine.RawValue));
        foreach (var raw in raws)
        {
            Assert.Equal(raw, LayoutEngine.RawValue(LayoutEngine.ParseKind(raw)));
        }
    }

    // -------- Fill (cover-crop): wide source into tall slot --------
    // side_by_side "left" on 1920x1080: slot pixel aspect = 0.5 * 16/9 = 8/9.
    // 16:9 source is twice as wide as the slot -> crop half the width.

    [Fact]
    public void Fill_WideIntoTallSlot_CenterAnchor()
    {
        var (t, c) = LayoutEngine.Place(LayoutKind.SideBySide, "left", 1920, 1080, 1920, 1080, "fill");
        Assert.Equal(0.25, c.Left, Hd);
        Assert.Equal(0.25, c.Right, Hd);
        Assert.Equal(0, c.Top, Hd);
        Assert.Equal(0, c.Bottom, Hd);
        // Visible half of the source spans the slot -> full transform width 1, shifted left.
        Assert.Equal(1.0, t.Width, Hd);
        Assert.Equal(1.0, t.Height, Hd);
        Assert.Equal(0.25, t.CenterX, Hd);
        Assert.Equal(0.5, t.CenterY, Hd);
    }

    [Fact]
    public void Fill_WideIntoTallSlot_LeftAnchor_KeepsLeftEdge()
    {
        var (t, c) = LayoutEngine.Place(LayoutKind.SideBySide, "left", 1920, 1080, 1920, 1080, "fill", "left");
        Assert.Equal(0, c.Left, Hd);
        Assert.Equal(0.5, c.Right, Hd);
        Assert.Equal(0.5, t.CenterX, Hd); // top-left at 0 with width 1
        Assert.Equal(1.0, t.Width, Hd);
    }

    [Fact]
    public void Fill_WideIntoTallSlot_RightAnchor_KeepsRightEdge()
    {
        var (t, c) = LayoutEngine.Place(LayoutKind.SideBySide, "left", 1920, 1080, 1920, 1080, "fill", "right");
        Assert.Equal(0.5, c.Left, Hd);
        Assert.Equal(0, c.Right, Hd);
        Assert.Equal(0.0, t.CenterX, Hd); // top-left at -0.5 with width 1
    }

    [Fact]
    public void Fill_WideIntoTallSlot_FineAnchorX()
    {
        var (t, c) = LayoutEngine.Place(
            LayoutKind.SideBySide, "left", 1920, 1080, 1920, 1080, "fill", anchorX: 0.25);
        Assert.Equal(0.125, c.Left, Hd);  // total crop 0.5 * anchorX 0.25
        Assert.Equal(0.375, c.Right, Hd);
        Assert.Equal(0.375, t.CenterX, Hd); // top-left = -0.125, width 1
    }

    // -------- Fill (cover-crop): tall source into wide slot --------
    // top_bottom "top" on 1920x1080: slot pixel aspect = 2 * 16/9 = 32/9.
    // 9:16 source: total vertical crop = 1 - (9/16)/(32/9) = 431/512.

    [Fact]
    public void Fill_TallIntoWideSlot_TopAnchor()
    {
        var (t, c) = LayoutEngine.Place(LayoutKind.TopBottom, "top", 1080, 1920, 1920, 1080, "fill", "top");
        Assert.Equal(0, c.Top, Hd);
        Assert.Equal(431.0 / 512.0, c.Bottom, Hd);
        Assert.Equal(0, c.Left, Hd);
        Assert.Equal(0, c.Right, Hd);
        // Visible fraction 81/512 must fill slot height 0.5 -> transform height 256/81.
        Assert.Equal(1.0, t.Width, Hd);
        Assert.Equal(256.0 / 81.0, t.Height, Hd);
        Assert.Equal(0.5, t.CenterX, Hd);
        Assert.Equal(128.0 / 81.0, t.CenterY, Hd); // top-left y = 0
    }

    [Fact]
    public void Fill_TallIntoWideSlot_CenterAnchor_CentersOnSlot()
    {
        var (t, c) = LayoutEngine.Place(LayoutKind.TopBottom, "top", 1080, 1920, 1920, 1080, "fill");
        Assert.Equal(431.0 / 1024.0, c.Top, Hd);
        Assert.Equal(431.0 / 1024.0, c.Bottom, Hd);
        Assert.Equal(256.0 / 81.0, t.Height, Hd);
        // Symmetric crop keeps the transform centered on the slot's center (y = 0.25).
        Assert.Equal(0.25, t.CenterY, Hd);
    }

    [Fact]
    public void Fill_MatchingAspect_IdentityCropAndSlotRect()
    {
        var (t, c) = LayoutEngine.Place(LayoutKind.Full, "main", 1920, 1080, 1920, 1080, "fill");
        Assert.True(c.IsIdentity);
        Assert.Equal(0.5, t.CenterX, Hd);
        Assert.Equal(0.5, t.CenterY, Hd);
        Assert.Equal(1.0, t.Width, Hd);
        Assert.Equal(1.0, t.Height, Hd);
    }

    [Fact]
    public void Fill_UnknownSourceDims_FillsSlotRectWithIdentityCrop()
    {
        var (t, c) = LayoutEngine.Place(LayoutKind.SideBySide, "right", 0, 0, 1920, 1080, "fill");
        Assert.True(c.IsIdentity);
        Assert.Equal(0.75, t.CenterX, Hd);
        Assert.Equal(0.5, t.CenterY, Hd);
        Assert.Equal(0.5, t.Width, Hd);
        Assert.Equal(1.0, t.Height, Hd);
    }

    // -------- Fit (letterbox) --------

    [Fact]
    public void Fit_TallIntoSquareSlot_PillarboxesCentered()
    {
        // grid_2x2 "top_left" on 1920x1080; 9:16 source. rel = (9/16)/(16/9) = 81/256.
        var (t, c) = LayoutEngine.Place(LayoutKind.Grid2x2, "top_left", 1080, 1920, 1920, 1080, "fit");
        Assert.True(c.IsIdentity);
        Assert.Equal(0.158203125, t.Width, Hd); // rel * slot height 0.5
        Assert.Equal(0.5, t.Height, Hd);
        Assert.Equal(0.25, t.CenterX, Hd); // centered in the quadrant
        Assert.Equal(0.25, t.CenterY, Hd);
    }

    [Fact]
    public void Fit_UltrawideIntoFull_LetterboxesWithAnchors()
    {
        // 32:9 source on 16:9 canvas: rel = 2 -> drawW = 1, drawH = 0.5.
        var (center, cc) = LayoutEngine.Place(LayoutKind.Full, "main", 3840, 1080, 1920, 1080, "fit");
        Assert.True(cc.IsIdentity);
        Assert.Equal(1.0, center.Width, Hd);
        Assert.Equal(0.5, center.Height, Hd);
        Assert.Equal(0.5, center.CenterY, Hd);

        var (top, _) = LayoutEngine.Place(LayoutKind.Full, "main", 3840, 1080, 1920, 1080, "fit", "top");
        Assert.Equal(0.25, top.CenterY, Hd); // flush to slot top

        var (bottom, _) = LayoutEngine.Place(LayoutKind.Full, "main", 3840, 1080, 1920, 1080, "fit", "bottom");
        Assert.Equal(0.75, bottom.CenterY, Hd); // flush to slot bottom
    }

    [Fact]
    public void Fit_UnknownSourceDims_FillsSlotRectWithIdentityCrop()
    {
        var (t, c) = LayoutEngine.Place(LayoutKind.Full, "main", 0, 0, 1920, 1080, "fit");
        Assert.True(c.IsIdentity);
        Assert.Equal(0.5, t.CenterX, Hd);
        Assert.Equal(0.5, t.CenterY, Hd);
        Assert.Equal(1.0, t.Width, Hd);
        Assert.Equal(1.0, t.Height, Hd);
    }

    // -------- Anchor resolution (Mac: ToolExecutor.resolveAnchor) --------

    [Fact]
    public void ResolveAnchor_DefaultsToCenter()
    {
        Assert.Equal((0.5, 0.5), LayoutEngine.ResolveAnchor(null, null, null));
    }

    [Theory]
    [InlineData("center", 0.5, 0.5)]
    [InlineData("top", 0.5, 0)]
    [InlineData("bottom", 0.5, 1)]
    [InlineData("left", 0, 0.5)]
    [InlineData("right", 1, 0.5)]
    [InlineData("top_left", 0, 0)]
    [InlineData("top_right", 1, 0)]
    [InlineData("bottom_left", 0, 1)]
    [InlineData("bottom_right", 1, 1)]
    public void ResolveAnchor_NamedAnchors(string name, double x, double y)
    {
        Assert.Equal((x, y), LayoutEngine.ResolveAnchor(name, null, null));
    }

    [Fact]
    public void ResolveAnchor_FineValuesOverridePerAxis()
    {
        Assert.Equal((0.75, 0.0), LayoutEngine.ResolveAnchor("top_left", 0.75, null));
        Assert.Equal((0.5, 0.2), LayoutEngine.ResolveAnchor(null, null, 0.2));
    }

    // -------- Errors --------

    [Fact]
    public void ParseKind_UnknownLayout_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => LayoutEngine.ParseKind("diagonal"));
        Assert.Contains("unknown layout 'diagonal'", ex.Message);
        Assert.Contains("side_by_side", ex.Message);
    }

    [Fact]
    public void Place_UnknownSlot_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => LayoutEngine.Place(LayoutKind.SideBySide, "middle", 1920, 1080, 1920, 1080));
        Assert.Contains("'middle' is not a slot of layout 'side_by_side'", ex.Message);
        Assert.Contains("left, right", ex.Message);
    }

    [Fact]
    public void ParseFit_Invalid_Throws_AndNullDefaultsToFill()
    {
        Assert.Equal(LayoutFit.Fill, LayoutEngine.ParseFit(null));
        Assert.Equal(LayoutFit.Fill, LayoutEngine.ParseFit("fill"));
        Assert.Equal(LayoutFit.Fit, LayoutEngine.ParseFit("fit"));
        var ex = Assert.Throws<ArgumentException>(() => LayoutEngine.ParseFit("stretch"));
        Assert.Contains("invalid fit 'stretch'", ex.Message);
    }

    [Fact]
    public void ResolveAnchor_InvalidNameOrRange_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => LayoutEngine.ResolveAnchor("middle", null, null));
        Assert.Contains("invalid anchor 'middle'", ex.Message);
        Assert.Throws<ArgumentException>(() => LayoutEngine.ResolveAnchor(null, 1.5, null));
        Assert.Throws<ArgumentException>(() => LayoutEngine.ResolveAnchor(null, null, -0.1));
    }
}
