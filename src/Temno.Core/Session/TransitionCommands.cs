using Temno.Core.Editing;
using Temno.Core.Models;

namespace Temno.Core.Session;

/// <summary>
/// WP-252. Fades and cut transitions as blocks: add, resize, retype, reshape and remove, each one
/// undo step. Fades write the clip's own fade fields; cut transitions write
/// <see cref="Timeline.Transitions"/>. Lengths clamp to <see cref="TransitionBlocks"/>' rooms, so
/// blocks in one clip never overlap.
/// </summary>
public static class TransitionCommands
{
    /// <summary>
    /// The clip, plus each link partner whose fade on the same edge has the same length. They were
    /// made together, so they change together; a partner the user tuned apart is left alone. Alt
    /// (<paramref name="expandLinks"/> false) keeps it to the one clip.
    /// </summary>
    public static IReadOnlyList<Clip> FadeTargets(Timeline timeline, Clip clip, FadeEdge edge, bool expandLinks)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(clip);
        var targets = new List<Clip> { clip };
        if (!expandLinks || string.IsNullOrEmpty(clip.LinkGroupId)) return targets;

        var length = FadeOf(clip, edge);
        foreach (var id in LinkGroup.Expand(timeline, [clip.Id]).OrderBy(id => id, StringComparer.Ordinal))
        {
            if (string.Equals(id, clip.Id, StringComparison.Ordinal)) continue;
            if (!TransitionBlocks.TryFindClip(timeline, id, out var partner, out var partnerTrackIndex)) continue;
            if (timeline.Tracks[partnerTrackIndex].Locked) continue;
            if (!TransitionBlocks.SupportsFade(partner.MediaType)) continue;
            if (FadeOf(partner, edge) == length) targets.Add(partner);
        }
        return targets;
    }

    /// <summary>
    /// Adds a block at a free corner: a fade, or at a video cut a cut transition that clears the
    /// two facing fades. <paramref name="frames"/> is clamped to the room.
    /// </summary>
    public static EditResult Add(
        ProjectSession session,
        ClipCorner corner,
        int frames,
        bool expandLinks,
        out TransitionBlockRef? created)
    {
        ArgumentNullException.ThrowIfNull(session);
        created = null;
        var timeline = session.ActiveTimeline;
        if (corner.TrackIndex >= 0 && corner.TrackIndex < timeline.Tracks.Count && timeline.Tracks[corner.TrackIndex].Locked)
            return EditResult.Fail(EditCommands.TrackLockedError);
        var index = new TransitionIndex(timeline);
        if (!TransitionBlocks.IsFree(index, corner))
            return EditResult.Fail("That edge already has a transition.");

        TransitionBlockRef? made = null;
        var result = Commit(session, () =>
        {
            if (corner.Pair is { } pair)
            {
                var length = TransitionBlocks.Clamp(frames, TransitionBlocks.MinCutFrames, TransitionBlocks.MaxCut(index, pair.A, pair.B));
                if (length < TransitionBlocks.MinCutFrames) return false;
                made = TransitionBlockRef.Cut(PlaceCut(timeline, pair.A, pair.B, TransitionBlocks.DefaultType, length).Id);
                return true;
            }

            var any = false;
            foreach (var target in FadeTargets(timeline, corner.Clip, corner.Edge, expandLinks))
            {
                var length = TransitionBlocks.Clamp(frames, TransitionBlocks.MinFadeFrames, TransitionBlocks.MaxFade(index, target, corner.Edge));
                if (length <= 0) continue;
                SetFade(target, corner.Edge, length);
                any = true;
            }
            if (any) made = TransitionBlockRef.Fade(corner.Edge, corner.Clip.Id);
            return any;
        });

        if (!result.Ok) return result;
        if (made is null) return EditResult.Fail("There is no room for a transition at that edge.");
        created = made;
        return result;
    }

    /// <summary>
    /// <c>Ctrl+D</c>: one second of the default block at the free corner nearest the playhead, on
    /// the focused clip's track, else on the first track that has a free corner.
    /// </summary>
    public static EditResult AddDefault(
        ProjectSession session,
        string? focusedClipId,
        int playheadFrame,
        bool expandLinks,
        out TransitionBlockRef? created)
    {
        ArgumentNullException.ThrowIfNull(session);
        created = null;
        var timeline = session.ActiveTimeline;
        var index = new TransitionIndex(timeline);
        if (TransitionBlocks.TrackForDefault(index, focusedClipId, playheadFrame) is not { } track
            || TransitionBlocks.NearestFreeCorner(index, track, playheadFrame) is not { } corner)
        {
            return EditResult.Fail("No free clip edge for a transition. Every edge on this track already has one.");
        }

        return Add(session, corner, Math.Max(1, timeline.Fps), expandLinks, out created);
    }

    /// <summary>Sets a block's length: a fade on its link partners too, a cut transition around its cut.</summary>
    public static EditResult SetFrames(ProjectSession session, TransitionBlockRef blockRef, int frames, bool expandLinks)
    {
        ArgumentNullException.ThrowIfNull(session);
        var timeline = session.ActiveTimeline;
        var index = new TransitionIndex(timeline);
        if (TransitionBlocks.Find(index, blockRef) is not { } block)
            return EditResult.Fail("That transition is no longer on the timeline.");
        if (timeline.Tracks[block.TrackIndex].Locked)
            return EditResult.Fail(EditCommands.TrackLockedError);

        return Commit(session, () =>
        {
            if (block.Transition is { } transition && block.ClipB is { } b)
            {
                var length = TransitionBlocks.Clamp(frames, TransitionBlocks.MinCutFrames, TransitionBlocks.MaxCut(index, block.Clip, b));
                if (length < TransitionBlocks.MinCutFrames || length == transition.DurationFrames) return false;
                transition.DurationFrames = length;
                return true;
            }

            var edge = blockRef.Edge;
            var changed = false;
            foreach (var target in FadeTargets(timeline, block.Clip, edge, expandLinks))
            {
                var length = TransitionBlocks.Clamp(frames, TransitionBlocks.MinFadeFrames, TransitionBlocks.MaxFade(index, target, edge));
                if (length <= 0 || length == FadeOf(target, edge)) continue;
                SetFade(target, edge, length);
                changed = true;
            }
            return changed;
        });
    }

    public static EditResult SetType(ProjectSession session, TransitionBlockRef blockRef, TransitionType type)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (blockRef.Kind != TransitionBlockKind.Cut)
            return EditResult.Fail("A fade has no transition type.");
        var transition = session.ActiveTimeline.Transitions.FirstOrDefault(t => string.Equals(t.Id, blockRef.Id, StringComparison.Ordinal));
        if (transition is null)
            return EditResult.Fail("That transition is no longer on the timeline.");
        if (EditCommands.IsClipOnLockedTrack(session.ActiveTimeline, transition.ClipIdA)
            || EditCommands.IsClipOnLockedTrack(session.ActiveTimeline, transition.ClipIdB))
            return EditResult.Fail(EditCommands.TrackLockedError);

        return Commit(session, () =>
        {
            if (transition.Type == type) return false;
            transition.Type = type;
            return true;
        });
    }

    public static EditResult SetCurve(ProjectSession session, TransitionBlockRef blockRef, Interpolation curve, bool expandLinks)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!blockRef.IsFade)
            return EditResult.Fail("A cut transition has no fade curve.");
        var timeline = session.ActiveTimeline;
        if (TransitionBlocks.Find(timeline, blockRef) is not { } block)
            return EditResult.Fail("That fade is no longer on the timeline.");
        if (timeline.Tracks[block.TrackIndex].Locked)
            return EditResult.Fail(EditCommands.TrackLockedError);

        var edge = blockRef.Edge;
        return Commit(session, () =>
        {
            var changed = false;
            foreach (var target in FadeTargets(timeline, block.Clip, edge, expandLinks))
            {
                if (edge == FadeEdge.In && target.FadeInInterpolation != curve)
                {
                    target.FadeInInterpolation = curve;
                    changed = true;
                }
                else if (edge == FadeEdge.Out && target.FadeOutInterpolation != curve)
                {
                    target.FadeOutInterpolation = curve;
                    changed = true;
                }
            }
            return changed;
        });
    }

    public static EditResult Remove(ProjectSession session, TransitionBlockRef blockRef, bool expandLinks)
    {
        ArgumentNullException.ThrowIfNull(session);
        var timeline = session.ActiveTimeline;
        if (blockRef.Kind == TransitionBlockKind.Cut)
        {
            var index = timeline.Transitions.FindIndex(t => string.Equals(t.Id, blockRef.Id, StringComparison.Ordinal));
            if (index < 0) return EditResult.Fail("That transition is no longer on the timeline.");
            var cut = timeline.Transitions[index];
            if (EditCommands.IsClipOnLockedTrack(timeline, cut.ClipIdA)
                || EditCommands.IsClipOnLockedTrack(timeline, cut.ClipIdB))
                return EditResult.Fail(EditCommands.TrackLockedError);
            return Commit(session, () =>
            {
                timeline.Transitions.RemoveAt(index);
                return true;
            });
        }

        if (TransitionBlocks.Find(timeline, blockRef) is not { } block)
            return EditResult.Fail("That fade is no longer on the timeline.");
        if (timeline.Tracks[block.TrackIndex].Locked)
            return EditResult.Fail(EditCommands.TrackLockedError);
        var edge = blockRef.Edge;
        return Commit(session, () =>
        {
            foreach (var target in FadeTargets(timeline, block.Clip, edge, expandLinks))
                SetFade(target, edge, 0);
            return true;
        });
    }

    internal static int FadeOf(Clip clip, FadeEdge edge) => edge == FadeEdge.In ? clip.FadeInFrames : clip.FadeOutFrames;

    internal static void SetFade(Clip clip, FadeEdge edge, int frames)
    {
        if (edge == FadeEdge.In) clip.FadeInFrames = frames;
        else clip.FadeOutFrames = frames;
    }

    /// <summary>
    /// One transition per cut: replaces any entry for the pair, and clears the two facing picture
    /// fades so the blend does not also dip.
    /// </summary>
    internal static Transition PlaceCut(Timeline timeline, Clip a, Clip b, TransitionType type, int frames)
    {
        timeline.Transitions.RemoveAll(t =>
            string.Equals(t.ClipIdA, a.Id, StringComparison.Ordinal)
            && string.Equals(t.ClipIdB, b.Id, StringComparison.Ordinal));
        var transition = new Transition
        {
            Id = Timeline.NewId(),
            ClipIdA = a.Id,
            ClipIdB = b.Id,
            Type = type,
            DurationFrames = frames,
        };
        timeline.Transitions.Add(transition);
        a.FadeOutFrames = 0;
        b.FadeInFrames = 0;
        return transition;
    }

    private static EditResult Commit(ProjectSession session, Func<bool> mutate)
    {
        session.RecordBeforeEdit();
        bool changed;
        try
        {
            changed = mutate();
        }
        catch
        {
            session.RollbackLastRecord();
            throw;
        }

        if (!changed)
        {
            session.RollbackLastRecord();
            return EditResult.Success();
        }

        session.MarkDirty();
        return EditResult.Success();
    }
}

