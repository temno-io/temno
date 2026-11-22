using Temno.Core.Models;
using Temno.Core.Editing;

namespace Temno.Core.Session;

public static partial class EditCommands
{
    public static EditResult AddClip(
        ProjectSession session,
        int trackIndex,
        Clip clip,
        bool overwrite = false,
        bool ripple = false)
    {
        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            if (trackIndex < 0 || trackIndex >= timeline.Tracks.Count)
                return EditResult.Fail($"trackIndex {trackIndex} out of range");

            var track = timeline.Tracks[trackIndex];
            if (track.Locked)
                return EditResult.Fail(TrackLockedError);
            if (!TrackAccepts(track, clip.MediaType))
                return EditResult.Fail($"clip type {clip.MediaType} does not belong on a {track.Type} track");

            PlaceClip(track, clip, overwrite, ripple);
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>
    /// Drop helper: place video (or image) on a video track and, when <paramref name="linkAudio"/> is true,
    /// a linked audio clip of the same media on an audio track (created if needed). Single undo step.
    /// </summary>
    public static EditResult AddLinkedMediaDrop(
        ProjectSession session,
        int videoTrackIndex,
        Clip videoClip,
        bool linkAudio,
        out int audioTrackIndex,
        bool overwrite = false,
        bool ripple = false)
    {
        var resolvedAudioTrack = -1;
        var result = ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            if (videoTrackIndex < 0 || videoTrackIndex >= timeline.Tracks.Count)
                return EditResult.Fail($"trackIndex {videoTrackIndex} out of range");
            if (timeline.Tracks[videoTrackIndex].Locked)
                return EditResult.Fail(TrackLockedError);
            if (!TrackAccepts(timeline.Tracks[videoTrackIndex], videoClip.MediaType))
                return EditResult.Fail(
                    $"clip type {videoClip.MediaType} does not belong on a {timeline.Tracks[videoTrackIndex].Type} track");

            void Place(int trackIdx, Clip clip)
                => PlaceClip(timeline.Tracks[trackIdx], clip, overwrite, ripple);

            Place(videoTrackIndex, videoClip);

            if (linkAudio && videoClip.MediaType == ClipType.video)
            {
                // Explicit opt-in: this caller is the drag-drop, which has already decided. The
                // manifest HasAudio gate belongs to the tool path (WP-157), not here — adding it
                // would change what a drop does.
                var partner = CreateLinkedAudioPartner(videoClip);
                resolvedAudioTrack = ResolveLinkedAudioTrackIndex(
                    timeline, videoTrackIndex, (partner.StartFrame, partner.EndFrame));
                Place(resolvedAudioTrack, partner);
            }

            session.MarkDirty();
            return EditResult.Success();
        });
        audioTrackIndex = resolvedAudioTrack;
        return result;
    }

    /// <summary>
    /// The audio track a linked partner lands on: the first audio track below the video track, else
    /// the first audio track anywhere, else a new one. Mirrors Mac <c>add_clips</c> — "an existing one
    /// if available, otherwise a new one" (<c>ToolDefinitions.swift:287</c>).
    /// </summary>
    /// <param name="freeRange">
    /// When set, a track only qualifies if nothing already occupies <c>[Start, End)</c> on it — Mac
    /// <c>availableAudioTrackIndex</c> (<c>EditorViewModel+Linking.swift:211</c>), which returns the
    /// first non-conflicting audio track and otherwise appends one.
    /// <para>
    /// WP-157's first implementation was occupancy-blind, and MCP <c>add_clips</c> places with
    /// <c>overwrite: true</c>. On the default V/V/A/A layout both video tracks resolved to Audio 1, so
    /// a partner could silently delete music the user had already put there, or delete the partner of
    /// another clip in the same batch. Found by the WP-157 T1 spot-check (Grok 4.6 xhigh, 2026-09-04).
    /// Pass null only where the caller has already made room — <c>InsertClips</c> ripples the track
    /// open before it places anything.
    /// </para>
    /// </param>
    private static int ResolveLinkedAudioTrackIndex(
        Timeline timeline,
        int videoTrackIndex,
        (int Start, int End)? freeRange)
    {
        bool Qualifies(Track track)
        {
            if (track.Type != ClipType.audio || track.Locked) return false;
            if (freeRange is not { } range) return true;
            return !track.Clips.Any(c => c.EndFrame > range.Start && c.StartFrame < range.End);
        }

        for (var i = videoTrackIndex + 1; i < timeline.Tracks.Count; i++)
        {
            if (Qualifies(timeline.Tracks[i])) return i;
        }

        for (var i = 0; i < timeline.Tracks.Count; i++)
        {
            if (Qualifies(timeline.Tracks[i])) return i;
        }

        timeline.Tracks.Add(new Track { Type = ClipType.audio });
        return timeline.Tracks.Count - 1;
    }

    /// <summary>
    /// Builds the audio half of a linked A/V pair and stamps the shared <see cref="Clip.LinkGroupId"/>
    /// onto <paramref name="videoClip"/>. One construction site so the drag-drop and tool paths cannot
    /// drift apart (WP-157).
    /// </summary>
    private static Clip CreateLinkedAudioPartner(Clip videoClip)
    {
        var linkId = string.IsNullOrEmpty(videoClip.LinkGroupId)
            ? Timeline.NewId()
            : videoClip.LinkGroupId;
        videoClip.LinkGroupId = linkId;
        return new Clip
        {
            MediaRef = videoClip.MediaRef,
            MediaType = ClipType.audio,
            SourceClipType = ClipType.video,
            StartFrame = videoClip.StartFrame,
            DurationFrames = videoClip.DurationFrames,
            LinkGroupId = linkId,
            TrimStartFrame = videoClip.TrimStartFrame,
            TrimEndFrame = videoClip.TrimEndFrame,
            Speed = videoClip.Speed,
        };
    }

    /// <summary>
    /// Mac contract: a video asset *with audio* spawns the partner. A silent video must not get a
    /// clip that decodes to nothing, so the manifest is the gate, not the clip.
    /// </summary>
    private static bool SourceHasAudio(ProjectSession session, Clip clip)
        => clip.MediaType == ClipType.video
            && session.Manifest.Entries.FirstOrDefault(e => e.Id == clip.MediaRef)?.HasAudio == true;

    public static EditResult RemoveClips(
        ProjectSession session,
        IReadOnlyList<string> clipIds,
        bool ripple,
        bool expandLinks = true)
    {
        return ExecuteWithUndo(session, () =>
        {
            var idSet = expandLinks
                ? LinkGroup.Expand(session.ActiveTimeline, clipIds)
                : clipIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
            var timeline = session.ActiveTimeline;
            var matchedLocked = false;
            var unlockedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var track in timeline.Tracks)
            {
                foreach (var clip in track.Clips)
                {
                    if (!idSet.Contains(clip.Id)) continue;
                    if (track.Locked) matchedLocked = true;
                    else unlockedIds.Add(clip.Id);
                }
            }
            if (unlockedIds.Count == 0 && matchedLocked)
                return EditResult.Fail(TrackLockedError);

            var any = false;
            foreach (var track in timeline.Tracks)
            {
                if (track.Locked) continue;
                if (ripple)
                {
                    var spans = track.Clips
                        .Select(c => (c.Id, c.StartFrame, c.EndFrame))
                        .ToList();
                    var shifts = RippleEngine.ComputeRippleShifts(spans, unlockedIds);
                    track.Clips.RemoveAll(c => unlockedIds.Contains(c.Id));
                    foreach (var shift in shifts)
                    {
                        var clip = track.Clips.FirstOrDefault(c => c.Id == shift.ClipId);
                        if (clip is not null)
                            clip.StartFrame = shift.NewStartFrame;
                    }
                }
                else
                {
                    var before = track.Clips.Count;
                    track.Clips.RemoveAll(c => unlockedIds.Contains(c.Id));
                    if (track.Clips.Count != before) any = true;
                }
            }

            // ripple path always dirties if ids might have matched
            session.MarkDirty();
            _ = any;
            return EditResult.Success();
        });
    }

    public static EditResult SplitClip(ProjectSession session, string clipId, int atFrame)
        => SplitClipsAtFrame(session, [clipId], atFrame);

    /// <summary>
    /// Splits every listed clip (and link-group partners) at <paramref name="atFrame"/> in one undo.
    /// Right halves of a former group get a new <see cref="Clip.LinkGroupId"/>.
    /// </summary>
    public static EditResult SplitClipsAtFrame(
        ProjectSession session,
        IReadOnlyList<string> clipIds,
        int atFrame,
        bool expandLinks = true)
    {
        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            var seed = clipIds.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
            if (seed.Count == 0)
                return EditResult.Fail("No clips specified");

            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var track in timeline.Tracks)
            {
                foreach (var clip in track.Clips)
                    present.Add(clip.Id);
            }
            if (!seed.Any(present.Contains))
                return EditResult.Fail($"clip not found: {seed.First()}");

            var expanded = expandLinks ? LinkGroup.Expand(timeline, seed) : seed;
            var rightsByGroup = new Dictionary<string, List<Clip>>(StringComparer.Ordinal);
            var any = false;
            var blockedByLock = false;

            foreach (var track in timeline.Tracks)
            {
                if (track.Locked)
                {
                    if (track.Clips.Any(c => expanded.Contains(c.Id) && atFrame > c.StartFrame && atFrame < c.EndFrame))
                        blockedByLock = true;
                    continue;
                }
                for (var i = 0; i < track.Clips.Count; i++)
                {
                    var clip = track.Clips[i];
                    if (!expanded.Contains(clip.Id)) continue;
                    if (atFrame <= clip.StartFrame || atFrame >= clip.EndFrame) continue;

                    var leftDuration = atFrame - clip.StartFrame;
                    var rightDuration = clip.EndFrame - atFrame;
                    var trimDelta = (int)Math.Round(leftDuration * clip.Speed);

                    var right = CloneClipShallow(clip);
                    right.Id = Timeline.NewId();
                    right.StartFrame = atFrame;
                    right.DurationFrames = rightDuration;
                    right.TrimStartFrame = clip.TrimStartFrame + trimDelta;
                    ClipSplitPreservation.ShiftRelativeTimings(right, -leftDuration);

                    clip.DurationFrames = leftDuration;
                    // WP-252, Mac EditorViewModel+ClipMutations: the fade-in stays on the left
                    // half and the fade-out on the right. Copying both made new fades at the split.
                    clip.FadeOutFrames = 0;
                    right.FadeInFrames = 0;
                    clip.ClampFadesToDuration();
                    right.ClampFadesToDuration();
                    track.Clips.Insert(i + 1, right);
                    i++;
                    any = true;

                    // WP-252: the transition at this clip's tail now leaves from the right half.
                    foreach (var transition in timeline.Transitions)
                    {
                        if (string.Equals(transition.ClipIdA, clip.Id, StringComparison.Ordinal))
                            transition.ClipIdA = right.Id;
                    }

                    if (!string.IsNullOrEmpty(clip.LinkGroupId))
                    {
                        if (!rightsByGroup.TryGetValue(clip.LinkGroupId, out var list))
                        {
                            list = [];
                            rightsByGroup[clip.LinkGroupId] = list;
                        }
                        list.Add(right);
                    }
                }
            }

            if (!any)
                return EditResult.Fail(blockedByLock ? TrackLockedError : "split frame must be inside clip");

            foreach (var rights in rightsByGroup.Values)
            {
                if (rights.Count == 0) continue;
                var newGroup = rights.Count > 1 ? Timeline.NewId() : null;
                foreach (var right in rights)
                    right.LinkGroupId = newGroup;
            }

            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static EditResult MoveClip(ProjectSession session, string clipId, int trackIndex, int newStartFrame, bool overwrite = false)
    {
        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            Clip? found = null;
            Track? fromTrack = null;
            foreach (var track in timeline.Tracks)
            {
                var c = track.Clips.FirstOrDefault(x => x.Id == clipId);
                if (c is null) continue;
                found = c;
                fromTrack = track;
                break;
            }
            if (found is null || fromTrack is null)
                return EditResult.Fail($"clip not found: {clipId}");
            if (trackIndex < 0 || trackIndex >= timeline.Tracks.Count)
                return EditResult.Fail($"trackIndex {trackIndex} out of range");

            var toTrack = timeline.Tracks[trackIndex];
            if (fromTrack.Locked || toTrack.Locked)
                return EditResult.Fail(TrackLockedError);
            if (!TrackAccepts(toTrack, found.MediaType))
                return EditResult.Fail($"track {toTrack.Id} ({toTrack.Type}) does not accept media type {found.MediaType}");

            fromTrack.Clips.Remove(found);
            found.StartFrame = Math.Max(0, newStartFrame);
            if (overwrite)
            {
                var spans = toTrack.Clips
                    .Select(c => new OverwriteEngine.ClipSpan(
                        c.Id, c.StartFrame, c.EndFrame, c.TrimStartFrame, c.Speed))
                    .ToList();
                var actions = OverwriteEngine.ComputeOverwrite(
                    spans, found.StartFrame, found.EndFrame);
                ApplyOverwriteActions(toTrack, actions);
            }
            toTrack.Clips.Add(found);
            toTrack.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>
    /// Moves several clips as one all-or-nothing, undoable edit. Existing clips in the destination
    /// tracks are overwritten only after every moving clip has been removed, so a grouped move
    /// never trims or removes another member of that same group.
    /// </summary>
    public static EditResult MoveClips(ProjectSession session, IReadOnlyList<ClipMove> moves, bool overwrite = false)
    {
        if (moves is null || moves.Count == 0)
            return EditResult.Fail("at least one clip move is required");
        if (moves.Any(move => string.IsNullOrWhiteSpace(move.ClipId)))
            return EditResult.Fail("clipId is required for every move");
        if (moves.Select(move => move.ClipId).Distinct(StringComparer.Ordinal).Count() != moves.Count)
            return EditResult.Fail("each clip can appear only once in a grouped move");

        return ExecuteWithUndo(session, () =>
        {
            var result = ApplyMovesInPlace(session.ActiveTimeline, moves, overwrite);
            if (!result.Ok) return result;
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    private static EditResult ApplyMovesInPlace(Timeline timeline, IReadOnlyList<ClipMove> moves, bool overwrite)
    {
        var resolved = new List<(ClipMove Move, Clip Clip, Track SourceTrack)>();
        foreach (var move in moves)
        {
            if (move.TrackIndex < 0 || move.TrackIndex >= timeline.Tracks.Count)
                return EditResult.Fail($"trackIndex {move.TrackIndex} out of range");

            var sourceTrack = timeline.Tracks.FirstOrDefault(track => track.Clips.Any(clip => clip.Id == move.ClipId));
            var clip = sourceTrack?.Clips.FirstOrDefault(candidate => candidate.Id == move.ClipId);
            if (sourceTrack is null || clip is null)
                return EditResult.Fail($"clip not found: {move.ClipId}");

            var destinationTrack = timeline.Tracks[move.TrackIndex];
            if (sourceTrack.Locked || destinationTrack.Locked)
                return EditResult.Fail(TrackLockedError);
            if (!TrackAccepts(destinationTrack, clip.MediaType))
                return EditResult.Fail($"track {destinationTrack.Id} ({destinationTrack.Type}) does not accept media type {clip.MediaType}");

            resolved.Add((move, clip, sourceTrack));
        }

        foreach (var item in resolved)
            item.SourceTrack.Clips.Remove(item.Clip);

        if (overwrite)
        {
            foreach (var group in resolved.GroupBy(item => item.Move.TrackIndex))
            {
                var destinationTrack = timeline.Tracks[group.Key];
                foreach (var item in group.OrderBy(item => item.Move.NewStartFrame))
                {
                    item.Clip.StartFrame = Math.Max(0, item.Move.NewStartFrame);
                    var spans = destinationTrack.Clips
                        .Select(clip => new OverwriteEngine.ClipSpan(
                            clip.Id, clip.StartFrame, clip.EndFrame, clip.TrimStartFrame, clip.Speed))
                        .ToList();
                    ApplyOverwriteActions(destinationTrack, OverwriteEngine.ComputeOverwrite(
                        spans, item.Clip.StartFrame, item.Clip.EndFrame));
                }
            }
        }

        foreach (var item in resolved)
        {
            item.Clip.StartFrame = Math.Max(0, item.Move.NewStartFrame);
            timeline.Tracks[item.Move.TrackIndex].Clips.Add(item.Clip);
        }

        foreach (var track in timeline.Tracks)
            track.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));

        return EditResult.Success();
    }

    /// <summary>
    /// Trim a clip edge to an absolute timeline frame.
    /// Left edge: adjusts start, duration, and source trim-in. Right edge: adjusts duration only.
    /// </summary>
    public static EditResult TrimClipEdge(ProjectSession session, string clipId, bool leftEdge, int edgeFrame)
    {
        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            foreach (var track in timeline.Tracks)
            {
                var clip = track.Clips.FirstOrDefault(c => c.Id == clipId);
                if (clip is null) continue;
                if (track.Locked)
                    return EditResult.Fail(TrackLockedError);

                if (leftEdge)
                {
                    var maxStart = clip.EndFrame - 1;
                    var newStart = Math.Clamp(edgeFrame, 0, maxStart);
                    var delta = newStart - clip.StartFrame;
                    if (delta == 0) return EditResult.Success();
                    clip.StartFrame = newStart;
                    clip.DurationFrames = Math.Max(1, clip.DurationFrames - delta);
                    clip.TrimStartFrame = Math.Max(0, clip.TrimStartFrame + (int)Math.Round(delta * clip.Speed));
                    ClipSplitPreservation.ShiftRelativeTimings(clip, -delta);
                }
                else
                {
                    var minEnd = clip.StartFrame + 1;
                    var newEnd = Math.Max(minEnd, edgeFrame);
                    clip.DurationFrames = Math.Max(1, newEnd - clip.StartFrame);
                }

                clip.ClampFadesToDuration();
                track.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
                session.MarkDirty();
                return EditResult.Success();
            }

            return EditResult.Fail($"clip not found: {clipId}");
        });
    }

    /// <summary>Trims several clip edges as one atomic, undoable edit.</summary>
    public static EditResult TrimClipEdges(ProjectSession session, IReadOnlyList<ClipEdgeTrim> trims)
    {
        if (trims is null || trims.Count == 0)
            return EditResult.Fail("at least one clip edge trim is required");
        if (trims.Any(trim => string.IsNullOrWhiteSpace(trim.ClipId)))
            return EditResult.Fail("clipId is required for every trim");
        if (trims.Select(trim => trim.ClipId).Distinct(StringComparer.Ordinal).Count() != trims.Count)
            return EditResult.Fail("each clip can appear only once in a grouped trim");

        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            var resolved = new List<(ClipEdgeTrim Trim, Clip Clip, Track Track)>();
            foreach (var trim in trims)
            {
                var track = timeline.Tracks.FirstOrDefault(candidate => candidate.Clips.Any(clip => clip.Id == trim.ClipId));
                var clip = track?.Clips.FirstOrDefault(candidate => candidate.Id == trim.ClipId);
                if (track is null || clip is null)
                    return EditResult.Fail($"clip not found: {trim.ClipId}");
                if (track.Locked)
                    return EditResult.Fail(TrackLockedError);

                resolved.Add((trim, clip, track));
            }

            foreach (var item in resolved)
            {
                if (item.Trim.LeftEdge)
                {
                    var newStart = Math.Clamp(item.Trim.EdgeFrame, 0, item.Clip.EndFrame - 1);
                    var delta = newStart - item.Clip.StartFrame;
                    item.Clip.StartFrame = newStart;
                    item.Clip.DurationFrames = Math.Max(1, item.Clip.DurationFrames - delta);
                    item.Clip.TrimStartFrame = Math.Max(0,
                        item.Clip.TrimStartFrame + (int)Math.Round(delta * item.Clip.Speed));
                    ClipSplitPreservation.ShiftRelativeTimings(item.Clip, -delta);
                }
                else
                {
                    var newEnd = Math.Max(item.Clip.StartFrame + 1, item.Trim.EdgeFrame);
                    item.Clip.DurationFrames = Math.Max(1, newEnd - item.Clip.StartFrame);
                }

                item.Clip.ClampFadesToDuration();
            }

            foreach (var track in resolved.Select(item => item.Track).Distinct())
                track.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));

            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static EditResult AddClips(
        ProjectSession session,
        IReadOnlyList<(int TrackIndex, Clip Clip)> clips,
        bool overwrite = false)
    {
        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            foreach (var item in clips)
            {
                if (item.TrackIndex < 0 || item.TrackIndex >= timeline.Tracks.Count)
                    return EditResult.Fail($"trackIndex {item.TrackIndex} out of range");

                var track = timeline.Tracks[item.TrackIndex];
                if (track.Locked)
                    return EditResult.Fail(TrackLockedError);
                if (!TrackAccepts(track, item.Clip.MediaType))
                    return EditResult.Fail($"clip type {item.Clip.MediaType} does not belong on a {track.Type} track");

                if (overwrite)
                {
                    var spans = track.Clips
                        .Select(c => new OverwriteEngine.ClipSpan(
                            c.Id, c.StartFrame, c.EndFrame, c.TrimStartFrame, c.Speed))
                        .ToList();
                    var actions = OverwriteEngine.ComputeOverwrite(
                        spans, item.Clip.StartFrame, item.Clip.EndFrame);
                    ApplyOverwriteActions(track, actions);
                }

                track.Clips.Add(item.Clip);
                track.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));

                // Mac add_clips: "When a video asset with audio is placed on a video track, a linked
                // audio clip is automatically created on an audio track" (ToolDefinitions.swift:287).
                // Inside this same ExecuteWithUndo, so one undo removes the pair (WP-157).
                if (!SourceHasAudio(session, item.Clip)) continue;
                var partner = CreateLinkedAudioPartner(item.Clip);
                // The partner goes somewhere it fits, and is placed with overwrite OFF. Carrying the
                // caller's overwrite here is what let a partner delete the user's music: the resolver
                // is occupancy-aware now, so there is nothing to overwrite.
                var audioTrackIndex = ResolveLinkedAudioTrackIndex(
                    timeline, item.TrackIndex, (partner.StartFrame, partner.EndFrame));
                PlaceClip(timeline.Tracks[audioTrackIndex], partner, overwrite: false, ripple: false);
            }
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static EditResult InsertClips(
        ProjectSession session,
        int trackIndex,
        int atFrame,
        IReadOnlyList<Clip> clipsToInsert)
    {
        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            if (trackIndex < 0 || trackIndex >= timeline.Tracks.Count)
                return EditResult.Fail($"trackIndex {trackIndex} out of range");

            var track = timeline.Tracks[trackIndex];
            if (track.Locked)
                return EditResult.Fail(TrackLockedError);
            if (clipsToInsert.Any(c => !TrackAccepts(track, c.MediaType)))
                return EditResult.Fail($"clip type does not belong on a {track.Type} track");

            var totalInsertDuration = clipsToInsert.Sum(c => c.DurationFrames);

            foreach (var c in track.Clips)
            {
                if (c.StartFrame >= atFrame)
                {
                    c.StartFrame += totalInsertDuration;
                }
            }

            var currentStart = atFrame;
            var partners = new List<Clip>();
            foreach (var c in clipsToInsert)
            {
                c.StartFrame = currentStart;
                track.Clips.Add(c);
                currentStart += c.DurationFrames;
                if (SourceHasAudio(session, c))
                    partners.Add(CreateLinkedAudioPartner(c));
            }

            track.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));

            // Mac insert_clips: the push is applied to the target track "AND the audio track any
            // auto-created linked audio lands on -- so a clip and its linked audio stay aligned"
            // (ToolDefinitions.swift:311). Without this the pair desynchronises on the next insert.
            if (partners.Count > 0)
            {
                // null: the push below opens the range on whichever track is chosen, so an
                // occupancy check here would send every insert to a fresh track.
                var audioTrack = timeline.Tracks[
                    ResolveLinkedAudioTrackIndex(timeline, trackIndex, freeRange: null)];
                foreach (var existing in audioTrack.Clips)
                {
                    if (existing.StartFrame >= atFrame)
                        existing.StartFrame += totalInsertDuration;
                }

                audioTrack.Clips.AddRange(partners);
                audioTrack.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
            }

            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static EditResult RippleDeleteRanges(
        ProjectSession session,
        int trackIndex,
        IReadOnlyList<(int Start, int End)> ranges)
        => RippleDeleteRanges(session, [trackIndex], ranges);

    /// <summary>
    /// Ripple-delete the same frame ranges on every listed track in one undo step.
    /// Used so linked A/V partners stay aligned after word-cut / silence removal.
    /// </summary>
    public static EditResult RippleDeleteRanges(
        ProjectSession session,
        IReadOnlyList<int> trackIndices,
        IReadOnlyList<(int Start, int End)> ranges)
    {
        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            if (trackIndices.Count == 0)
                return EditResult.Fail("No tracks specified.");
            foreach (var trackIndex in trackIndices.Distinct())
            {
                if (trackIndex < 0 || trackIndex >= timeline.Tracks.Count)
                    return EditResult.Fail($"trackIndex {trackIndex} out of range");
                if (timeline.Tracks[trackIndex].Locked)
                    return EditResult.Fail(TrackLockedError);
            }

            var sortedRanges = ranges.OrderByDescending(r => r.Start).ToList();
            foreach (var trackIndex in trackIndices.Distinct().OrderBy(i => i))
            {
                var track = timeline.Tracks[trackIndex];
                foreach (var range in sortedRanges)
                {
                    var start = range.Start;
                    var end = range.End;
                    if (end <= start)
                        continue;
                    var duration = end - start;

                    // Mid-clip holes need real splits; shrinking DurationFrames would keep
                    // the deleted source audio/video in the remaining piece.
                    SplitClipAtFrame(track, start);
                    SplitClipAtFrame(track, end);

                    var toRemove = new List<Clip>();
                    foreach (var clip in track.Clips)
                    {
                        var clipStart = clip.StartFrame;
                        var clipEnd = clip.EndFrame;

                        if (clipStart >= start && clipEnd <= end)
                        {
                            toRemove.Add(clip);
                        }
                        else if (clipStart < start && clipEnd > start)
                        {
                            clip.DurationFrames = start - clipStart;
                        }
                        else if (clipStart < end && clipEnd > end)
                        {
                            var shift = end - clipStart;
                            clip.StartFrame = start;
                            clip.DurationFrames -= shift;
                            clip.TrimStartFrame += shift;
                            ClipSplitPreservation.ShiftRelativeTimings(clip, -shift);
                        }
                    }

                    foreach (var clip in toRemove)
                        track.Clips.Remove(clip);

                    foreach (var clip in track.Clips)
                    {
                        if (clip.StartFrame >= end)
                            clip.StartFrame -= duration;
                    }
                }

                track.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
            }

            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static EditResult SetClipProperties(
        ProjectSession session,
        IReadOnlyList<string> clipIds,
        int? durationFrames,
        int? trimStartFrame,
        int? trimEndFrame,
        double? speed,
        double? volume,
        double? opacity,
        Transform? transform,
        string? blendMode,
        Crop? crop = null,
        int? fadeInFrames = null,
        int? fadeOutFrames = null)
    {
        return ExecuteWithUndo(session, () =>
            ApplyClipPropertiesInPlace(
                session, clipIds, durationFrames, trimStartFrame, trimEndFrame,
                speed, volume, opacity, transform, blendMode, crop, fadeInFrames, fadeOutFrames));
    }

    /// <summary>
    /// Applies property patches without recording undo (caller must own ClipLiveEdit / RecordBeforeEdit).
    /// </summary>
    public static EditResult ApplyClipPropertiesInPlace(
        ProjectSession session,
        IReadOnlyList<string> clipIds,
        int? durationFrames,
        int? trimStartFrame,
        int? trimEndFrame,
        double? speed,
        double? volume,
        double? opacity,
        Transform? transform,
        string? blendMode,
        Crop? crop = null,
        int? fadeInFrames = null,
        int? fadeOutFrames = null)
    {
        var timeline = session.ActiveTimeline;
        var idSet = clipIds.ToHashSet();
        var foundAny = false;
        var matchedLocked = false;

        foreach (var track in timeline.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (!idSet.Contains(clip.Id)) continue;
                if (track.Locked)
                {
                    matchedLocked = true;
                    continue;
                }
                foundAny = true;
                if (durationFrames != null) clip.DurationFrames = Math.Max(1, durationFrames.Value);
                if (trimStartFrame != null) clip.TrimStartFrame = Math.Max(0, trimStartFrame.Value);
                if (trimEndFrame != null) clip.TrimEndFrame = Math.Max(0, trimEndFrame.Value);
                if (speed != null) clip.Speed = Math.Clamp(speed.Value, 0.05, 8);
                if (volume != null) clip.Volume = Math.Max(0, volume.Value);
                if (opacity != null) clip.Opacity = Math.Clamp(opacity.Value, 0, 1);
                if (transform != null) clip.Transform = transform.Clone();
                if (blendMode != null) clip.BlendMode = blendMode is "normal" or "" ? null : blendMode;
                if (crop != null) clip.Crop = crop.Clone();
                if (fadeInFrames != null) clip.FadeInFrames = Math.Max(0, fadeInFrames.Value);
                if (fadeOutFrames != null) clip.FadeOutFrames = Math.Max(0, fadeOutFrames.Value);
            }
        }

        if (!foundAny)
            return EditResult.Fail(matchedLocked ? TrackLockedError : "No clips found with the specified IDs");

        session.MarkDirty();
        return EditResult.Success();
    }
}
