using Temno.Core.Models;

namespace Temno.Core.Captions;

/// <summary>
/// Frame-domain timing policy shared by caption generation and subtitle import: resolve overlaps
/// between neighbouring caption clips, then close short gaps so captions do not flicker off between
/// phrases.
///
/// Mac: MediaPanel/CaptionsTab/CaptionSpecBuilder.swift + CaptionGapSettings. That revision is not
/// present in the macOS checkout on this workstation (see WP-115 "Mac revision" note); the policy
/// below is implemented from the behaviour recorded in docs/audits/captions-speech.md:
///   * maximum gap default 0.5 s, accepted range 0…2 s (0.5 s at 30 fps = 15 frames);
///   * gaps at or under the maximum are closed by extending the earlier caption;
///   * the final caption is held to the timeline end when that gap is also within the maximum;
///   * an overlap of exactly one frame is awarded to whichever caption was originally shorter;
///   * a caption that starts on the same frame as its predecessor collapses the predecessor to one
///     frame, which Mac's overwrite placement then removes.
/// </summary>
public static class CaptionSpecBuilder
{
    /// <summary>Mac CaptionGapSettings default.</summary>
    public const double DefaultMaximumGapSeconds = 0.5;

    /// <summary>Mac CaptionGapSettings accepted range upper bound.</summary>
    public const double MaximumGapSecondsLimit = 2.0;

    /// <summary>Subtitle import places cues exactly as authored (Mac forces gap = 0).</summary>
    public const double SubtitleImportGapSeconds = 0.0;

    public static bool IsValidGapSeconds(double seconds)
        => double.IsFinite(seconds) && seconds >= 0 && seconds <= MaximumGapSecondsLimit;

    public static double ClampGapSeconds(double seconds)
        => !double.IsFinite(seconds) ? DefaultMaximumGapSeconds : Math.Clamp(seconds, 0, MaximumGapSecondsLimit);

    /// <summary>
    /// Applies overlap ownership then gap closing to <paramref name="clips"/> in place. The list is
    /// sorted by start frame first; clips fully covered by a later caption are removed, matching the
    /// overwrite semantics of Mac's caption placement.
    /// </summary>
    /// <param name="timelineEndFrame">
    /// Exclusive frame the last caption may be held to (the source clip's end, or the timeline end).
    /// Null leaves the final caption at its transcribed length.
    /// </param>
    public static void Apply(
        List<Clip> clips,
        int fps,
        double maximumGapSeconds = DefaultMaximumGapSeconds,
        int? timelineEndFrame = null)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps), "fps must be positive");
        if (clips.Count == 0) return;

        // OrderBy is stable; List.Sort is not. Captions that share a start frame must keep their
        // authored order, or which one survives the overwrite pass would be arbitrary.
        var ordered = clips.OrderBy(c => c.StartFrame).ToList();
        clips.Clear();
        clips.AddRange(ordered);

        ResolveOverlaps(clips);
        CloseGaps(clips, GapFrames(ClampGapSeconds(maximumGapSeconds), fps), timelineEndFrame);
        FlattenToOverwrite(clips);
    }

    /// <summary>Mac 0.5 s at 30 fps = 15 frames.</summary>
    public static int GapFrames(double seconds, int fps)
        => (int)Math.Round(seconds * fps, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Ownership of contested frames. Uses the durations the captions were transcribed with, so the
    /// "prefer the shorter original" rule is not skewed by an earlier trim in the same pass.
    /// </summary>
    private static void ResolveOverlaps(List<Clip> clips)
    {
        var original = clips.Select(c => c.DurationFrames).ToArray();
        for (var i = 0; i + 1 < clips.Count; i++)
        {
            var prev = clips[i];
            var next = clips[i + 1];
            var overlap = prev.EndFrame - next.StartFrame;
            if (overlap <= 0) continue;

            if (next.StartFrame <= prev.StartFrame)
            {
                // Same-frame start: the earlier caption keeps a single frame and is then removed by
                // the overwrite pass, exactly as Mac's placeTextClips drops a fully covered spec.
                prev.DurationFrames = 1;
                continue;
            }

            // One contested frame and the previous caption is the shorter original: it keeps the
            // frame and the next one starts a frame later. Only when that shift has somewhere to go —
            // pushing it onto the following caption's start would collapse this caption instead, and
            // the overwrite pass would then delete a caption that had just won its dispute.
            var shifted = next.StartFrame + 1;
            var roomToShift = i + 2 >= clips.Count || shifted < clips[i + 2].StartFrame;
            if (overlap == 1 && original[i] < original[i + 1] && next.DurationFrames > 1 && roomToShift)
            {
                next.StartFrame = shifted;
                next.DurationFrames -= 1;
                continue;
            }

            prev.DurationFrames = Math.Max(1, next.StartFrame - prev.StartFrame);
        }
    }

    private static void CloseGaps(List<Clip> clips, int maxGapFrames, int? timelineEndFrame)
    {
        if (maxGapFrames <= 0) return;

        for (var i = 0; i + 1 < clips.Count; i++)
        {
            var gap = clips[i + 1].StartFrame - clips[i].EndFrame;
            if (gap > 0 && gap <= maxGapFrames)
                clips[i].DurationFrames = clips[i + 1].StartFrame - clips[i].StartFrame;
        }

        if (timelineEndFrame is not { } end) return;
        var last = clips[^1];
        var tail = end - last.EndFrame;
        if (tail > 0 && tail <= maxGapFrames)
            last.DurationFrames = end - last.StartFrame;
    }

    /// <summary>
    /// Later captions win contested frames, which is what Mac's overwrite placement produces. Leaves
    /// a strictly non-overlapping list so the clips can go straight onto one Windows track.
    /// </summary>
    private static void FlattenToOverwrite(List<Clip> clips)
    {
        for (var i = clips.Count - 2; i >= 0; i--)
        {
            var prev = clips[i];
            var next = clips[i + 1];
            if (prev.EndFrame <= next.StartFrame) continue;

            if (next.StartFrame <= prev.StartFrame)
            {
                clips.RemoveAt(i);
                continue;
            }
            prev.DurationFrames = next.StartFrame - prev.StartFrame;
        }
    }
}
