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
    private void DrawHeaders(Timeline timeline)
    {
        HeaderCanvas.Children.Clear();
        for (var ti = 0; ti < timeline.Tracks.Count; ti++)
        {
            var track = timeline.Tracks[ti];
            // Header canvas Y is track-local (ruler lives in the fixed XAML spacer above HeaderScroll).
            var y = HeaderTrackY(ti);
            var h = _geo.TrackHeightAt(ti);
            var color = TrackColor(track.Type);
            var bg = new Border
            {
                Width = HeaderCol.Width.Value,
                Height = h,
                Background = AppTheme.Background.RaisedBrush,
                BorderBrush = AppTheme.Border.SubtleBrush,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Tag = track.Id,
                Child = BuildTrackHeader(timeline, ti, track, color),
            };
            bg.RightTapped += TrackHeader_RightTapped;
            Canvas.SetLeft(bg, 0);
            Canvas.SetTop(bg, y);
            HeaderCanvas.Children.Add(bg);

            var resizeHandle = new Border
            {
                Width = HeaderCol.Width.Value,
                Height = TimelineGeometry.TrackResizeHandleZone * 2,
                Background = new SolidColorBrush(Colors.Transparent),
                Tag = track.Id,
            };
            resizeHandle.PointerPressed += TrackResizeHandle_PointerPressed;
            Canvas.SetLeft(resizeHandle, 0);
            Canvas.SetTop(resizeHandle, y + h - TimelineGeometry.TrackResizeHandleZone);
            HeaderCanvas.Children.Add(resizeHandle);
        }

        var firstAudio = timeline.Tracks.FindIndex(track => track.Type == ClipType.audio);
        if (firstAudio > 0 && firstAudio < timeline.Tracks.Count)
        {
            var divider = new Border
            {
                Width = HeaderCol.Width.Value,
                Height = 2,
                Background = AppTheme.Border.PrimaryBrush,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(divider, 0);
            Canvas.SetTop(divider, HeaderTrackY(firstAudio) - 1);
            HeaderCanvas.Children.Add(divider);
        }
    }

    /// <summary>
    /// Y of track <paramref name="index"/> inside <see cref="HeaderCanvas"/> (matches LaneCanvas track-local Y).
    /// </summary>
    private double HeaderTrackY(int index) => _geo.TrackYAt(index);

    private Grid BuildTrackHeader(Timeline timeline, int trackIndex, Track track, Color color)
    {
        var grid = new Grid
        {
            // WP-197 exception: timeline track-header chrome (off-scale 3 px); WP-200 owns this surface.
            Margin = new Thickness(3, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var grip = new Border
        {
            Width = 18,
            Height = 18,
            // WP-200: a null Background is not hit-testable — the press fell through to
            // HeaderCanvas and the drag never started (found driving the reorder receipt).
            Background = new SolidColorBrush(Colors.Transparent),
            Tag = track.Id,
            Child = FluentPath.At(Icons.ReOrderDotsVertical, AppTheme.IconSize.Xs, AppTheme.Text.TertiaryBrush),
        };
        ToolTipService.SetToolTip(grip, "Drag to reorder this track");
        AutomationProperties.SetName(grip, $"Drag {TrackDisplayLabel(timeline, trackIndex)} to reorder");
        grip.PointerPressed += TrackReorderGrip_PointerPressed;
        grid.Children.Add(grip);

        var label = new StackPanel
        {
            Margin = new Thickness(3, 0, 2, 0), // WP-197 exception: timeline track-header chrome (off-scale 3 px); WP-200 owns this surface.
            Spacing = AppTheme.Spacing.Xxs,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.Children.Add(new TextBlock
        {
            Text = TrackDisplayLabel(timeline, trackIndex),
            FontSize = AppTheme.FontSize.Xs,
            Foreground = AppTheme.Text.SecondaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        label.Children.Add(new Border
        {
            Width = 22,
            Height = 3,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(color),
        });
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = AppTheme.Spacing.Hairline,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (track.Type == ClipType.audio)
        {
            var mutedBrush = track.Muted ? AppTheme.Accent.PrimaryBrush : AppTheme.Text.TertiaryBrush;
            controls.Children.Add(CreateTrackHeaderButton(
                track.Id,
                TrackHeaderAction.ToggleMuted,
                track.Muted ? TrackGlyph.MutedSpeaker(mutedBrush) : TrackGlyph.Speaker(mutedBrush),
                track.Muted,
                track.Muted ? "Unmute track" : "Mute track",
                track.Muted ? "Unmute track" : "Mute track"));
        }
        else
        {
            var hiddenBrush = track.Hidden ? AppTheme.Accent.PrimaryBrush : AppTheme.Text.TertiaryBrush;
            controls.Children.Add(CreateTrackHeaderButton(
                track.Id,
                TrackHeaderAction.ToggleHidden,
                track.Hidden ? TrackGlyph.HiddenEye(hiddenBrush) : TrackGlyph.Eye(hiddenBrush),
                track.Hidden,
                track.Hidden ? "Show track in preview and export" : "Hide track from preview and export",
                track.Hidden ? "Show track" : "Hide track"));
        }
        var trackLabel = TrackDisplayLabel(timeline, trackIndex);
        var lockedBrush = track.Locked ? AppTheme.Accent.PrimaryBrush : AppTheme.Text.TertiaryBrush;
        controls.Children.Add(CreateTrackHeaderButton(
            track.Id,
            TrackHeaderAction.ToggleLocked,
            track.Locked ? TrackGlyph.LockClosed(lockedBrush) : TrackGlyph.LockOpen(lockedBrush),
            track.Locked,
            track.Locked ? $"Unlock {trackLabel} to allow edits" : $"Lock {trackLabel} to prevent edits",
            track.Locked ? $"Unlock {trackLabel}" : $"Lock {trackLabel}"));
        Grid.SetColumn(controls, 2);
        grid.Children.Add(controls);
        return grid;
    }

    private Button CreateTrackHeaderButton(
        string trackId,
        TrackHeaderAction action,
        FrameworkElement glyph,
        bool active,
        string tooltip,
        string automationName,
        bool enabled = true)
    {
        var button = new Button
        {
            MinWidth = TrackGlyph.ButtonSize,
            MinHeight = TrackGlyph.ButtonSize,
            Padding = new Thickness(0),
            Content = glyph,
            Opacity = active ? 1 : 0.58,
            IsEnabled = enabled,
            Tag = new TrackHeaderActionTag(trackId, action),
        };
        ToolTipService.SetToolTip(button, tooltip);
        AutomationProperties.SetName(button, automationName);
        button.Click += TrackHeaderAction_Click;
        return button;
    }

    /// <summary>
    /// WP-200. Eye / speaker / lock glyphs for the track-header buttons, replacing the H and S
    /// letters. Hand paths on the 16-grid (the track header never got the WP-206 catalog pass —
    /// see that package for the shared icon rule). States keep the old button pattern: the accent
    /// brush at full opacity when on, the tertiary brush at 0.58 when off. Headers rebuild on
    /// every state change, so the eye-off / muted-speaker glyphs always match the track.
    /// </summary>
    private static class TrackGlyph
    {
        public const double ButtonSize = 20;
        public static FrameworkElement Eye(Brush brush) => FluentPath.At(Icons.Eye, AppTheme.IconSize.Xs, brush);
        public static FrameworkElement HiddenEye(Brush brush) => FluentPath.At(Icons.EyeOff, AppTheme.IconSize.Xs, brush);
        public static FrameworkElement Speaker(Brush brush) => FluentPath.At(Icons.Speaker2, AppTheme.IconSize.Xs, brush);
        public static FrameworkElement MutedSpeaker(Brush brush) => FluentPath.At(Icons.SpeakerMute, AppTheme.IconSize.Xs, brush);
        public static FrameworkElement LockClosed(Brush brush) => FluentPath.At(Icons.LockClosed, AppTheme.IconSize.Xs, brush);
        public static FrameworkElement LockOpen(Brush brush) => FluentPath.At(Icons.LockOpen, AppTheme.IconSize.Xs, brush);
    }

    private static string TrackDisplayLabel(Timeline timeline, int trackIndex)
    {
        // WP-188: the Mac's numbering — V counts up from the bottom of the video stack, A counts down
        // from the top of the audio stack, so V1 sits directly over A1. Rows, ids and file order are
        // unchanged: index 0 is still the top row and still composites on top.
        var types = new ClipType[timeline.Tracks.Count];
        for (var i = 0; i < types.Length; i++) types[i] = timeline.Tracks[i].Type;
        return Temno.Core.Ui.TrackLabels.LabelFor(types, trackIndex);
    }

    private void TrackHeaderAction_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || sender is not FrameworkElement { Tag: TrackHeaderActionTag tag }) return;
        var track = _session.ActiveTimeline.Tracks.FirstOrDefault(candidate => candidate.Id == tag.TrackId);
        if (track is null) return;

        var result = tag.Action switch
        {
            TrackHeaderAction.ToggleMuted => EditCommands.SetTrackFlags(
                _session, tag.TrackId, muted: !track.Muted),
            TrackHeaderAction.ToggleHidden => EditCommands.SetTrackFlags(
                _session, tag.TrackId, hidden: !track.Hidden),
            TrackHeaderAction.ToggleLocked => EditCommands.SetTrackFlags(
                _session, tag.TrackId, locked: !track.Locked),
            _ => EditResult.Fail("unknown track header action"),
        };

        if (result.Ok) NotifyEdited();
    }

    public bool TryToggleTrackLocked(string trackId)
    {
        if (_session is null || string.IsNullOrWhiteSpace(trackId)) return false;
        var track = _session.ActiveTimeline.Tracks.FirstOrDefault(candidate => candidate.Id == trackId);
        if (track is null) return false;
        var result = EditCommands.SetTrackFlags(_session, trackId, locked: !track.Locked);
        if (!result.Ok) return false;
        NotifyEdited();
        return true;
    }

    private void TrackReorderGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null || sender is not FrameworkElement { Tag: string trackId }) return;
        var sourceIndex = _session.ActiveTimeline.Tracks.FindIndex(track => track.Id == trackId);
        if (sourceIndex < 0) return;

        _trackReorder = new TrackReorderState(trackId, sourceIndex);
        HoldHeaderScroll();
        ShowTrackReorderIndicator(sourceIndex);
        HeaderCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void TrackResizeHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null || sender is not FrameworkElement { Tag: string trackId }) return;
        var track = _session.ActiveTimeline.Tracks.FirstOrDefault(candidate => candidate.Id == trackId);
        if (track is null) return;

        _trackResize = new TrackResizeState(trackId, track.DisplayHeight);
        HoldHeaderScroll();
        HeaderCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    /// <summary>
    /// WP-200. Both header gestures drag vertically, which is also HeaderScroll's pan axis:
    /// after a few pixels the ScrollViewer takes the pointer back (PointerCaptureLost cancels
    /// the gesture) and the drag silently does nothing — found driving the reorder receipt.
    /// Holding vertical scroll for the gesture's life fixes reorder and resize together.
    /// The bar is already Hidden, so there is no visual change.
    /// </summary>
    private void HoldHeaderScroll() => HeaderScroll.VerticalScrollMode = ScrollMode.Disabled;

    private void RestoreHeaderScroll() => HeaderScroll.VerticalScrollMode = ScrollMode.Enabled;

    private void HeaderCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null) return;
        var point = e.GetCurrentPoint(HeaderCanvas).Position;
        if (_trackResize is { } resize)
        {
            var index = _session.ActiveTimeline.Tracks.FindIndex(track => track.Id == resize.TrackId);
            if (index >= 0)
            {
                // point.Y is header-local; HeaderTrackY is the top of the track in the same space.
                var nextHeight = TimelineGeometry.ClampTrackHeight(point.Y - HeaderTrackY(index));
                var track = _session.ActiveTimeline.Tracks[index];
                if (Math.Abs(track.DisplayHeight - nextHeight) >= 0.001)
                {
                    track.DisplayHeight = nextHeight;
                    Rebuild();
                }
            }
            e.Handled = true;
            return;
        }

        if (_trackReorder is { } reorder)
        {
            var count = _session.ActiveTimeline.Tracks.Count;
            var targetIndex = Math.Clamp(_geo.TrackAtY(point.Y), 0, Math.Max(0, count - 1));
            targetIndex = ResolveTrackReorderTarget(reorder.TrackId, targetIndex);
            if (targetIndex != reorder.TargetIndex
                || (_trackReorderIndicator is not null && !HeaderCanvas.Children.Contains(_trackReorderIndicator)))
            {
                _trackReorder = reorder with { TargetIndex = targetIndex };
                ShowTrackReorderIndicator(targetIndex);
            }
            e.Handled = true;
        }
    }

    private void HeaderCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null) return;
        var changed = false;
        EditResult result = EditResult.Success();

        if (_trackResize is { })
        {
            (changed, result) = CommitTrackResizeIfActive();
        }
        else if (_trackReorder is { } reorder)
        {
            _trackReorder = null;
            HideTrackReorderIndicator();
            var before = _session.ActiveTimeline.Tracks.Select(track => track.Id).ToArray();
            result = EditCommands.ReorderTrack(_session, reorder.TrackId, reorder.TargetIndex);
            changed = result.Ok && !before.SequenceEqual(_session.ActiveTimeline.Tracks.Select(track => track.Id));
        }
        else
        {
            return;
        }

        HeaderCanvas.ReleasePointerCapture(e.Pointer);
        RestoreHeaderScroll();
        if (changed && result.Ok) NotifyEdited();
        else Rebuild();
        e.Handled = true;
    }

    private void HeaderCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // Synthetic or interrupted gestures sometimes raise CaptureLost without PointerReleased.
        // Commit a finished resize so live preview is not discarded; cancel incomplete reorder.
        if (_trackResize is { })
        {
            var (changed, result) = CommitTrackResizeIfActive();
            RestoreHeaderScroll();
            if (changed && result.Ok) NotifyEdited();
            else Rebuild();
            return;
        }

        _trackReorder = null;
        HideTrackReorderIndicator();
        RestoreHeaderScroll();
        Rebuild();
    }

    /// <summary>
    /// Ends an in-progress track resize: restores the pre-drag height, then records one
    /// <see cref="EditCommands.SetTrackHeight"/> when the live height actually changed.
    /// </summary>
    private (bool Changed, EditResult Result) CommitTrackResizeIfActive()
    {
        if (_session is null || _trackResize is not { } resize)
            return (false, EditResult.Success());

        var track = _session.ActiveTimeline.Tracks.FirstOrDefault(candidate => candidate.Id == resize.TrackId);
        var finalHeight = track?.DisplayHeight ?? resize.OriginalHeight;
        if (track is not null)
            track.DisplayHeight = resize.OriginalHeight;
        _trackResize = null;

        if (Math.Abs(finalHeight - resize.OriginalHeight) < 0.001)
            return (false, EditResult.Success());

        return (true, EditCommands.SetTrackHeight(_session, resize.TrackId, finalHeight));
    }

    private void ShowTrackReorderIndicator(int targetIndex)
    {
        _trackReorderIndicator ??= new Border
        {
            Height = 2,
            Background = AppTheme.Accent.PrimaryBrush,
            IsHitTestVisible = false,
        };
        if (!HeaderCanvas.Children.Contains(_trackReorderIndicator))
            HeaderCanvas.Children.Add(_trackReorderIndicator);

        _trackReorderIndicator.Width = HeaderCol.Width.Value;
        Canvas.SetLeft(_trackReorderIndicator, 0);
        Canvas.SetTop(_trackReorderIndicator, TrackReorderIndicatorY(targetIndex));
        _trackReorderIndicator.Visibility = Visibility.Visible;
    }

    private int ResolveTrackReorderTarget(string trackId, int requestedIndex)
    {
        if (_session is null) return requestedIndex;
        var tracks = _session.ActiveTimeline.Tracks;
        var sourceIndex = tracks.FindIndex(track => track.Id == trackId);
        if (sourceIndex < 0) return requestedIndex;

        var moving = tracks[sourceIndex];
        var remaining = tracks.Where((_, index) => index != sourceIndex).ToList();
        var firstAudioIndex = remaining.FindIndex(track => track.Type == ClipType.audio);
        if (firstAudioIndex < 0) firstAudioIndex = remaining.Count;
        return moving.Type == ClipType.audio
            ? Math.Clamp(requestedIndex, firstAudioIndex, remaining.Count)
            : Math.Clamp(requestedIndex, 0, firstAudioIndex);
    }

    private double TrackReorderIndicatorY(int destinationIndex)
    {
        if (_session is null || _trackReorder is not { } reorder)
            return HeaderTrackY(destinationIndex);

        var sourceIndex = _session.ActiveTimeline.Tracks.FindIndex(track => track.Id == reorder.TrackId);
        if (sourceIndex < 0 || sourceIndex == destinationIndex)
            return HeaderTrackY(destinationIndex);

        var insertionEdge = destinationIndex < sourceIndex ? destinationIndex : destinationIndex + 1;
        return insertionEdge < _geo.TrackCount
            ? HeaderTrackY(insertionEdge)
            : Math.Max(0, _geo.ContentHeight - _geo.RulerHeight);
    }

    private void HideTrackReorderIndicator()
    {
        if (_trackReorderIndicator is null) return;
        HeaderCanvas.Children.Remove(_trackReorderIndicator);
        _trackReorderIndicator.Visibility = Visibility.Collapsed;
    }

    public bool TryAddTrack(ClipType type, int? insertIndex = null)
    {
        if (_session is null) return false;
        var result = EditCommands.AddTrack(_session, type, insertIndex, out var createdId);
        if (!result.Ok) return false;
        NotifyEdited();
        if (createdId is not null)
        {
            var id = createdId;
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => ScrollTrackIntoView(id)))
                ScrollTrackIntoView(id);
        }
        return true;
    }

    public bool TryRemoveTrack(string trackId)
    {
        if (_session is null || string.IsNullOrWhiteSpace(trackId)) return false;
        var result = EditCommands.RemoveTrack(_session, trackId);
        if (!result.Ok) return false;
        // Prune any clip ids that lived on the removed track.
        var liveIds = _session.ActiveTimeline.Tracks
            .SelectMany(t => t.Clips)
            .Select(c => c.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (_selectedClipIds.Any(id => !liveIds.Contains(id)))
        {
            var remaining = _selectedClipIds.Where(liveIds.Contains).ToArray();
            var nextFocus = _selectedClipId is not null && liveIds.Contains(_selectedClipId)
                ? _selectedClipId
                : remaining.FirstOrDefault();
            ApplySelection(remaining, nextFocus, notify: true, rebuild: false);
        }
        NotifyEdited();
        return true;
    }

    private void ScrollTrackIntoView(string trackId)
    {
        if (_session is null) return;
        var index = _session.ActiveTimeline.Tracks.FindIndex(t => t.Id == trackId);
        if (index < 0) return;
        var top = HeaderTrackY(index);
        var bottom = top + _geo.TrackHeightAt(index);
        var viewTop = BodyScroll.VerticalOffset;
        var viewHeight = BodyScroll.ViewportHeight > 1 ? BodyScroll.ViewportHeight : BodyScroll.ActualHeight;
        if (viewHeight <= 1)
            viewHeight = HeaderScroll.ViewportHeight > 1 ? HeaderScroll.ViewportHeight : HeaderScroll.ActualHeight;
        viewHeight = Math.Max(1, viewHeight);
        if (top < viewTop)
        {
            BodyScroll.ChangeView(null, top, null, disableAnimation: true);
            ApplyHeaderVerticalOffset(top);
        }
        else if (bottom > viewTop + viewHeight)
        {
            var target = Math.Max(0, bottom - viewHeight);
            var heightChanged = SyncHeaderCanvasHeight(CurrentBodyContentHeight());
            BodyScroll.ChangeView(null, target, null, disableAnimation: true);
            if (heightChanged && target > HeaderScroll.ScrollableHeight + 0.5)
                ScheduleHeaderVerticalSync();
            else
                ApplyHeaderVerticalOffset(target);
        }
    }

    private void AddVideoTrack_Click(object sender, RoutedEventArgs e)
        => TryAddTrack(ClipType.video);

    private void AddAudioTrack_Click(object sender, RoutedEventArgs e)
        => TryAddTrack(ClipType.audio);

    private void TrackHeader_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_session is null || sender is not FrameworkElement { Tag: string trackId }) return;
        e.Handled = true;
        ShowTrackHeaderContextMenu(HeaderCanvas, e.GetPosition(HeaderCanvas), trackId);
    }

    private void HeaderCanvas_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_session is null || e.Handled) return;
        e.Handled = true;
        var anchor = sender as FrameworkElement ?? HeaderScroll;
        ShowTrackHeaderContextMenu(anchor, e.GetPosition(anchor), trackId: null);
    }

    private void ShowTrackHeaderContextMenu(FrameworkElement anchor, Point at, string? trackId)
    {
        if (_session is null) return;
        var timeline = _session.ActiveTimeline;
        var flyout = new MenuFlyout();

        void AddItem(string text, Action action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
            item.Click += (_, _) => action();
            flyout.Items.Add(item);
        }

        var trackIndex = trackId is null ? -1 : timeline.Tracks.FindIndex(t => t.Id == trackId);
        if (trackIndex >= 0)
        {
            var track = timeline.Tracks[trackIndex];
            var label = TrackDisplayLabel(timeline, trackIndex);
            if (track.Type == ClipType.audio)
            {
                AddItem("Add Audio Track Above", () => TryAddTrack(ClipType.audio, trackIndex));
                AddItem("Add Audio Track Below", () => TryAddTrack(ClipType.audio, trackIndex + 1));
                AddItem("Add Video Track", () => TryAddTrack(ClipType.video));
            }
            else
            {
                AddItem("Add Video Track Above", () => TryAddTrack(ClipType.video, trackIndex));
                AddItem("Add Video Track Below", () => TryAddTrack(ClipType.video, trackIndex + 1));
                AddItem("Add Audio Track", () => TryAddTrack(ClipType.audio));
            }

            flyout.Items.Add(new MenuFlyoutSeparator());
            AddItem(
                track.Locked ? $"Unlock Track {label}" : $"Lock Track {label}",
                () => TryToggleTrackLocked(track.Id));
            var hasMulticam = track.Clips.Any(c => !string.IsNullOrEmpty(c.MulticamGroupId));
            var canRemove = timeline.Tracks.Count > 1 && !hasMulticam && !track.Locked;
            AddItem($"Delete Track {label}", () => TryRemoveTrack(track.Id), enabled: canRemove);
        }
        else
        {
            AddItem("Add Video Track", () => TryAddTrack(ClipType.video));
            AddItem("Add Audio Track", () => TryAddTrack(ClipType.audio));
        }

        WindowPopout.ShowContextMenuAt(flyout, anchor, at);
    }
}
