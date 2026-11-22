using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;
using static Temno.Core.Tests.TransitionBlocksTests;

namespace Temno.Core.Tests;

public class TransitionCommandsTests
{
    [Fact]
    public void AddFade_LinkPartnerFollows_UnlessAlt()
    {
        var session = ProjectSession.CreateNew();
        var timeline = session.ActiveTimeline;
        var v = Add(timeline, 0, "v", 0, 90, link: "pair");
        var a = Add(timeline, 2, "a", 0, 90, ClipType.audio, link: "pair");
        Add(timeline, 1, "other", 0, 90);

        var corner = TransitionBlocks.Corner(timeline, "v", FadeEdge.In)!.Value;
        Assert.True(TransitionCommands.Add(session, corner, 30, expandLinks: true, out var created).Ok);
        Assert.Equal(TransitionBlockRef.Fade(FadeEdge.In, "v"), created);
        Assert.Equal((30, 30), (v.FadeInFrames, a.FadeInFrames));
        Assert.Equal(0, ClipOf(session, "other").FadeInFrames);
        // Undo decodes the project again, so clips are looked up afresh from here on.
        session.Undo();
        Assert.Equal((0, 0), (ClipOf(session, "v").FadeInFrames, ClipOf(session, "a").FadeInFrames));
        Assert.False(session.CanUndo());

        corner = TransitionBlocks.Corner(session.ActiveTimeline, "v", FadeEdge.Out)!.Value;
        Assert.True(TransitionCommands.Add(session, corner, 12, expandLinks: false, out _).Ok);
        Assert.Equal((12, 0), (ClipOf(session, "v").FadeOutFrames, ClipOf(session, "a").FadeOutFrames));
    }

    [Fact]
    public void AddCut_ClearsTheFacingFades_InOneUndo()
    {
        var session = ProjectSession.CreateNew();
        var timeline = session.ActiveTimeline;
        Add(timeline, 0, "a", 0, 60, fadeIn: 6);
        Add(timeline, 0, "b", 60, 60, fadeOut: 9);
        var corner = TransitionBlocks.Corner(timeline, "b", FadeEdge.In)!.Value;

        Assert.True(TransitionCommands.Add(session, corner, 24, expandLinks: true, out var created).Ok);
        var transition = Assert.Single(session.ActiveTimeline.Transitions);
        Assert.Equal(TransitionBlockRef.Cut(transition.Id), created);
        Assert.Equal(("a", "b", TransitionType.CrossDissolve, 24), (transition.ClipIdA, transition.ClipIdB, transition.Type, transition.DurationFrames));
        Assert.Equal((6, 9), (ClipOf(session, "a").FadeInFrames, ClipOf(session, "b").FadeOutFrames));

        session.Undo();
        Assert.Empty(session.ActiveTimeline.Transitions);
        Assert.False(session.CanUndo());
    }

    [Fact]
    public void AddCut_RefusesATakenCut_AndClampsToTheRoom()
    {
        var session = ProjectSession.CreateNew();
        var timeline = session.ActiveTimeline;
        Add(timeline, 0, "a", 0, 10);
        Add(timeline, 0, "b", 10, 100);
        var corner = TransitionBlocks.Corner(timeline, "a", FadeEdge.Out)!.Value;

        Assert.True(TransitionCommands.Add(session, corner, 90, expandLinks: true, out _).Ok);
        Assert.Equal(10, Assert.Single(session.ActiveTimeline.Transitions).DurationFrames);

        corner = TransitionBlocks.Corner(session.ActiveTimeline, "b", FadeEdge.In)!.Value;
        Assert.False(TransitionCommands.Add(session, corner, 20, expandLinks: true, out var none).Ok);
        Assert.Null(none);
    }

    [Fact]
    public void AddDefault_IsOneSecondAtTheNearestFreeEdge()
    {
        var session = ProjectSession.CreateNew(fps: 25);
        var timeline = session.ActiveTimeline;
        Add(timeline, 0, "a", 0, 100);
        Add(timeline, 0, "b", 100, 100);

        Assert.True(TransitionCommands.AddDefault(session, null, 96, expandLinks: true, out var cut).Ok);
        var transition = Assert.Single(session.ActiveTimeline.Transitions);
        Assert.Equal((TransitionBlockKind.Cut, 25), (cut!.Value.Kind, transition.DurationFrames));

        Assert.True(TransitionCommands.AddDefault(session, "b", 190, expandLinks: true, out var fade).Ok);
        Assert.Equal(TransitionBlockRef.Fade(FadeEdge.Out, "b"), fade);
        Assert.Equal(25, ClipOf(session, "b").FadeOutFrames);

        Assert.True(TransitionCommands.AddDefault(session, "a", 0, expandLinks: true, out _).Ok);
        Assert.False(TransitionCommands.AddDefault(session, "a", 0, expandLinks: true, out var nothing).Ok);
        Assert.Null(nothing);
    }

