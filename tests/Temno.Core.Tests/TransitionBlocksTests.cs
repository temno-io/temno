using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public class TransitionBlocksTests
{
    [Fact]
    public void Blocks_AreTheFadesAndTheRenderedCutWindow()
    {
        var timeline = NewTimeline();
        var a = Add(timeline, 0, "a", 0, 60, fadeIn: 10);
        var b = Add(timeline, 0, "b", 60, 60);
        Add(timeline, 2, "music", 0, 120, ClipType.audio, fadeOut: 15);
        Add(timeline, 0, "text", 200, 40, ClipType.text, fadeIn: 5);
        timeline.Transitions.Add(new Transition { Id = "t", ClipIdA = a.Id, ClipIdB = b.Id, DurationFrames = 20 });

        var blocks = TransitionBlocks.ForTimeline(timeline);

        Assert.Collection(
            blocks.OrderBy(x => x.TrackIndex).ThenBy(x => x.StartFrame),
            x => Assert.Equal((TransitionBlockKind.FadeIn, 0, 10, "a"), (x.Kind, x.StartFrame, x.EndFrame, x.Clip.Id)),
            x => Assert.Equal((TransitionBlockKind.Cut, 50, 70, "a", "b"), (x.Kind, x.StartFrame, x.EndFrame, x.Clip.Id, x.ClipB!.Id)),
            x => Assert.Equal((TransitionBlockKind.FadeIn, 200, 205, "text"), (x.Kind, x.StartFrame, x.EndFrame, x.Clip.Id)),
            x => Assert.Equal((TransitionBlockKind.FadeOut, 105, 120, "music"), (x.Kind, x.StartFrame, x.EndFrame, x.Clip.Id)));
        Assert.Equal("Cross Dissolve", blocks.Single(x => x.IsCut).Name);
    }

    [Theory]
    [InlineData(7, 97, 104)]
    [InlineData(8, 96, 104)]
    [InlineData(1, 100, 101)]
    public void CutWindow_IsTheRenderersCentredWindow(int frames, int start, int end)
    {
        var timeline = NewTimeline();
        var a = Add(timeline, 0, "a", 40, 60);
        var b = Add(timeline, 0, "b", 100, 60);
        timeline.Transitions.Add(new Transition { ClipIdA = a.Id, ClipIdB = b.Id, DurationFrames = frames });

        var rt = Assert.Single(timeline.ResolvedTransitions);
        Assert.Equal((start, end), (rt.StartFrame, rt.EndFrame));
        Assert.True(rt.IsActiveAt(start));
        Assert.False(rt.IsActiveAt(end));
        var block = Assert.Single(TransitionBlocks.ForTimeline(timeline));
        Assert.Equal((start, end), (block.StartFrame, block.EndFrame));
    }

    [Fact]
    public void Corners_CreateCutsOnlyBetweenTouchingPictureClipsOnAVideoTrack()
    {
        var timeline = NewTimeline();
        Add(timeline, 0, "a", 0, 60);
        Add(timeline, 0, "b", 60, 60);
        Add(timeline, 0, "title", 120, 30, ClipType.text);
        Add(timeline, 0, "c", 200, 30, ClipType.image);
        Add(timeline, 2, "x", 0, 60, ClipType.audio);
        Add(timeline, 2, "y", 60, 60, ClipType.audio);

        var video = TransitionBlocks.Corners(timeline, 0).Select(Describe).ToArray();
        Assert.Equal(
            ["a:In", "a:Out>b", "b:In<a", "b:Out", "title:In", "title:Out", "c:In", "c:Out"],
            video);
        var audio = TransitionBlocks.Corners(timeline, 2).Select(Describe).ToArray();
        Assert.Equal(["x:In", "x:Out", "y:In", "y:Out"], audio);
    }

    [Fact]
    public void Occupant_AndFree_CoverBothSidesOfACut()
    {
        var timeline = NewTimeline();
        var a = Add(timeline, 0, "a", 0, 60, fadeOut: 12);
        var b = Add(timeline, 0, "b", 60, 60);
        var index = new TransitionIndex(timeline);
        var aOut = TransitionBlocks.Corner(timeline, "a", FadeEdge.Out)!.Value;
        var bIn = TransitionBlocks.Corner(timeline, "b", FadeEdge.In)!.Value;

        Assert.Equal(TransitionBlockRef.Fade(FadeEdge.Out, "a"), TransitionBlocks.Occupant(index, aOut));
        Assert.Null(TransitionBlocks.Occupant(index, bIn));
        Assert.False(TransitionBlocks.IsFree(index, bIn));

        a.FadeOutFrames = 0;
        timeline.Transitions.Add(new Transition { Id = "t", ClipIdA = a.Id, ClipIdB = b.Id, DurationFrames = 10 });
        index = new TransitionIndex(timeline);
        Assert.Equal(TransitionBlockRef.Cut("t"), TransitionBlocks.Occupant(index, aOut));
        Assert.Equal(TransitionBlockRef.Cut("t"), TransitionBlocks.Occupant(index, bIn));
    }

    [Fact]
    public void Rooms_KeepBlocksInOneClipApart()
    {
        var timeline = NewTimeline();
        var a = Add(timeline, 0, "a", 0, 60, fadeIn: 50);
        var b = Add(timeline, 0, "b", 60, 60, fadeOut: 20);
        var index = new TransitionIndex(timeline);

        Assert.Equal(10, TransitionBlocks.MaxFade(index, a, FadeEdge.Out));
        Assert.Equal(40, TransitionBlocks.MaxFade(index, b, FadeEdge.In));
        // Before the cut A has 10 frames, after it B has 40: 21 puts 10 before and 11 after.
        Assert.Equal(21, TransitionBlocks.MaxCut(index, a, b));

        a.FadeInFrames = 0;
        timeline.Transitions.Add(new Transition { ClipIdA = a.Id, ClipIdB = b.Id, DurationFrames = 30 });
        index = new TransitionIndex(timeline);
        Assert.Equal(15, TransitionBlocks.TailUsed(index, a));
        Assert.Equal(15, TransitionBlocks.HeadUsed(index, b));
        Assert.Equal(45, TransitionBlocks.MaxFade(index, a, FadeEdge.In));
        Assert.Equal(45, TransitionBlocks.MaxFade(index, b, FadeEdge.Out));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 0)]
    [InlineData(3, 3, 6)]
    [InlineData(3, 4, 7)]
    [InlineData(4, 3, 6)]
    public void MaxCutFrames_FitsBothSidesOfTheCut(int before, int after, int expected)
    {
        var max = TransitionBlocks.MaxCutFrames(before, after);
        Assert.Equal(expected, max);
        Assert.True(max / 2 <= before && max - (max / 2) <= after);
    }

    [Fact]
    public void MaxCut_IsCappedAtTheShorterClip_AsTheRendererCapsIt()
    {
        var timeline = NewTimeline();
        var a = Add(timeline, 0, "a", 0, 10);
        var b = Add(timeline, 0, "b", 10, 100);
        Assert.Equal(10, TransitionBlocks.MaxCut(new TransitionIndex(timeline), a, b));
    }

    [Fact]
    public void DragLengths_FollowTheEdgeFrame()
    {
        var clip = new Clip { StartFrame = 100, DurationFrames = 50 };
        Assert.Equal(12, TransitionBlocks.FadeFramesAt(clip, FadeEdge.In, 112));
        Assert.Equal(8, TransitionBlocks.FadeFramesAt(clip, FadeEdge.Out, 142));
        Assert.Equal(14, TransitionBlocks.CutFramesAt(150, 157));
        Assert.Equal(14, TransitionBlocks.CutFramesAt(150, 143));
        Assert.Equal(0, TransitionBlocks.Clamp(5, 1, 0));
        Assert.Equal(2, TransitionBlocks.Clamp(0, 2, 30));
        Assert.Equal(30, TransitionBlocks.Clamp(99, 2, 30));
        Assert.Equal(1, TransitionBlocks.Clamp(0, 2, 1));
    }

    [Fact]
    public void EdgePreviewFrame_IsTheFirstFullFrame_InsideTheEdgesClip()
    {
        var timeline = NewTimeline();
        var a = Add(timeline, 0, "a", 0, 60, fadeIn: 10);
        var b = Add(timeline, 0, "b", 60, 60);
        timeline.Transitions.Add(new Transition { ClipIdA = a.Id, ClipIdB = b.Id, DurationFrames = 20 });
        var blocks = TransitionBlocks.ForTimeline(timeline);
        var fade = blocks.Single(x => x.Kind == TransitionBlockKind.FadeIn);
        var cut = blocks.Single(x => x.IsCut);

        Assert.Equal(10, TransitionBlocks.EdgePreviewFrame(fade, rightEdge: true));
        Assert.Equal(50, TransitionBlocks.EdgePreviewFrame(cut, rightEdge: false));
        Assert.Equal(70, TransitionBlocks.EdgePreviewFrame(cut, rightEdge: true));

        a.FadeInFrames = 60;
        timeline.Transitions.Clear();
        var whole = TransitionBlocks.ForTimeline(timeline).Single();
        Assert.Equal(59, TransitionBlocks.EdgePreviewFrame(whole, rightEdge: true));
    }

    [Fact]
    public void NearestFreeCorner_SkipsTakenEdges_AndPrefersTheCutOnATie()
    {
        var timeline = NewTimeline();
        Add(timeline, 0, "a", 0, 60, fadeIn: 5);
        Add(timeline, 0, "b", 60, 60);
        var index = new TransitionIndex(timeline);

        var atCut = TransitionBlocks.NearestFreeCorner(index, 0, 58)!.Value;
        Assert.True(atCut.CreatesCut);
        Assert.Equal(60, atCut.Frame);

        var nearStart = TransitionBlocks.NearestFreeCorner(index, 0, 2)!.Value;
        Assert.True(nearStart.CreatesCut);

        var nearEnd = TransitionBlocks.NearestFreeCorner(index, 0, 115)!.Value;
        Assert.Equal(("b", FadeEdge.Out), (nearEnd.Clip.Id, nearEnd.Edge));
    }

    [Fact]
    public void TrackForDefault_UsesTheFocusedClip_ElseTheFirstTrackWithRoom()
    {
        var timeline = NewTimeline();
        Add(timeline, 1, "v2", 0, 60);
        Add(timeline, 2, "music", 0, 60, ClipType.audio);
        var index = new TransitionIndex(timeline);

        Assert.Equal(2, TransitionBlocks.TrackForDefault(index, "music", 0));
        Assert.Equal(1, TransitionBlocks.TrackForDefault(index, null, 0));
        Assert.Equal(1, TransitionBlocks.TrackForDefault(index, "missing", 0));
    }

    [Theory]
    [InlineData(TransitionType.CrossDissolve, "Cross Dissolve")]
    [InlineData(TransitionType.FadeToBlack, "Dip to Black")]
    [InlineData(TransitionType.FadeToWhite, "Dip to White")]
    [InlineData(TransitionType.SlideUp, "Slide Up")]
    [InlineData(TransitionType.Wipe, "Wipe")]
    public void DisplayNames(TransitionType type, string name)
        => Assert.Equal(name, TransitionBlocks.DisplayName(type));

    [Theory]
    [InlineData(30, 30, "1 s")]
    [InlineData(15, 30, "0.5 s")]
    [InlineData(10, 30, "0.33 s")]
    [InlineData(45, 30, "1.5 s")]
    [InlineData(1, 24, "0.04 s")]
    public void Seconds_AreShortAndCultureFree(int frames, int fps, string text)
        => Assert.Equal(text, TransitionBlocks.FormatSeconds(frames, fps));

    private static string Describe(ClipCorner corner)
    {
        var side = corner.Neighbour is null ? "" : (corner.Edge == FadeEdge.Out ? ">" : "<") + corner.Neighbour.Id;
        return $"{corner.Clip.Id}:{corner.Edge}{side}";
    }

    internal static Timeline NewTimeline() => ProjectSession.CreateNew().ActiveTimeline;

    internal static Clip Add(
        Timeline timeline,
        int track,
        string id,
        int start,
        int duration,
        ClipType type = ClipType.video,
        int fadeIn = 0,
        int fadeOut = 0,
        string? link = null)
    {
        var clip = new Clip
        {
            Id = id,
            MediaRef = "m-" + id,
            MediaType = type,
            StartFrame = start,
            DurationFrames = duration,
            FadeInFrames = fadeIn,
            FadeOutFrames = fadeOut,
            LinkGroupId = link,
        };
        timeline.Tracks[track].Clips.Add(clip);
        return clip;
    }
}
