using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public sealed class LinkIsolationTests
{
    private static (ProjectSession Session, Clip Video, Clip Audio) CreateLinkedPairSession()
    {
        var session = ProjectSession.CreateNew();
        var video = new Clip
        {
            MediaRef = "VID-1",
            MediaType = ClipType.video,
            SourceClipType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 120,
        };
        Assert.True(EditCommands.AddLinkedMediaDrop(session, videoTrackIndex: 1, video, linkAudio: true, out var audioTrack).Ok);
        var audio = Assert.Single(session.ActiveTimeline.Tracks[audioTrack].Clips);
        Assert.NotNull(video.LinkGroupId);
        Assert.Equal(video.LinkGroupId, audio.LinkGroupId);
        return (session, video, audio);
    }

    [Fact]
    public void LinkGroup_DetectsIsolatedSelectionAndControlsExpansion()
    {
        var (session, video, audio) = CreateLinkedPairSession();
        var timeline = session.ActiveTimeline;

        var both = new HashSet<string>([video.Id, audio.Id], StringComparer.Ordinal);
        var videoOnly = new HashSet<string>([video.Id], StringComparer.Ordinal);
        var audioOnly = new HashSet<string>([audio.Id], StringComparer.Ordinal);
        var none = new HashSet<string>(StringComparer.Ordinal);

        Assert.False(LinkGroup.IsIsolatedInSelection(timeline, both, video.Id));
        Assert.False(LinkGroup.HasIsolatedMember(timeline, both));
        Assert.True(LinkGroup.ShouldExpandLinks(timeline, both, video.Id, altHeld: false));
        Assert.False(LinkGroup.ShouldExpandLinks(timeline, both, video.Id, altHeld: true));

        // Alt+clicked video only: even after releasing Alt, ShouldExpandLinks stays false for video.
        Assert.True(LinkGroup.IsIsolatedInSelection(timeline, videoOnly, video.Id));
        Assert.True(LinkGroup.HasIsolatedMember(timeline, videoOnly));
        Assert.False(LinkGroup.ShouldExpandLinks(timeline, videoOnly, video.Id, altHeld: false));
        Assert.False(LinkGroup.ShouldExpandSelection(timeline, videoOnly, altHeld: false));

        // Same for Alt+clicked audio only.
        Assert.True(LinkGroup.IsIsolatedInSelection(timeline, audioOnly, audio.Id));
        Assert.False(LinkGroup.ShouldExpandLinks(timeline, audioOnly, audio.Id, altHeld: false));

        // Empty selection expands normally unless Alt is held.
        Assert.True(LinkGroup.ShouldExpandLinks(timeline, none, video.Id, altHeld: false));
        Assert.False(LinkGroup.ShouldExpandLinks(timeline, none, video.Id, altHeld: true));
    }

    [Fact]
    public void IsolatedFadeEdits_DoNotModifyLinkedPartner()
    {
        var (session, video, audio) = CreateLinkedPairSession();
        var videoOnly = new HashSet<string>([video.Id], StringComparer.Ordinal);
        var expand = LinkGroup.ShouldExpandLinks(session.ActiveTimeline, videoOnly, video.Id, altHeld: false);
        Assert.False(expand);

        var corner = TransitionBlocks.Corner(session.ActiveTimeline, video.Id, FadeEdge.In);
        Assert.NotNull(corner);
        Assert.True(TransitionCommands.Add(session, corner!.Value, frames: 20, expandLinks: expand, out var created).Ok);
        Assert.NotNull(created);
        Assert.Equal(20, video.FadeInFrames);
        Assert.Equal(0, audio.FadeInFrames);

        // Resizing / changing curve / removing with isolated expandLinks keeps audio untouched even if both have matching fades.
        audio.FadeInFrames = 20;
        Assert.True(TransitionCommands.SetFrames(session, created!.Value, frames: 35, expandLinks: false).Ok);
        Assert.Equal(35, video.FadeInFrames);
        Assert.Equal(20, audio.FadeInFrames);

        Assert.True(TransitionCommands.SetCurve(session, created.Value, Interpolation.smooth, expandLinks: false).Ok);
        Assert.Equal(Interpolation.smooth, video.FadeInInterpolation);
        Assert.Equal(Interpolation.linear, audio.FadeInInterpolation);

        Assert.True(TransitionCommands.Remove(session, created.Value, expandLinks: false).Ok);
        Assert.Equal(0, video.FadeInFrames);
        Assert.Equal(20, audio.FadeInFrames);
    }

    [Fact]
    public void RemoveAndSplitWithExpandLinksFalse_PreserveLinkedPartner()
    {
        var (session, video, audio) = CreateLinkedPairSession();

        // Isolated split cuts only video; audio remains one intact 120-frame clip.
        Assert.True(EditCommands.SplitClipsAtFrame(session, [video.Id], atFrame: 60, expandLinks: false).Ok);
        Assert.Equal(2, session.ActiveTimeline.Tracks[1].Clips.Count);
        Assert.Single(session.ActiveTimeline.Tracks[2].Clips);
        Assert.Equal(120, session.ActiveTimeline.Tracks[2].Clips[0].DurationFrames);
        Assert.Null(session.ActiveTimeline.Tracks[1].Clips[1].LinkGroupId);

        session.Undo();
        Assert.Single(session.ActiveTimeline.Tracks[1].Clips);

        // Isolated delete removes only audio; video stays on V1.
        Assert.True(EditCommands.RemoveClips(session, [audio.Id], ripple: false, expandLinks: false).Ok);
        Assert.Single(session.ActiveTimeline.Tracks[1].Clips);
        Assert.Empty(session.ActiveTimeline.Tracks[2].Clips);

        session.Undo();
        Assert.Single(session.ActiveTimeline.Tracks[2].Clips);
    }
}