    [Fact]
    public void SetFrames_ClampsToTheRoom_AndMovesOnlyMatchingPartners()
    {
        var session = ProjectSession.CreateNew();
        var timeline = session.ActiveTimeline;
        var v = Add(timeline, 0, "v", 0, 60, fadeIn: 10, fadeOut: 20, link: "pair");
        var matching = Add(timeline, 2, "a", 0, 60, ClipType.audio, fadeIn: 10, link: "pair");
        var tuned = Add(timeline, 3, "a2", 0, 60, ClipType.audio, fadeIn: 14, link: "pair");

        Assert.True(TransitionCommands.SetFrames(session, TransitionBlockRef.Fade(FadeEdge.In, "v"), 99, expandLinks: true).Ok);
        Assert.Equal((40, 60, 14), (v.FadeInFrames, matching.FadeInFrames, tuned.FadeInFrames));
        session.Undo();
        Assert.Equal((10, 10, 14), (ClipOf(session, "v").FadeInFrames, ClipOf(session, "a").FadeInFrames, ClipOf(session, "a2").FadeInFrames));

        Assert.True(TransitionCommands.SetFrames(session, TransitionBlockRef.Fade(FadeEdge.In, "v"), 10, expandLinks: true).Ok);
        Assert.False(session.CanUndo());
    }

    [Fact]
    public void SetFrames_OnACut_StaysWithinBothClips()
    {
        var session = ProjectSession.CreateNew();
        var timeline = session.ActiveTimeline;
        var a = Add(timeline, 0, "a", 0, 60, fadeIn: 50);
        var b = Add(timeline, 0, "b", 60, 60);
        timeline.Transitions.Add(new Transition { Id = "t", ClipIdA = a.Id, ClipIdB = b.Id, DurationFrames = 8 });

        Assert.True(TransitionCommands.SetFrames(session, TransitionBlockRef.Cut("t"), 40, expandLinks: true).Ok);
        Assert.Equal(21, timeline.Transitions[0].DurationFrames);
        Assert.True(TransitionCommands.SetFrames(session, TransitionBlockRef.Cut("t"), 0, expandLinks: true).Ok);
        Assert.Equal(TransitionBlocks.MinCutFrames, timeline.Transitions[0].DurationFrames);
    }

    [Fact]
    public void SetType_SetCurve_AndRemove_AreOneUndoEach()
    {
        var session = ProjectSession.CreateNew();
        var timeline = session.ActiveTimeline;
        var a = Add(timeline, 0, "a", 0, 60, fadeIn: 12, link: "pair");
        var partner = Add(timeline, 2, "sound", 0, 60, ClipType.audio, fadeIn: 12, link: "pair");
        var b = Add(timeline, 0, "b", 60, 60);
        timeline.Transitions.Add(new Transition { Id = "t", ClipIdA = a.Id, ClipIdB = b.Id, DurationFrames = 10 });
        var cut = TransitionBlockRef.Cut("t");
        var fade = TransitionBlockRef.Fade(FadeEdge.In, "a");

        Assert.True(TransitionCommands.SetType(session, cut, TransitionType.FadeToBlack).Ok);
        Assert.Equal(TransitionType.FadeToBlack, timeline.Transitions[0].Type);
        // A no-op rolls its record back, which decodes the project: look clips up afresh.
        Assert.True(TransitionCommands.SetType(session, cut, TransitionType.FadeToBlack).Ok);
        Assert.False(TransitionCommands.SetType(session, fade, TransitionType.Wipe).Ok);

        Assert.True(TransitionCommands.SetCurve(session, fade, Interpolation.smooth, expandLinks: true).Ok);
        Assert.Equal((Interpolation.smooth, Interpolation.smooth), (ClipOf(session, "a").FadeInInterpolation, ClipOf(session, "sound").FadeInInterpolation));
        Assert.False(TransitionCommands.SetCurve(session, cut, Interpolation.smooth, expandLinks: true).Ok);

        Assert.True(TransitionCommands.Remove(session, fade, expandLinks: true).Ok);
        Assert.Equal((0, 0), (ClipOf(session, "a").FadeInFrames, ClipOf(session, "sound").FadeInFrames));
        Assert.True(TransitionCommands.Remove(session, cut, expandLinks: true).Ok);
        Assert.Empty(session.ActiveTimeline.Transitions);

        // Four real edits, four undo steps; the repeated type left none.
        for (var i = 0; i < 4; i++) session.Undo();
        Assert.False(session.CanUndo());
        timeline = session.ActiveTimeline;
        Assert.Equal(TransitionType.CrossDissolve, Assert.Single(timeline.Transitions).Type);
        Assert.Equal(12, ClipOf(session, "a").FadeInFrames);
        Assert.Equal(Interpolation.linear, ClipOf(session, "sound").FadeInInterpolation);
    }

