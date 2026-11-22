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
    // --- WP-60: drag media from the panel onto the timeline ---

    private void LaneCanvas_DragOver(object sender, DragEventArgs e)
    {
        if (_session is null || !e.DataView.Properties.ContainsKey(MediaRefDragKey))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        if (ActiveTimelineHasNoClips())
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            HideDropGhost();
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.IsContentVisible = true;
                e.DragUIOverride.Caption = "Add to timeline";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = true;
            }
            e.Handled = true;
            return;
        }

        var mediaRef = e.DataView.Properties[MediaRefDragKey] as string;
        var mediaType = MediaTypeForRef(mediaRef);
        var (trackIndex, startFrame, newTrackInsertIndex) = DropTarget(e, mediaType);
        if (trackIndex < 0 && newTrackInsertIndex is null)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            HideDropGhost();
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;

        if (e.DragUIOverride is not null)
        {
            e.DragUIOverride.IsContentVisible = false;
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;
        }

        if (newTrackInsertIndex is int insertAt)
            ShowDropNewTrackIndicator(insertAt, startFrame, MediaDurationFrames(mediaRef));
        else
            ShowDropGhost(trackIndex, startFrame, MediaDurationFrames(mediaRef));
        e.Handled = true;
    }

    private void LaneCanvas_DragLeave(object sender, DragEventArgs e) => HideDropGhost();

    private void LaneCanvas_Drop(object sender, DragEventArgs e)
    {
        HideDropGhost();
        if (_session is null
            || !e.DataView.Properties.TryGetValue(MediaRefDragKey, out var refObj)
            || refObj is not string mediaRef)
            return;

        var entry = _session.Manifest.Entries.FirstOrDefault(x => x.Id == mediaRef);
        if (entry is null) return;

        var mediaType = entry.Type switch
        {
            ClipType.audio => ClipType.audio,
            ClipType.image => ClipType.image,
            _ => ClipType.video,
        };
        int trackIndex;
        int startFrame;
        int? newTrackInsertIndex = null;
        if (ActiveTimelineHasNoClips())
        {
            startFrame = 0;
            trackIndex = EditCommands.DefaultTrackForMedia(_session.ActiveTimeline, mediaType);
            if (trackIndex < 0)
                newTrackInsertIndex = EditCommands.PartitionedInsertionIndex(
                    _session.ActiveTimeline, mediaType == ClipType.audio ? ClipType.audio : ClipType.video);
        }
        else
        {
            (trackIndex, startFrame, newTrackInsertIndex) = DropTarget(e, mediaType);
            if (trackIndex < 0 && newTrackInsertIndex is null) return;
        }

        e.Handled = newTrackInsertIndex is int insertAt
            ? InsertLibraryMediaToNewTrack(mediaRef, insertAt, startFrame)
            : InsertLibraryMedia(mediaRef, trackIndex, startFrame);
    }

    public bool InsertLibraryMediaAtPlayhead(string mediaRef)
    {
        if (_session is null) return false;
        var mediaType = MediaTypeForRef(mediaRef);
        var track = EditCommands.DefaultTrackForMedia(_session.ActiveTimeline, mediaType);
        if (track >= 0)
            return InsertLibraryMedia(mediaRef, track, _playheadFrame);
        var insertAt = EditCommands.PartitionedInsertionIndex(
            _session.ActiveTimeline, mediaType == ClipType.audio ? ClipType.audio : ClipType.video);
        return InsertLibraryMediaToNewTrack(mediaRef, insertAt, _playheadFrame);
    }

    private bool InsertLibraryMediaToNewTrack(string mediaRef, int insertIndex, int startFrame)
    {
        if (_session is null) return false;
        var entry = _session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
        if (entry is null) return false;
        var mediaType = MediaTypeForRef(mediaRef);
        var duration = MediaDurationFrames(mediaRef);
        var trackType = mediaType == ClipType.audio ? ClipType.audio : ClipType.video;
        var clip = new Clip
        {
            MediaRef = mediaRef,
            MediaType = mediaType,
            SourceClipType = mediaType,
            StartFrame = startFrame,
            DurationFrames = duration,
            Transform = LayoutEngine.FitTransform(entry, _session.ActiveTimeline),
        };

        var linkAudio = mediaType == ClipType.video && entry.HasAudio != false;
        var result = EditCommands.AddMediaToNewTrack(
            _session, trackType, insertIndex, clip, linkAudio, out var createdTrackIndex, out _);
        if (!result.Ok) return false;

        var createdTrackId = createdTrackIndex >= 0 && createdTrackIndex < _session.ActiveTimeline.Tracks.Count
            ? _session.ActiveTimeline.Tracks[createdTrackIndex].Id
            : null;
        SetSelectedClips(LinkGroup.Expand(_session.ActiveTimeline, new[] { clip.Id }), clip.Id);
        MaybeFitLongTimeline(duration);
        TimelineEdited?.Invoke(this, EventArgs.Empty);
        if (createdTrackId is not null)
        {
            var id = createdTrackId;
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ScrollTrackIntoView(id)))
                ScrollTrackIntoView(id);
        }
        return true;
    }

    private bool InsertLibraryMedia(string mediaRef, int trackIndex, int startFrame)
    {
        if (_session is null) return false;
        var entry = _session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
        if (entry is null) return false;
        var mediaType = MediaTypeForRef(mediaRef);
        var duration = MediaDurationFrames(mediaRef);
        var videoClip = new Clip
        {
            MediaRef = mediaRef,
            MediaType = mediaType,
            SourceClipType = mediaType,
            StartFrame = startFrame,
            DurationFrames = duration,
            Transform = LayoutEngine.FitTransform(entry, _session.ActiveTimeline),
        };

        var ripple = EditorEditContext.Current.RippleDelete;
        var overwrite = !ripple && EditorEditContext.Current.OverwriteOnMove;
        var linkAudio = mediaType == ClipType.video && entry.HasAudio != false;
        EditResult result;
        if (linkAudio)
        {
            result = EditCommands.AddLinkedMediaDrop(
                _session, trackIndex, videoClip, linkAudio: true, out _,
                overwrite: overwrite, ripple: ripple);
        }
        else
        {
            result = EditCommands.AddClip(
                _session, trackIndex, videoClip, overwrite, ripple);
        }
        if (!result.Ok) return false;

        SetSelectedClips(LinkGroup.Expand(_session.ActiveTimeline, new[] { videoClip.Id }), videoClip.Id);
        MaybeFitLongTimeline(duration);
        TimelineEdited?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private (int TrackIndex, int StartFrame, int? NewTrackInsertIndex) DropTarget(DragEventArgs e, ClipType mediaType)
    {
        var bodyPos = e.GetPosition(BodyScroll);
        var laneY = bodyPos.Y + BodyScroll.VerticalOffset;
        var frame = DropFrameAtViewportX(bodyPos.X);
        if (_session is { } session)
        {
            // Dropping over the sticky ruler above the track viewport creates a new top visual track without stealing pixels from V2.
            if (mediaType != ClipType.audio && bodyPos.Y < 0)
            {
                HideSnapIndicator();
                return (-1, frame, 0);
            }
            if (mediaType == ClipType.audio && laneY >= BottomNewAudioTrackThreshold())
            {
                HideSnapIndicator();
                return (-1, frame, session.ActiveTimeline.Tracks.Count);
            }
        }

        var rawTrack = _geo.TrackAtY(laneY);
        if (_session is { } currentSession
            && rawTrack >= 0
            && rawTrack < currentSession.ActiveTimeline.Tracks.Count
            && currentSession.ActiveTimeline.Tracks[rawTrack].Locked)
        {
            HideSnapIndicator();
            return (-1, frame, null);
        }
        var track = _session is null
            ? rawTrack
            : EditCommands.NearestCompatibleTrack(_session.ActiveTimeline, rawTrack, mediaType);
        if (track < 0)
        {
            if (_session is { } s)
            {
                HideSnapIndicator();
                var fallbackInsert = EditCommands.PartitionedInsertionIndex(
                    s.ActiveTimeline, mediaType == ClipType.audio ? ClipType.audio : ClipType.video);
                return (-1, frame, fallbackInsert);
            }
            return (-1, Math.Max(0, frame), null);
        }
        HideSnapIndicator();
        return (track, frame, null);
    }

    /// <summary>
    /// Lane-Y threshold for dragging audio below the last track to create a new audio track.
    /// Strictly below the last track when empty gutter exists; when tracks fill the viewport,
    /// a small bottom lip keeps new-track creation reachable for both library drops and clip moves.
    /// </summary>
    private double BottomNewAudioTrackThreshold()
    {
        var viewH = BodyScroll.ViewportHeight > 1 ? BodyScroll.ViewportHeight : BodyScroll.ActualHeight;
        return TimelineGeometry.BottomNewAudioTrackThreshold(
            _geo.ContentHeight, viewH, BodyScroll.ScrollableHeight, FullViewportBottomDropLip);
    }

    private ClipType MediaTypeForRef(string? mediaRef)
    {
        var entry = _session?.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
        return entry?.Type switch
        {
            ClipType.audio => ClipType.audio,
            ClipType.image => ClipType.image,
            _ => ClipType.video,
        };
    }

    private int MediaDurationFrames(string? mediaRef)
    {
        var fps = Math.Max(1, _session?.ActiveTimeline.Fps ?? 30);
        var entry = _session?.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
        if (entry is null || entry.Duration <= 0)
            return DefaultStillDurationSeconds * fps;
        return Math.Max(1, (int)Math.Round(entry.Duration * fps));
    }

    private void ShowDropGhost(int trackIndex, int startFrame, int durationFrames)
    {
        if (_dropNewTrackIndicator is not null) _dropNewTrackIndicator.Visibility = Visibility.Collapsed;
        if (_dropGhost is null)
        {
            _dropGhost = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(80, AppTheme.Accent.Brand.R, AppTheme.Accent.Brand.G, AppTheme.Accent.Brand.B)),
                BorderBrush = AppTheme.Accent.PrimaryBrush,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(AppTheme.Radius.XsSm),
                IsHitTestVisible = false,
            };
            DropOverlayCanvas.Children.Add(_dropGhost);
        }
        var y = _geo.TrackYAt(trackIndex) + TimelineGeometry.ClipVerticalInset;
        var h = Math.Max(4, _geo.TrackHeightAt(trackIndex) - TimelineGeometry.ClipVerticalInset * 2);
        var contentX = _geo.XForFrame(startFrame);
        var contentW = Math.Max(2, durationFrames * _pixelsPerFrame);
        var liveLeft = BodyScroll.HorizontalOffset;
        var liveWidth = BodyScroll.ViewportWidth > 1 ? BodyScroll.ViewportWidth : _viewWidth;
        var vis = TimelineGeometry.IntersectHorizontal(contentX, contentW, liveLeft, liveLeft + liveWidth);
        if (vis is null)
        {
            _dropGhost.Visibility = Visibility.Collapsed;
            return;
        }
        var laneOrigin = LaneCanvas.TransformToVisual(DropOverlayCanvas).TransformPoint(new Point(0, 0));
        var bodyOrigin = BodyScroll.TransformToVisual(DropOverlayCanvas).TransformPoint(new Point(0, 0));
        ClipDropOverlay();
        _dropGhost.Width = Math.Max(2, vis.Value.Width);
        _dropGhost.Height = h;
        Canvas.SetLeft(_dropGhost, bodyOrigin.X + (vis.Value.X - liveLeft));
        Canvas.SetTop(_dropGhost, laneOrigin.Y + y);
        _dropGhost.Visibility = Visibility.Visible;
    }

    private void ClipDropOverlay()
    {
        if (DropOverlayCanvas.ActualWidth > 1 && DropOverlayCanvas.ActualHeight > 1)
            DropOverlayCanvas.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, DropOverlayCanvas.ActualWidth, DropOverlayCanvas.ActualHeight),
            };
    }

    private void ShowDropNewTrackIndicator(int insertIndex, int startFrame, int durationFrames)
    {
        if (_dropGhost is not null) _dropGhost.Visibility = Visibility.Collapsed;
        ClipDropOverlay();
        if (_dropNewTrackIndicator is null)
        {
            _dropNewTrackIndicator = new Border
            {
                Height = AppTheme.BorderWidth.Thick,
                Background = AppTheme.Accent.BrandBrush,
                IsHitTestVisible = false,
            };
            DropOverlayCanvas.Children.Add(_dropNewTrackIndicator);
        }
        var lineY = insertIndex <= 0
            ? _geo.TrackYAt(0)
            : insertIndex >= _geo.TrackCount
                ? Math.Max(_geo.RulerHeight, _geo.ContentHeight - AppTheme.BorderWidth.Thick)
                : _geo.TrackYAt(insertIndex);
        var liveLeft = BodyScroll.HorizontalOffset;
        var liveWidth = BodyScroll.ViewportWidth > 1 ? BodyScroll.ViewportWidth : _viewWidth;
        var laneOrigin = LaneCanvas.TransformToVisual(DropOverlayCanvas).TransformPoint(new Point(0, 0));
        var bodyOrigin = BodyScroll.TransformToVisual(DropOverlayCanvas).TransformPoint(new Point(0, 0));
        var overlayHeight = DropOverlayCanvas.ActualHeight > 1 ? DropOverlayCanvas.ActualHeight : Math.Max(lineY + AppTheme.BorderWidth.Thick, _geo.ContentHeight);
        var indicatorTop = Math.Clamp(laneOrigin.Y + lineY, 0, Math.Max(0, overlayHeight - AppTheme.BorderWidth.Thick));
        _dropNewTrackIndicator.Width = Math.Max(2, liveWidth);
        Canvas.SetLeft(_dropNewTrackIndicator, bodyOrigin.X);
        Canvas.SetTop(_dropNewTrackIndicator, indicatorTop);
        _dropNewTrackIndicator.Visibility = Visibility.Visible;

        // Also show a slim clip preview aligned to the insertion edge so timing stays clear.
        var contentX = _geo.XForFrame(startFrame);
        var contentW = Math.Max(2, durationFrames * _pixelsPerFrame);
        var vis = TimelineGeometry.IntersectHorizontal(contentX, contentW, liveLeft, liveLeft + liveWidth);
        if (vis is not null)
        {
            if (_dropGhost is null)
            {
                _dropGhost = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(80, AppTheme.Accent.Brand.R, AppTheme.Accent.Brand.G, AppTheme.Accent.Brand.B)),
                    BorderBrush = AppTheme.Accent.PrimaryBrush,
                    BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(AppTheme.Radius.XsSm),
                    IsHitTestVisible = false,
                };
                DropOverlayCanvas.Children.Add(_dropGhost);
            }
            const double previewHeight = 14;
            _dropGhost.Width = Math.Max(2, vis.Value.Width);
            _dropGhost.Height = previewHeight;
            Canvas.SetLeft(_dropGhost, bodyOrigin.X + (vis.Value.X - liveLeft));
            var ghostTop = insertIndex <= 0
                ? indicatorTop
                : Math.Max(0, indicatorTop - previewHeight);
            Canvas.SetTop(_dropGhost, ghostTop);
            _dropGhost.Visibility = Visibility.Visible;
        }
    }

    private void HideDropGhost()
    {
        if (_dropGhost is not null) _dropGhost.Visibility = Visibility.Collapsed;
        if (_dropNewTrackIndicator is not null) _dropNewTrackIndicator.Visibility = Visibility.Collapsed;
        HideSnapIndicator();
        _snapState.CurrentlySnappedTo = null;
    }
}
