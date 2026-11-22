using System;
using System.Collections.Generic;
using System.Linq;

namespace Temno.Media.Export;

public enum SmartRenderSegmentKind { Encode, Copy }

/// <param name="StartFrame">First timeline frame of the segment, inclusive.</param>
/// <param name="FrameCount">Number of timeline frames. Always exact.</param>
/// <param name="SourcePath">Copy segments only: the file to copy from.</param>
/// <param name="SourceStartSeconds">
/// Copy segments only: the source keyframe to seek to, in seconds from the container start. A copy
/// takes the first <paramref name="FrameCount"/> packets from this keyframe in decode order; the
/// analyzer emits a copy only when those packets are exactly the frames displayed in
/// [StartFrame, StartFrame + FrameCount).
/// </param>
public sealed record SmartRenderSegment(
    SmartRenderSegmentKind Kind,
    int StartFrame,
    int FrameCount,
    string? SourcePath = null,
    double SourceStartSeconds = 0);

public sealed record SmartRenderPlan(
    IReadOnlyList<SmartRenderSegment> Segments,
    int CopiedFrames,
    int TotalFrames,
    string Reason)      // human-readable: why smart rendering did or did not apply
{
    /// <summary>True when at least one segment can be copied.</summary>
    public bool HasCopySegments => CopiedFrames > 0;

    /// <summary>Timeline frame rate the segments are counted in; 0 when unknown.</summary>
    public int Fps { get; init; }

    /// <summary>
    /// Validating factory that enforces tiling invariants: ordered by StartFrame, no gaps,
    /// no overlaps, starts at 0, and the sum of FrameCount equals TotalFrames.
    /// </summary>
    public static SmartRenderPlan Create(
        IReadOnlyList<SmartRenderSegment> segments,
        int totalFrames,
        string reason,
        int fps = 0)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(reason);
        if (totalFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(totalFrames), "Total frames must be non-negative.");
        if (fps < 0)
            throw new ArgumentOutOfRangeException(nameof(fps), "Frame rate must be non-negative.");

        Validate(segments, totalFrames);

        var copiedFrames = segments
            .Where(s => s.Kind == SmartRenderSegmentKind.Copy)
            .Sum(s => s.FrameCount);

        return new SmartRenderPlan(segments, copiedFrames, totalFrames, reason) { Fps = fps };
    }

    /// <summary>
    /// Asserts that the segments tile [0, totalFrames) with no gaps or overlaps.
    /// </summary>
    public static void Validate(IReadOnlyList<SmartRenderSegment> segments, int totalFrames)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (totalFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(totalFrames), "Total frames must be non-negative.");

        if (totalFrames == 0)
        {
            if (segments.Count > 0 && segments.Sum(s => s.FrameCount) != 0)
                throw new InvalidOperationException("Non-empty segment list with non-zero frame count provided for 0 total frames.");
            return;
        }

        if (segments.Count == 0)
            throw new InvalidOperationException("Segments list cannot be empty when totalFrames > 0.");

        var expectedStart = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.FrameCount <= 0)
                throw new InvalidOperationException($"Segment {i} has non-positive frame count: {segment.FrameCount}.");

            if (segment.StartFrame != expectedStart)
                throw new InvalidOperationException(
                    $"Segment {i} start frame {segment.StartFrame} does not match expected frame {expectedStart}.");

            if (segment.Kind == SmartRenderSegmentKind.Copy && string.IsNullOrEmpty(segment.SourcePath))
                throw new InvalidOperationException($"Copy segment {i} has null or empty SourcePath.");

            expectedStart += segment.FrameCount;
        }

        if (expectedStart != totalFrames)
            throw new InvalidOperationException(
                $"Segments do not tile total frames: covered {expectedStart} frames, expected {totalFrames}.");
    }
}
