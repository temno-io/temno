using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public class LinkGroupTests
{
    private static ProjectSession SessionWithLinkedAv()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClips(session,
        [
            (0, new Clip { Id = "V", MediaRef = "m", StartFrame = 10, DurationFrames = 40, MediaType = ClipType.video }),
            (2, new Clip { Id = "A", MediaRef = "m", StartFrame = 10, DurationFrames = 40, MediaType = ClipType.audio }),
            (0, new Clip { Id = "Solo", MediaRef = "x", StartFrame = 100, DurationFrames = 10, MediaType = ClipType.video }),
        ]).Ok);
        Assert.True(EditCommands.LinkClips(session, ["V", "A"]).Ok);
        return session;
    }

    [Fact]
    public void Expand_ReturnsPartnersForLinkedClip()
    {
        var session = SessionWithLinkedAv();
        var expanded = LinkGroup.Expand(session.ActiveTimeline, ["V"]);
        Assert.Equal(2, expanded.Count);
        Assert.Contains("V", expanded);
        Assert.Contains("A", expanded);
    }

    [Fact]
    public void Expand_UnlinkedReturnsSeedOnly()
    {
        var session = SessionWithLinkedAv();
        var expanded = LinkGroup.Expand(session.ActiveTimeline, ["Solo"]);
        Assert.Equal(["Solo"], expanded.OrderBy(x => x));
    }

    [Fact]
    public void LinkClips_IsUndoable()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClips(session,
        [
            (0, new Clip { Id = "V", MediaRef = "m", StartFrame = 0, DurationFrames = 20 }),
            (2, new Clip { Id = "A", MediaRef = "m", StartFrame = 0, DurationFrames = 20, MediaType = ClipType.audio }),
        ]).Ok);

        Assert.True(EditCommands.LinkClips(session, ["V", "A"]).Ok);
        var v = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == "V");
        var a = session.ActiveTimeline.Tracks[2].Clips.Single(c => c.Id == "A");
        Assert.False(string.IsNullOrEmpty(v.LinkGroupId));
        Assert.Equal(v.LinkGroupId, a.LinkGroupId);

        session.Undo();
        v = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == "V");
        a = session.ActiveTimeline.Tracks[2].Clips.Single(c => c.Id == "A");
        Assert.Null(v.LinkGroupId);
        Assert.Null(a.LinkGroupId);
    }

    [Fact]
    public void UnlinkClips_ClearsWholeGroup()
    {
        var session = SessionWithLinkedAv();
        Assert.True(EditCommands.UnlinkClips(session, ["V"]).Ok);
        Assert.All(
            session.ActiveTimeline.Tracks.SelectMany(t => t.Clips),
            c => Assert.True(c.Id == "Solo" || c.LinkGroupId is null));
    }

    [Fact]
    public void MoveClips_WithExpandedLinkGroup_KeepsRelativeStarts()
    {
        var session = SessionWithLinkedAv();
        var expanded = LinkGroup.Expand(session.ActiveTimeline, ["V"]);
        var moves = expanded.Select(id =>
        {
            var clip = session.ActiveTimeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == id);
            var track = session.ActiveTimeline.Tracks.FindIndex(t => t.Clips.Any(c => c.Id == id));
            return new ClipMove(id, track, clip.StartFrame + 15);
        }).ToArray();

        Assert.True(EditCommands.MoveClips(session, moves).Ok);
        Assert.Equal(25, session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == "V").StartFrame);
        Assert.Equal(25, session.ActiveTimeline.Tracks[2].Clips.Single(c => c.Id == "A").StartFrame);
    }

    [Fact]
    public void RenameAndRelinkMedia_RoundTrip()
    {
        var session = ProjectSession.CreateNew();
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M1",
            Name = "old.mp4",
            Type = ClipType.video,
            Source = MediaSource.External(@"C:\media\old.mp4"),
            Duration = 5,
        });

        Assert.True(EditCommands.RenameMediaEntry(session, "M1", "new name").Ok);
        Assert.Equal("new name", session.Manifest.Entries.Single().Name);

        Assert.True(EditCommands.RelinkMediaEntry(session, "M1", @"D:\media\found.mp4").Ok);
        Assert.Equal(@"D:\media\found.mp4", session.Resolver.ResolvePath(session.Manifest.Entries.Single()));

        session.Undo();
        session.Undo();
        Assert.Equal("old.mp4", session.Manifest.Entries.Single().Name);
    }

    [Fact]
    public void RemoveMediaEntry_RejectsWhenInUse()
    {
        var session = ProjectSession.CreateNew();
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M1",
            Name = "clip.mp4",
            Type = ClipType.video,
            Source = MediaSource.External(@"C:\a.mp4"),
        });
        Assert.True(EditCommands.AddClip(session, 0,
            new Clip { Id = "C1", MediaRef = "M1", StartFrame = 0, DurationFrames = 10 }).Ok);

        Assert.False(EditCommands.RemoveMediaEntry(session, "M1").Ok);
        Assert.True(EditCommands.RemoveClips(session, ["C1"], ripple: false).Ok);
        Assert.True(EditCommands.RemoveMediaEntry(session, "M1").Ok);
        Assert.Empty(session.Manifest.Entries);
    }
}
