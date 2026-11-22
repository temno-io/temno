using Temno.Core.Models;

namespace Temno.Core.Editing;

/// <summary>Mac: Timeline/SnapEngine.swift — pure geometry, no UI haptics.</summary>
public static class SnapEngine
{
    public enum Kind
    {
        Playhead,
        ClipEdge,
        Beat,
    }

    public readonly record struct SnapTarget(int Frame, Kind Kind);

    public readonly record struct SnapResult(int Frame, int ProbeOffset, double X);

    /// <summary>Mutable sticky state across drag events.</summary>
    public sealed class SnapState
    {
        public int? CurrentlySnappedTo { get; set; }
        public int CurrentProbeOffset { get; set; }
    }

    // Mac Utilities/Constants.swift enum Snap
    public const double ThresholdPixels = 8.0;
    public const double StickyMultiplier = 1.5;
    public const double PlayheadMultiplier = 1.5;

    public static List<SnapTarget> CollectTargets(
        IReadOnlyList<Track> tracks,
        int playheadFrame = 0,
        IReadOnlySet<string>? excludeClipIds = null,
        bool includePlayhead = false,
        Func<Clip, IEnumerable<int>>? beatFrames = null,
        bool includeExcludedClipBeats = false)
    {
        excludeClipIds ??= new HashSet<string>();
        var targets = new List<SnapTarget>();
        if (includePlayhead)
            targets.Add(new SnapTarget(playheadFrame, Kind.Playhead));

        foreach (var track in tracks)
        {
            foreach (var clip in track.Clips)
            {
                var excluded = excludeClipIds.Contains(clip.Id);
                if (!excluded)
                {
                    targets.Add(new SnapTarget(clip.StartFrame, Kind.ClipEdge));
                    targets.Add(new SnapTarget(clip.EndFrame, Kind.ClipEdge));
                }

                if (beatFrames is not null && (!excluded || includeExcludedClipBeats))
                {
                    foreach (var frame in beatFrames(clip))
                        targets.Add(new SnapTarget(frame, Kind.Beat));
                }
            }
        }

        return targets;
    }

    /// <summary>
    /// Snap position(s) to nearest target with sticky hold.
    /// <paramref name="probeOffsets"/> e.g. 0 = start, duration = end.
    /// </summary>
    public static SnapResult? FindSnap(
        int position,
        IReadOnlyList<int> probeOffsets,
        IReadOnlyList<SnapTarget> targets,
        SnapState state,
        double baseThreshold,
        double pixelsPerFrame)
    {
        if (probeOffsets.Count == 0)
            probeOffsets = [0];

        var baseFrameThreshold = baseThreshold / pixelsPerFrame;

        if (state.CurrentlySnappedTo is int snapped)
        {
            var holdThreshold = baseFrameThreshold * StickyMultiplier;
            var probePos = position + state.CurrentProbeOffset;
            if (Math.Abs(probePos - snapped) <= holdThreshold
                && targets.Any(t => t.Frame == snapped))
            {
                return new SnapResult(snapped, state.CurrentProbeOffset, snapped * pixelsPerFrame);
            }
            state.CurrentlySnappedTo = null;
            state.CurrentProbeOffset = 0;
        }

        (int ProbeOffset, SnapTarget Target, double Distance)? best = null;
        foreach (var probeOffset in probeOffsets)
        {
            var probePos = position + probeOffset;
            foreach (var target in targets)
            {
                var threshold = target.Kind == Kind.Playhead
                    ? baseFrameThreshold * PlayheadMultiplier
                    : baseFrameThreshold;
                var dist = Math.Abs((double)probePos - target.Frame);
                if (dist <= threshold && dist < (best?.Distance ?? double.PositiveInfinity))
                    best = (probeOffset, target, dist);
            }
        }

        if (best is null)
            return null;

        state.CurrentlySnappedTo = best.Value.Target.Frame;
        state.CurrentProbeOffset = best.Value.ProbeOffset;
        return new SnapResult(
            best.Value.Target.Frame,
            best.Value.ProbeOffset,
            best.Value.Target.Frame * pixelsPerFrame);
    }

    public static SnapResult? FindSnap(
        int position,
        IReadOnlyList<SnapTarget> targets,
        SnapState state,
        double baseThreshold,
        double pixelsPerFrame)
        => FindSnap(position, [0], targets, state, baseThreshold, pixelsPerFrame);
}
