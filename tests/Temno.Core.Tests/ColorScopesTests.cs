using Temno.Media;
using Xunit;

namespace Temno.Core.Tests;

public class ColorScopesTests
{
    private const float Tolerance = 0.001f;

    private static VideoFrame SolidFrame(int w, int h, byte r, byte g, byte b, string format = "rgba")
    {
        var data = new byte[w * h * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = r;
            data[i + 1] = g;
            data[i + 2] = b;
            data[i + 3] = 255;
        }
        return new VideoFrame(w, h, data, format);
    }

    [Fact]
    public void SolidBlack_ClipsAllShadows()
    {
        using var frame = SolidFrame(16, 16, 0, 0, 0);
        var report = ColorScopes.Measure(frame);

        Assert.Equal(1f, report.ClipLow, Tolerance);
        Assert.Equal(0f, report.ClipHigh, Tolerance);
        Assert.Equal(0f, report.LumaMean, Tolerance);
        Assert.Equal(0f, report.LumaBlack, Tolerance);
        Assert.Equal(0f, report.LumaWhite, Tolerance);

        Assert.Equal(16, report.LumaHistogram.Length);
        Assert.Equal(1f, report.LumaHistogram[0], Tolerance);
        for (var i = 1; i < 16; i++) Assert.Equal(0f, report.LumaHistogram[i], Tolerance);

        Assert.Equal(0f, report.MeanRgb.R, Tolerance);
        Assert.Equal(0f, report.MeanRgb.G, Tolerance);
        Assert.Equal(0f, report.MeanRgb.B, Tolerance);
        Assert.Equal(0f, report.SaturationMean, Tolerance);
        Assert.Equal(0f, report.ColorfulPct, Tolerance);

        // All samples land in the shadow zone; mid/high zones stay zero (Mac zone()).
        Assert.Equal(default, report.MidRgb);
        Assert.Equal(default, report.HighRgb);
    }

    [Fact]
    public void SolidWhite_ClipsAllHighlights()
    {
        using var frame = SolidFrame(16, 16, 255, 255, 255);
        var report = ColorScopes.Measure(frame);

        Assert.Equal(1f, report.ClipHigh, Tolerance);
        Assert.Equal(0f, report.ClipLow, Tolerance);
        Assert.Equal(1f, report.LumaMean, Tolerance);
        Assert.Equal(1f, report.LumaWhite, Tolerance);
        Assert.Equal(1f, report.LumaHistogram[15], Tolerance);
        for (var i = 0; i < 15; i++) Assert.Equal(0f, report.LumaHistogram[i], Tolerance);

        Assert.Equal(1f, report.MeanRgb.R, Tolerance);
        Assert.Equal(1f, report.MeanRgb.G, Tolerance);
        Assert.Equal(1f, report.MeanRgb.B, Tolerance);
        Assert.Equal(1f, report.WhiteRgb.R, Tolerance);
        Assert.Equal(0f, report.SaturationMean, Tolerance);
        Assert.Equal(0f, report.WarmCoolBias, Tolerance);
        Assert.Equal(0f, report.GreenMagentaBias, Tolerance);
        Assert.Equal(default, report.ShadowRgb);
        Assert.Equal(default, report.MidRgb);
    }

    [Fact]
    public void SolidMidGray_ZeroSaturationMidLuma()
    {
        using var frame = SolidFrame(16, 16, 128, 128, 128);
        var report = ColorScopes.Measure(frame);

        var expectedLuma = 128f / 255f; // ≈ 0.502
        Assert.Equal(expectedLuma, report.LumaMean, Tolerance);
        Assert.Equal(0f, report.SaturationMean, Tolerance);
        Assert.Equal(0f, report.ClipLow, Tolerance);
        Assert.Equal(0f, report.ClipHigh, Tolerance);
        Assert.Equal(0f, report.ColorfulPct, Tolerance);

        // 0.502 * 16 = 8.03 → bin 8 holds everything.
        Assert.Equal(1f, report.LumaHistogram[8], Tolerance);

        // Mid zone (1/3 ≤ luma ≤ 2/3) collects all samples.
        Assert.Equal(expectedLuma, report.MidRgb.R, Tolerance);
        Assert.Equal(expectedLuma, report.MidRgb.G, Tolerance);
        Assert.Equal(expectedLuma, report.MidRgb.B, Tolerance);
        Assert.Equal(default, report.ShadowRgb);
        Assert.Equal(default, report.HighRgb);
    }

