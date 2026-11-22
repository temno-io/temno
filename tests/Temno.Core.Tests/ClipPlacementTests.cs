using Temno.Core.Editing;
using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-405: a picture keeps its own aspect inside its transform box. A 1152x2048 still saved with a
/// full-canvas box on 1920x1080 drew stretched 3.2x wide; it now draws at full height, pillarboxed.
/// </summary>
public sealed class ClipPlacementTests
{
    private const int Hd = 10;

    [Fact]
    public void PortraitInFullCanvasBox_FitsAtFullHeight()
    {
        var box = new Transform();
        var drawn = ClipPlacement.FitInBox(box, 1152, 2048, 1920, 1080);

        Assert.Equal(0.31640625, drawn.Width, Hd);   // 607.5 px of 1920
        Assert.Equal(1.0, drawn.Height, Hd);
        Assert.Equal(0.5, drawn.CenterX, Hd);
        Assert.Equal(0.5, drawn.CenterY, Hd);
        // The stored box is never edited by drawing it.
        Assert.Equal(1.0, box.Width, Hd);
    }

    [Fact]
    public void LandscapeInTallBox_FitsAtFullWidth()
    {
        var box = new Transform { CenterX = 0.25, CenterY = 0.6, Width = 0.5, Height = 0.8 };
        // A 960x864 box holding a 16:9 picture: full width, 540 px tall.
        var drawn = ClipPlacement.FitInBox(box, 1920, 1080, 1920, 1080);

        Assert.Equal(0.5, drawn.Width, Hd);
        Assert.Equal(0.5, drawn.Height, Hd);
        Assert.Equal(0.25, drawn.CenterX, Hd);
        Assert.Equal(0.6, drawn.CenterY, Hd);
    }

    [Fact]
    public void FittedBox_IsReturnedUnchanged()
    {
        var box = LayoutEngine.FitTransform(1152, 2048, 1920, 1080);
        Assert.Same(box, ClipPlacement.FitInBox(box, 1152, 2048, 1920, 1080));

        // A preview decode rounds the picture to even sizes; that is not a shape change.
        Assert.Same(box, ClipPlacement.FitInBox(box, 720, 1280, 1280, 720));
    }

    [Fact]
    public void WithinMacAspectTolerance_DrawsTheBox()
    {
        // 1920x1088 on a 1920x1080 canvas: 0.012 apart, which FitTransform also fills.
        var box = new Transform();
        Assert.Same(box, ClipPlacement.FitInBox(box, 1920, 1088, 1920, 1080));
        Assert.Equal(1.0, LayoutEngine.FitTransform(1920, 1088, 1920, 1080).Width, Hd);
    }

    [Fact]
    public void RotationAndFlips_AreKept()
    {
        var box = new Transform { Rotation = 30, FlipHorizontal = true, FlipVertical = true };
        var drawn = ClipPlacement.FitInBox(box, 1080, 1920, 1920, 1080);

        Assert.Equal(30, drawn.Rotation, Hd);
        Assert.True(drawn.FlipHorizontal);
        Assert.True(drawn.FlipVertical);
    }

    [Theory]
    [InlineData(0, 1080)]
    [InlineData(1920, 0)]
    public void UnknownPictureSize_DrawsTheBox(double width, double height)
    {
        var box = new Transform();
        Assert.Same(box, ClipPlacement.FitInBox(box, width, height, 1920, 1080));
    }

    [Fact]
    public void Crop_DrawsTheVisibleRegionWhereItSat()
    {
        // Whole picture fills a 1000x500 canvas; the right half stays where it was.
        var placed = new Transform();
        var region = ClipPlacement.VisibleRegion(placed, 200, 100, new Crop { Left = 0.5 }, 1000, 500);

        Assert.NotNull(region);
        Assert.Equal((100, 0, 100, 100), (region!.X, region.Y, region.Width, region.Height));
        Assert.Equal(0.75, region.Placement.CenterX, Hd);
        Assert.Equal(0.5, region.Placement.CenterY, Hd);
        Assert.Equal(0.5, region.Placement.Width, Hd);
        Assert.Equal(1.0, region.Placement.Height, Hd);
    }

    [Fact]
    public void Crop_FlipMirrorsWhereTheRegionSits()
    {
        var placed = new Transform { FlipHorizontal = true };
        var region = ClipPlacement.VisibleRegion(placed, 200, 100, new Crop { Left = 0.5 }, 1000, 500);

        // The picture's right half shows on the canvas's left when flipped.
        Assert.Equal(0.25, region!.Placement.CenterX, Hd);
        Assert.True(region.Placement.FlipHorizontal);
    }

    [Fact]
    public void Crop_TurnsAboutThePictureCentre()
    {
        // Rotated 90 degrees clockwise, the right half of a 400x400 picture lands below its centre.
        var placed = new Transform { Width = 0.4, Height = 0.4, Rotation = 90 };
        var region = ClipPlacement.VisibleRegion(placed, 100, 100, new Crop { Left = 0.5 }, 1000, 1000);

        Assert.Equal(0.5, region!.Placement.CenterX, 9);
        Assert.Equal(0.6, region.Placement.CenterY, 9);
        Assert.Equal(0.2, region.Placement.Width, Hd);
        Assert.Equal(0.4, region.Placement.Height, Hd);
        Assert.Equal(90, region.Placement.Rotation, Hd);
    }

    [Fact]
    public void CropThatLeavesNothing_ReturnsNull()
    {
        Assert.Null(ClipPlacement.VisibleRegion(new Transform(), 100, 100, new Crop { Left = 0.6, Right = 0.4 }, 100, 100));
    }
}
