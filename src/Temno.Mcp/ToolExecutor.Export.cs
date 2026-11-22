using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.Export;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Media.Export;
using Temno.Media.Playback;

namespace Temno.Mcp;

// WP-170: export_project's allowOfflineMedia. WP-154 made the exporter refuse a video export that
// would be missing picture, and gave only the editor a way through: its warning names what is offline
// and "Export anyway" sets TimelineExporter.AllowPartialPicture. An agent cannot be shown that list
// before it decides, so it opts in with the call and the result names what was offline and where.

public static partial class ToolExecutor
{
    /// <summary>Opt-in exports by job id, so manage_exports can say what each one left out.</summary>
    private static readonly ConcurrentDictionary<string, OfflineExportReceipt> _offlineExports =
        new(StringComparer.Ordinal);

    private static bool _offlineExportHooked;

    /// <summary>
    /// The editor's preflight, run the way <c>EditorPage.ConfirmOfflineMediaAsync</c> runs it: collect
    /// under the session gate, classify outside it. export_project is off the dispatcher, so the disk
    /// half runs on this thread.
    /// </summary>
    private static ExportMediaPreflightReport RunExportPreflight(ProjectSession session, Timeline timeline)
    {
        var plan = ProjectHub.RunSerialized(() => ExportMediaPreflight.Collect(
            timeline,
            session.Manifest,
            SessionTimelineLookup.For(session),
            session.PackagePath));
        return ExportMediaPreflight.Classify(plan, session.Resolver, !string.IsNullOrEmpty(session.PackagePath));
    }

    /// <summary>
    /// One row per reference the preflight will not find, with the frames of the exported timeline that
    /// use it. The ranges read the session's timelines, so they are computed under the gate.
    /// </summary>
    private static List<object> OfflineMediaJson(
        ProjectSession session, Timeline timeline, ExportMediaPreflightReport report)
    {
        var problems = report.Problems;
        if (problems.Count == 0) return [];
        var lookup = SessionTimelineLookup.For(session);
        return ProjectHub.RunSerialized(() => problems
            .Select(p => (object)new
            {
                mediaRef = p.Reference,
                name = p.Name,
                state = char.ToLowerInvariant(p.State.ToString()[0]) + p.State.ToString()[1..],
                reason = p.Explanation,
                ranges = ExportMediaRanges.Where(timeline, p.Reference, lookup)
                    .Select(r => new { startFrame = r.StartFrame, endFrame = r.EndFrame })
                    .ToList(),
            })
            .ToList());
    }

