using System.Globalization;
using Temno.Core.Models;

namespace Temno.Core.Editing;

public enum TransitionBlockKind
{
    FadeIn,
    FadeOut,
    Cut,
}

/// <summary>
/// A block's identity across redraws: the clip id for a fade, the transition id for a cut.
/// </summary>
public readonly record struct TransitionBlockRef(TransitionBlockKind Kind, string Id)
{
    public static TransitionBlockRef Fade(FadeEdge edge, string clipId)
        => new(edge == FadeEdge.In ? TransitionBlockKind.FadeIn : TransitionBlockKind.FadeOut, clipId);

    public static TransitionBlockRef Cut(string transitionId) => new(TransitionBlockKind.Cut, transitionId);

    public bool IsFade => Kind != TransitionBlockKind.Cut;

    public FadeEdge Edge => Kind == TransitionBlockKind.FadeOut ? FadeEdge.Out : FadeEdge.In;
}

/// <summary>
/// One fade or cut transition as the timeline draws it. Frames are timeline frames, end
/// exclusive. A fade runs from its clip's edge inward; a cut transition covers the renderer's
/// window (<see cref="ResolvedTransition.StartFrame"/> to <see cref="ResolvedTransition.EndFrame"/>).
/// </summary>
public sealed record TransitionBlock(
    TransitionBlockRef Ref,
    int TrackIndex,
    int StartFrame,
    int EndFrame,
    Clip Clip,
    Clip? ClipB,
    Transition? Transition)
{
    public TransitionBlockKind Kind => Ref.Kind;

    public int Frames => EndFrame - StartFrame;

    public bool IsCut => Kind == TransitionBlockKind.Cut;

    /// <summary>The edit point of a cut transition; the clip edge of a fade.</summary>
    public int AnchorFrame => Kind switch
    {
        TransitionBlockKind.FadeIn => Clip.StartFrame,
        TransitionBlockKind.FadeOut => Clip.EndFrame,
        _ => ClipB?.StartFrame ?? Clip.EndFrame,
    };

    public Interpolation Curve => Kind switch
    {
        TransitionBlockKind.FadeIn => Clip.FadeInInterpolation,
        TransitionBlockKind.FadeOut => Clip.FadeOutInterpolation,
        _ => Interpolation.linear,
    };

    public string Name => TransitionBlocks.Name(this);

    /// <summary>A fade's inner edge is the one that resizes it; a cut transition resizes from both.</summary>
    public bool Resizes(bool rightEdge) => Kind switch
    {
        TransitionBlockKind.FadeIn => rightEdge,
        TransitionBlockKind.FadeOut => !rightEdge,
        _ => true,
    };
}

/// <summary>
/// One top corner of a clip, where a new block starts. At a cut between two video or image
/// clips on a video track, <see cref="Neighbour"/> is the clip across the cut and the corner
/// creates a cut transition; elsewhere it creates a fade.
/// </summary>
public readonly record struct ClipCorner(Clip Clip, int TrackIndex, FadeEdge Edge, Clip? Neighbour)
{
    public bool CreatesCut => Neighbour is not null;

    public int Frame => Edge == FadeEdge.In ? Clip.StartFrame : Clip.EndFrame;

    /// <summary>Outgoing and incoming clips of the cut this corner sits on.</summary>
    public (Clip A, Clip B)? Pair => Neighbour is null
        ? null
        : Edge == FadeEdge.Out ? (Clip, Neighbour) : (Neighbour, Clip);
}

/// <summary>
/// The timeline's resolved cut transitions, looked up by clip. <see cref="Timeline.ResolvedTransitions"/>
/// rescans every track per transition, so a pass that asks about many corners builds this once.
/// Stale as soon as the timeline changes.
/// </summary>
public sealed class TransitionIndex
{
    private readonly Dictionary<string, ResolvedTransition> _byTail = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ResolvedTransition> _byHead = new(StringComparer.Ordinal);

