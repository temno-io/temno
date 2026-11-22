using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Temno.App.Audio;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Captions;
using Temno.Core.Editing;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Preview;
using Temno.Core.Session;
using Temno.Core.Ui;
using Temno.Media;
using Temno.Media.Cache;
using Temno.Media.Audio;
using Temno.Media.Playback;
using Windows.Foundation;
using Windows.Media.Playback;
using NativeMediaSource = Windows.Media.Core.MediaSource;

using Temno.App.ViewModels;

namespace Temno.App.Controls;

public sealed partial class PreviewHostControl : UserControl
{
    // ---------------------------------------------------------------- caption preview overlay
    /// <summary>
    /// Shows the Captions tab's live sample over the real preview canvas (Mac's `Preview` toggle).
    /// The overlay is rendered at the project's canvas size and stretched uniformly exactly like
    /// <c>PreviewImage</c>, so its normalized placement lines up with the video regardless of
    /// letterboxing. It is a preview only: it is never a clip and never survives a Generate.
    /// </summary>
    public void ShowCaptionOverlay(
        string content,
        TextStyle style,
        Temno.Core.Models.Transform transform,
        TextAnimation? animation,
        int canvasWidth,
        int canvasHeight)
    {
        var overlay = _captionOverlay;
        if (overlay is null)
        {
            overlay = new CaptionPreviewImage
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false,
            };
            overlay.PointerPressed += CaptionOverlay_PointerPressed;
            overlay.PointerMoved += CaptionOverlay_PointerMoved;
            overlay.PointerReleased += CaptionOverlay_PointerReleased;
            overlay.PointerCanceled += CaptionOverlay_PointerCanceled;
            overlay.PointerCaptureLost += CaptionOverlay_PointerCaptureLost;
            overlay.Loaded += (_, _) => UpdateCaptionOverlayAutomation();
            ToolTipService.SetToolTip(overlay, "Drag to position the sample caption");
            AutomationProperties.SetName(overlay, "Caption sample overlay");
            Canvas.SetZIndex(overlay, 1);
            _captionOverlay = overlay;
            PreviewCanvasGrid.Children.Add(overlay);
        }

        _captionOverlayTransform = transform;
        _captionCanvasWidth = Math.Max(1, canvasWidth);
        _captionCanvasHeight = Math.Max(1, canvasHeight);

        // CSS / canvas pixels. CaptionPreviewImage multiplies by RasterizationScale so a 150 %
        // display does not upscale a 1× bitmap.
        overlay.RenderPixelWidth = Math.Clamp(canvasWidth, 16, 4096);
        overlay.RenderPixelHeight = Math.Clamp(canvasHeight, 16, 4096);
        overlay.Configure(content, style, transform, animation);
        overlay.Animate = animation is { } a && a.Preset is not (null or "" or "none" or "off");
        overlay.Visibility = Visibility.Visible;
        overlay.IsHitTestVisible = true;
        UpdateCaptionOverlayAutomation();

