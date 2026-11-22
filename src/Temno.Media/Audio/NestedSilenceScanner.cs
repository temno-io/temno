using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Media.Playback;
using System.Text.Json;

namespace Temno.Media.Audio;

/// <summary>
/// Snapshots nested carriers under the session lock, then analyzes their combined playback PCM.
/// Both silence entry points share this path so a quiet inner file cannot cause removal of
/// another inner track's audible content. Playback owns trim, speed, gain and nesting semantics.
/// </summary>
public sealed class NestedSilenceScanner
{
    private readonly TimelineAudioDecoder _decoder;

    public NestedSilenceScanner(IAudioSource? audioSource = null)
    {
        _decoder = new TimelineAudioDecoder(audioSource);
    }

    /// <summary>
    /// One hop of a carrier chain: frames addressed at <see cref="ChildFps"/> map into the
    /// carrier's own timeline at <see cref="ParentFps"/> through its placement window.
    /// Hops apply innermost-first.
    /// </summary>
    public sealed record CarrierHop(
        int ChildFps,
        int StartFrame,
        int EndFrame,
        int TrimStartFrame,
        double Speed,
        int ParentFps);

    /// <summary>
    /// An audible inner file reachable only through carriers. Its own fields map source seconds
    /// to frames of the deepest timeline (<see cref="NestedFps"/>); <see cref="Hops"/> then walks
    /// those frames out to the parent timeline, innermost hop first.
    /// </summary>
    public sealed record NestedAudioSource(
        string Path,
        int ParentTrackIndex,
        string CarrierClipId,
        int TrimStartFrame,
        int DurationFrames,
        int StartFrame,
        int EndFrame,
        double Speed,
        int NestedFps,
        IReadOnlyList<CarrierHop> Hops)
    {
        internal CarrierProgram? Program { get; init; }
    }

    internal sealed record CarrierProgram(
        Timeline Placement,
        IReadOnlyDictionary<string, Timeline> Timelines,
        IReadOnlyDictionary<string, string?> Paths);

    /// <summary>One detected silence, expressed in parent-timeline frames.</summary>
    public sealed record NestedSilenceHit(
        int ParentTrackIndex,
        string CarrierClipId,
        FrameRange ParentRange,
        double Seconds);

    /// <summary>
    /// Structural pass. Call under the session lock: <paramref name="resolveAudioPath"/> maps a
    /// clip <c>MediaRef</c> to an existing audio-bearing file path or null (the caller's usual
    /// manifest/<c>HasAudio</c>/offline checks), <paramref name="timelineLookup"/> resolves a
    /// carrier's nested timeline. Returns one entry per audible inner file; direct (non-nested)
    /// clips are the caller's own loop, not this list.
    /// </summary>
    public static IReadOnlyList<NestedAudioSource> Plan(
        Timeline parent,
        Func<string, Timeline?> timelineLookup,
        Func<string, string?> resolveAudioPath)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(timelineLookup);
        ArgumentNullException.ThrowIfNull(resolveAudioPath);

