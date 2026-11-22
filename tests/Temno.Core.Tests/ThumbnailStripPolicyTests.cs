using System.Linq;
using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-159. The owner: "the track in the timeline shows a static image instead of various frames
/// across the track." The clip was the 8.09 h VOD trimmed in at frame 26958 of a 60 fps timeline, and
/// the one frame shown came from t = 1 s — outside the clip entirely.
/// </summary>
public class ThumbnailStripPolicyTests
{
    // The owner's clip: 60 fps, trimmed in at 26958, 7698 frames long, speed 1.
    private const int Fps = 60;
    private const int OwnerTrimStart = 26958;
    private const int OwnerDuration = 7698;

    [Fact]
    public void TheOwnersClip_SamplesItsOwnRange_NeverOneSecond()
    {
        var start = ThumbnailStripPolicy.VisibleStartSeconds(OwnerTrimStart, Fps);
        var length = ThumbnailStripPolicy.VisibleDurationSeconds(OwnerDuration, 1.0, Fps);
        var samples = ThumbnailStripPolicy.SampleSeconds(start, length, count: 12);

        Assert.Equal(449.3, start, 1);
        Assert.Equal(128.3, length, 1);
        Assert.All(samples, s => Assert.InRange(s, start - 0.5, start + length + 0.5));
        Assert.DoesNotContain(samples, s => s < 2.0);   // the old behaviour sampled t = 1 s
    }

    [Fact]
    public void TwoClipsOffOneSource_SampleDifferentFrames()
    {
        var a = ThumbnailStripPolicy.SampleSeconds(
            ThumbnailStripPolicy.VisibleStartSeconds(0, Fps),
            ThumbnailStripPolicy.VisibleDurationSeconds(600, 1.0, Fps), 8);
        var b = ThumbnailStripPolicy.SampleSeconds(
            ThumbnailStripPolicy.VisibleStartSeconds(OwnerTrimStart, Fps),
            ThumbnailStripPolicy.VisibleDurationSeconds(600, 1.0, Fps), 8);

        Assert.Empty(a.Intersect(b));
    }

    [Fact]
    public void FirstCellIsTheClipsInPoint()
    {
        var start = ThumbnailStripPolicy.VisibleStartSeconds(OwnerTrimStart, Fps);
        var samples = ThumbnailStripPolicy.SampleSeconds(start, 128.3, count: 10);

        Assert.Equal(start, samples[0], 1);
    }

    [Theory]
    // The load-bearing property: the count follows pixels, not the source. An 8-hour clip and a
    // 10-second clip at the same on-screen width cost the same number of extractions.
    [InlineData(0, 1)]
    [InlineData(8, 1)]        // narrower than MinStripWidthPixels
    [InlineData(100, 2)]      // 100 / 89 -> 2
    [InlineData(890, 11)]   // 890 / 88.9 = 10.01 -> 11
    [InlineData(100000, ThumbnailStripPolicy.MaxSamplesPerClip)]
    public void SampleCount_FollowsOnScreenWidth_AndIsCapped(double widthPixels, int expected)
    {
        var thumbWidth = ThumbnailStripPolicy.ThumbnailWidth(50, 1920, 1080);   // 88.9 px
        Assert.Equal(expected, ThumbnailStripPolicy.SampleCount(widthPixels, thumbWidth));
    }

    [Fact]
    public void TheOwnersEightHourSource_CostsAtMostFortyExtractions()
    {
        // The Mac cadence (one every 2 s) would be 14,570 on this source.
        var thumbWidth = ThumbnailStripPolicy.ThumbnailWidth(50, 1920, 1080);
        var count = ThumbnailStripPolicy.SampleCount(1800, thumbWidth);   // full-width timeline

        Assert.True(count <= ThumbnailStripPolicy.MaxSamplesPerClip);
        Assert.Equal(21, count);
        Assert.Equal(count, ThumbnailStripPolicy.SampleSeconds(449.3, 128.3, count).Count);
    }

    [Fact]
    public void ThumbnailWidth_UsesTheSourceAspect_AndFallsBackTo16By9()
    {
        Assert.Equal(50 * 16.0 / 9.0, ThumbnailStripPolicy.ThumbnailWidth(50, 1920, 1080), 3);
        Assert.Equal(50 * 16.0 / 9.0, ThumbnailStripPolicy.ThumbnailWidth(50, null, null), 3);
        Assert.Equal(50 * 16.0 / 9.0, ThumbnailStripPolicy.ThumbnailWidth(50, 0, 0), 3);
        Assert.Equal(50.0, ThumbnailStripPolicy.ThumbnailWidth(50, 1080, 1080), 3);
    }

    [Fact]
    public void CoarseSpacingSnapsToTheCacheGrid_SoAScrollReusesWhatIsOnDisk()
    {
        // 128.3 s over 10 cells = 12.83 s apart, well above the 1 s quantisation threshold.
        var samples = ThumbnailStripPolicy.SampleSeconds(449.3, 128.3, count: 10);

        // Cell 0 keeps the exact in-point; every later cell lands on the grid.
        Assert.Equal(449.3, samples[0], 6);
        Assert.All(samples.Skip(1), s => Assert.Equal(0, s % ThumbnailStripPolicy.QuantumSeconds, 6));
    }

    [Fact]
    public void FineSpacingIsNotSnapped_OrZoomingInWouldShowOneFrameRepeated()
    {
        // 2 s over 20 cells = 0.1 s apart. Rounding to 0.5 s would collapse them onto 4 distinct
        // frames and the strip would visibly stutter.
        var samples = ThumbnailStripPolicy.SampleSeconds(10.0, 2.0, count: 20);

        Assert.Equal(20, samples.Distinct().Count());
    }

    [Fact]
    public void SpeedIsHonoured_SoAFastClipConsumesMoreSource()
    {
        Assert.Equal(
            2 * ThumbnailStripPolicy.VisibleDurationSeconds(600, 1.0, Fps),
            ThumbnailStripPolicy.VisibleDurationSeconds(600, 2.0, Fps),
            6);
        // Zero speed is data, not an assertion failure: treat it as 1x rather than divide by it.
        Assert.Equal(
            ThumbnailStripPolicy.VisibleDurationSeconds(600, 1.0, Fps),
            ThumbnailStripPolicy.VisibleDurationSeconds(600, 0.0, Fps),
            6);
        // A reversed clip consumes the same amount of source.
        Assert.Equal(
            ThumbnailStripPolicy.VisibleDurationSeconds(600, 1.0, Fps),
            ThumbnailStripPolicy.VisibleDurationSeconds(600, -1.0, Fps),
            6);
    }

    [Fact]
    public void DegenerateInputsReturnOneSampleRatherThanThrowing()
    {
        Assert.Single(ThumbnailStripPolicy.SampleSeconds(0, 0, count: 10));
        Assert.Single(ThumbnailStripPolicy.SampleSeconds(5, 10, count: 0));
        Assert.Equal(0, ThumbnailStripPolicy.VisibleDurationSeconds(600, 1.0, fps: 0));
        Assert.Equal(0, ThumbnailStripPolicy.VisibleStartSeconds(100, fps: 0));
        Assert.All(ThumbnailStripPolicy.SampleSeconds(-5, 10, count: 4), s => Assert.True(s >= 0));
    }
}
