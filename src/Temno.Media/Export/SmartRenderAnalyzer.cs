using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media.Compositing;
using Temno.Media.Playback;

namespace Temno.Media.Export;

/// <summary>
/// Analyzes a timeline to determine which portions can be stream-copied (smart rendered)
/// without decoding, compositing, and re-encoding.
/// </summary>
public sealed class SmartRenderAnalyzer
{
    /// <summary>
    /// Minimum number of frames for a copy segment. A short copy costs two extra ffmpeg
    /// invocations and a concat boundary, which is slower than simply encoding those frames.
    /// </summary>
    public const int MinCopyFrames = 30;

    /// <summary>
    /// Frame height at or above which an untagged source is assumed BT.709 rather than BT.601.
    /// Matches the convention decoders use when a stream carries no colour description.
    /// </summary>
    public const int HdHeightThreshold = 720;

    /// <summary>
    /// Deepest display reordering H.264 and HEVC permit (sixteen frames of decoded-picture buffer).
    /// Once more frames than this are displayed ahead of one still missing, no longer copy can
    /// become exact, so the proof stops looking.
    /// </summary>
    public const int MaxReorderFrames = 16;

    /// <summary>
    /// How far, in frames, a packet's timestamp may sit from its slot on the keyframe's frame grid.
    /// Container timebases round (a millisecond timebase is 0.03 frames off at 30 fps); anything
    /// near half a frame is a different frame.
    /// </summary>
    private const double GridToleranceFrames = 0.25;

    /// <summary>
    /// Floating-point slack, in frames, when deciding which source frame a timeline instant shows.
    /// Probe times carry microsecond rounding; this is far below any real frame offset.
    /// </summary>
    private const double SampleEpsilonFrames = 0.001;

    private readonly IVideoStreamProbe _probe;
    private readonly MediaResolver? _mediaResolver;

    /// <summary>
    /// WP-399: a clip's <c>mediaRef</c> used as a file only when it has no manifest entry (an entry that did
    /// not resolve stays unresolved, even if its id is a path) and, in a restricted session, only when the
    /// user allowed that path. Decided from text before <c>File.Exists</c>, so a host the project names is
    /// never contacted.
    /// </summary>
    private string? LoosePath(string mediaRef)
    {
        if (string.IsNullOrEmpty(mediaRef))
            return null;
        if (_mediaResolver is not null
            && (_mediaResolver.Entry(mediaRef) is not null || !_mediaResolver.AllowsLooseFile(mediaRef)))
            return null;
        return File.Exists(mediaRef) ? mediaRef : null;
    }

    public SmartRenderAnalyzer(IVideoStreamProbe probe, MediaResolver? mediaResolver = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
        _mediaResolver = mediaResolver;
    }

    public async Task<SmartRenderPlan> AnalyzeAsync(
        Timeline timeline,
        VideoCodec targetCodec,
        int outputWidth,
        int outputHeight,
        int totalFrames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (outputWidth <= 0) throw new ArgumentOutOfRangeException(nameof(outputWidth));
        if (outputHeight <= 0) throw new ArgumentOutOfRangeException(nameof(outputHeight));
        if (totalFrames < 0) throw new ArgumentOutOfRangeException(nameof(totalFrames));

        if (totalFrames == 0)
        {
            return SmartRenderPlan.Create([], 0, "Timeline has no frames to export.");
        }

        // ProRes and HevcHdr are never eligible for smart rendering.
        // ProRes cannot be carried in MPEG-TS intermediate, and HDR requires colour-conversion filters.
        if (targetCodec == VideoCodec.ProRes)
        {
            return SmartRenderPlan.Create(
                [new SmartRenderSegment(SmartRenderSegmentKind.Encode, 0, totalFrames)],
                totalFrames,
                "ProRes export target does not support smart rendering.");
        }

        if (targetCodec == VideoCodec.HevcHdr)
        {
            return SmartRenderPlan.Create(
                [new SmartRenderSegment(SmartRenderSegmentKind.Encode, 0, totalFrames)],
                totalFrames,
                "HEVC HDR export target requires colour conversion and does not support smart rendering.");
        }

        if (targetCodec is not (VideoCodec.H264 or VideoCodec.H265))
        {
            return SmartRenderPlan.Create(
                [new SmartRenderSegment(SmartRenderSegmentKind.Encode, 0, totalFrames)],
                totalFrames,
                $"Export target codec {targetCodec} does not support smart rendering.");
        }

        var clipsById = new Dictionary<string, Clip>(StringComparer.Ordinal);
        foreach (var track in timeline.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                clipsById.TryAdd(clip.Id, clip);
            }
        }

