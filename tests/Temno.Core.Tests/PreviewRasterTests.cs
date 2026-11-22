using Temno.Media;
using Xunit;

namespace Temno.Core.Tests;

public sealed class PreviewRasterTests
{
    [Fact]
    public void Fit_1440p_KeepsNative()
    {
        var (w, h) = PreviewRaster.Fit(2560, 1440);
        Assert.Equal(2560, w);
        Assert.Equal(1440, h);
    }

    [Fact]
    public void Fit_4K_FitsMaxEdge()
    {
        var (w, h) = PreviewRaster.Fit(3840, 2160);
        Assert.Equal(PreviewRaster.DefaultMaxDimension, w);
        Assert.Equal(1440, h);
    }

    [Fact]
    public void Fit_AlreadySmall_KeepsEvenSize()
    {
        var (w, h) = PreviewRaster.Fit(1280, 720);
        Assert.Equal(1280, w);
        Assert.Equal(720, h);
    }

    [Fact]
    public void Fit_OddDimensions_BecomeEven()
    {
        var (w, h) = PreviewRaster.Fit(641, 361, maxDimension: 641);
        Assert.Equal(0, w % 2);
        Assert.Equal(0, h % 2);
        Assert.True(w >= 2 && h >= 2);
    }

    [Fact]
    public void Fit_Explicit1280Cap_StillHalves1440p()
    {
        var (w, h) = PreviewRaster.Fit(2560, 1440, maxDimension: 1280);
        Assert.Equal(1280, w);
        Assert.Equal(720, h);
    }

    [Fact]
    public void PreviewMaxDimension_Quarters1440pPixels()
    {
        // WP-163: the interactive preview cap. A 2560x1440 canvas composites at 1280x720.
        var (w, h) = PreviewRaster.Fit(2560, 1440, PreviewRaster.PreviewMaxDimension);
        Assert.Equal(1280, w);
        Assert.Equal(720, h);
        Assert.Equal(PreviewRaster.PreviewMaxDimension, Math.Max(w, h));
    }
}
