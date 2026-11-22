using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public class ClipLiveEditTests
{
    [Fact]
    public void Commit_CreatesSingleUndoStep()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m1", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 90, Opacity = 1, Volume = 1,
        }).Ok);

        var live = new ClipLiveEdit();
        live.Begin(session);
        live.Mutate(() => session.ActiveTimeline.Tracks[0].Clips[0].Opacity = 0.5);
        live.End(keep: true);

        Assert.True(session.CanUndo());
        Assert.Equal(0.5, session.ActiveTimeline.Tracks[0].Clips[0].Opacity);
        session.Undo();
        Assert.Equal(1.0, session.ActiveTimeline.Tracks[0].Clips[0].Opacity);
    }

    [Fact]
    public void NoMutation_EndRollsBack_NoUndoPollution()
    {
        var session = ProjectSession.CreateNew();
        var before = session.CanUndo();
        var live = new ClipLiveEdit();
        live.Begin(session);
        live.End(keep: true);
        Assert.Equal(before, session.CanUndo());
    }

    [Fact]
    public void EndKeepFalse_RollsBackMutation()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m1", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 90, Opacity = 1, Volume = 1,
        }).Ok);

        var live = new ClipLiveEdit();
        live.Begin(session);
        live.Mutate(() => session.ActiveTimeline.Tracks[0].Clips[0].Opacity = 0.2);
        live.End(keep: false);
        Assert.Equal(1.0, session.ActiveTimeline.Tracks[0].Clips[0].Opacity);
    }

    [Fact]
    public void NestedBegin_Throws()
    {
        var session = ProjectSession.CreateNew();
        var live = new ClipLiveEdit();
        live.Begin(session);
        Assert.Throws<InvalidOperationException>(() => live.Begin(session));
        live.End(keep: false);
    }

    [Fact]
    public void ApplyClipPropertiesInPlace_SetsCropAndFades()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "c1", MediaRef = "m1", MediaType = ClipType.video,
            StartFrame = 0, DurationFrames = 90,
        }).Ok);
        session.RecordBeforeEdit();
        var result = EditCommands.ApplyClipPropertiesInPlace(
            session,
            ["c1"],
            durationFrames: null,
            trimStartFrame: null,
            trimEndFrame: null,
            speed: 1.5,
            volume: 0.5,
            opacity: null,
            transform: null,
            blendMode: "multiply",
            crop: new Crop { Left = 0.1, Top = 0.2, Right = 0.3, Bottom = 0.4 },
            fadeInFrames: 10,
            fadeOutFrames: 15);
        Assert.True(result.Ok);
        var clip = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.Equal(1.5, clip.Speed);
        Assert.Equal(0.5, clip.Volume);
        Assert.Equal("multiply", clip.BlendMode);
        Assert.Equal(0.1, clip.Crop.Left);
        Assert.Equal(10, clip.FadeInFrames);
        Assert.Equal(15, clip.FadeOutFrames);
    }
}
