using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Audio;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Ui;
using Temno.Media.Audio;
using Temno.Media.Cache;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Colors = Microsoft.UI.Colors;

namespace Temno.App.Controls;

public sealed partial class TimelineControl : UserControl
{
    // --- WP-63: clip thumbnails + waveforms ---

    /// <summary>
    /// Draws the clip's filmstrip and asks for any cell it does not have yet. Returns false when
    /// nothing was drawn, so the caller keeps the flat track colour.
    /// </summary>
    /// <remarks>
    /// WP-159. Sampling is <see cref="ThumbnailStripPolicy"/>'s job and is unit-tested there; this
    /// method only turns its seconds into rectangles. Cells are laid across the clip's **on-screen**
    /// span, so extraction is bounded by pixels rather than by the source's duration — the owner's
    /// 8.09 h VOD costs at most <see cref="ThumbnailStripPolicy.MaxSamplesPerClip"/> runs, where the
    /// Mac's every-2-seconds cadence would have been 14,570.
    /// </remarks>
    private bool AddClipFilmstrip(
        Clip clip, double cx, double cw, double cy, double ch, double vx, double vw, double opacity)
    {
        if (_session is null) return false;
        if (clip.MediaType is not (ClipType.video or ClipType.image)) return false;
        if (string.IsNullOrEmpty(clip.MediaRef) || cw <= 0 || ch < 4 || vw <= 0) return false;

        var entry = _session.Manifest.Entries.FirstOrDefault(e => e.Id == clip.MediaRef);
        if (entry is null) return false;

        var fps = Math.Max(1, _session.ActiveTimeline.Fps);
        var clipStart = ThumbnailStripPolicy.VisibleStartSeconds(clip.TrimStartFrame, fps);
        var clipLength = ThumbnailStripPolicy.VisibleDurationSeconds(clip.DurationFrames, clip.Speed, fps);

        // A still has no time axis: one frame tiled, as the Mac renderer does.
        var isStill = clip.MediaType == ClipType.image;

        // Which slice of the clip is on screen. Sampling the whole clip and drawing part of it would
        // make the cost depend on zoom-out rather than on what is visible.
        var fromFraction = Math.Clamp((vx - cx) / cw, 0, 1);
        var toFraction = Math.Clamp((vx + vw - cx) / cw, 0, 1);
        var rangeStart = clipStart + fromFraction * clipLength;
        var rangeLength = Math.Max(0, (toFraction - fromFraction) * clipLength);

        var thumbWidth = ThumbnailStripPolicy.ThumbnailWidth(ch, entry.SourceWidth, entry.SourceHeight);
        if (isStill) return AddTiledStill(clip.MediaRef, cx, cy, ch, vx, vw, thumbWidth, opacity);
        var count = ThumbnailStripPolicy.SampleCount(vw, thumbWidth);
        var seconds = ThumbnailStripPolicy.SampleSeconds(rangeStart, rangeLength, count);

        var cellWidth = vw / seconds.Count;
        var drewAny = false;
        // WP-171: while playing, extraction is deferred (see RequestStripCell), so cells the
        // playhead-follow scrolled onto have nothing cached. Gather this media's cached seconds
        // once and draw the nearest one instead of nothing: stale but continuous, zero ffmpeg.
        // WP-320: a live trim defers the same way. Sample times move with the edge, and asking for
        // each one blanked the strip and started ffmpeg on the UI thread. After release,
        // _preferCachedStrip keeps the nearest cell up until the exact ones land.
        var deferExtraction = _isPlaying || IsLiveTrim;
        var holdNearest = deferExtraction || _preferCachedStrip;
        List<double>? heldSeconds = null;
        if (holdNearest)
        {
            heldSeconds = new List<double>();
            foreach (var cached in _stripCache)
                if (cached.Key.MediaRef == clip.MediaRef)
                    heldSeconds.Add(cached.Key.Seconds);
        }
        for (var i = 0; i < seconds.Count; i++)
        {
            var key = (clip.MediaRef, seconds[i]);
            if (!_stripCache.TryGetValue(key, out var image))
            {
                if (!deferExtraction)
                    RequestStripCell(clip.MediaRef, seconds[i]);
                ImageSource? fallback = null;
                if (heldSeconds is not null)
                {
                    var near = StripCellFallback.Nearest(seconds[i], heldSeconds);
                    if (near.HasValue)
                        fallback = _stripCache[(clip.MediaRef, near.Value)];
                }
                if (fallback is null)
                {
                    if (holdNearest) _stripPassUsedFallback = true;
                    continue;
                }
                _stripPassUsedFallback = true;
                image = fallback;
            }

            // The last cell is clamped so the strip cannot overhang the clip it belongs to.
            var width = Math.Min(cellWidth, vx + vw - (vx + i * cellWidth));
            if (width <= 0.5) continue;

            var cell = new Border
            {
                Width = width,
                Height = ch,
                Opacity = opacity,
                IsHitTestVisible = false,
                Background = new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill },
            };
            Canvas.SetLeft(cell, LocalX(vx + i * cellWidth));
            Canvas.SetTop(cell, cy);
            AddLaneChild(cell);
            drewAny = true;
        }