    [Fact]
    public void ResizeDrag_FollowsTheEdge_ClampsToOneFrame_AndLeavesOneUndo()
    {
        var session = ProjectSession.CreateNew();
        var timeline = session.ActiveTimeline;
        Add(timeline, 0, "c", 100, 60, fadeOut: 10);

        var drag = TransitionDrag.BeginResize(session, TransitionBlockRef.Fade(FadeEdge.Out, "c"), rightEdge: false, expandLinks: true)!;
        Assert.Equal(10, drag.Frames);
        Assert.Equal(150, drag.EdgeFrame);
        Assert.Equal(25, drag.DragTo(135));
        Assert.Equal(135, drag.PreviewFrame);
        Assert.Equal(1, drag.DragTo(400));
        Assert.Equal(60, drag.DragTo(-50));
        Assert.Equal(60, ClipOf(session, "c").FadeOutFrames);
        Assert.True(drag.Commit());

        session.Undo();
        Assert.Equal(10, ClipOf(session, "c").FadeOutFrames);
        Assert.False(session.CanUndo());

        var noop = TransitionDrag.BeginResize(session, TransitionBlockRef.Fade(FadeEdge.Out, "c"), rightEdge: false, expandLinks: true)!;
        noop.DragTo(130);
        noop.DragTo(150);
        Assert.False(noop.Commit());
        Assert.False(session.CanUndo());
    }

    [Fact]
    public void CreateDrag_OnACut_NeedsBothSidesFree_GrowsCentred_FromEitherSide_AndShrinksToNothing()
    {
        var session = ProjectSession.CreateNew();
        var timeline = session.ActiveTimeline;
        Add(timeline, 0, "a", 0, 60, fadeOut: 4);
        Add(timeline, 0, "b", 60, 60, fadeIn: 6);
        var corner = TransitionBlocks.Corner(timeline, "a", FadeEdge.Out)!.Value;
        Assert.Null(TransitionDrag.BeginCreate(session, corner, expandLinks: true));
        ClipOf(session, "a").FadeOutFrames = 0;
        ClipOf(session, "b").FadeInFrames = 0;

        var drag = TransitionDrag.BeginCreate(session, corner, expandLinks: true)!;
        Assert.Null(drag.Ref);
        Assert.Equal(0, drag.DragTo(60));
        Assert.Null(drag.Ref);
        Assert.Equal(10, drag.DragTo(55));
        Assert.False(drag.RightEdge);
        Assert.Equal(55, drag.EdgeFrame);
        var transition = Assert.Single(timeline.Transitions);
        Assert.Equal(drag.Ref, TransitionBlockRef.Cut(transition.Id));
        Assert.Equal(16, drag.DragTo(68));
        Assert.True(drag.RightEdge);
        Assert.Equal(68, drag.EdgeFrame);
        Assert.Equal(68, drag.PreviewFrame);
        Assert.True(drag.Commit());
        Assert.Equal(16, Assert.Single(session.ActiveTimeline.Transitions).DurationFrames);
        session.Undo();
        Assert.Empty(session.ActiveTimeline.Transitions);

        timeline = session.ActiveTimeline;
        ClipOf(session, "a").FadeOutFrames = 3;
        var again = TransitionDrag.BeginCreate(session, TransitionBlocks.Corner(timeline, "b", FadeEdge.In)!.Value, expandLinks: true);
        Assert.Null(again);
        ClipOf(session, "a").FadeOutFrames = 0;
        again = TransitionDrag.BeginCreate(session, TransitionBlocks.Corner(timeline, "b", FadeEdge.In)!.Value, expandLinks: true)!;
        again.DragTo(70);
        Assert.Single(timeline.Transitions);
        again.DragTo(60);
        Assert.Empty(timeline.Transitions);
        Assert.Null(again.Ref);
        Assert.Equal(0, ClipOf(session, "b").FadeInFrames);
        Assert.False(again.Commit());
        Assert.False(session.CanUndo());
    }

