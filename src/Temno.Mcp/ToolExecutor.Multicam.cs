using System.Text.Json.Nodes;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Media.Audio;

namespace Temno.Mcp;

public static partial class ToolExecutor
{
    private static object ManageMulticam(JsonObject args)
    {
        if (args["create"] is JsonObject create)
            return ManageMulticamCreate(create);
        if (args["ungroup"] is JsonObject ungroup)
            return ManageMulticamUngroup(ungroup);
        throw new InvalidOperationException("manage_multicam: pass create or ungroup.");
    }

    private static object ManageMulticamCreate(JsonObject args)
    {
        var session = GetSession();

        // mode is optional documentation-only; sync always prefers audio then falls back to source TC.
        _ = args["mode"]?.GetValue<string>();

        if (args["members"] is not JsonArray rawMembers || rawMembers.Count < 2)
            throw new InvalidOperationException("create.members requires at least two entries (cameras and mics).");

        var specs = new List<MulticamCommands.MemberSpec>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rawMembers.Count; i++)
        {
            if (rawMembers[i] is not JsonObject m)
                throw new InvalidOperationException($"create.members[{i}] must be an object.");
            var mediaRef = m["mediaRef"]?.GetValue<string>()
                ?? throw new InvalidOperationException($"create.members[{i}]: mediaRef is required");
            if (!seen.Add(mediaRef))
                throw new InvalidOperationException($"create.members[{i}]: duplicate mediaRef {mediaRef}");

            var kindRaw = m["kind"]?.GetValue<string>() ?? "angle";
            if (!Enum.TryParse<MulticamMemberKind>(kindRaw, ignoreCase: true, out var kind))
                throw new InvalidOperationException($"create.members[{i}]: kind must be angle, mic, or both.");

            var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef)
                ?? throw new InvalidOperationException($"create.members[{i}]: media not found: {mediaRef}");

            switch (kind)
            {
                case MulticamMemberKind.angle when entry.Type != ClipType.video:
                    throw new InvalidOperationException($"create.members[{i}]: angle members must be video.");
                case MulticamMemberKind.mic when entry.Type != ClipType.audio && entry.HasAudio != true:
                    throw new InvalidOperationException($"create.members[{i}]: mic members need audio.");
                case MulticamMemberKind.both when entry.Type != ClipType.video || entry.HasAudio != true:
                    throw new InvalidOperationException($"create.members[{i}]: 'both' members must be video with audio.");
            }