    /// <summary>
    /// Display names for every reference the export reaches, taken from the preflight so that naming
    /// what the render dropped needs no second read of the session, and still works after it closes.
    /// </summary>
    private static Dictionary<string, string> PreflightNames(ExportMediaPreflightReport report)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in report.Items) names.TryAdd(item.Reference, item.Name);
        return names;
    }

    /// <summary>What the render left out of the picture: the exporter's own record, one row per clip.</summary>
    private static List<object> MissingPictureJson(
        IReadOnlyList<TimelineFrameRenderer.SkippedLayer> dropped, IReadOnlyDictionary<string, string> names)
        => dropped
            .Select(d => (object)new { clipId = d.ClipId, mediaRef = d.MediaRef, name = names.GetValueOrDefault(d.MediaRef, d.MediaRef), reason = d.Reason })
            .ToList();

    /// <summary>The editor's completion line (<c>EditorPage.DroppedPictureSuffix</c>), as a sentence.</summary>
    private static string? MissingPictureSentence(
        IReadOnlyList<TimelineFrameRenderer.SkippedLayer> dropped, IReadOnlyDictionary<string, string> names)
    {
        if (dropped.Count == 0) return null;
        var named = string.Join(", ", dropped.Take(3).Select(d => $"“{names.GetValueOrDefault(d.MediaRef, d.MediaRef)}”"));
        var more = dropped.Count > 3 ? $" and {dropped.Count - 3} more" : "";
        var noun = dropped.Count == 1 ? "clip is" : "clips are";
        return $"{dropped.Count} {noun} missing from the picture: {named}{more}.";
    }

    /// <summary>The editor's warning, then its completion line once the render has run.</summary>
    private static IEnumerable<string> OfflineWarnings(ExportMediaPreflightReport preflight, string? missingPicture)
    {
        if (preflight.Sentence is { } before) yield return before;
        if (missingPicture is not null) yield return missingPicture;
    }

    /// <summary>
    /// The result an opted-in call returns: the ordinary one, plus what was offline. Built on the
    /// ordinary result so the default call's shape cannot drift from it.
    /// </summary>
    private static JsonObject WithOfflineReceipt(
        object result, List<object> offlineMedia, List<string> warnings, List<object>? missingPicture)
    {
        var node = JsonSerializer.SerializeToNode(result)!.AsObject();
        node["offlineMedia"] = JsonSerializer.SerializeToNode(offlineMedia);
        if (missingPicture is not null) node["missingPicture"] = JsonSerializer.SerializeToNode(missingPicture);
        if (warnings.Count > 0) node["warnings"] = JsonSerializer.SerializeToNode(warnings);
        return node;
    }

    /// <summary>
    /// Keeps a queued opt-in export's receipt for manage_exports. The receipt holds the exporter only
    /// until the job settles and takes its names from the preflight, so a row never reads the session.
    /// </summary>
    private static void TrackOfflineExport(
        ExportJob job, TimelineExporter exporter, ExportMediaPreflightReport report)
    {
        // Subscribed before the flag is published: a second export must not see the flag, skip the
        // subscription, and finish before the first caller has attached the handler.
        lock (_offlineExports)
        {
            if (!_offlineExportHooked)
            {
                _exportQueue.JobChanged += changed =>
                {
                    if (changed.State.IsFinished() && _offlineExports.TryGetValue(changed.Id, out var receipt))
                        receipt.Settle();
                };
                _offlineExportHooked = true;
            }
        }

        var tracked = _offlineExports.GetOrAdd(
            job.Id, _ => new OfflineExportReceipt(exporter, report.Sentence, PreflightNames(report)));
        // The job can finish before it is in the map, and then its JobChanged has already gone by.
        if (job.State.IsFinished()) tracked.Settle();
    }

    /// <summary>manage_exports list: what an opt-in export left out, once the render has said.</summary>
    private static void AddOfflineExportFields(ExportJob job, Dictionary<string, object> row)
    {
        if (!_offlineExports.TryGetValue(job.Id, out var receipt)) return;
        if (job.State.IsFinished()) receipt.Settle();
        var dropped = job.State == ExportJobState.Completed ? receipt.Dropped : null;

        var warnings = new List<string>();
        if (receipt.PreflightSentence is { } before) warnings.Add(before);
        if (dropped is not null && MissingPictureSentence(dropped, receipt.Names) is { } after) warnings.Add(after);
        // The Mac's manage_exports row carries its warnings under the same name.
        if (warnings.Count > 0) row["warnings"] = warnings;
        if (dropped is not null) row["missingPicture"] = MissingPictureJson(dropped, receipt.Names);
    }

    private sealed class OfflineExportReceipt(
        TimelineExporter exporter, string? preflightSentence, IReadOnlyDictionary<string, string> names)
    {
        private readonly object _gate = new();
        private TimelineExporter? _exporter = exporter;
        private IReadOnlyList<TimelineFrameRenderer.SkippedLayer>? _dropped;

        public string? PreflightSentence => preflightSentence;
        public IReadOnlyDictionary<string, string> Names => names;

        /// <summary>Null until the job has settled.</summary>
        public IReadOnlyList<TimelineFrameRenderer.SkippedLayer>? Dropped { get { lock (_gate) return _dropped; } }

        /// <summary>Called once the job is finished, when the exporter's record is final.</summary>
        public void Settle()
        {
            lock (_gate)
            {
                if (_exporter is null) return;
                _dropped = _exporter.LastVideoDiagnostics;
                _exporter = null;
            }
        }
    }
}

