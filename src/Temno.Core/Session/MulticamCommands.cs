using Temno.Core.Editing;
using Temno.Core.Models;

namespace Temno.Core.Session;

/// <summary>
/// Multicam group create / ungroup / angle switch (WP-93) and multi-angle layout overlays (WP-107).
/// Sync maps are computed outside Core (Media audio correlation); this layer only mutates the project.
/// </summary>
public static class MulticamCommands
{
    public sealed class MemberSpec
    {
        public required string MediaRef { get; init; }
        public MulticamMemberKind Kind { get; init; } = MulticamMemberKind.angle;
        public string? AngleLabel { get; init; }
        public double? PinnedOffsetSeconds { get; init; }
    }

    public sealed class CreateResult
    {
        public required string GroupId { get; init; }
        public List<string> ClipIds { get; } = [];
        public List<(string MediaRef, string Reason)> NeedsAttention { get; } = [];
    }

    public sealed class SwitchResult
    {
        public int Switched { get; set; }
        public int Merged { get; set; }
        public List<(int Start, int End)> Applied { get; } = [];
        public List<(int ReqStart, int ReqEnd, int AppStart, int AppEnd, string Culprit)> Clamped { get; } = [];
        public List<(int Start, int End, string Reason)> Skipped { get; } = [];
        public List<string> OverlayClipIds { get; } = [];
    }

    public static MulticamSource? FindGroup(ProjectSession session, string groupId)
        => session.ProjectFile.MulticamGroups?.FirstOrDefault(g => g.Id == groupId);

    public static MulticamSource? FindGroupOfClip(ProjectSession session, string clipId)
    {
        var clip = FindClip(session, clipId, out _);
        if (clip?.MulticamGroupId is not { Length: > 0 } gid) return null;
        return FindGroup(session, gid);
    }

    public static EditResult CreateGroup(
        ProjectSession session,
        IReadOnlyList<MemberSpec> specs,
        IReadOnlyDictionary<string, MulticamSyncMap> syncMaps,
        string masterMediaRef,
        IReadOnlyDictionary<string, double> sourceDurations,
        IReadOnlyDictionary<string, ClipType> mediaTypes,
        string? name,
        int? startFrame,
        out CreateResult created)
    {
        created = new CreateResult { GroupId = "" };
        if (specs is null || specs.Count < 2)
            return EditResult.Fail("create.members requires at least two entries.");
        if (string.IsNullOrEmpty(masterMediaRef))
            return EditResult.Fail("master mediaRef is required.");
        if (!specs.Any(s => s.MediaRef == masterMediaRef))
            return EditResult.Fail("Master member not found among members.");

        session.RecordBeforeEdit();
        try
        {
            var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var members = new List<MulticamMember>();
            foreach (var spec in specs)
            {
                var label = UniqueLabel(spec.AngleLabel ?? spec.MediaRef, usedLabels);
                var map = syncMaps.TryGetValue(spec.MediaRef, out var m) ? m : new MulticamSyncMap();
                members.Add(new MulticamMember
                {
                    MediaRef = spec.MediaRef,
                    Kind = spec.Kind,
                    AngleLabel = label,
                    Sync = map,
                });
            }

            var master = members.First(m => m.MediaRef == masterMediaRef);
            var group = new MulticamSource
            {
                Name = string.IsNullOrWhiteSpace(name) ? NextGroupName(session) : name.Trim(),
                Members = members,
                MasterMemberId = master.Id,
            };

            var fps = Math.Max(1, session.ActiveTimeline.Fps);
            var angleRanges = new List<(MulticamMember Member, int Start, int End)>();
            foreach (var angle in group.Angles)
            {
                if (!sourceDurations.TryGetValue(angle.MediaRef, out var dur) || dur <= 0) continue;
                var (s, e) = angle.Coverage(dur, fps);
                if (e > s) angleRanges.Add((angle, s, e));
            }
            if (angleRanges.Count == 0)
            {
                session.RollbackLastRecord();
                return EditResult.Fail("No synced camera has picture — nothing to place.");
            }

            var videoStart = angleRanges.Min(r => r.Start);
            var videoEnd = angleRanges.Max(r => r.End);
            var at = startFrame ?? session.ActiveTimeline.TotalFrames;
            var groupOrigin = at - videoStart;

            // Greedy fill of [videoStart, videoEnd) with angle coverages (seed = first usable).
            var holes = new List<(int S, int E)> { (videoStart, videoEnd) };
            var programSpans = new List<(MulticamMember Member, int S, int E)>();
            foreach (var (member, covS, covE) in angleRanges)
            {
                var nextHoles = new List<(int S, int E)>();
                foreach (var hole in holes)
                {
                    var fillS = Math.Max(hole.S, covS);
                    var fillE = Math.Min(hole.E, covE);
                    if (fillE <= fillS)
                    {
                        nextHoles.Add(hole);
                        continue;
                    }
                    programSpans.Add((member, fillS, fillE));
                    if (hole.S < fillS) nextHoles.Add((hole.S, fillS));
                    if (fillE < hole.E) nextHoles.Add((fillE, hole.E));
                }
                holes = nextHoles;
                if (holes.Count == 0) break;
            }
            programSpans.Sort((a, b) => a.S.CompareTo(b.S));

            var timeline = session.ActiveTimeline;
            // Insert program video track at top (index 0).
            timeline.Tracks.Insert(0, new Track { Type = ClipType.video, SyncLocked = true });
            var videoIdx = 0;
            var clipIds = new List<string>();

            foreach (var span in programSpans)
            {
                var clip = MakeMemberClip(
                    span.Member, span.S, span.E, ClipType.video, group.Id, groupOrigin, fps,
                    sourceDurations.GetValueOrDefault(span.Member.MediaRef),
                    mediaTypes.GetValueOrDefault(span.Member.MediaRef, ClipType.video));
                if (clip is null) continue;
                timeline.Tracks[videoIdx].Clips.Add(clip);
                clipIds.Add(clip.Id);
            }
            timeline.Tracks[videoIdx].Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));