        var activeTransitions = timeline.ResolvedTransitions;

        // Step 1: Per-frame eligibility analysis
        var frameCheckResults = new FrameCheckResult[totalFrames];
        string? firstIneligibilityReason = null;

        for (var frame = 0; frame < totalFrames; frame++)
        {
            var check = CheckFrameEligibility(timeline, frame, activeTransitions, clipsById);
            frameCheckResults[frame] = check;
            if (!check.IsEligible && firstIneligibilityReason == null)
            {
                firstIneligibilityReason = check.IneligibilityReason;
            }
        }

        // Group consecutive eligible frames sharing the same ClipId and MediaPath into candidate runs
        var candidateRuns = new List<CandidateRun>();
        int runStart = -1;
        string? currentClipId = null;
        string? currentMediaPath = null;

        for (var frame = 0; frame < totalFrames; frame++)
        {
            var check = frameCheckResults[frame];
            if (check.IsEligible)
            {
                if (runStart < 0)
                {
                    runStart = frame;
                    currentClipId = check.ClipId;
                    currentMediaPath = check.MediaPath;
                }
                else if (check.ClipId != currentClipId || check.MediaPath != currentMediaPath)
                {
                    candidateRuns.Add(new CandidateRun(runStart, frame - runStart, currentClipId!, currentMediaPath!));
                    runStart = frame;
                    currentClipId = check.ClipId;
                    currentMediaPath = check.MediaPath;
                }
            }
            else
            {
                if (runStart >= 0)
                {
                    candidateRuns.Add(new CandidateRun(runStart, frame - runStart, currentClipId!, currentMediaPath!));
                    runStart = -1;
                    currentClipId = null;
                    currentMediaPath = null;
                }
            }
        }

        if (runStart >= 0)
        {
            candidateRuns.Add(new CandidateRun(runStart, totalFrames - runStart, currentClipId!, currentMediaPath!));
        }

        // Step 2 & 3: Probe source files and apply GOP alignment
        var rawSegments = new List<SmartRenderSegment>();
        var keyframeIndexes = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        var seekInexactReasons = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var copyClipPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentTimelineFrame = 0;

        foreach (var run in candidateRuns)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Fill gap before run with Encode segment
            if (run.StartFrame > currentTimelineFrame)
            {
                rawSegments.Add(new SmartRenderSegment(
                    SmartRenderSegmentKind.Encode,
                    currentTimelineFrame,
                    run.StartFrame - currentTimelineFrame));
                currentTimelineFrame = run.StartFrame;
            }

            // Check source time progression: SourceSeconds delta must match frame interval
            var s0 = frameCheckResults[run.StartFrame].SourceSeconds;
            var sLast = frameCheckResults[run.EndFrame - 1].SourceSeconds;
            var expectedDelta = (run.FrameCount - 1) / (double)timeline.Fps;
            var halfFrame = 0.5 / timeline.Fps;

            if (Math.Abs((sLast - s0) - expectedDelta) > halfFrame)
            {
                firstIneligibilityReason ??= "source time progression is non-linear";
                rawSegments.Add(new SmartRenderSegment(SmartRenderSegmentKind.Encode, run.StartFrame, run.FrameCount));
                currentTimelineFrame = run.EndFrame;
                continue;
            }

