using System;
using System.Collections.Generic;

namespace Temno.Core.Ui;

/// <summary>
/// How many frames a timeline clip's filmstrip shows, and which source seconds they come from. WP-159.
/// WinUI-free so `Temno.Core.Tests` can reach it.
/// </summary>
/// <remarks>
/// Before this, `TimelineControl` held one <c>ImageSource</c> per media asset, sampled at
/// <c>Math.Min(1.0, duration / 2)</c> seconds and painted across the whole clip with
/// <c>Stretch.UniformToFill</c>. Every clip cut from one source therefore showed the same frame
/// whatever its trim, and for the owner's 8.09 h VOD trimmed in at frame 26958 that frame was
/// t = 1 s — a frame the clip does not contain.
///
/// <para><b>Why the Mac's cadence is not ported.</b>
/// <c>MediaVisualCache.videoThumbnailTimes</c> samples every 2 s for anything ≥ 10 s. On a 29,140 s
/// source that is 14,570 extractions. AVAssetImageGenerator batches those against one asset;
/// `ThumbnailCache` spawns one ffmpeg process per frame, so a literal port would be worst on exactly
/// the media that exposed the bug. Here the count comes from <b>how many thumbnails fit on screen</b>,
/// which is all a strip can show, and never from the source duration.</para>
/// </remarks>
public static class ThumbnailStripPolicy
{
    /// <summary>
    /// Hard ceiling per clip, whatever the zoom. At 40 the owner's 8.09 h source costs 40 ffmpeg
    /// runs instead of 14,570, and a 1800 px timeline cannot legibly show more anyway.
    /// </summary>
    public const int MaxSamplesPerClip = 40;

    /// <summary>Below this the clip is a sliver; one frame is all it can carry.</summary>
    public const double MinStripWidthPixels = 12.0;

    /// <summary>
    /// Sampled seconds are rounded onto this grid when the spacing is coarse enough for it not to
    /// matter, so a scroll or a small zoom re-asks for keys the disk cache already holds. Purely a
    /// cache-hit optimisation: `ThumbnailCache` keys on (path, seconds, height).
    /// </summary>
    public const double QuantumSeconds = 0.5;

    /// <summary>First source second the clip actually shows.</summary>
    public static double VisibleStartSeconds(int trimStartFrame, int fps)
        => fps <= 0 ? 0 : Math.Max(0, trimStartFrame / (double)fps);

    /// <summary>
    /// How much source the clip consumes, honouring speed — the same arithmetic
    /// `PreviewHostControl.TimelineNativeReachedClipEnd` uses for the native out-point.
    /// </summary>
    public static double VisibleDurationSeconds(int durationFrames, double speed, int fps)
    {
        if (fps <= 0 || durationFrames <= 0) return 0;
        var rate = speed == 0 ? 1.0 : Math.Abs(speed);
        return durationFrames * rate / fps;
    }

    /// <summary>
    /// One thumbnail's on-screen width: the lane height at the source's aspect. Falls back to 16:9
    /// when the manifest has no dimensions, which is what an un-probed asset looks like.
    /// </summary>
    public static double ThumbnailWidth(double laneHeight, int? sourceWidth, int? sourceHeight)
    {
        var aspect = sourceWidth is > 0 && sourceHeight is > 0
            ? sourceWidth.Value / (double)sourceHeight.Value
            : 16.0 / 9.0;
        return Math.Max(1.0, laneHeight * aspect);
    }

    /// <summary>
    /// How many frames the strip shows. Driven by pixels on screen, never by source duration, and
    /// capped at <see cref="MaxSamplesPerClip"/>.
    /// </summary>
    public static int SampleCount(
        double onScreenWidthPixels,
        double thumbnailWidthPixels,
        int max = MaxSamplesPerClip)
    {
        if (onScreenWidthPixels < MinStripWidthPixels) return 1;
        if (thumbnailWidthPixels <= 0) return 1;
        var fits = (int)Math.Ceiling(onScreenWidthPixels / thumbnailWidthPixels);
        return Math.Clamp(fits, 1, Math.Max(1, max));
    }

    /// <summary>
    /// The source seconds for <paramref name="count"/> cells laid across
    /// <paramref name="rangeDurationSeconds"/> starting at <paramref name="rangeStartSeconds"/>.
    /// Each cell is sampled at its **left edge**, so cell 0 is the clip's in-point — the frame a
    /// reader expects to see at the head of a clip.
    /// </summary>
    public static IReadOnlyList<double> SampleSeconds(
        double rangeStartSeconds,
        double rangeDurationSeconds,
        int count)
    {
        var start = Math.Max(0, rangeStartSeconds);
        if (count <= 1 || rangeDurationSeconds <= 0) return [start];

        var interval = rangeDurationSeconds / count;
        var quantise = interval >= 1.0;
        var samples = new List<double>(count);
        for (var i = 0; i < count; i++)
        {
            // Cell 0 is never snapped: the head of a clip must be its exact in-point, and rounding
            // it onto the cache grid showed a frame the clip does not start on.
            var seconds = start + i * interval;
            if (quantise && i > 0) seconds = Math.Round(seconds / QuantumSeconds) * QuantumSeconds;
            samples.Add(Math.Max(0, seconds));
        }

        return samples;
    }
}