/// <summary>
/// WP-252. One live block-edge drag, from an existing block (resize) or a free corner (create).
/// Records a single undo step on begin; keeps it only if the timeline ended up different.
/// Mutations inside the drag are silent (<see cref="ClipLiveEdit"/>), so the editor does not
/// redraw and end the gesture mid-drag.
/// </summary>
public sealed class TransitionDrag
{
    private readonly ProjectSession _session;
    private readonly ClipLiveEdit _live = new();
    private readonly Clip _clip;
    private readonly Clip? _clipB;
    private readonly FadeEdge _edge;
    private readonly bool _creating;
    private readonly int _max;
    private readonly List<(Clip Clip, int Original, int Max)> _fades = [];
    private readonly Transition? _existing;
    private readonly int _originalCutFrames;
    private readonly int _originalFadeOutA;
    private readonly int _originalFadeInB;
    private Transition? _placed;
    private bool _open;

    private TransitionDrag(
        ProjectSession session,
        Clip clip,
        Clip? clipB,
        FadeEdge edge,
        bool creating,
        bool rightEdge,
        int max,
        Transition? existing)
    {
        _session = session;
        _clip = clip;
        _clipB = clipB;
        _edge = edge;
        _creating = creating;
        RightEdge = rightEdge;
        _max = max;
        _existing = existing;
        _placed = existing;
        _originalCutFrames = existing?.DurationFrames ?? 0;
        _originalFadeOutA = clip.FadeOutFrames;
        _originalFadeInB = clipB?.FadeInFrames ?? 0;
    }