/// <summary>A span of timeline frames, end exclusive.</summary>
public readonly record struct ExportFrameRange(int StartFrame, int EndFrame);

/// <summary>
/// WP-170. Where an export's timeline uses one media reference, in that timeline's frames, so an agent
/// that exported over offline media is told which parts of the file are blank or silent.
/// <para>
/// Reach mirrors <see cref="ExportMediaPreflight.Collect"/>, which decided that the reference is
/// offline: hidden tracks are skipped, a video track reaches picture, an unmuted track reaches sound,
/// and nested sequences are followed to <see cref="ExportMediaPreflight.MaxSequenceNestingDepth"/>.
/// Like the renderer, the walk is bounded by that depth and not by revisiting a timeline, so a cycle is
/// followed as far as the render follows it (WP-170 T1 finding 1). A nested sequence's frames are mapped back through its carrier clip with the renderer's own
/// formula (<see cref="TimelineFrameResolver.SourceSecondsAt"/>, then the nested frame rate), so trim,
/// speed and a different frame rate land where the render puts them. A transition that has an offline
/// clip on either side blends that side from black for the whole of its window, so the window counts.
/// </para>
/// <para>
/// This is where the timeline places the reference. It does not model a clip hidden behind another on
/// the same track or faded to zero opacity; the picture there is not missing for this reason.
/// </para>
/// </summary>
public static class ExportMediaRanges
{
    public static IReadOnlyList<ExportFrameRange> Where(
        Timeline timeline, string reference, Func<string, Timeline?>? timelineLookup = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentException.ThrowIfNullOrEmpty(reference);
        // One entry per timeline and depth: the depth cap then bounds the work, cycles included.
        var memo = new Dictionary<(string, int), List<ExportFrameRange>>();
        return Merge(Walk(timeline, reference, timelineLookup, 0, memo), 0, Math.Max(0, timeline.TotalFrames));
    }

    private static List<ExportFrameRange> Walk(
        Timeline timeline,
        string reference,
        Func<string, Timeline?>? lookup,
        int depth,
        Dictionary<(string, int), List<ExportFrameRange>> memo)
    {
        var ranges = new List<ExportFrameRange>();
        var pictureClips = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in timeline.Tracks)
        {
            if (track.Hidden) continue;

            foreach (var clip in track.Clips)
            {
                if (clip.DurationFrames <= 0 || string.IsNullOrEmpty(clip.MediaRef)) continue;
                var reachedByVideo = track.Type != ClipType.audio
                    && clip.MediaType is not (ClipType.text or ClipType.audio);
                var reachedByAudio = track.Type is ClipType.audio or ClipType.video
                    && !track.Muted;
                if (!reachedByVideo && !reachedByAudio) continue;

                // Also how a nested sequence that does not resolve is reported: by its timeline id.
                if (string.Equals(clip.MediaRef, reference, StringComparison.Ordinal))
                {
                    ranges.Add(new ExportFrameRange(clip.StartFrame, clip.EndFrame));
                    if (reachedByVideo) pictureClips.Add(clip.Id);
                    continue;
                }

                // Both fields, as the preflight tests them: they are set independently.
                if (clip.MediaType != ClipType.sequence && clip.SourceClipType != ClipType.sequence) continue;
                var nested = lookup?.Invoke(clip.MediaRef);
                if (nested is null || depth + 1 > ExportMediaPreflight.MaxSequenceNestingDepth)
                    continue;
                if (!memo.TryGetValue((nested.Id, depth + 1), out var inner))
                {
                    inner = Merge(Walk(nested, reference, lookup, depth + 1, memo), 0, int.MaxValue);
                    memo[(nested.Id, depth + 1)] = inner;
                }
                if (inner.Count > 0)
                    ranges.AddRange(ThroughCarrier(clip, timeline.Fps, nested.Fps, inner));
            }
        }

