using Temno.Core.Models;
using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

public sealed class NativePreviewEligibilityTests
{
    [Theory]
    [InlineData(5, 52, true)]
    [InlineData(359, 2, true)]
    [InlineData(360, 30, false)]
    [InlineData(-30, 30, false)]
    [InlineData(-30, 31, true)]
    [InlineData(10, 0, false)]
    public void LaterCaptionUsesCompositionAcrossItsPictureClip(int start, int duration, bool expected)
    {
        var picture = new Clip { Id = "picture", MediaType = ClipType.video, DurationFrames = 360 };
        var caption = new Clip { Id = "caption", MediaType = ClipType.text, StartFrame = start,
            DurationFrames = duration, TextContent = "Hello" };
        var timeline = new Timeline { Tracks = [new Track { Clips = [picture, caption] }] };
        Assert.Equal(expected, NativePreviewEligibility.HasOverlappingVisuals(timeline, picture));
    }

    [Fact]
    public void AudioAndHiddenOverlaysDoNotDisableNativePicture()
    {
        var picture = new Clip { Id = "picture", MediaType = ClipType.video, DurationFrames = 360 };
        var timeline = new Timeline { Tracks = [
            new Track { Clips = [picture, new Clip { Id = "audio", MediaType = ClipType.audio, DurationFrames = 360 }] },
            new Track { Hidden = true, Clips = [new Clip { Id = "hidden", MediaType = ClipType.image, DurationFrames = 360 }] }
        ] };
        Assert.False(NativePreviewEligibility.HasOverlappingVisuals(timeline, picture));
        timeline.Tracks[1].Hidden = false;
        Assert.True(NativePreviewEligibility.HasOverlappingVisuals(timeline, picture));
    }
}
