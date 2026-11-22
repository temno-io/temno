using Temno.Core.Models;

namespace Temno.Core.Ui;

public static class NativePreviewEligibility
{
    /// <summary>A native player owns the whole picture clip, so later overlays need composition too.</summary>
    public static bool HasOverlappingVisuals(Timeline timeline, Clip picture)
    {
        var end = (long)picture.StartFrame + picture.DurationFrames;
        foreach (var track in timeline.Tracks)
        {
            if (track.Hidden) continue;
            foreach (var clip in track.Clips)
            {
                if (clip.Id == picture.Id || clip.DurationFrames <= 0
                    || clip.MediaType is ClipType.audio or ClipType.subtitle)
                    continue;
                if (clip.MediaType == ClipType.text && string.IsNullOrEmpty(clip.TextContent))
                    continue;
                if (clip.StartFrame < end && (long)clip.StartFrame + clip.DurationFrames > picture.StartFrame)
                    return true;
            }
        }
        return false;
    }
}
