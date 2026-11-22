using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-248: which upcoming decodes playback starts ahead of the cut.</summary>
public sealed class PlaybackPrerollTests
{
    private static Clip MakeClip(
        string mediaRef, int start, int duration, int trimStart = 0, double speed = 1.0,
        ClipType type = ClipType.video, string? id = null)
        => new()
        {
            Id = id ?? Timeline.NewId(),
            MediaRef = mediaRef,
            MediaType = type,
            SourceClipType = type,
            StartFrame = start,
            DurationFrames = duration,
            TrimStartFrame = trimStart,
            Speed = speed,
        };

    private static Track VideoTrack(params Clip[] clips) => new() { Type = ClipType.video, Clips = [.. clips] };

    private static Timeline MakeTimeline(params Track[] tracks) => new() { Fps = 24, Tracks = [.. tracks] };

    private static MediaResolver MakeResolver(params string[] ids)
    {
        var manifest = new MediaManifest
        {
            Entries = ids
                .Select(id => new MediaManifestEntry { Id = id, Source = MediaSource.External($"C:/media/{id}.mp4") })
                .ToList(),
        };
        return new MediaResolver(() => manifest, () => null);
    }

    [Fact]
    public void NextClipInsideTheWindow_IsPlannedAtItsFirstSourceFrame()
    {
        // The Thaw cut list: 01 Sphere 0-60, 02 Bead 60-168 (trimmed 12 frames in), 03 Drip 168-240.
        var timeline = MakeTimeline(VideoTrack(
            MakeClip("sphere", 0, 60),
            MakeClip("bead", 60, 108, trimStart: 12),
            MakeClip("drip", 168, 72)));

        var targets = PlaybackPreroll.Plan(timeline, frame: 30, lookaheadFrames: 36, MakeResolver("sphere", "bead", "drip"));

        var target = Assert.Single(targets);
        Assert.Equal("C:/media/bead.mp4", target.MediaPath);
        Assert.Equal(12 / 24.0, target.SourceSeconds, 9);
        Assert.Equal(60, target.NeededAtFrame);
    }

    [Fact]
    public void PlannedPosition_IsExactlyWhatTheRendererWillRequestAtTheCut()
    {
        var bead = MakeClip("bead", 60, 108, trimStart: 7, speed: 1.5);
        var timeline = MakeTimeline(VideoTrack(MakeClip("sphere", 0, 60), bead));
        var resolver = MakeResolver("sphere", "bead");

        var target = Assert.Single(PlaybackPreroll.Plan(timeline, 50, 36, resolver));
        var atCut = Assert.Single(TimelineFrameResolver.Resolve(timeline, 60, resolver));

        Assert.Equal(atCut.MediaPath, target.MediaPath);
        Assert.Equal(atCut.SourceSeconds, target.SourceSeconds);
    }

    [Fact]
    public void CutsOutsideTheWindow_AndTheClipPlayingNow_AreNotPlanned()
    {
        var timeline = MakeTimeline(VideoTrack(
            MakeClip("sphere", 0, 60),
            MakeClip("bead", 60, 108)));
        var resolver = MakeResolver("sphere", "bead");

        Assert.Empty(PlaybackPreroll.Plan(timeline, frame: 10, lookaheadFrames: 36, resolver));
        Assert.Empty(PlaybackPreroll.Plan(timeline, frame: 60, lookaheadFrames: 36, resolver));
        Assert.Single(PlaybackPreroll.Plan(timeline, frame: 24, lookaheadFrames: 36, resolver));
    }

    [Fact]
    public void HiddenTracks_OverlappedClips_NonVideo_AndUnresolvedMedia_AreNotPlanned()
    {
        var hidden = VideoTrack(MakeClip("a", 20, 30));
        hidden.Hidden = true;
        var timeline = MakeTimeline(
            hidden,
            // "b" is never shown: it starts inside its predecessor (the resolver's Mac overlap rule).
            VideoTrack(MakeClip("base", 0, 40), MakeClip("b", 30, 30)),
            VideoTrack(MakeClip("still", 20, 30, type: ClipType.image)),
            VideoTrack(MakeClip("missing", 20, 30)));

        Assert.Empty(PlaybackPreroll.Plan(timeline, 10, 36, MakeResolver("a", "base", "b", "still")));
    }

    [Fact]
    public void ClipEnteringThroughATransition_IsNeededWhenTheTransitionStarts()
    {
        var a = MakeClip("sphere", 0, 60, id: "A");
        var b = MakeClip("bead", 60, 108, trimStart: 5, id: "B");
        var timeline = MakeTimeline(VideoTrack(a, b));
        timeline.Transitions.Add(new Transition { ClipIdA = "A", ClipIdB = "B", DurationFrames = 12 });

        var target = Assert.Single(PlaybackPreroll.Plan(timeline, 20, 36, MakeResolver("sphere", "bead")));

        // The renderer decodes B from frame 54 on, clamped to B's own start frame.
        Assert.Equal(54, target.NeededAtFrame);
        Assert.Equal(5 / 24.0, target.SourceSeconds, 9);
    }

    [Fact]
    public void SplitWithNothingRemoved_IsLeftToThePlayingDecoder_ButAJumpCutIsPlanned()
    {
        // Same source, contiguous: 0-60 plays source frames 0-59, the next clip resumes at 60.
        var split = MakeTimeline(VideoTrack(
            MakeClip("take", 0, 60),
            MakeClip("take", 60, 60, trimStart: 60)));
        // Same source, 30 frames removed at the cut.
        var jump = MakeTimeline(VideoTrack(
            MakeClip("take", 0, 60),
            MakeClip("take", 60, 60, trimStart: 90)));

        Assert.Empty(PlaybackPreroll.Plan(split, 40, 36, MakeResolver("take")));
        var target = Assert.Single(PlaybackPreroll.Plan(jump, 40, 36, MakeResolver("take")));
        Assert.Equal(90 / 24.0, target.SourceSeconds, 9);
    }

    [Fact]
    public void SeveralCuts_AreReturnedSoonestFirst()
    {
        var timeline = MakeTimeline(
            VideoTrack(MakeClip("overlay", 40, 10)),
            VideoTrack(MakeClip("a", 0, 30), MakeClip("b", 30, 30)));

        var targets = PlaybackPreroll.Plan(timeline, 10, 36, MakeResolver("overlay", "a", "b"));

        Assert.Equal([30, 40], targets.Select(t => t.NeededAtFrame).ToArray());
    }

    [Fact]
    public void LeadFrames_CoverTheLeadSecondsAtTheTimelineRate()
    {
        Assert.Equal(36, PlaybackPreroll.LeadFrames(24));
        Assert.Equal(90, PlaybackPreroll.LeadFrames(60));
    }
}
