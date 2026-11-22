using Temno.Core.Models;
using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-174. A sequence carrier is neither "not visual" nor "not audio": it has a transform
/// and mixer-gained volume. Tabs must not depend on the previous selection.
/// </summary>
public sealed class InspectorClipTabsTests
{
    [Fact]
    public void Sequence_MatchesVideoTabSet()
    {
        var video = InspectorClipTabs.ForClip(ClipType.video, hasTextContent: false, includeMulticam: false);
        var sequence = InspectorClipTabs.ForClip(ClipType.sequence, hasTextContent: false, includeMulticam: false);
        Assert.Equal(video, sequence);
        Assert.Equal(
            new[] { InspectorClipTab.Video, InspectorClipTab.Adjust, InspectorClipTab.Audio },
            sequence);
    }

    [Theory]
    [InlineData(ClipType.video, new[] { InspectorClipTab.Video, InspectorClipTab.Adjust, InspectorClipTab.Audio })]
    [InlineData(ClipType.image, new[] { InspectorClipTab.Video, InspectorClipTab.Adjust })]
    [InlineData(ClipType.audio, new[] { InspectorClipTab.Audio })]
    [InlineData(ClipType.text, new[] { InspectorClipTab.Content, InspectorClipTab.Animation, InspectorClipTab.Video })]
    public void ExistingTypes_Unchanged(ClipType type, InspectorClipTab[] expected)
        => Assert.Equal(expected, InspectorClipTabs.ForClip(type, hasTextContent: false, includeMulticam: false));

    [Fact]
    public void Lottie_Unchanged_NoTabs()
        => Assert.Empty(InspectorClipTabs.ForClip(ClipType.lottie, hasTextContent: false, includeMulticam: false));

    [Fact]
    public void Sequence_WithMulticam_AppendsMulticam()
        => Assert.Equal(
            new[] { InspectorClipTab.Video, InspectorClipTab.Adjust, InspectorClipTab.Audio, InspectorClipTab.Multicam },
            InspectorClipTabs.ForClip(ClipType.sequence, hasTextContent: false, includeMulticam: true));

    [Fact]
    public void Preferred_TextThenSequence_DropsStyleForVideo()
    {
        var tabs = InspectorClipTabs.ForClip(ClipType.sequence, false, false);
        var preferred = InspectorClipTabs.ResolvePreferred(tabs, InspectorClipTab.Content, ClipType.sequence, false);
        Assert.Equal(InspectorClipTab.Video, preferred);
        Assert.DoesNotContain(InspectorClipTab.Content, tabs);
    }

    [Fact]
    public void Preferred_VideoAudioThenSequence_KeepsAudio()
    {
        var tabs = InspectorClipTabs.ForClip(ClipType.sequence, false, false);
        var preferred = InspectorClipTabs.ResolvePreferred(tabs, InspectorClipTab.Audio, ClipType.sequence, false);
        Assert.Equal(InspectorClipTab.Audio, preferred);
    }

    [Fact]
    public void Preferred_AdjustThenSequence_KeepsAdjust()
    {
        var tabs = InspectorClipTabs.ForClip(ClipType.sequence, false, false);
        Assert.Equal(
            InspectorClipTab.Adjust,
            InspectorClipTabs.ResolvePreferred(tabs, InspectorClipTab.Adjust, ClipType.sequence, false));
    }

    [Fact]
    public void Preferred_EmptyAvailable_IsNull_NotStale()
    {
        Assert.Null(InspectorClipTabs.ResolvePreferred(
            Array.Empty<InspectorClipTab>(), InspectorClipTab.Content, ClipType.lottie, false));
    }

    [Fact]
    public void Preferred_VideoThenText_LandsOnStyle()
    {
        var tabs = InspectorClipTabs.ForClip(ClipType.text, hasTextContent: true, includeMulticam: false);
        Assert.Equal(
            InspectorClipTab.Content,
            InspectorClipTabs.ResolvePreferred(tabs, InspectorClipTab.Video, ClipType.text, true));
    }

    [Fact]
    public void Preferred_TextOnVideoTab_StaysOnVideoAcrossEditsAndTextSelection()
    {
        var tabs = InspectorClipTabs.ForClip(ClipType.text, hasTextContent: true, includeMulticam: false);
        Assert.Equal(
            InspectorClipTab.Video,
            InspectorClipTabs.ResolvePreferred(tabs, InspectorClipTab.Video, ClipType.text, true, wasTextClip: true));
        Assert.Equal(
            InspectorClipTab.Animation,
            InspectorClipTabs.ResolvePreferred(tabs, InspectorClipTab.Animation, ClipType.text, true, wasTextClip: true));
    }

    [Fact]
    public void PictureAndAudio_SequenceIsBoth()
    {
        Assert.True(InspectorClipTabs.IsPictureClip(ClipType.sequence));
        Assert.True(InspectorClipTabs.HasClipAudioLevels(ClipType.sequence));
        Assert.False(InspectorClipTabs.HasClipAudioLevels(ClipType.image));
        Assert.False(InspectorClipTabs.IsPictureClip(ClipType.audio));
        Assert.False(InspectorClipTabs.IsPictureClip(ClipType.text));
        Assert.True(InspectorClipTabs.HasVideoTab(ClipType.text));
        Assert.True(InspectorClipTabs.HasVideoTab(ClipType.video));
        Assert.True(InspectorClipTabs.HasVideoTab(ClipType.image));
        Assert.True(InspectorClipTabs.HasVideoTab(ClipType.sequence));
        Assert.False(InspectorClipTabs.HasVideoTab(ClipType.audio));
        Assert.False(InspectorClipTabs.HasVideoTab(ClipType.lottie));
    }
}
