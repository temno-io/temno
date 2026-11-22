using Temno.Core.Models;
using Temno.Core.Editing;

namespace Temno.Core.Session;

public static partial class EditCommands
{
    /// <summary>
    /// Clamps a track insertion index so visual tracks always sit above every audio track
    /// (Mac <c>EditorViewModel.partitionedInsertionIndex</c>). Default is index 0 (top of the visual
    /// stack, e.g. V3 above V2) for visual tracks and the end of the list (e.g. A3 below A2) for audio.
    /// </summary>
    public static int PartitionedInsertionIndex(Timeline timeline, ClipType type, int? requestedIndex = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var count = timeline.Tracks.Count;
        var firstAudio = timeline.Tracks.FindIndex(IsAudioTrack);
        if (firstAudio < 0) firstAudio = count;
        if (type == ClipType.audio)
            return Math.Clamp(requestedIndex ?? count, firstAudio, count);
        return Math.Clamp(requestedIndex ?? 0, 0, firstAudio);
    }

    public static EditResult AddTrack(ProjectSession session, ClipType type, int? insertIndex = null)
        => AddTrack(session, type, insertIndex, out _);

    public static EditResult AddTrack(
        ProjectSession session,
        ClipType type,
        int? insertIndex,
        out string? createdTrackId)
    {
        createdTrackId = null;
        if (!type.IsTimelinePlaceable())
            return EditResult.Fail($"track type {type} is not supported on the timeline");

        string? id = null;
        var result = ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            var clamped = PartitionedInsertionIndex(timeline, type, insertIndex);
            var track = new Track { Type = type };
            timeline.Tracks.Insert(clamped, track);
            id = track.Id;
            session.MarkDirty();
            return EditResult.Success();
        });
        createdTrackId = result.Ok ? id : null;
        return result;
    }

    public static EditResult RemoveTrack(ProjectSession session, string trackId)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            return EditResult.Fail("trackId is required");

        var tracks = session.ActiveTimeline.Tracks;
        var target = tracks.FirstOrDefault(t => string.Equals(t.Id, trackId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return EditResult.Fail($"track not found: {trackId}");
        if (tracks.Count <= 1)
            return EditResult.Fail("cannot remove last track");
        if (target.Locked)
            return EditResult.Fail("Track is locked — unlock it before removing.");
        if (target.Clips.Any(c => !string.IsNullOrEmpty(c.MulticamGroupId)))
            return EditResult.Fail("A multicam group's track can't be removed — delete the group's clips first and the empty track prunes itself.");

        return ExecuteWithUndo(session, () =>
        {
            var list = session.ActiveTimeline.Tracks;
            if (list.Count <= 1)
                return EditResult.Fail("cannot remove last track");
            var removedClipIds = list
                .Where(t => string.Equals(t.Id, trackId, StringComparison.OrdinalIgnoreCase))
                .SelectMany(t => t.Clips)
                .Select(c => c.Id)
                .ToHashSet(StringComparer.Ordinal);
            var removed = list.RemoveAll(t => string.Equals(t.Id, trackId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return EditResult.Fail($"track not found: {trackId}");
            PruneTransitionsForClips(session.ActiveTimeline, removedClipIds);
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    private static void PruneTransitionsForClips(Timeline timeline, HashSet<string> removedClipIds)
    {
        if (removedClipIds.Count == 0 || timeline.Transitions.Count == 0) return;
        timeline.Transitions.RemoveAll(tr =>
            removedClipIds.Contains(tr.ClipIdA) || removedClipIds.Contains(tr.ClipIdB));
    }

    /// <summary>
    /// Inserts a new track in its type zone and places <paramref name="clip"/> (plus optional linked
    /// audio partner) on it in one undo step. Used when dragging media past the top/bottom track edge.
    /// </summary>
    public static EditResult AddMediaToNewTrack(
        ProjectSession session,
        ClipType trackType,
        int? insertIndex,
        Clip clip,
        bool linkAudio,
        out int createdTrackIndex,
        out int audioTrackIndex)
    {
        createdTrackIndex = -1;
        audioTrackIndex = -1;
        if (!trackType.IsTimelinePlaceable())
            return EditResult.Fail($"track type {trackType} is not supported on the timeline");
        var probe = new Track { Type = trackType };
        if (!TrackAccepts(probe, clip.MediaType))
            return EditResult.Fail($"clip type {clip.MediaType} does not belong on a {trackType} track");

        var resolvedTrack = -1;
        var resolvedAudio = -1;
        var result = ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            var at = PartitionedInsertionIndex(timeline, trackType, insertIndex);
            var created = new Track { Type = trackType };
            timeline.Tracks.Insert(at, created);
            PlaceClip(created, clip, overwrite: false, ripple: false);
            resolvedTrack = at;

            if (linkAudio && clip.MediaType == ClipType.video)
            {
                var partner = CreateLinkedAudioPartner(clip);
                resolvedAudio = ResolveLinkedAudioTrackIndex(
                    timeline, at, (partner.StartFrame, partner.EndFrame));
                PlaceClip(timeline.Tracks[resolvedAudio], partner, overwrite: false, ripple: false);
            }

            session.MarkDirty();
            return EditResult.Success();
        });
        createdTrackIndex = result.Ok ? resolvedTrack : -1;
        audioTrackIndex = result.Ok ? resolvedAudio : -1;
        return result;
    }

    /// <summary>
    /// Inserts a new track in its type zone and moves <paramref name="newTrackClips"/> onto it (while
    /// keeping <paramref name="existingMoves"/> on their shifted existing tracks) in a single undo step.
    /// </summary>
    public static EditResult MoveClipsToNewTrack(
        ProjectSession session,
        IReadOnlyList<ClipMove> existingMoves,
        IReadOnlyList<(string ClipId, int NewStartFrame)> newTrackClips,
        ClipType newTrackType,
        int? insertIndex,
        bool overwrite = false)
    {
        if (newTrackClips is null || newTrackClips.Count == 0)
            return EditResult.Fail("at least one clip is required for the new track");
        if (!newTrackType.IsTimelinePlaceable())
            return EditResult.Fail($"track type {newTrackType} is not supported on the timeline");

        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            var newIdx = PartitionedInsertionIndex(timeline, newTrackType, insertIndex);
            timeline.Tracks.Insert(newIdx, new Track { Type = newTrackType });

            var combined = new List<ClipMove>(newTrackClips.Count + (existingMoves?.Count ?? 0));
            foreach (var (clipId, startFrame) in newTrackClips)
                combined.Add(new ClipMove(clipId, newIdx, startFrame));
            if (existingMoves is not null)
            {
                foreach (var move in existingMoves)
                {
                    var shifted = move.TrackIndex >= newIdx ? move.TrackIndex + 1 : move.TrackIndex;
                    combined.Add(new ClipMove(move.ClipId, shifted, move.NewStartFrame));
                }
            }

            if (combined.Any(m => string.IsNullOrWhiteSpace(m.ClipId)))
                return EditResult.Fail("clipId is required for every move");
            if (combined.Select(m => m.ClipId).Distinct(StringComparer.Ordinal).Count() != combined.Count)
                return EditResult.Fail("each clip can appear only once in a grouped move");

            var moved = ApplyMovesInPlace(timeline, combined, overwrite);
            if (!moved.Ok) return moved;

            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>One selected clip participating in a drag-to-new-track gesture.</summary>
    public readonly record struct NewTrackDragItem(string ClipId, int TrackIndex, ClipType MediaType, int NewStartFrame);

    /// <summary>Planned placement for one clip in a drag-to-new-track gesture.</summary>
    /// <param name="LandsOnNewTrack">True when the clip moves onto the newly inserted track.</param>
    /// <param name="PreInsertTrackIndex">
    /// Track index in the pre-insert timeline (for ghost preview and <see cref="MoveClipsToNewTrack"/>
    /// <c>existingMoves</c>); equals <c>-1</c> when <paramref name="LandsOnNewTrack"/> is true.
    /// </param>
    /// <param name="PostInsertTrackIndex">Exact track index after inserting the new track.</param>
    public readonly record struct NewTrackDragAssignment(
        string ClipId,
        bool LandsOnNewTrack,
        int PreInsertTrackIndex,
        int PostInsertTrackIndex,
        int NewStartFrame);

    /// <summary>
    /// Computes how a single or multi-track selection shifts when dragged onto a new top visual
    /// track or bottom audio track: same-zone selections shift rigidly to preserve stacking order,
    /// while cross-zone linked companions stay on their original (index-shifted) tracks.
    /// </summary>
    public static IReadOnlyList<NewTrackDragAssignment> PlanMoveToNewTrack(
        Timeline timeline,
        IReadOnlyList<NewTrackDragItem> items,
        int primaryTrackIndex,
        ClipType newTrackType,
        int? insertIndex)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (items is null || items.Count == 0) return [];

        var newIdx = PartitionedInsertionIndex(timeline, newTrackType, insertIndex);
        var isAudioZone = newTrackType == ClipType.audio;
        var probeNewTrack = new Track { Type = newTrackType };

        Track TrackAtPostIndex(int postIndex)
            => postIndex == newIdx
                ? probeNewTrack
                : timeline.Tracks[postIndex > newIdx ? postIndex - 1 : postIndex];

        bool IsSameZone(NewTrackDragItem item) => (item.MediaType == ClipType.audio) == isAudioZone;

        var sameZone = items.Where(IsSameZone).ToList();
        var postCount = timeline.Tracks.Count + 1;
        Dictionary<string, int>? rigidPostIndex = null;

        if (sameZone.Count > 0)
        {
            int? anchor = null;
            if (!isAudioZone && newIdx == 0)
                anchor = sameZone.Min(i => i.TrackIndex);
            else if (isAudioZone && newIdx == timeline.Tracks.Count)
                anchor = sameZone.Max(i => i.TrackIndex);

            if (anchor is int anchorTrack)
            {
                var candidate = new Dictionary<string, int>(sameZone.Count, StringComparer.Ordinal);
                var valid = true;
                foreach (var item in sameZone)
                {
                    var post = !isAudioZone
                        ? item.TrackIndex - anchorTrack
                        : newIdx - (anchorTrack - item.TrackIndex);
                    if (post < 0 || post >= postCount)
                    {
                        valid = false;
                        break;
                    }
                    var destTrack = TrackAtPostIndex(post);
                    if (destTrack.Locked || !TrackAccepts(destTrack, item.MediaType))
                    {
                        valid = false;
                        break;
                    }
                    candidate[item.ClipId] = post;
                }
                if (valid && candidate.Values.Contains(newIdx))
                    rigidPostIndex = candidate;
            }
        }

        var plan = new List<NewTrackDragAssignment>(items.Count);
        foreach (var item in items)
        {
            int post;
            if (rigidPostIndex is not null && rigidPostIndex.TryGetValue(item.ClipId, out var plannedPost))
            {
                post = plannedPost;
            }
            else if (item.TrackIndex == primaryTrackIndex && TrackAccepts(probeNewTrack, item.MediaType))
            {
                post = newIdx;
            }
            else
            {
                post = item.TrackIndex >= newIdx ? item.TrackIndex + 1 : item.TrackIndex;
            }

            var landsOnNew = post == newIdx;
            var pre = landsOnNew ? -1 : (post > newIdx ? post - 1 : post);
            plan.Add(new NewTrackDragAssignment(item.ClipId, landsOnNew, pre, post, item.NewStartFrame));
        }

        return plan;
    }

    public static EditResult ManageTracks(
        ProjectSession session,
        string action,
        ClipType? type = null,
        int? trackIndex = null)
    {
        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            switch (action)
            {
                case "add":
                {
                    var trackType = type ?? ClipType.video;
                    if (!trackType.IsTimelinePlaceable())
                        return EditResult.Fail($"track type {trackType} is not supported on the timeline");
                    var insertAt = PartitionedInsertionIndex(timeline, trackType, trackIndex);
                    timeline.Tracks.Insert(insertAt, new Track { Type = trackType });
                    break;
                }
                case "remove":
                {
                    if (trackIndex is null || trackIndex < 0 || trackIndex >= timeline.Tracks.Count)
                        return EditResult.Fail("invalid trackIndex");
                    if (timeline.Tracks.Count <= 1)
                        return EditResult.Fail("cannot remove last track");
                    var target = timeline.Tracks[trackIndex.Value];
                    if (target.Locked)
                        return EditResult.Fail("Track is locked — unlock it before removing.");
                    if (target.Clips.Any(c => !string.IsNullOrEmpty(c.MulticamGroupId)))
                        return EditResult.Fail("A multicam group's track can't be removed — delete the group's clips first and the empty track prunes itself.");
                    var removedClipIds = target.Clips.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                    timeline.Tracks.RemoveAt(trackIndex.Value);
                    PruneTransitionsForClips(timeline, removedClipIds);
                    break;
                }
                default:
                    return EditResult.Fail($"unknown action: {action}");
            }
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static EditResult SetTrackFlags(
        ProjectSession session,
        string trackId,
        bool? muted = null,
        bool? hidden = null,
        bool? syncLocked = null,
        bool? locked = null)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            return EditResult.Fail("trackId is required");
        if (muted is null && hidden is null && syncLocked is null && locked is null)
            return EditResult.Fail("at least one track flag is required");

        var track = session.ActiveTimeline.Tracks.FirstOrDefault(candidate => candidate.Id == trackId);
        if (track is null)
            return EditResult.Fail($"track not found: {trackId}");

        var changed = (muted is { } nextMuted && track.Muted != nextMuted)
            || (hidden is { } nextHidden && track.Hidden != nextHidden)
            || (syncLocked is { } nextSyncLocked && track.SyncLocked != nextSyncLocked)
            || (locked is { } nextLocked && track.Locked != nextLocked);
        if (!changed)
            return EditResult.Success();

        return ExecuteWithUndo(session, () =>
        {
            var target = session.ActiveTimeline.Tracks.FirstOrDefault(candidate => candidate.Id == trackId);
            if (target is null)
                return EditResult.Fail($"track not found: {trackId}");

            if (muted is { } nextMuted) target.Muted = nextMuted;
            if (hidden is { } nextHidden) target.Hidden = nextHidden;
            if (syncLocked is { } nextSyncLocked) target.SyncLocked = nextSyncLocked;
            if (locked is { } nextLocked) target.Locked = nextLocked;
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static EditResult SetTrackHeight(ProjectSession session, string trackId, double height)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            return EditResult.Fail("trackId is required");
        if (!double.IsFinite(height))
            return EditResult.Fail("track height must be finite");

        var track = session.ActiveTimeline.Tracks.FirstOrDefault(candidate => candidate.Id == trackId);
        if (track is null)
            return EditResult.Fail($"track not found: {trackId}");

        var clampedHeight = TimelineGeometry.ClampTrackHeight(height);
        if (Math.Abs(track.DisplayHeight - clampedHeight) < 0.001)
            return EditResult.Success();

        return ExecuteWithUndo(session, () =>
        {
            var target = session.ActiveTimeline.Tracks.FirstOrDefault(candidate => candidate.Id == trackId);
            if (target is null)
                return EditResult.Fail($"track not found: {trackId}");

            target.DisplayHeight = clampedHeight;
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>Reorders one track without allowing visual and audio track zones to cross.</summary>
    public static EditResult ReorderTrack(ProjectSession session, string trackId, int targetIndex)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            return EditResult.Fail("trackId is required");

        var tracks = session.ActiveTimeline.Tracks;
        var sourceIndex = tracks.FindIndex(track => track.Id == trackId);
        if (sourceIndex < 0)
            return EditResult.Fail($"track not found: {trackId}");
        if (targetIndex < 0 || targetIndex >= tracks.Count)
            return EditResult.Fail("targetIndex out of range");

        var moving = tracks[sourceIndex];
        var remaining = tracks.Where((_, index) => index != sourceIndex).ToList();
        var firstAudioIndex = remaining.FindIndex(IsAudioTrack);
        if (firstAudioIndex < 0) firstAudioIndex = remaining.Count;
        var destinationIndex = IsAudioTrack(moving)
            ? Math.Clamp(targetIndex, firstAudioIndex, remaining.Count)
            : Math.Clamp(targetIndex, 0, firstAudioIndex);

        if (sourceIndex == destinationIndex)
            return EditResult.Success();

        return ExecuteWithUndo(session, () =>
        {
            var currentTracks = session.ActiveTimeline.Tracks;
            var currentSourceIndex = currentTracks.FindIndex(track => track.Id == trackId);
            if (currentSourceIndex < 0)
                return EditResult.Fail($"track not found: {trackId}");

            var track = currentTracks[currentSourceIndex];
            currentTracks.RemoveAt(currentSourceIndex);
            currentTracks.Insert(destinationIndex, track);
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>Mac <c>manage_tracks</c>: reorder, set flags, and remove in one undo step.</summary>
    public static EditResult ApplyTrackEdits(
        ProjectSession session,
        IReadOnlyList<(string Id, int To)> reorders,
        IReadOnlyList<(string Id, bool? Muted, bool? Hidden, bool? SyncLocked)> flagSets,
        IReadOnlyList<string> removeIds,
        IReadOnlyList<(string Id, bool Locked)>? lockSets = null)
    {
        if ((reorders is null || reorders.Count == 0)
            && (flagSets is null || flagSets.Count == 0)
            && (lockSets is null || lockSets.Count == 0)
            && (removeIds is null || removeIds.Count == 0))
            return EditResult.Fail("Nothing to do — pass at least one of reorder, set, remove.");

        var tracks = session.ActiveTimeline.Tracks;
        var multicamIds = tracks
            .Where(t => t.Clips.Any(c => !string.IsNullOrEmpty(c.MulticamGroupId)))
            .Select(t => t.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (removeIds is { Count: > 0 } && removeIds.Any(multicamIds.Contains))
            return EditResult.Fail("A multicam group's track can't be removed — delete the group's clips first (remove_clips) and the empty track prunes itself.");
        if (flagSets is { Count: > 0 }
            && flagSets.Any(f => multicamIds.Contains(f.Id) && f.SyncLocked == false))
            return EditResult.Fail("Sync lock stays on for a multicam group's tracks — unlocking would let ripples shift the group's members apart.");

        return ExecuteWithUndo(session, () =>
        {
            var list = session.ActiveTimeline.Tracks;
            if (reorders is { Count: > 0 })
            {
                foreach (var r in reorders)
                {
                    var from = list.FindIndex(t => string.Equals(t.Id, r.Id, StringComparison.OrdinalIgnoreCase));
                    if (from < 0)
                        return EditResult.Fail($"track not found: {r.Id}");
                    if (r.To < 0 || r.To >= list.Count)
                        return EditResult.Fail($"destination index {r.To} is outside the track's type zone");
                    if (list[from].Type != list[r.To].Type && from != r.To)
                        return EditResult.Fail($"destination index {r.To} is outside the track's type zone");
                    var moving = list[from];
                    list.RemoveAt(from);
                    var dest = Math.Clamp(r.To, 0, list.Count);
                    list.Insert(dest, moving);
                }
            }

            if (flagSets is { Count: > 0 })
            {
                foreach (var f in flagSets)
                {
                    var target = list.FirstOrDefault(t => string.Equals(t.Id, f.Id, StringComparison.OrdinalIgnoreCase));
                    if (target is null)
                        return EditResult.Fail($"track not found: {f.Id}");
                    if (f.Muted is { } m) target.Muted = m;
                    if (f.Hidden is { } h) target.Hidden = h;
                    if (f.SyncLocked is { } s) target.SyncLocked = s;
                }
            }

            if (lockSets is { Count: > 0 })
            {
                foreach (var l in lockSets)
                {
                    var target = list.FirstOrDefault(t => string.Equals(t.Id, l.Id, StringComparison.OrdinalIgnoreCase));
                    if (target is null)
                        return EditResult.Fail($"track not found: {l.Id}");
                    target.Locked = l.Locked;
                }
            }

            if (removeIds is { Count: > 0 })
            {
                var remove = removeIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (list.Any(t => remove.Contains(t.Id) && t.Locked))
                    return EditResult.Fail("Track is locked — unlock it before removing.");
                if (list.Count - list.Count(t => remove.Contains(t.Id)) < 1)
                    return EditResult.Fail("cannot remove last track");
                var removedClipIds = list
                    .Where(t => remove.Contains(t.Id))
                    .SelectMany(t => t.Clips)
                    .Select(c => c.Id)
                    .ToHashSet(StringComparer.Ordinal);
                list.RemoveAll(t => remove.Contains(t.Id));
                PruneTransitionsForClips(session.ActiveTimeline, removedClipIds);
            }

            session.MarkDirty();
            return EditResult.Success();
        });
    }

    private static bool IsAudioTrack(Track track) => track.Type == ClipType.audio;

    /// <summary>Assigns a new shared <see cref="Clip.LinkGroupId"/> to every listed clip (merges prior groups).</summary>
    public static EditResult LinkClips(ProjectSession session, IReadOnlyList<string> clipIds)
    {
        if (clipIds is null || clipIds.Count < 2)
            return EditResult.Fail("at least two clipIds are required to link");
        var unique = clipIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        if (unique.Count < 2)
            return EditResult.Fail("at least two distinct clipIds are required to link");

        var timeline = session.ActiveTimeline;
        var found = 0;
        foreach (var track in timeline.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (!unique.Contains(clip.Id, StringComparer.Ordinal)) continue;
                if (track.Locked)
                    return EditResult.Fail(TrackLockedError);
                found++;
            }
        }
        if (found != unique.Count)
            return EditResult.Fail("one or more clipIds were not found");

        return ExecuteWithUndo(session, () =>
        {
            var groupId = Guid.NewGuid().ToString().ToUpperInvariant();
            var idSet = unique.ToHashSet(StringComparer.Ordinal);
            foreach (var track in session.ActiveTimeline.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    if (idSet.Contains(clip.Id))
                        clip.LinkGroupId = groupId;
                }
            }
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>Clears <see cref="Clip.LinkGroupId"/> on every unlocked clip that shares a group with any listed id.</summary>
    public static EditResult UnlinkClips(ProjectSession session, IReadOnlyList<string> clipIds)
    {
        if (clipIds is null || clipIds.Count == 0)
            return EditResult.Fail("at least one clipId is required");

        if (clipIds.Any(id => IsClipOnLockedTrack(session.ActiveTimeline, id)))
            return EditResult.Fail(TrackLockedError);

        var expanded = LinkGroup.Expand(session.ActiveTimeline, clipIds);
        var anyLinked = session.ActiveTimeline.Tracks
            .Where(t => !t.Locked)
            .SelectMany(t => t.Clips)
            .Any(c => expanded.Contains(c.Id) && !string.IsNullOrEmpty(c.LinkGroupId));
        if (!anyLinked)
            return EditResult.Success();

        return ExecuteWithUndo(session, () =>
        {
            var set = LinkGroup.Expand(session.ActiveTimeline, clipIds);
            foreach (var track in session.ActiveTimeline.Tracks)
            {
                if (track.Locked) continue;
                foreach (var clip in track.Clips)
                {
                    if (set.Contains(clip.Id))
                        clip.LinkGroupId = null;
                }
            }
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>One video clip's repair outcome from <see cref="RestoreLinkedAudio"/>.</summary>
    public sealed record RestoredLinkedAudio(string ClipId, string AudioClipId, int AudioTrackIndex);

    /// <summary>One video clip <see cref="RestoreLinkedAudio"/> deliberately left alone, and why.</summary>
    public sealed record SkippedLinkedAudio(string ClipId, string Reason);

    /// <summary>
    /// Repair path for timelines built before WP-157: gives existing video clips the linked audio
    /// partner <c>add_clips</c> now creates going forward (WP-162). For every listed clip on a video
    /// track whose manifest entry has audio and which has no audio-track clip sharing its link group,
    /// places a partner with the same occupancy-aware rule as the tool path, in one undo step.
    /// Never overwrites: partners always place with <c>overwrite: false</c> on a track that is free
    /// for their range. Clips that need nothing are reported in <c>Skipped</c>, not failed.
    /// </summary>
    public static (IReadOnlyList<RestoredLinkedAudio> Restored, IReadOnlyList<SkippedLinkedAudio> Skipped)
        RestoreLinkedAudio(ProjectSession session, IReadOnlyList<string> clipIds)
    {
        var restored = new List<RestoredLinkedAudio>();
        var skipped = new List<SkippedLinkedAudio>();
        if (clipIds is null || clipIds.Count == 0)
            return (restored, skipped);

        session.RecordBeforeEdit();
        var timeline = session.ActiveTimeline;
        foreach (var clipId in clipIds.Distinct(StringComparer.Ordinal))
        {
            var found = timeline.Tracks
                .SelectMany((track, index) => track.Clips.Select(clip => (Clip: clip, TrackIndex: index)))
                .FirstOrDefault(pair => string.Equals(pair.Clip.Id, clipId, StringComparison.Ordinal));
            if (found.Clip is null)
            {
                skipped.Add(new SkippedLinkedAudio(clipId, "clip not found"));
                continue;
            }

            var videoTrack = timeline.Tracks[found.TrackIndex];
            if (videoTrack.Locked)
            {
                skipped.Add(new SkippedLinkedAudio(clipId, "track is locked"));
                continue;
            }
            if (videoTrack.Type != ClipType.video || found.Clip.MediaType != ClipType.video)
            {
                skipped.Add(new SkippedLinkedAudio(clipId, "not a video clip on a video track"));
                continue;
            }

            if (!SourceHasAudio(session, found.Clip))
            {
                skipped.Add(new SkippedLinkedAudio(clipId, "source has no audio"));
                continue;
            }

            if (!string.IsNullOrEmpty(found.Clip.LinkGroupId)
                && timeline.Tracks.Any(t => t.Type == ClipType.audio
                    && t.Clips.Any(c => c.LinkGroupId == found.Clip.LinkGroupId)))
            {
                skipped.Add(new SkippedLinkedAudio(clipId, "already has a linked audio clip"));
                continue;
            }

            var partner = CreateLinkedAudioPartner(found.Clip);
            var audioTrackIndex = ResolveLinkedAudioTrackIndex(
                timeline, found.TrackIndex, (partner.StartFrame, partner.EndFrame));
            PlaceClip(timeline.Tracks[audioTrackIndex], partner, overwrite: false, ripple: false);
            restored.Add(new RestoredLinkedAudio(found.Clip.Id, partner.Id, audioTrackIndex));
        }

        if (restored.Count == 0)
        {
            session.RollbackLastRecord();
            return (restored, skipped);
        }

        session.MarkDirty();
        return (restored, skipped);
    }
}
