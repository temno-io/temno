using Temno.Core.Captions;
using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// Gap-closing and overlap-ownership vectors for <see cref="CaptionSpecBuilder"/>.
/// Policy source: docs/audits/captions-speech.md (Mac CaptionSpecBuilder / CaptionGapSettings).
/// The macOS checkout on this workstation predates that type, so these vectors encode the audit's
/// written contract rather than transcribed Swift assertions — see WP-115.
/// </summary>
public sealed class CaptionSpecBuilderTests
{
    private const int Fps = 30;

    private static Clip Caption(int start, int duration) => new()
    {
        MediaRef = "",
        MediaType = ClipType.text,
        SourceClipType = ClipType.text,
        StartFrame = start,
        DurationFrames = duration,
        TextContent = $"c{start}",
    };

    [Fact]
    public void DefaultGap_IsHalfASecond_FifteenFramesAtThirtyFps()
    {
        Assert.Equal(0.5, CaptionSpecBuilder.DefaultMaximumGapSeconds);
        Assert.Equal(15, CaptionSpecBuilder.GapFrames(CaptionSpecBuilder.DefaultMaximumGapSeconds, Fps));
    }

    [Fact]
    public void GapWithinMaximum_ExtendsTheEarlierCaption()
    {
        // 10-frame hole is inside the 15-frame default budget.
        var clips = new List<Clip> { Caption(0, 10), Caption(20, 10) };
        CaptionSpecBuilder.Apply(clips, Fps);

        Assert.Equal(20, clips[0].DurationFrames);
        Assert.Equal(20, clips[0].EndFrame);
        Assert.Equal(20, clips[1].StartFrame);
        Assert.Equal(10, clips[1].DurationFrames);
    }

    [Fact]
    public void GapBeyondMaximum_IsLeftAsTranscribed()
    {
        // 20-frame hole exceeds the 15-frame default budget.
        var clips = new List<Clip> { Caption(0, 10), Caption(30, 10) };
        CaptionSpecBuilder.Apply(clips, Fps);

        Assert.Equal(10, clips[0].DurationFrames);
        Assert.Equal(30, clips[1].StartFrame);
    }

    [Fact]
    public void ZeroGap_ClosesNothing()
    {
        var clips = new List<Clip> { Caption(0, 10), Caption(20, 10) };
        CaptionSpecBuilder.Apply(clips, Fps, maximumGapSeconds: 0);

        Assert.Equal(10, clips[0].DurationFrames);
        Assert.Equal(20, clips[1].StartFrame);
    }

    [Fact]
    public void LastCaption_HoldsToTheEnd_WhenTheTailIsWithinTheBudget()
    {
        var held = new List<Clip> { Caption(0, 10) };
        CaptionSpecBuilder.Apply(held, Fps, timelineEndFrame: 20);
        Assert.Equal(20, held[0].DurationFrames);

        var free = new List<Clip> { Caption(0, 10) };
        CaptionSpecBuilder.Apply(free, Fps, timelineEndFrame: 40);
        Assert.Equal(10, free[0].DurationFrames);
    }

    [Fact]
    public void OneFrameOverlap_GoesToTheShorterOriginal()
    {
        // Previous caption is the shorter original, so it keeps the contested frame.
        var prevShorter = new List<Clip> { Caption(0, 5), Caption(4, 20) };
        CaptionSpecBuilder.Apply(prevShorter, Fps, maximumGapSeconds: 0);
        Assert.Equal(5, prevShorter[0].DurationFrames);
        Assert.Equal(5, prevShorter[1].StartFrame);
        Assert.Equal(19, prevShorter[1].DurationFrames);

        // Next caption is the shorter original, so the previous one yields the frame instead.
        var nextShorter = new List<Clip> { Caption(0, 20), Caption(19, 5) };
        CaptionSpecBuilder.Apply(nextShorter, Fps, maximumGapSeconds: 0);
        Assert.Equal(19, nextShorter[0].DurationFrames);
        Assert.Equal(19, nextShorter[1].StartFrame);
        Assert.Equal(5, nextShorter[1].DurationFrames);
    }

    [Fact]
    public void MultiFrameOverlap_TrimsThePreviousCaption()
    {
        var clips = new List<Clip> { Caption(0, 20), Caption(12, 20) };
        CaptionSpecBuilder.Apply(clips, Fps, maximumGapSeconds: 0);

        Assert.Equal(12, clips[0].DurationFrames);
        Assert.Equal(12, clips[1].StartFrame);
        Assert.Equal(20, clips[1].DurationFrames);
    }

