using Temno.Core.Models;
using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-188. The Mac numbers video tracks upward from the bottom of the video stack and audio tracks
/// downward from the top of the audio stack. Rows, ids and file order never move.
/// </summary>
public sealed class TrackLabelsTests
{
    private static string[] Labels(params ClipType[] types)
        => Enumerable.Range(0, types.Length).Select(i => TrackLabels.LabelFor(types, i)).ToArray();

    [Fact]
    public void Video_counts_up_from_the_bottom_of_the_stack_and_audio_down_from_its_top()
        => Assert.Equal(
            ["V3", "V2", "V1", "A1", "A2"],
            Labels(ClipType.video, ClipType.video, ClipType.video, ClipType.audio, ClipType.audio));

    [Fact]
    public void One_of_each_is_V1_directly_over_A1()
        => Assert.Equal(["V1", "A1"], Labels(ClipType.video, ClipType.audio));

    [Fact]
    public void Single_type_timelines()
    {
        Assert.Equal(["V2", "V1"], Labels(ClipType.video, ClipType.video));
        Assert.Equal(["A1", "A2"], Labels(ClipType.audio, ClipType.audio));
    }

    [Fact]
    public void A_video_track_below_the_audio_zone_is_numbered_alone_like_the_Mac()
        => Assert.Equal(["V1", "A1", "V1"], Labels(ClipType.video, ClipType.audio, ClipType.video));

    [Fact]
    public void Out_of_range_is_empty()
    {
        Assert.Equal("", TrackLabels.LabelFor([], 0));
        Assert.Equal("", TrackLabels.LabelFor([ClipType.video], 3));
        Assert.Equal("", TrackLabels.LabelFor([ClipType.video], -1));
    }
}
