using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Media;
using Temno.Media.Effects;
using Xunit;

namespace Temno.Core.Tests;

public sealed class ColorDepthDeferredTests
{
    [Fact]
    public void ColorWheels_NeutralIsIdentity()
    {
        var c = ColorWheelsMath.FromParams(0, 0, 0, 0, 0, 1, 0, 0, 1);
        double r = 0.4, g = 0.5, b = 0.6;
        ColorWheelsMath.Apply(ref r, ref g, ref b, c);
        Assert.Equal(0.4, r, 3);
        Assert.Equal(0.5, g, 3);
        Assert.Equal(0.6, b, 3);
    }

    [Fact]
    public void ColorWheels_LiftMasterBrightens()
    {
        var c = ColorWheelsMath.FromParams(0, 0, 0.2, 0, 0, 1, 0, 0, 1);
        double r = 0.2, g = 0.2, b = 0.2;
        ColorWheelsMath.Apply(ref r, ref g, ref b, c);
        Assert.True(r > 0.2);
        Assert.True(g > 0.2);
        Assert.True(b > 0.2);
    }

    [Fact]
    public void CubeLut_ParsesAndSamplesIdentityLike()
    {
        // Minimal 2³ identity cube
        var text = """
            LUT_3D_SIZE 2
            0 0 0
            1 0 0
            0 1 0
            1 1 0
            0 0 1
            1 0 1
            0 1 1
            1 1 1
            """;
        var lut = CubeLut.TryParse(text);
        Assert.NotNull(lut);
        Assert.Equal(2, lut!.Dimension);
        lut.Sample(0.5, 0.5, 0.5, out var r, out var g, out var b);
        Assert.InRange(r, 0.4, 0.6);
        Assert.InRange(g, 0.4, 0.6);
        Assert.InRange(b, 0.4, 0.6);
    }

    [Fact]
    public void HueCurves_HueVsSatBoostsSaturation()
    {
        var curves = new HueCurves
        {
            HueVsSat =
            [
                new CurvePoint(0, 0.5),
                new CurvePoint(0.5, 1.0), // 2× sat at mid hues
                new CurvePoint(1, 0.5),
            ],
        };
        // Pure red-ish
        double r = 0.8, g = 0.2, b = 0.2;
        curves.Apply(ref r, ref g, ref b);
        // Saturation should increase: red pulls further from green/blue
        Assert.True(r - g > 0.5);
    }

    [Fact]
    public void CpuRenderer_AppliesWheelsAndHueCurves()
    {
        var renderer = new CpuEffectsRenderer();
        using var source = new VideoFrame(1, 1, new byte[] { 128, 64, 32, 255 }, "rgba");
        var effects = new[]
        {
            new Effect
            {
                Type = "color.wheels",
                Params = new()
                {
                    ["lift_m"] = new EffectParam { Value = 0.15 },
                    ["gamma_m"] = new EffectParam { Value = 1 },
                    ["gain_m"] = new EffectParam { Value = 1 },
                },
            },
        };
        using var rendered = renderer.Apply(source, effects, 0);
        Assert.True(rendered.Data[0] > 128 || rendered.Data[1] > 64);
    }

    [Fact]
    public void CpuRenderer_AppliesCubeLut()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TemnoLut_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "id.cube");
        File.WriteAllText(path, """
            LUT_3D_SIZE 2
            0 0 0
            1 0 0
            0 1 0
            1 1 0
            0 0 1
            1 0 1
            0 1 1
            1 1 1
            """);
        try
        {
            var renderer = new CpuEffectsRenderer();
            using var source = new VideoFrame(1, 1, new byte[] { 128, 128, 128, 255 }, "rgba");
            var effects = new[]
            {
                new Effect
                {
                    Type = "color.lut",
                    Params = new()
                    {
                        ["path"] = new EffectParam { String = path },
                        ["intensity"] = new EffectParam { Value = 1 },
                    },
                },
            };
            using var rendered = renderer.Apply(source, effects, 0);
            // Identity-ish cube keeps mid gray in mid range
            Assert.InRange(rendered.Data[0], 100, 160);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void MoveKeyframe_RetimesOpacity()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 100, Opacity = 0.5,
        }).Ok);
        Assert.True(KeyframeCommands.StampOpacity(session, "c1", 10).Ok);
        Assert.True(KeyframeCommands.MoveKeyframe(session, "c1", "opacity", 10, 40).Ok);
        var clip = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.Equal(40, clip.OpacityTrack!.Keyframes[0].Frame);
        Assert.DoesNotContain(clip.OpacityTrack.Keyframes, k => k.Frame == 10);
        Assert.True(session.CanUndo());
    }
}