    public TransitionIndex(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        Timeline = timeline;
        Resolved = timeline.ResolvedTransitions;
        foreach (var rt in Resolved)
        {
            _byTail.TryAdd(rt.ClipA.Id, rt);
            _byHead.TryAdd(rt.ClipB.Id, rt);
        }
    }

    public Timeline Timeline { get; }

    public IReadOnlyList<ResolvedTransition> Resolved { get; }

    /// <summary>The cut transition leaving from <paramref name="clip"/>'s tail, if it resolves.</summary>
    public ResolvedTransition? AtTail(Clip clip) => _byTail.GetValueOrDefault(clip.Id);

    /// <summary>The cut transition arriving at <paramref name="clip"/>'s head, if it resolves.</summary>
    public ResolvedTransition? AtHead(Clip clip) => _byHead.GetValueOrDefault(clip.Id);
}

/// <summary>
/// WP-252. What the timeline draws for fades and cut transitions, where new ones can start,
/// how much room each has, and how a dragged edge maps to a length. Pure: no session, no UI.
/// Each clip edge holds at most one block, and blocks inside one clip never overlap.
/// </summary>
public static class TransitionBlocks
{
    public const int MinFadeFrames = 1;

    /// <summary>
    /// A one-frame cut transition renders as a hard cut (progress 1 on its only frame), so two is
    /// the shortest length that shows a blend.
    /// </summary>
    public const int MinCutFrames = 2;

    public const TransitionType DefaultType = TransitionType.CrossDissolve;

    public static bool SupportsFade(ClipType type)
        => type is ClipType.audio or ClipType.video or ClipType.image or ClipType.sequence or ClipType.text;

    public static bool SupportsCut(ClipType type) => type is ClipType.video or ClipType.image;

    public static IReadOnlyList<TransitionBlock> ForTimeline(Timeline timeline)
        => ForTimeline(new TransitionIndex(timeline));

    public static IReadOnlyList<TransitionBlock> ForTimeline(TransitionIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        var blocks = new List<TransitionBlock>();
        for (var i = 0; i < index.Timeline.Tracks.Count; i++)
        {
            foreach (var clip in index.Timeline.Tracks[i].Clips)
            {
                if (FadeBlock(clip, i, FadeEdge.In) is { } fadeIn) blocks.Add(fadeIn);
                if (FadeBlock(clip, i, FadeEdge.Out) is { } fadeOut) blocks.Add(fadeOut);
            }
        }
        foreach (var rt in index.Resolved)
            blocks.Add(CutBlock(rt));
        return blocks;
    }

    public static TransitionBlock? Find(Timeline timeline, TransitionBlockRef blockRef)
        => Find(new TransitionIndex(timeline), blockRef);

    public static TransitionBlock? Find(TransitionIndex index, TransitionBlockRef blockRef)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (string.IsNullOrEmpty(blockRef.Id)) return null;
        if (blockRef.Kind == TransitionBlockKind.Cut)
        {
            foreach (var rt in index.Resolved)
            {
                if (string.Equals(rt.Transition.Id, blockRef.Id, StringComparison.Ordinal))
                    return CutBlock(rt);
            }
            return null;
        }

