using Temno.Core.Editing;
using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

public class SnapEngineTests
{
    // baseThreshold=8 px, pixelsPerFrame=4 → 2-frame base threshold (Mac tests).
    private const double BasePx = 8;
    private const double PxPerFrame = 4;

    private static Track VideoTrack(params Clip[] clips) => new()
    {
        Type = ClipType.video,
        Clips = clips.ToList(),
    };

    private static Clip Clip(string id, int start, int duration) => new()
    {
        Id = id,
        MediaRef = "m",
        StartFrame = start,
        DurationFrames = duration,
    };

    [Fact]
    public void CollectTargets_EmptyTracks_NoTargets()
    {
        Assert.Empty(SnapEngine.CollectTargets([], includePlayhead: false));
    }

    [Fact]
    public void CollectTargets_IncludesPlayheadOnlyWhenRequested()
    {
        var with = SnapEngine.CollectTargets([], playheadFrame: 75, includePlayhead: true);
        Assert.Single(with);
        Assert.Equal(75, with[0].Frame);
        Assert.Equal(SnapEngine.Kind.Playhead, with[0].Kind);

        var without = SnapEngine.CollectTargets([], playheadFrame: 75, includePlayhead: false);
        Assert.Empty(without);
    }

    [Fact]
    public void CollectTargets_StartAndEndForEachClip()
    {
        var track = VideoTrack(
            Clip("a", 0, 50),
            Clip("b", 100, 80));
        var targets = SnapEngine.CollectTargets([track]);
        var frames = targets.Select(t => t.Frame).OrderBy(f => f).ToList();
        Assert.Equal(new[] { 0, 50, 100, 180 }, frames);
        Assert.All(targets, t => Assert.Equal(SnapEngine.Kind.ClipEdge, t.Kind));
    }

    [Fact]
    public void CollectTargets_SkipsExcludedClipIds()
    {
        var track = VideoTrack(
            Clip("drag", 0, 50),
            Clip("static", 100, 80));
        var targets = SnapEngine.CollectTargets([track], excludeClipIds: new HashSet<string> { "drag" });
        var frames = targets.Select(t => t.Frame).OrderBy(f => f).ToList();
        Assert.Equal(new[] { 100, 180 }, frames);
    }

    [Fact]
    public void FindSnap_NoTargets_ReturnsNull()
    {
        var state = new SnapEngine.SnapState();
        var result = SnapEngine.FindSnap(100, [], state, BasePx, PxPerFrame);
        Assert.Null(result);
        Assert.Null(state.CurrentlySnappedTo);
    }

    [Fact]
    public void FindSnap_BeyondThreshold_ReturnsNull()
    {
        var targets = new[] { new SnapEngine.SnapTarget(50, SnapEngine.Kind.ClipEdge) };
        var state = new SnapEngine.SnapState();
        // pos=55, dist=5, frame threshold=2 → no snap
        var result = SnapEngine.FindSnap(55, targets, state, BasePx, PxPerFrame);
        Assert.Null(result);
    }

    [Fact]
    public void FindSnap_WithinThreshold_Snaps()
    {
        var targets = new[] { new SnapEngine.SnapTarget(50, SnapEngine.Kind.ClipEdge) };
        var state = new SnapEngine.SnapState();
        var result = SnapEngine.FindSnap(49, targets, state, BasePx, PxPerFrame);
        Assert.NotNull(result);
        Assert.Equal(50, result!.Value.Frame);
        Assert.Equal(0, result.Value.ProbeOffset);
        Assert.Equal(200, result.Value.X); // 50 * 4
        Assert.Equal(50, state.CurrentlySnappedTo);
    }

    [Fact]
    public void FindSnap_PicksClosest_FirstOnTie()
    {
        var targets = new[]
        {
            new SnapEngine.SnapTarget(49, SnapEngine.Kind.ClipEdge),
            new SnapEngine.SnapTarget(51, SnapEngine.Kind.ClipEdge),
        };
        var state = new SnapEngine.SnapState();
        var result = SnapEngine.FindSnap(50, targets, state, BasePx, PxPerFrame);
        Assert.Equal(49, result!.Value.Frame);
    }

    [Fact]
    public void FindSnap_StickyWithinHoldThreshold()
    {
        var targets = new[] { new SnapEngine.SnapTarget(50, SnapEngine.Kind.ClipEdge) };
        var state = new SnapEngine.SnapState();

        _ = SnapEngine.FindSnap(49, targets, state, BasePx, PxPerFrame);
        Assert.Equal(50, state.CurrentlySnappedTo);

        // Move to dist=3 frames; sticky hold = 2 * 1.5 = 3 → still sticky
        var held = SnapEngine.FindSnap(53, targets, state, BasePx, PxPerFrame);
        Assert.Equal(50, held!.Value.Frame);
        Assert.Equal(50, state.CurrentlySnappedTo);
    }

    [Fact]
    public void FindSnap_ProbeOffsets_UsesEndEdge()
    {
        var targets = new[] { new SnapEngine.SnapTarget(100, SnapEngine.Kind.ClipEdge) };
        var state = new SnapEngine.SnapState();
        // clip start at 50, duration 50 → end probe at 100
        var result = SnapEngine.FindSnap(
            position: 50,
            probeOffsets: [0, 50],
            targets,
            state,
            BasePx,
            PxPerFrame);
        Assert.NotNull(result);
        Assert.Equal(100, result!.Value.Frame);
        Assert.Equal(50, result.Value.ProbeOffset);
    }

    [Fact]
    public void FindSnap_MultiClipProbes_SnapsSecondaryEndEdge()
    {
        // Primary start 0, secondary starts at +40 with duration 30 → end probe at 70.
        var targets = new[] { new SnapEngine.SnapTarget(71, SnapEngine.Kind.ClipEdge) };
        var state = new SnapEngine.SnapState();
        var result = SnapEngine.FindSnap(
            position: 0,
            probeOffsets: [0, 40, 40 + 30],
            targets,
            state,
            BasePx,
            PxPerFrame);
        Assert.NotNull(result);
        Assert.Equal(71, result!.Value.Frame);
        Assert.Equal(70, result.Value.ProbeOffset);
    }

    [Fact]
    public void CollectTargets_IncludesBeatFrames()
    {
        var track = VideoTrack(Clip("a", 0, 100));
        var targets = SnapEngine.CollectTargets(
            [track],
            beatFrames: _ => [10, 20, 10]);
        Assert.Contains(targets, t => t is { Frame: 10, Kind: SnapEngine.Kind.Beat });
        Assert.Contains(targets, t => t is { Frame: 20, Kind: SnapEngine.Kind.Beat });
        // CollectTargets does not dedupe beat frames (UI HashSet does when mapping analysis).
        Assert.Equal(3, targets.Count(t => t.Kind == SnapEngine.Kind.Beat));
    }

    [Fact]
    public void CollectTargets_IncludeExcludedClipBeats_ForTrim()
    {
        var track = VideoTrack(Clip("trim", 0, 100), Clip("other", 200, 50));
        var targets = SnapEngine.CollectTargets(
            [track],
            excludeClipIds: new HashSet<string> { "trim" },
            beatFrames: clip => clip.Id == "trim" ? [25] : Array.Empty<int>(),
            includeExcludedClipBeats: true);
        Assert.DoesNotContain(targets, t => t.Frame is 0 or 100); // trim edges excluded
        Assert.Contains(targets, t => t is { Frame: 25, Kind: SnapEngine.Kind.Beat });
        Assert.Contains(targets, t => t.Frame == 200);
    }
}