    [Fact]
    public void CreateDrag_OnAFreeEdge_MakesAFadeOnMatchingPartners_AndCancelReverts()
    {
        var session = ProjectSession.CreateNew();
        var timeline = session.ActiveTimeline;
        var v = Add(timeline, 0, "v", 30, 60, link: "pair");
        var a = Add(timeline, 2, "a", 30, 60, ClipType.audio, link: "pair");
        var corner = TransitionBlocks.Corner(timeline, "v", FadeEdge.In)!.Value;

        var drag = TransitionDrag.BeginCreate(session, corner, expandLinks: true)!;
        Assert.Equal(0, drag.DragTo(20));
        Assert.Equal(15, drag.DragTo(45));
        Assert.Equal((15, 15), (v.FadeInFrames, a.FadeInFrames));
        Assert.Equal(TransitionBlockRef.Fade(FadeEdge.In, "v"), drag.Ref);
        drag.Cancel();
        Assert.Equal((0, 0), (ClipOf(session, "v").FadeInFrames, ClipOf(session, "a").FadeInFrames));
        Assert.False(session.CanUndo());
    }

    [Fact]
    public void Drag_AnchorFrame_IsTheClipEdgeForAFade_AndTheCutForATransition()
    {
        // The timeline leaves this frame out of the snap targets: snapping there is a zero-length block.
        var session = ProjectSession.CreateNew();
        var timeline = session.ActiveTimeline;
        Add(timeline, 0, "a", 30, 60);
        Add(timeline, 0, "b", 90, 60);

        var fadeIn = TransitionDrag.BeginCreate(session, TransitionBlocks.Corner(timeline, "a", FadeEdge.In)!.Value, expandLinks: true)!;
        Assert.Equal(30, fadeIn.AnchorFrame);
        fadeIn.Cancel();

        var fadeOut = TransitionDrag.BeginCreate(session, TransitionBlocks.Corner(session.ActiveTimeline, "b", FadeEdge.Out)!.Value, expandLinks: true)!;
        Assert.Equal(150, fadeOut.AnchorFrame);
        fadeOut.Cancel();

        var cut = TransitionDrag.BeginCreate(session, TransitionBlocks.Corner(session.ActiveTimeline, "b", FadeEdge.In)!.Value, expandLinks: true)!;
        Assert.True(cut.IsCut);
        Assert.Equal(90, cut.AnchorFrame);
        cut.Cancel();
    }

    [Fact]
    public void ResizeDrag_OnACut_HoldsItsSide_AndStopsAtTheMinimum()
    {
        var session = ProjectSession.CreateNew();
        var timeline = session.ActiveTimeline;
        var a = Add(timeline, 0, "a", 0, 60);
        var b = Add(timeline, 0, "b", 60, 60);
        timeline.Transitions.Add(new Transition { Id = "t", ClipIdA = a.Id, ClipIdB = b.Id, DurationFrames = 20 });

        var drag = TransitionDrag.BeginResize(session, TransitionBlockRef.Cut("t"), rightEdge: true, expandLinks: true)!;
        Assert.Equal(70, drag.EdgeFrame);
        Assert.Equal(30, drag.DragTo(75));
        Assert.Equal(TransitionBlocks.MinCutFrames, drag.DragTo(40));
        Assert.True(drag.RightEdge);
        Assert.Equal(60, drag.DragTo(200));
        Assert.True(drag.Commit());
        Assert.Equal(60, session.ActiveTimeline.Transitions[0].DurationFrames);
    }

    [Fact]
    public void Split_KeepsTheFadeInLeft_TheFadeOutRight_AndTheTailTransition()
    {
        var session = ProjectSession.CreateNew();
        var timeline = session.ActiveTimeline;
        var a = Add(timeline, 0, "a", 0, 100, fadeIn: 10, fadeOut: 20);
        var b = Add(timeline, 0, "b", 100, 60);
        timeline.Transitions.Add(new Transition { Id = "t", ClipIdA = a.Id, ClipIdB = b.Id, DurationFrames = 10 });

        Assert.True(EditCommands.SplitClip(session, "a", 50).Ok);

        var clips = session.ActiveTimeline.Tracks[0].Clips.OrderBy(c => c.StartFrame).ToList();
        Assert.Equal(3, clips.Count);
        Assert.Equal((10, 0), (clips[0].FadeInFrames, clips[0].FadeOutFrames));
        Assert.Equal((0, 20), (clips[1].FadeInFrames, clips[1].FadeOutFrames));
        var rt = Assert.Single(session.ActiveTimeline.ResolvedTransitions);
        Assert.Same(clips[1], rt.ClipA);
        Assert.Same(clips[2], rt.ClipB);
    }

    private static Clip ClipOf(ProjectSession session, string id)
        => session.ActiveTimeline.Tracks.SelectMany(t => t.Clips).Single(c => c.Id == id);
}