            var audioInsert = timeline.Tracks.Count;
            foreach (var mic in group.Mics)
            {
                if (!sourceDurations.TryGetValue(mic.MediaRef, out var dur) || dur <= 0) continue;
                var (s, e) = mic.Coverage(dur, fps);
                var clip = MakeMemberClip(
                    mic, s, e, ClipType.audio, group.Id, groupOrigin, fps, dur,
                    mediaTypes.GetValueOrDefault(mic.MediaRef, ClipType.audio));
                if (clip is null) continue;
                timeline.Tracks.Insert(audioInsert, new Track { Type = ClipType.audio, SyncLocked = true });
                timeline.Tracks[audioInsert].Clips.Add(clip);
                clipIds.Add(clip.Id);
                audioInsert++;
            }

            if (clipIds.Count == 0)
            {
                session.RollbackLastRecord();
                return EditResult.Fail("Could not place the multicam on the timeline.");
            }

            session.ProjectFile.MulticamGroups ??= [];
            session.ProjectFile.MulticamGroups.Add(group);
            session.MarkDirty();

            created = new CreateResult { GroupId = group.Id };
            created.ClipIds.AddRange(clipIds);
            return EditResult.Success();
        }
        catch (Exception ex)
        {
            session.RollbackLastRecord();
            return EditResult.Fail(ex.Message);
        }
    }

    public static EditResult Ungroup(ProjectSession session, string groupId)
    {
        if (FindGroup(session, groupId) is null)
            return EditResult.Fail($"No multicam group '{groupId}'.");

        session.RecordBeforeEdit();
        foreach (var track in session.ActiveTimeline.Tracks)
        {
            foreach (var clip in track.Clips.Where(c => c.MulticamGroupId == groupId))
                clip.MulticamGroupId = null;
        }
        session.ProjectFile.MulticamGroups?.RemoveAll(g => g.Id == groupId);
        session.MarkDirty();
        return EditResult.Success();
    }

    /// <summary>Full-frame angle switch over [start, end).</summary>
    public static EditResult ChangeCam(
        ProjectSession session,
        string groupId,
        int rangeStart,
        int rangeEnd,
        string angleLabel,
        IReadOnlyDictionary<string, double> sourceDurations,
        out SwitchResult outcome)
        => ChangeCam(
            session, groupId, rangeStart, rangeEnd,
            primaryAngleLabel: angleLabel,
            extraAngleLabels: null,
            layout: LayoutKind.Full,
            sourceDurations,
            sourceSizes: null,
            out outcome);

    /// <summary>
    /// Angle switch over [start, end). When <paramref name="layout"/> is not <see cref="LayoutKind.Full"/>,
    /// the primary angle fills the main slot and extra angles become overlay clips above the program track
    /// with transforms from <see cref="LayoutEngine.Place"/> (Mac MulticamEngine multi-angle path).
    /// </summary>
    public static EditResult ChangeCam(
        ProjectSession session,
        string groupId,
        int rangeStart,
        int rangeEnd,
        string primaryAngleLabel,
        IReadOnlyList<string>? extraAngleLabels,
        LayoutKind layout,
        IReadOnlyDictionary<string, double> sourceDurations,
        IReadOnlyDictionary<string, (double W, double H)>? sourceSizes,
        out SwitchResult outcome)
    {
        outcome = new SwitchResult();
        var group = FindGroup(session, groupId);
        if (group is null) return EditResult.Fail($"No multicam group '{groupId}'.");
        if (rangeStart >= rangeEnd) return EditResult.Fail("range must have start < end.");

        var angleLabels = new List<string> { primaryAngleLabel };
        if (extraAngleLabels is { Count: > 0 })
            angleLabels.AddRange(extraAngleLabels);

        if (layout != LayoutKind.Full)
        {
            var slots = LayoutEngine.Slots(layout);
            if (angleLabels.Count == 0)
                return EditResult.Fail($"Layout {LayoutEngine.RawValue(layout)} needs angles.");
            if (angleLabels.Count > slots.Count)
            {
                return EditResult.Fail(
                    $"Layout {LayoutEngine.RawValue(layout)} takes at most {slots.Count} angle(s): " +
                    $"{string.Join(", ", slots.Select(s => s.Name))}.");
            }
        }

        var slotMembers = new List<MulticamMember>();
        foreach (var label in angleLabels)
        {
            var member = group.MemberLabeled(label);
            if (member is null) return EditResult.Fail($"Unknown angle '{label}'.");
            if (!member.ProvidesVideo)
                return EditResult.Fail($"Angle '{label}' is not a camera.");
            if (!member.Usable)
                return EditResult.Fail($"Angle '{label}' isn't synced — pin an offset or recreate the group.");
            slotMembers.Add(member);
        }

        var primary = slotMembers[0];
        var useLayout = layout != LayoutKind.Full && slotMembers.Count >= 1;
        // Full-frame uses only the primary angle; multi-angle layout uses all slots that have angles.
        if (layout == LayoutKind.Full)
            useLayout = false;

        session.RecordBeforeEdit();
        try
        {
            var timeline = session.ActiveTimeline;
            var fps = Math.Max(1, timeline.Fps);
            var canvasW = Math.Max(1, timeline.Width);
            var canvasH = Math.Max(1, timeline.Height);
            var programTrackIdx = FindProgramTrackIndex(session, groupId, rangeStart, rangeEnd);
            if (programTrackIdx < 0)
            {
                outcome.Skipped.Add((rangeStart, rangeEnd, "no multicam clip in this range"));
                session.MarkDirty();
                return EditResult.Success();
            }

            var programTrackId = timeline.Tracks[programTrackIdx].Id;
            var track = timeline.Tracks[programTrackIdx];
            // Snapshot overlapping fragments before surgery (ids stable across splits of other clips).
            var fragmentIds = track.Clips
                .Where(c =>
                    c.MulticamGroupId == groupId
                    && c.MediaType != ClipType.audio
                    && c.StartFrame < rangeEnd
                    && c.EndFrame > rangeStart)
                .Select(c => c.Id)
                .ToList();

            foreach (var fragmentId in fragmentIds)
            {
                programTrackIdx = IndexOfTrack(timeline, programTrackId);
                if (programTrackIdx < 0) break;
                track = timeline.Tracks[programTrackIdx];
                var clip = track.Clips.FirstOrDefault(c => c.Id == fragmentId);
                if (clip is null) continue;

                var current = group.MemberByMediaRef(clip.MediaRef);
                if (current is null) continue;

                if (!sourceDurations.TryGetValue(primary.MediaRef, out var targetDur) || targetDur <= 0)
                {
                    outcome.Skipped.Add((clip.StartFrame, clip.EndFrame, $"{primary.AngleLabel} has no duration"));
                    continue;
                }

                var wantedS = Math.Max(clip.StartFrame, rangeStart);
                var wantedE = Math.Min(clip.EndFrame, rangeEnd);
                if (wantedE <= wantedS) continue;

                var (covS, covE) = ProjectCoverage(clip, current, primary, targetDur, fps);
                var appS = Math.Max(wantedS, covS);
                var appE = Math.Min(wantedE, covE);
                if (appE <= appS)
                {
                    outcome.Skipped.Add((wantedS, wantedE, $"{primary.AngleLabel} wasn't recording here"));
                    continue;
                }

                if (appS > wantedS || appE < wantedE)
                    outcome.Clamped.Add((wantedS, wantedE, appS, appE, primary.AngleLabel));

                // Anchor fragment (pre-rewrite) for overlay trim math.
                var anchor = clip;
                var anchorMember = current;

                var hadLayout = ClearOverlays(timeline, programTrackIdx, groupId, appS, appE) > 0;

                if (useLayout && slotMembers.Count > 1)
                {
                    var slots = LayoutEngine.Slots(layout);
                    for (var si = 1; si < slotMembers.Count && si < slots.Count; si++)
                    {
                        programTrackIdx = IndexOfTrack(timeline, programTrackId);
                        if (programTrackIdx < 0) break;
                        var overlayId = PlaceOverlay(
                            timeline,
                            programTrackIdx,
                            slotMembers[si],
                            appS,
                            appE,
                            anchor,
                            anchorMember,
                            group,
                            sourceDurations,
                            sourceSizes,
                            fps,
                            canvasW,
                            canvasH,
                            layout,
                            slots[si].Name);
                        if (overlayId is not null)
                            outcome.OverlayClipIds.Add(overlayId);
                    }
                }

                programTrackIdx = IndexOfTrack(timeline, programTrackId);
                if (programTrackIdx < 0) break;
                track = timeline.Tracks[programTrackIdx];
                SplitTrackAt(track, appS, groupId);
                SplitTrackAt(track, appE, groupId);

                var mainSlotName = useLayout ? LayoutEngine.Slots(layout)[0].Name : null;
                foreach (var frag in track.Clips.Where(c =>
                             c.MulticamGroupId == groupId
                             && c.MediaType != ClipType.audio
                             && c.StartFrame >= appS
                             && c.EndFrame <= appE).ToList())
                {
                    var cur = group.MemberByMediaRef(frag.MediaRef) ?? current;
                    RewriteProgramFragment(frag, cur, primary, sourceDurations, fps);
                    if (useLayout && mainSlotName is not null)
                    {
                        var (sw, sh) = SourceSize(sourceSizes, frag.MediaRef);
                        var (transform, crop) = LayoutEngine.Place(
                            layout, mainSlotName, sw, sh, canvasW, canvasH);
                        frag.Transform = transform;
                        frag.Crop = crop;
                    }
                    else if (hadLayout)
                    {
                        // Restore full-frame after a prior multi-angle layout.
                        frag.Transform = new Transform();
                        frag.Crop = new Crop();
                    }
                    outcome.Switched++;
                }
                outcome.Applied.Add((appS, appE));
            }

            programTrackIdx = IndexOfTrack(timeline, programTrackId);
            if (programTrackIdx >= 0)
                outcome.Merged = JoinThroughEdits(timeline.Tracks[programTrackIdx], groupId);
            session.MarkDirty();
            return EditResult.Success();
        }
        catch (Exception ex)
        {
            session.RollbackLastRecord();
            return EditResult.Fail(ex.Message);
        }
    }

    public static List<object[]> ProgramRows(
        ProjectSession session, string groupId, int? startFrame, int? endFrame)
    {
        var group = FindGroup(session, groupId);
        if (group is null) return [];

        // Mac: program is the bottom-most video track that holds the group (overlays sit above).
        var programTrackIdx = FindProgramTrackIndex(
            session, groupId, startFrame ?? 0, endFrame ?? int.MaxValue);
        if (programTrackIdx < 0)
        {
            // Fall back: any range if the optional window filtered everything out.
            programTrackIdx = FindProgramTrackIndex(session, groupId, 0, int.MaxValue);
            if (programTrackIdx < 0) return [];
        }

        var clips = session.ActiveTimeline.Tracks[programTrackIdx].Clips
            .Where(c => c.MulticamGroupId == groupId && c.MediaType != ClipType.audio)
            .OrderBy(c => c.StartFrame)
            .ToList();

        var rows = new List<object[]>();
        string? runAngle = null;
        var runStart = 0;
        var runEnd = 0;

        void Flush()
        {
            if (runAngle is null) return;
            var a = startFrame is { } s ? Math.Max(runStart, s) : runStart;
            var b = endFrame is { } e ? Math.Min(runEnd, e) : runEnd;
            if (b > a) rows.Add([runAngle, a, b]);
        }

        foreach (var clip in clips)
        {
            var label = group.MemberByMediaRef(clip.MediaRef)?.AngleLabel ?? clip.MediaRef;
            if (runAngle is null)
            {
                runAngle = label;
                runStart = clip.StartFrame;
                runEnd = clip.EndFrame;
                continue;
            }
            if (label == runAngle && clip.StartFrame == runEnd)
            {
                runEnd = clip.EndFrame;
                continue;
            }
            Flush();
            runAngle = label;
            runStart = clip.StartFrame;
            runEnd = clip.EndFrame;
        }
        Flush();
        return rows;
    }

    public static List<int> TrackIndexes(ProjectSession session, string groupId)
    {
        var set = new SortedSet<int>();
        var tracks = session.ActiveTimeline.Tracks;
        for (var i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Clips.Any(c => c.MulticamGroupId == groupId))
                set.Add(i);
        }
        return set.ToList();
    }

    // --- helpers ---

    private static Clip? MakeMemberClip(
        MulticamMember member,
        int groupStart,
        int groupEnd,
        ClipType mediaType,
        string groupId,
        int groupOrigin,
        int fps,
        double? sourceDuration,
        ClipType sourceClipType)
    {
        var start = groupOrigin + groupStart;
        var end = groupOrigin + groupEnd;
        if (start < 0)
        {
            groupStart += -start;
            start = 0;
        }
        if (end <= start) return null;

        var clip = new Clip
        {
            MediaRef = member.MediaRef,
            MediaType = mediaType,
            SourceClipType = sourceClipType,
            StartFrame = start,
            DurationFrames = end - start,
            MulticamGroupId = groupId,
            TrimStartFrame = Math.Max(0, member.TrimFrameAtGroupFrame(groupStart, fps)),
        };
        if (sourceDuration is { } dur)
        {
            var sourceLen = (int)Math.Round(dur * fps);
            clip.TrimEndFrame = Math.Max(0, sourceLen - clip.TrimStartFrame - clip.SourceFramesConsumed);
        }
        return clip;
    }

    private static void RewriteProgramFragment(
        Clip frag,
        MulticamMember current,
        MulticamMember target,
        IReadOnlyDictionary<string, double> sourceDurations,
        int fps)
    {
        // Group frame at frag start from current anchor.
        var groupFrame = frag.TrimStartFrame + current.OffsetFrames(fps);
        frag.MediaRef = target.MediaRef;
        frag.TrimStartFrame = Math.Max(0, groupFrame - target.OffsetFrames(fps));
        if (sourceDurations.TryGetValue(target.MediaRef, out var dur))
        {
            var sourceLen = (int)Math.Round(dur * fps);
            frag.TrimEndFrame = Math.Max(0, sourceLen - frag.TrimStartFrame - frag.SourceFramesConsumed);
        }
    }

    private static (int Start, int End) ProjectCoverage(
        Clip fragment,
        MulticamMember current,
        MulticamMember target,
        double targetSourceDuration,
        int fps)
    {
        var groupStartSec = fragment.TrimStartFrame / (double)fps + current.Sync.OffsetSeconds;
        int Project(double groupSeconds)
            => fragment.StartFrame + (int)Math.Round((groupSeconds - groupStartSec) * fps);

        var covS = Project(target.Sync.OffsetSeconds);
        var covE = Project(target.Sync.OffsetSeconds + targetSourceDuration);
        return (covS, Math.Max(covS, covE));
    }

    private static int FindProgramTrackIndex(ProjectSession session, string groupId, int rangeStart, int rangeEnd)
    {
        var tracks = session.ActiveTimeline.Tracks;
        // Prefer bottom-most video track with program fragments (Mac: tracks.last matching)
        for (var i = tracks.Count - 1; i >= 0; i--)
        {
            var t = tracks[i];
            if (t.Type != ClipType.video) continue;
            if (t.Clips.Any(c =>
                    c.MulticamGroupId == groupId
                    && c.MediaType != ClipType.audio
                    && c.StartFrame < rangeEnd && c.EndFrame > rangeStart))
                return i;
        }
        return -1;
    }

    private static int IndexOfTrack(Timeline timeline, string trackId)
    {
        for (var i = 0; i < timeline.Tracks.Count; i++)
        {
            if (timeline.Tracks[i].Id == trackId) return i;
        }
        return -1;
    }

    private static (double W, double H) SourceSize(
        IReadOnlyDictionary<string, (double W, double H)>? sourceSizes, string mediaRef)
    {
        if (sourceSizes is not null
            && sourceSizes.TryGetValue(mediaRef, out var s)
            && s.W > 0 && s.H > 0)
            return s;
        return (1920, 1080);
    }

    /// <summary>
    /// Mac MulticamSource.Member.anchorFrame — timeline origin of group frame 0 for this clip/member.
    /// </summary>
    private static int AnchorFrame(Clip clip, MulticamMember member, int fps)
        => clip.StartFrame - clip.TrimStartFrame - member.OffsetFrames(fps);

    /// <summary>
    /// Remove multicam overlay clips above the program track over [rangeStart, rangeEnd).
    /// Mac: MulticamEngine.clearOverlays.
    /// </summary>
    private static int ClearOverlays(
        Timeline timeline, int programTrackIdx, string groupId, int rangeStart, int rangeEnd)
    {
        var removed = 0;
        for (var i = 0; i < programTrackIdx; i++)
        {
            var track = timeline.Tracks[i];
            if (track.Type != ClipType.video) continue;
            SplitTrackAt(track, rangeStart, groupId);
            SplitTrackAt(track, rangeEnd, groupId);
            var before = track.Clips.Count;
            track.Clips.RemoveAll(c =>
                c.MulticamGroupId == groupId
                && c.StartFrame >= rangeStart
                && c.EndFrame <= rangeEnd);
            removed += before - track.Clips.Count;
        }
        return removed;
    }

    /// <summary>
    /// Insert an overlay angle clip above the program track with layout placement.
    /// Mac: MulticamEngine.placeOverlay.
    /// </summary>
    private static string? PlaceOverlay(
        Timeline timeline,
        int programTrackIdx,
        MulticamMember member,
        int rangeStart,
        int rangeEnd,
        Clip anchor,
        MulticamMember anchorMember,
        MulticamSource group,
        IReadOnlyDictionary<string, double> sourceDurations,
        IReadOnlyDictionary<string, (double W, double H)>? sourceSizes,
        int fps,
        int canvasW,
        int canvasH,
        LayoutKind layout,
        string slotName)
    {
        if (rangeEnd <= rangeStart) return null;

        var groupFrame = rangeStart - AnchorFrame(anchor, anchorMember, fps);
        var clip = new Clip
        {
            MediaRef = member.MediaRef,
            MediaType = ClipType.video,
            SourceClipType = ClipType.video,
            StartFrame = rangeStart,
            DurationFrames = rangeEnd - rangeStart,
            MulticamGroupId = group.Id,
            TrimStartFrame = groupFrame - member.OffsetFrames(fps),
        };
        if (sourceDurations.TryGetValue(member.MediaRef, out var dur) && dur > 0)
        {
            var sourceLen = (int)Math.Round(dur * fps);
            clip.TrimEndFrame = Math.Max(0, sourceLen - clip.TrimStartFrame - clip.SourceFramesConsumed);
        }

        var (sw, sh) = SourceSize(sourceSizes, member.MediaRef);
        var (transform, crop) = LayoutEngine.Place(layout, slotName, sw, sh, canvasW, canvasH);
        clip.Transform = transform;
        clip.Crop = crop;

        // Prefer a free video track above program; else insert a new track at program index.
        var freeIdx = -1;
        for (var i = programTrackIdx - 1; i >= 0; i--)
        {
            var t = timeline.Tracks[i];
            if (t.Type != ClipType.video) continue;
            if (!t.Clips.Any(c => c.StartFrame < rangeEnd && c.EndFrame > rangeStart))
            {
                freeIdx = i;
                break;
            }
        }
        var insertIdx = freeIdx >= 0 ? freeIdx : programTrackIdx;
        if (freeIdx < 0)
            timeline.Tracks.Insert(programTrackIdx, new Track { Type = ClipType.video });

        timeline.Tracks[insertIdx].Clips.Add(clip);
        timeline.Tracks[insertIdx].Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
        return clip.Id;
    }

    private static void SplitTrackAt(Track track, int frame, string groupId)
    {
        foreach (var clip in track.Clips.ToList())
        {
            if (clip.MulticamGroupId != groupId) continue;
            if (clip.StartFrame >= frame || clip.EndFrame <= frame) continue;
            // WP-235: full state preservation (effects, all keyframe tracks, blend,
            // fades, word timings) with right-side retiming; see ClipSplitPreservation.
            track.Clips.Add(ClipSplitPreservation.SplitOffRight(clip, frame));
        }
        track.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
    }

    private static int JoinThroughEdits(Track track, string groupId)
    {
        var merged = 0;
        track.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
        for (var i = 0; i < track.Clips.Count - 1;)
        {
            var a = track.Clips[i];
            var b = track.Clips[i + 1];
            if (a.MulticamGroupId == groupId && b.MulticamGroupId == groupId
                && a.MediaType != ClipType.audio && b.MediaType != ClipType.audio
                && a.MediaRef == b.MediaRef
                && a.EndFrame == b.StartFrame
                && a.Speed == b.Speed
                && a.TrimStartFrame + a.SourceFramesConsumed == b.TrimStartFrame
                && TransformEqual(a.Transform, b.Transform)
                && CropEqual(a.Crop, b.Crop))
            {
                var leftDuration = a.DurationFrames;
                a.DurationFrames += b.DurationFrames;
                a.TrimEndFrame = b.TrimEndFrame;
                // Split parks fade-out on the right edge and clears it on the head.
                // A same-angle rejoin discards that edge, so the survivor takes it
                // back. Skip when the head's opacity/volume track was baked dense
                // for an intersected fade — those keys already include the multiplier.
                if (b.FadeOutFrames > a.FadeOutFrames
                    && !IsDenseBake(a.OpacityTrack, leftDuration)
                    && !IsDenseBake(a.VolumeTrack, leftDuration))
                {
                    a.FadeOutFrames = b.FadeOutFrames;
                    a.FadeOutInterpolation = b.FadeOutInterpolation;
                }
                track.Clips.RemoveAt(i + 1);
                merged++;
                continue;
            }
            i++;
        }
        return merged;
    }

    private static bool IsDenseBake(KeyframeTrack<double>? track, int duration)
        => duration > 0 && track is { IsActive: true } && track.Keyframes.Count == duration;

    private static bool TransformEqual(Transform a, Transform b)
        => Math.Abs(a.CenterX - b.CenterX) < 1e-9
           && Math.Abs(a.CenterY - b.CenterY) < 1e-9
           && Math.Abs(a.Width - b.Width) < 1e-9
           && Math.Abs(a.Height - b.Height) < 1e-9
           && Math.Abs(a.Rotation - b.Rotation) < 1e-9;

    private static bool CropEqual(Crop a, Crop b)
        => Math.Abs(a.Left - b.Left) < 1e-9
           && Math.Abs(a.Right - b.Right) < 1e-9
           && Math.Abs(a.Top - b.Top) < 1e-9
           && Math.Abs(a.Bottom - b.Bottom) < 1e-9;

    private static Clip? FindClip(ProjectSession session, string clipId, out int trackIndex)
    {
        trackIndex = -1;
        var tracks = session.ActiveTimeline.Tracks;
        for (var i = 0; i < tracks.Count; i++)
        {
            var c = tracks[i].Clips.FirstOrDefault(x => x.Id == clipId);
            if (c is null) continue;
            trackIndex = i;
            return c;
        }
        return null;
    }

    private static string UniqueLabel(string raw, HashSet<string> used)
    {
        var baseLabel = new string(raw.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray()).Trim('-');
        if (string.IsNullOrEmpty(baseLabel)) baseLabel = "angle";
        var label = baseLabel;
        var n = 2;
        while (!used.Add(label))
        {
            label = $"{baseLabel}-{n}";
            n++;
        }
        return label;
    }

    private static string NextGroupName(ProjectSession session)
    {
        var n = (session.ProjectFile.MulticamGroups?.Count ?? 0) + 1;
        return $"Multicam {n}";
    }
}
