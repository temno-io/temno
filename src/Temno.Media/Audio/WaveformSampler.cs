using Temno.Core.Models;

namespace Temno.Media.Audio;

/// <summary>
/// Source-time-correct waveform sampling for the timeline (WP-213). The old renderer stretched
/// the whole-file envelope across the clipped viewport rectangle; every column here maps its
/// visible timeline interval back to source seconds through the canonical clip timing math
/// (timeline frames, source seconds, trim + speed — the same convention as
/// <see cref="TimelineAudioDecoder"/> and <see cref="Clip.TimelineFrameFromSourceSeconds"/>)
/// and aggregates envelope peaks over that interval.
/// </summary>
public static class WaveformSampler
{
    /// <summary>
    /// Source seconds for a (possibly fractional) timeline frame on <paramref name="clip"/>.
    /// Inverse of <see cref="Clip.TimelineFrameFromSourceSeconds"/>: the decoder consumes
    /// <c>DurationFrames * Speed</c> source frames starting at <c>TrimStartFrame</c>.
    /// Returns NaN when the mapping is undefined (bad fps/speed/duration).
    /// </summary>
    public static double SourceSecondsForTimelineFrame(Clip clip, double timelineFrame, int fps)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (fps <= 0) return double.NaN;
        if (clip.DurationFrames <= 0) return double.NaN;
        if (!double.IsFinite(clip.Speed) || clip.Speed <= 0) return double.NaN;
        if (!double.IsFinite(timelineFrame)) return double.NaN;

        var sourceFrame = Math.Max(0, clip.TrimStartFrame) + (timelineFrame - clip.StartFrame) * clip.Speed;
        return sourceFrame / fps;
    }

    /// <summary>
    /// One aggregated peak per visible column. Content X maps to timeline frames via
    /// <paramref name="pixelsPerFrame"/> (header width 0, matching the timeline control's
    /// geometry); each column's frame interval becomes a source-seconds interval whose
    /// envelope max is the column peak. Time outside the clip or envelope reads as silence.
    /// Returns all zeros when the mapping is undefined.
    /// </summary>
    public static float[] SampleVisibleColumns(
        Clip clip,
        int fps,
        WaveformEnvelope envelope,
        double pixelsPerFrame,
        double visibleStartContentX,
        double visibleEndContentX,
        int columnCount)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(envelope);
        if (columnCount <= 0) return [];
        var peaks = new float[columnCount];
        if (fps <= 0 || pixelsPerFrame <= 0 || !double.IsFinite(pixelsPerFrame)) return peaks;
        if (clip.DurationFrames <= 0) return peaks;
        if (!double.IsFinite(clip.Speed) || clip.Speed <= 0) return peaks;
        if (!double.IsFinite(visibleStartContentX) || !double.IsFinite(visibleEndContentX)) return peaks;
        if (visibleEndContentX <= visibleStartContentX) return peaks;

        var clipStartFrame = clip.StartFrame;
        var clipEndFrame = clip.EndFrame;
        var columnWidth = (visibleEndContentX - visibleStartContentX) / columnCount;

        for (var i = 0; i < columnCount; i++)
        {
            var frameStart = (visibleStartContentX + i * columnWidth) / pixelsPerFrame;
            var frameEnd = (visibleStartContentX + (i + 1) * columnWidth) / pixelsPerFrame;
            // Clamp the column to the clip body so neighbouring audio cannot bleed in.
            frameStart = Math.Max(frameStart, clipStartFrame);
            frameEnd = Math.Min(frameEnd, clipEndFrame);
            if (frameEnd <= frameStart)
            {
                peaks[i] = 0f;
                continue;
            }

            var sourceStart = SourceSecondsForTimelineFrame(clip, frameStart, fps);
            var sourceEnd = SourceSecondsForTimelineFrame(clip, frameEnd, fps);
            peaks[i] = envelope.PeakForRange(sourceStart, sourceEnd);
        }

        return peaks;
    }
}