            // Step 2: Probe the source file
            VideoStreamInfo probeInfo;
            try
            {
                probeInfo = await _probe.ProbeAsync(run.MediaPath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                firstIneligibilityReason ??= $"failed to probe source '{run.MediaPath}': {ex.Message}";
                rawSegments.Add(new SmartRenderSegment(SmartRenderSegmentKind.Encode, run.StartFrame, run.FrameCount));
                currentTimelineFrame = run.EndFrame;
                continue;
            }

            // Verify codec match
            var codecMatches = targetCodec switch
            {
                VideoCodec.H264 => string.Equals(probeInfo.CodecName, "h264", StringComparison.OrdinalIgnoreCase),
                VideoCodec.H265 => string.Equals(probeInfo.CodecName, "hevc", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };

            if (!codecMatches)
            {
                firstIneligibilityReason ??= $"source codec '{probeInfo.CodecName}' does not match target '{(targetCodec == VideoCodec.H264 ? "h264" : "hevc")}'";
                rawSegments.Add(new SmartRenderSegment(SmartRenderSegmentKind.Encode, run.StartFrame, run.FrameCount));
                currentTimelineFrame = run.EndFrame;
                continue;
            }

            // WP-405: a turned or anamorphic source decodes upright with square pixels; its packets do not.
            if (probeInfo.Reshaped)
            {
                firstIneligibilityReason ??= "source is stored turned or with non-square pixels, so a stream copy would not match its decoded picture";
                rawSegments.Add(new SmartRenderSegment(SmartRenderSegmentKind.Encode, run.StartFrame, run.FrameCount));
                currentTimelineFrame = run.EndFrame;
                continue;
            }

            // Verify resolution match
            if (probeInfo.Width != outputWidth || probeInfo.Height != outputHeight)
            {
                firstIneligibilityReason ??= $"source resolution {probeInfo.Width}x{probeInfo.Height} does not match export resolution {outputWidth}x{outputHeight}";
                rawSegments.Add(new SmartRenderSegment(SmartRenderSegmentKind.Encode, run.StartFrame, run.FrameCount));
                currentTimelineFrame = run.EndFrame;
                continue;
            }

            // Verify framerate match
            if (Math.Abs(probeInfo.FrameRate - timeline.Fps) >= 0.01)
            {
                firstIneligibilityReason ??= $"source is {probeInfo.Width}x{probeInfo.Height} {probeInfo.CodecName} {probeInfo.PixelFormat} @ {probeInfo.FrameRate:0.##} but the export target is {timeline.Fps} fps";
                rawSegments.Add(new SmartRenderSegment(SmartRenderSegmentKind.Encode, run.StartFrame, run.FrameCount));
                currentTimelineFrame = run.EndFrame;
                continue;
            }

            // Verify pixel format
            if (!string.Equals(probeInfo.PixelFormat, "yuv420p", StringComparison.OrdinalIgnoreCase))
            {
                firstIneligibilityReason ??= $"source pixel format '{probeInfo.PixelFormat}' is not yuv420p";
                rawSegments.Add(new SmartRenderSegment(SmartRenderSegmentKind.Encode, run.StartFrame, run.FrameCount));
                currentTimelineFrame = run.EndFrame;
                continue;
            }

            // Verify colour description.
            //
            // The mux pass stamps `-color_primaries bt709 -color_trc bt709 -colorspace bt709
            // -color_range tv` on the finished file unconditionally (ExportEncodeArguments
            // .CopyColorFlags). For a re-encode that is honest: the filter chain really did convert
            // the pixels to BT.709. For a stream copy it is only a relabel — the YUV passes through
            // untouched — so the copy is safe ONLY if the source already is BT.709.
            //
            // An explicitly tagged non-BT.709 source is therefore rejected. An *untagged* source is
            // the subtle case: decoders resolve missing tags by resolution, BT.709 for HD and
            // BT.601 for SD. Copying an untagged SD source and stamping BT.709 on it would shift
            // every hue in the file, so untagged sources are only eligible at HD.
            static bool TagOk(string? tag) => tag is null || string.Equals(tag, "bt709", StringComparison.OrdinalIgnoreCase);
            var taggedBt709 = TagOk(probeInfo.ColorPrimaries) && TagOk(probeInfo.ColorTransfer) && TagOk(probeInfo.ColorSpace);
            var untagged = probeInfo.ColorPrimaries is null || probeInfo.ColorTransfer is null || probeInfo.ColorSpace is null;

            if (!taggedBt709)
            {
                firstIneligibilityReason ??= $"source colour tagging is not BT.709 (primaries: {probeInfo.ColorPrimaries}, transfer: {probeInfo.ColorTransfer}, space: {probeInfo.ColorSpace})";
                rawSegments.Add(new SmartRenderSegment(SmartRenderSegmentKind.Encode, run.StartFrame, run.FrameCount));
                currentTimelineFrame = run.EndFrame;
                continue;
            }

            if (untagged && probeInfo.Height < HdHeightThreshold)
            {
                firstIneligibilityReason ??= $"source is untagged and below HD ({probeInfo.Width}x{probeInfo.Height}), so it is conventionally BT.601 and a stream copy would be relabelled BT.709";
                rawSegments.Add(new SmartRenderSegment(SmartRenderSegmentKind.Encode, run.StartFrame, run.FrameCount));
                currentTimelineFrame = run.EndFrame;
                continue;
            }

            // Step 3: place copies on keyframes and end each one exactly (WP-234).
            //
            // A stream copy of N frames is the first N packets after the keyframe in DECODE order.
            // With B-frames that is not the first N frames in display order: cutting an I B B P
            // stream after "I P B" shows frames 0, 1 and 3 and drops 2, and a frame count cannot
            // tell. So every copy is proven against the packet table: it may only end where the
            // decode-order prefix is exactly the display run, and whatever cannot be proven is
            // encoded instead.
            if (probeInfo.Packets is not { Count: > 0 } packets)
            {
                firstIneligibilityReason ??= "source packet order was not probed, so no stream-copy boundary can be proven exact";
                rawSegments.Add(new SmartRenderSegment(SmartRenderSegmentKind.Encode, run.StartFrame, run.FrameCount));
                currentTimelineFrame = run.EndFrame;
                continue;
            }

            if (!keyframeIndexes.TryGetValue(run.MediaPath, out var keyframes))
            {
                keyframes = KeyframePacketsByTime(packets);
                keyframeIndexes[run.MediaPath] = keyframes;
            }

            // The render that continues this source after a copy re-seeks it (ExportFrameSource).
            // Only some sources seek exactly to any frame; the rest only to a keyframe.
            if (!seekInexactReasons.TryGetValue(run.MediaPath, out var seekInexactReason))
            {
                seekInexactReason = SeekInexactReason(probeInfo, packets);
                seekInexactReasons[run.MediaPath] = seekInexactReason;
            }

            // The export shows, at source time t, the first frame presented at or after t (ffmpeg's
            // -ss). A keyframe therefore opens the copy at the first timeline frame whose source time
            // is not after it. Sources whose video starts part-way into the container's first frame
            // interval — MPEG-TS, where the audio leads — sit off the timeline's grid by that part,
            // and still map one-to-one.
            var epsilonSeconds = SampleEpsilonFrames / timeline.Fps;
            var cursor = run.StartFrame;
            var searchFromSeconds = s0 - epsilonSeconds;
            var copiedInRun = false;
            string? runReason = null;
            while (cursor < run.EndFrame)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = FirstKeyframeAtOrAfter(keyframes, packets, searchFromSeconds);
                if (next < 0)
                {
                    runReason ??= "no keyframe found at or after segment start";
                    break;
                }

                var keyPacket = keyframes[next];
                var k0 = packets[keyPacket].PtsSeconds;
                // Whatever happens below, the next attempt starts after this keyframe.
                searchFromSeconds = k0 + halfFrame;

                var copyStartFrame = run.StartFrame + (int)Math.Floor((k0 - s0) * timeline.Fps + SampleEpsilonFrames);
                if (copyStartFrame >= run.EndFrame)
                {
                    runReason ??= $"no keyframe inside the last {run.EndFrame - cursor} frames of the run";
                    break;
                }

                var copyFrameCount = ExactCopyLength(packets, keyPacket, k0, timeline.Fps, run.EndFrame - copyStartFrame);
                if (copyFrameCount < MinCopyFrames)
                {
                    runReason ??= copyFrameCount == run.EndFrame - copyStartFrame
                        ? $"copy segment of {copyFrameCount} frames is shorter than minimum {MinCopyFrames} frames"
                        : $"the stream copy from keyframe {k0:0.###}s is exact for only {copyFrameCount} frames (minimum {MinCopyFrames}): packets after it are reordered past the cut, lead the keyframe, or are hidden";
                    continue;
                }

                // Where this source is still on screen right after the copy, the render there
                // re-seeks it: a copy leaves the decoder behind. A source that cannot seek exactly
                // mid-GOP must then be showing a keyframe, so the copy is cut back to the last
                // keyframe it contains and the frames up to the old end are rendered from there.
                if (seekInexactReason is not null
                    && ReseeksOffKeyframe(timeline, copyStartFrame + copyFrameCount, totalFrames, run.MediaPath, keyframes, packets, timeline.Fps))
                {
                    var aligned = KeyframeAlignedCopyLength(packets, keyPacket, k0, timeline.Fps, copyFrameCount - 1);
                    if (aligned < MinCopyFrames)
                    {
                        runReason ??= $"the stream copy from keyframe {k0:0.###}s would hand the render after it a mid-GOP seek, which is not exact for this source ({seekInexactReason}), and it holds no keyframe-aligned end at least {MinCopyFrames} frames long";
                        continue;
                    }

                    copyFrameCount = aligned;
                }

                if (copyStartFrame > cursor)
                {
                    rawSegments.Add(new SmartRenderSegment(SmartRenderSegmentKind.Encode, cursor, copyStartFrame - cursor));
                }

                rawSegments.Add(new SmartRenderSegment(
                    Kind: SmartRenderSegmentKind.Copy,
                    StartFrame: copyStartFrame,
                    FrameCount: copyFrameCount,
                    SourcePath: run.MediaPath,
                    SourceStartSeconds: k0));
                copyClipPaths.Add(run.MediaPath);
                copiedInRun = true;

                cursor = copyStartFrame + copyFrameCount;
                searchFromSeconds = s0 + (cursor - run.StartFrame) / (double)timeline.Fps - epsilonSeconds;
            }

            // The tail the copy could not reach exactly — reordered B-frames past the cut, a
            // missing keyframe — is encoded. That is the fix for the audited defect: it used to be
            // copied anyway, with the right frame count and the wrong last frames.
            if (cursor < run.EndFrame)
            {
                rawSegments.Add(new SmartRenderSegment(SmartRenderSegmentKind.Encode, cursor, run.EndFrame - cursor));
            }

            if (!copiedInRun)
            {
                firstIneligibilityReason ??= runReason;
            }

            currentTimelineFrame = run.EndFrame;
        }

