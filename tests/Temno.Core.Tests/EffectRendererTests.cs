using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Effects;
using Xunit;

namespace Temno.Core.Tests;

public sealed class EffectRendererTests
{
    private readonly CpuEffectsRenderer _renderer = new();

    [Fact]
    public void NullEffectStack_ReturnsTheSourceFrameWithoutCloning()
    {
        using var source = Frame(40, 80, 120, 33);

        var rendered = _renderer.Apply(source, null, clipRelativeFrame: 0);

        Assert.Same(source, rendered);
    }

    [Fact]
    public void EmptyEffectStack_ReturnsTheSourceFrameWithoutCloning()
    {
        using var source = Frame(40, 80, 120, 33);

        var rendered = _renderer.Apply(source, [], clipRelativeFrame: 0);

        Assert.Same(source, rendered);
    }

    [Fact]
    public void FullyBypassedEffectStack_ReturnsTheSourceFrameWithoutCloning()
    {
        using var source = Frame(40, 80, 120, 33);
        var effects = new[]
        {
            new Effect
            {
                Type = "color.exposure",
                Enabled = false,
                Params = new() { ["ev"] = new EffectParam { Value = 3 } },
            },
            new Effect
            {
                Type = "blur.gaussian",
                Enabled = false,
                Params = new() { ["radius"] = new EffectParam { Value = 8 } },
            },
        };

        var rendered = _renderer.Apply(source, effects, clipRelativeFrame: 0);

        Assert.Same(source, rendered);
    }

    [Fact]
    public void EnabledEffect_StillRendersIntoASeparateBuffer()
    {
        using var source = Frame(40, 80, 120, 33);
        var effects = new[]
        {
            new Effect
            {
                Type = "color.exposure",
                Params = new() { ["ev"] = new EffectParam { Value = 1 } },
            },
        };

        using var rendered = _renderer.Apply(source, effects, clipRelativeFrame: 0);

        Assert.NotSame(source, rendered);
        Assert.NotSame(source.Data, rendered.Data);
    }

    [Fact]
    public void NoRecognizedEffects_ReturnsAnUnmodifiedFrameCopy()
    {
        using var source = Frame(40, 80, 120, 33);
        var effects = new[]
        {
            new Effect { Type = "vendor.future", Params = new() },
            new Effect
            {
                Type = "color.exposure",
                Enabled = false,
                Params = new() { ["ev"] = new EffectParam { Value = 3 } },
            },
        };

        using var rendered = _renderer.Apply(source, effects, clipRelativeFrame: 0);

        Assert.NotSame(source, rendered);
        Assert.NotSame(source.Data, rendered.Data);
        Assert.Equal(source.Data, rendered.Data);
    }

    [Fact]
    public void MissingV1Parameters_UseRegistryDefaults()
    {
        using var source = Frame(40, 80, 120, 33);
        var effects = new[]
        {
            new Effect { Type = "color.exposure" },
            new Effect { Type = "color.contrast" },
            new Effect { Type = "color.saturation" },
        };

        using var rendered = _renderer.Apply(source, effects, clipRelativeFrame: 0);

        Assert.Equal(source.Data, rendered.Data);
    }

    [Fact]
    public void Exposure_UsesLinearSrgbAndPreservesAlphaWithoutMutatingSource()
    {
        using var source = Frame(128, 0, 0, 37);
        var effects = new[] { Effect("color.exposure", "ev", 1) };

        using var rendered = _renderer.Apply(source, effects, clipRelativeFrame: 0);

        // A gamma-space doubling would clamp 128 to 255. Linear-sRGB exposure is 176.
        Assert.Equal(176, rendered.Data[0]);
        Assert.Equal(0, rendered.Data[1]);
        Assert.Equal(0, rendered.Data[2]);
        Assert.Equal(37, rendered.Data[3]);
        Assert.Equal(new byte[] { 128, 0, 0, 37 }, source.Data);
    }

    [Fact]
    public void SaturationZero_UsesLumaAndPreservesAlpha()
    {
        using var source = Frame(255, 0, 0, 19);
        var effects = new[] { Effect("color.saturation", "amount", 0) };

        using var rendered = _renderer.Apply(source, effects, clipRelativeFrame: 0);

        Assert.Equal(new byte[] { 54, 54, 54, 19 }, rendered.Data);
    }

    [Fact]
    public void Effects_ApplyInStoredOrder()
    {
        using var source = Frame(64, 128, 192, 255);
        var exposureThenContrast = new[]
        {
            Effect("color.exposure", "ev", 1),
            Effect("color.contrast", "amount", 0.5),
        };
        var contrastThenExposure = new[]
        {
            Effect("color.contrast", "amount", 0.5),
            Effect("color.exposure", "ev", 1),
        };

        using var first = _renderer.Apply(source, exposureThenContrast, clipRelativeFrame: 0);
        using var second = _renderer.Apply(source, contrastThenExposure, clipRelativeFrame: 0);

        Assert.False(first.Data.SequenceEqual(second.Data));
    }

    [Fact]
    public void KeyframedParameters_ResolveAtClipRelativeFrame()
    {
        using var source = Frame(128, 0, 0, 211);
        var effect = new Effect
        {
            Type = "color.exposure",
            Params = new()
            {
                ["ev"] = new EffectParam
                {
                    Track = new KeyframeTrack<double>
                    {
                        Keyframes =
                        [
                            new Keyframe<double> { Frame = 0, Value = 0, InterpolationOut = Interpolation.linear },
                            new Keyframe<double> { Frame = 10, Value = 1, InterpolationOut = Interpolation.linear },
                        ],
                    },
                },
            },
        };

        using var atStart = _renderer.Apply(source, new[] { effect }, clipRelativeFrame: 0);
        using var atTen = _renderer.Apply(source, new[] { effect }, clipRelativeFrame: 10);

        Assert.Equal(128, atStart.Data[0]);
        Assert.Equal(176, atTen.Data[0]);
        Assert.Equal(211, atTen.Data[3]);
    }

    [Fact]
    public void Parameters_AreClampedToTheRegistryRange()
    {
        using var source = Frame(8, 0, 0, 255);
        var effects = new[] { Effect("color.exposure", "ev", 99) };

        using var rendered = _renderer.Apply(source, effects, clipRelativeFrame: 0);

        // 99 EV must clamp to the registry maximum of +3 EV, not white-clamp.
        Assert.InRange(rendered.Data[0], 37, 39);
    }

    [Fact]
    public void BgraFrames_AreRenderedWithoutChangingTheirDeclaredFormat()
    {
        using var source = new VideoFrame(1, 1, new byte[] { 0, 0, 128, 71 }, "bgra");
        var effects = new[] { Effect("color.exposure", "ev", 1) };

        using var rendered = _renderer.Apply(source, effects, clipRelativeFrame: 0);

        Assert.Equal("bgra", rendered.PixelFormat);
        Assert.Equal(new byte[] { 0, 0, 176, 71 }, rendered.Data);
    }

    private static VideoFrame Frame(byte red, byte green, byte blue, byte alpha)
        => new(1, 1, new byte[] { red, green, blue, alpha }, "rgba");

    private static Effect Effect(string type, string parameter, double value)
        => new()
        {
            Type = type,
            Params = new() { [parameter] = new EffectParam { Value = value } },
        };
}