    [Fact]
    public void SameFrameStart_LeavesOnlyTheLaterCaption()
    {
        // Mac collapses the earlier spec to one frame; overwrite placement then removes it.
        var clips = new List<Clip> { Caption(10, 8), Caption(10, 20) };
        CaptionSpecBuilder.Apply(clips, Fps, maximumGapSeconds: 0);

        Assert.Single(clips);
        Assert.Equal(10, clips[0].StartFrame);
        Assert.Equal(20, clips[0].DurationFrames);
    }

    [Fact]
    public void ThreeCaptionsOnOneFrameLeaveTheLastAuthoredOne()
    {
        // Ordering must be stable, or which caption survives would depend on the sort's internals.
        var clips = new List<Clip> { Caption(10, 8), Caption(10, 20), Caption(10, 5) };
        clips[0].TextContent = "first";
        clips[1].TextContent = "second";
        clips[2].TextContent = "third";

        CaptionSpecBuilder.Apply(clips, Fps, maximumGapSeconds: 0);

        Assert.Single(clips);
        Assert.Equal("third", clips[0].TextContent);
        Assert.Equal(5, clips[0].DurationFrames);
    }

    [Fact]
    public void WinningAOneFrameDisputeNeverCostsTheCaptionItsPlace()
    {
        // A beats B for the contested frame, which would shift B onto C's start frame. Shifting there
        // would collapse B and the overwrite pass would then delete it outright, losing a whole
        // phrase, so the shift must be declined and A trimmed instead.
        var clips = new List<Clip> { Caption(0, 5), Caption(4, 20), Caption(5, 3) };
        clips[0].TextContent = "a";
        clips[1].TextContent = "b";
        clips[2].TextContent = "c";

        CaptionSpecBuilder.Apply(clips, Fps, maximumGapSeconds: 0);

        Assert.Equal(3, clips.Count);
        Assert.Equal(["a", "b", "c"], clips.Select(c => c.TextContent ?? "").ToArray());
        Assert.All(clips, c => Assert.True(c.DurationFrames >= 1));
        for (var i = 0; i + 1 < clips.Count; i++)
            Assert.True(clips[i].EndFrame <= clips[i + 1].StartFrame);
    }

    [Fact]
    public void NoCaptionIsSilentlyLostToOverlapResolution()
    {
        // Densely packed captions with assorted one- and multi-frame overlaps: every phrase must
        // survive unless a later one genuinely starts on or before its start frame.
        var clips = new List<Clip>
        {
            Caption(0, 5), Caption(4, 20), Caption(5, 3), Caption(7, 9), Caption(15, 2), Caption(16, 30),
        };
        var before = clips.Count;

        CaptionSpecBuilder.Apply(clips, Fps, maximumGapSeconds: 0);

        Assert.Equal(before, clips.Count);
        for (var i = 0; i + 1 < clips.Count; i++)
            Assert.True(clips[i].EndFrame <= clips[i + 1].StartFrame, "captions must not overlap");
    }

    [Fact]
    public void ResultIsAlwaysOrderedAndNonOverlapping()
    {
        var clips = new List<Clip>
        {
            Caption(40, 30), Caption(0, 25), Caption(20, 12), Caption(60, 5),
        };
        CaptionSpecBuilder.Apply(clips, Fps);

        for (var i = 0; i + 1 < clips.Count; i++)
        {
            Assert.True(clips[i].StartFrame <= clips[i + 1].StartFrame, "captions must stay ordered");
            Assert.True(clips[i].EndFrame <= clips[i + 1].StartFrame, "captions must not overlap");
            Assert.True(clips[i].DurationFrames >= 1, "captions must stay at least one frame long");
        }
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.0, 0.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(3.5, 2.0)]
    [InlineData(-1.0, 0.0)]
    public void GapSecondsClampToTheMacRange(double input, double expected)
        => Assert.Equal(expected, CaptionSpecBuilder.ClampGapSeconds(input));

    [Fact]
    public void NonFiniteGapFallsBackToTheDefault()
    {
        Assert.Equal(CaptionSpecBuilder.DefaultMaximumGapSeconds, CaptionSpecBuilder.ClampGapSeconds(double.NaN));
        Assert.False(CaptionSpecBuilder.IsValidGapSeconds(double.NaN));
        Assert.False(CaptionSpecBuilder.IsValidGapSeconds(2.5));
        Assert.True(CaptionSpecBuilder.IsValidGapSeconds(0));
        Assert.True(CaptionSpecBuilder.IsValidGapSeconds(2));
    }

    [Fact]
    public void SubtitleImportUsesGapZero()
        => Assert.Equal(0.0, CaptionSpecBuilder.SubtitleImportGapSeconds);
}
