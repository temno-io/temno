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
    private string ClipLabel(Clip clip)
    {
        if (!string.IsNullOrWhiteSpace(clip.TextContent)) return clip.TextContent!;
        if (_session is not null)
        {
            var entry = _session.Manifest.Entries.FirstOrDefault(e => e.Id == clip.MediaRef);
            if (entry is not null && !string.IsNullOrWhiteSpace(entry.Name)) return entry.Name;
        }
        return string.IsNullOrEmpty(clip.MediaRef) ? clip.MediaType.ToString() : clip.MediaRef;
    }

    private static Color TrackColor(ClipType type) => type switch
    {
        ClipType.video => AppTheme.TrackColor.Video,
        ClipType.audio => AppTheme.TrackColor.Audio,
        ClipType.image => AppTheme.TrackColor.Image,
        ClipType.text => AppTheme.TrackColor.Text,
        ClipType.lottie => AppTheme.TrackColor.Lottie,
        ClipType.sequence => AppTheme.TrackColor.Sequence,
        _ => AppTheme.Background.Prominent,
    };

    private void SyncPlayheadTransforms()
    {
        var liveLeft = BodyScroll.HorizontalOffset;
        var contentX = _playheadFrame * _pixelsPerFrame;
        var viewX = contentX - liveLeft;
        RulerTransform.X = _viewLeft - liveLeft;
        PlayheadTransform.X = LocalX(contentX);
        RulerPlayheadTransform.X = viewX;
        PlayheadHeadTransform.X = viewX - 3;
    }

    private void UpdatePlayheadVisual()
    {
        SyncPlayheadTransforms();
        PlayheadLine.Height = Math.Max(BodyContent.Height, 40);
        if (EditorEditContext.Current.ToolMode == EditorToolMode.Blade && _bladeHoverPoint is { } hover)
            UpdateBladeIndication(hover);
    }

    private void UpdateLabels()
    {
        var fps = _session?.ActiveTimeline.Fps ?? 30;
        PlayheadLabel.Text = TimelineGeometry.FormatTimecode(_playheadFrame, fps);
        // WP-181: how much time the lanes show, not pixels per frame.
        var seconds = _pixelsPerFrame > 0 ? _viewWidth / _pixelsPerFrame / Math.Max(1, fps) : 0;
        ZoomLabel.Text = seconds <= 0
            ? "Zoom"
            : seconds < 60
                ? $"Zoom · {seconds:0.#} s in view"
                : $"Zoom · {(int)(seconds / 60)}:{(int)(seconds % 60):00} in view";
    }

    private void PersistPlayhead()
    {
        if (_session is null) return;
        var id = _session.ProjectFile.ActiveTimelineId ?? _session.ActiveTimeline.Id;
        _session.ProjectFile.ViewStates ??= new Dictionary<string, TimelineViewState>();
        if (!_session.ProjectFile.ViewStates.TryGetValue(id, out var vs))
        {
            vs = new TimelineViewState();
            _session.ProjectFile.ViewStates[id] = vs;
        }
        vs.PlayheadFrame = _playheadFrame;
        vs.ZoomScale = _pixelsPerFrame;
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag }) return;
        EditorEditContext.Current.ToolMode = Enum.Parse<EditorToolMode>(tag);
        SyncModeUi();
        Rebuild();
    }

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        EditorEditContext.Current.RippleDelete = ToggleRipple.IsChecked == true;
        EditorEditContext.Current.OverwriteOnMove = ToggleOverwrite.IsChecked == true;
        EditorEditContext.Current.SnapEnabled = ToggleSnap.IsChecked == true;
    }

    private void SyncModeUi()
    {
        var mode = EditorEditContext.Current.ToolMode;
        ModeSelect.IsChecked = mode == EditorToolMode.Select;
        ModeTrim.IsChecked = mode == EditorToolMode.Trim;
        ModeBlade.IsChecked = mode == EditorToolMode.Blade;
        ToggleRipple.IsChecked = EditorEditContext.Current.RippleDelete;
        ToggleOverwrite.IsChecked = EditorEditContext.Current.OverwriteOnMove;
        ToggleSnap.IsChecked = EditorEditContext.Current.SnapEnabled;
        if (mode != EditorToolMode.Blade)
            ClearBladeIndication();
        else if (_bladeHoverPoint is { } hover)
            UpdateBladeIndication(hover);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => PixelsPerFrame = _pixelsPerFrame * 1.25;
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => PixelsPerFrame = _pixelsPerFrame / 1.25;

    public void SyncEditModeUi() => SyncModeUi();

    public void FitToWindow() => Fit_Click(this, new RoutedEventArgs());

    private void MaybeFitLongTimeline(int durationFrames)
    {
        var viewW = Math.Max(100, BodyScroll.ActualWidth > 1 ? BodyScroll.ActualWidth : 800);
        var framesOnScreen = viewW / Math.Max(_pixelsPerFrame, TimelineGeometry.MinPixelsPerFrame);
        if (durationFrames > framesOnScreen * 2)
            FitToWindow();
    }

    private void Fit_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var frames = Math.Max(1, _session.ActiveTimeline.TotalFrames);
        var viewW = Math.Max(100, BodyScroll.ActualWidth - 16);
        PixelsPerFrame = viewW / (frames + TimelineGeometry.EndPaddingFrames * 0.25);
        BodyScroll.ChangeView(0, BodyScroll.VerticalOffset, null, disableAnimation: true);
    }

    private void BodyScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_handlingWheel)
            _lastBodyScrollY = BodyScroll.VerticalOffset;
        SyncPlayheadTransforms();
        if (EditorEditContext.Current.ToolMode == EditorToolMode.Blade && _bladeHoverPoint is { } hover)
        {
            var adjusted = new Point(
                hover.X,
                hover.Y + BodyScroll.VerticalOffset - _bladeHoverVerticalOffset);
            UpdateBladeIndication(adjusted);
        }
        if (_syncingScroll || _rebuilding)
        {
            RequestViewportRedraw();
            return;
        }
        SyncHeaderScrollToBody();
        RequestViewportRedraw();
    }

    private void HeaderScroll_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_syncingScroll) return;
        if (_expectedHeaderSyncY is double expected)
        {
            _expectedHeaderSyncY = null;
            if (Math.Abs(HeaderScroll.VerticalOffset - expected) <= 0.5)
                return;
        }
        if (Math.Abs(BodyScroll.VerticalOffset - HeaderScroll.VerticalOffset) <= 0.5)
            return;
        _syncingScroll = true;
        BodyScroll.ChangeView(BodyScroll.HorizontalOffset, HeaderScroll.VerticalOffset, null, disableAnimation: true);
        _syncingScroll = false;
    }

    private bool _handlingWheel;

    private void Timeline_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_handlingWheel)
            return;

        var props = e.GetCurrentPoint(this).Properties;
        var delta = props.MouseWheelDelta;
        // WM_MOUSEWHEEL stamps Control in KeyModifiers; Shift/Alt are usually missing.
        // WinUI GetKeyStateForCurrentThread is also unreliable on wheel. Ask user32.
        var ctrl = ModifierHeld(VirtualKey.Control) || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        var shift = ModifierHeld(VirtualKey.Shift) || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        var alt = ModifierHeld(VirtualKey.Menu) || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu);

        var action = TimelineWheel.Resolve(ctrl, shift, alt, props.IsHorizontalMouseWheel);
        if (action == TimelineWheelAction.VerticalScroll)
            return;

        _handlingWheel = true;
        // ScrollViewer has already applied the notch (handledEventsToo). Pin Y to the
        // last committed offset so Ctrl+zoom and Shift+pan do not also scroll tracks.
        var pinnedY = _lastBodyScrollY;
        e.Handled = true;

        if (action == TimelineWheelAction.Zoom)
        {
            var factor = delta > 0 ? TimelineWheel.WheelZoomFactor : 1 / TimelineWheel.WheelZoomFactor;
            var oldPpf = _pixelsPerFrame;
            var newPpf = Math.Clamp(oldPpf * factor, TimelineGeometry.MinPixelsPerFrame, TimelineGeometry.MaxPixelsPerFrame);
            if (Math.Abs(newPpf - oldPpf) >= 0.0001)
            {
                var contentX = WheelContentX(e);
                var newOffset = TimelineWheel.ZoomHorizontalOffset(contentX, oldPpf, newPpf, BodyScroll.HorizontalOffset);
                PixelsPerFrame = newPpf;
                PinScroll(newOffset, pinnedY);
                return;
            }

            PinScroll(BodyScroll.HorizontalOffset, pinnedY);
            return;
        }

        var dx = props.IsHorizontalMouseWheel ? delta : -delta;
        PinScroll(Math.Max(0, BodyScroll.HorizontalOffset + dx), pinnedY);
    }

    private void PinScroll(double x, double y)
    {
        BodyScroll.ChangeView(x, y, null, disableAnimation: true);
        ApplyHeaderVerticalOffset(y);
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                BodyScroll.ChangeView(x, y, null, disableAnimation: true);
                ApplyHeaderVerticalOffset(y);
                _handlingWheel = false;
            }))
            _handlingWheel = false;
    }

    private double WheelContentX(PointerRoutedEventArgs e)
    {
        var viewportW = BodyScroll.ViewportWidth;
        if (viewportW <= 1)
            viewportW = BodyScroll.ActualWidth;
        viewportW = Math.Max(1, viewportW);
        var x = e.GetCurrentPoint(BodyScroll).Position.X;
        // Header column is to the left of BodyScroll → negative X. Zoom about centre,
        // not the viewport edge (the failure WP-146 exists to stop).
        if (double.IsNaN(x) || x < 0)
            x = viewportW / 2;
        else
            x = Math.Clamp(x, 0, viewportW);
        return BodyScroll.HorizontalOffset + x;
    }

    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static bool ModifierHeld(VirtualKey key)
    {
        var vk = key switch
        {
            VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift => VkShift,
            VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl => VkControl,
            VirtualKey.Menu => VkMenu,
            _ => (int)key,
        };
        if ((GetAsyncKeyState(vk) & 0x8000) != 0)
            return true;
        return InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    }
}
