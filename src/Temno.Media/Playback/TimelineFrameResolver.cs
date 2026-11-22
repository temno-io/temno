using Temno.Core.IO;
using Temno.Core.Models;

namespace Temno.Media.Playback;

/// <summary>
/// One visible video layer at a timeline frame, with the source-media seek position needed to decode it.
/// <see cref="SourceSeconds"/> is in source-media seconds; all frame values are timeline frames.
/// </summary>
public readonly record struct ResolvedFrameClip(
    string ClipId,
    string MediaRef,
    ClipType MediaType,
    string? MediaPath,
    double SourceSeconds,
    int TrackOrderIndex,
    double Opacity,
    Transform Transform,
    string? BlendMode,
    IReadOnlyList<Effect>? Effects,
    int ClipRelativeFrame,
    Crop? Crop = null);

/// <summary>
/// Mac: Preview/CompositionBuilder.swift (layer selection), Models/Timeline.swift (clip retime math).
/// Resolves which clips are visible per video track at a timeline frame and maps the timeline frame
/// to per-clip source seconds (trim + speed aware).
/// </summary>
public static class TimelineFrameResolver
{
    /// <summary>
    /// Visible video layers at <paramref name="frame"/>, ordered bottom→top (Tracks[0] is topmost,
    /// so the topmost layer is the LAST element). Mac parity rules: hidden tracks excluded; within a
    /// track clips are scanned in StartFrame order and any clip overlapping its predecessor is skipped
    /// entirely; text clips never become media layers; layers with opacity ≤ 0 at the frame are skipped.
    /// </summary>
    public static IReadOnlyList<ResolvedFrameClip> Resolve(Timeline timeline, int frame, MediaResolver? mediaResolver = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.Fps <= 0) throw new ArgumentOutOfRangeException(nameof(timeline), "Timeline fps must be positive.");

        var layers = new List<ResolvedFrameClip>();
        for (var trackIndex = timeline.Tracks.Count - 1; trackIndex >= 0; trackIndex--)
        {
            var track = timeline.Tracks[trackIndex];
            if (track.Type == ClipType.audio || track.Hidden) continue;

            var clip = VisibleClipAt(track, frame);
            if (clip is null) continue;
            if (clip.MediaType is ClipType.text or ClipType.audio) continue;

            var opacity = clip.OpacityAt(frame);
            if (opacity <= 0) continue;

            // Still images ignore trim/speed for decoding: any timestamp shows the same picture.
            var sourceSeconds = clip.MediaType == ClipType.image
                ? 0.0
                : SourceSecondsAt(clip, frame, timeline.Fps);

            layers.Add(new ResolvedFrameClip(
                ClipId: clip.Id,
                MediaRef: clip.MediaRef,
                MediaType: clip.MediaType,
                MediaPath: mediaResolver?.ResolvePath(clip.MediaRef),
                SourceSeconds: sourceSeconds,
                TrackOrderIndex: trackIndex,
                Opacity: opacity,
                Transform: clip.TransformAt(frame),
                BlendMode: clip.BlendMode,
                Effects: clip.Effects,
                ClipRelativeFrame: frame - clip.StartFrame,
                Crop: clip.CropAt(frame)));
        }

        return layers;
    }

    /// <summary>
    /// Maps an absolute timeline frame inside the clip to source-media seconds:
    /// (trimStart + (frame - start) × speed) / fps. Inverse of Mac Clip.timelineFrame(sourceSeconds:fps:).
    /// </summary>
    public static double SourceSecondsAt(Clip clip, int timelineFrame, int fps)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps), "Fps must be positive.");
        var sourceFrame = clip.TrimStartFrame + (timelineFrame - clip.StartFrame) * clip.Speed;
        return sourceFrame / fps;
    }

    /// <summary>
    /// Inverse of <see cref="SourceSecondsAt"/>: source-media seconds → absolute timeline frame,
    /// clamped to the clip's half-open interval.
    /// </summary>
    public static int TimelineFrameAt(Clip clip, double sourceSeconds, int fps)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps), "Fps must be positive.");
        var speed = clip.Speed == 0 ? 1.0 : clip.Speed;
        var local = (sourceSeconds * fps - clip.TrimStartFrame) / speed;
        var frame = clip.StartFrame + (int)Math.Round(local, MidpointRounding.AwayFromZero);
        var last = Math.Max(clip.StartFrame, clip.EndFrame - 1);
        return Math.Clamp(frame, clip.StartFrame, last);
    }

    private static Clip? VisibleClipAt(Track track, int frame)
    {
        var previousEnd = int.MinValue;
        foreach (var clip in track.Clips.OrderBy(c => c.StartFrame))
        {
            // Mac overlap rule: a clip starting before the previous accepted clip ends is skipped entirely.
            if (clip.StartFrame < previousEnd) continue;
            previousEnd = clip.EndFrame;
            if (clip.Contains(frame)) return clip;
            if (clip.StartFrame > frame) return null;
        }

        return null;
    }
}