        // Transitions join two video or image clips (Timeline.AdjacentVideoClipPair), never a carrier.
        foreach (var rt in timeline.ResolvedTransitions)
        {
            if (pictureClips.Contains(rt.ClipA.Id) || pictureClips.Contains(rt.ClipB.Id))
                ranges.Add(new ExportFrameRange(rt.StartFrame, rt.EndFrame));
        }
        return ranges;
    }

    /// <summary>
    /// The carrier's frames whose nested frame falls in <paramref name="inner"/>. Same mapping as
    /// <c>TimelineFrameRenderer.NestedTimelineFrame</c>: the carrier's source seconds at the parent
    /// frame, times the nested frame rate, rounded.
    /// </summary>
    private static List<ExportFrameRange> ThroughCarrier(
        Clip carrier, int parentFps, int nestedFps, List<ExportFrameRange> inner)
    {
        parentFps = Math.Max(1, parentFps);
        nestedFps = Math.Max(1, nestedFps);
        int NestedFrameAt(int frame) => Math.Max(0, (int)Math.Round(
            TimelineFrameResolver.SourceSecondsAt(carrier, frame, parentFps) * nestedFps));

        var runs = new List<ExportFrameRange>();
        if (carrier.Speed > 0)
        {
            // Playing forward, the nested frame never decreases along the carrier, so each inner span is
            // one run of parent frames and two searches find it; a two-hour carrier costs ~40 probes, not
            // a pass over every frame per offline file (WP-170 T1 lead).
            int FirstAtOrPast(int nestedFrame)
            {
                int lo = carrier.StartFrame, hi = carrier.EndFrame;
                while (lo < hi)
                {
                    var mid = lo + ((hi - lo) >> 1);
                    if (NestedFrameAt(mid) >= nestedFrame) hi = mid;
                    else lo = mid + 1;
                }
                return lo;
            }

            foreach (var span in inner)
            {
                var start = FirstAtOrPast(span.StartFrame);
                var end = FirstAtOrPast(span.EndFrame);
                if (end > start) runs.Add(new ExportFrameRange(start, end));
            }
            return runs;
        }

        var runStart = -1;
        for (var frame = carrier.StartFrame; frame < carrier.EndFrame; frame++)
        {
            var hit = Contains(inner, NestedFrameAt(frame));
            if (hit && runStart < 0) runStart = frame;
            else if (!hit && runStart >= 0)
            {
                runs.Add(new ExportFrameRange(runStart, frame));
                runStart = -1;
            }
        }
        if (runStart >= 0) runs.Add(new ExportFrameRange(runStart, carrier.EndFrame));
        return runs;
    }

    private static bool Contains(List<ExportFrameRange> sorted, int frame)
    {
        int lo = 0, hi = sorted.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            if (frame < sorted[mid].StartFrame) hi = mid - 1;
            else if (frame >= sorted[mid].EndFrame) lo = mid + 1;
            else return true;
        }
        return false;
    }

    /// <summary>Sorted, with overlapping or touching spans joined, clipped to [min, max).</summary>
    private static List<ExportFrameRange> Merge(List<ExportFrameRange> ranges, int min, int max)
    {
        var merged = new List<ExportFrameRange>();
        foreach (var r in ranges.OrderBy(r => r.StartFrame))
        {
            var start = Math.Max(min, r.StartFrame);
            var end = Math.Min(max, r.EndFrame);
            if (end <= start) continue;
            if (merged.Count > 0 && start <= merged[^1].EndFrame)
            {
                if (end > merged[^1].EndFrame) merged[^1] = merged[^1] with { EndFrame = end };
                continue;
            }
            merged.Add(new ExportFrameRange(start, end));
        }
        return merged;
    }
}