        // Fill trailing gap to totalFrames with Encode segment
        if (currentTimelineFrame < totalFrames)
        {
            rawSegments.Add(new SmartRenderSegment(
                SmartRenderSegmentKind.Encode,
                currentTimelineFrame,
                totalFrames - currentTimelineFrame));
        }

        // Step 4: Merge adjacent Encode segments
        var mergedSegments = new List<SmartRenderSegment>();
        foreach (var segment in rawSegments)
        {
            if (mergedSegments.Count > 0 &&
                mergedSegments[^1].Kind == SmartRenderSegmentKind.Encode &&
                segment.Kind == SmartRenderSegmentKind.Encode &&
                mergedSegments[^1].StartFrame + mergedSegments[^1].FrameCount == segment.StartFrame)
            {
                var prev = mergedSegments[^1];
                mergedSegments[^1] = prev with { FrameCount = prev.FrameCount + segment.FrameCount };
            }
            else
            {
                mergedSegments.Add(segment);
            }
        }

        var copiedFrames = mergedSegments
            .Where(s => s.Kind == SmartRenderSegmentKind.Copy)
            .Sum(s => s.FrameCount);

        string reason;
        if (copiedFrames > 0)
        {
            var count = copyClipPaths.Count;
            var clipWord = count == 1 ? "1 source clip" : $"{count} source clips";
            reason = $"copying {copiedFrames} of {totalFrames} frames from {clipWord}";
        }
        else
        {
            reason = $"no copy segments: {firstIneligibilityReason ?? "no eligible matching source media"}";
        }

