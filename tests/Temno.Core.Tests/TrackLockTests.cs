using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class TrackLockTests
{
    private static ProjectSession CreateSessionWithLinkedPair(out Clip videoClip, out Clip audioClip)
    {
        var session = ProjectSession.CreateNew(); // [V2(0), V1(1), A1(2), A2(3)]
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M1",
            Name = "clip.mp4",
            Type = ClipType.video,
            Duration = 10.0,
            HasAudio = true,
        });

        videoClip = new Clip
        {
            MediaRef = "M1",
            MediaType = ClipType.video,
            SourceClipType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 120,
            LinkGroupId = "LG1",
        };
        audioClip = new Clip
        {
            MediaRef = "M1",
            MediaType = ClipType.audio,
            SourceClipType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 120,
            LinkGroupId = "LG1",
        };

        session.ActiveTimeline.Tracks[1].Clips.Add(videoClip);
        session.ActiveTimeline.Tracks[2].Clips.Add(audioClip);
        return session;
    }

    [Fact]
    public void TrackLocked_OmittedWhenFalse_RoundTripsWhenTrue()
    {
        var unlocked = new Track { Type = ClipType.video };
        var unlockedJson = JsonSerializer.Serialize(unlocked, JsonOptions.ForProject);
        Assert.DoesNotContain("\"locked\"", unlockedJson, StringComparison.Ordinal);

        var locked = new Track { Type = ClipType.video, Locked = true };
        var lockedJson = JsonSerializer.Serialize(locked, JsonOptions.ForProject);
        Assert.Contains("\"locked\": true", lockedJson, StringComparison.Ordinal);

        var roundTripped = JsonSerializer.Deserialize<Track>(lockedJson, JsonOptions.ForProject);
        Assert.NotNull(roundTripped);
        Assert.True(roundTripped!.Locked);
    }

    [Fact]
    public void SetTrackFlags_LockUnlock_RoundTripsUndoRedo_AndBlocksTrackRemoval()
    {
        var session = ProjectSession.CreateNew();
        var v1 = session.ActiveTimeline.Tracks[1];
        Assert.False(v1.Locked);

        Assert.True(EditCommands.SetTrackFlags(session, v1.Id, locked: true).Ok);
        Assert.True(v1.Locked);

        var removeDirect = EditCommands.RemoveTrack(session, v1.Id);
        Assert.False(removeDirect.Ok);
        Assert.Contains("locked", removeDirect.Error, StringComparison.OrdinalIgnoreCase);

        var removeLegacy = EditCommands.ManageTracks(session, "remove", trackIndex: 1);
        Assert.False(removeLegacy.Ok);
        Assert.Contains("locked", removeLegacy.Error, StringComparison.OrdinalIgnoreCase);

        var removeBatch = EditCommands.ApplyTrackEdits(session, [], [], [v1.Id]);
        Assert.False(removeBatch.Ok);
        Assert.Contains("locked", removeBatch.Error, StringComparison.OrdinalIgnoreCase);

        session.Undo();
        Assert.False(session.ActiveTimeline.Tracks[1].Locked);

        session.Redo();
        Assert.True(session.ActiveTimeline.Tracks[1].Locked);

        Assert.True(EditCommands.SetTrackFlags(session, v1.Id, locked: false).Ok);
        Assert.False(session.ActiveTimeline.Tracks[1].Locked);
        Assert.True(EditCommands.RemoveTrack(session, v1.Id).Ok);
    }

    [Fact]
    public void PlacementHelpers_SkipLockedTracks()
    {
        var session = ProjectSession.CreateNew(); // [V2(0), V1(1), A1(2), A2(3)]
        var timeline = session.ActiveTimeline;
        timeline.Tracks[1].Locked = true; // lock V1
        timeline.Tracks[2].Locked = true; // lock A1

        Assert.Equal(0, EditCommands.DefaultTrackForMedia(timeline, ClipType.video));
        Assert.Equal(3, EditCommands.DefaultTrackForMedia(timeline, ClipType.audio));
        Assert.Equal(0, EditCommands.NearestCompatibleTrack(timeline, 1, ClipType.video));
        Assert.Equal(3, EditCommands.NearestCompatibleTrack(timeline, 2, ClipType.audio));

        timeline.Tracks[0].Locked = true; // lock V2 too
        Assert.Equal(-1, EditCommands.DefaultTrackForMedia(timeline, ClipType.video));
        Assert.Equal(-1, EditCommands.NearestCompatibleTrack(timeline, 1, ClipType.video));
    }

    [Fact]
    public void ClipEditCommands_RefuseEditsOnLockedTrack()
    {
        var session = CreateSessionWithLinkedPair(out var vClip, out var aClip);
        var v1 = session.ActiveTimeline.Tracks[1];
        v1.Locked = true;

        var newClip = new Clip { MediaRef = "M1", MediaType = ClipType.video, StartFrame = 150, DurationFrames = 30 };
        Assert.False(EditCommands.AddClip(session, 1, newClip).Ok);
        Assert.False(EditCommands.AddClips(session, [(1, newClip)]).Ok);
        Assert.False(EditCommands.InsertClips(session, 1, 0, [newClip]).Ok);
        Assert.False(EditCommands.AddLinkedMediaDrop(session, 1, newClip, linkAudio: false, out _).Ok);

        Assert.False(EditCommands.MoveClip(session, vClip.Id, 1, 60).Ok);
        Assert.False(EditCommands.MoveClip(session, vClip.Id, 0, 60).Ok);
        Assert.False(EditCommands.MoveClips(session, [new ClipMove(vClip.Id, 1, 60)]).Ok);
        Assert.False(EditCommands.TrimClipEdge(session, vClip.Id, leftEdge: true, 20).Ok);
        Assert.False(EditCommands.TrimClipEdges(session, [new ClipEdgeTrim(vClip.Id, false, 80)]).Ok);
        Assert.False(EditCommands.SplitClipsAtFrame(session, [vClip.Id], 60, expandLinks: false).Ok);
        Assert.False(EditCommands.RemoveClips(session, [vClip.Id], ripple: false, expandLinks: false).Ok);
        Assert.False(EditCommands.RippleDeleteRanges(session, 1, [(10, 30)]).Ok);
        Assert.False(EditCommands.SetClipProperties(
            session, [vClip.Id], null, null, null, null, null, 0.5, null, null).Ok);
        Assert.False(EditCommands.LinkClips(session, [vClip.Id, aClip.Id]).Ok);
        Assert.False(EditCommands.UnlinkClips(session, [vClip.Id]).Ok);

        // Moving an unlocked clip ONTO a locked track is also blocked.
        var unlockedClip = new Clip { MediaRef = "M1", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 30 };
        session.ActiveTimeline.Tracks[0].Clips.Add(unlockedClip);
        Assert.False(EditCommands.MoveClip(session, unlockedClip.Id, 1, 200).Ok);
        Assert.False(EditCommands.MoveClips(session, [new ClipMove(unlockedClip.Id, 1, 200)]).Ok);

        // Original clip on locked track is completely untouched.
        Assert.Single(v1.Clips);
        Assert.Equal(0, vClip.StartFrame);
        Assert.Equal(120, vClip.DurationFrames);
        Assert.Equal(1.0, vClip.Opacity);
    }

    [Fact]
    public void TextKeyframeEffectAndTransitionCommands_RefuseLockedTrack()
    {
        var session = CreateSessionWithLinkedPair(out var vClip, out _);
        var v1 = session.ActiveTimeline.Tracks[1];
        var vClip2 = new Clip
        {
            MediaRef = "M1",
            MediaType = ClipType.video,
            SourceClipType = ClipType.video,
            StartFrame = 120,
            DurationFrames = 120,
        };
        v1.Clips.Add(vClip2);

        var textSpec = new TextClipSpec(1, 300, 360, "Title", new TextStyle(), null, null);
        Assert.True(TextClipCommands.AddTexts(session, [textSpec], out var createdTextIds).Ok);
        var textClipId = Assert.Single(createdTextIds);

        Assert.True(EditCommands.AddTransition(session, vClip.Id, vClip2.Id, "crossDissolve", 20).Ok);
        var transitionId = session.ActiveTimeline.Transitions[0].Id;

        Assert.True(KeyframeCommands.StampOpacity(session, vClip.Id, 10).Ok);

        // Lock V1 and verify every secondary edit surface refuses mutations.
        v1.Locked = true;

        Assert.False(TextClipCommands.AddTexts(
            session, [new TextClipSpec(1, 400, 460, "Blocked", new TextStyle(), null, null)], out _).Ok);
        Assert.False(TextClipCommands.UpdateText(
            session, [textClipId], null, new TextPatch { HasContent = true, Content = "Edited" }, out _, out _).Ok);

        Assert.False(KeyframeCommands.StampOpacity(session, vClip.Id, 20).Ok);
        Assert.False(KeyframeCommands.RemoveAt(session, vClip.Id, "opacity", 10).Ok);
        Assert.False(KeyframeCommands.MoveKeyframe(session, vClip.Id, "opacity", 10, 30).Ok);
        Assert.False(KeyframeCommands.UpsertScalarKey(session, vClip.Id, "opacity", 25, 0.4).Ok);
        Assert.False(KeyframeCommands.MoveScalarKey(session, vClip.Id, "opacity", 10, 25, 0.5).Ok);
        Assert.False(KeyframeCommands.SetInterpolation(session, vClip.Id, "opacity", 10, Interpolation.linear).Ok);
        Assert.False(EditCommands.SetKeyframes(session, vClip.Id, "opacity", [new object[] { 0, 0.5 }]).Ok);

        Assert.False(EditCommands.ApplyEffect(
            session, [vClip.Id], [new EffectEntry { Type = "blur.gaussian" }], null).Ok);
        Assert.False(EditCommands.ApplyColor(session, [vClip.Id], new ColorArgs { Exposure = 0.5 }).Ok);
        Assert.False(EffectStackCommands.AddEffect(session, vClip.Id, "blur.gaussian").Ok);

        Assert.False(EditCommands.AddTransition(session, vClip.Id, vClip2.Id, "wipe", 20).Ok);
        Assert.False(EditCommands.RemoveTransition(session, transitionId).Ok);
        var cutRef = TransitionBlockRef.Cut(transitionId);
        Assert.False(TransitionCommands.SetFrames(session, cutRef, 30, expandLinks: false).Ok);
        Assert.False(TransitionCommands.SetType(session, cutRef, TransitionType.Wipe).Ok);
        Assert.False(TransitionCommands.Remove(session, cutRef, expandLinks: false).Ok);
        Assert.Null(TransitionDrag.BeginResize(session, cutRef, rightEdge: true, expandLinks: false));
        var corner = TransitionBlocks.Corner(session.ActiveTimeline, vClip.Id, FadeEdge.In)!.Value;
        Assert.False(TransitionCommands.Add(session, corner, 15, expandLinks: false, out _).Ok);
        Assert.Null(TransitionDrag.BeginCreate(session, corner, expandLinks: false));

        // TrackForDefault skips locked V1 and picks unlocked A1 instead.
        var defaultTrack = TransitionBlocks.TrackForDefault(new TransitionIndex(session.ActiveTimeline), vClip.Id, 0);
        Assert.Equal(2, defaultTrack);
    }

    [Fact]
    public void LockedAudioTrack_ProtectsAudioWhenEditingLinkedVideo()
    {
        var session = CreateSessionWithLinkedPair(out var vClip, out var aClip);
        var v1 = session.ActiveTimeline.Tracks[1];
        var a1 = session.ActiveTimeline.Tracks[2];
        var a2 = session.ActiveTimeline.Tracks[3];
        a1.Locked = true;

        // Splitting unlocked V1 with expandLinks=true splits V1 only and leaves locked A1 intact.
        Assert.True(EditCommands.SplitClipsAtFrame(session, [vClip.Id], 60, expandLinks: true).Ok);
        Assert.Equal(2, v1.Clips.Count);
        Assert.Single(a1.Clips);
        Assert.Equal(120, aClip.DurationFrames);

        // FadeTargets excludes locked A1 partner.
        var fadeTargets = TransitionCommands.FadeTargets(session.ActiveTimeline, v1.Clips[0], FadeEdge.In, expandLinks: true);
        Assert.Single(fadeTargets);
        Assert.Equal(v1.Clips[0].Id, fadeTargets[0].Id);

        // Deleting unlocked V1 with expandLinks=true removes V1 clip and preserves locked A1.
        Assert.True(EditCommands.RemoveClips(session, [v1.Clips[0].Id], ripple: false, expandLinks: true).Ok);
        Assert.Single(v1.Clips);
        Assert.Single(a1.Clips);

        // Dropping a new video clip with audio onto unlocked V1 routes linked audio to unlocked A2, never locked A1.
        var dropClip = new Clip
        {
            MediaRef = "M1",
            MediaType = ClipType.video,
            SourceClipType = ClipType.video,
            StartFrame = 200,
            DurationFrames = 60,
        };
        Assert.True(EditCommands.AddLinkedMediaDrop(session, 1, dropClip, linkAudio: true, out var audioTrackIdx).Ok);
        Assert.Equal(3, audioTrackIdx);
        Assert.Single(a1.Clips);
        Assert.Single(a2.Clips);
    }

    [Fact]
    public void McpTools_GetTimelineAndManageTracks_ReportLockedFlagAndBlockRemoval()
    {
        var previous = ProjectHub.ActiveSession;
        try
        {
            var session = ProjectSession.CreateNew();
            ProjectHub.ActiveSession = session;

            Assert.True(EditCommands.SetTrackFlags(session, session.ActiveTimeline.Tracks[1].Id, locked: true).Ok);

            var tlResult = ToolExecutor.Execute("get_timeline", new JsonObject());
            Assert.False(tlResult.IsError, tlResult.Content[0].Text);
            using var doc = JsonDocument.Parse(tlResult.Content[0].Text!);
            var tracks = doc.RootElement.GetProperty("tracks");
            Assert.False(tracks[0].TryGetProperty("locked", out _));
            Assert.True(tracks[1].GetProperty("locked").GetBoolean());

            var removeResult = ToolExecutor.Execute("manage_tracks", new JsonObject
            {
                ["remove"] = new JsonArray { 1 },
            });
            Assert.True(removeResult.IsError);
            Assert.Contains("locked", removeResult.Content[0].Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ProjectHub.ActiveSession = previous;
        }
    }
}
