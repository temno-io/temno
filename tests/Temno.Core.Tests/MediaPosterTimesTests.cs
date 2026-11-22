using System.Linq;
using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// The media library and the generation sources list both find a video's frame by these times, so a
/// change here moves the thumbnail key for both at once.
/// </summary>
public class MediaPosterTimesTests
{
    [Theory]
    [InlineData(10.0, 5.0)]
    [InlineData(6.0, 3.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(-1.0, 0.0)]
    public void Primary_is_the_midpoint_or_zero_for_stills(double duration, double expected) =>
        Assert.Equal(expected, MediaPosterTimes.Primary(duration));

    [Fact]
    public void Fallbacks_try_three_quarters_then_one_quarter()
    {
        Assert.Equal([7.5, 2.5], MediaPosterTimes.Fallbacks(10, MediaPosterTimes.Primary(10)).ToArray());
        Assert.Equal([0.75, 0.25], MediaPosterTimes.Fallbacks(1, MediaPosterTimes.Primary(1)).ToArray());
    }

    [Fact]
    public void Fallbacks_skip_frames_too_close_to_the_one_already_tried()
    {
        // A 60 ms clip: every candidate clamps within 50 ms of the midpoint, so there is nothing else to try.
        Assert.Empty(MediaPosterTimes.Fallbacks(0.06, MediaPosterTimes.Primary(0.06)));
        // Candidates never pass the last frame.
        Assert.All(MediaPosterTimes.Fallbacks(0.2, 0), seconds => Assert.True(seconds <= 0.2 - 0.04 + 1e-12));
    }
}
