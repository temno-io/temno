using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Xunit;

namespace Temno.Core.Tests;

public class CompositorTests
{
    private static VideoFrame SolidFrame(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var data = new byte[w * h * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = r;
            data[i + 1] = g;
            data[i + 2] = b;
            data[i + 3] = a;
        }
        return new VideoFrame(w, h, data, "rgba");
    }

    [Fact]
    public void EmptyLayers_ReturnsOpaqueBlack()
    {
        var compositor = new CpuCompositor();
        using var frame = compositor.Render([], 16, 16);

        Assert.Equal(16, frame.Width);
        Assert.Equal(16, frame.Height);
        Assert.Equal("rgba", frame.PixelFormat);
        Assert.Equal(16 * 16 * 4, frame.Data.Length);

        // Every pixel should be opaque black.
        for (var i = 0; i < frame.Data.Length; i += 4)
        {
            Assert.Equal(0, frame.Data[i]);     // R
            Assert.Equal(0, frame.Data[i + 1]); // G
            Assert.Equal(0, frame.Data[i + 2]); // B
            Assert.Equal(255, frame.Data[i + 3]); // A
        }
    }

    [Fact]
    public void SingleOpaqueLayer_FullCoverage()
    {
        var compositor = new CpuCompositor();
        using var red = SolidFrame(8, 8, 255, 0, 0);
        var layer = new CompositorLayer(red, opacity: 1.0);
        using var result = compositor.Render([layer], 8, 8);

        // Center pixel should be red.
        var mid = (4 * 8 + 4) * 4;
        Assert.Equal(255, result.Data[mid]);     // R
        Assert.Equal(0, result.Data[mid + 1]);   // G
        Assert.Equal(0, result.Data[mid + 2]);   // B
        Assert.Equal(255, result.Data[mid + 3]); // A
    }

    [Fact]
    public void HalfOpacity_BlendsWithBlackCanvas()
    {
        var compositor = new CpuCompositor();
        using var white = SolidFrame(8, 8, 255, 255, 255);
        var layer = new CompositorLayer(white, opacity: 0.5);
        using var result = compositor.Render([layer], 8, 8);

        // Center pixel at 50% opacity over black ≈ (127, 127, 127).
        var mid = (4 * 8 + 4) * 4;
        Assert.InRange(result.Data[mid], 126, 128);
        Assert.InRange(result.Data[mid + 1], 126, 128);
        Assert.InRange(result.Data[mid + 2], 126, 128);
    }

    [Fact]
    public void TwoLayers_TopCoversPreviousLayer()
    {
        var compositor = new CpuCompositor();
        using var red = SolidFrame(8, 8, 255, 0, 0);
        using var green = SolidFrame(8, 8, 0, 255, 0);

        var layers = new[]
        {
            new CompositorLayer(red, trackIndex: 1, opacity: 1.0),   // bottom
            new CompositorLayer(green, trackIndex: 0, opacity: 1.0), // top
        };
        using var result = compositor.Render(layers, 8, 8);

        var mid = (4 * 8 + 4) * 4;
        Assert.Equal(0, result.Data[mid]);       // R (green on top)
        Assert.Equal(255, result.Data[mid + 1]); // G
        Assert.Equal(0, result.Data[mid + 2]);   // B
    }

    [Fact]
    public void ZeroOpacityLayer_IsSkipped()
    {
        var compositor = new CpuCompositor();
        using var red = SolidFrame(8, 8, 255, 0, 0);
        var layer = new CompositorLayer(red, opacity: 0);
        using var result = compositor.Render([layer], 8, 8);

        // Should still be black (layer skipped).
        var mid = (4 * 8 + 4) * 4;
        Assert.Equal(0, result.Data[mid]);
        Assert.Equal(0, result.Data[mid + 1]);
        Assert.Equal(0, result.Data[mid + 2]);
    }

    [Fact]
    public void ScaledDownTransform_LeavesBlackBorder()
    {
        var compositor = new CpuCompositor();
        using var white = SolidFrame(8, 8, 255, 255, 255);

        // Place white at half size, centered.
        var transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.5, Height = 0.5 };
        var layer = new CompositorLayer(white, opacity: 1.0, transform: transform);
        using var result = compositor.Render([layer], 16, 16);

        // Corner pixel (0,0) should be black (outside the scaled-down image).
        Assert.Equal(0, result.Data[0]);
        Assert.Equal(0, result.Data[1]);
        Assert.Equal(0, result.Data[2]);

