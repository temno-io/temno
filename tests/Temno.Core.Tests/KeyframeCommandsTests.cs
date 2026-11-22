using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public class KeyframeCommandsTests
{
    [Fact]
    public void StampOpacity_AddsKeyframe_Undoable()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 10, DurationFrames = 50, Opacity = 0.8,
        }).Ok);

        var stamp = KeyframeCommands.StampOpacity(session, "c1", timelineFrame: 20);
        Assert.True(stamp.Ok);
        var clip = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.NotNull(clip.OpacityTrack);
        Assert.Single(clip.OpacityTrack!.Keyframes);
        Assert.Equal(10, clip.OpacityTrack.Keyframes[0].Frame); // 20 - 10 start
        Assert.Equal(0.8, clip.OpacityTrack.Keyframes[0].Value);

        session.Undo();
        Assert.Null(session.ActiveTimeline.Tracks[0].Clips[0].OpacityTrack);
    }

    [Fact]
    public void ToggleStamp_RemovesExisting()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 30, Opacity = 1,
        }).Ok);
        Assert.True(KeyframeCommands.StampOpacity(session, "c1", 5).Ok);
        Assert.True(KeyframeCommands.ToggleStampOpacity(session, "c1", 5).Ok);
        Assert.Null(session.ActiveTimeline.Tracks[0].Clips[0].OpacityTrack);
    }

    [Fact]
    public void PrevNext_Navigate()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 100, Opacity = 1,
        }).Ok);
        Assert.True(KeyframeCommands.StampOpacity(session, "c1", 10).Ok);
        Assert.True(KeyframeCommands.StampOpacity(session, "c1", 40).Ok);
        var clip = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.Equal(10, KeyframeCommands.PreviousTimelineFrame(clip, "opacity", 40));
        Assert.Equal(40, KeyframeCommands.NextTimelineFrame(clip, "opacity", 10));
        Assert.Null(KeyframeCommands.PreviousTimelineFrame(clip, "opacity", 10));
    }

    [Fact]
    public void OutsideClip_Fails()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 50, DurationFrames = 10,
        }).Ok);
        var r = KeyframeCommands.StampOpacity(session, "c1", 0);
        Assert.False(r.Ok);
    }

    [Fact]
    public void StampRotation_Toggle_Undoable()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 40,
            Transform = new Transform { Rotation = 45 },
        }).Ok);

        Assert.True(KeyframeCommands.StampRotation(session, "c1", 10).Ok);
        var clip = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.NotNull(clip.RotationTrack);
        Assert.Single(clip.RotationTrack!.Keyframes);
        Assert.Equal(10, clip.RotationTrack.Keyframes[0].Frame);
        Assert.Equal(45, clip.RotationTrack.Keyframes[0].Value);
        Assert.True(KeyframeCommands.HasKeyframeAt(clip, "rotation", 10));
        Assert.Equal(1, KeyframeCommands.KeyframeCount(clip, "rotation"));

        Assert.True(KeyframeCommands.ToggleStampRotation(session, "c1", 10).Ok);
        Assert.Null(session.ActiveTimeline.Tracks[0].Clips[0].RotationTrack);

        Assert.True(KeyframeCommands.Stamp(session, "c1", "rotation", 15).Ok);
        session.Undo();
        Assert.Null(session.ActiveTimeline.Tracks[0].Clips[0].RotationTrack);
    }

    [Fact]
    public void StampScale_FromTransformSize_Toggle_Undoable()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 5, DurationFrames = 50,
            Transform = new Transform { Width = 0.8, Height = 0.6 },
        }).Ok);

        Assert.True(KeyframeCommands.StampScale(session, "c1", 15).Ok);
        var clip = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.NotNull(clip.ScaleTrack);
        Assert.Single(clip.ScaleTrack!.Keyframes);
        Assert.Equal(10, clip.ScaleTrack.Keyframes[0].Frame); // 15 - 5
        Assert.Equal(0.8, clip.ScaleTrack.Keyframes[0].Value.A);
        Assert.Equal(0.6, clip.ScaleTrack.Keyframes[0].Value.B);

        Assert.True(KeyframeCommands.HasKeyframeAt(clip, "scale", 15));
        Assert.Equal(1, KeyframeCommands.KeyframeCount(clip, "scale"));

        Assert.True(KeyframeCommands.ToggleStampScale(session, "c1", 15).Ok);
        Assert.Null(session.ActiveTimeline.Tracks[0].Clips[0].ScaleTrack);

        Assert.True(KeyframeCommands.Stamp(session, "c1", "scale", 20).Ok);
        session.Undo();
        Assert.Null(session.ActiveTimeline.Tracks[0].Clips[0].ScaleTrack);
    }

    [Fact]
    public void StampPosition_FromTopLeft_Toggle_Undoable()
    {
        var session = ProjectSession.CreateNew();
        // Center 0.5,0.5 size 1×1 → top-left 0,0
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 40,
            Transform = new Transform
            {
                CenterX = 0.6, CenterY = 0.4,
                Width = 0.5, Height = 0.5,
            },
        }).Ok);

        Assert.True(KeyframeCommands.StampPosition(session, "c1", 8).Ok);
        var clip = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.NotNull(clip.PositionTrack);
        Assert.Single(clip.PositionTrack!.Keyframes);
        Assert.Equal(8, clip.PositionTrack.Keyframes[0].Frame);
        // top-left = center - size/2
        Assert.Equal(0.6 - 0.25, clip.PositionTrack.Keyframes[0].Value.A, 6);
        Assert.Equal(0.4 - 0.25, clip.PositionTrack.Keyframes[0].Value.B, 6);

        Assert.True(KeyframeCommands.HasKeyframeAt(clip, "position", 8));
        Assert.True(KeyframeCommands.ToggleStampPosition(session, "c1", 8).Ok);
        Assert.Null(session.ActiveTimeline.Tracks[0].Clips[0].PositionTrack);

        Assert.True(KeyframeCommands.Stamp(session, "c1", "position", 12).Ok);
        session.Undo();
        Assert.Null(session.ActiveTimeline.Tracks[0].Clips[0].PositionTrack);
    }

    [Fact]
    public void StampCrop_Toggle_Undoable()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 30,
            Crop = new Crop { Left = 0.1, Top = 0.2, Right = 0.05, Bottom = 0.15 },
        }).Ok);

        Assert.True(KeyframeCommands.StampCrop(session, "c1", 5).Ok);
        var clip = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.NotNull(clip.CropTrack);
        Assert.Single(clip.CropTrack!.Keyframes);
        Assert.Equal(5, clip.CropTrack.Keyframes[0].Frame);
        Assert.Equal(0.1, clip.CropTrack.Keyframes[0].Value.Left);
        Assert.Equal(0.2, clip.CropTrack.Keyframes[0].Value.Top);
        Assert.Equal(0.05, clip.CropTrack.Keyframes[0].Value.Right);
        Assert.Equal(0.15, clip.CropTrack.Keyframes[0].Value.Bottom);

        Assert.True(KeyframeCommands.HasKeyframeAt(clip, "crop", 5));
        Assert.Equal(1, KeyframeCommands.KeyframeCount(clip, "crop"));

        Assert.True(KeyframeCommands.ToggleStampCrop(session, "c1", 5).Ok);
        Assert.Null(session.ActiveTimeline.Tracks[0].Clips[0].CropTrack);

        Assert.True(KeyframeCommands.Stamp(session, "c1", "crop", 10).Ok);
        session.Undo();
        Assert.Null(session.ActiveTimeline.Tracks[0].Clips[0].CropTrack);
    }

    [Fact]
    public void PrevNext_WorksForScaleAndPosition()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 100,
            Transform = new Transform { Width = 1, Height = 1, CenterX = 0.5, CenterY = 0.5 },
        }).Ok);

        Assert.True(KeyframeCommands.StampScale(session, "c1", 10).Ok);
        Assert.True(KeyframeCommands.StampScale(session, "c1", 40).Ok);
        Assert.True(KeyframeCommands.StampPosition(session, "c1", 20).Ok);
        Assert.True(KeyframeCommands.StampPosition(session, "c1", 60).Ok);

        var clip = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.Equal(10, KeyframeCommands.PreviousTimelineFrame(clip, "scale", 40));
        Assert.Equal(40, KeyframeCommands.NextTimelineFrame(clip, "scale", 10));
        Assert.Equal(20, KeyframeCommands.PreviousTimelineFrame(clip, "position", 60));
        Assert.Equal(60, KeyframeCommands.NextTimelineFrame(clip, "position", 20));
    }

    [Fact]
    public void ToggleStamp_UnifiedApi()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 20,
            Transform = new Transform { Rotation = 12 },
        }).Ok);

        Assert.True(KeyframeCommands.ToggleStamp(session, "c1", "rotation", 4).Ok);
        Assert.True(KeyframeCommands.HasKeyframeAt(session.ActiveTimeline.Tracks[0].Clips[0], "rotation", 4));
        Assert.True(KeyframeCommands.ToggleStamp(session, "c1", "rotation", 4).Ok);
        Assert.Null(session.ActiveTimeline.Tracks[0].Clips[0].RotationTrack);
    }

    [Fact]
    public void UnknownProperty_Fails()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 10,
        }).Ok);
        Assert.False(KeyframeCommands.Stamp(session, "c1", "blur", 0).Ok);
        Assert.False(KeyframeCommands.ToggleStamp(session, "c1", "blur", 0).Ok);
    }
}
