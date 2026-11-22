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
    private bool IsTrackLocked(int trackIndex)
        => _session is not null
           && trackIndex >= 0
           && trackIndex < _session.ActiveTimeline.Tracks.Count
           && _session.ActiveTimeline.Tracks[trackIndex].Locked;

    private void Clip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null || sender is not FrameworkElement { Tag: ClipTag tag }) return;
        if (IsTrackLocked(tag.TrackIndex))
        {
            e.Handled = true;
            return;
        }
        if (RouteNonPrimaryButton(sender, e, tag.ClipId)) return;
        var point = e.GetCurrentPoint(LaneCanvas).Position;
        var frame = FrameAtContentX(BodyScroll.HorizontalOffset + e.GetCurrentPoint(BodyScroll).Position.X);
        var shift = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
        var mode = EditorEditContext.Current.ToolMode;

        if (mode == EditorToolMode.Blade)
        {
            TryBladeSplit(tag.ClipId, point);
            e.Handled = true;
            return;
        }

        if (shift)
        {
            ToggleSelection(LinkedUnlessAlt(tag.ClipId), tag.ClipId);
            e.Handled = true;
            return;
        }

        // WP-252: a block's resize edge reaches a few pixels past the block, onto the clip.
        if (mode == EditorToolMode.Select && TryBeginTransitionResize(point, e))
        {
            e.Handled = true;
            return;
        }

        if (AltIgnoresLinks() || !_selectedClipIds.Contains(tag.ClipId))
            SetSelectedClips(LinkedUnlessAlt(tag.ClipId), tag.ClipId);

        var clip = FindClip(tag.ClipId);
        if (clip is null) return;
        var px = ContentX(point.X);
        var (clipX, _, clipWidth, _) = _geo.ClipRect(clip, tag.TrackIndex);
        if (px <= clipX + EdgeHitPx || mode == EditorToolMode.Trim && px < clipX + clipWidth * 0.5)
        {
            BeginDrag(DragKind.TrimLeft, tag, frame);
        }
        else if (px >= clipX + clipWidth - EdgeHitPx || mode == EditorToolMode.Trim && px >= clipX + clipWidth * 0.5)
        {
            BeginDrag(DragKind.TrimRight, tag, frame);
        }
        else if (mode is EditorToolMode.Select or EditorToolMode.Trim)
        {
            BeginDrag(DragKind.Move, tag, frame);
        }

        if (_dragKind != DragKind.None)
            LaneCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Edge_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null || sender is not FrameworkElement { Tag: EdgeTag edge }) return;
        if (IsTrackLocked(edge.TrackIndex))
        {
            e.Handled = true;
            return;
        }
        if (RouteNonPrimaryButton(sender, e, edge.ClipId)) return;
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift))
        {
            ToggleSelection(LinkedUnlessAlt(edge.ClipId), edge.ClipId);
            e.Handled = true;
            return;
        }

        if (AltIgnoresLinks() || !_selectedClipIds.Contains(edge.ClipId))
            SetSelectedClips(LinkedUnlessAlt(edge.ClipId), edge.ClipId);
        var frame = FrameAtContentX(BodyScroll.HorizontalOffset + e.GetCurrentPoint(BodyScroll).Position.X);
        BeginDrag(edge.Left ? DragKind.TrimLeft : DragKind.TrimRight, new ClipTag(edge.ClipId, edge.TrackIndex), frame);
        if (_dragKind != DragKind.None)
            LaneCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    /// <summary>
    /// WP-184. Only the left button edits. The right button selects the clip (with its link group)
    /// and opens its menu; it never reaches the blade, trim or drag paths, which used to run for
    /// any button. Middle and X buttons do nothing.
    /// </summary>
    private bool RouteNonPrimaryButton(object sender, PointerRoutedEventArgs e, string clipId)
    {
        var props = e.GetCurrentPoint(LaneCanvas).Properties;
        if (props.IsLeftButtonPressed) return false;
        if (props.IsRightButtonPressed && sender is FrameworkElement)
        {
            // WP-252: anchored on the lane canvas. Selecting an unselected clip rebuilds the lanes
            // and replaces the clicked element, and a flyout shown at a detached element never opens.
            var at = e.GetCurrentPoint(LaneCanvas).Position;
            if (AltIgnoresLinks() || !_selectedClipIds.Contains(clipId))
                SetSelectedClips(LinkedUnlessAlt(clipId), clipId);
            ShowClipContextMenu(LaneCanvas, at, clipId);
        }
        e.Handled = true;
        return true;
    }

    private void ShowClipContextMenu(FrameworkElement anchor, Windows.Foundation.Point at, string clipId)
    {
        var linked = _selectedClipIds.Count > 1;
        var flyout = new MenuFlyout();
        void Add(string text, string action, string? accelerator = null, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            if (accelerator is not null) item.KeyboardAcceleratorTextOverride = accelerator;
            item.Click += (_, _) => ContextActionRequested?.Invoke(this, action);
            flyout.Items.Add(item);
        }
        Add("Split at Playhead", "split", "B");
        Add("Trim Start to Playhead", "trim-start", "Q");
        Add("Trim End to Playhead", "trim-end", "W");
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add("Delete", "delete", "Delete");
        Add("Ripple Delete", "ripple-delete");
        flyout.Items.Add(new MenuFlyoutSeparator());
        AddTransitionItems(flyout, clipId);
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add(linked ? "Unlink Selection" : "Link Selection", linked ? "unlink" : "link", linked ? "Ctrl+Shift+L" : "Ctrl+L");
        Add("Restore Linked Audio", "restore-audio");
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add("Reveal Source in Explorer", "reveal-source");
        Add("Properties", "properties");
        WindowPopout.ShowContextMenuAt(flyout, anchor, at);
    }

    private void BeginDrag(DragKind kind, ClipTag tag, int pointerFrame)
    {
        // Preserve an already-active selection (including an Alt-isolated single half) when the user
        // releases Alt and drags or trims it; expand links only when starting fresh on an unselected clip.
        HashSet<string> selectedIds;
        if (AltIgnoresLinks())
            selectedIds = new HashSet<string>([tag.ClipId], StringComparer.Ordinal);
        else if (_selectedClipIds.Contains(tag.ClipId))
            selectedIds = new HashSet<string>(_selectedClipIds, StringComparer.Ordinal);
        else
            selectedIds = LinkedUnlessAlt(tag.ClipId);
        ApplySelection(selectedIds, tag.ClipId, notify: true, rebuild: false);
        _dragSnapshots = CreateDragSnapshots(selectedIds);
        if (_dragSnapshots.Count == 0) return;

        _dragKind = kind;
        _dragClipId = tag.ClipId;
        _dragPointerStartFrame = pointerFrame;
        _dragFrameDelta = 0;
        _dragTrackOffset = 0;
        _dragNewTrackInsertIndex = null;
        _trimVisualApplied = false;
        _snapState.CurrentlySnappedTo = null;
        if (kind is DragKind.TrimLeft or DragKind.TrimRight)
            _preferCachedStrip = true;
    }

    private HashSet<string> LinkedUnlessAlt(string clipId)
        => AltIgnoresLinks() ? new HashSet<string>([clipId], StringComparer.Ordinal) : ExpandLinked(clipId);
    private HashSet<string> LinkedUnlessAlt(IEnumerable<string> clipIds)
        => AltIgnoresLinks() ? clipIds.ToHashSet(StringComparer.Ordinal) : ExpandLinked(clipIds);

    private HashSet<string> ExpandLinked(string clipId)
        => ExpandLinked(new HashSet<string>([clipId], StringComparer.Ordinal));

    private HashSet<string> ExpandLinked(IEnumerable<string> clipIds)
    {
        if (_session is null)
            return clipIds.ToHashSet(StringComparer.Ordinal);
        var timeline = _session.ActiveTimeline;
        var expanded = LinkGroup.Expand(timeline, clipIds);
        expanded.RemoveWhere(id => EditCommands.IsClipOnLockedTrack(timeline, id));
        return expanded;
    }

    private static bool AltIgnoresLinks()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(CoreVirtualKeyStates.Down);

    private bool ShouldExpandLinksForClip(string clipId)
        => _session is null
            ? !AltIgnoresLinks()
            : LinkGroup.ShouldExpandLinks(_session.ActiveTimeline, _selectedClipIds, clipId, AltIgnoresLinks());

    private bool ShouldExpandCurrentSelection()
        => _session is null
            ? !AltIgnoresLinks()
            : LinkGroup.ShouldExpandSelection(_session.ActiveTimeline, _selectedClipIds, AltIgnoresLinks());

    private void ToggleSelection(IEnumerable<string> clipIds, string clickedClipId)
    {
        var next = new HashSet<string>(_selectedClipIds, StringComparer.Ordinal);
        var focused = _selectedClipId;
        var batch = clipIds.ToHashSet(StringComparer.Ordinal);
        var allSelected = batch.All(next.Contains);
        if (allSelected)
        {
            next.ExceptWith(batch);
            if (focused is not null && !next.Contains(focused))
                focused = next.OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
        }
        else
        {
            next.UnionWith(batch);
            focused = batch.Contains(clickedClipId)
                ? clickedClipId
                : batch.OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault() ?? focused;
        }
        ApplySelection(next, focused, notify: true);
    }

    private List<DragClipSnapshot> CreateDragSnapshots(IEnumerable<string> clipIds)
    {
        if (_session is null) return [];
        var selectedIds = clipIds.ToHashSet(StringComparer.Ordinal);
        var snapshots = new List<DragClipSnapshot>();
        for (var trackIndex = 0; trackIndex < _session.ActiveTimeline.Tracks.Count; trackIndex++)
        {
            if (_session.ActiveTimeline.Tracks[trackIndex].Locked) continue;
            foreach (var clip in _session.ActiveTimeline.Tracks[trackIndex].Clips)
            {
                if (selectedIds.Contains(clip.Id))
                {
                    snapshots.Add(new DragClipSnapshot(
                        clip.Id, trackIndex, clip.StartFrame, clip.DurationFrames, clip.TrimStartFrame, clip.Speed, clip.MediaType));
                }
            }
        }
        return snapshots;
    }

    private void LaneCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragKind != DragKind.None || _marqueeSelecting || _draggingPlayhead || _seeking) return;
        var point = e.GetCurrentPoint(LaneCanvas).Position;

        if (EditorEditContext.Current.ToolMode == EditorToolMode.Blade)
        {
            e.Handled = true;
            return;
        }

        if (EditorEditContext.Current.ToolMode == EditorToolMode.Select && TryBeginTransitionResize(point, e))
        {
            e.Handled = true;
            return;
        }

        SelectTransition(null);
        BeginMarquee(point, e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
        LaneCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void LaneCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(LaneCanvas).Position;
        if (_dragKind == DragKind.Transition)
        {
            UpdateTransitionDrag(point);
            MaybeAutoScrollToward(point.X);
            e.Handled = true;
            return;
        }
        UpdateBladeIndication(point);

        if (_marqueeSelecting)
        {
            UpdateMarquee(point);
            MaybeAutoScrollToward(point.X);
            e.Handled = true;
            return;
        }

        if (_dragKind == DragKind.None)
        {
            UpdateTransitionHover(point);
            return;
        }
        if (_session is null || _dragClipId is null) return;
        var bodyPoint = e.GetCurrentPoint(BodyScroll).Position;
        var frame = FrameAtContentX(BodyScroll.HorizontalOffset + bodyPoint.X);
        var requestedDelta = frame - _dragPointerStartFrame;

        if (_dragKind == DragKind.Move)
        {
            var lanePoint = new Point(point.X, bodyPoint.Y + BodyScroll.VerticalOffset);
            UpdateMovePreview(lanePoint, bodyPoint, requestedDelta);
        }
        else
        {
            QueueTrimTarget(requestedDelta);
        }

        MaybeAutoScrollViewportX(bodyPoint.X);
        e.Handled = true;
    }

    /// <summary>When the pointer approaches the left/right edge of the body viewport, scroll the timeline.</summary>
    private void MaybeAutoScrollToward(double laneX)
        => MaybeAutoScrollViewportX(laneX + _viewLeft - BodyScroll.HorizontalOffset);

    private void MaybeAutoScrollViewportX(double viewX)
    {
        // WP-252: measure in live viewport coordinates rather than against stale _viewLeft, which
        // follows the scroll offset only at the next viewport redraw.
        var viewWidth = BodyScroll.ViewportWidth > 1 ? BodyScroll.ViewportWidth : _viewWidth;
        var contentW = double.IsNaN(BodyContent.Width) ? 0 : BodyContent.Width;
        var next = TimelineGeometry.AutoScrollHorizontalOffset(
            viewX,
            BodyScroll.HorizontalOffset,
            viewWidth,
            contentW,
            BodyScroll.ScrollableWidth);
        if (next is double target)
            BodyScroll.ChangeView(target, BodyScroll.VerticalOffset, null, disableAnimation: true);
    }

    /// <summary>Scroll so the playhead stays visible (WP-68 follow during playback).</summary>
    public void EnsurePlayheadVisible(bool forceCenter = false)
    {
        var x = _playheadFrame * _pixelsPerFrame;
        var viewLeft = BodyScroll.HorizontalOffset;
        var viewW = Math.Max(1, BodyScroll.ViewportWidth);
        var viewRight = viewLeft + viewW;
        var margin = viewW * 0.15;
        if (forceCenter || x < viewLeft + margin || x > viewRight - margin)
        {
            var target = Math.Max(0, x - viewW * 0.4);
            BodyScroll.ChangeView(target, BodyScroll.VerticalOffset, null, disableAnimation: true);
        }
    }

    private void LaneCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueeSelecting)
        {
            EndMarquee();
            LaneCanvas.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
            return;
        }

        if (_dragKind == DragKind.Transition)
        {
            CommitTransitionDrag();
            e.Handled = true;
            return;
        }

        if (_dragKind == DragKind.None || _session is null || _dragClipId is null) return;

        if (_dragKind == DragKind.Move)
        {
            var newTrackInsert = _dragNewTrackInsertIndex;
            var changed = _dragFrameDelta != 0 || _dragTrackOffset != 0 || newTrackInsert is not null;
            var result = EditResult.Success();
            string? revealTrackId = null;
            if (newTrackInsert is int insertAt)
            {
                var primary = _dragSnapshots.FirstOrDefault(s => s.ClipId == _dragClipId) ?? _dragSnapshots[0];
                var items = _dragSnapshots
                    .Select(s => new EditCommands.NewTrackDragItem(
                        s.ClipId, s.TrackIndex, s.MediaType, s.StartFrame + _dragFrameDelta))
                    .ToArray();
                var plan = EditCommands.PlanMoveToNewTrack(
                    _session.ActiveTimeline, items, primary.TrackIndex, _dragNewTrackType, insertAt);
                var newTrackClips = new List<(string ClipId, int NewStartFrame)>();
                var existingMoves = new List<ClipMove>();
                foreach (var entry in plan)
                {
                    if (entry.LandsOnNewTrack)
                        newTrackClips.Add((entry.ClipId, entry.NewStartFrame));
                    else
                        existingMoves.Add(new ClipMove(entry.ClipId, entry.PreInsertTrackIndex, entry.NewStartFrame));
                }
                result = EditCommands.MoveClipsToNewTrack(
                    _session,
                    existingMoves,
                    newTrackClips,
                    _dragNewTrackType,
                    insertAt,
                    EditorEditContext.Current.OverwriteOnMove);
                if (result.Ok && newTrackClips.Count > 0)
                {
                    var movedId = newTrackClips[0].ClipId;
                    revealTrackId = _session.ActiveTimeline.Tracks
                        .FirstOrDefault(t => t.Clips.Any(c => string.Equals(c.Id, movedId, StringComparison.Ordinal)))
                        ?.Id;
                }
            }
            else if (changed)
            {
                result = EditCommands.MoveClips(_session,
                    _dragSnapshots.Select(snapshot => new ClipMove(
                        snapshot.ClipId,
                        snapshot.TrackIndex + _dragTrackOffset,
                        snapshot.StartFrame + _dragFrameDelta)).ToArray(),
                    EditorEditContext.Current.OverwriteOnMove);
            }
            ClearDragGhosts();
            ResetDragState();
            LaneCanvas.ReleasePointerCapture(e.Pointer);
            if (changed && result.Ok)
            {
                NotifyEdited();
                if (revealTrackId is not null)
                {
                    var id = revealTrackId;
                    if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ScrollTrackIntoView(id)))
                        ScrollTrackIntoView(id);
                }
            }
            else Rebuild();
        }
        else
        {
            CommitTrimDrag();
            LaneCanvas.ReleasePointerCapture(e.Pointer);
        }
        e.Handled = true;
    }

    private void LaneCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_marqueeSelecting)
            EndMarquee();

        if (_dragKind == DragKind.Transition)
        {
            CancelTransitionDrag();
            return;
        }

        if (_dragKind == DragKind.None) return;
        if (_dragKind is DragKind.TrimLeft or DragKind.TrimRight)
            RestoreTrimPreview();
        ResetDragState();
        Rebuild();
    }

    private void ToggleSelection(string clipId)
    {
        var next = new HashSet<string>(_selectedClipIds, StringComparer.Ordinal);
        var focused = _selectedClipId;
        if (!next.Remove(clipId))
        {
            next.Add(clipId);
            focused = clipId;
        }
        else if (string.Equals(focused, clipId, StringComparison.Ordinal))
        {
            focused = next.OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
        }

        ApplySelection(next, focused, notify: true);
    }

    private void BeginMarquee(Point start, bool append)
    {
        _marqueeSelecting = true;
        _marqueeStart = start;
        _marqueeBaseSelection = append
            ? new HashSet<string>(_selectedClipIds, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        if (!append)
            ApplySelection(Array.Empty<string>(), focusedClipId: null, notify: true);

        _marquee ??= new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(48, AppTheme.Accent.Brand.R, AppTheme.Accent.Brand.G, AppTheme.Accent.Brand.B)),
            BorderBrush = AppTheme.Accent.PrimaryBrush,
            BorderThickness = new Thickness(AppTheme.BorderWidth.Thin),
            CornerRadius = new CornerRadius(AppTheme.Radius.XsSm),
            IsHitTestVisible = false,
        };
        if (!OverlayCanvas.Children.Contains(_marquee))
            OverlayCanvas.Children.Add(_marquee);

        _marquee.Width = 0;
        _marquee.Height = 0;
        Canvas.SetLeft(_marquee, start.X);
        Canvas.SetTop(_marquee, start.Y);
        _marquee.Visibility = Visibility.Visible;
    }

    private void UpdateMarquee(Point point)
    {
        if (_marquee is null) return;
        var left = Math.Min(_marqueeStart.X, point.X);
        var top = Math.Min(_marqueeStart.Y, point.Y);
        var rect = new Rect(left, top, Math.Abs(point.X - _marqueeStart.X), Math.Abs(point.Y - _marqueeStart.Y));
        _marquee.Width = rect.Width;
        _marquee.Height = rect.Height;
        Canvas.SetLeft(_marquee, rect.X);
        Canvas.SetTop(_marquee, rect.Y);

        var hits = ClipIdsIntersecting(rect).ToHashSet(StringComparer.Ordinal);
        if (!AltIgnoresLinks())
            hits = ExpandLinked(hits);
        var next = new HashSet<string>(_marqueeBaseSelection, StringComparer.Ordinal);
        next.UnionWith(hits);
        var focused = next.Contains(_selectedClipId ?? string.Empty)
            ? _selectedClipId
            : hits.OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
        ApplySelection(next, focused, notify: true);
    }

    private IEnumerable<string> ClipIdsIntersecting(Rect selection)
    {
        if (_session is null || selection.Width <= 0 || selection.Height <= 0)
            return Array.Empty<string>();

        var hits = new List<string>();
        for (var trackIndex = 0; trackIndex < _session.ActiveTimeline.Tracks.Count; trackIndex++)
        {
            if (_session.ActiveTimeline.Tracks[trackIndex].Locked) continue;
            foreach (var clip in _session.ActiveTimeline.Tracks[trackIndex].Clips)
            {
                var (x, y, width, height) = _geo.ClipRect(clip, trackIndex);
                var left = selection.X + _viewLeft;
                var right = selection.Right + _viewLeft;
                if (x < right && x + width > left
                    && y < selection.Bottom && y + height > selection.Top)
                {
                    hits.Add(clip.Id);
                }
            }
        }
        return hits;
    }

    private void EndMarquee()
    {
        _marqueeSelecting = false;
        _marqueeBaseSelection.Clear();
        if (_marquee is not null)
            _marquee.Visibility = Visibility.Collapsed;
    }

    private void UpdateMovePreview(Point point, Point bodyPoint, int requestedDelta)
    {
        if (_session is null || _dragSnapshots.Count == 0) return;
        var primary = _dragSnapshots.FirstOrDefault(snapshot => snapshot.ClipId == _dragClipId)
            ?? _dragSnapshots[0];

        var minStart = _dragSnapshots.Min(snapshot => snapshot.StartFrame);
        var delta = Math.Max(-minStart, requestedDelta);
        var newPrimaryStart = primary.StartFrame + delta;
        if (EditorEditContext.Current.SnapEnabled)
        {
            var movingIds = _dragSnapshots.Select(snapshot => snapshot.ClipId).ToHashSet(StringComparer.Ordinal);
            // Any selected edge can drive the snap (Mac TimelineInputController moveClip).
            var probeOffsets = new List<int>();
            foreach (var snapshot in _dragSnapshots)
            {
                var baseOffset = snapshot.StartFrame - primary.StartFrame;
                probeOffsets.Add(baseOffset);
                probeOffsets.Add(baseOffset + snapshot.DurationFrames);
            }

            var targets = CollectSnapTargets(movingIds, includeExcludedClipBeats: false);
            var snap = SnapEngine.FindSnap(
                newPrimaryStart,
                probeOffsets,
                targets,
                _snapState,
                SnapEngine.ThresholdPixels,
                _pixelsPerFrame);
            if (snap is { } candidate)
            {
                delta = Math.Max(-minStart, candidate.Frame - candidate.ProbeOffset - primary.StartFrame);
                ShowSnapIndicator(candidate.X);
            }
            else
            {
                HideSnapIndicator();
            }
        }
        else
        {
            HideSnapIndicator();
        }

        var tracks = _session.ActiveTimeline.Tracks;
        if (primary.MediaType != ClipType.audio && bodyPoint.Y < 0)
        {
            _dragNewTrackInsertIndex = 0;
            _dragNewTrackType = ClipType.video;
            _dragFrameDelta = delta;
            _dragTrackOffset = 0;
            DrawMoveGhosts();
            return;
        }
        if (primary.MediaType == ClipType.audio && point.Y >= BottomNewAudioTrackThreshold())
        {
            _dragNewTrackInsertIndex = tracks.Count;
            _dragNewTrackType = ClipType.audio;
            _dragFrameDelta = delta;
            _dragTrackOffset = 0;
            DrawMoveGhosts();
            return;
        }

        _dragNewTrackInsertIndex = null;
        var requestedTrack = Math.Clamp(
            _geo.TrackAtY(point.Y),
            0,
            tracks.Count - 1);
        var minTrack = _dragSnapshots.Min(snapshot => snapshot.TrackIndex);
        var maxTrack = _dragSnapshots.Max(snapshot => snapshot.TrackIndex);
        var trackOffset = requestedTrack - primary.TrackIndex;
        trackOffset = Math.Clamp(trackOffset, -minTrack, tracks.Count - 1 - maxTrack);

        while (trackOffset != 0 && _dragSnapshots.Any(snapshot =>
            tracks[snapshot.TrackIndex + trackOffset].Locked
            || !EditCommands.TrackAccepts(tracks[snapshot.TrackIndex + trackOffset], snapshot.MediaType)))
        {
            trackOffset += trackOffset > 0 ? -1 : 1;
        }

        _dragFrameDelta = delta;
        _dragTrackOffset = trackOffset;
        DrawMoveGhosts();
    }

    private void UpdateTrimPreview(int requestedDelta)
    {
        if (_session is null || _dragSnapshots.Count == 0) return;

        var delta = requestedDelta;
        if (EditorEditContext.Current.SnapEnabled)
        {
            var primary = _dragSnapshots.FirstOrDefault(snapshot => snapshot.ClipId == _dragClipId)
                ?? _dragSnapshots[0];
            var excludeIds = _dragSnapshots.Select(s => s.ClipId).ToHashSet(StringComparer.Ordinal);
            // Include beats of the trimmed clips (Mac includeExcludedClipBeats: true).
            var targets = CollectSnapTargets(excludeIds, includeExcludedClipBeats: true);

            if (_dragKind == DragKind.TrimLeft)
            {
                var candidateStart = primary.StartFrame + requestedDelta;
                var snap = SnapEngine.FindSnap(
                    candidateStart,
                    targets,
                    _snapState,
                    SnapEngine.ThresholdPixels,
                    _pixelsPerFrame);
                if (snap is { } candidate)
                {
                    delta = candidate.Frame - primary.StartFrame;
                    ShowSnapIndicator(candidate.X);
                }
                else
                {
                    HideSnapIndicator();
                }
            }
            else
            {
                var originalEnd = primary.StartFrame + primary.DurationFrames;
                var candidateEnd = originalEnd + requestedDelta;
                var snap = SnapEngine.FindSnap(
                    candidateEnd,
                    targets,
                    _snapState,
                    SnapEngine.ThresholdPixels,
                    _pixelsPerFrame);
                if (snap is { } candidate)
                {
                    delta = candidate.Frame - originalEnd;
                    ShowSnapIndicator(candidate.X);
                }
                else
                {
                    HideSnapIndicator();
                }
            }
        }
        else
        {
            HideSnapIndicator();
        }

        if (delta == _dragFrameDelta && _trimVisualApplied)
            return;

        _dragFrameDelta = delta;
        _trimVisualApplied = true;
        TimelineTrace.Mark("tp", requestedDelta, delta); // WP-320 trim probe (TEMNO_TIMELINE_TRACE)
        ApplyTrimPreview();
    }

    /// <summary>
    /// WP-320. Pointer input outranks rendering, so applying the preview inside the move handler
    /// starved the frame. The move only records the latest delta; <see cref="OnTrimRendering"/>
    /// applies it at most once per rendered frame.
    /// </summary>
    private void QueueTrimTarget(int requestedDelta)
    {
        _trimRequestedDelta = requestedDelta;
        _trimTargetPending = true;
        if (_trimRenderHooked) return;
        _trimRenderHooked = true;
        CompositionTarget.Rendering += OnTrimRendering;
    }

    private void OnTrimRendering(object? sender, object e)
    {
        if (_trimApplying) return;
        if (!IsLiveTrim)
        {
            StopTrimRendering();
            return;
        }
        if (!_trimTargetPending) return;
        var requested = _trimRequestedDelta;
        _trimTargetPending = false;
        _trimApplying = true;
        try
        {
            UpdateTrimPreview(requested);
        }
        finally
        {
            _trimApplying = false;
        }
    }

    /// <summary>Applies a delta the pointer recorded and no frame has consumed yet. Release calls this before it commits.</summary>
    private void FlushTrimTarget()
    {
        if (_trimApplying || !_trimTargetPending || !IsLiveTrim) return;
        var requested = _trimRequestedDelta;
        _trimTargetPending = false;
        _trimApplying = true;
        try
        {
            UpdateTrimPreview(requested);
        }
        finally
        {
            _trimApplying = false;
        }
    }

    private void StopTrimRendering()
    {
        _trimTargetPending = false;
        if (!_trimRenderHooked) return;
        CompositionTarget.Rendering -= OnTrimRendering;
        _trimRenderHooked = false;
    }

    /// <summary>The trim branch of <see cref="LaneCanvas_PointerReleased"/>. The probe calls this same method.</summary>
    private void CommitTrimDrag()
    {
        FlushTrimTarget();
        var changed = _dragFrameDelta != 0 || _dragTrackOffset != 0;
        var result = EditResult.Success();
        RestoreTrimPreview();
        if (changed && _session is not null)
        {
            var leftEdge = _dragKind == DragKind.TrimLeft;
            result = EditCommands.TrimClipEdges(_session,
                _dragSnapshots.Select(snapshot => new ClipEdgeTrim(
                    snapshot.ClipId,
                    leftEdge,
                    leftEdge
                        ? snapshot.StartFrame + _dragFrameDelta
                        : snapshot.StartFrame + snapshot.DurationFrames + _dragFrameDelta)).ToArray());
        }
        ResetDragState();
        // The release rebuild asks for the exact cells. Until they land, draw the nearest cached one.
        _preferCachedStrip = true;
        if (changed && result.Ok) NotifyEdited();
        else Rebuild();
    }

    private List<SnapEngine.SnapTarget> CollectSnapTargets(
        IReadOnlySet<string>? excludeClipIds,
        bool includeExcludedClipBeats)
    {
        if (_session is null) return [];
        var fps = Math.Max(1, _session.ActiveTimeline.Fps);
        return SnapEngine.CollectTargets(
            _session.ActiveTimeline.Tracks,
            _playheadFrame,
            excludeClipIds,
            includePlayhead: true,
            beatFrames: clip => BeatSnapFrames(clip, fps),
            includeExcludedClipBeats: includeExcludedClipBeats);
    }

    private IEnumerable<int> BeatSnapFrames(Clip clip, int fps)
    {
        if (_beatStore is null || string.IsNullOrEmpty(clip.MediaRef))
            return Array.Empty<int>();

        var analysis = _beatStore.GetAnalysis(clip.MediaRef);
        if (analysis is null || (analysis.Beats.Length == 0 && analysis.Downbeats.Length == 0))
            return Array.Empty<int>();

        var frames = new HashSet<int>();
        foreach (var seconds in analysis.Beats.Concat(analysis.Downbeats))
        {
            var frame = clip.TimelineFrameFromSourceSeconds(seconds, fps);
            if (frame is int f)
                frames.Add(f);
        }
        return frames;
    }
}
