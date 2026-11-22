using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

public class ClipMathTests
{
    [Fact]
    public void SourceFramesConsumed_RoundsSpeed()
    {
        var c = new Clip { DurationFrames = 10, Speed = 1.5 };
        Assert.Equal(15, c.SourceFramesConsumed);
    }

    [Fact]
    public void FadeMultiplier_LinearIn()
    {
        var c = new Clip
        {
            StartFrame = 0,
            DurationFrames = 10,
            FadeInFrames = 10,
            FadeInInterpolation = Interpolation.linear,
        };
        Assert.Equal(0.0, c.FadeMultiplier(0), 5);
        Assert.Equal(0.5, c.FadeMultiplier(5), 5);
        Assert.Equal(1.0, c.FadeMultiplier(10), 5);
    }

    [Fact]
    public void OpacityAt_AppliesFadeOnVisualClips()
    {
        var c = new Clip
        {
            StartFrame = 0,
            DurationFrames = 10,
            Opacity = 1.0,
            FadeInFrames = 10,
            MediaType = ClipType.video,
        };
        Assert.Equal(0.5, c.OpacityAt(5), 5);
    }

    [Fact]
    public void KeyframeSample_Linear()
    {
        var track = new KeyframeTrack<double>();
        track.Upsert(new Keyframe<double> { Frame = 0, Value = 0, InterpolationOut = Interpolation.linear });
        track.Upsert(new Keyframe<double> { Frame = 10, Value = 10, InterpolationOut = Interpolation.linear });
        Assert.Equal(5, KeyframeMath.Sample(track, 5, -1), 5);
    }

    [Fact]
    public void KeyframeSample_Hold()
    {
        var track = new KeyframeTrack<double>();
        track.Upsert(new Keyframe<double> { Frame = 0, Value = 2, InterpolationOut = Interpolation.hold });
        track.Upsert(new Keyframe<double> { Frame = 10, Value = 8, InterpolationOut = Interpolation.linear });
        Assert.Equal(2, KeyframeMath.Sample(track, 5, -1), 5);
    }
}