        ShowCaptionGuides(transform);
    }

    public void HideCaptionOverlay()
    {
        EndCaptionOverlayDrag(commit: false);
        if (_captionOverlay is { } overlay)
        {
            overlay.Animate = false;
            overlay.IsHitTestVisible = false;
            overlay.Visibility = Visibility.Collapsed;
        }
        if (_captionGuides is { } guides)
            guides.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Mac's centre guides: hairlines while X or Y is snapped to centre
    /// (<c>AppTheme.Caption.centerSnapValue</c>), so the snap is visible rather than merely felt.
    /// </summary>
    private void ShowCaptionGuides(Temno.Core.Models.Transform transform)
    {
        const double centre = CaptionOverlaySnap.Center;
        const double epsilon = 0.0005;
        var onX = Math.Abs(transform.CenterX - centre) < epsilon;
        var onY = Math.Abs(transform.CenterY - centre) < epsilon;

        if (_captionGuides is null)
        {
            if (!onX && !onY) return;
            _captionGuides = new Border { IsHitTestVisible = false };
            Canvas.SetZIndex(_captionGuides, 2);
            PreviewCanvasGrid.Children.Add(_captionGuides);
        }

        if (!onX && !onY)
        {
            _captionGuides.Visibility = Visibility.Collapsed;
            return;
        }

        var host = new Grid();
        if (onX)
        {
            host.Children.Add(new Border
            {
                Width = AppTheme.BorderWidth.Hairline,
                Background = AppTheme.Accent.TimecodeBrush,
                Opacity = AppTheme.Opacity.Prominent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
            });
        }
        if (onY)
        {
            host.Children.Add(new Border
            {
                Height = AppTheme.BorderWidth.Hairline,
                Background = AppTheme.Accent.TimecodeBrush,
                Opacity = AppTheme.Opacity.Prominent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        _captionGuides.Child = host;
        _captionGuides.Visibility = Visibility.Visible;
    }

    private void CaptionOverlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_captionOverlay is not { IsHitTestVisible: true, Visibility: Visibility.Visible } overlay)
            return;
        var point = e.GetCurrentPoint(overlay);
        if (!point.Properties.IsLeftButtonPressed) return;

        _captionOverlayDragging = true;
        _captionDragStart = point.Position;
        _captionDragOriginX = _captionOverlayTransform?.CenterX ?? 0.5;
        _captionDragOriginY = _captionOverlayTransform?.CenterY ?? 0.9;
        overlay.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void CaptionOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_captionOverlayDragging || _captionOverlay is null) return;
        ApplyCaptionOverlayDrag(e.GetCurrentPoint(_captionOverlay).Position, isFinal: false);
        e.Handled = true;
    }

    private void CaptionOverlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_captionOverlayDragging || _captionOverlay is null) return;
        ApplyCaptionOverlayDrag(e.GetCurrentPoint(_captionOverlay).Position, isFinal: true);
        EndCaptionOverlayDrag(commit: false);
        e.Handled = true;
    }

    private void CaptionOverlay_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (!_captionOverlayDragging) return;
        CaptionOverlayPositionChanged?.Invoke(
            this,
            new CaptionOverlayPositionEventArgs(
                _captionOverlayTransform?.CenterX ?? 0.5,
                _captionOverlayTransform?.CenterY ?? 0.9,
                isFinal: true));
        EndCaptionOverlayDrag(commit: false);
        e.Handled = true;
    }

    private void CaptionOverlay_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_captionOverlayDragging) return;
        CaptionOverlayPositionChanged?.Invoke(
            this,
            new CaptionOverlayPositionEventArgs(
                _captionOverlayTransform?.CenterX ?? 0.5,
                _captionOverlayTransform?.CenterY ?? 0.9,
                isFinal: true));
        EndCaptionOverlayDrag(commit: false);
    }

    private void ApplyCaptionOverlayDrag(Windows.Foundation.Point position, bool isFinal)
    {
        if (_captionOverlay is null || _captionOverlayTransform is null) return;

        var (contentW, contentH, _, _) = CaptionOverlayContentRect();
        if (contentW <= 0 || contentH <= 0) return;

        var nx = CaptionOverlaySnap.Snap(Math.Clamp(
            _captionDragOriginX + (position.X - _captionDragStart.X) / contentW, 0, 1));
        var ny = CaptionOverlaySnap.Snap(Math.Clamp(
            _captionDragOriginY + (position.Y - _captionDragStart.Y) / contentH, 0, 1));

        if (!isFinal
            && Math.Abs(nx - _captionOverlayTransform.CenterX) < 1e-9
            && Math.Abs(ny - _captionOverlayTransform.CenterY) < 1e-9)
            return;

        _captionOverlayTransform.CenterX = nx;
        _captionOverlayTransform.CenterY = ny;
        _captionOverlay.Refresh();
        ShowCaptionGuides(_captionOverlayTransform);
        CaptionOverlayPositionChanged?.Invoke(this, new CaptionOverlayPositionEventArgs(nx, ny, isFinal));
    }

    // --- WP-145: preview guides -------------------------------------------------------------

    // Loaded at construction, not in OnLoaded. WP-144 shipped that bug: state loaded in OnLoaded
    // arrived after the surface was built and silently never applied.
    private PreviewGuideState _guides = PreviewGuideStore.Load();

    /// <summary>Current guide selection. Setting it persists and redraws.</summary>
    internal PreviewGuideState Guides
    {
        get => _guides;
        private set
        {
            _guides = value;
            PreviewGuideStore.Save(value);
            RedrawGuides();
        }
    }
}
