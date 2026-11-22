using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Ui;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class TrackManagementTests
{
    private static string[] LabelsOf(Timeline timeline)
    {
        var types = timeline.Tracks.Select(t => t.Type).ToArray();
        return Enumerable.Range(0, types.Length).Select(i => TrackLabels.LabelFor(types, i)).ToArray();
    }

    [Fact]
    public void AddTrack_CreatesArbitraryVideoAndAudioTracksInTheirZones()
    {
        var session = ProjectSession.CreateNew();
        Assert.Equal(["V2", "V1", "A1", "A2"], LabelsOf(session.ActiveTimeline));

        Assert.True(EditCommands.AddTrack(session, ClipType.video, insertIndex: null, out var v3Id).Ok);
        Assert.NotNull(v3Id);
        Assert.Equal(v3Id, session.ActiveTimeline.Tracks[0].Id);
        Assert.Equal(["V3", "V2", "V1", "A1", "A2"], LabelsOf(session.ActiveTimeline));

        Assert.True(EditCommands.AddTrack(session, ClipType.video).Ok);
        Assert.Equal(["V4", "V3", "V2", "V1", "A1", "A2"], LabelsOf(session.ActiveTimeline));

        Assert.True(EditCommands.AddTrack(session, ClipType.audio, insertIndex: null, out var a3Id).Ok);
        Assert.NotNull(a3Id);
        Assert.Equal(a3Id, session.ActiveTimeline.Tracks[^1].Id);
        Assert.Equal(["V4", "V3", "V2", "V1", "A1", "A2", "A3"], LabelsOf(session.ActiveTimeline));
    }

    [Fact]
    public void AddTrack_ClampsExplicitIndexToTypeZone()
    {
        var session = ProjectSession.CreateNew(); // [V2(0), V1(1), A1(2), A2(3)]

        // Requesting a video track past the audio zone clamps to firstAudio (index 2, below V1 and above A1).
        Assert.True(EditCommands.AddTrack(session, ClipType.video, insertIndex: 99, out var newVideoId).Ok);
        Assert.Equal(newVideoId, session.ActiveTimeline.Tracks[2].Id);
        Assert.Equal(["V3", "V2", "V1", "A1", "A2"], LabelsOf(session.ActiveTimeline));

        // Requesting an audio track at index 0 clamps to firstAudio (index 3, above A1 and below V1).
        Assert.True(EditCommands.AddTrack(session, ClipType.audio, insertIndex: 0, out var newAudioId).Ok);
        Assert.Equal(newAudioId, session.ActiveTimeline.Tracks[3].Id);
        Assert.Equal(["V3", "V2", "V1", "A1", "A2", "A3"], LabelsOf(session.ActiveTimeline));
    }

    [Fact]
    public void AddTrack_RejectsNonPlaceableType()
    {
        var session = ProjectSession.CreateNew();
        var result = EditCommands.AddTrack(session, ClipType.subtitle);
        Assert.False(result.Ok);
        Assert.Equal(4, session.ActiveTimeline.Tracks.Count);
    }

    [Fact]
    public void AddAndRemoveTrack_RoundTripThroughUndoRedo()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddTrack(session, ClipType.video, insertIndex: null, out var v3Id).Ok);
        Assert.Equal(5, session.ActiveTimeline.Tracks.Count);

        session.Undo();
        Assert.Equal(4, session.ActiveTimeline.Tracks.Count);
        Assert.DoesNotContain(session.ActiveTimeline.Tracks, t => t.Id == v3Id);

        session.Redo();
        Assert.Equal(5, session.ActiveTimeline.Tracks.Count);
        Assert.Equal(v3Id, session.ActiveTimeline.Tracks[0].Id);

        Assert.True(EditCommands.RemoveTrack(session, v3Id!).Ok);
        Assert.Equal(4, session.ActiveTimeline.Tracks.Count);

        session.Undo();
        Assert.Equal(5, session.ActiveTimeline.Tracks.Count);
        Assert.Equal(v3Id, session.ActiveTimeline.Tracks[0].Id);
    }

    [Fact]
    public void RemoveTrack_RefusesLastRemainingTrackAndMulticamTrack()
    {
        var session = ProjectSession.CreateNew();
        while (session.ActiveTimeline.Tracks.Count > 1)
            Assert.True(EditCommands.RemoveTrack(session, session.ActiveTimeline.Tracks[^1].Id).Ok);

        var lastFail = EditCommands.RemoveTrack(session, session.ActiveTimeline.Tracks[0].Id);
        Assert.False(lastFail.Ok);
        Assert.Contains("last track", lastFail.Error);
        Assert.Single(session.ActiveTimeline.Tracks);

        // Add a second track with a multicam clip and verify it cannot be deleted directly.
        Assert.True(EditCommands.AddTrack(session, ClipType.video, insertIndex: 0, out var mcTrackId).Ok);
        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            MediaRef = "M1",
            MediaType = ClipType.video,
            DurationFrames = 30,
            MulticamGroupId = "MC-1",
        });
        var mcFail = EditCommands.RemoveTrack(session, mcTrackId!);
        Assert.False(mcFail.Ok);
        Assert.Contains("multicam", mcFail.Error, StringComparison.OrdinalIgnoreCase);

        Assert.False(EditCommands.RemoveTrack(session, "does-not-exist").Ok);
        Assert.False(EditCommands.RemoveTrack(session, "").Ok);
    }

    [Fact]
    public void ManageTracks_McpAddVideo_InsertsInVisualZoneNotBelowAudio()
    {
        var previous = ProjectHub.ActiveSession;
        try
        {
            var session = ProjectSession.CreateNew();
            ProjectHub.ActiveSession = session;

            var result = ToolExecutor.Execute("manage_tracks", new JsonObject
            {
                ["action"] = "add",
                ["type"] = "video",
            });
            Assert.False(result.IsError, result.Content[0].Text);
            Assert.Equal(["V3", "V2", "V1", "A1", "A2"], LabelsOf(session.ActiveTimeline));
            using var doc = JsonDocument.Parse(result.Content[0].Text!);
            var created = Assert.Single(doc.RootElement.GetProperty("createdTracks").EnumerateArray());
            Assert.Equal(0, created.GetProperty("index").GetInt32());
            Assert.Equal("V3", created.GetProperty("label").GetString());
        }
        finally
        {
            ProjectHub.ActiveSession = previous;
        }
    }

    [Fact]
    public void AddMediaToNewTrack_PlacesClipAndLinkedAudioInSingleUndo()
    {
        var session = ProjectSession.CreateNew();
        var videoClip = new Clip
        {
            MediaRef = "VID-1",
            MediaType = ClipType.video,
            SourceClipType = ClipType.video,
            StartFrame = 30,
            DurationFrames = 90,
        };

        var result = EditCommands.AddMediaToNewTrack(
            session, ClipType.video, insertIndex: 0, videoClip, linkAudio: true,
            out var createdTrackIndex, out var audioTrackIndex);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(0, createdTrackIndex);
        Assert.Equal(3, audioTrackIndex); // [V3(0), V2(1), V1(2), A1(3), A2(4)]
        Assert.Equal(["V3", "V2", "V1", "A1", "A2"], LabelsOf(session.ActiveTimeline));
        Assert.Contains(session.ActiveTimeline.Tracks[0].Clips, c => c.Id == videoClip.Id);
        Assert.Contains(session.ActiveTimeline.Tracks[3].Clips, c => c.LinkGroupId == videoClip.LinkGroupId);

        session.Undo();
        Assert.Equal(4, session.ActiveTimeline.Tracks.Count);
        Assert.All(session.ActiveTimeline.Tracks, t => Assert.Empty(t.Clips));
    }

    [Fact]
    public void MoveClipsToNewTrack_MovesVideoAboveTopAndShiftsLinkedAudioInSingleUndo()
    {
        var session = ProjectSession.CreateNew(); // [V2(0), V1(1), A1(2), A2(3)]
        var videoClip = new Clip
        {
            MediaRef = "VID-1",
            MediaType = ClipType.video,
            SourceClipType = ClipType.video,
            StartFrame = 10,
            DurationFrames = 60,
        };
        Assert.True(EditCommands.AddLinkedMediaDrop(session, videoTrackIndex: 0, videoClip, linkAudio: true, out var audioTrackIdx).Ok);
        var audioClip = Assert.Single(session.ActiveTimeline.Tracks[audioTrackIdx].Clips);
        Assert.Equal(2, audioTrackIdx);

        var move = EditCommands.MoveClipsToNewTrack(
            session,
            existingMoves: [new ClipMove(audioClip.Id, audioTrackIdx, 40)],
            newTrackClips: [(videoClip.Id, 40)],
            newTrackType: ClipType.video,
            insertIndex: 0,
            overwrite: false);

        Assert.True(move.Ok, move.Error);
        Assert.Equal(["V3", "V2", "V1", "A1", "A2"], LabelsOf(session.ActiveTimeline));
        var movedVideo = Assert.Single(session.ActiveTimeline.Tracks[0].Clips);
        Assert.Equal(videoClip.Id, movedVideo.Id);
        Assert.Equal(40, movedVideo.StartFrame);
        Assert.Empty(session.ActiveTimeline.Tracks[1].Clips);
        var movedAudio = Assert.Single(session.ActiveTimeline.Tracks[3].Clips); // A1 shifted 2 -> 3
        Assert.Equal(audioClip.Id, movedAudio.Id);
        Assert.Equal(40, movedAudio.StartFrame);

        session.Undo();
        Assert.Equal(4, session.ActiveTimeline.Tracks.Count);
        Assert.Equal(10, Assert.Single(session.ActiveTimeline.Tracks[0].Clips).StartFrame);
        Assert.Equal(10, Assert.Single(session.ActiveTimeline.Tracks[2].Clips).StartFrame);
    }

    [Fact]
    public void MoveClipsToNewTrack_MovesAudioBelowBottomInSingleUndo()
    {
        var session = ProjectSession.CreateNew(); // [V2(0), V1(1), A1(2), A2(3)]
        var audioClip = new Clip
        {
            MediaRef = "AUD-1",
            MediaType = ClipType.audio,
            SourceClipType = ClipType.audio,
            StartFrame = 0,
            DurationFrames = 120,
        };
        Assert.True(EditCommands.AddClip(session, trackIndex: 3, audioClip).Ok);

        var move = EditCommands.MoveClipsToNewTrack(
            session,
            existingMoves: [],
            newTrackClips: [(audioClip.Id, 25)],
            newTrackType: ClipType.audio,
            insertIndex: session.ActiveTimeline.Tracks.Count,
            overwrite: false);

        Assert.True(move.Ok, move.Error);
        Assert.Equal(["V2", "V1", "A1", "A2", "A3"], LabelsOf(session.ActiveTimeline));
        var landed = Assert.Single(session.ActiveTimeline.Tracks[4].Clips);
        Assert.Equal(audioClip.Id, landed.Id);
        Assert.Equal(25, landed.StartFrame);

        session.Undo();
        Assert.Equal(4, session.ActiveTimeline.Tracks.Count);
        Assert.Equal(0, Assert.Single(session.ActiveTimeline.Tracks[3].Clips).StartFrame);
    }

    [Fact]
    public void PlanMoveToNewTrack_PreservesMultiTrackVisualOrderAndPinnedAudio()
    {
        var session = ProjectSession.CreateNew(); // [V2(0), V1(1), A1(2), A2(3)]
        var v2Clip = new Clip { MediaRef = "IMG-1", MediaType = ClipType.image, StartFrame = 0, DurationFrames = 60 };
        var v1Clip = new Clip { MediaRef = "VID-1", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 60 };
        Assert.True(EditCommands.AddClip(session, 0, v2Clip).Ok);
        Assert.True(EditCommands.AddLinkedMediaDrop(session, 1, v1Clip, linkAudio: true, out var a1Idx).Ok);
        var a1Clip = Assert.Single(session.ActiveTimeline.Tracks[a1Idx].Clips);

        // Grab the lower V1 clip (primaryTrackIndex = 1) while V2 + V1 + linked A1 are selected, and drag above V2.
        var items = new[]
        {
            new EditCommands.NewTrackDragItem(v2Clip.Id, 0, ClipType.image, 15),
            new EditCommands.NewTrackDragItem(v1Clip.Id, 1, ClipType.video, 15),
            new EditCommands.NewTrackDragItem(a1Clip.Id, a1Idx, ClipType.audio, 15),
        };
        var plan = EditCommands.PlanMoveToNewTrack(
            session.ActiveTimeline, items, primaryTrackIndex: 1, ClipType.video, insertIndex: 0);

        var newClips = plan.Where(a => a.LandsOnNewTrack).Select(a => (a.ClipId, a.NewStartFrame)).ToArray();
        var existingMoves = plan.Where(a => !a.LandsOnNewTrack)
            .Select(a => new ClipMove(a.ClipId, a.PreInsertTrackIndex, a.NewStartFrame)).ToArray();

        // V2 shifts onto new V3 (0), V1 shifts onto V2 (1), A1 stays on A1 (3) — no z-order inversion!
        Assert.Single(newClips);
        Assert.Equal(v2Clip.Id, newClips[0].ClipId);
        var move = EditCommands.MoveClipsToNewTrack(session, existingMoves, newClips, ClipType.video, insertIndex: 0);
        Assert.True(move.Ok, move.Error);
        Assert.Equal(["V3", "V2", "V1", "A1", "A2"], LabelsOf(session.ActiveTimeline));
        Assert.Equal(v2Clip.Id, Assert.Single(session.ActiveTimeline.Tracks[0].Clips).Id);
        Assert.Equal(v1Clip.Id, Assert.Single(session.ActiveTimeline.Tracks[1].Clips).Id);
        Assert.Empty(session.ActiveTimeline.Tracks[2].Clips);
        Assert.Equal(a1Clip.Id, Assert.Single(session.ActiveTimeline.Tracks[3].Clips).Id);
    }

    [Fact]
    public void PlanMoveToNewTrack_PreservesMultiTrackAudioOrderBelowBottom()
    {
        var session = ProjectSession.CreateNew(); // [V2(0), V1(1), A1(2), A2(3)]
        var a1Clip = new Clip { MediaRef = "AUD-1", MediaType = ClipType.audio, StartFrame = 0, DurationFrames = 60 };
        var a2Clip = new Clip { MediaRef = "AUD-2", MediaType = ClipType.audio, StartFrame = 0, DurationFrames = 60 };
        Assert.True(EditCommands.AddClip(session, 2, a1Clip).Ok);
        Assert.True(EditCommands.AddClip(session, 3, a2Clip).Ok);

        var items = new[]
        {
            new EditCommands.NewTrackDragItem(a1Clip.Id, 2, ClipType.audio, 20),
            new EditCommands.NewTrackDragItem(a2Clip.Id, 3, ClipType.audio, 20),
        };
        var plan = EditCommands.PlanMoveToNewTrack(
            session.ActiveTimeline, items, primaryTrackIndex: 2, ClipType.audio, insertIndex: 4);

        var newClips = plan.Where(a => a.LandsOnNewTrack).Select(a => (a.ClipId, a.NewStartFrame)).ToArray();
        var existingMoves = plan.Where(a => !a.LandsOnNewTrack)
            .Select(a => new ClipMove(a.ClipId, a.PreInsertTrackIndex, a.NewStartFrame)).ToArray();

        var move = EditCommands.MoveClipsToNewTrack(session, existingMoves, newClips, ClipType.audio, insertIndex: 4);
        Assert.True(move.Ok, move.Error);
        Assert.Equal(["V2", "V1", "A1", "A2", "A3"], LabelsOf(session.ActiveTimeline));
        Assert.Empty(session.ActiveTimeline.Tracks[2].Clips);
        Assert.Equal(a1Clip.Id, Assert.Single(session.ActiveTimeline.Tracks[3].Clips).Id);
        Assert.Equal(a2Clip.Id, Assert.Single(session.ActiveTimeline.Tracks[4].Clips).Id);
    }

    [Fact]
    public void RemoveTrackAndManageTracks_PruneOrphanedTransitionsAndEnforceGuards()
    {
        var session = ProjectSession.CreateNew();
        var clipA = new Clip { MediaRef = "M1", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 30 };
        var clipB = new Clip { MediaRef = "M2", MediaType = ClipType.video, StartFrame = 30, DurationFrames = 30 };
        Assert.True(EditCommands.AddClip(session, 0, clipA).Ok);
        Assert.True(EditCommands.AddClip(session, 0, clipB).Ok);
        session.ActiveTimeline.Transitions.Add(new Transition { ClipIdA = clipA.Id, ClipIdB = clipB.Id, DurationFrames = 10 });

        var v2Id = session.ActiveTimeline.Tracks[0].Id;
        Assert.True(EditCommands.RemoveTrack(session, v2Id).Ok);
        Assert.Empty(session.ActiveTimeline.Transitions);

        // ManageTracks rejects non-placeable subtitle tracks and multicam track removal.
        Assert.False(EditCommands.ManageTracks(session, "add", ClipType.subtitle).Ok);

        session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
        {
            MediaRef = "MC",
            MediaType = ClipType.video,
            DurationFrames = 30,
            MulticamGroupId = "GROUP-1",
        });
        var mcRemove = EditCommands.ManageTracks(session, "remove", trackIndex: 0);
        Assert.False(mcRemove.Ok);
        Assert.Contains("multicam", mcRemove.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultTrackForMedia_TargetsV1AndA1AcrossDefaultAndExpandedTimelines()
    {
        var session = ProjectSession.CreateNew(); // [V2(0), V1(1), A1(2), A2(3)]
        var labels = LabelsOf(session.ActiveTimeline);

        var videoIdx = EditCommands.DefaultTrackForMedia(session.ActiveTimeline, ClipType.video);
        var imageIdx = EditCommands.DefaultTrackForMedia(session.ActiveTimeline, ClipType.image);
        var audioIdx = EditCommands.DefaultTrackForMedia(session.ActiveTimeline, ClipType.audio);
        Assert.Equal(1, videoIdx);
        Assert.Equal("V1", labels[videoIdx]);
        Assert.Equal(1, imageIdx);
        Assert.Equal("V1", labels[imageIdx]);
        Assert.Equal(2, audioIdx);
        Assert.Equal("A1", labels[audioIdx]);

        // First linked video drop using DefaultTrackForMedia lands on V1 + A1, leaving V2 empty.
        var clip = new Clip { MediaRef = "VID-1", MediaType = ClipType.video, SourceClipType = ClipType.video, StartFrame = 0, DurationFrames = 60 };
        Assert.True(EditCommands.AddLinkedMediaDrop(session, videoIdx, clip, linkAudio: true, out var linkedAudioIdx).Ok);
        Assert.Equal(2, linkedAudioIdx);
        Assert.Empty(session.ActiveTimeline.Tracks[0].Clips); // V2 stays clear
        Assert.Single(session.ActiveTimeline.Tracks[1].Clips); // V1 holds video
        Assert.Single(session.ActiveTimeline.Tracks[2].Clips); // A1 holds linked audio

        // Adding V3 and A3 still resolves visual to V1 (index 2) and audio to A1 (index 3).
        Assert.True(EditCommands.AddTrack(session, ClipType.video).Ok);
        Assert.True(EditCommands.AddTrack(session, ClipType.audio).Ok);
        var expanded = LabelsOf(session.ActiveTimeline);
        Assert.Equal(["V3", "V2", "V1", "A1", "A2", "A3"], expanded);
        Assert.Equal("V1", expanded[EditCommands.DefaultTrackForMedia(session.ActiveTimeline, ClipType.video)]);
        Assert.Equal("A1", expanded[EditCommands.DefaultTrackForMedia(session.ActiveTimeline, ClipType.audio)]);

        // Missing zone returns -1 so callers can create a track in the proper zone.
        var audioOnly = new Timeline { Tracks = [new Track { Type = ClipType.audio }] };
        Assert.Equal(-1, EditCommands.DefaultTrackForMedia(audioOnly, ClipType.video));
        Assert.Equal(0, EditCommands.DefaultTrackForMedia(audioOnly, ClipType.audio));

        var videoOnly = new Timeline { Tracks = [new Track { Type = ClipType.video }, new Track { Type = ClipType.video }] };
        Assert.Equal(1, EditCommands.DefaultTrackForMedia(videoOnly, ClipType.video));
        Assert.Equal(-1, EditCommands.DefaultTrackForMedia(videoOnly, ClipType.audio));
    }
}