        return drewAny;
    }

    // Mac ClipRenderer.tileImage: maxTiles.
    private const int MaxStillTiles = 200;

    /// <summary>
    /// Mac <c>ClipRenderer.drawTiledImage</c>: a still repeats at its own aspect from the clip's
    /// start, and only the tiles at the visible edges are cut. One cell stretched across the whole
    /// clip with UniformToFill zoomed a 9:16 portrait until the lane showed a sliver of its middle.
    /// </summary>
    private bool AddTiledStill(
        string mediaRef, double cx, double cy, double ch, double vx, double vw, double tileWidth, double opacity)
    {
        var key = (mediaRef, 0d);
        if (!_stripCache.TryGetValue(key, out var image))
        {
            RequestStripCell(mediaRef, 0d);
            return false;
        }

        var drewAny = false;
        var first = Math.Max(0, (int)Math.Floor((vx - cx) / tileWidth));
        for (var i = first; i < first + MaxStillTiles; i++)
        {
            var tx = cx + i * tileWidth;
            if (tx >= vx + vw) break;
            var left = Math.Max(tx, vx) - tx;
            var right = Math.Min(tx + tileWidth, vx + vw) - tx;
            if (right - left <= 0.5) continue;

            var tile = new Border
            {
                Width = tileWidth,
                Height = ch,
                Opacity = opacity,
                IsHitTestVisible = false,
                Background = new ImageBrush { ImageSource = image, Stretch = Stretch.UniformToFill },
                Clip = new RectangleGeometry { Rect = new Rect(left, 0, right - left, ch) },
            };
            Canvas.SetLeft(tile, LocalX(tx));
            Canvas.SetTop(tile, cy);
            AddLaneChild(tile);
            drewAny = true;
        }
        return drewAny;
    }

    /// <summary>Queues one strip cell. Idempotent per (media, second) until the cache is cleared.</summary>
    private void RequestStripCell(string mediaRef, double seconds)
    {
        if (_session is null) return;
        // WP-163: deferred during playback (see IsPlaying). Returning before _stripRequested.Add
        // keeps the deferral a deferral: the next redraw after pause re-asks.
        // WP-320: a live trim defers too. The release rebuild is the one request for the new cells.
        if (_isPlaying || IsLiveTrim) return;
        var key = (mediaRef, seconds);
        if (!_stripRequested.Add(key)) return;

        var entry = _session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
        var path = entry is null ? null : _session.Resolver.ResolvePath(entry);
        if (path is null || !File.Exists(path))
        {
            _stripRequested.Remove(key);   // allow a retry once the source is online
            return;
        }

        TimelineTrace.Mark("sq", (long)Math.Round(seconds * 1000)); // WP-320 trim probe (TEMNO_TIMELINE_TRACE)
        GenerateStripCellAsync(mediaRef, path, seconds);
    }
}
