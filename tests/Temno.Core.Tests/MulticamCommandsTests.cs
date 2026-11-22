using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public sealed class MulticamCommandsTests
{
    [Fact]
    public void CreateGroup_PlacesProgramAndAudio()
    {
        var session = ProjectSession.CreateNew();
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "camA", Name = "A", Type = ClipType.video, Duration = 10, HasAudio = true,
            Source = MediaSource.External("/a.mp4"),
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "camB", Name = "B", Type = ClipType.video, Duration = 10, HasAudio = true,
            Source = MediaSource.External("/b.mp4"),
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "mic1", Name = "Mic", Type = ClipType.audio, Duration = 10, HasAudio = true,
            Source = MediaSource.External("/m.wav"),
        });

        var maps = new Dictionary<string, MulticamSyncMap>
        {
            ["camA"] = new() { OffsetSeconds = 0, Confidence = 1 },
            ["camB"] = new() { OffsetSeconds = 1, Confidence = 0.9 },
            ["mic1"] = new() { OffsetSeconds = 0, Confidence = 1, Locked = true },
        };
        var durations = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Duration);
        var types = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Type);

        var result = MulticamCommands.CreateGroup(
            session,
            [
                new MulticamCommands.MemberSpec { MediaRef = "camA", Kind = MulticamMemberKind.both, AngleLabel = "a" },
                new MulticamCommands.MemberSpec { MediaRef = "camB", Kind = MulticamMemberKind.angle, AngleLabel = "b" },
                new MulticamCommands.MemberSpec { MediaRef = "mic1", Kind = MulticamMemberKind.mic, AngleLabel = "mic" },
            ],
            maps,
            masterMediaRef: "camA",
            durations,
            types,
            name: "Test MC",
            startFrame: 0,
            out var created);

        Assert.True(result.Ok, result.Error);
        Assert.False(string.IsNullOrEmpty(created.GroupId));
        Assert.NotEmpty(created.ClipIds);
        Assert.NotNull(MulticamCommands.FindGroup(session, created.GroupId));
        Assert.Contains(session.ActiveTimeline.Tracks.SelectMany(t => t.Clips),
            c => c.MulticamGroupId == created.GroupId && c.MediaType == ClipType.video);
        Assert.Contains(session.ActiveTimeline.Tracks.SelectMany(t => t.Clips),
            c => c.MulticamGroupId == created.GroupId && c.MediaType == ClipType.audio);
        Assert.True(session.CanUndo());
    }

    [Fact]
    public void ChangeCam_RewritesMediaRef()
    {
        var session = ProjectSession.CreateNew();
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "camA", Type = ClipType.video, Duration = 5, Source = MediaSource.External("/a.mp4"),
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "camB", Type = ClipType.video, Duration = 5, Source = MediaSource.External("/b.mp4"),
        });
        var maps = new Dictionary<string, MulticamSyncMap>
        {
            ["camA"] = new() { OffsetSeconds = 0, Confidence = 1 },
            ["camB"] = new() { OffsetSeconds = 0, Confidence = 1 },
        };
        var durations = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Duration);
        var types = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Type);
        Assert.True(MulticamCommands.CreateGroup(
            session,
            [
                new MulticamCommands.MemberSpec { MediaRef = "camA", Kind = MulticamMemberKind.angle, AngleLabel = "a" },
                new MulticamCommands.MemberSpec { MediaRef = "camB", Kind = MulticamMemberKind.angle, AngleLabel = "b" },
            ],
            maps, "camA", durations, types, "MC", 0, out var created).Ok);

        var before = session.ActiveTimeline.Tracks.SelectMany(t => t.Clips)
            .First(c => c.MulticamGroupId == created.GroupId && c.MediaType == ClipType.video);
        var end = before.EndFrame;

        var sw = MulticamCommands.ChangeCam(
            session, created.GroupId, before.StartFrame, end, "b", durations, out var outcome);
        Assert.True(sw.Ok, sw.Error);
        Assert.True(outcome.Switched >= 1);

        var after = session.ActiveTimeline.Tracks.SelectMany(t => t.Clips)
            .Where(c => c.MulticamGroupId == created.GroupId && c.MediaType == ClipType.video)
            .OrderBy(c => c.StartFrame)
            .First();
        Assert.Equal("camB", after.MediaRef);
    }

    [Fact]
    public void Ungroup_ClearsStamps()
    {
        var session = ProjectSession.CreateNew();
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "camA", Type = ClipType.video, Duration = 3, Source = MediaSource.External("/a.mp4"),
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "camB", Type = ClipType.video, Duration = 3, Source = MediaSource.External("/b.mp4"),
        });
        var maps = new Dictionary<string, MulticamSyncMap>
        {
            ["camA"] = new() { OffsetSeconds = 0, Confidence = 1 },
            ["camB"] = new() { OffsetSeconds = 0, Confidence = 1 },
        };
        var durations = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Duration);
        var types = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Type);
        Assert.True(MulticamCommands.CreateGroup(
            session,
            [
                new MulticamCommands.MemberSpec { MediaRef = "camA", Kind = MulticamMemberKind.angle },
                new MulticamCommands.MemberSpec { MediaRef = "camB", Kind = MulticamMemberKind.angle },
            ],
            maps, "camA", durations, types, null, 0, out var created).Ok);

        Assert.True(MulticamCommands.Ungroup(session, created.GroupId).Ok);
        Assert.Null(MulticamCommands.FindGroup(session, created.GroupId));
        Assert.DoesNotContain(session.ActiveTimeline.Tracks.SelectMany(t => t.Clips),
            c => c.MulticamGroupId == created.GroupId);
    }

    [Fact]
    public void ChangeCam_SideBySide_PlacesOverlayAndProgramTransform()
    {
        var session = ProjectSession.CreateNew();
        session.ActiveTimeline.Width = 1920;
        session.ActiveTimeline.Height = 1080;
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "camA", Type = ClipType.video, Duration = 5,
            SourceWidth = 1920, SourceHeight = 1080,
            Source = MediaSource.External("/a.mp4"),
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "camB", Type = ClipType.video, Duration = 5,
            SourceWidth = 1920, SourceHeight = 1080,
            Source = MediaSource.External("/b.mp4"),
        });
        var maps = new Dictionary<string, MulticamSyncMap>
        {
            ["camA"] = new() { OffsetSeconds = 0, Confidence = 1 },
            ["camB"] = new() { OffsetSeconds = 0, Confidence = 1 },
        };
        var durations = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Duration);
        var types = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Type);
        var sizes = session.Manifest.Entries.ToDictionary(
            e => e.Id, e => ((double)e.SourceWidth!.Value, (double)e.SourceHeight!.Value));
        Assert.True(MulticamCommands.CreateGroup(
            session,
            [
                new MulticamCommands.MemberSpec { MediaRef = "camA", Kind = MulticamMemberKind.angle, AngleLabel = "a" },
                new MulticamCommands.MemberSpec { MediaRef = "camB", Kind = MulticamMemberKind.angle, AngleLabel = "b" },
            ],
            maps, "camA", durations, types, "MC", 0, out var created).Ok);

        var before = session.ActiveTimeline.Tracks.SelectMany(t => t.Clips)
            .First(c => c.MulticamGroupId == created.GroupId && c.MediaType == ClipType.video);

        var sw = MulticamCommands.ChangeCam(
            session,
            created.GroupId,
            before.StartFrame,
            before.EndFrame,
            primaryAngleLabel: "a",
            extraAngleLabels: ["b"],
            layout: LayoutKind.SideBySide,
            durations,
            sizes,
            out var outcome);
        Assert.True(sw.Ok, sw.Error);
        Assert.True(outcome.Switched >= 1);
        Assert.NotEmpty(outcome.OverlayClipIds);

        var program = session.ActiveTimeline.Tracks
            .SelectMany(t => t.Clips)
            .First(c => c.MulticamGroupId == created.GroupId
                        && c.MediaRef == "camA"
                        && c.MediaType != ClipType.audio);
        // Fill-mode side_by_side left slot: center sits on the left half (CX≈0.25).
        Assert.True(program.Transform.CenterX < 0.4,
            $"expected left-slot program center, got CX={program.Transform.CenterX}");

        var overlay = session.ActiveTimeline.Tracks
            .SelectMany(t => t.Clips)
            .First(c => outcome.OverlayClipIds.Contains(c.Id));
        Assert.Equal("camB", overlay.MediaRef);
        Assert.Equal(created.GroupId, overlay.MulticamGroupId);
        Assert.True(overlay.Transform.CenterX > 0.6,
            $"expected right-slot overlay center, got CX={overlay.Transform.CenterX}");
        // Overlay track sits above program (lower index).
        var overlayTrack = session.ActiveTimeline.Tracks.FindIndex(t => t.Clips.Any(c => c.Id == overlay.Id));
        var programTrack = session.ActiveTimeline.Tracks.FindIndex(t => t.Clips.Any(c =>
            c.MulticamGroupId == created.GroupId && c.MediaRef == "camA" && c.MediaType != ClipType.audio));
        Assert.True(overlayTrack < programTrack);
    }

    [Fact]
    public void ChangeCam_FullFrame_ClearsPriorLayoutOverlays()
    {
        var session = ProjectSession.CreateNew();
        session.ActiveTimeline.Width = 1920;
        session.ActiveTimeline.Height = 1080;
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "camA", Type = ClipType.video, Duration = 5, Source = MediaSource.External("/a.mp4"),
        });
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "camB", Type = ClipType.video, Duration = 5, Source = MediaSource.External("/b.mp4"),
        });
        var maps = new Dictionary<string, MulticamSyncMap>
        {
            ["camA"] = new() { OffsetSeconds = 0, Confidence = 1 },
            ["camB"] = new() { OffsetSeconds = 0, Confidence = 1 },
        };
        var durations = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Duration);
        var types = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Type);
        Assert.True(MulticamCommands.CreateGroup(
            session,
            [
                new MulticamCommands.MemberSpec { MediaRef = "camA", Kind = MulticamMemberKind.angle, AngleLabel = "a" },
                new MulticamCommands.MemberSpec { MediaRef = "camB", Kind = MulticamMemberKind.angle, AngleLabel = "b" },
            ],
            maps, "camA", durations, types, "MC", 0, out var created).Ok);

        var clip = session.ActiveTimeline.Tracks.SelectMany(t => t.Clips)
            .First(c => c.MulticamGroupId == created.GroupId && c.MediaType == ClipType.video);
        Assert.True(MulticamCommands.ChangeCam(
            session, created.GroupId, clip.StartFrame, clip.EndFrame,
            "a", ["b"], LayoutKind.SideBySide, durations, null, out _).Ok);

        Assert.True(MulticamCommands.ChangeCam(
            session, created.GroupId, clip.StartFrame, clip.EndFrame, "b", durations, out var full).Ok);
        Assert.Empty(full.OverlayClipIds);

        var remaining = session.ActiveTimeline.Tracks.SelectMany(t => t.Clips)
            .Where(c => c.MulticamGroupId == created.GroupId && c.MediaType != ClipType.audio)
            .ToList();
        // Full-frame: single program angle, identity-ish placement (center ~0.5).
        Assert.All(remaining, c => Assert.Equal("camB", c.MediaRef));
        Assert.All(remaining, c => Assert.InRange(c.Transform.CenterX, 0.45, 0.55));
    }

    [Fact]
    public void ProgramRows_RunLength()
    {
        var session = ProjectSession.CreateNew();
        var group = new MulticamSource
        {
            Name = "G",
            Members =
            [
                new MulticamMember { MediaRef = "a", AngleLabel = "cam-a", Kind = MulticamMemberKind.angle, Sync = new() { Confidence = 1 } },
                new MulticamMember { MediaRef = "b", AngleLabel = "cam-b", Kind = MulticamMemberKind.angle, Sync = new() { Confidence = 1 } },
            ],
        };
        group.MasterMemberId = group.Members[0].Id;
        session.ProjectFile.MulticamGroups = [group];
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            MediaRef = "a", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 10, MulticamGroupId = group.Id,
        });
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            MediaRef = "a", MediaType = ClipType.video, StartFrame = 10, DurationFrames = 10, MulticamGroupId = group.Id,
            TrimStartFrame = 10,
        });
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            MediaRef = "b", MediaType = ClipType.video, StartFrame = 20, DurationFrames = 10, MulticamGroupId = group.Id,
        });

        var rows = MulticamCommands.ProgramRows(session, group.Id, null, null);
        Assert.Equal(2, rows.Count);
        Assert.Equal("cam-a", rows[0][0]);
        Assert.Equal(0, rows[0][1]);
        Assert.Equal(20, rows[0][2]);
        Assert.Equal("cam-b", rows[1][0]);
    }
}