    public bool IsCut => _clipB is not null;

    public bool IsOpen => _open;

    /// <summary>Every clip whose block this drag can redraw: the clip, the other side of a cut, fade partners.</summary>
    public IReadOnlyList<string> ClipIds
    {
        get
        {
            var ids = new List<string> { _clip.Id };
            if (_clipB is not null) ids.Add(_clipB.Id);
            ids.AddRange(_fades.Select(f => f.Clip.Id));
            return ids.Distinct(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Which edge of the block the pointer holds. A new cut transition follows the pointer's side of the cut.</summary>
    public bool RightEdge { get; private set; }

    public int Frames { get; private set; }

    public int MinFrames => _creating ? 0 : IsCut ? TransitionBlocks.MinCutFrames : TransitionBlocks.MinFadeFrames;

    public int MaxFrames => _max;

    /// <summary>The block being drawn, or null while a new block has no length yet.</summary>
    public TransitionBlockRef? Ref => IsCut
        ? _placed is not null && _session.ActiveTimeline.Transitions.Contains(_placed) ? TransitionBlockRef.Cut(_placed.Id) : null
        : Frames > 0 ? TransitionBlockRef.Fade(_edge, _clip.Id) : null;

    /// <summary>The frame a zero-length block sits on: the clip edge for a fade, the cut for a cut transition.</summary>
    public int AnchorFrame => IsCut || _edge == FadeEdge.Out ? _clip.EndFrame : _clip.StartFrame;

    /// <summary>The frame at the dragged edge after clamping.</summary>
    public int EdgeFrame
    {
        get
        {
            if (!IsCut)
                return _edge == FadeEdge.In ? _clip.StartFrame + Frames : _clip.EndFrame - Frames;
            var cut = _clip.EndFrame;
            var start = cut - (Frames / 2);
            return RightEdge ? start + Frames : start;
        }
    }

    /// <summary>The frame the monitor shows: the first full-picture frame past the dragged edge.</summary>
    public int PreviewFrame
    {
        get
        {
            var owner = IsCut && RightEdge ? _clipB! : _clip;
            return Math.Clamp(EdgeFrame, owner.StartFrame, Math.Max(owner.StartFrame, owner.EndFrame - 1));
        }
    }

    public static TransitionDrag? BeginResize(ProjectSession session, TransitionBlockRef blockRef, bool rightEdge, bool expandLinks)
    {
        ArgumentNullException.ThrowIfNull(session);
        var timeline = session.ActiveTimeline;
        var index = new TransitionIndex(timeline);
        if (TransitionBlocks.Find(index, blockRef) is not { } block) return null;
        if (timeline.Tracks[block.TrackIndex].Locked) return null;

        TransitionDrag drag;
        if (block.Transition is { } transition && block.ClipB is { } b)
        {
            drag = new TransitionDrag(session, block.Clip, b, FadeEdge.Out, creating: false, rightEdge,
                TransitionBlocks.MaxCut(index, block.Clip, b), transition);
            drag.Frames = block.Frames;
        }
        else
        {
            var edge = blockRef.Edge;
            drag = new TransitionDrag(session, block.Clip, null, edge, creating: false, rightEdge: edge == FadeEdge.In,
                TransitionBlocks.MaxFade(index, block.Clip, edge), null);
            drag.Frames = TransitionCommands.FadeOf(block.Clip, edge);
            foreach (var target in TransitionCommands.FadeTargets(timeline, block.Clip, edge, expandLinks))
                drag._fades.Add((target, TransitionCommands.FadeOf(target, edge), TransitionBlocks.MaxFade(index, target, edge)));
        }

        drag.Open();
        return drag;
    }

    public static TransitionDrag? BeginCreate(ProjectSession session, ClipCorner corner, bool expandLinks)
    {
        ArgumentNullException.ThrowIfNull(session);
        var timeline = session.ActiveTimeline;
        if (corner.TrackIndex >= 0 && corner.TrackIndex < timeline.Tracks.Count && timeline.Tracks[corner.TrackIndex].Locked)
            return null;
        var index = new TransitionIndex(timeline);
        if (!TransitionBlocks.IsFree(index, corner)) return null;

        TransitionDrag drag;
        if (corner.Pair is { } pair)
        {
            drag = new TransitionDrag(session, pair.A, pair.B, FadeEdge.Out, creating: true, rightEdge: corner.Edge == FadeEdge.In,
                TransitionBlocks.MaxCut(index, pair.A, pair.B), null);
        }
        else
        {
            drag = new TransitionDrag(session, corner.Clip, null, corner.Edge, creating: true, rightEdge: corner.Edge == FadeEdge.In,
                TransitionBlocks.MaxFade(index, corner.Clip, corner.Edge), null);
            foreach (var target in TransitionCommands.FadeTargets(timeline, corner.Clip, corner.Edge, expandLinks))
                drag._fades.Add((target, TransitionCommands.FadeOf(target, corner.Edge), TransitionBlocks.MaxFade(index, target, corner.Edge)));
        }

        drag.Open();
        return drag;
    }

    /// <summary>Moves the held edge to <paramref name="edgeFrame"/>; returns the clamped length.</summary>
    public int DragTo(int edgeFrame)
    {
        if (!_open) return Frames;
        if (!IsCut)
            return Apply(TransitionBlocks.FadeFramesAt(_clip, _edge, edgeFrame));

        var cut = _clip.EndFrame;
        if (_creating)
        {
            if (edgeFrame != cut) RightEdge = edgeFrame > cut;
            return Apply(TransitionBlocks.CutFramesAt(cut, edgeFrame));
        }

        var offset = RightEdge ? edgeFrame - cut : cut - edgeFrame;
        return Apply(2 * offset);
    }

    /// <summary>Sets the length directly, clamped as a drag would be.</summary>
    public int Apply(int frames)
    {
        if (!_open) return Frames;
        var length = TransitionBlocks.Clamp(frames, MinFrames, _max);
        if (IsCut && _creating && length < TransitionBlocks.MinCutFrames) length = 0;
        if (length == Frames) return Frames;

        _live.Mutate(() =>
        {
            if (IsCut) ApplyCut(length);
            else ApplyFades(length);
        });
        Frames = length;
        return Frames;
    }

    /// <summary>Ends the drag. True when it left one undo step behind.</summary>
    public bool Commit()
    {
        if (!_open) return false;
        _open = false;
        var keep = Changed();
        _live.End(keep);
        return keep;
    }

    public void Cancel()
    {
        if (!_open) return;
        _open = false;
        _live.End(keep: false);
    }

    private void Open()
    {
        _live.Begin(_session);
        _open = true;
    }

    private void ApplyFades(int length)
    {
        foreach (var (clip, _, max) in _fades)
        {
            var target = length <= 0 ? 0 : TransitionBlocks.Clamp(length, TransitionBlocks.MinFadeFrames, max);
            TransitionCommands.SetFade(clip, _edge, target);
        }
    }

    private void ApplyCut(int length)
    {
        var timeline = _session.ActiveTimeline;
        if (length < TransitionBlocks.MinCutFrames)
        {
            // Only a new transition can shrink to nothing: take it out and give back the fades it cleared.
            if (_placed is not null && _existing is null)
            {
                timeline.Transitions.Remove(_placed);
                _placed = null;
                _clip.FadeOutFrames = _originalFadeOutA;
                _clipB!.FadeInFrames = _originalFadeInB;
            }
            return;
        }

        if (_placed is null || !timeline.Transitions.Contains(_placed))
            _placed = TransitionCommands.PlaceCut(timeline, _clip, _clipB!, TransitionBlocks.DefaultType, length);
        else
            _placed.DurationFrames = length;
    }

    private bool Changed()
    {
        if (IsCut)
        {
            if (_existing is null) return _placed is not null && _session.ActiveTimeline.Transitions.Contains(_placed);
            return _existing.DurationFrames != _originalCutFrames;
        }
        return _fades.Any(f => TransitionCommands.FadeOf(f.Clip, _edge) != f.Original);
    }
}
