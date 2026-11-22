using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Effects;
using Temno.Media.Text;
using Xunit;

namespace Temno.Core.Tests;

public sealed class CpuSpatialEffectsTests
{
    private readonly CpuEffectsRenderer _renderer = new();

    private static VideoFrame Solid(byte r, byte g, byte b, byte a = 255, int w = 16, int h = 16)
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

    private static Effect Fx(string type, params (string Key, double Value)[] p)
    {
        var e = new Effect { Type = type };
        foreach (var (k, v) in p)
            e.Params[k] = new EffectParam { Value = v };
        return e;
    }

    [Fact]
    public void GaussianBlur_SoftensCheckerEdge()
    {
        var data = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
        {
            var on = x < 4;
            var i = (y * 8 + x) * 4;
            data[i] = data[i + 1] = data[i + 2] = (byte)(on ? 255 : 0);
            data[i + 3] = 255;
        }
        using var source = new VideoFrame(8, 8, data, "rgba");
        using var outFrame = _renderer.Apply(source, [Fx("blur.gaussian", ("radius", 2))], 0);
        // Boundary pixel should no longer be pure 0 or 255
        var mid = outFrame.Data[(0 * 8 + 3) * 4];
        Assert.InRange(mid, 1, 254);
    }

    [Fact]
    public void Vignette_DarkensCornersRelativeToCenter()
    {
        using var source = Solid(200, 200, 200, w: 32, h: 32);
        using var outFrame = _renderer.Apply(source, [
            Fx("stylize.vignette", ("amount", 1), ("midpoint", 0.2), ("roundness", 0), ("feather", 0.8)),
        ], 0);
        var center = outFrame.Data[((16 * 32) + 16) * 4];
        var corner = outFrame.Data[0];
        Assert.True(corner < center);
    }

    [Fact]
    public void ChromaKey_KeysGreen()
    {
        using var source = Solid(0, 255, 0, w: 4, h: 4);
        using var outFrame = _renderer.Apply(source, [
            Fx("key.chroma", ("keyHue", 0.33), ("tolerance", 0.4), ("softness", 0.2), ("spill", 0.5)),
        ], 0);
        Assert.True(outFrame.Data[3] < 200); // alpha reduced
    }

    [Fact]
    public void Sharpen_AndClarity_DoNotCrashAndPreserveAlpha()
    {
        using var source = Solid(40, 80, 120, 200, 12, 12);
        using var outFrame = _renderer.Apply(source, [
            Fx("blur.sharpen", ("amount", 0.8)),
            Fx("detail.clarity", ("clarity", 0.5), ("dehaze", 0.2)),
            Fx("stylize.glow", ("intensity", 0.3), ("radius", 2), ("threshold", 0.1), ("warmth", 0.2)),
            Fx("blur.motion", ("radius", 2), ("angle", 45)),
            Fx("blur.noiseReduction", ("amount", 0.4)),
            Fx("stylize.grain", ("amount", 0.2), ("size", 1.5)),
        ], 3);
        Assert.Equal(200, outFrame.Data[3]);
    }

    [Fact]
    public void TextAnimator_FadeInAndTypewriter()
    {
        var fade = TextAnimator.Evaluate(new TextAnimation { Preset = "fadeIn" }, "Hi", rel: 0, clipDurationFrames: 30);
        Assert.Equal(0, fade.Opacity, 3);
        var fadeEnd = TextAnimator.Evaluate(new TextAnimation { Preset = "fadeIn" }, "Hi", rel: 30, clipDurationFrames: 30);
        Assert.Equal(1, fadeEnd.Opacity, 3);

        var tw = TextAnimator.Evaluate(
            new TextAnimation { Preset = "typewriter", PerWordFrames = 2 },
            "ABCD", rel: 4, clipDurationFrames: 40);
        Assert.Equal("AB", tw.VisibleText);
    }
}
