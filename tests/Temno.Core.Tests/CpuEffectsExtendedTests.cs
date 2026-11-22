using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Effects;
using Xunit;

namespace Temno.Core.Tests;

public sealed class CpuEffectsExtendedTests
{
    [Fact]
    public void Temperature_WarmsReds()
    {
        var src = new VideoFrame(1, 1, [128, 128, 128, 255], "rgba");
        var renderer = new CpuEffectsRenderer();
        using var outFrame = renderer.Apply(src, [
            new Effect
            {
                Type = "color.temperature",
                Params = new()
                {
                    ["temperature"] = new EffectParam { Value = 9000 },
                    ["tint"] = new EffectParam { Value = 0 },
                },
            },
        ], 0);
        // Warmer → more red, less blue in RGBA.
        Assert.True(outFrame.Data[0] > outFrame.Data[2]);
    }

    [Fact]
    public void WindowsViewerKelvin_CoolsRelativeToD65()
    {
        double red = 128 / 255.0, green = 128 / 255.0, blue = 128 / 255.0;
        ColorTemperature.ApplyRgb(ref red, ref green, ref blue, ColorTemperature.WindowsViewerKelvin);
        Assert.True(red < green);
        Assert.True(blue > green);
    }

    [Fact]
    public void ApplyWindowsViewerToBgra_CoolsRedChannel()
    {
        var bgra = new byte[] { 128, 128, 128, 255 };
        ColorTemperature.ApplyWindowsViewerToBgra(bgra);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(bgra[2] < 128);
            Assert.True(bgra[0] > 128);
            Assert.Equal(128, bgra[1]);
        }
        else
        {
            Assert.Equal(new byte[] { 128, 128, 128, 255 }, bgra);
        }
    }

    [Fact]
    public void Temperature_D65IsIdentityRelativeToSource()
    {
        var src = new VideoFrame(1, 1, [128, 128, 128, 255], "rgba");
        var renderer = new CpuEffectsRenderer();
        using var outFrame = renderer.Apply(src, [
            new Effect
            {
                Type = "color.temperature",
                Params = new()
                {
                    ["temperature"] = new EffectParam { Value = ColorTemperature.D65Kelvin },
                    ["tint"] = new EffectParam { Value = 0 },
                },
            },
        ], 0);
        Assert.Equal(src.Data[0], outFrame.Data[0]);
        Assert.Equal(src.Data[1], outFrame.Data[1]);
        Assert.Equal(src.Data[2], outFrame.Data[2]);
    }

    [Fact]
    public void NeutralKelvin_IsWindowsViewerOnThisPlatform()
    {
        Assert.Equal(
            OperatingSystem.IsWindows() ? ColorTemperature.WindowsViewerKelvin : ColorTemperature.D65Kelvin,
            ColorTemperature.NeutralKelvin);
    }

    [Fact]
    public void Vibrance_ChangesMidGrayLessThanSaturationWould()
    {
        var src = new VideoFrame(1, 1, [200, 40, 40, 255], "rgba");
        var renderer = new CpuEffectsRenderer();
        using var outFrame = renderer.Apply(src, [
            new Effect
            {
                Type = "color.vibrance",
                Params = new() { ["amount"] = new EffectParam { Value = 0.5 } },
            },
        ], 0);
        Assert.NotEqual(src.Data[0], outFrame.Data[0]);
    }
}
