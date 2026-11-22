using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;
using static Temno.Core.Tests.TransitionBlocksTests;

namespace Temno.Core.Tests;

public class FadeCommandsTests
{
    [Fact]
    public void ApplyEdge_ClampsToTheRoomBesideTheOtherEdgesBlock()
    {
        var timeline = NewTimeline();
        var a = Add(timeline, 0, "a", 0, 60, fadeOut: 10);
        var b = Add(timeline, 0, "b", 60, 60);
        timeline.Transitions.Add(new Transition { ClipIdA = a.Id, ClipIdB = b.Id, DurationFrames = 20 });
        a.FadeOutFrames = 0;
        var index = new TransitionIndex(timeline);

        FadeCommands.ApplyEdge(index, a, FadeEdge.In, 99);
        Assert.Equal(50, a.FadeInFrames);
        FadeCommands.ApplyEdge(index, b, FadeEdge.Out, 99);
        Assert.Equal(50, b.FadeOutFrames);
        FadeCommands.ApplyEdge(index, b, FadeEdge.Out, -4);
        Assert.Equal(0, b.FadeOutFrames);
        Assert.Equal(0, b.FadeInFrames);
    }

    [Theory]
    [InlineData(Interpolation.linear, FadeEdge.In)]
    [InlineData(Interpolation.smooth, FadeEdge.In)]
    [InlineData(Interpolation.linear, FadeEdge.Out)]
    [InlineData(Interpolation.smooth, FadeEdge.Out)]
    [InlineData(Interpolation.hold, FadeEdge.In)]
    public void Ramp_MatchesFadeMultiplier(Interpolation interpolation, FadeEdge edge)
    {
        var clip = new Clip
        {
            StartFrame = 100,
            DurationFrames = 48,
            FadeInFrames = edge == FadeEdge.In ? 12 : 0,
            FadeOutFrames = edge == FadeEdge.Out ? 12 : 0,
            FadeInInterpolation = interpolation,
            FadeOutInterpolation = interpolation,
        };

        var samples = FadeRamp.Samples(clip, edge);
        Assert.NotEmpty(samples);
        foreach (var sample in samples)
        {
            var expected = clip.FadeMultiplier(sample.TimelineFrame);
            Assert.True(Math.Abs(sample.Multiplier - expected) <= 1e-9,
                $"{interpolation} {edge} t={sample.T} frame={sample.TimelineFrame}");
        }

        Assert.Equal(0, samples[0].T);
        Assert.Equal(1, samples[^1].T);
        Assert.Equal(0, samples[0].Multiplier, 9);
        Assert.Equal(1, samples[^1].Multiplier, 9);
        if (interpolation == Interpolation.smooth)
        {
            var quarter = samples.First(s => Math.Abs(s.T - 0.25) < 0.02);
            Assert.NotEqual(0.25, quarter.Multiplier);
        }
    }
}