        return SmartRenderPlan.Create(mergedSegments, totalFrames, reason, timeline.Fps);
    }

    /// <summary>
    /// Length of the longest stream copy starting at the keyframe packet <paramref name="keyPacket"/>
    /// whose content is exactly the display run k0, k0 + 1/fps, … — at most <paramref name="maxFrames"/>.
    /// <para>
    /// The first N packets in decode order are an exact copy of N frames when the timestamps they
    /// carry are precisely the N display slots from the keyframe: each on the frame grid, none
    /// before the keyframe (an open-GOP leading picture, which references the previous GOP), none
    /// hidden by an edit list, none twice. Decoding cannot need a packet outside the prefix — a
    /// frame only references frames decoded before it, and nothing before a random-access point.
    /// </para>
    /// </summary>
    internal static int ExactCopyLength(IReadOnlyList<VideoPacket> packets, int keyPacket, double k0, int fps, int maxFrames)
        => ScanCopy(packets, keyPacket, k0, fps, maxFrames).Exact;

    /// <summary>
    /// Length of the longest exact stream copy from <paramref name="keyPacket"/> that ends where the
    /// next packet in decode order is a keyframe presented in the very next slot: at most
    /// <paramref name="maxFrames"/>, or 0. Cut there, the render that follows starts on a keyframe.
    /// An open-GOP keyframe never qualifies: its leading pictures are decoded after it.
    /// </summary>
    internal static int KeyframeAlignedCopyLength(IReadOnlyList<VideoPacket> packets, int keyPacket, double k0, int fps, int maxFrames)
        => ScanCopy(packets, keyPacket, k0, fps, maxFrames).KeyframeAligned;

    private static (int Exact, int KeyframeAligned) ScanCopy(IReadOnlyList<VideoPacket> packets, int keyPacket, double k0, int fps, int maxFrames)
    {
        var exact = 0;
        var aligned = 0;
        var count = 0;
        var nextMissing = 0;             // every slot below this has been seen
        var ahead = new HashSet<int>();  // slots seen above nextMissing, displayed ahead of a gap
        for (var i = keyPacket; i < packets.Count; i++)
        {
            var packet = packets[i];
            if (packet.IsDiscard || double.IsNaN(packet.PtsSeconds))
                break;

            var position = (packet.PtsSeconds - k0) * fps;
            var slot = (int)Math.Round(position, MidpointRounding.AwayFromZero);
            if (Math.Abs(position - slot) > GridToleranceFrames)
                break;

            // A keyframe opening the slot right after a complete prefix: a cut here is exact AND
            // leaves the next frame on a keyframe.
            if (i > keyPacket && packet.IsKeyframe && ahead.Count == 0 && slot == nextMissing)
                aligned = slot;

            if (slot < 0 || slot >= maxFrames)
                break;
            if (slot < nextMissing || !ahead.Add(slot))
                break;

            count++;
            while (ahead.Remove(nextMissing))
                nextMissing++;

            // Nothing is outstanding: the prefix is exactly slots 0..count-1.
            if (ahead.Count == 0)
                exact = count;
            else if (ahead.Count > MaxReorderFrames)
                break;
        }

        return (exact, aligned);
    }

    /// <summary>
    /// Why the export frame source cannot seek this source exactly to any frame, or null when it can.
    /// <para>
    /// Measured 2026-09-23 (WP-234): ffmpeg's <c>-ss</c>, as <see cref="ExportFrameSource"/> uses it,
    /// landed on the intended frame at every position of every closed-GOP MP4, MOV and Matroska
    /// fixture. MPEG-TS has no index and landed on the next keyframe from a few frames into each GOP;
    /// open-GOP MP4 landed on the keyframe instead of the leading pictures before it. Every keyframe
    /// without leading pictures, in every container, was exact, which is the only kind
    /// <see cref="KeyframeAlignedCopyLength"/> ends a copy on.
    /// </para>
    /// </summary>
    internal static string? SeekInexactReason(VideoStreamInfo info, IReadOnlyList<VideoPacket> packets)
    {
        var formats = (info.ContainerFormat ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!formats.Any(f => f is "mov" or "matroska"))
            return $"container '{info.ContainerFormat ?? "unknown"}' seeks exactly only to keyframes";
        return HasLeadingPictures(packets) ? "open-GOP leading pictures seek exactly only to keyframes" : null;
    }

    /// <summary>Whether any packet decoded after a keyframe is presented before it (open GOP).</summary>
    internal static bool HasLeadingPictures(IReadOnlyList<VideoPacket> packets)
    {
        var keyPts = double.NaN;
        foreach (var packet in packets)
        {
            if (packet.IsDiscard || double.IsNaN(packet.PtsSeconds))
                continue;
            if (packet.IsKeyframe)
                keyPts = packet.PtsSeconds;
            else if (packet.PtsSeconds < keyPts - 1e-4)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the render of timeline <paramref name="frame"/> shows <paramref name="mediaPath"/>
    /// at a frame that is not a keyframe, where a fresh seek of an inexact source goes wrong.
    /// </summary>
    private bool ReseeksOffKeyframe(
        Timeline timeline, int frame, int totalFrames, string mediaPath, int[] keyframes, IReadOnlyList<VideoPacket> packets, int fps)
    {
        if (frame >= totalFrames)
            return false;

        foreach (var layer in TimelineFrameResolver.Resolve(timeline, frame, _mediaResolver))
        {
            var path = string.IsNullOrEmpty(layer.MediaPath) ? LoosePath(layer.MediaRef) : layer.MediaPath;
            if (!string.Equals(path, mediaPath, StringComparison.OrdinalIgnoreCase))
                continue;

            // The frame shown at source time t is the first presented at or after t; it is a
            // keyframe when a keyframe lands inside [t, t + one frame).
            var t = layer.SourceSeconds;
            var next = FirstKeyframeAtOrAfter(keyframes, packets, t - SampleEpsilonFrames / fps);
            if (next < 0 || (packets[keyframes[next]].PtsSeconds - t) * fps >= 1 - GridToleranceFrames)
                return true;
        }

        return false;
    }

    /// <summary>Indexes of the usable keyframe packets, ordered by presentation time.</summary>
    private static int[] KeyframePacketsByTime(IReadOnlyList<VideoPacket> packets)
    {
        var keys = new List<int>();
        for (var i = 0; i < packets.Count; i++)
        {
            var packet = packets[i];
            if (packet.IsKeyframe && !packet.IsDiscard && !double.IsNaN(packet.PtsSeconds))
                keys.Add(i);
        }

        keys.Sort((a, b) => packets[a].PtsSeconds.CompareTo(packets[b].PtsSeconds));
        return keys.ToArray();
    }

    /// <summary>Position in <paramref name="keyframes"/> of the first keyframe at or after <paramref name="seconds"/>, or -1.</summary>
    private static int FirstKeyframeAtOrAfter(int[] keyframes, IReadOnlyList<VideoPacket> packets, double seconds)
    {
        int lo = 0, hi = keyframes.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (packets[keyframes[mid]].PtsSeconds < seconds)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo < keyframes.Length ? lo : -1;
    }

    private FrameCheckResult CheckFrameEligibility(
        Timeline timeline,
        int frame,
        IReadOnlyList<ResolvedTransition> activeTransitions,
        IReadOnlyDictionary<string, Clip> clipsById)
    {
        // 1. Transition active check
        foreach (var rt in activeTransitions)
        {
            if (rt.IsActiveAt(frame))
            {
                return FrameCheckResult.Ineligible("transition active");
            }
        }

        // 2. Visible text layer check
        foreach (var track in timeline.Tracks)
        {
            if (track.Type == ClipType.audio || track.Hidden) continue;
            foreach (var clip in track.Clips)
            {
                if (clip.Contains(frame))
                {
                    if (clip.MediaType == ClipType.text || !string.IsNullOrEmpty(clip.TextContent))
                    {
                        var opacity = clip.OpacityAt(frame);
                        if (opacity > 0)
                        {
                            return FrameCheckResult.Ineligible("text layer visible");
                        }
                    }
                }
            }
        }

        // 3. Resolve layers
        var resolved = TimelineFrameResolver.Resolve(timeline, frame, _mediaResolver);
        if (resolved.Count != 1)
        {
            return FrameCheckResult.Ineligible(resolved.Count == 0 ? "no visible video layer" : "multiple visible video layers");
        }

        var layer = resolved[0];

        if (layer.MediaType != ClipType.video)
        {
            return FrameCheckResult.Ineligible($"media type is {layer.MediaType}");
        }

        var path = layer.MediaPath;
        if (string.IsNullOrEmpty(path))
        {
            path = LoosePath(layer.MediaRef);
        }

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return FrameCheckResult.Ineligible("media file missing or unresolved");
        }

        if (layer.Opacity != 1.0)
        {
            return FrameCheckResult.Ineligible($"layer opacity is {layer.Opacity}");
        }

        if (!CpuBlendModes.IsNormal(layer.BlendMode))
        {
            return FrameCheckResult.Ineligible($"blend mode '{layer.BlendMode}' is not normal");
        }

        if (layer.Effects != null && layer.Effects.Count > 0 && layer.Effects.Any(e => e.Enabled))
        {
            var effect = layer.Effects.First(e => e.Enabled);
            return FrameCheckResult.Ineligible($"effect '{effect.Type}' applied to clip");
        }

        // Identity transform
        var t = layer.Transform;
        if (t.FlipHorizontal || t.FlipVertical)
        {
            return FrameCheckResult.Ineligible("transform flip applied");
        }
        if (Math.Abs(t.Rotation) > 0.001)
        {
            return FrameCheckResult.Ineligible("transform rotation applied");
        }
        if (Math.Abs(t.CenterX - 0.5) > 1e-9 || Math.Abs(t.CenterY - 0.5) > 1e-9)
        {
            return FrameCheckResult.Ineligible("transform position offset");
        }
        if (Math.Abs(t.Width - 1.0) > 1e-9 || Math.Abs(t.Height - 1.0) > 1e-9)
        {
            return FrameCheckResult.Ineligible("transform scale applied");
        }

        // Owning clip checks
        if (!clipsById.TryGetValue(layer.ClipId, out var owningClip))
        {
            return FrameCheckResult.Ineligible("owning clip not found");
        }

        if (Math.Abs(owningClip.Speed - 1.0) > 1e-9)
        {
            return FrameCheckResult.Ineligible($"clip speed is {owningClip.Speed}");
        }

        if (owningClip.Crop != null && !owningClip.Crop.IsIdentity)
        {
            return FrameCheckResult.Ineligible("clip crop is not identity");
        }

        if (owningClip.CropTrack != null && owningClip.CropTrack.IsActive && !owningClip.CropAt(frame).IsIdentity)
        {
            return FrameCheckResult.Ineligible("clip crop track is active");
        }

        return new FrameCheckResult(
            IsEligible: true,
            ClipId: layer.ClipId,
            MediaPath: path,
            SourceSeconds: layer.SourceSeconds,
            IneligibilityReason: null);
    }

    private readonly record struct FrameCheckResult(
        bool IsEligible,
        string? ClipId,
        string? MediaPath,
        double SourceSeconds,
        string? IneligibilityReason)
    {
        public static FrameCheckResult Ineligible(string reason) =>
            new(false, null, null, 0, reason);
    }

    private sealed record CandidateRun(
        int StartFrame,
        int FrameCount,
        string ClipId,
        string MediaPath)
    {
        public int EndFrame => StartFrame + FrameCount;
    }
}