            specs.Add(new MulticamCommands.MemberSpec
            {
                MediaRef = mediaRef,
                Kind = kind,
                AngleLabel = m["angleLabel"]?.GetValue<string>(),
                PinnedOffsetSeconds = Num(m["offsetSeconds"]),
            });
        }

        var master = args["master"]?.GetValue<string>()
            ?? specs.FirstOrDefault(s => s.Kind is MulticamMemberKind.angle or MulticamMemberKind.both)?.MediaRef
            ?? specs[0].MediaRef;

        string? Resolve(string mediaRef)
        {
            var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
            return entry is null ? null : ResolveAssetPath(session, entry);
        }

        var searchWindow = Num(args["searchWindowSeconds"]) ?? 240;
        var memberPairs = specs
            .Select(s => (s.MediaRef, s.PinnedOffsetSeconds))
            .ToList();
        var sync = MulticamMemberSync.SyncAsync(master, memberPairs, Resolve, searchWindow)
            .GetAwaiter().GetResult();

        var durations = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Duration, StringComparer.Ordinal);
        var types = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Type, StringComparer.Ordinal);

        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var result = MulticamCommands.CreateGroup(
            session,
            specs,
            sync.Maps,
            master,
            durations,
            types,
            args["name"]?.GetValue<string>(),
            args["startFrame"]?.GetValue<int>(),
            out var created);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "manage_multicam.create failed");

        foreach (var (mediaRef, reason) in sync.Failures)
            created.NeedsAttention.Add((mediaRef, reason));

        var extra = new Dictionary<string, object>
        {
            ["created"] = new
            {
                groupId = created.GroupId,
                clipIds = created.ClipIds,
                members = MemberRows(session, created.GroupId),
                needsAttention = created.NeedsAttention
                    .Select(n => new { mediaRef = n.MediaRef, reason = n.Reason }).ToList(),
            },
        };
        return GetMutationResult(session, snapshot, touched: created.ClipIds, extra: extra);
    }

    private static object ManageMulticamUngroup(JsonObject args)
    {
        var session = GetSession();
        var groupId = args["groupId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("ungroup.groupId is required");
        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);
        var result = MulticamCommands.Ungroup(session, groupId);
        if (!result.Ok) throw new InvalidOperationException(result.Error ?? "ungroup failed");
        return GetMutationResult(session, snapshot, extra: new Dictionary<string, object>
        {
            ["ungrouped"] = groupId,
        });
    }

    private static object ChangeCam(JsonObject args)
    {
        var session = GetSession();
        var groupId = ResolveMulticamGroupId(session, args);
        if (args["entries"] is not JsonArray entries || entries.Count == 0)
            throw new InvalidOperationException("change_cam: entries requires at least one {range, angle} or {range, layout, angles} entry.");

        var durations = session.Manifest.Entries.ToDictionary(e => e.Id, e => e.Duration, StringComparer.Ordinal);
        var sizes = session.Manifest.Entries.ToDictionary(
            e => e.Id,
            e => ((double)(e.SourceWidth ?? 0), (double)(e.SourceHeight ?? 0)),
            StringComparer.Ordinal);
        var snapshot = TakeTimelineSnapshot(session.ActiveTimeline);

        var totalSwitched = 0;
        var totalMerged = 0;
        var overlayClipIds = new List<string>();
        var applied = new List<object>();
        var clamped = new List<object>();
        var skipped = new List<object>();
        int? lo = null, hi = null;

        // Multi-entry: one ChangeCam (and undo step) per entry — agent undoes N times if needed.
        foreach (var (i, node) in entries.Select((n, i) => (i, n)))
        {
            if (node is not JsonObject entry)
                throw new InvalidOperationException($"entries[{i}] must be an object.");

            if (entry["range"] is not JsonArray range || range.Count != 2)
                throw new InvalidOperationException($"entries[{i}]: range must be [startFrame, endFrame).");
            var a = range[0]?.GetValue<int>() ?? throw new InvalidOperationException($"entries[{i}]: bad range");
            var b = range[1]?.GetValue<int>() ?? throw new InvalidOperationException($"entries[{i}]: bad range");
            if (a >= b)
                throw new InvalidOperationException($"entries[{i}]: range must be [startFrame, endFrame) with start < end.");

            string primary;
            List<string>? extras = null;
            var layout = LayoutKind.Full;

            if (entry["layout"]?.GetValue<string>() is { Length: > 0 } layoutRaw)
            {
                if (entry["angle"] is not null)
                    throw new InvalidOperationException(
                        $"entries[{i}]: pass angle for a full-frame switch OR layout + angles, not both.");
                try { layout = LayoutEngine.ParseKind(layoutRaw); }
                catch (ArgumentException ex) { throw new InvalidOperationException($"entries[{i}]: {ex.Message}"); }
                if (layout == LayoutKind.Full)
                {
                    var valid = string.Join(", ",
                        LayoutEngine.AllKinds.Where(k => k != LayoutKind.Full).Select(LayoutEngine.RawValue));
                    throw new InvalidOperationException(
                        $"entries[{i}]: unknown layout '{layoutRaw}'. Valid: {valid}. For full frame, pass angle instead.");
                }
                if (entry["angles"] is not JsonArray anglesArr || anglesArr.Count == 0)
                {
                    var slots = string.Join(", ", LayoutEngine.SlotNames(layout));
                    throw new InvalidOperationException(
                        $"entries[{i}]: layout needs angles — angleLabels in slot order ({slots}); fewer than slots leaves cells empty.");
                }
                var labels = new List<string>();
                for (var ai = 0; ai < anglesArr.Count; ai++)
                {
                    var lab = anglesArr[ai]?.GetValue<string>()
                        ?? throw new InvalidOperationException($"entries[{i}].angles[{ai}] must be a string.");
                    labels.Add(lab);
                }
                primary = labels[0];
                if (labels.Count > 1)
                    extras = labels.Skip(1).ToList();
            }
            else
            {
                primary = entry["angle"]?.GetValue<string>()
                    ?? throw new InvalidOperationException($"entries[{i}]: angle is required for full-frame switch.");
            }

            var result = MulticamCommands.ChangeCam(
                session, groupId, a, b,
                primaryAngleLabel: primary,
                extraAngleLabels: extras,
                layout: layout,
                sourceDurations: durations,
                sourceSizes: sizes,
                out var outcome);
            if (!result.Ok) throw new InvalidOperationException(result.Error ?? "change_cam failed");

            totalSwitched += outcome.Switched;
            totalMerged += outcome.Merged;
            overlayClipIds.AddRange(outcome.OverlayClipIds);
            foreach (var (s, e) in outcome.Applied)
            {
                applied.Add(new[] { s, e });
                lo = lo is null ? s : Math.Min(lo.Value, s);
                hi = hi is null ? e : Math.Max(hi.Value, e);
            }
            foreach (var c in outcome.Clamped)
                clamped.Add(new
                {
                    requested = new[] { c.ReqStart, c.ReqEnd },
                    applied = new[] { c.AppStart, c.AppEnd },
                    culprit = c.Culprit,
                });
            foreach (var s in outcome.Skipped)
                skipped.Add(new { range = new[] { s.Start, s.End }, reason = s.Reason });
        }

        var extra = new Dictionary<string, object>
        {
            ["groupId"] = groupId,
            ["switched"] = totalSwitched,
        };
        if (totalMerged > 0) extra["cutsMerged"] = totalMerged;
        if (overlayClipIds.Count > 0) extra["overlayClipIds"] = overlayClipIds;
        if (lo is not null && hi is not null)
            extra["program"] = MulticamCommands.ProgramRows(session, groupId, lo, hi);
        if (clamped.Count > 0) extra["clamped"] = clamped;
        if (skipped.Count > 0) extra["skipped"] = skipped;

        // Mutation delta from first snapshot (before all entries) — but multiple undos exist.
        // Re-snapshot after all for accurate delta vs original:
        return GetMutationResult(session, snapshot, extra: extra);
    }

    private static object GetMulticam(JsonObject args)
    {
        var session = GetSession();
        var groupId = ResolveMulticamGroupId(session, args);
        var group = MulticamCommands.FindGroup(session, groupId)
            ?? throw new InvalidOperationException($"No multicam group '{groupId}'.");

        int? start = args["startFrame"]?.GetValue<int>();
        int? end = args["endFrame"]?.GetValue<int>();
        if (start is { } s && end is { } e && s >= e)
            throw new InvalidOperationException("startFrame must be less than endFrame");

        return new
        {
            groupId,
            name = group.Name,
            members = MemberRows(session, groupId),
            program = MulticamCommands.ProgramRows(session, groupId, start, end),
            trackIndexes = MulticamCommands.TrackIndexes(session, groupId),
        };
    }

    private static string ResolveMulticamGroupId(ProjectSession session, JsonObject args)
    {
        if (args["groupId"]?.GetValue<string>() is { Length: > 0 } gid)
        {
            if (MulticamCommands.FindGroup(session, gid) is null)
                throw new InvalidOperationException($"No multicam group '{gid}'.");
            return gid;
        }
        if (args["clipId"]?.GetValue<string>() is { Length: > 0 } clipId)
        {
            var group = MulticamCommands.FindGroupOfClip(session, clipId)
                ?? throw new InvalidOperationException($"Clip '{clipId}' is not part of a multicam group.");
            return group.Id;
        }
        throw new InvalidOperationException("Pass groupId or clipId.");
    }

    private static List<object> MemberRows(ProjectSession session, string groupId)
    {
        var group = MulticamCommands.FindGroup(session, groupId);
        if (group is null) return [];
        return group.Members.Select(m => (object)new
        {
            id = m.Id,
            mediaRef = m.MediaRef,
            kind = m.Kind.ToString(),
            angleLabel = m.AngleLabel,
            offsetSeconds = m.Sync.OffsetSeconds,
            confidence = m.Sync.Confidence,
            locked = m.Sync.Locked,
            isMaster = m.Id == group.MasterMemberId,
        }).ToList();
    }
}
