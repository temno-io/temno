using Temno.Core.Models;
using Temno.Core.Editing;

namespace Temno.Core.Session;

public static partial class EditCommands
{
    internal const string TrackLockedError = "Track is locked — unlock it before editing.";

    /// <summary>Returns the track containing <paramref name="clipId"/>, or null if not found.</summary>
    public static Track? TrackOfClip(Timeline timeline, string clipId)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (string.IsNullOrEmpty(clipId)) return null;
        foreach (var track in timeline.Tracks)
        {
            if (track.Clips.Any(c => string.Equals(c.Id, clipId, StringComparison.Ordinal)))
                return track;
        }
        return null;
    }

    /// <summary>True when <paramref name="clipId"/> belongs to a locked track.</summary>
    public static bool IsClipOnLockedTrack(Timeline timeline, string clipId)
        => TrackOfClip(timeline, clipId)?.Locked == true;

    /// <summary>
    /// Audio clips belong on audio tracks; everything else belongs on non-audio tracks
    /// (video, image, text, nested sequence).
    /// </summary>
    public static bool TrackAccepts(Track track, ClipType mediaType)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Type == ClipType.audio)
            return mediaType == ClipType.audio;
        return mediaType != ClipType.audio;
    }

    /// <summary>Nearest unlocked track of a compatible type, or -1 when none exist.</summary>
    public static int NearestCompatibleTrack(Timeline timeline, int preferredIndex, ClipType mediaType)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var tracks = timeline.Tracks;
        if (tracks.Count == 0) return -1;
        var preferred = Math.Clamp(preferredIndex, 0, tracks.Count - 1);
        if (!tracks[preferred].Locked && TrackAccepts(tracks[preferred], mediaType))
            return preferred;

        for (var distance = 1; distance < tracks.Count; distance++)
        {
            var down = preferred + distance;
            if (down < tracks.Count && !tracks[down].Locked && TrackAccepts(tracks[down], mediaType))
                return down;
            var up = preferred - distance;
            if (up >= 0 && !tracks[up].Locked && TrackAccepts(tracks[up], mediaType))
                return up;
        }

        return -1;
    }

    /// <summary>
    /// Primary unlocked track for unpositioned media placement (dropping onto an empty timeline or
    /// inserting from the Media library at the playhead): the base visual track directly above
    /// the V/A divider (<c>V1</c>) for visual media, and the first audio track (<c>A1</c>) for
    /// audio, or <c>-1</c> when no compatible unlocked track exists.
    /// </summary>
    public static int DefaultTrackForMedia(Timeline timeline, ClipType mediaType)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var tracks = timeline.Tracks;
        if (tracks.Count == 0) return -1;

        if (mediaType == ClipType.audio)
        {
            for (var i = 0; i < tracks.Count; i++)
            {
                if (!tracks[i].Locked && TrackAccepts(tracks[i], mediaType))
                    return i;
            }
            return -1;
        }

        for (var i = tracks.Count - 1; i >= 0; i--)
        {
            if (!tracks[i].Locked && TrackAccepts(tracks[i], mediaType))
                return i;
        }
        return -1;
    }

    private static void PlaceClip(Track track, Clip clip, bool overwrite, bool ripple)
    {
        if (ripple)
        {
            var duration = Math.Max(1, clip.DurationFrames);
            foreach (var existing in track.Clips)
            {
                if (existing.StartFrame >= clip.StartFrame)
                    existing.StartFrame += duration;
            }
        }
        else if (overwrite)
        {
            var spans = track.Clips
                .Select(c => new OverwriteEngine.ClipSpan(
                    c.Id, c.StartFrame, c.EndFrame, c.TrimStartFrame, c.Speed))
                .ToList();
            var actions = OverwriteEngine.ComputeOverwrite(
                spans, clip.StartFrame, clip.EndFrame);
            ApplyOverwriteActions(track, actions);
        }

        track.Clips.Add(clip);
        track.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
    }

    private static void ApplyOverwriteActions(Track track, IReadOnlyList<OverwriteEngine.Action> actions)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case OverwriteEngine.Action.Remove r:
                    track.Clips.RemoveAll(c => c.Id == r.ClipId);
                    break;
                case OverwriteEngine.Action.TrimEnd t:
                {
                    var clip = track.Clips.FirstOrDefault(c => c.Id == t.ClipId);
                    if (clip is not null)
                    {
                        clip.DurationFrames = t.NewDuration;
                        clip.ClampFadesToDuration();
                    }
                    break;
                }
                case OverwriteEngine.Action.TrimStart t:
                {
                    var clip = track.Clips.FirstOrDefault(c => c.Id == t.ClipId);
                    if (clip is not null)
                    {
                        var delta = t.NewStartFrame - clip.StartFrame;
                        clip.StartFrame = t.NewStartFrame;
                        clip.TrimStartFrame = t.NewTrimStart;
                        clip.DurationFrames = t.NewDuration;
                        ClipSplitPreservation.ShiftRelativeTimings(clip, -delta);
                        clip.ClampFadesToDuration();
                    }
                    break;
                }
                case OverwriteEngine.Action.Split s:
                {
                    var clip = track.Clips.FirstOrDefault(c => c.Id == s.ClipId);
                    if (clip is null) break;
                    var delta = s.RightStartFrame - clip.StartFrame;
                    var right = CloneClipShallow(clip);
                    right.Id = s.RightId;
                    right.StartFrame = s.RightStartFrame;
                    right.TrimStartFrame = s.RightTrimStart;
                    right.DurationFrames = s.RightDuration;
                    ClipSplitPreservation.ShiftRelativeTimings(right, -delta);
                    right.ClampFadesToDuration();
                    clip.DurationFrames = s.LeftDuration;
                    clip.ClampFadesToDuration();
                    track.Clips.Add(right);
                    break;
                }
            }
        }
        track.Clips.Sort((a, b) => a.StartFrame.CompareTo(b.StartFrame));
    }

    private static void SplitClipAtFrame(Track track, int atFrame)
    {
        for (var i = 0; i < track.Clips.Count; i++)
        {
            var clip = track.Clips[i];
            if (atFrame <= clip.StartFrame || atFrame >= clip.EndFrame)
                continue;

            var leftDuration = atFrame - clip.StartFrame;
            var rightDuration = clip.EndFrame - atFrame;
            var trimDelta = (int)Math.Round(leftDuration * clip.Speed);

            var right = CloneClipShallow(clip);
            right.Id = Timeline.NewId();
            right.StartFrame = atFrame;
            right.DurationFrames = rightDuration;
            right.TrimStartFrame = clip.TrimStartFrame + trimDelta;
            ClipSplitPreservation.ShiftRelativeTimings(right, -leftDuration);

            clip.DurationFrames = leftDuration;
            clip.ClampFadesToDuration();
            right.ClampFadesToDuration();
            track.Clips.Insert(i + 1, right);
            return;
        }
    }

    private static Clip CloneClipShallow(Clip c) => CloneClip(c);

    private static Clip CloneClip(Clip c) => new()
    {
        Id = c.Id,
        MediaRef = c.MediaRef,
        MediaType = c.MediaType,
        SourceClipType = c.SourceClipType,
        StartFrame = c.StartFrame,
        DurationFrames = c.DurationFrames,
        TrimStartFrame = c.TrimStartFrame,
        TrimEndFrame = c.TrimEndFrame,
        Speed = c.Speed,
        Volume = c.Volume,
        FadeInFrames = c.FadeInFrames,
        FadeOutFrames = c.FadeOutFrames,
        FadeInInterpolation = c.FadeInInterpolation,
        FadeOutInterpolation = c.FadeOutInterpolation,
        Opacity = c.Opacity,
        Transform = c.Transform.Clone(),
        Crop = c.Crop.Clone(),
        LinkGroupId = c.LinkGroupId,
        CaptionGroupId = c.CaptionGroupId,
        MulticamGroupId = c.MulticamGroupId,
        TextContent = c.TextContent,
        TextStyle = c.TextStyle,
        TextAnimation = c.TextAnimation,
        WordTimings = c.WordTimings?.Select(w => w.Clone()).ToList(),
        OpacityTrack = c.OpacityTrack?.Clone(),
        PositionTrack = c.PositionTrack?.Clone(p => p.Clone()),
        ScaleTrack = c.ScaleTrack?.Clone(p => p.Clone()),
        RotationTrack = c.RotationTrack?.Clone(),
        CropTrack = c.CropTrack?.Clone(crop => crop.Clone()),
        VolumeTrack = c.VolumeTrack?.Clone(),
        Effects = c.Effects?.Select(e => e.Clone()).ToList(),
        BlendMode = c.BlendMode,
    };
}
