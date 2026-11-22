using Temno.Core.Models;
using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-183. The Inspector treated a video clip plus its linked audio as a two-clip batch. These pin
/// the classification the Inspector now relies on.
/// </summary>
public sealed class SelectionSemanticsTests
{
    [Fact]
    public void A_video_clip_with_its_linked_audio_is_one_logical_clip()
    {
        var c = SelectionSemantics.Classify(
            [("a", ClipType.audio, "L1"), ("v", ClipType.video, "L1")], focusedId: "a");
        Assert.Equal(SelectionSemantics.Kind.LinkedPair, c.Kind);
        Assert.Equal("v", c.PrimaryId);
        Assert.Equal("a", c.AudioPartnerId);
    }

    [Fact]
    public void Two_clips_that_are_not_linked_are_a_batch_led_by_the_focused_clip()
    {
        var c = SelectionSemantics.Classify(
            [("v1", ClipType.video, null), ("a1", ClipType.audio, null)], focusedId: "a1");
        Assert.Equal(SelectionSemantics.Kind.Batch, c.Kind);
        Assert.Equal("a1", c.PrimaryId);
        Assert.Null(c.AudioPartnerId);
    }

    [Fact]
    public void Two_linked_video_clips_are_still_a_batch()
    {
        var c = SelectionSemantics.Classify(
            [("v1", ClipType.video, "L1"), ("v2", ClipType.video, "L1")], focusedId: null);
        Assert.Equal(SelectionSemantics.Kind.Batch, c.Kind);
    }

    [Fact]
    public void Three_clips_are_a_batch_and_one_clip_is_single()
    {
        Assert.Equal(SelectionSemantics.Kind.Batch, SelectionSemantics.Classify(
            [("v", ClipType.video, "L1"), ("a", ClipType.audio, "L1"), ("t", ClipType.text, null)], "v").Kind);
        Assert.Equal(SelectionSemantics.Kind.Single, SelectionSemantics.Classify([("v", ClipType.video, "L1")], "v").Kind);
        Assert.Equal(SelectionSemantics.Kind.None, SelectionSemantics.Classify([], null).Kind);
    }
}