        var results = new List<NestedAudioSource>();
        var parentFps = Math.Max(1, parent.Fps);
        for (var trackIndex = 0; trackIndex < parent.Tracks.Count; trackIndex++)
        {
            var track = parent.Tracks[trackIndex];
            if (track.Muted || track.Hidden) continue;
            // Same track gate as the decoder: carriers anywhere else are never mixed.
            if (track.Type is not (ClipType.audio or ClipType.video)) continue;
            var previousEndFrame = int.MinValue;
            foreach (var clip in track.Clips.OrderBy(c => c.StartFrame))
            {
                if (clip.DurationFrames <= 0 || clip.StartFrame < previousEndFrame) continue;
                previousEndFrame = clip.EndFrame;
                if (!IsSequenceClip(clip)) continue;
                if (string.IsNullOrWhiteSpace(clip.MediaRef)) continue;
                if (!IsAudibleSpeed(clip.Speed)) continue;
                if (clip.DurationFrames <= 0) continue;
                if (timelineLookup(clip.MediaRef) is not { } nested) continue;

                var hop = new CarrierHop(
                    Math.Max(1, nested.Fps),
                    clip.StartFrame,
                    clip.EndFrame,
                    clip.TrimStartFrame,
                    clip.Speed,
                    parentFps);
                var firstSource = results.Count;
                PlanLevel(nested, timelineLookup, resolveAudioPath, [hop], depth: 1,
                    trackIndex, clip.Id, results);
                if (results.Count == firstSource) continue;

                // Snapshot under the caller's session lock. Analysis must use playback's
                // complete submix, not a union of quiet spans from independent inner files.
                var snapshots = new Dictionary<string, Timeline>(StringComparer.Ordinal);
                var paths = new Dictionary<string, string?>(StringComparer.Ordinal);
                Capture(nested, 1);
                var placement = new Timeline
                {
                    Fps = parentFps,
                    Tracks = [new Track { Type = track.Type, Clips = [Copy(clip)] }],
                };
                var program = new CarrierProgram(placement, snapshots, paths);
                for (var i = firstSource; i < results.Count; i++)
                    results[i] = results[i] with { Program = program };

                void Capture(Timeline source, int depth)
                {
                    if (snapshots.ContainsKey(source.Id)) return;
                    var snapshot = Copy(source);
                    snapshots.Add(source.Id, snapshot);
                    foreach (var child in snapshot.Tracks.SelectMany(t => t.Clips))
                    {
                        if (IsSequenceClip(child))
                        {
                            if (depth < TimelineFrameRenderer.MaxSequenceNestingDepth
                                && timelineLookup(child.MediaRef) is { } inner)
                                Capture(inner, depth + 1);
                        }
                        else if (!paths.ContainsKey(child.MediaRef))
                            paths[child.MediaRef] = resolveAudioPath(child.MediaRef);
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Runs off-lock over the immutable carrier snapshots captured by <see cref="Plan"/>.
    /// Analyze the summed stereo program once per carrier, in the parent clock. Any decode
    /// diagnostic excludes that carrier from destructive cuts; cancellation propagates.
    /// </summary>
    public async Task<IReadOnlyList<NestedSilenceHit>> ScanAsync(
        IReadOnlyList<NestedAudioSource> plan,
        SilenceDetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new SilenceDetectionOptions();
        options.Validate();

        var hits = new List<NestedSilenceHit>();
        foreach (var group in plan.GroupBy(s => (s.ParentTrackIndex, s.CarrierClipId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = group.First();
            if (source.Program is not { } program) continue;
            var timeline = program.Placement;
            var carrier = timeline.Tracks[0].Clips[0];
            var decoded = await _decoder.DecodeAsync(timeline,
                id => program.Paths.GetValueOrDefault(id), SilenceDetector.SampleRate, 2,
                cancellationToken, id => program.Timelines.GetValueOrDefault(id)).ConfigureAwait(false);

            // Unknown or failed contributors must not turn into deletable silence.
            if (decoded.Diagnostics.Count > 0 || decoded.Segments.Count == 0) continue;
            var stereo = AudioMixer.Mix(decoded.Segments, carrier.StartFrame, carrier.DurationFrames,
                timeline.Fps, SilenceDetector.SampleRate, 2);
            var peaks = new float[stereo.Length / 2];
            for (var i = 0; i < peaks.Length; i++)
                peaks[i] = Math.Max(Math.Abs(stereo[i * 2]), Math.Abs(stereo[i * 2 + 1]));
            foreach (var range in SilenceDetector.AnalyzeSamples(peaks, SilenceDetector.SampleRate, options))
            {
                int Place(double seconds) => Math.Clamp(carrier.StartFrame
                    + (int)Math.Round(seconds * timeline.Fps, MidpointRounding.AwayFromZero),
                    carrier.StartFrame, carrier.EndFrame);
                var start = Place(range.Start);
                var end = Place(range.End);
                if (end > start)
                    hits.Add(new NestedSilenceHit(
                        source.ParentTrackIndex, source.CarrierClipId,
                        new FrameRange(start, end),
                        (end - start) / (double)timeline.Fps));
            }
        }

        return hits;
    }

    /// <summary>
    /// Source seconds to timeline frames through one clip's trim/speed window. This is the exact
    /// arithmetic both silence call sites already use for direct clips (kept inline there);
    /// carriers reuse it for the innermost level so flat and nested scans agree. Seconds are
    /// reported in the mapped timeline's own frame clock, matching the call sites' totals.
    /// </summary>
    public static (int Start, int End, double Seconds)? MapSourceRangeToTimelineFrames(
        SilenceRange range,
        int trimStartFrame,
        int durationFrames,
        int startFrame,
        int endFrame,
        double speed,
        int fps)
    {
        ArgumentNullException.ThrowIfNull(range);
        speed = Math.Max(speed, 0.0001);
        var visibleStart = (double)trimStartFrame;
        var visibleEnd = visibleStart + durationFrames * speed;
        var srcStart = Math.Max(range.Start * fps, visibleStart);
        var srcEnd = Math.Min(range.End * fps, visibleEnd);
        if (srcEnd <= srcStart) return null;

        int ToTimeline(double srcFrame) => (int)Math.Round(
            startFrame + (srcFrame - visibleStart) / speed, MidpointRounding.AwayFromZero);
        var ts = Math.Clamp(ToTimeline(srcStart), startFrame, endFrame);
        var te = Math.Clamp(ToTimeline(srcEnd), startFrame, endFrame);
        if (te <= ts) return null;
        return (ts, te, (srcEnd - srcStart) / fps / speed);
    }

    /// <summary>
    /// One carrier hop: frames of the nested timeline (<paramref name="nestedFps"/>) into the
    /// carrier's own timeline (<paramref name="parentFps"/>). Inverse of the decoder's
    /// <c>SliceProgramToClip</c> window: trim is in parent frames, and only the carrier's
    /// <c>Speed</c> retimes. Rounds per hop like the direct-clip mapping rounds per clip, so a
    /// depth-2 chain can differ by a frame from a hypothetical single end-to-end rounding.
    /// </summary>
    public static FrameRange? MapNestedRangeToCarrier(
        int nestedStartFrame,
        int nestedEndFrame,
        int nestedFps,
        CarrierHop hop,
        int parentFps)
    {
        ArgumentNullException.ThrowIfNull(hop);
        if (nestedEndFrame <= nestedStartFrame) return null;
        if (!IsAudibleSpeed(hop.Speed)) return null;
        nestedFps = Math.Max(1, nestedFps);
        parentFps = Math.Max(1, parentFps);

        var trimSeconds = Math.Max(0, hop.TrimStartFrame) / (double)parentFps;
        double ToParent(int nestedFrame) =>
            hop.StartFrame + (nestedFrame / (double)nestedFps - trimSeconds) / hop.Speed * parentFps;
        var ts = Math.Clamp(
            (int)Math.Round(ToParent(nestedStartFrame), MidpointRounding.AwayFromZero),
            hop.StartFrame, hop.EndFrame);
        var te = Math.Clamp(
            (int)Math.Round(ToParent(nestedEndFrame), MidpointRounding.AwayFromZero),
            hop.StartFrame, hop.EndFrame);
        return te > ts ? new FrameRange(ts, te) : null;
    }

    private static void PlanLevel(
        Timeline timeline,
        Func<string, Timeline?> timelineLookup,
        Func<string, string?> resolveAudioPath,
        IReadOnlyList<CarrierHop> outerHops,
        int depth,
        int parentTrackIndex,
        string carrierClipId,
        List<NestedAudioSource> results)
    {
        // Same cap the decoder enforces: past it the renderer draws a placeholder and the mix
        // contributes nothing, so there is no audible silence to report either.
        if (depth > TimelineFrameRenderer.MaxSequenceNestingDepth)
            return;

        var fps = Math.Max(1, timeline.Fps);
        foreach (var track in timeline.Tracks)
        {
            if (track.Muted || track.Hidden) continue;
            if (track.Type is not (ClipType.audio or ClipType.video)) continue;

            var previousEndFrame = int.MinValue;
            foreach (var clip in track.Clips.OrderBy(c => c.StartFrame))
            {
                if (clip.DurationFrames <= 0) continue;
                if (clip.StartFrame < previousEndFrame) continue;
                previousEndFrame = clip.EndFrame;

                if (IsSequenceClip(clip))
                {
                    if (string.IsNullOrWhiteSpace(clip.MediaRef)) continue;
                    if (!IsAudibleSpeed(clip.Speed)) continue;
                    if (timelineLookup(clip.MediaRef) is not { } nested) continue;

                    var hop = new CarrierHop(
                        Math.Max(1, nested.Fps),
                        clip.StartFrame,
                        clip.EndFrame,
                        clip.TrimStartFrame,
                        clip.Speed,
                        fps);
                    PlanLevel(nested, timelineLookup, resolveAudioPath, [hop, .. outerHops],
                        depth + 1, parentTrackIndex, carrierClipId, results);
                    continue;
                }

                if (track.Type != ClipType.audio || !IsAudibleSpeed(clip.Speed)) continue;
                if (clip.MediaType is not (ClipType.audio or ClipType.video)) continue;
                if (string.IsNullOrEmpty(clip.MediaRef)) continue;
                if (resolveAudioPath(clip.MediaRef) is not { } path) continue;

                results.Add(new NestedAudioSource(
                    path, parentTrackIndex, carrierClipId,
                    clip.TrimStartFrame, clip.DurationFrames, clip.StartFrame, clip.EndFrame,
                    clip.Speed, fps, outerHops));
            }
        }
    }

    private static bool IsSequenceClip(Clip clip)
        => clip.SourceClipType == ClipType.sequence || clip.MediaType == ClipType.sequence;

    private static bool IsAudibleSpeed(double speed)
        => double.IsFinite(speed) && speed > 0;

    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(value, JsonOptions.ForProject), JsonOptions.ForProject)!;
}
