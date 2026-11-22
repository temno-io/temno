using Temno.Core.Models;

namespace Temno.Core.Editing;

/// <summary>
/// WP-405: keeps clips at their fit when the canvas or a source's size changes after placement.
/// </summary>
public static class ClipRefit
{
    // Mac EditorViewModel.transformScale(_:matches:).
    private const double SizeMatchTolerance = 0.0001;

    /// <summary>
    /// Mac <c>EditorViewModel.applyTimelineSettings</c>, "keep visual scale proportional when the canvas
    /// aspect changes", run before <paramref name="timeline"/> takes its new size. A clip still at its
    /// placed fit takes the fit for the new canvas; any other clip keeps its box's pixel shape (its
    /// height, and its animated heights, scale by the change in canvas aspect). One difference from the
    /// Mac: a clip counts as "at its fit" by what it draws (<see cref="ClipPlacement.FitInBox"/>), so a
    /// full-canvas box holding a portrait picture, which draws the portrait fit, re-fits too.
    /// Clips whose source size is unknown, text among them, are left alone, as on the Mac.
    /// </summary>
    public static int ForCanvasChange(
        Timeline timeline, int newWidth, int newHeight, Func<Clip, (int Width, int Height)?> sourceSize)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(sourceSize);
        var prevWidth = timeline.Width;
        var prevHeight = timeline.Height;
        if (prevWidth <= 0 || prevHeight <= 0 || newWidth <= 0 || newHeight <= 0) return 0;
        if (prevWidth == newWidth && prevHeight == newHeight) return 0;

        var changed = 0;
        foreach (var track in timeline.Tracks)
        foreach (var clip in track.Clips)
        {
            if (clip.MediaType == ClipType.text)
            {
                // Text rasterizers scale font size by canvasHeight / 1080.0, so normalized height
                // stays invariant across canvas changes while normalized box width must scale
                // inversely with canvas aspect ratio so titles do not collapse into narrow vertical
                // columns when switching landscape <-> portrait.
                var oldCanvasAspect = prevWidth / (double)prevHeight;
                var newCanvasAspect = newWidth / (double)newHeight;
                var widthScale = oldCanvasAspect / newCanvasAspect;
                if (Math.Abs(widthScale - 1.0) <= SizeMatchTolerance) continue;

                var oldW = clip.Transform.Width;
                if (oldW <= 1e-6) continue;
                var newW = Math.Clamp(oldW * widthScale, 0.05, 0.96);
                var appliedRatio = newW / oldW;
                if (Math.Abs(appliedRatio - 1.0) <= SizeMatchTolerance) continue;

                List<double>? oldPosWidths = null;
                if (clip.PositionTrack is { IsActive: true } posTrack)
                {
                    oldPosWidths = new List<double>(posTrack.Keyframes.Count);
                    foreach (var kf in posTrack.Keyframes)
                        oldPosWidths.Add(clip.SizeAt(clip.StartFrame + kf.Frame).X);
                }

                clip.Transform.Width = newW;
                if (clip.ScaleTrack is { IsActive: true } textScaleTrack)
                {
                    foreach (var kf in textScaleTrack.Keyframes)
                        kf.Value = new AnimPair(Math.Clamp(kf.Value.A * appliedRatio, 1e-4, 16.0), kf.Value.B);
                }

                if (clip.PositionTrack is { IsActive: true } posTrackAfter && oldPosWidths is not null)
                {
                    for (var i = 0; i < posTrackAfter.Keyframes.Count; i++)
                    {
                        var kf = posTrackAfter.Keyframes[i];
                        var newWAtF = clip.SizeAt(clip.StartFrame + kf.Frame).X;
                        var dx = (newWAtF - oldPosWidths[i]) / 2.0;
                        kf.Value = new AnimPair(kf.Value.A - dx, kf.Value.B);
                    }
                }
                changed++;
                continue;
            }

            if (sourceSize(clip) is not { Width: > 0, Height: > 0 } dims) continue;

            var sourceAspect = dims.Width / (double)dims.Height;
            var oldAspect = sourceAspect / (prevWidth / (double)prevHeight);
            var newAspect = sourceAspect / (newWidth / (double)newHeight);

            var scaleAnimated = clip.ScaleTrack is { IsActive: true };
            var oldFit = LayoutEngine.FitTransform(dims.Width, dims.Height, prevWidth, prevHeight);
            var drawn = ClipPlacement.FitInBox(clip.Transform, dims.Width, dims.Height, prevWidth, prevHeight);
            if (!scaleAnimated && SameSize(drawn, oldFit))
            {
                var newFit = LayoutEngine.FitTransform(dims.Width, dims.Height, newWidth, newHeight);
                clip.Transform.Width = newFit.Width;
                clip.Transform.Height = newFit.Height;
            }
            else
            {
                var heightScale = oldAspect / newAspect;
                clip.Transform.Height *= heightScale;
                if (clip.ScaleTrack is { IsActive: true } scaleTrack)
                {
                    foreach (var keyframe in scaleTrack.Keyframes)
                        keyframe.Value.B *= heightScale;
                }
            }
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// WP-405: a clip placed while its media had no known size got the full-canvas box
    /// (<see cref="LayoutEngine.FitTransform(MediaManifestEntry?, Timeline)"/>). When the size arrives,
    /// a generation landing for example, such a clip takes its real fit, unless it has been scaled or
    /// its scale animated since. Call it only when <paramref name="entry"/>'s size was unknown before.
    /// </summary>
    public static int AfterSourceSizeKnown(IEnumerable<Timeline> timelines, MediaManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(timelines);
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.SourceWidth is not > 0 || entry.SourceHeight is not > 0) return 0;

        var unknownFit = new Transform();
        var changed = 0;
        foreach (var timeline in timelines)
        {
            var fit = LayoutEngine.FitTransform(entry, timeline);
            foreach (var track in timeline.Tracks)
            foreach (var clip in track.Clips)
            {
                if (clip.MediaRef != entry.Id || clip.ScaleTrack is { IsActive: true }) continue;
                if (!SameSize(clip.Transform, unknownFit)) continue;
                clip.Transform.Width = fit.Width;
                clip.Transform.Height = fit.Height;
                changed++;
            }
        }
        return changed;
    }

    /// <summary>
    /// Mac <c>EditorViewModel.sourceDimensions(for:)</c>: the library entry's probed size, else a nested
    /// timeline's canvas for a sequence clip, else unknown.
    /// </summary>
    public static (int Width, int Height)? SourceSize(MediaManifest manifest, IEnumerable<Timeline> timelines, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(timelines);
        ArgumentNullException.ThrowIfNull(clip);
        var entry = manifest.Entries.FirstOrDefault(e => e.Id == clip.MediaRef);
        if (entry?.SourceWidth is > 0 && entry.SourceHeight is > 0)
            return (entry.SourceWidth.Value, entry.SourceHeight.Value);
        if (clip.SourceClipType == ClipType.sequence
            && timelines.FirstOrDefault(t => t.Id == clip.MediaRef) is { Width: > 0, Height: > 0 } child)
            return (child.Width, child.Height);
        return null;
    }

    private static bool SameSize(Transform a, Transform b)
        => Math.Abs(a.Width - b.Width) < SizeMatchTolerance && Math.Abs(a.Height - b.Height) < SizeMatchTolerance;
}
