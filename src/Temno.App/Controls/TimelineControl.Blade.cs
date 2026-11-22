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
    private readonly record struct BladeResolveResult(int Frame, bool SnappedToPlayhead, double ContentX);

    private void TimelineBody_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ClearBladeIndication();
        if (_dragKind != DragKind.Transition)
            ClearTransitionHover();
    }

    private void ApplyBladeBodyCursor(bool on)
    {
        if (_bladeBodyCursor == on) return;
        _bladeBodyCursor = on;
        _transparentBladeCursor ??= CreateTransparentCursor()
            ?? InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        // The XAML blade badge is the visual pointer. A transparent native cursor avoids a
        // competing arrow/crosshair while preserving a safe arrow fallback if interop is unavailable.
        ProtectedCursor = on
            ? _transparentBladeCursor
            : InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private void TryBladeSplit(string clipId, Point lanePoint)
    {
        var clip = FindClip(clipId);
        if (clip is null || _session is null || EditCommands.IsClipOnLockedTrack(_session.ActiveTimeline, clipId))
        {
            ClearBladeIndication();
            return;
        }

        var expand = ShouldExpandLinksForClip(clipId);
        SetSelectedClips(expand ? ExpandLinked(clipId) : [clipId], clipId);
        var resolved = ResolveBladeFrame(lanePoint.X, clip);
        if (resolved is null || _session is null)
        {
            UpdateBladeIndication(lanePoint);
            return;
        }

        HideBladePreviewAndSnap();
        var result = EditCommands.SplitClipsAtFrame(_session, [clipId], resolved.Value.Frame, expandLinks: expand);
        if (result.Ok)
            NotifyEdited();
        else
            UpdateBladeIndication(lanePoint);
    }

    private void UpdateBladeIndication(Point lanePoint)
    {
        if (EditorEditContext.Current.ToolMode != EditorToolMode.Blade)
        {
            ClearBladeIndication();
            return;
        }

        if (_seeking || _marqueeSelecting || _dragKind != DragKind.None || _draggingPlayhead)
        {
            ClearBladeIndication();
            return;
        }

        if (!IsBladePointerArea(lanePoint))
        {
            ClearBladeIndication();
            return;
        }

        _bladeHoverPoint = lanePoint;
        _bladeHoverVerticalOffset = BodyScroll.VerticalOffset;
        ApplyBladeBodyCursor(true);

        if (!TryFindClipAtLanePoint(lanePoint, out var clip, out _)
            || ResolveBladeFrame(lanePoint.X, clip) is not { } resolved)
        {
            ShowBladeCursorIndicator(lanePoint, ContentX(lanePoint.X));
            HideBladePreviewAndSnap();
            return;
        }

        ShowBladeCursorIndicator(lanePoint, resolved.ContentX);
        ShowBladePreview(clip, resolved);
    }

    private bool IsBladePointerArea(Point lanePoint)
    {
        return _session?.ActiveTimeline.Tracks.Count > 0
            && lanePoint.Y >= 0
            && lanePoint.Y < _geo.ContentHeight;
    }

    private void ShowBladeCursorIndicator(Point lanePoint, double contentX)
    {
        var width = BladeCursorIndicator.ActualWidth > 0 ? BladeCursorIndicator.ActualWidth : BladeCursorIndicator.Width;
        var height = BladeCursorIndicator.ActualHeight > 0 ? BladeCursorIndicator.ActualHeight : BladeCursorIndicator.Height;
        Canvas.SetLeft(BladeCursorIndicator, LocalX(contentX) - width / 2);
        Canvas.SetTop(BladeCursorIndicator, lanePoint.Y - height);
        BladeCursorIndicator.Visibility = Visibility.Visible;
    }

    private bool TryFindClipAtLanePoint(Point lanePoint, out Clip clip, out int trackIndex)
    {
        clip = null!;
        trackIndex = -1;
        if (_session is null || lanePoint.Y < 0)
            return false;

        var tracks = _session.ActiveTimeline.Tracks;
        if (tracks.Count == 0) return false;

        var ti = _geo.TrackAtY(lanePoint.Y);
        if (ti < 0 || ti >= tracks.Count) return false;

        var contentX = ContentX(lanePoint.X);
        var track = tracks[ti];
        if (track.Locked) return false;
        for (var i = track.Clips.Count - 1; i >= 0; i--)
        {
            var candidate = track.Clips[i];
            var (x, y, w, h) = _geo.ClipRect(candidate, ti);
            if (contentX >= x && contentX < x + w && lanePoint.Y >= y && lanePoint.Y < y + h)
            {
                clip = candidate;
                trackIndex = ti;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Shared hover/click frame. Snap-off uses raw <see cref="FrameAt"/>. Snap-on probes only the
    /// playhead through <see cref="SnapEngine"/>; a hit is kept only if it is strictly inside the clip.
    /// Dedicated <see cref="_bladeSnapState"/> so move/trim sticky state is untouched.
    /// </summary>
    private BladeResolveResult? ResolveBladeFrame(double laneX, Clip clip)
    {
        var raw = FrameAt(laneX);
        if (!EditorEditContext.Current.SnapEnabled)
        {
            _bladeSnapState.CurrentlySnappedTo = null;
            _bladeSnapState.CurrentProbeOffset = 0;
            return InteriorBladeFrame(clip, raw, snapped: false, raw * _pixelsPerFrame);
        }

        var targets = new List<SnapEngine.SnapTarget>
        {
            new(_playheadFrame, SnapEngine.Kind.Playhead),
        };
        var snap = SnapEngine.FindSnap(
            raw,
            targets,
            _bladeSnapState,
            SnapEngine.ThresholdPixels,
            _pixelsPerFrame);
        if (snap is { } candidate
            && candidate.Frame > clip.StartFrame
            && candidate.Frame < clip.EndFrame)
        {
            return new BladeResolveResult(candidate.Frame, true, candidate.X);
        }

        _bladeSnapState.CurrentlySnappedTo = null;
        _bladeSnapState.CurrentProbeOffset = 0;
        return InteriorBladeFrame(clip, raw, snapped: false, raw * _pixelsPerFrame);
    }

    private static BladeResolveResult? InteriorBladeFrame(Clip clip, int frame, bool snapped, double contentX)
    {
        if (frame <= clip.StartFrame || frame >= clip.EndFrame)
            return null;
        return new BladeResolveResult(frame, snapped, contentX);
    }

    private void ShowBladePreview(Clip hoveredClip, BladeResolveResult resolved)
    {
        if (_session is null)
        {
            HideBladePreviewAndSnap();
            return;
        }

        var ids = ShouldExpandLinksForClip(hoveredClip.Id)
            ? LinkGroup.Expand(_session.ActiveTimeline, [hoveredClip.Id])
            : new HashSet<string>([hoveredClip.Id], StringComparer.Ordinal);
        var tracks = _session.ActiveTimeline.Tracks;
        var shown = 0;
        for (var ti = 0; ti < tracks.Count; ti++)
        {
            if (tracks[ti].Locked) continue;
            foreach (var partner in tracks[ti].Clips)
            {
                if (!ids.Contains(partner.Id)) continue;
                if (resolved.Frame <= partner.StartFrame || resolved.Frame >= partner.EndFrame)
                    continue;
                var line = BladePreviewLineAt(shown);
                var (_, y, _, h) = _geo.ClipRect(partner, ti);
                var x = LocalX(resolved.ContentX);
                line.X1 = x;
                line.X2 = x;
                line.Y1 = y;
                line.Y2 = y + h;
                line.Visibility = Visibility.Visible;
                shown++;
            }
        }

        for (var i = shown; i < _bladeLines.Count; i++)
            _bladeLines[i].Visibility = Visibility.Collapsed;

        if (resolved.SnappedToPlayhead)
            ShowSnapIndicator(resolved.ContentX);
        else
            HideSnapIndicator();
    }

    private Line BladePreviewLineAt(int index)
    {
        while (_bladeLines.Count <= index)
        {
            var line = new Line
            {
                Stroke = AppTheme.Accent.PrimaryBrush,
                StrokeThickness = AppTheme.BorderWidth.Thin,
                IsHitTestVisible = false,
            };
            Canvas.SetZIndex(line, BladePreviewZIndex);
            OverlayCanvas.Children.Add(line);
            _bladeLines.Add(line);
        }

        return _bladeLines[index];
    }

    private void HideBladePreviewAndSnap()
    {
        _bladeSnapState.CurrentlySnappedTo = null;
        _bladeSnapState.CurrentProbeOffset = 0;
        foreach (var line in _bladeLines)
            line.Visibility = Visibility.Collapsed;
        if (_dragKind == DragKind.None)
            HideSnapIndicator();
    }

    private void ClearBladeIndication()
    {
        _bladeHoverPoint = null;
        BladeCursorIndicator.Visibility = Visibility.Collapsed;
        ApplyBladeBodyCursor(false);
        HideBladePreviewAndSnap();
    }

    private static InputCursor? CreateTransparentCursor()
    {
        var bytesPerPlane = TransparentCursorSize * TransparentCursorSize / 8;
        var andMask = new byte[bytesPerPlane];
        Array.Fill(andMask, byte.MaxValue);
        var xorMask = new byte[bytesPerPlane];
        var cursorHandle = CreateCursor(
            0,
            0,
            0,
            TransparentCursorSize,
            TransparentCursorSize,
            andMask,
            xorMask);
        if (cursorHandle == 0)
            return null;

        try
        {
            return CreateInputCursor(cursorHandle);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            _ = DestroyCursor(cursorHandle);
        }
    }

    private static InputCursor? CreateInputCursor(nint cursorHandle)
    {
        const string RuntimeClass = "Microsoft.UI.Input.InputCursor";
        Marshal.ThrowExceptionForHR(WindowsCreateString(RuntimeClass, RuntimeClass.Length, out var className));
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(
                className,
                typeof(IInputCursorStaticsInterop).GUID,
                out var interop));
            Marshal.ThrowExceptionForHR(interop.CreateFromHCursor(cursorHandle, out var cursorAbi));
            if (cursorAbi == 0)
                return null;

            try
            {
                return WinRT.MarshalInspectable<InputCursor>.FromAbi(cursorAbi);
            }
            finally
            {
                WinRT.MarshalInspectable<InputCursor>.DisposeAbi(cursorAbi);
            }
        }
        finally
        {
            _ = WindowsDeleteString(className);
        }
    }

    [ComImport]
    [Guid("AC6F5065-90C4-46CE-BEB7-05E138E54117")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInputCursorStaticsInterop
    {
        [PreserveSig]
        int GetIids(out uint iidCount, out nint iids);

        [PreserveSig]
        int GetRuntimeClassName(out nint className);

        [PreserveSig]
        int GetTrustLevel(out int trustLevel);

        [PreserveSig]
        int CreateFromHCursor(nint cursor, out nint inputCursor);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateCursor(
        nint instance,
        int hotSpotX,
        int hotSpotY,
        int width,
        int height,
        [In] byte[] andPlane,
        [In] byte[] xorPlane);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(nint cursor);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(
        nint activatableClassId,
        [MarshalAs(UnmanagedType.LPStruct)] Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IInputCursorStaticsInterop factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(
        string sourceString,
        int length,
        out nint @string);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint @string);

    private void ShowSnapIndicator(double contentX)
    {
        // Lane geometry uses headerWidth 0; X is already lane-local (frame * ppf).
        _snapLine ??= new Line
        {
            Stroke = new SolidColorBrush(Color.FromArgb(255, 255, 214, 10)), // systemYellow-ish
            StrokeThickness = AppTheme.BorderWidth.Thin,
            StrokeDashArray = new DoubleCollection { 4, 4 },
            IsHitTestVisible = false,
        };
        if (!OverlayCanvas.Children.Contains(_snapLine))
            OverlayCanvas.Children.Add(_snapLine);

        var height = Math.Max(OverlayCanvas.ActualHeight, _geo.ContentHeight);
        var x = LocalX(contentX);
        _snapLine.X1 = x;
        _snapLine.X2 = x;
        _snapLine.Y1 = _geo.RulerHeight;
        _snapLine.Y2 = height;
        _snapLine.Visibility = Visibility.Visible;
    }

    private void HideSnapIndicator()
    {
        if (_snapLine is not null)
            _snapLine.Visibility = Visibility.Collapsed;
    }

    private void DrawMoveGhosts()
    {
        ClearDragGhosts();
        var primary = _dragSnapshots.FirstOrDefault(s => s.ClipId == _dragClipId)
            ?? _dragSnapshots.FirstOrDefault();
        Dictionary<string, EditCommands.NewTrackDragAssignment>? newTrackPlan = null;

        var liveLeft = BodyScroll.HorizontalOffset;
        var liveWidth = BodyScroll.ViewportWidth > 1 ? BodyScroll.ViewportWidth : _viewWidth;
        if (_dragNewTrackInsertIndex is int insertAt && _session is not null && primary is not null)
        {
            var items = _dragSnapshots
                .Select(s => new EditCommands.NewTrackDragItem(
                    s.ClipId, s.TrackIndex, s.MediaType, s.StartFrame + _dragFrameDelta))
                .ToArray();
            newTrackPlan = EditCommands.PlanMoveToNewTrack(
                    _session.ActiveTimeline, items, primary.TrackIndex, _dragNewTrackType, insertAt)
                .ToDictionary(a => a.ClipId, StringComparer.Ordinal);

            var lineY = insertAt <= 0
                ? Math.Max(_geo.TrackYAt(0), BodyScroll.VerticalOffset)
                : Math.Max(0, _geo.ContentHeight - AppTheme.BorderWidth.Thick);
            _newTrackIndicator ??= new Border
            {
                Height = AppTheme.BorderWidth.Thick,
                Background = AppTheme.Accent.BrandBrush,
                IsHitTestVisible = false,
            };
            if (!OverlayCanvas.Children.Contains(_newTrackIndicator))
                OverlayCanvas.Children.Add(_newTrackIndicator);
            _newTrackIndicator.Width = Math.Max(2, liveWidth);
            Canvas.SetLeft(_newTrackIndicator, LocalX(liveLeft));
            Canvas.SetTop(_newTrackIndicator, lineY);
            _newTrackIndicator.Visibility = Visibility.Visible;
        }

        foreach (var snapshot in _dragSnapshots)
        {
            EditCommands.NewTrackDragAssignment planned = default;
            var hasPlanned = newTrackPlan is not null && newTrackPlan.TryGetValue(snapshot.ClipId, out planned);
            var contentX = (snapshot.StartFrame + _dragFrameDelta) * _pixelsPerFrame;
            var contentW = Math.Max(2, snapshot.DurationFrames * _pixelsPerFrame);
            var vis = TimelineGeometry.IntersectHorizontal(contentX, contentW, liveLeft, liveLeft + liveWidth);
            if (vis is null) continue;

            double ghostY;
            double ghostH;
            if (hasPlanned && planned.LandsOnNewTrack && _dragNewTrackInsertIndex is int insertPos)
            {
                const double previewHeight = 14;
                ghostH = previewHeight;
                ghostY = insertPos <= 0
                    ? Math.Max(_geo.TrackYAt(0), BodyScroll.VerticalOffset)
                    : Math.Max(0, _geo.ContentHeight - previewHeight);
            }
            else
            {
                var trackIndex = hasPlanned
                    ? planned.PreInsertTrackIndex
                    : snapshot.TrackIndex + _dragTrackOffset;
                ghostY = _geo.TrackYAt(trackIndex) + TimelineGeometry.ClipVerticalInset;
                ghostH = Math.Max(4, _geo.TrackHeightAt(trackIndex) - TimelineGeometry.ClipVerticalInset * 2);
            }

            var ghost = new Border
            {
                Width = Math.Max(2, vis.Value.Width),
                Height = ghostH,
                Background = new SolidColorBrush(Color.FromArgb(64, AppTheme.Accent.Brand.R, AppTheme.Accent.Brand.G, AppTheme.Accent.Brand.B)),
                BorderBrush = AppTheme.Accent.PrimaryBrush,
                BorderThickness = new Thickness(AppTheme.BorderWidth.Thin),
                CornerRadius = new CornerRadius(AppTheme.Radius.XsSm),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(ghost, LocalX(vis.Value.X));
            Canvas.SetTop(ghost, ghostY);
            OverlayCanvas.Children.Add(ghost);
            _dragGhosts.Add(ghost);
        }
    }

    private void ClearDragGhosts()
    {
        foreach (var ghost in _dragGhosts)
            OverlayCanvas.Children.Remove(ghost);
        _dragGhosts.Clear();
        if (_newTrackIndicator is not null)
        {
            OverlayCanvas.Children.Remove(_newTrackIndicator);
            _newTrackIndicator.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyTrimPreview()
    {
        foreach (var snapshot in _dragSnapshots)
        {
            var clip = FindClip(snapshot.ClipId);
            if (clip is null) continue;

            if (_dragKind == DragKind.TrimLeft)
            {
                var newStart = Math.Clamp(
                    snapshot.StartFrame + _dragFrameDelta,
                    0,
                    snapshot.StartFrame + snapshot.DurationFrames - 1);
                var delta = newStart - snapshot.StartFrame;
                clip.StartFrame = newStart;
                clip.DurationFrames = Math.Max(1, snapshot.DurationFrames - delta);
                clip.TrimStartFrame = Math.Max(0,
                    snapshot.TrimStartFrame + (int)Math.Round(delta * snapshot.Speed));
            }
            else
            {
                var newEnd = Math.Max(
                    snapshot.StartFrame + 1,
                    snapshot.StartFrame + snapshot.DurationFrames + _dragFrameDelta);
                clip.StartFrame = snapshot.StartFrame;
                clip.DurationFrames = Math.Max(1, newEnd - snapshot.StartFrame);
                clip.TrimStartFrame = snapshot.TrimStartFrame;
            }
        }
        RedrawTrimTracks();
    }

    private void RestoreTrimPreview()
    {
        foreach (var snapshot in _dragSnapshots)
        {
            var clip = FindClip(snapshot.ClipId);
            if (clip is null) continue;
            clip.StartFrame = snapshot.StartFrame;
            clip.DurationFrames = snapshot.DurationFrames;
            clip.TrimStartFrame = snapshot.TrimStartFrame;
        }
    }

    private void ResetDragState()
    {
        StopTrimRendering();
        _trimVisualApplied = false;
        _dragKind = DragKind.None;
        _dragClipId = null;
        _dragSnapshots = [];
        _dragFrameDelta = 0;
        _dragTrackOffset = 0;
        _dragNewTrackInsertIndex = null;
        _snapState.CurrentlySnappedTo = null;
        ClearDragGhosts();
        HideSnapIndicator();
    }
}
