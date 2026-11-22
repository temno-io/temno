using Temno.Core.Models;
using Temno.Core.Editing;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public class EditCommandsTests
{
    [Fact]
    public void AddLinkedMediaDrop_PlacesVideoAndLinkedAudio()
    {
        var session = ProjectSession.CreateNew();
        // Default new project has V/V/A/A tracks.
        var video = new Clip
        {
            MediaRef = "cam",
            MediaType = ClipType.video,
            SourceClipType = ClipType.video,
            StartFrame = 10,
            DurationFrames = 50,
        };
        Assert.True(EditCommands.AddLinkedMediaDrop(session, 0, video, linkAudio: true, out var audioTrack).Ok);
        Assert.True(audioTrack >= 0);
        Assert.Equal(ClipType.audio, session.ActiveTimeline.Tracks[audioTrack].Type);
        var v = session.ActiveTimeline.Tracks[0].Clips.Single();
        var a = session.ActiveTimeline.Tracks[audioTrack].Clips.Single();
        Assert.Equal(v.MediaRef, a.MediaRef);
        Assert.Equal(v.StartFrame, a.StartFrame);
        Assert.Equal(v.DurationFrames, a.DurationFrames);
        Assert.False(string.IsNullOrEmpty(v.LinkGroupId));
        Assert.Equal(v.LinkGroupId, a.LinkGroupId);
        Assert.Equal(ClipType.audio, a.MediaType);
    }

    [Fact]
    public void AddSplitRemove_WorkOnSession()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip
        {
            Id = "C1",
            MediaRef = "M1",
            StartFrame = 0,
            DurationFrames = 100,
        };
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);
        Assert.True(EditCommands.SplitClip(session, "C1", 40).Ok);
        Assert.Equal(2, session.ActiveTimeline.Tracks[0].Clips.Count);
        Assert.Equal(40, session.ActiveTimeline.Tracks[0].Clips[0].DurationFrames);
        Assert.Equal(40, session.ActiveTimeline.Tracks[0].Clips[1].StartFrame);
        Assert.Equal(60, session.ActiveTimeline.Tracks[0].Clips[1].DurationFrames);

        var ids = session.ActiveTimeline.Tracks[0].Clips.Select(c => c.Id).ToList();
        Assert.True(EditCommands.RemoveClips(session, ids, ripple: false).Ok);
        Assert.Empty(session.ActiveTimeline.Tracks[0].Clips);
    }

    [Fact]
    public void RippleRemove_ClosesGap()
    {
        var session = ProjectSession.CreateNew();
        EditCommands.AddClip(session, 0, new Clip { Id = "A", MediaRef = "m", StartFrame = 0, DurationFrames = 10 });
        EditCommands.AddClip(session, 0, new Clip { Id = "B", MediaRef = "m", StartFrame = 10, DurationFrames = 10 });
        EditCommands.AddClip(session, 0, new Clip { Id = "C", MediaRef = "m", StartFrame = 20, DurationFrames = 10 });
        Assert.True(EditCommands.RemoveClips(session, ["B"], ripple: true).Ok);
        var clips = session.ActiveTimeline.Tracks[0].Clips.OrderBy(c => c.StartFrame).ToList();
        Assert.Equal(2, clips.Count);
        Assert.Equal("A", clips[0].Id);
        Assert.Equal("C", clips[1].Id);
        Assert.Equal(10, clips[1].StartFrame);
    }

    [Fact]
    public void MoveClips_IsAtomicAndUndoableInOneStep()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClips(session,
        [
            (0, new Clip { Id = "A", MediaRef = "a", StartFrame = 10, DurationFrames = 20 }),
            (1, new Clip { Id = "B", MediaRef = "b", StartFrame = 40, DurationFrames = 30 }),
            (1, new Clip { Id = "C", MediaRef = "c", StartFrame = 100, DurationFrames = 10 }),
        ]).Ok);

        Assert.True(EditCommands.MoveClips(session,
        [
            new ClipMove("A", TrackIndex: 1, NewStartFrame: 50),
            new ClipMove("B", TrackIndex: 0, NewStartFrame: 80),
        ]).Ok);

        Assert.DoesNotContain(session.ActiveTimeline.Tracks[0].Clips, clip => clip.Id == "A");
        Assert.Equal(80, session.ActiveTimeline.Tracks[0].Clips.Single(clip => clip.Id == "B").StartFrame);
        Assert.Equal(50, session.ActiveTimeline.Tracks[1].Clips.Single(clip => clip.Id == "A").StartFrame);
        Assert.Equal(100, session.ActiveTimeline.Tracks[1].Clips.Single(clip => clip.Id == "C").StartFrame);

        session.Undo();

        Assert.Equal(10, session.ActiveTimeline.Tracks[0].Clips.Single(clip => clip.Id == "A").StartFrame);
        Assert.Equal(40, session.ActiveTimeline.Tracks[1].Clips.Single(clip => clip.Id == "B").StartFrame);
        Assert.DoesNotContain(session.ActiveTimeline.Tracks[0].Clips, clip => clip.Id == "B");
        Assert.Equal(100, session.ActiveTimeline.Tracks[1].Clips.Single(clip => clip.Id == "C").StartFrame);

        session.Redo();
        Assert.Equal(80, session.ActiveTimeline.Tracks[0].Clips.Single(clip => clip.Id == "B").StartFrame);
        Assert.Equal(50, session.ActiveTimeline.Tracks[1].Clips.Single(clip => clip.Id == "A").StartFrame);
    }

    [Fact]
    public void MoveClips_RejectsVideoOntoAudioTrack()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClips(session,
        [
            (0, new Clip { Id = "A", MediaRef = "a", StartFrame = 10, DurationFrames = 20, MediaType = ClipType.video }),
        ]).Ok);

        var result = EditCommands.MoveClips(session,
        [
            new ClipMove("A", TrackIndex: 2, NewStartFrame: 50),
        ]);

        Assert.False(result.Ok);
        Assert.Equal(10, session.ActiveTimeline.Tracks[0].Clips.Single(clip => clip.Id == "A").StartFrame);
        Assert.Empty(session.ActiveTimeline.Tracks[2].Clips);
    }

    [Fact]
    public void TrimClipEdges_IsAtomicAndUndoableInOneStep()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClips(session,
        [
            (0, new Clip { Id = "A", MediaRef = "a", StartFrame = 10, DurationFrames = 40, TrimStartFrame = 5 }),
            (1, new Clip { Id = "B", MediaRef = "b", StartFrame = 20, DurationFrames = 40, TrimStartFrame = 2 }),
        ]).Ok);

        Assert.True(EditCommands.TrimClipEdges(session,
        [
            new ClipEdgeTrim("A", LeftEdge: true, EdgeFrame: 20),
            new ClipEdgeTrim("B", LeftEdge: false, EdgeFrame: 45),
        ]).Ok);

        var first = session.ActiveTimeline.Tracks[0].Clips.Single();
        var second = session.ActiveTimeline.Tracks[1].Clips.Single();
        Assert.Equal((20, 30, 15), (first.StartFrame, first.DurationFrames, first.TrimStartFrame));
        Assert.Equal((20, 25, 2), (second.StartFrame, second.DurationFrames, second.TrimStartFrame));

        session.Undo();
        first = session.ActiveTimeline.Tracks[0].Clips.Single();
        second = session.ActiveTimeline.Tracks[1].Clips.Single();
        Assert.Equal((10, 40, 5), (first.StartFrame, first.DurationFrames, first.TrimStartFrame));
        Assert.Equal((20, 40, 2), (second.StartFrame, second.DurationFrames, second.TrimStartFrame));
    }

    [Fact]
    public void GroupedEdits_RollBackWhenAnyTargetIsInvalid()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0,
            new Clip { Id = "A", MediaRef = "a", StartFrame = 12, DurationFrames = 30 }).Ok);

        var move = EditCommands.MoveClips(session,
        [
            new ClipMove("A", TrackIndex: 1, NewStartFrame: 90),
            new ClipMove("missing", TrackIndex: 1, NewStartFrame: 120),
        ]);
        Assert.False(move.Ok);
        Assert.Equal(12, session.ActiveTimeline.Tracks[0].Clips.Single(clip => clip.Id == "A").StartFrame);
        Assert.Empty(session.ActiveTimeline.Tracks[1].Clips);

        var trim = EditCommands.TrimClipEdges(session,
        [
            new ClipEdgeTrim("A", LeftEdge: true, EdgeFrame: 20),
            new ClipEdgeTrim("missing", LeftEdge: false, EdgeFrame: 40),
        ]);
        Assert.False(trim.Ok);
        var original = session.ActiveTimeline.Tracks[0].Clips.Single(clip => clip.Id == "A");
        Assert.Equal((12, 30), (original.StartFrame, original.DurationFrames));
    }

    [Fact]
    public void SetTrackFlags_IsUndoableAndRejectsUnknownTracks()
    {
        var session = ProjectSession.CreateNew();
        var track = session.ActiveTimeline.Tracks[0];

        Assert.False(EditCommands.SetTrackFlags(session, "missing", hidden: true).Ok);
        Assert.False(session.CanUndo());

        Assert.True(EditCommands.SetTrackFlags(session, track.Id, hidden: true, syncLocked: false).Ok);
        Assert.True(track.Hidden);
        Assert.False(track.SyncLocked);

        session.Undo();
        track = session.ActiveTimeline.Tracks.Single(candidate => candidate.Id == track.Id);
        Assert.False(track.Hidden);
        Assert.True(track.SyncLocked);

        session.Redo();
        track = session.ActiveTimeline.Tracks.Single(candidate => candidate.Id == track.Id);
        Assert.True(track.Hidden);
        Assert.False(track.SyncLocked);
    }

    [Fact]
    public void SetTrackFlags_Muted_IsUndoableAndNoOpDoesNotPushUndo()
    {
        var session = ProjectSession.CreateNew();
        var audio = session.ActiveTimeline.Tracks.First(track => track.Type == ClipType.audio);
        Assert.False(audio.Muted);

        Assert.True(EditCommands.SetTrackFlags(session, audio.Id, muted: true).Ok);
        Assert.True(audio.Muted);
        Assert.True(session.CanUndo());

        // No-op: already muted — must not create another undo entry.
        Assert.True(EditCommands.SetTrackFlags(session, audio.Id, muted: true).Ok);
        session.Undo();
        audio = session.ActiveTimeline.Tracks.Single(candidate => candidate.Id == audio.Id);
        Assert.False(audio.Muted);
        Assert.False(session.CanUndo());
    }

    [Fact]
    public void RemoveClips_MultipleIds_IsOneUndoStep()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClips(session,
        [
            (0, new Clip { Id = "A", MediaRef = "a", StartFrame = 0, DurationFrames = 10 }),
            (0, new Clip { Id = "B", MediaRef = "b", StartFrame = 20, DurationFrames = 10 }),
            (1, new Clip { Id = "C", MediaRef = "c", StartFrame = 5, DurationFrames = 15 }),
        ]).Ok);

        Assert.True(EditCommands.RemoveClips(session, ["A", "C"], ripple: false).Ok);
        Assert.DoesNotContain(session.ActiveTimeline.Tracks[0].Clips, clip => clip.Id == "A");
        Assert.DoesNotContain(session.ActiveTimeline.Tracks[1].Clips, clip => clip.Id == "C");
        Assert.Contains(session.ActiveTimeline.Tracks[0].Clips, clip => clip.Id == "B");

        session.Undo();
        Assert.Contains(session.ActiveTimeline.Tracks[0].Clips, clip => clip.Id == "A");
        Assert.Contains(session.ActiveTimeline.Tracks[1].Clips, clip => clip.Id == "C");
        Assert.Contains(session.ActiveTimeline.Tracks[0].Clips, clip => clip.Id == "B");

        session.Redo();
        Assert.DoesNotContain(session.ActiveTimeline.Tracks[0].Clips, clip => clip.Id == "A");
        Assert.DoesNotContain(session.ActiveTimeline.Tracks[1].Clips, clip => clip.Id == "C");
    }

    [Fact]
    public void MoveClips_OverwriteDoesNotTrimOtherMembersOfGroup()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClips(session,
        [
            (0, new Clip { Id = "A", MediaRef = "a", StartFrame = 0, DurationFrames = 30 }),
            (0, new Clip { Id = "B", MediaRef = "b", StartFrame = 40, DurationFrames = 30 }),
        ]).Ok);

        // Move both onto the same destination range with overwrite: members must not eat each other.
        Assert.True(EditCommands.MoveClips(session,
        [
            new ClipMove("A", TrackIndex: 1, NewStartFrame: 10),
            new ClipMove("B", TrackIndex: 1, NewStartFrame: 30),
        ], overwrite: true).Ok);

        var moved = session.ActiveTimeline.Tracks[1].Clips.OrderBy(clip => clip.StartFrame).ToList();
        Assert.Equal(2, moved.Count);
        Assert.Equal(("A", 10, 30), (moved[0].Id, moved[0].StartFrame, moved[0].DurationFrames));
        Assert.Equal(("B", 30, 30), (moved[1].Id, moved[1].StartFrame, moved[1].DurationFrames));
    }

    [Theory]
    [InlineData(-40, TimelineGeometry.MinTrackHeight)]
    [InlineData(88, 88)]
    [InlineData(999, TimelineGeometry.MaxTrackHeight)]
    public void SetTrackHeight_ClampsAndIsUndoable(double requested, double expected)
    {
        var session = ProjectSession.CreateNew();
        var track = session.ActiveTimeline.Tracks[0];
        var original = track.DisplayHeight;

        Assert.True(EditCommands.SetTrackHeight(session, track.Id, requested).Ok);
        Assert.Equal(expected, track.DisplayHeight);

        var roundTripped = ProjectFile.Decode(session.ProjectFile.Encode());
        Assert.Equal(expected, roundTripped.Timelines.Single().Tracks.Single(candidate => candidate.Id == track.Id).DisplayHeight);

        session.Undo();
        track = session.ActiveTimeline.Tracks.Single(candidate => candidate.Id == track.Id);
        Assert.Equal(original, track.DisplayHeight);
    }

    [Fact]
    public void SetTrackHeight_RejectsNonFiniteValues()
    {
        var session = ProjectSession.CreateNew();
        var track = session.ActiveTimeline.Tracks[0];

        Assert.False(EditCommands.SetTrackHeight(session, track.Id, double.NaN).Ok);
        Assert.False(EditCommands.SetTrackHeight(session, track.Id, double.PositiveInfinity).Ok);
        Assert.False(session.CanUndo());
    }

    [Fact]
    public void ReorderTrack_PreservesVisualAndAudioZonesAndUndo()
    {
        var session = ProjectSession.CreateNew();
        var videoOne = session.ActiveTimeline.Tracks[0];
        var videoTwo = session.ActiveTimeline.Tracks[1];
        var audioOne = session.ActiveTimeline.Tracks[2];
        var audioTwo = session.ActiveTimeline.Tracks[3];

        Assert.True(EditCommands.ReorderTrack(session, videoOne.Id, targetIndex: 3).Ok);
        Assert.Equal([videoTwo.Id, videoOne.Id, audioOne.Id, audioTwo.Id],
            session.ActiveTimeline.Tracks.Select(track => track.Id));

        session.Undo();
        Assert.Equal([videoOne.Id, videoTwo.Id, audioOne.Id, audioTwo.Id],
            session.ActiveTimeline.Tracks.Select(track => track.Id));

        Assert.True(EditCommands.ReorderTrack(session, audioOne.Id, targetIndex: 0).Ok);
        Assert.Equal([videoOne.Id, videoTwo.Id, audioOne.Id, audioTwo.Id],
            session.ActiveTimeline.Tracks.Select(track => track.Id));
        Assert.False(session.CanUndo());
    }

    [Fact]
    public void ReorderTrack_AudioWithinZone_PreservesClipsAndIsUndoable()
    {
        var session = ProjectSession.CreateNew();
        var videoOne = session.ActiveTimeline.Tracks[0];
        var videoTwo = session.ActiveTimeline.Tracks[1];
        var audioOne = session.ActiveTimeline.Tracks[2];
        var audioTwo = session.ActiveTimeline.Tracks[3];
        Assert.True(EditCommands.AddClip(session, 2,
            new Clip { Id = "AUD", MediaRef = "a", StartFrame = 4, DurationFrames = 12, MediaType = ClipType.audio }).Ok);

        // Swap A1 below A0 within the audio zone (targetIndex 3).
        Assert.True(EditCommands.ReorderTrack(session, audioOne.Id, targetIndex: 3).Ok);
        Assert.Equal([videoOne.Id, videoTwo.Id, audioTwo.Id, audioOne.Id],
            session.ActiveTimeline.Tracks.Select(track => track.Id));
        Assert.Contains(session.ActiveTimeline.Tracks[3].Clips, clip => clip.Id == "AUD");

        session.Undo();
        Assert.Equal([videoOne.Id, videoTwo.Id, audioOne.Id, audioTwo.Id],
            session.ActiveTimeline.Tracks.Select(track => track.Id));
        Assert.Contains(session.ActiveTimeline.Tracks[2].Clips, clip => clip.Id == "AUD");
    }

    [Fact]
    public void ReorderTrack_RejectsOutOfRangeTargetIndex()
    {
        var session = ProjectSession.CreateNew();
        var trackId = session.ActiveTimeline.Tracks[0].Id;
        Assert.False(EditCommands.ReorderTrack(session, trackId, targetIndex: -1).Ok);
        Assert.False(EditCommands.ReorderTrack(session, trackId, targetIndex: session.ActiveTimeline.Tracks.Count).Ok);
        Assert.False(session.CanUndo());
    }

    [Fact]
    public void TrackDisplayHeight_ClampsOnAssignAndJsonRoundTrip()
    {
        var session = ProjectSession.CreateNew();
        var track = session.ActiveTimeline.Tracks[0];
        track.DisplayHeight = 999;
        Assert.Equal(TimelineGeometry.MaxTrackHeight, track.DisplayHeight);
        track.DisplayHeight = 10;
        Assert.Equal(TimelineGeometry.MinTrackHeight, track.DisplayHeight);

        var roundTripped = ProjectFile.Decode(session.ProjectFile.Encode());
        var loaded = roundTripped.Timelines.Single().Tracks.Single(candidate => candidate.Id == track.Id);
        Assert.Equal(TimelineGeometry.MinTrackHeight, loaded.DisplayHeight);
    }

    [Fact]
    public void UndoRedo_RestoresAndReapplies()
    {
        var session = ProjectSession.CreateNew();
        Assert.False(session.CanUndo());
        Assert.False(session.CanRedo());

        var clip = new Clip
        {
            Id = "C1",
            MediaRef = "M1",
            StartFrame = 0,
            DurationFrames = 100,
        };

        // 1. Add clip
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);
        Assert.True(session.CanUndo());
        Assert.False(session.CanRedo());
        Assert.Single(session.ActiveTimeline.Tracks[0].Clips);

        // 2. Split clip
        Assert.True(EditCommands.SplitClip(session, "C1", 40).Ok);
        Assert.True(session.CanUndo());
        Assert.False(session.CanRedo());
        Assert.Equal(2, session.ActiveTimeline.Tracks[0].Clips.Count);

        // 3. Undo split
        session.Undo();
        Assert.True(session.CanUndo());
        Assert.True(session.CanRedo());
        Assert.Single(session.ActiveTimeline.Tracks[0].Clips);
        Assert.Equal(100, session.ActiveTimeline.Tracks[0].Clips[0].DurationFrames);

        // 4. Undo add
        session.Undo();
        Assert.False(session.CanUndo());
        Assert.True(session.CanRedo());
        Assert.Empty(session.ActiveTimeline.Tracks[0].Clips);

        // 5. Redo add
        session.Redo();
        Assert.True(session.CanUndo());
        Assert.True(session.CanRedo());
        Assert.Single(session.ActiveTimeline.Tracks[0].Clips);
        Assert.Equal(100, session.ActiveTimeline.Tracks[0].Clips[0].DurationFrames);

        // 6. Redo split
        session.Redo();
        Assert.True(session.CanUndo());
        Assert.False(session.CanRedo());
        Assert.Equal(2, session.ActiveTimeline.Tracks[0].Clips.Count);
    }

    [Fact]
    public void UndoRedo_StackDepth()
    {
        var session = ProjectSession.CreateNew();
        session.MaxStackDepth = 3;

        // Add 5 markers
        for (int i = 0; i < 5; i++)
        {
            Assert.True(EditCommands.AddMarker(session, i * 10, $"Marker {i}").Ok);
        }

        Assert.Equal(5, session.ActiveTimeline.Markers.Count);
        Assert.True(session.CanUndo());

        // We should be able to undo exactly 3 times (MaxStackDepth is 3)
        session.Undo(); // undo Marker 4 -> 4 markers remain
        Assert.Equal(4, session.ActiveTimeline.Markers.Count);

        session.Undo(); // undo Marker 3 -> 3 markers remain
        Assert.Equal(3, session.ActiveTimeline.Markers.Count);

        session.Undo(); // undo Marker 2 -> 2 markers remain
        Assert.Equal(2, session.ActiveTimeline.Markers.Count);

        // Undo stack should now be empty because oldest undos were pruned
        Assert.False(session.CanUndo());
    }

    [Fact]
    public void UndoRedo_AfterSave()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "temno-save-undo-" + Guid.NewGuid().ToString("N") + ".palmier");
        try
        {
            var session = ProjectSession.CreateNew();
            Assert.True(EditCommands.AddMarker(session, 10, "MarkerBeforeSave").Ok);
            Assert.True(session.IsDirty);
            Assert.True(session.CanUndo());

            session.Save(dir);
            Assert.False(session.IsDirty);
            Assert.True(session.CanUndo()); // Still can undo after save

            session.Undo();
            Assert.Empty(session.ActiveTimeline.Markers);
            Assert.True(session.IsDirty); // Becomes dirty again after undo
            Assert.True(session.CanRedo());

            session.Redo();
            Assert.Single(session.ActiveTimeline.Markers);
            Assert.False(session.IsDirty);
        }
        finally
        {
            if (System.IO.Directory.Exists(dir))
                System.IO.Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Undo_OnlyEditClearsDirtyWhenMatchingCreateBaseline()
    {
        var session = ProjectSession.CreateNew();
        Assert.False(session.IsDirty);
        Assert.True(EditCommands.AddMarker(session, 10, "One").Ok);
        Assert.True(session.IsDirty);

        session.Undo();
        Assert.Empty(session.ActiveTimeline.Markers);
        Assert.False(session.IsDirty);
        Assert.True(session.CanRedo());

        session.Redo();
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void SetKeyframes_ShouldApplyAndModifyTracks()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip { Id = "clip1", MediaRef = "M1", StartFrame = 0, DurationFrames = 100, MediaType = ClipType.video };
        EditCommands.AddClip(session, 0, clip);

        var getClip = () => session.ActiveTimeline.Tracks[0].Clips[0];

        // 1. Set opacity keyframes: [[0, 1.0, "linear"], [50, 0.5, "smooth"], [100, 0.0, "hold"]]
        var opacityRows = new List<object>
        {
            new List<object> { 0, 1.0, "linear" },
            new List<object> { 50, 0.5, "smooth" },
            new List<object> { 100, 0.0, "hold" }
        };
        var res = EditCommands.SetKeyframes(session, "clip1", "opacity", opacityRows);
        Assert.True(res.Ok, res.Error);

        var opacityTrack = Assert.IsType<KeyframeTrack<double>>(getClip().OpacityTrack);
        Assert.Equal(3, opacityTrack.Keyframes.Count);
        Assert.Equal(0, opacityTrack.Keyframes[0].Frame);
        Assert.Equal(1.0, opacityTrack.Keyframes[0].Value);
        Assert.Equal(Interpolation.linear, opacityTrack.Keyframes[0].InterpolationOut);

        // 2. Volume track
        var volumeRows = new List<object>
        {
            new List<object> { 10, -6.0, "smooth" }
        };
        Assert.True(EditCommands.SetKeyframes(session, "clip1", "volume", volumeRows).Ok);
        var volumeTrack = Assert.IsType<KeyframeTrack<double>>(getClip().VolumeTrack);
        Assert.Single(volumeTrack.Keyframes);

        // 3. Clear keyframes
        Assert.True(EditCommands.SetKeyframes(session, "clip1", "opacity", new List<object>()).Ok);
        Assert.Null(getClip().OpacityTrack);

        // 4. Undo restores
        session.Undo(); // undo clear opacity
        Assert.NotNull(getClip().OpacityTrack);

        session.Undo(); // undo set volume
        Assert.Null(getClip().VolumeTrack);

        session.Undo(); // undo set opacity
        Assert.Null(getClip().OpacityTrack);
    }

    [Fact]
    public void ApplyEffect_ShouldValidateAndMutateStack()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip { Id = "clip1", MediaRef = "M1", StartFrame = 0, DurationFrames = 100, MediaType = ClipType.video };
        EditCommands.AddClip(session, 0, clip);

        var getClip = () => session.ActiveTimeline.Tracks[0].Clips[0];
        var getEffects = () => Assert.IsType<List<Effect>>(getClip().Effects);

        // 1. Block color grades
        var colorApply = EditCommands.ApplyEffect(session, new[] { "clip1" }, new[] { new EffectEntry { Type = "color.exposure" } }, null);
        Assert.False(colorApply.Ok);
        Assert.Contains("is a color grade — use apply_color", colorApply.Error);

        // 2. Reject unknown effect
        var unknownApply = EditCommands.ApplyEffect(session, new[] { "clip1" }, new[] { new EffectEntry { Type = "blur.nonexistent" } }, null);
        Assert.False(unknownApply.Ok);
        Assert.Contains("Unknown effect", unknownApply.Error);

        // 3. Reject invalid parameter name
        var invalidParam = EditCommands.ApplyEffect(session, new[] { "clip1" }, new[] { new EffectEntry { Type = "blur.gaussian", Params = new() { { "bad_param", 10 } } } }, null);
        Assert.False(invalidParam.Ok);
        Assert.Contains("unknown param(s)", invalidParam.Error);

        // 4. Successful non-color effect application with clamping
        var validApply = EditCommands.ApplyEffect(session, new[] { "clip1" }, new[]
        {
            new EffectEntry { Type = "blur.gaussian", Params = new() { { "radius", 150.0 } } }, // max is 100
            new EffectEntry { Type = "stylize.grain", Params = new() { { "amount", 0.5 } } }
        }, null);
        Assert.True(validApply.Ok, validApply.Error);

        Assert.Equal(2, getEffects().Count);
        
        // Canonical order: blur.gaussian comes before stylize.grain in EffectRegistry.CanonicalOrder
        Assert.Equal("blur.gaussian", getEffects()[0].Type);
        Assert.Equal("stylize.grain", getEffects()[1].Type);

        // Check clamping
        Assert.Equal(100.0, getEffects()[0].Params["radius"].Value);
        Assert.Equal(0.5, getEffects()[1].Params["amount"].Value);

        // 5. Update existing effect in stack
        Assert.True(EditCommands.ApplyEffect(session, new[] { "clip1" }, new[]
        {
            new EffectEntry { Type = "blur.gaussian", Params = new() { { "radius", 25.0 } }, Enabled = false }
        }, null).Ok);

        Assert.Equal(2, getEffects().Count);
        Assert.False(getEffects()[0].Enabled);
        Assert.Equal(25.0, getEffects()[0].Params["radius"].Value);

        // 6. Remove effect
        Assert.True(EditCommands.ApplyEffect(session, new[] { "clip1" }, null, new[] { "blur.gaussian" }).Ok);
        Assert.Single(getEffects());
        Assert.Equal("stylize.grain", getEffects()[0].Type);

        // 7. Undo/Redo works
        session.Undo(); // undo remove
        Assert.Equal(2, getEffects().Count);
        Assert.Equal("blur.gaussian", getEffects()[0].Type);

        session.Undo(); // undo update
        Assert.Equal(100.0, getEffects()[0].Params["radius"].Value);
        Assert.True(getEffects()[0].Enabled);
    }

    [Fact]
    public void ApplyColor_ShouldProcessAllOptions()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip { Id = "clip1", MediaRef = "M1", StartFrame = 0, DurationFrames = 100, MediaType = ClipType.video };
        EditCommands.AddClip(session, 0, clip);

        var getClip = () => session.ActiveTimeline.Tracks[0].Clips[0];
        var getEffects = () => Assert.IsType<List<Effect>>(getClip().Effects);

        // 1. Paste knobs
        var colorRes = EditCommands.ApplyColor(session, new[] { "clip1" }, new ColorArgs
        {
            Exposure = 1.5,
            Contrast = 1.1,
            ShadowsHue = 180.0,
            ShadowsAmount = 0.5
        });
        Assert.True(colorRes.Ok, colorRes.Error);

        // We expect color.exposure, color.contrast, and color.wheels
        Assert.Equal(3, getEffects().Count);
        Assert.Equal("color.exposure", getEffects()[0].Type);
        Assert.Equal(1.5, getEffects()[0].Params["ev"].Value);
        Assert.Equal("color.contrast", getEffects()[1].Type);
        Assert.Equal(1.1, getEffects()[1].Params["amount"].Value);
        Assert.Equal("color.wheels", getEffects()[2].Type);
        
        // 2. Reset clears color effects
        Assert.True(EditCommands.ApplyColor(session, new[] { "clip1" }, new ColorArgs { Reset = true }).Ok);
        Assert.Null(getClip().Effects);

        // 3. Paste complete color dictionary
        var colorDict = new Dictionary<string, object>
        {
            { "exposure", 0.8 },
            { "contrast", 1.25 },
            { "masterCurve", new List<List<double>> { new() { 0, 0 }, new() { 0.5, 0.45 }, new() { 1, 1 } } }
        };

        Assert.True(EditCommands.ApplyColor(session, new[] { "clip1" }, new ColorArgs { Color = colorDict }).Ok);
        Assert.Equal(3, getEffects().Count);
        Assert.Equal("color.exposure", getEffects()[0].Type);
        Assert.Equal(0.8, getEffects()[0].Params["ev"].Value);
        Assert.Equal("color.curves", getEffects()[2].Type);

        // 4. Undo/redo restores
        session.Undo(); // undo paste dict
        Assert.Null(getClip().Effects);

        session.Undo(); // undo reset
        Assert.Equal(3, getEffects().Count);
        Assert.Equal("color.exposure", getEffects()[0].Type);
        Assert.Equal(1.5, getEffects()[0].Params["ev"].Value);
    }

    [Fact]
    public void Transitions_ShouldCRUD()
    {
        var session = ProjectSession.CreateNew();
        
        // Setup adjacent clips on video track 0
        var clipA = new Clip { Id = "clipA", MediaRef = "M1", StartFrame = 0, DurationFrames = 50, MediaType = ClipType.video };
        var clipB = new Clip { Id = "clipB", MediaRef = "M1", StartFrame = 50, DurationFrames = 50, MediaType = ClipType.video };
        EditCommands.AddClip(session, 0, clipA);
        EditCommands.AddClip(session, 0, clipB);

        var getTimeline = () => session.ActiveTimeline;

        // 1. Add transition between clipA and clipB
        var res = EditCommands.AddTransition(session, "clipA", "clipB", "crossDissolve", 10);
        Assert.True(res.Ok, res.Error);

        Assert.Single(getTimeline().Transitions);
        Assert.Equal("clipA", getTimeline().Transitions[0].ClipIdA);
        Assert.Equal("clipB", getTimeline().Transitions[0].ClipIdB);
        Assert.Equal(10, getTimeline().Transitions[0].DurationFrames);

        // Verify resolution
        var resolved = getTimeline().ResolvedTransitions;
        Assert.Single(resolved);
        Assert.Equal(10, resolved[0].Frames);
        Assert.Equal(0, resolved[0].TrackIndex);

        // 2. Reject non-adjacent clips
        var clipC = new Clip { Id = "clipC", MediaRef = "M1", StartFrame = 120, DurationFrames = 30, MediaType = ClipType.video };
        EditCommands.AddClip(session, 0, clipC);
        var badRes = EditCommands.AddTransition(session, "clipB", "clipC", "crossDissolve", 10);
        Assert.False(badRes.Ok);

        // 3. List Transitions
        var list = EditCommands.ListTransitions(session);
        Assert.Single(list);

        // 4. Remove transition
        var transId = getTimeline().Transitions[0].Id;
        Assert.True(EditCommands.RemoveTransition(session, transId).Ok);
        Assert.Empty(getTimeline().Transitions);

        // 5. Undo restores transition
        session.Undo();
        Assert.Single(getTimeline().Transitions);
        Assert.Equal(transId, getTimeline().Transitions[0].Id);
    }

    [Fact]
    public void RippleDeleteRanges_MidClipHole_SplitsAndOneUndoRestores()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0,
            new Clip { Id = "C1", MediaRef = "M", StartFrame = 0, DurationFrames = 100 }).Ok);

        Assert.True(EditCommands.RippleDeleteRanges(session, 0, [(40, 50)]).Ok);

        var clips = session.ActiveTimeline.Tracks[0].Clips.OrderBy(c => c.StartFrame).ToList();
        Assert.Equal(2, clips.Count);
        Assert.Equal(0, clips[0].StartFrame);
        Assert.Equal(40, clips[0].DurationFrames);
        Assert.Equal(0, clips[0].TrimStartFrame);
        Assert.Equal(40, clips[1].StartFrame);
        Assert.Equal(50, clips[1].DurationFrames);
        Assert.Equal(50, clips[1].TrimStartFrame);

        session.Undo();
        var restored = Assert.Single(session.ActiveTimeline.Tracks[0].Clips);
        Assert.Equal(100, restored.DurationFrames);
        Assert.Equal(0, restored.TrimStartFrame);
    }

    [Fact]
    public void RippleDeleteRanges_LinkedTracks_OneUndo()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClips(session,
        [
            (0, new Clip { Id = "V", MediaRef = "m", StartFrame = 0, DurationFrames = 80, MediaType = ClipType.video }),
            (2, new Clip { Id = "A", MediaRef = "m", StartFrame = 0, DurationFrames = 80, MediaType = ClipType.audio }),
        ]).Ok);
        Assert.True(EditCommands.LinkClips(session, ["V", "A"]).Ok);

        Assert.True(EditCommands.RippleDeleteRanges(session, [0, 2], [(10, 20)]).Ok);

        foreach (var trackIndex in new[] { 0, 2 })
        {
            var clips = session.ActiveTimeline.Tracks[trackIndex].Clips.OrderBy(c => c.StartFrame).ToList();
            Assert.Equal(2, clips.Count);
            Assert.Equal(10, clips[0].DurationFrames);
            Assert.Equal(10, clips[1].StartFrame);
            Assert.Equal(60, clips[1].DurationFrames);
        }

        session.Undo();
        Assert.Equal(80, session.ActiveTimeline.Tracks[0].Clips.Single().DurationFrames);
        Assert.Equal(80, session.ActiveTimeline.Tracks[2].Clips.Single().DurationFrames);
    }

    [Fact]
    public void RemoveClips_ExpandsLinkGroup()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClips(session,
        [
            (0, new Clip { Id = "V", MediaRef = "m", StartFrame = 0, DurationFrames = 20, MediaType = ClipType.video }),
            (2, new Clip { Id = "A", MediaRef = "m", StartFrame = 0, DurationFrames = 20, MediaType = ClipType.audio }),
        ]).Ok);
        Assert.True(EditCommands.LinkClips(session, ["V", "A"]).Ok);

        Assert.True(EditCommands.RemoveClips(session, ["V"], ripple: false).Ok);
        Assert.Empty(session.ActiveTimeline.Tracks[0].Clips);
        Assert.Empty(session.ActiveTimeline.Tracks[2].Clips);
    }

    [Fact]
    public void SplitClip_DeepCopiesTransformAndKeyframeTracks()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip
        {
            Id = "C1",
            MediaRef = "M1",
            StartFrame = 0,
            DurationFrames = 100,
            Transform = new Transform { Width = 0.8, Height = 0.8, Rotation = 12 },
            Crop = new Crop { Left = 0.1 },
            OpacityTrack = new KeyframeTrack<double>
            {
                Keyframes =
                [
                    new Keyframe<double> { Frame = 0, Value = 1 },
                    new Keyframe<double> { Frame = 50, Value = 0.25 },
                ]
            },
            VolumeTrack = new KeyframeTrack<double>
            {
                Keyframes = [new Keyframe<double> { Frame = 0, Value = 0.9 }]
            },
        };
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);
        Assert.True(EditCommands.SplitClip(session, "C1", 40).Ok);

        var left = session.ActiveTimeline.Tracks[0].Clips[0];
        var right = session.ActiveTimeline.Tracks[0].Clips[1];
        Assert.NotSame(left.Transform, right.Transform);
        Assert.NotSame(left.Crop, right.Crop);
        Assert.NotSame(left.OpacityTrack, right.OpacityTrack);
        Assert.NotSame(left.VolumeTrack, right.VolumeTrack);
        Assert.Equal(0.8, left.Transform.Width);
        Assert.Equal(0.1, left.Crop.Left);

        right.Transform.Width = 1.5;
        right.Crop.Left = 0.4;
        right.OpacityTrack!.Keyframes[0].Value = 0;
        right.VolumeTrack!.Keyframes[0].Value = 0.1;

        Assert.Equal(0.8, left.Transform.Width);
        Assert.Equal(0.1, left.Crop.Left);
        Assert.Equal(1, left.OpacityTrack!.Keyframes[0].Value);
        Assert.Equal(0.9, left.VolumeTrack!.Keyframes[0].Value);
    }

    [Fact]
    public void SplitClip_SplitsLinkedPartnersAndRegroupsRights()
    {
        var session = ProjectSession.CreateNew();
        var group = "lg";
        Assert.True(EditCommands.AddClips(session,
        [
            (0, new Clip { Id = "V", MediaRef = "m", StartFrame = 0, DurationFrames = 100, MediaType = ClipType.video, LinkGroupId = group }),
            (2, new Clip { Id = "A", MediaRef = "m", StartFrame = 0, DurationFrames = 100, MediaType = ClipType.audio, LinkGroupId = group }),
        ]).Ok);

        Assert.True(EditCommands.SplitClip(session, "V", 40).Ok);
        var video = session.ActiveTimeline.Tracks[0].Clips.OrderBy(c => c.StartFrame).ToList();
        var audio = session.ActiveTimeline.Tracks[2].Clips.OrderBy(c => c.StartFrame).ToList();
        Assert.Equal(2, video.Count);
        Assert.Equal(2, audio.Count);
        Assert.Equal(40, video[0].DurationFrames);
        Assert.Equal(40, audio[0].DurationFrames);
        Assert.Equal(group, video[0].LinkGroupId);
        Assert.Equal(group, audio[0].LinkGroupId);
        Assert.Equal(video[1].LinkGroupId, audio[1].LinkGroupId);
        Assert.NotEqual(group, video[1].LinkGroupId);
        Assert.False(string.IsNullOrEmpty(video[1].LinkGroupId));
    }

    [Fact]
    public void AddClip_RejectsIncompatibleTrackType()
    {
        var session = ProjectSession.CreateNew();
        var audioOnVideo = EditCommands.AddClip(session, 0, new Clip
        {
            MediaRef = "m",
            MediaType = ClipType.audio,
            StartFrame = 0,
            DurationFrames = 10,
        });
        Assert.False(audioOnVideo.Ok);
        var videoOnAudio = EditCommands.AddClip(session, 2, new Clip
        {
            MediaRef = "m",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 10,
        });
        Assert.False(videoOnAudio.Ok);
    }

    [Fact]
    public void AddClip_RipplePushesLaterClipsOnTheTrack()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "A",
            MediaRef = "m",
            StartFrame = 0,
            DurationFrames = 10,
        }).Ok);
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "B",
            MediaRef = "m",
            StartFrame = 0,
            DurationFrames = 5,
        }, overwrite: false, ripple: true).Ok);

        var clips = session.ActiveTimeline.Tracks[0].Clips.OrderBy(c => c.StartFrame).ToList();
        Assert.Equal(2, clips.Count);
        Assert.Equal("B", clips[0].Id);
        Assert.Equal(0, clips[0].StartFrame);
        Assert.Equal("A", clips[1].Id);
        Assert.Equal(5, clips[1].StartFrame);
    }

    [Fact]
    public void NearestCompatibleTrack_SnapsAudioDropOffAVideoLane()
    {
        var session = ProjectSession.CreateNew();
        Assert.Equal(2, EditCommands.NearestCompatibleTrack(session.ActiveTimeline, 0, ClipType.audio));
        Assert.Equal(1, EditCommands.NearestCompatibleTrack(session.ActiveTimeline, 2, ClipType.video));
    }
}