        if (!TryFindClip(index.Timeline, blockRef.Id, out var clip, out var trackIndex)) return null;
        return FadeBlock(clip, trackIndex, blockRef.Edge);
    }

    /// <summary>
    /// Every top corner on the track that can hold a block, in time order. At a cut between two
    /// video or image clips both corners carry the other clip as <see cref="ClipCorner.Neighbour"/>.
    /// </summary>
    public static IReadOnlyList<ClipCorner> Corners(Timeline timeline, int trackIndex)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var corners = new List<ClipCorner>();
        if (trackIndex < 0 || trackIndex >= timeline.Tracks.Count) return corners;
        var track = timeline.Tracks[trackIndex];
        var clips = track.Clips.Where(c => c.DurationFrames > 0).OrderBy(c => c.StartFrame).ToList();
        for (var i = 0; i < clips.Count; i++)
        {
            var clip = clips[i];
            if (!SupportsFade(clip.MediaType)) continue;
            corners.Add(new ClipCorner(clip, trackIndex, FadeEdge.In, CutNeighbour(track, clips, i, before: true)));
            corners.Add(new ClipCorner(clip, trackIndex, FadeEdge.Out, CutNeighbour(track, clips, i, before: false)));
        }
        return corners;
    }

    public static ClipCorner? Corner(Timeline timeline, string clipId, FadeEdge edge)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (!TryFindClip(timeline, clipId, out var clip, out var trackIndex)) return null;
        foreach (var corner in Corners(timeline, trackIndex))
        {
            if (ReferenceEquals(corner.Clip, clip) && corner.Edge == edge)
                return corner;
        }
        return null;
    }

    /// <summary>The block already at this corner, or null when the corner is free.</summary>
    public static TransitionBlockRef? Occupant(TransitionIndex index, ClipCorner corner)
    {
        ArgumentNullException.ThrowIfNull(index);
        var clip = corner.Clip;
        if (corner.Edge == FadeEdge.In)
        {
            if (index.AtHead(clip) is { } head) return TransitionBlockRef.Cut(head.Transition.Id);
            if (clip.FadeInFrames > 0) return TransitionBlockRef.Fade(FadeEdge.In, clip.Id);
        }
        else
        {
            if (index.AtTail(clip) is { } tail) return TransitionBlockRef.Cut(tail.Transition.Id);
            if (clip.FadeOutFrames > 0) return TransitionBlockRef.Fade(FadeEdge.Out, clip.Id);
        }
        return null;
    }

    /// <summary>
    /// A corner that can take a new block: nothing on it, and nothing on a cut transition's
    /// other side either.
    /// </summary>
    public static bool IsFree(TransitionIndex index, ClipCorner corner)
    {
        if (Occupant(index, corner) is not null) return false;
        return corner.Pair is not { } pair || Occupant(index, OtherSide(corner, pair)) is null;
    }

    /// <summary>Frames a block at the clip's head takes from the clip.</summary>
    public static int HeadUsed(TransitionIndex index, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(index);
        var fade = Math.Max(0, clip.FadeInFrames);
        var cut = index.AtHead(clip) is { } rt ? rt.EndFrame - rt.CutFrame : 0;
        return Math.Max(fade, cut);
    }

    /// <summary>Frames a block at the clip's tail takes from the clip.</summary>
    public static int TailUsed(TransitionIndex index, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(index);
        var fade = Math.Max(0, clip.FadeOutFrames);
        var cut = index.AtTail(clip) is { } rt ? rt.CutFrame - rt.StartFrame : 0;
        return Math.Max(fade, cut);
    }

    /// <summary>Longest fade this edge can take without overlapping the other edge's block.</summary>
    public static int MaxFade(TransitionIndex index, Clip clip, FadeEdge edge)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(clip);
        var opposite = edge == FadeEdge.In ? TailUsed(index, clip) : HeadUsed(index, clip);
        return Math.Max(0, clip.DurationFrames - opposite);
    }

    /// <summary>
    /// Longest cut transition between <paramref name="a"/> and <paramref name="b"/>. The part before
    /// the cut (<c>frames / 2</c>) must fit in A beside A's head block, the part after it in B
    /// beside B's tail block, and the whole is capped at the shorter clip as
    /// <see cref="Timeline.ResolvedTransitions"/> caps it.
    /// </summary>
    public static int MaxCut(TransitionIndex index, Clip a, Clip b)
    {
        ArgumentNullException.ThrowIfNull(index);
        var roomA = Math.Max(0, a.DurationFrames - HeadUsed(index, a));
        var roomB = Math.Max(0, b.DurationFrames - TailUsed(index, b));
        return Math.Min(MaxCutFrames(roomA, roomB), Math.Min(a.DurationFrames, b.DurationFrames));
    }

    /// <summary>Largest <c>f</c> with <c>f / 2 ≤ roomBefore</c> and <c>f − f / 2 ≤ roomAfter</c>.</summary>
    public static int MaxCutFrames(int roomBefore, int roomAfter)
    {
        roomBefore = Math.Max(0, roomBefore);
        roomAfter = Math.Max(0, roomAfter);
        var even = 2 * Math.Min(roomBefore, roomAfter);
        var odd = roomAfter >= 1 ? (2 * Math.Min(roomBefore, roomAfter - 1)) + 1 : 0;
        return Math.Max(even, odd);
    }

    /// <summary>Fade length that puts the fade's inner edge at <paramref name="edgeFrame"/>.</summary>
    public static int FadeFramesAt(Clip clip, FadeEdge edge, int edgeFrame)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return edge == FadeEdge.In ? edgeFrame - clip.StartFrame : clip.EndFrame - edgeFrame;
    }

    /// <summary>
    /// Cut length that puts one edge of the centred window at <paramref name="edgeFrame"/>. The
    /// window grows by one frame on each side at a time, so the length is always even.
    /// </summary>
    public static int CutFramesAt(int cutFrame, int edgeFrame) => 2 * Math.Abs(edgeFrame - cutFrame);

    /// <summary><paramref name="frames"/> in <c>[min, max]</c>; no room at all gives 0.</summary>
    public static int Clamp(int frames, int min, int max)
    {
        if (max <= 0) return 0;
        return Math.Clamp(frames, Math.Min(min, max), max);
    }

    /// <summary>
    /// The frame a monitor shows for a dragged edge: the first frame at full picture on the far
    /// side of the transition, kept inside the clip the edge belongs to.
    /// </summary>
    public static int EdgePreviewFrame(TransitionBlock block, bool rightEdge)
    {
        ArgumentNullException.ThrowIfNull(block);
        var owner = block.Kind == TransitionBlockKind.Cut && rightEdge ? block.ClipB ?? block.Clip : block.Clip;
        var frame = rightEdge ? block.EndFrame : block.StartFrame;
        return Math.Clamp(frame, owner.StartFrame, Math.Max(owner.StartFrame, owner.EndFrame - 1));
    }

    /// <summary>
    /// The free corner nearest <paramref name="playheadFrame"/> on the track. Ties go to the corner
    /// that creates a cut transition, then to the earlier corner.
    /// </summary>
    public static ClipCorner? NearestFreeCorner(TransitionIndex index, int trackIndex, int playheadFrame)
    {
        ArgumentNullException.ThrowIfNull(index);
        ClipCorner? best = null;
        var bestDistance = int.MaxValue;
        foreach (var corner in Corners(index.Timeline, trackIndex))
        {
            if (!IsFree(index, corner)) continue;
            var distance = Math.Abs(corner.Frame - playheadFrame);
            var better = distance < bestDistance
                || (distance == bestDistance && corner.CreatesCut && best is { CreatesCut: false });
            if (!better) continue;
            best = corner;
            bestDistance = distance;
        }
        return best;
    }

    /// <summary>
    /// The track <c>Ctrl+D</c> works on: the focused clip's track, else the first track, in stack
    /// order, that has a free corner.
    /// </summary>
    public static int? TrackForDefault(TransitionIndex index, string? focusedClipId, int playheadFrame)
    {
        ArgumentNullException.ThrowIfNull(index);
        var timeline = index.Timeline;
        if (!string.IsNullOrEmpty(focusedClipId)
            && TryFindClip(timeline, focusedClipId, out _, out var focusedTrack)
            && !timeline.Tracks[focusedTrack].Locked)
            return focusedTrack;
        for (var i = 0; i < timeline.Tracks.Count; i++)
        {
            if (timeline.Tracks[i].Locked) continue;
            if (NearestFreeCorner(index, i, playheadFrame) is not null)
                return i;
        }
        return null;
    }

    public static string DisplayName(TransitionType type) => type switch
    {
        TransitionType.CrossDissolve => "Cross Dissolve",
        TransitionType.FadeToBlack => "Dip to Black",
        TransitionType.FadeToWhite => "Dip to White",
        TransitionType.SlideLeft => "Slide Left",
        TransitionType.SlideRight => "Slide Right",
        TransitionType.SlideUp => "Slide Up",
        TransitionType.SlideDown => "Slide Down",
        TransitionType.Wipe => "Wipe",
        _ => type.ToString(),
    };

    public static string Name(TransitionBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        return block.Kind switch
        {
            TransitionBlockKind.FadeIn => "Fade in",
            TransitionBlockKind.FadeOut => "Fade out",
            _ => DisplayName(block.Transition?.Type ?? DefaultType),
        };
    }

    /// <summary>Seconds with at most two decimals and no trailing zeros: "1 s", "0.5 s", "0.33 s".</summary>
    public static string FormatSeconds(int frames, int fps)
    {
        var seconds = frames / (double)Math.Max(1, fps);
        return seconds.ToString("0.##", CultureInfo.InvariantCulture) + " s";
    }

    /// <summary>"Cross Dissolve · 1 s", the block label and the start of its accessible name.</summary>
    public static string Describe(TransitionBlock block, int fps) => $"{Name(block)} · {FormatSeconds(block.Frames, fps)}";

    internal static bool TryFindClip(Timeline timeline, string clipId, out Clip clip, out int trackIndex)
    {
        for (var i = 0; i < timeline.Tracks.Count; i++)
        {
            foreach (var candidate in timeline.Tracks[i].Clips)
            {
                if (!string.Equals(candidate.Id, clipId, StringComparison.Ordinal)) continue;
                clip = candidate;
                trackIndex = i;
                return true;
            }
        }
        clip = null!;
        trackIndex = -1;
        return false;
    }

    private static ClipCorner OtherSide(ClipCorner corner, (Clip A, Clip B) pair)
        => corner.Edge == FadeEdge.Out
            ? new ClipCorner(pair.B, corner.TrackIndex, FadeEdge.In, pair.A)
            : new ClipCorner(pair.A, corner.TrackIndex, FadeEdge.Out, pair.B);

    private static TransitionBlock? FadeBlock(Clip clip, int trackIndex, FadeEdge edge)
    {
        if (!SupportsFade(clip.MediaType) || clip.DurationFrames <= 0) return null;
        var frames = Math.Clamp(edge == FadeEdge.In ? clip.FadeInFrames : clip.FadeOutFrames, 0, clip.DurationFrames);
        if (frames <= 0) return null;
        var start = edge == FadeEdge.In ? clip.StartFrame : clip.EndFrame - frames;
        return new TransitionBlock(TransitionBlockRef.Fade(edge, clip.Id), trackIndex, start, start + frames, clip, null, null);
    }

    private static TransitionBlock CutBlock(ResolvedTransition rt)
        => new(TransitionBlockRef.Cut(rt.Transition.Id), rt.TrackIndex, rt.StartFrame, rt.EndFrame, rt.ClipA, rt.ClipB, rt.Transition);

    private static Clip? CutNeighbour(Track track, List<Clip> ordered, int index, bool before)
    {
        if (track.Type != ClipType.video) return null;
        var clip = ordered[index];
        if (!SupportsCut(clip.MediaType)) return null;
        if (before)
        {
            for (var j = index - 1; j >= 0; j--)
            {
                var other = ordered[j];
                if (other.EndFrame == clip.StartFrame && SupportsCut(other.MediaType)) return other;
            }
            return null;
        }

        for (var j = index + 1; j < ordered.Count; j++)
        {
            var other = ordered[j];
            if (other.StartFrame == clip.EndFrame && SupportsCut(other.MediaType)) return other;
            if (other.StartFrame > clip.EndFrame) break;
        }
        return null;
    }
}
