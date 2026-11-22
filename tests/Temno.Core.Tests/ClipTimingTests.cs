using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

public class ClipTimingTests
{
    [Fact]
    public void TimelineFrameFromSourceSeconds_MapsInsideVisibleWindow()
    {
        var clip = new Clip
        {
            StartFrame = 100,
            DurationFrames = 50,
            TrimStartFrame = 30,
            Speed = 1,
        };
        // source frame 40 → offset from trim = 10 → timeline 110
        Assert.Equal(110, clip.TimelineFrameFromSourceSeconds(sourceSeconds: 40.0 / 30.0, fps: 30));
    }

    [Fact]
    public void TimelineFrameFromSourceSeconds_ReturnsNullOutsideClip()
    {
        var clip = new Clip
        {
            StartFrame = 0,
            DurationFrames = 30,
            TrimStartFrame = 0,
            Speed = 1,
        };
        Assert.Null(clip.TimelineFrameFromSourceSeconds(sourceSeconds: -1, fps: 30));
        Assert.Null(clip.TimelineFrameFromSourceSeconds(sourceSeconds: 2.0, fps: 30)); // past end
    }

    [Fact]
    public void TimelineFrameFromSourceSeconds_RespectsSpeed()
    {
        var clip = new Clip
        {
            StartFrame = 0,
            DurationFrames = 60,
            TrimStartFrame = 0,
            Speed = 2,
        };
        // source 60 frames at 30fps = 2s → offset 60 / speed 2 = 30 timeline frames
        Assert.Equal(30, clip.TimelineFrameFromSourceSeconds(sourceSeconds: 2.0, fps: 30));
    }
}
