using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Media;
using Temno.Media.Effects;
using Xunit;

namespace Temno.Core.Tests;

public sealed class KeyframeGraphAndCurveEditorTests
{
    [Fact]
    public void UpsertScalarKey_Opacity_AndSample()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 50, Opacity = 1,
        }).Ok);

        Assert.True(KeyframeCommands.UpsertScalarKey(session, "c1", "opacity", 0, 0).Ok);
        Assert.True(KeyframeCommands.UpsertScalarKey(session, "c1", "opacity", 40, 1).Ok);
        var clip = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.Equal(0, KeyframeCommands.SampleScalar(clip, "opacity", 0, 1), 3);
        Assert.Equal(1, KeyframeCommands.SampleScalar(clip, "opacity", 40, 0), 3);
        var mid = KeyframeCommands.SampleScalar(clip, "opacity", 20, 0);
        Assert.InRange(mid, 0.4, 0.6); // smoothstep mid
    }

    [Fact]
    public void MoveScalarKey_ChangesOffsetAndValue()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 100, Opacity = 1,
        }).Ok);
        Assert.True(KeyframeCommands.UpsertScalarKey(session, "c1", "opacity", 10, 0.2).Ok);
        Assert.True(KeyframeCommands.MoveScalarKey(session, "c1", "opacity", 10, 30, 0.8).Ok);
        var keys = KeyframeCommands.GetScalarKeys(session.ActiveTimeline.Tracks[0].Clips[0], "opacity");
        Assert.Single(keys);
        Assert.Equal(30, keys[0].Offset);
        Assert.Equal(0.8, keys[0].Value, 3);
    }

    [Fact]
    public void ScaleChannel_PreservesOtherComponent()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 30,
            Transform = new Transform { Width = 1, Height = 1 },
        }).Ok);
        Assert.True(KeyframeCommands.UpsertScalarKey(session, "c1", "scale.a", 5, 2.0).Ok);
        Assert.True(KeyframeCommands.UpsertScalarKey(session, "c1", "scale.b", 5, 0.5).Ok);
        var clip = session.ActiveTimeline.Tracks[0].Clips[0];
        var kf = clip.ScaleTrack!.Keyframes.Single(k => k.Frame == 5);
        Assert.Equal(2.0, kf.Value.A, 3);
        Assert.Equal(0.5, kf.Value.B, 3);
    }

    [Fact]
    public void MovingScaleChannel_MovesTheCoupledKeyWithoutLosingSibling()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 50,
            Transform = new Transform { Width = 1, Height = 1 },
        }).Ok);
        Assert.True(KeyframeCommands.UpsertScalarKey(session, "c1", "scale.a", 10, 2).Ok);
        Assert.True(KeyframeCommands.UpsertScalarKey(session, "c1", "scale.b", 10, 0.5).Ok);

        Assert.True(KeyframeCommands.MoveScalarKey(session, "c1", "scale.a", 10, 30, 1.5).Ok);

        var key = Assert.Single(session.ActiveTimeline.Tracks[0].Clips[0].ScaleTrack!.Keyframes);
        Assert.Equal(30, key.Frame);
        Assert.Equal(1.5, key.Value.A, 3);
        Assert.Equal(0.5, key.Value.B, 3);
    }

    [Fact]
    public void CropChannel_MovesOneEdgeAndPreservesTheOtherEdges()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 50,
            Crop = new Crop { Left = 0.1, Top = 0.2, Right = 0.3, Bottom = 0.4 },
        }).Ok);
        Assert.True(KeyframeCommands.UpsertScalarKey(session, "c1", "crop.left", 10, 0.15).Ok);
        Assert.True(KeyframeCommands.MoveScalarKey(session, "c1", "crop.left", 10, 30, 0.25).Ok);

        var key = Assert.Single(session.ActiveTimeline.Tracks[0].Clips[0].CropTrack!.Keyframes);
        Assert.Equal(30, key.Frame);
        Assert.Equal(0.25, key.Value.Left, 3);
        Assert.Equal(0.2, key.Value.Top, 3);
        Assert.Equal(0.3, key.Value.Right, 3);
        Assert.Equal(0.4, key.Value.Bottom, 3);
    }

    [Fact]
    public void PointCurveEditor_UpsertMoveRemove()
    {
        var pts = PointCurveEditor.Identity;
        pts = PointCurveEditor.Upsert(pts, 0.5, 0.25);
        Assert.Equal(3, pts.Count);
        pts = PointCurveEditor.Move(pts, 1, 0.4, 0.1);
        Assert.Contains(pts, p => Math.Abs(p.X - 0.4) < 0.01 && Math.Abs(p.Y - 0.1) < 0.01);
        pts = PointCurveEditor.RemoveAt(pts, 1);
        Assert.Equal(2, pts.Count);
    }

    [Fact]
    public void PointCurveEditor_KeepsBoundaryAnchorsAndInteriorOrder()
    {
        var pts = PointCurveEditor.Upsert(PointCurveEditor.Identity, 0.5, 0.25);
        var moved = PointCurveEditor.Move(pts, 1, 1, 0.75);

        Assert.Equal(0, moved[0].X, 3);
        Assert.Equal(1, moved[^1].X, 3);
        Assert.InRange(moved[1].X, 0.004, 0.996);
        Assert.Equal(3, PointCurveEditor.RemoveAt(moved, 0).Count);
    }

    [Fact]
    public void PerChannelCurves_RenderDifferentlyPerChannel()
    {
        var grade = new GradeCurve
        {
            Master = PointCurveEditor.Identity,
            Red = [new(0, 0), new(1, 0.2)], // crush reds
            Green = PointCurveEditor.Identity,
            Blue = PointCurveEditor.Identity,
        };
        var renderer = new CpuEffectsRenderer();
        using var source = new VideoFrame(1, 1, new byte[] { 200, 200, 200, 255 }, "rgba");
        var effects = new[]
        {
            new Effect
            {
                Type = "color.curves",
                Params = new() { ["curve"] = new EffectParam { String = grade.Encoded() } },
            },
        };
        using var rendered = renderer.Apply(source, effects, 0);
        Assert.True(rendered.Data[0] < rendered.Data[1]); // red crushed below green
    }

    [Fact]
    public void SetInterpolation_Hold()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 40, Opacity = 1,
        }).Ok);
        Assert.True(KeyframeCommands.UpsertScalarKey(session, "c1", "opacity", 0, 0, Interpolation.smooth).Ok);
        Assert.True(KeyframeCommands.UpsertScalarKey(session, "c1", "opacity", 20, 1, Interpolation.smooth).Ok);
        Assert.True(KeyframeCommands.SetInterpolation(session, "c1", "opacity", 0, Interpolation.hold).Ok);
        var keys = KeyframeCommands.GetScalarKeys(session.ActiveTimeline.Tracks[0].Clips[0], "opacity");
        Assert.Equal(Interpolation.hold, keys[0].InterpolationOut);
    }
}
