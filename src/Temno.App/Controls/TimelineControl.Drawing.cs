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
    private void DrawLanes(Timeline timeline)
    {
        // WP-161: count lane redraws inside the playback trace window. Unconditional (env-gated
        // inside): this control cannot see the clock, so the analyzer windows by tick lines.
        PreviewHostControl.PlaybackTrace("lanes", _playheadFrame);
        var dlT0 = System.Diagnostics.Stopwatch.GetTimestamp(); // WP-320 trim probe (TEMNO_TIMELINE_TRACE)
        LaneCanvas.Children.Clear();
        _laneByTrack.Clear();
        var dlClear = System.Diagnostics.Stopwatch.GetTimestamp() - dlT0;
        BeginTransitionPass(timeline);
        _stripPassUsedFallback = false;

        RulerCanvas.Children.Clear();
        DrawRuler(timeline.Fps);

        long filmTicks = 0, waveTicks = 0;
        for (var ti = 0; ti < timeline.Tracks.Count; ti++)
        {
            var (film, wave) = DrawTrackLane(timeline, ti);
            filmTicks += film;
            waveTicks += wave;
        }

        RedrawTransitionTabs();
        SettleStripPreference();
        TimelineTrace.Mark("dlf", filmTicks, waveTicks);
        TimelineTrace.Mark("dlc", dlClear, System.Diagnostics.Stopwatch.GetTimestamp() - dlT0);
    }

    /// <summary>
    /// WP-320. Replaces the tracks that hold the dragged clips and leaves the ruler, the other
    /// tracks and the headers in place. A trim that extends the timeline widens the content;
    /// the ruler and a shrink wait for the release <see cref="Rebuild"/>.
    /// </summary>
    private void RedrawTrimTracks()
    {
        if (_session?.ActiveTimeline is not { } timeline) return;
        RefreshTransitionIndex(timeline);
        _stripPassUsedFallback = false;
        var dlT0 = System.Diagnostics.Stopwatch.GetTimestamp();
        long filmTicks = 0, waveTicks = 0;
        foreach (var ti in TrimTrackIndexes())
        {
            if ((uint)ti >= (uint)timeline.Tracks.Count) continue;
            var (film, wave) = DrawTrackLane(timeline, ti);
            filmTicks += film;
            waveTicks += wave;
        }
        GrowContentExtent(timeline);
        SettleStripPreference();
        TimelineTrace.Mark("dlf", filmTicks, waveTicks);
        TimelineTrace.Mark("dlc", 0, System.Diagnostics.Stopwatch.GetTimestamp() - dlT0);
    }

    private List<int> TrimTrackIndexes()
    {
        var seen = new HashSet<int>();
        var tracks = new List<int>();
        foreach (var snapshot in _dragSnapshots)
        {
            if (seen.Add(snapshot.TrackIndex))
                tracks.Add(snapshot.TrackIndex);
        }
        return tracks;
    }

    private void RefreshTransitionIndex(Timeline timeline)
    {
        _transitionIndex = new TransitionIndex(timeline);
        _transitionBlocks = TransitionBlocks.ForTimeline(_transitionIndex);
    }

    private void GrowContentExtent(Timeline timeline)
    {
        var totalFrames = Math.Max(timeline.TotalFrames, timeline.Fps * 5);
        var contentW = Math.Max(
            BodyScroll.ActualWidth > 0 ? BodyScroll.ActualWidth : 400,
            _geo.ContentWidth(totalFrames) - _geo.HeaderWidth);
        if (contentW > BodyContent.Width + 0.5)
            BodyContent.Width = contentW;
    }

    private void RemoveTrackLane(int ti)
    {
        if (!_laneByTrack.TryGetValue(ti, out var old)) return;
        foreach (var element in old)
            LaneCanvas.Children.Remove(element);
        _laneByTrack.Remove(ti);
    }

    private void ForgetTrackLabels(int ti)
    {
        if (_session is null) return;
        var tracks = _session.ActiveTimeline.Tracks;
        if ((uint)ti >= (uint)tracks.Count) return;
        foreach (var clip in tracks[ti].Clips)
            _clipLabels.Remove(clip.Id);
    }

    private void AddLaneChild(UIElement element)
    {
        LaneCanvas.Children.Add(element);
        _laneBucket?.Add(element);
    }

    private void SettleStripPreference()
    {
        if (_preferCachedStrip && _stripInflight == 0 && !_stripPassUsedFallback)
            _preferCachedStrip = false;
    }

    private (long Film, long Wave) DrawTrackLane(Timeline timeline, int ti)
    {
        RemoveTrackLane(ti);
        ForgetTrackLabels(ti);
        var bucket = new List<UIElement>();
        _laneBucket = bucket;
        long filmTicks = 0, waveTicks = 0;
        try
        {
            var track = timeline.Tracks[ti];
            var y = _geo.TrackYAt(ti);
            var h = _geo.TrackHeightAt(ti);
            var color = TrackColor(track.Type);
            var viewRight = _viewLeft + _viewWidth;
            const double clipOverscan = 64;
            var mode = EditorEditContext.Current.ToolMode;

            var laneBg = new Rectangle
            {
                Width = _viewWidth,
                Height = h,
                Fill = new SolidColorBrush(Color.FromArgb(track.Locked ? (byte)14 : (byte)28, color.R, color.G, color.B)),
            };
            Canvas.SetLeft(laneBg, 0); Canvas.SetTop(laneBg, y);
            AddLaneChild(laneBg);

            var line = new Rectangle { Width = _viewWidth, Height = 1, Fill = AppTheme.Border.SubtleBrush };
            Canvas.SetLeft(line, 0); Canvas.SetTop(line, y + h - 1);
            AddLaneChild(line);

            var edgeHandles = new List<(double X, double Y, double H, string ClipId, bool Left)>();
            foreach (var clip in track.Clips)
            {
                var (cx, cy, cw, ch) = _geo.ClipRect(clip, ti);
                var vis = TimelineGeometry.IntersectHorizontal(cx, cw, _viewLeft - clipOverscan, viewRight + clipOverscan);
                if (vis is null) continue;
                var (vx, vw) = vis.Value;
                var selected = !track.Locked && _selectedClipIds.Contains(clip.Id);

                var laneOpacity = track.Hidden ? 0.4 : track.Locked ? 0.62 : 0.92;
                // WP-159: the strip goes down first and `body` sits on top of it, transparent, so the
                // border, corner radius and selection ring still belong to one element and the clip
                // keeps a single hit target. A Transparent brush is hit-testable in WinUI; null is not.
                var fT0 = System.Diagnostics.Stopwatch.GetTimestamp();
                var hasStrip = AddClipFilmstrip(clip, cx, cw, cy, ch, vx, vw, laneOpacity);
                filmTicks += System.Diagnostics.Stopwatch.GetTimestamp() - fT0;
                Brush background = hasStrip
                    ? new SolidColorBrush(Colors.Transparent)
                    : selected ? AppTheme.Accent.SelectedBrush : new SolidColorBrush(color);

                var body = new Border
                {
                    Width = Math.Max(2, vw),
                    Height = Math.Max(4, ch),
                    Background = background,
                    BorderBrush = selected ? AppTheme.Accent.BrandBrush : AppTheme.Border.SubtleBrush,
                    BorderThickness = new Thickness(selected ? 2 : 1),
                    CornerRadius = new CornerRadius(AppTheme.Radius.XsSm),
                    Opacity = laneOpacity,
                    IsHitTestVisible = !track.Locked,
                    Tag = new ClipTag(clip.Id, ti),
                };
                if (!track.Locked)
                    body.PointerPressed += Clip_PointerPressed;
                Canvas.SetLeft(body, LocalX(vx));
                Canvas.SetTop(body, cy);
                AddLaneChild(body);

                if (clip.MediaType == ClipType.audio)
                {
                    var wT0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    AddClipWaveform(clip, timeline.Fps, vx, cy, vw, ch);
                    waveTicks += System.Diagnostics.Stopwatch.GetTimestamp() - wT0;
                }

                var label = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                    CornerRadius = new CornerRadius(2),
                    Padding = AppTheme.Padding.HXs,
                    Height = 14,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    MaxWidth = Math.Max(2, vw - 4),
                    IsHitTestVisible = false,
                    Child = new TextBlock
                    {
                        Text = ClipLabel(clip),
                        FontSize = AppTheme.FontSize.Xs,
                        Foreground = AppTheme.Text.PrimaryBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                };
                // WP-252: the name sits between the head and tail blocks, so neither covers it.
                var slot = new ClipLabelSlot(label, vx, vw, cx, cw);
                _clipLabels[clip.Id] = slot;
                PlaceClipLabel(clip, slot);
                Canvas.SetTop(label, cy + 2);
                AddLaneChild(label);

                // Blade must not present trim handles; an edge click would start TrimLeft/TrimRight.
                // WP-252: they are added after the track's transition blocks, so they stay on top.
                if (!track.Locked && (selected || mode == EditorToolMode.Trim) && mode != EditorToolMode.Blade)
                {
                    if (cx >= _viewLeft - 8 && cx <= viewRight + 8 && !EdgeHasCut(clip, left: true))
                        edgeHandles.Add((LocalX(cx), cy, ch, clip.Id, true));
                    if (cx + cw - 4 >= _viewLeft - 8 && cx + cw - 4 <= viewRight + 8 && !EdgeHasCut(clip, left: false))
                        edgeHandles.Add((LocalX(cx + cw - 4), cy, ch, clip.Id, false));
                }
            }

            DrawTransitionBlocks(ti);
            foreach (var (hx, hy, hh, clipId, left) in edgeHandles)
                AddEdgeHandle(hx, hy, hh, clipId, ti, left);

            if (track.Locked)
            {
                var lockScrim = new Border
                {
                    Width = _viewWidth,
                    Height = Math.Max(1, h - 1),
                    Background = new SolidColorBrush(Color.FromArgb(38, 0, 0, 0)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(
                        72, AppTheme.Accent.Primary.R, AppTheme.Accent.Primary.G, AppTheme.Accent.Primary.B)),
                    BorderThickness = new Thickness(0, 1, 0, 1),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(lockScrim, 0);
                Canvas.SetTop(lockScrim, y);
                Canvas.SetZIndex(lockScrim, EdgeHandleZIndex + 1);
                AddLaneChild(lockScrim);
            }
        }
        finally
        {
            _laneBucket = null;
            _laneByTrack[ti] = bucket;
        }
        return (filmTicks, waveTicks);
    }

    private void AddEdgeHandle(double x, double y, double h, string clipId, int trackIndex, bool left)
    {
        var handle = new Border
        {
            Width = 6,
            Height = Math.Max(8, h),
            Background = AppTheme.Accent.TimecodeBrush,
            Opacity = 0.85,
            Tag = new EdgeTag(clipId, trackIndex, left),
        };
        handle.PointerPressed += Edge_PointerPressed;
        Canvas.SetLeft(handle, x);
        Canvas.SetTop(handle, y);
        Canvas.SetZIndex(handle, EdgeHandleZIndex);
        AddLaneChild(handle);
    }

    private void DrawRuler(int fps)
    {
        var timeline = _session?.ActiveTimeline;
        var rulerH = Theme.Layout.RulerHeight;
        var major = TimelineGeometry.MajorTickIntervalFrames(_pixelsPerFrame, fps);
        if (major <= 0) return;
        var minors = TimelineGeometry.MinorSubdivisions(major, _pixelsPerFrame);
        var minorStep = minors > 0 ? major / minors : 0;
        var (startFrame, endFrame) = TimelineGeometry.VisibleTickFrames(
            _viewLeft, _viewLeft + _viewWidth, _pixelsPerFrame, major);

        const double rulerOverscan = 64;
        if (minorStep > 0)
        {
            var f = startFrame / minorStep * minorStep;
            for (; f <= endFrame; f += minorStep)
            {
                if (f % major == 0) continue;
                var x = LocalX(f * _pixelsPerFrame);
                if (x < -rulerOverscan || x > _viewWidth + rulerOverscan) continue;
                var mid = minors % 2 == 0 && major / 2 > 0 && f % (major / 2) == 0;
                var tick = new Rectangle
                {
                    Width = 1,
                    Height = mid ? 6 : 4,
                    Fill = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)),
                };
                Canvas.SetLeft(tick, x);
                Canvas.SetTop(tick, rulerH - (mid ? 6 : 4));
                RulerCanvas.Children.Add(tick);
            }
        }

        var majorFrame = startFrame / major * major;
        var majorPx = major * _pixelsPerFrame;
        for (var f = majorFrame; f <= endFrame; f += major)
        {
            var x = LocalX(f * _pixelsPerFrame);
            if (x < -rulerOverscan || x > _viewWidth + rulerOverscan) continue;
            var tick = new Rectangle { Width = 1, Height = 8, Fill = AppTheme.Text.MutedBrush };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, rulerH - 8);
            RulerCanvas.Children.Add(tick);
            if (x < -1 || x > _viewWidth + 1) continue;
            // WP-200: abbreviate by available spacing — the tighter of the next tick and the
            // viewport edge — instead of overlapping or dropping labels. Empty means tick only.
            var availablePx = Math.Min(majorPx, _viewWidth - x)
                - (TimelineGeometry.RulerLabelOffsetPx + TimelineGeometry.RulerLabelGapPx);
            var text = TimelineGeometry.RulerLabel(f, fps, availablePx);
            if (text.Length == 0) continue;
            var label = new TextBlock
            {
                Text = text,
                FontSize = AppTheme.FontSize.Xs,
                Foreground = AppTheme.Text.TertiaryBrush,
                FontFamily = AppTheme.FontFamily.Mono,
            };
            Canvas.SetLeft(label, x + TimelineGeometry.RulerLabelOffsetPx);
            Canvas.SetTop(label, 2);
            RulerCanvas.Children.Add(label);
        }

        // WP-205: marker glyphs on the ruler — a small diamond in the marker's colour at its
        // frame, clipped to the visible range like the ticks, tooltip "name · timecode".
        if (timeline is not null)
            DrawRulerMarkers(timeline, fps);
    }

    private void DrawRulerMarkers(Timeline timeline, int fps)
    {
        // WP-205 (Premiere pass): a marker head (diamond) with a stem down through the ruler,
        // in the marker's colour. Click seeks to the marker; double-click opens Edit Marker.
        if (timeline.Markers.Count == 0) return;
        var rulerH = Theme.Layout.RulerHeight;
        // WP-258: the head sits between the timecode labels (top 2) and the 8 px major ticks, as in
        // Premiere. Drawn at the top, it painted over the label digit at its frame.
        var headTop = rulerH - 8 - 1 - MarkerPalette.HeadHalfPx * 2;
        foreach (var marker in timeline.Markers.OrderBy(m => m.Frame))
        {
            var x = LocalX(marker.Frame * _pixelsPerFrame);
            if (x < -8 || x > _viewWidth + 8) continue;
            var fill = MarkerPalette.BrushFor(marker.Color);
            var head = new Polygon
            {
                Points = new PointCollection
                {
                    new(x, headTop),
                    new(x + MarkerPalette.HeadHalfPx, headTop + MarkerPalette.HeadHalfPx),
                    new(x, headTop + MarkerPalette.HeadHalfPx * 2),
                    new(x - MarkerPalette.HeadHalfPx, headTop + MarkerPalette.HeadHalfPx),
                },
                Fill = fill,
                IsHitTestVisible = false,
            };
            var stemTop = headTop + MarkerPalette.HeadHalfPx * 2;
            var stem = new Rectangle
            {
                Width = MarkerPalette.StemPx,
                Height = Math.Max(0, rulerH - stemTop - 1),
                Fill = fill,
                Opacity = 0.85,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(stem, x - MarkerPalette.StemPx / 2);
            Canvas.SetTop(stem, stemTop);
            var name = string.IsNullOrWhiteSpace(marker.Name) ? "Marker" : marker.Name;
            var timecode = FormatRulerMarkerTime(marker.Frame, fps);
            // A generous transparent hit target: the head is 11 px wide and hard to click.
            var hit = new Rectangle
            {
                Width = MarkerPalette.HitPx,
                Height = rulerH,
                Fill = new SolidColorBrush(Colors.Transparent),
                Tag = marker.Id,
            };
            hit.PointerPressed += RulerMarker_PointerPressed;
            hit.DoubleTapped += RulerMarker_DoubleTapped;
            Canvas.SetLeft(hit, x - MarkerPalette.HitPx / 2);
            Canvas.SetTop(hit, 0);
            var tip = string.IsNullOrWhiteSpace(marker.Note)
                ? $"{name} · {timecode}"
                : $"{name} · {timecode}\n{marker.Note}";
            ToolTipService.SetToolTip(hit, tip);
            AutomationProperties.SetName(hit, $"{name} at {timecode}");
            RulerCanvas.Children.Add(stem);
            RulerCanvas.Children.Add(head);
            RulerCanvas.Children.Add(hit);
        }
    }

    private void RulerMarker_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // Click a ruler marker = seek to it. Handled so the lane does not start a scrub.
        if (sender is not FrameworkElement { Tag: string id }) return;
        var marker = _session?.ActiveTimeline.Markers.FirstOrDefault(m => m.Id == id);
        if (marker is null) return;
        PlayheadFrame = marker.Frame;
        e.Handled = true;
    }

    private void RulerMarker_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        var timeline = _session?.ActiveTimeline;
        var marker = timeline?.Markers.FirstOrDefault(m => m.Id == id);
        if (marker is null || timeline is null) return;
        _ = EditRulerMarkerAsync(marker.Id, marker.Name, marker.Color, marker.Note, marker.Frame, timeline.Fps);
        e.Handled = true;
    }

    /// <summary>
    /// WP-205 (Premiere pass): the ruler's Edit Marker path. One <see cref="EditCommands.UpdateMarker"/>
    /// commit, then <see cref="NotifyEdited"/> redraws the ruler and refreshes the Index list.
    /// </summary>
    private async Task EditRulerMarkerAsync(string id, string? name, string? color, string? note, int frame, int fps)
    {
        if (_session is null) return;
        var edit = await MarkerPalette.ShowEditDialogAsync(XamlRoot, name, color, note, frame, fps);
        if (edit is null) return;
        var result = EditCommands.UpdateMarker(_session, id, frame: null, edit.Name, edit.ColorHex, edit.Note);
        if (!result.Ok) return;
        NotifyEdited();
    }

    private static string FormatRulerMarkerTime(int frame, int fps)
    {
        if (fps <= 0) fps = 30;
        frame = Math.Max(0, frame);
        var ff = frame % fps;
        var totalSeconds = frame / fps;
        var ss = totalSeconds % 60;
        var mm = (totalSeconds / 60) % 60;
        var hh = totalSeconds / 3600;
        return hh > 0 ? $"{hh}:{mm:00}:{ss:00}:{ff:00}" : $"{mm:00}:{ss:00}:{ff:00}";
    }
}