    [Fact]
    public void PureRed_FullSaturationWarmBias()
    {
        using var frame = SolidFrame(16, 16, 255, 0, 0);
        var report = ColorScopes.Measure(frame);

        Assert.Equal(1f, report.SaturationMean, Tolerance);
        Assert.Equal(1f, report.MeanRgb.R, Tolerance);
        Assert.Equal(0f, report.MeanRgb.G, Tolerance);
        Assert.Equal(0f, report.MeanRgb.B, Tolerance);
        Assert.Equal(1f, report.WarmCoolBias, Tolerance);       // meanR − meanB
        Assert.Equal(-0.5f, report.GreenMagentaBias, Tolerance); // meanG − (meanR+meanB)/2
        Assert.Equal(1f, report.ColorfulPct, Tolerance);

        // Hue 0° → first 30° bin gets all the saturation weight.
        Assert.Equal(12, report.HueHistogram.Length);
        Assert.Equal(1f, report.HueHistogram[0], Tolerance);
        for (var i = 1; i < 12; i++) Assert.Equal(0f, report.HueHistogram[i], Tolerance);

        // Rec.709 red luma 0.2126 → 0.2126*16 = 3.4 → bin 3.
        Assert.Equal(1f, report.LumaHistogram[3], Tolerance);
    }

    [Fact]
    public void HalfBlackHalfWhite_BimodalHistogram()
    {
        const int w = 16, h = 16;
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v = x < w / 2 ? (byte)0 : (byte)255;
                var i = (y * w + x) * 4;
                data[i] = v; data[i + 1] = v; data[i + 2] = v; data[i + 3] = 255;
            }
        }
        using var frame = new VideoFrame(w, h, data, "rgba");
        var report = ColorScopes.Measure(frame);

        Assert.Equal(0.5f, report.LumaMean, Tolerance);
        Assert.Equal(0.5f, report.MeanRgb.R, Tolerance);
        Assert.Equal(0.5f, report.MeanRgb.G, Tolerance);
        Assert.Equal(0.5f, report.MeanRgb.B, Tolerance);

        Assert.Equal(0.5f, report.LumaHistogram[0], Tolerance);
        Assert.Equal(0.5f, report.LumaHistogram[15], Tolerance);
        for (var i = 1; i < 15; i++) Assert.Equal(0f, report.LumaHistogram[i], Tolerance);

        Assert.Equal(0.5f, report.ClipLow, Tolerance);
        Assert.Equal(0.5f, report.ClipHigh, Tolerance);

        // 2nd/98th percentiles sit inside the black/white halves.
        Assert.Equal(0f, report.LumaBlack, Tolerance);
        Assert.Equal(1f, report.LumaWhite, Tolerance);
    }

    [Fact]
    public void BgraAndRgba_ProduceEquivalentReports()
    {
        // Asymmetric color so a channel swap would show up in every RGB metric.
        using var rgba = SolidFrame(8, 8, 200, 64, 32, "rgba");

        var bgraData = new byte[8 * 8 * 4];
        for (var i = 0; i < bgraData.Length; i += 4)
        {
            bgraData[i] = 32;      // B
            bgraData[i + 1] = 64;  // G
            bgraData[i + 2] = 200; // R
            bgraData[i + 3] = 255;
        }
        using var bgra = new VideoFrame(8, 8, bgraData, "bgra");

        var a = ColorScopes.Measure(rgba);
        var b = ColorScopes.Measure(bgra);

        Assert.Equal(a.LumaMean, b.LumaMean, Tolerance);
        Assert.Equal(a.MeanRgb, b.MeanRgb);
        Assert.Equal(a.BlackRgb, b.BlackRgb);
        Assert.Equal(a.WhiteRgb, b.WhiteRgb);
        Assert.Equal(a.ShadowRgb, b.ShadowRgb);
        Assert.Equal(a.MidRgb, b.MidRgb);
        Assert.Equal(a.HighRgb, b.HighRgb);
        Assert.Equal(a.SaturationMean, b.SaturationMean, Tolerance);
        Assert.Equal(a.WarmCoolBias, b.WarmCoolBias, Tolerance);
        Assert.Equal(a.GreenMagentaBias, b.GreenMagentaBias, Tolerance);
        Assert.Equal(a.ColorfulPct, b.ColorfulPct, Tolerance);
        Assert.Equal(a.LumaHistogram, b.LumaHistogram);
        Assert.Equal(a.HueHistogram, b.HueHistogram);
    }

    [Fact]
    public void InvalidPixelFormat_Throws()
    {
        using var frame = SolidFrame(4, 4, 10, 10, 10, "yuv420p");
        Assert.Throws<NotSupportedException>(() => ColorScopes.Measure(frame));
    }

    [Fact]
    public void UndersizedData_Throws()
    {
        using var frame = new VideoFrame(4, 4, new byte[4 * 4 * 4 - 1], "rgba");
        Assert.Throws<ArgumentException>(() => ColorScopes.Measure(frame));
    }

    [Fact]
    public void NonPositiveGridEdge_Throws()
    {
        using var frame = SolidFrame(4, 4, 0, 0, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorScopes.Measure(frame, gridEdge: 0));
    }

    [Fact]
    public void HistogramsSumToOne()
    {
        using var frame = SolidFrame(16, 16, 255, 0, 0);
        var report = ColorScopes.Measure(frame);

        Assert.Equal(1f, report.LumaHistogram.Sum(), Tolerance);
        Assert.Equal(1f, report.HueHistogram.Sum(), Tolerance);
    }
}