        // Center pixel (8,8) should be white.
        var mid = (8 * 16 + 8) * 4;
        Assert.Equal(255, result.Data[mid]);
        Assert.Equal(255, result.Data[mid + 1]);
        Assert.Equal(255, result.Data[mid + 2]);
    }

    [Fact]
    public void SemiTransparentSource_BlendsCorrectly()
    {
        var compositor = new CpuCompositor();
        // White with 50% alpha.
        using var semiWhite = SolidFrame(8, 8, 255, 255, 255, 128);
        var layer = new CompositorLayer(semiWhite, opacity: 1.0);
        using var result = compositor.Render([layer], 8, 8);

        // 128/255 ≈ 0.502 alpha → white × 0.502 + black × 0.498 ≈ 128.
        var mid = (4 * 8 + 4) * 4;
        Assert.InRange(result.Data[mid], 126, 130);
    }

    [Fact]
    public void FlipHorizontal_MirrorsContent()
    {
        var compositor = new CpuCompositor();
        // Left half red, right half blue.
        var data = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
        {
            var i = (y * 8 + x) * 4;
            if (x < 4) { data[i] = 255; data[i + 3] = 255; } // red left
            else { data[i + 2] = 255; data[i + 3] = 255; } // blue right
        }
        using var src = new VideoFrame(8, 8, data, "rgba");

        var transform = new Transform { FlipHorizontal = true };
        var layer = new CompositorLayer(src, opacity: 1.0, transform: transform);
        using var result = compositor.Render([layer], 8, 8);

        // After horizontal flip, the left side should be blue.
        var leftPx = (4 * 8 + 1) * 4;
        Assert.Equal(0, result.Data[leftPx]);       // R
        Assert.Equal(255, result.Data[leftPx + 2]);  // B (was originally on right)
    }

    [Fact]
    public void Upscale_BlendsInsteadOfNearestBlocks()
    {
        var compositor = new CpuCompositor();
        var data = new byte[2 * 1 * 4];
        data[0] = 255; data[3] = 255;       // red
        data[4 + 2] = 255; data[7] = 255;   // blue
        using var src = new VideoFrame(2, 1, data, "rgba");
        // WP-405: a 2:1 canvas, so the 2x1 picture fills it rather than fitting a 4x upscale inside an 8:1 box.
        using var result = compositor.Render([new CompositorLayer(src)], 8, 4);

        var mid = 4 * 4; // x=4, y=0
        Assert.InRange(result.Data[mid], 1, 254);       // R mixed, not a hard 0/255 block
        Assert.InRange(result.Data[mid + 2], 1, 254);   // B mixed
    }

    public static IEnumerable<object[]> MacBlendReferencePixels =>
    [
        ["normal", 204, 102, 51],
        ["darken", 64, 102, 51],
        ["multiply", 51, 51, 38],
        ["colorBurn", 16, 0, 0],
        ["lighten", 204, 128, 191],
        ["screen", 216, 178, 203],
        ["colorDodge", 255, 213, 238],
        ["overlay", 102, 102, 152],
        ["softLight", 102, 115, 162],
        ["hardLight", 178, 102, 76],
        ["difference", 140, 25, 140],
        ["exclusion", 165, 127, 165],
        ["hue", 179, 94, 52],
        ["saturation", 53, 130, 206],
        ["color", 192, 90, 39],
        ["luminosity", 75, 139, 202],
    ];

    [Theory]
    [MemberData(nameof(MacBlendReferencePixels))]
    public void MacBlendModes_MatchReferencePixels(string mode, int red, int green, int blue)
    {
        var compositor = new CpuCompositor();
        using var background = SolidFrame(1, 1, 64, 128, 191);
        using var source = SolidFrame(1, 1, 204, 102, 51);
        using var result = compositor.Render(
        [
            new CompositorLayer(background, trackIndex: 1),
            new CompositorLayer(source, trackIndex: 0, blendMode: mode),
        ], 1, 1);

        Assert.Equal(red, result.Data[0]);
        Assert.Equal(green, result.Data[1]);
        Assert.Equal(blue, result.Data[2]);
        Assert.Equal(255, result.Data[3]);
    }

    [Fact]
    public void NonNormalOpacity_FadesTheBlendResultBackToBackground()
    {
        var compositor = new CpuCompositor();
        using var background = SolidFrame(1, 1, 64, 128, 191);
        using var source = SolidFrame(1, 1, 204, 102, 51);
        using var result = compositor.Render(
        [
            new CompositorLayer(background, trackIndex: 1),
            new CompositorLayer(source, trackIndex: 0, opacity: 0.5, blendMode: "multiply"),
        ], 1, 1);

        // Background (64,128,191) half-way toward multiply result (51,51,38).
        Assert.Equal(new byte[] { 57, 89, 114, 255 }, result.Data);
    }

    [Fact]
    public void NonNormalSourceAlpha_AlsoFadesTheBlendResult()
    {
        var compositor = new CpuCompositor();
        using var background = SolidFrame(1, 1, 64, 128, 191);
        using var source = SolidFrame(1, 1, 204, 102, 51, 128);
        using var result = compositor.Render(
        [
            new CompositorLayer(background, trackIndex: 1),
            new CompositorLayer(source, trackIndex: 0, blendMode: "multiply"),
        ], 1, 1);

        Assert.Equal(new byte[] { 57, 89, 114, 255 }, result.Data);
    }

    [Fact]
    public void UnknownBlendMode_FallsBackToNormalSourceOver()
    {
        var compositor = new CpuCompositor();
        using var background = SolidFrame(1, 1, 64, 128, 191);
        using var source = SolidFrame(1, 1, 204, 102, 51);
        using var result = compositor.Render(
        [
            new CompositorLayer(background, trackIndex: 1),
            new CompositorLayer(source, trackIndex: 0, blendMode: "future-mode"),
        ], 1, 1);

        Assert.Equal(new byte[] { 204, 102, 51, 255 }, result.Data);
    }

    [Fact]
    public void BgraSources_AreReadInTheirDeclaredChannelOrder()
    {
        var compositor = new CpuCompositor();
        using var source = new VideoFrame(1, 1, [51, 102, 204, 255], "bgra");
        using var result = compositor.Render([new CompositorLayer(source)], 1, 1);

        Assert.Equal(new byte[] { 204, 102, 51, 255 }, result.Data);
    }
}
