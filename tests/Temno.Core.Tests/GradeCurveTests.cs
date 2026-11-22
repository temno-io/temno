using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Effects;
using Xunit;

namespace Temno.Core.Tests;

public sealed class GradeCurveTests
{
    [Fact]
    public void Sample_IdentityEmpty_ReturnsInput()
    {
        Assert.Equal(0.4, GradeCurve.Sample(null, 0.4), 5);
        Assert.Equal(0.4, GradeCurve.Sample([], 0.4), 5);
    }

    [Fact]
    public void Sample_PiecewiseLinear_Interpolates()
    {
        var pts = new List<CurvePoint>
        {
            new(0, 0),
            new(0.5, 1),
            new(1, 0.5),
        };
        Assert.Equal(0, GradeCurve.Sample(pts, 0), 5);
        Assert.Equal(1, GradeCurve.Sample(pts, 0.5), 5);
        Assert.Equal(0.5, GradeCurve.Sample(pts, 0.25), 5);
        Assert.Equal(0.75, GradeCurve.Sample(pts, 0.75), 5);
    }

    [Fact]
    public void EncodeDecode_RoundTripsMaster()
    {
        var curve = new GradeCurve
        {
            Master =
            [
                new CurvePoint(0, 0),
                new CurvePoint(0.25, 0.3),
                new CurvePoint(0.5, 0.5),
                new CurvePoint(0.75, 0.7),
                new CurvePoint(1, 1),
            ],
        };
        var json = curve.Encoded();
        Assert.NotNull(json);
        var decoded = GradeCurve.Decode(json!);
        Assert.NotNull(decoded);
        Assert.Equal(5, decoded!.Master.Count);
        Assert.Equal(0.3, decoded.Master[1].Y, 5);
        Assert.False(decoded.IsIdentity);
    }

    [Fact]
    public void CpuRenderer_AppliesMasterCurve()
    {
        // Crush midtones toward black: out@0.5 = 0.
        var curve = new GradeCurve
        {
            Master =
            [
                new CurvePoint(0, 0),
                new CurvePoint(0.5, 0),
                new CurvePoint(1, 1),
            ],
        };
        var json = curve.Encoded()!;
        var src = new VideoFrame(1, 1, [128, 128, 128, 255], "rgba");
        var renderer = new CpuEffectsRenderer();
        using var outFrame = renderer.Apply(src, [
            new Effect
            {
                Type = "color.curves",
                Params = new() { ["curve"] = new EffectParam { String = json } },
            },
        ], 0);
        // Mid gray should be darker after crushing mids.
        Assert.True(outFrame.Data[0] < 128);
        Assert.True(outFrame.Data[1] < 128);
        Assert.True(outFrame.Data[2] < 128);
    }
}
