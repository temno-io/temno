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
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Captions;
using Temno.Core.Editing;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Preview;
using Temno.Core.Project;
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
    private void TransportBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        var compact = width < PreviewPictureBudget.CompactTransportBreakpoint;
        CompactTransportRow.Height = new GridLength(compact ? PreviewPictureBudget.CompactTransportExtraHeight : 0);
        Grid.SetRow(TransportButtons, compact ? 1 : 0);
        Grid.SetColumn(TransportButtons, compact ? 0 : 1);
        Grid.SetColumnSpan(TransportButtons, compact ? 3 : 1);
        TransportButtons.HorizontalAlignment = HorizontalAlignment.Center;

        // Lose status text first, then shorten the resolution pill, then hide it on very narrow panes
        // (Preview options "..." always keeps the Frame Preview submenu reachable).
        var showResolution = compact ? width >= 370 : width >= 540;
        _compactPreviewResolutionLabel = compact ? width < 460 : width < 660;
        _showStatusLabel = !compact && width >= 780;

        BtnPreviewResolution.Visibility = showResolution ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreviewResolutionLabel();
        RefreshStatusLabel();

        var rightColumn = compact
            ? Math.Max(72, width - 20 - 176)
            : Math.Max(72, (width - 170) / 2);
        var resolutionWidth = !showResolution ? 0 : (_compactPreviewResolutionLabel ? 88 : 164);
        var availableForStatus = Math.Max(0, rightColumn - 80 - resolutionWidth);
        StatusLabel.MaxWidth = Math.Clamp(availableForStatus, 0, 180);
        // The audio warning keeps its full explanation on hover without pushing capture/guides out.
        AudioStatusLabel.MaxWidth = Math.Clamp(availableForStatus, 0, 190);
    }

    private async void CaptureFrame_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = await CaptureDisplayedFrameAsync().ConfigureAwait(true);
            if (file is null)
            {
                SetStatusText("No frame to capture");
                return;
            }

            FrameCaptured?.Invoke(this, file);
            SetStatusText("Frame captured");
        }
        catch (Exception ex)
        {
            SetStatusText("Capture failed");
            System.Diagnostics.Debug.WriteLine($"WP-145 capture failed: {ex}");
        }
    }

    private async Task<string?> CaptureDisplayedFrameAsync()
    {
        if (_tabs.IsTimeline && _session is not null && _provider is not null)
        {
            var video = await _provider.GetCompositedFrameAsync(
                _session.ActiveTimeline, PlayheadFrame).ConfigureAwait(true);
            if (video is not null)
            {
                using (video)
                    return await WriteCapturedPngAsync(video).ConfigureAwait(true);
            }
        }

        if (!_tabs.IsTimeline && _session is not null && _tabs.Active.MediaRef is { } mediaRef)
        {
            var path = _session.Resolver.ResolvePath(mediaRef);
            var type = _tabs.Active.MediaType ?? ClipType.video;
            if (type is ClipType.video && !string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var seconds = CaptionImporter.SecondsFromFrame(PlayheadFrame, MonitorFps);
                using var decoded = await _playbackFrames.GetFrameAsync(path, seconds).ConfigureAwait(true);
                if (decoded is not null)
                    return await WriteCapturedPngAsync(decoded).ConfigureAwait(true);
            }
        }

        if (PreviewImage?.Source is not null && PreviewImage.Visibility == Visibility.Visible)
            return await RenderPreviewImagePngAsync().ConfigureAwait(true);

        return null;
    }

    public async Task EnsureProjectThumbnailAsync()
    {
        var session = _session;
        if (session is null || session.IsDirty || session.PackagePath is not { Length: > 0 } package || _provider is null)
            return;
        var destination = Path.Combine(package, Temno.Core.Project.ProjectConstants.ThumbnailFilename);
        if (File.Exists(destination)) return;
        var temporary = destination + ".tmp";
        try
        {
            var posterFrame = Math.Clamp(_timelinePlayhead, 0, Math.Max(0, session.ActiveTimeline.TotalFrames - 1));
            using var frame = await _provider.GetCompositedFrameAsync(session.ActiveTimeline, posterFrame);
            if (frame is null || !ReferenceEquals(session, _session)) return;
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, (uint)frame.Width, (uint)frame.Height,
                96, 96, EncodeDisplayBgra(frame));
            var width = (uint)Math.Min(640, frame.Width);
            encoder.BitmapTransform.ScaledWidth = width;
            encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, frame.Height * width / frame.Width);
            await encoder.FlushAsync();
            stream.Seek(0);
            await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                await stream.AsStreamForRead().CopyToAsync(output);
            File.Move(temporary, destination, overwrite: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Project thumbnail unavailable: {ex.Message}");
            try { File.Delete(temporary); } catch { }
        }
    }

    private static async Task<string> WriteCapturedPngAsync(VideoFrame frame)
    {
        var png = await FfmpegPngEncoder.EncodeAsync(frame).ConfigureAwait(true);
        var dir = Path.Combine(
            UserDataRoots.LocalAppDataRoot,
            "captures");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"frame-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        await File.WriteAllBytesAsync(file, png).ConfigureAwait(true);
        return file;
    }

    private async Task<string> RenderPreviewImagePngAsync()
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(PreviewImage);
        var pixels = await rtb.GetPixelsAsync();
        var dir = Path.Combine(
            UserDataRoots.LocalAppDataRoot,
            "captures");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"frame-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
        using (var stream = File.Create(file))
        {
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96,
                pixels.ToArray());
            await encoder.FlushAsync();
        }
        return file;
    }

    private static SolidColorBrush GuideBrush(byte alpha) =>
        new(Windows.UI.Color.FromArgb(alpha, 255, 255, 255));

    private void AddGuideLine(double x1, double y1, double x2, double y2, byte alpha, double thickness = 1)
        => GuideOverlay.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            Stroke = GuideBrush(alpha),
            StrokeThickness = thickness,
        });

    private void AddGuideRect(double x, double y, double w, double h, byte alpha, double[]? dash = null)
    {
        var rect = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = Math.Max(0, w), Height = Math.Max(0, h),
            Stroke = GuideBrush(alpha), StrokeThickness = 1,
            Fill = null,
        };
        if (dash is not null)
            foreach (var d in dash) rect.StrokeDashArray.Add(d);
        Microsoft.UI.Xaml.Controls.Canvas.SetLeft(rect, x);
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(rect, y);
        GuideOverlay.Children.Add(rect);
    }

    /// <summary>
    /// Redraws every guide against the <b>letterboxed frame rectangle</b>, not the panel bounds.
    /// A safe zone measured against the panel would be wrong on any canvas whose aspect differs
    /// from the preview pane, which is most of them.
    /// </summary>
    private void RedrawGuides()
    {
        if (GuideOverlay is null) return;
        GuideOverlay.Children.Clear();
        if (!_guides.AnyVisible)
        {
            AutomationProperties.SetHelpText(GuideOverlay, "Guides hidden. Not included in export or capture.");
            return;
        }

        // Measure against PreviewCanvasGrid, not GuideOverlay: a Canvas measures to (0,0) and its
        // ActualWidth cannot be relied on for layout maths.
        var (canvasW, canvasH) = GuideCanvasSize();
        var (w, h, ox, oy) = LetterboxRect(
            PreviewCanvasGrid.ActualWidth,
            PreviewCanvasGrid.ActualHeight,
            (double)canvasW / canvasH);
        if (w <= 0 || h <= 0) return;

        if (_guides.Grid != PreviewGridMode.None)
        {
            var n = (int)_guides.Grid;
            for (var i = 1; i < n; i++)
            {
                var fx = ox + w * i / n;
                var fy = oy + h * i / n;
                AddGuideLine(fx, oy, fx, oy + h, 70);
                AddGuideLine(ox, fy, ox + w, fy, 70);
            }
        }

        if (_guides.SafeZones.HasFlag(PreviewSafeZones.Action))
        {
            var i = PreviewSafeZones.Action.Inset();
            AddGuideRect(ox + w * i, oy + h * i, w * (1 - 2 * i), h * (1 - 2 * i), 110);
        }

        if (_guides.SafeZones.HasFlag(PreviewSafeZones.Title))
        {
            var i = PreviewSafeZones.Title.Inset();
            AddGuideRect(ox + w * i, oy + h * i, w * (1 - 2 * i), h * (1 - 2 * i), 110, [4, 4]);
        }

        if (_guides.SafeZones.HasFlag(PreviewSafeZones.Center))
        {
            var cx = ox + w / 2;
            var cy = oy + h / 2;
            var arm = Math.Min(w, h) * 0.04;
            AddGuideLine(cx - arm, cy, cx + arm, cy, 150);
            AddGuideLine(cx, cy - arm, cx, cy + arm, 150);
        }

        if (_guides.Format.AspectRatio() is { } target)
        {
            // The reference crop is the largest rect of the target aspect that fits the frame.
            var (fw, fh, fx2, fy2) = LetterboxRect(w, h, target);
            AddGuideRect(ox + fx2, oy + fy2, fw, fh, 190, [6, 4]);
        }

        var shapeCount = GuideOverlay.Children.Count;
        AutomationProperties.SetHelpText(
            GuideOverlay,
            $"{shapeCount} overlay shape{(shapeCount == 1 ? "" : "s")}. Not included in export or capture.");
    }

    private (int Width, int Height) GuideCanvasSize()
    {
        var tl = _session?.ActiveTimeline;
        if (tl is { Width: > 0, Height: > 0 })
            return (tl.Width, tl.Height);
        return (Math.Max(1, _captionCanvasWidth), Math.Max(1, _captionCanvasHeight));
    }

    /// <summary>
    /// Where a Uniform-stretched canvas of <paramref name="canvasAspect"/> actually lands inside a
    /// view of <paramref name="viewW"/> x <paramref name="viewH"/>, letterboxing included.
    /// </summary>
    /// <remarks>
    /// WP-145 extracted this from <see cref="CaptionOverlayContentRect"/> rather than writing a
    /// second copy for the guides. Two overlays computing the same rectangle independently is how
    /// surfaces start disagreeing, which this session has already paid for twice.
    /// </remarks>
    internal static (double Width, double Height, double OffsetX, double OffsetY) LetterboxRect(
        double viewW, double viewH, double canvasAspect)
    {
        if (viewW <= 0 || viewH <= 0 || canvasAspect <= 0) return (0, 0, 0, 0);
        if (viewW / viewH > canvasAspect)
        {
            var contentW = viewH * canvasAspect;
            return (contentW, viewH, (viewW - contentW) / 2.0, 0);
        }
        var contentH = viewW / canvasAspect;
        return (viewW, contentH, 0, (viewH - contentH) / 2.0);
    }

    private (double Width, double Height, double OffsetX, double OffsetY) CaptionOverlayContentRect()
    {
        var overlay = _captionOverlay;
        if (overlay is null) return (0, 0, 0, 0);
        return LetterboxRect(
            overlay.ActualWidth,
            overlay.ActualHeight,
            (double)_captionCanvasWidth / _captionCanvasHeight);
    }

    private void EndCaptionOverlayDrag(bool commit)
    {
        if (!_captionOverlayDragging) return;
        if (commit && _captionOverlayTransform is not null)
        {
            CaptionOverlayPositionChanged?.Invoke(
                this,
                new CaptionOverlayPositionEventArgs(
                    _captionOverlayTransform.CenterX,
                    _captionOverlayTransform.CenterY,
                    isFinal: true));
        }
        _captionOverlayDragging = false;
        _captionOverlay?.ReleasePointerCaptures();
    }

    private void UpdateCaptionOverlayAutomation()
    {
        if (_captionOverlay is not { } overlay) return;
        AutomationProperties.SetName(overlay, "Caption sample overlay");
        AutomationProperties.SetHelpText(
            overlay,
            $"render={overlay.EffectiveRenderWidth}x{overlay.EffectiveRenderHeight} scale={overlay.CurrentRasterizationScale:0.###}");
    }

    /// <summary>Mac <c>AppTheme.Caption.centerSnapValue</c> / <c>centerSnapThreshold</c>.</summary>
    private static class CaptionOverlaySnap
    {
        public const double Center = 0.5;
        public const double Threshold = 0.02;

        public static double Snap(double value)
            => Math.Abs(value - Center) < Threshold ? Center : value;
    }

    /// <summary>
    /// Writes into the cached <see cref="WriteableBitmap"/> when the size is unchanged. A new
    /// bitmap per tick allocated a full-frame native pixel buffer that the managed heap barely
    /// accounts for, so preview playback grew process memory without GC pressure.
    /// </summary>
    private WriteableBitmap BgraToBitmap(byte[] bgra, int byteLength, int width, int height)
    {
        var bitmap = _displayBitmap;
        if (bitmap is null || bitmap.PixelWidth != width || bitmap.PixelHeight != height)
        {
            bitmap = new WriteableBitmap(width, height);
            _displayBitmap = bitmap;
        }

        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(bgra, 0, Math.Min(byteLength, (int)bitmap.PixelBuffer.Capacity));
        bitmap.Invalidate();
        return bitmap;
    }

    private void UpdateLabels(int frame)
    {
        var fps = MonitorFps;
        var total = MonitorDurationFrames;
        PlayheadText.Text = TimelineGeometry.FormatTimecodeFull(frame, fps); // WP-184: HH:MM:SS:FF always
        DurationText.Text = TimelineGeometry.FormatTimecodeFull(total, fps);
        UpdateScrubFill(frame, total);
        BtnPlay.IsEnabled = CanPlayActiveTab();
    }

    private void UpdateScrubFill(int frame, int total)
    {
        var trackWidth = Math.Max(0, ScrubBar.ActualWidth - ScrubBar.Padding.Left - ScrubBar.Padding.Right);
        var t = total <= 0 ? 0 : Math.Clamp(frame / (double)Math.Max(1, total), 0, 1);
        ScrubFill.Width = trackWidth * t;
    }

    private void RebuildTabStrip()
    {
        PreviewTabStrip.Children.Clear();
        foreach (var tab in _tabs.Tabs)
        {
            var active = tab.Id == _tabs.ActiveId;
            var underline = TabUnderline(tab);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = AppTheme.Spacing.None, VerticalAlignment = VerticalAlignment.Stretch };

            var title = new Button
            {
                Content = tab.Title,
                Tag = tab.Id,
                AllowFocusOnInteraction = false,
                IsTabStop = false,
                // WP-197: conditional close-button inset, computed at runtime (DESIGN.md §1 exception 1).
                Padding = new Thickness(AppTheme.Spacing.SmMd, AppTheme.Spacing.None, tab.IsCloseable ? AppTheme.Spacing.Xxs : AppTheme.Spacing.SmMd, AppTheme.Spacing.None),
                MinHeight = 24,
                Background = active ? AppTheme.Background.ProminentBrush : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0, 0, 0, active ? 2 : 0),
                BorderBrush = underline,
                Foreground = active ? AppTheme.Text.PrimaryBrush : AppTheme.Text.SecondaryBrush,
                FontSize = AppTheme.FontSize.Sm,
            };
            AutomationProperties.SetName(title, tab.Title);
            title.Click += PreviewTab_Click;
            title.AddHandler(UIElement.PreviewKeyDownEvent, (KeyEventHandler)Chrome_PreviewKeyDown, handledEventsToo: false);
            row.Children.Add(title);

            if (tab.IsCloseable)
            {
                var close = new Button
                {
                    Content = Temno.App.Controls.Shared.FluentPath.At(Temno.App.Controls.Shared.Icons.Dismiss, AppTheme.IconSize.Xs, AppTheme.Text.TertiaryBrush),
                    Tag = tab.Id,
                    AllowFocusOnInteraction = false,
                    IsTabStop = false,
                    Width = 20,
                    MinHeight = 24,
                    Padding = AppTheme.Padding.None,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0, 0, 0, active ? 2 : 0),
                    BorderBrush = underline,
                    Foreground = AppTheme.Text.TertiaryBrush,
                    FontSize = AppTheme.FontSize.SmMd,
                };
                ToolTipService.SetToolTip(close, "Close tab");
                AutomationProperties.SetName(close, "Close " + tab.Title);
                close.Click += PreviewTabClose_Click;
                close.AddHandler(UIElement.PreviewKeyDownEvent, (KeyEventHandler)Chrome_PreviewKeyDown, handledEventsToo: false);
                row.Children.Add(close);
            }

            PreviewTabStrip.Children.Add(row);
        }

        BtnTabBack.IsEnabled = _tabs.CanGoBack;
        BtnTabForward.IsEnabled = _tabs.CanGoForward;
        BtnPlay.IsEnabled = CanPlayActiveTab();
        BtnCloseAllTabs.IsEnabled = true;
        ToolTipService.SetToolTip(BtnCloseAllTabs, "Preview options");
    }

    private static Brush TabUnderline(PreviewMonitorTab tab)
    {
        if (tab.Kind == PreviewMonitorKind.Timeline)
            return AppTheme.Accent.PrimaryBrush;
        return tab.MediaType switch
        {
            ClipType.audio => AppTheme.TrackColor.AudioBrush,
            ClipType.image => AppTheme.TrackColor.ImageBrush,
            ClipType.lottie => AppTheme.TrackColor.LottieBrush,
            ClipType.sequence => AppTheme.TrackColor.SequenceBrush,
            ClipType.text => AppTheme.TrackColor.TextBrush,
            _ => AppTheme.TrackColor.VideoBrush,
        };
    }

    private void Chrome_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled || e.Key != Windows.System.VirtualKey.Space) return;
        if (e.KeyStatus.WasKeyDown && e.KeyStatus.RepeatCount > 1)
        {
            e.Handled = true;
            return;
        }

        TogglePlay();
        e.Handled = true;
    }

    private void PreviewTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        if (_tabs.ActiveId == id) return;
        if (IsPlaying) PauseTransport();
        RememberPlayhead();
        if (_tabs.Select(id))
            ApplyActiveMonitor();
    }

    private void PreviewTabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var closingActive = _tabs.ActiveId == id;
        if (closingActive && IsPlaying)
            PauseTransport();
        if (closingActive)
            RememberPlayhead();
        if (_tabs.Close(id))
            ApplyActiveMonitor();
        else
            RebuildTabStrip();
    }

    private void TabBack_Click(object sender, RoutedEventArgs e)
    {
        if (!_tabs.CanGoBack) return;
        if (IsPlaying) PauseTransport();
        RememberPlayhead();
        if (_tabs.Back())
            ApplyActiveMonitor();
    }

    private void TabForward_Click(object sender, RoutedEventArgs e)
    {
        if (!_tabs.CanGoForward) return;
        if (IsPlaying) PauseTransport();
        RememberPlayhead();
        if (_tabs.Forward())
            ApplyActiveMonitor();
    }

    private void PreviewMore_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        var canvasItem = new MenuFlyoutItem
        {
            Text = "Canvas Size…",
            IsEnabled = _session is not null,
        };
        canvasItem.Click += (_, _) => _ = ShowCustomCanvasSizeDialogAsync();
        flyout.Items.Add(canvasItem);

        var previewSub = new MenuFlyoutSubItem { Text = "Frame Preview" };
        PopulatePreviewResolutionMenuItems(previewSub.Items);
        flyout.Items.Add(previewSub);
        flyout.Items.Add(new MenuFlyoutSeparator());

        var closeTabs = new MenuFlyoutItem
        {
            Text = "Close All Source Tabs",
            IsEnabled = _tabs.HasSourceTabs,
        };
        closeTabs.Click += (_, _) => CloseAllSourceTabs();
        flyout.Items.Add(closeTabs);
        WindowPopout.ShowMenuAt(flyout, BtnCloseAllTabs, PopoutSide.Bottom, PopoutAlignment.Right);
    }

    private void PreviewResolution_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        PopulatePreviewResolutionMenuItems(flyout.Items);
        WindowPopout.ShowMenuAt(flyout, BtnPreviewResolution, PopoutSide.Top, PopoutAlignment.Right);
    }

    private void PopulatePreviewResolutionMenuItems(IList<MenuFlyoutItemBase> items)
    {
        var (canvasW, canvasH) = ActiveMonitorNativeSize();
        foreach (var preset in PreviewResolution.Presets)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = preset.MenuLabel(canvasW, canvasH),
                IsChecked = !_previewResolution.IsCustom && _previewResolution == preset,
            };
            var chosen = preset;
            item.Click += (_, _) => ApplyPreviewResolution(chosen);
            items.Add(item);
        }

        items.Add(new MenuFlyoutSeparator());
        var customItem = new ToggleMenuFlyoutItem
        {
            Text = _previewResolution.CustomMenuLabel(canvasW, canvasH),
            IsChecked = _previewResolution.IsCustom,
        };
        customItem.Click += (_, _) => _ = ShowCustomPreviewResolutionDialogAsync();
        items.Add(customItem);
    }

    private (int? Width, int? Height) ActiveMonitorNativeSize()
    {
        if (_tabs.IsTimeline)
        {
            var tl = _session?.ActiveTimeline;
            return tl is { Width: > 0, Height: > 0 } ? (tl.Width, tl.Height) : (null, null);
        }

        if (_tabs.Active.MediaRef is { } mediaRef
            && _session?.Resolver.Entry(mediaRef) is { SourceWidth: > 0 and var sw, SourceHeight: > 0 and var sh })
        {
            return (sw, sh);
        }

        return (null, null);
    }

    private int EffectiveDecodeMaxDimension()
    {
        if (_tabs.IsTimeline)
        {
            var tl = _session?.ActiveTimeline;
            return _previewResolution.ResolveDecodeMaxDimension(tl?.Width, tl?.Height);
        }

        return _previewResolution.ResolveDecodeMaxDimension(null, null);
    }

    private void SyncPreviewResolutionPipeline()
    {
        var decodeMax = EffectiveDecodeMaxDimension();
        var proxyChanged = _proxies is not null && _proxies.PreviewMaxDimension != decodeMax;
        if (_proxies is not null)
            _proxies.PreviewMaxDimension = decodeMax;
        if (proxyChanged)
        {
            _proxyStatus = null;
            RefreshStatusLabel();
        }

        _playbackFrames.MaxDimension = decodeMax;
        if (_provider is not null)
            _provider.MaxDimension = _previewResolution.MaxDimension;
        var displayBudget = decodeMax > 1920 ? 96L * 1024 * 1024 : FrameBufferPool.DefaultMaxRetainedBytes;
        if (_displayPool.MaxRetainedBytes != displayBudget)
            _displayPool.MaxRetainedBytes = displayBudget;
    }

    private void UpdatePreviewResolutionLabel()
    {
        if (PreviewResolutionLabel is null) return;
        var (canvasW, canvasH) = ActiveMonitorNativeSize();
        var (presentedW, presentedH) = _lastPresentedSize is { } p
            ? ((int?)p.Width, (int?)p.Height)
            : ((int?)null, (int?)null);
        PreviewResolutionLabel.Text = _previewResolution.ButtonLabel(
            presentedW, presentedH, canvasW, canvasH, _compactPreviewResolutionLabel);
    }

    private void ApplyPreviewResolution(PreviewResolution next)
    {
        if (next == _previewResolution) return;
        _previewResolution = next;
        try
        {
            var prefs = UserPreferences.Load();
            prefs.Preview.Resolution = next.Tag;
            UserPreferences.Save(prefs);
        }
        catch
        {
            // Persisting preview quality is a convenience; never interrupt playback.
        }

        _decodeGeneration++;
        _decodeBusy = false;
        _decodeCts?.Cancel();
        CancelPrerolls();
        _lastPresentedSize = null;
        _displayPool.Clear();
        SyncPreviewResolutionPipeline();
        UpdatePreviewResolutionLabel();
        if (_session is not null)
            InvalidateCurrentFrame();
    }

    private async Task ShowCustomPreviewResolutionDialogAsync()
    {
        if (XamlRoot is null) return;
        var (canvasW, canvasH) = ActiveMonitorNativeSize();
        var initialMax = _previewResolution.MaxDimension
            ?? (canvasW is > 0 and var cw && canvasH is > 0 and var ch
                ? Math.Clamp(Math.Max(cw, ch), PreviewResolution.MinCustomDimension, PreviewResolution.MaxCustomDimension)
                : PreviewResolution.DefaultMaxDimension);

        var maxEdgeField = new UnitNumberField
        {
            Spec = new UnitFieldSpec
            {
                Unit = " px",
                Minimum = PreviewResolution.MinCustomDimension,
                Maximum = PreviewResolution.MaxCustomDimension,
                Step = 2,
                Decimals = 0,
                UnitsPerPixel = 4,
                Default = PreviewResolution.DefaultMaxDimension,
            },
            Value = initialMax,
        };
        AutomationProperties.SetName(maxEdgeField, "Frame preview max edge");

        var noteText = new TextBlock
        {
            Text = "Caps the longest edge decoded and composited for preview playback. Export always renders at full output resolution.",
            FontSize = AppTheme.FontSize.Sm,
            Foreground = AppTheme.Text.TertiaryBrush,
            TextWrapping = TextWrapping.WrapWholeWords,
        };

        var resultText = new TextBlock
        {
            FontSize = AppTheme.FontSize.Sm,
            Foreground = AppTheme.Text.SecondaryBrush,
            TextWrapping = TextWrapping.WrapWholeWords,
        };

        void UpdateResultReadout(double rawValue)
        {
            var maxEdge = (int)Math.Round(rawValue);
            if (canvasW is > 0 and var cw && canvasH is > 0 and var ch
                && maxEdge >= PreviewResolution.MinCustomDimension
                && maxEdge <= PreviewResolution.MaxCustomDimension)
            {
                var candidate = PreviewResolution.FromCustomMaxDimension(maxEdge);
                var (fitW, fitH) = candidate.FitDimensions(cw, ch);
                var suffix = fitW == cw && fitH == ch ? " (full canvas)" : "";
                resultText.Text = $"Result on current canvas: {fitW}×{fitH}{suffix}";
                resultText.Visibility = Visibility.Visible;
            }
            else
            {
                resultText.Visibility = Visibility.Collapsed;
            }
        }

        UpdateResultReadout(initialMax);
        maxEdgeField.ValueChanged += (_, value) => UpdateResultReadout(value);

        var errorBar = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, IsClosable = true };
        var body = new StackPanel { Spacing = AppTheme.Spacing.Md, MinWidth = 300 };
        var row = new Grid { ColumnSpacing = AppTheme.Spacing.Md };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = "Max edge",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = AppTheme.FontSize.Sm,
            Foreground = AppTheme.Text.TertiaryBrush,
        });
        Grid.SetColumn(maxEdgeField, 1);
        row.Children.Add(maxEdgeField);

        body.Children.Add(noteText);
        body.Children.Add(row);
        body.Children.Add(resultText);
        body.Children.Add(errorBar);

        var dialog = AppDialog.Create(XamlRoot);
        dialog.Title = "Frame Preview Resolution";
        dialog.Content = body;
        dialog.PrimaryButtonText = "Apply";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        var chosenMax = initialMax;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            maxEdgeField.CommitPendingEdit();
            var value = (int)Math.Round(maxEdgeField.Value);
            if (!PreviewResolution.TryValidateCustomMaxDimension(value, out var err))
            {
                errorBar.Message = err ?? "Enter an even pixel size.";
                errorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }

            chosenMax = value;
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ApplyPreviewResolution(PreviewResolution.FromCustomMaxDimension(chosenMax));
    }

    private void CanvasSize_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var tl = _session.ActiveTimeline;
        var currentAspect = CanvasSizePresets.MatchAspectTag(tl.Width, tl.Height);
        var currentQuality = CanvasSizePresets.MatchQualityTag(tl.Width, tl.Height);

        var flyout = new MenuFlyout();
        foreach (var (tag, label) in CanvasSizePresets.Aspects)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = label,
                IsChecked = string.Equals(currentAspect, tag, StringComparison.Ordinal),
            };
            var aspectTag = tag;
            item.Click += (_, _) => ApplyCanvasSettings(aspectRatio: aspectTag, quality: currentQuality);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        foreach (var (tag, _) in CanvasSizePresets.Qualities)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Text = tag,
                IsChecked = string.Equals(currentQuality, tag, StringComparison.Ordinal),
            };
            var qualityTag = tag;
            item.Click += (_, _) => ApplyCanvasSettings(aspectRatio: null, quality: qualityTag);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        var isCustom = currentAspect is null || currentQuality is null;
        var customItem = new ToggleMenuFlyoutItem
        {
            Text = isCustom ? $"Custom ({tl.Width}×{tl.Height})…" : "Custom size…",
            IsChecked = isCustom,
        };
        customItem.Click += (_, _) => _ = ShowCustomCanvasSizeDialogAsync();
        flyout.Items.Add(customItem);

        WindowPopout.ShowMenuAt(flyout, BtnCanvasSize, PopoutSide.Bottom, PopoutAlignment.Right);
    }

    private void ApplyCanvasSettings(string? aspectRatio, string? quality, int? width = null, int? height = null)
    {
        if (_session is null) return;
        var result = EditCommands.SetProjectSettings(
            _session, fps: null, width: width, height: height, aspectRatio: aspectRatio, quality: quality);
        if (!result.Ok) return;
        var tl = _session.ActiveTimeline;
        ResolutionLabel.Text = $"{tl.Width}×{tl.Height}";
        _lastPresentedSize = null;
        _displayPool.Clear();
        SyncPreviewResolutionPipeline();
        UpdatePreviewResolutionLabel();
        RedrawGuides();
        ProjectSettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ShowCustomCanvasSizeDialogAsync()
    {
        if (_session is null || XamlRoot is null) return;
        var tl = _session.ActiveTimeline;
        var widthField = new UnitNumberField
        {
            Spec = new UnitFieldSpec
            {
                Unit = " px",
                Minimum = CanvasSizePresets.MinCanvasDimension,
                Maximum = CanvasSizePresets.MaxCanvasDimension,
                Step = 2,
                Decimals = 0,
                UnitsPerPixel = 4,
                Default = tl.Width,
            },
            Value = tl.Width,
        };
        var heightField = new UnitNumberField
        {
            Spec = new UnitFieldSpec
            {
                Unit = " px",
                Minimum = CanvasSizePresets.MinCanvasDimension,
                Maximum = CanvasSizePresets.MaxCanvasDimension,
                Step = 2,
                Decimals = 0,
                UnitsPerPixel = 4,
                Default = tl.Height,
            },
            Value = tl.Height,
        };
        AutomationProperties.SetName(widthField, "Canvas width");
        AutomationProperties.SetName(heightField, "Canvas height");

        var errorBar = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, IsClosable = true };
        var body = new StackPanel { Spacing = AppTheme.Spacing.Md, MinWidth = 280 };
        Grid Row(string label, FrameworkElement control)
        {
            var g = new Grid { ColumnSpacing = AppTheme.Spacing.Md };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = AppTheme.FontSize.Sm,
                Foreground = AppTheme.Text.TertiaryBrush,
            });
            Grid.SetColumn(control, 1);
            g.Children.Add(control);
            return g;
        }
        body.Children.Add(Row("Width", widthField));
        body.Children.Add(Row("Height", heightField));
        body.Children.Add(errorBar);

        var dialog = AppDialog.Create(XamlRoot);
        dialog.Title = "Canvas Size";
        dialog.Content = body;
        dialog.PrimaryButtonText = "Apply";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        var chosenW = tl.Width;
        var chosenH = tl.Height;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            widthField.CommitPendingEdit();
            heightField.CommitPendingEdit();
            var w = (int)Math.Round(widthField.Value);
            var h = (int)Math.Round(heightField.Value);
            if (!CanvasSizePresets.TryValidateCustomCanvas(w, h, out var err))
            {
                errorBar.Message = err ?? "Enter an even width and height.";
                errorBar.IsOpen = true;
                args.Cancel = true;
                return;
            }
            chosenW = w;
            chosenH = h;
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            ApplyCanvasSettings(aspectRatio: null, quality: null, width: chosenW, height: chosenH);
    }

    private void CloseAllSourceTabs()
    {
        if (_tabs.Tabs.Count <= 1) return;
        if (IsPlaying) PauseTransport();
        RememberPlayhead();
        _tabs.CloseAllMedia();
        ApplyActiveMonitor();
    }

    private void ScrubBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ScrubBar.CapturePointer(e.Pointer);
        BeginScrub();
        SeekFromScrub(e.GetCurrentPoint(ScrubBar).Position);
        e.Handled = true;
    }

    private void ScrubBar_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_scrubbing) return;
        SeekFromScrub(e.GetCurrentPoint(ScrubBar).Position);
        e.Handled = true;
    }

    private void ScrubBar_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_scrubbing) return;
        SeekFromScrub(e.GetCurrentPoint(ScrubBar).Position);
        ScrubBar.ReleasePointerCapture(e.Pointer);
        EndScrub();
        e.Handled = true;
    }

    private void ScrubBar_PointerCanceled(object sender, PointerRoutedEventArgs e) => EndPointerScrub();

    private void ScrubBar_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndPointerScrub();

    private void EndPointerScrub()
    {
        if (_scrubbing)
            EndScrub();
    }

    private void SeekFromScrub(Point position)
    {
        var pad = ScrubBar.Padding.Left;
        var width = Math.Max(1, ScrubBar.ActualWidth - ScrubBar.Padding.Left - ScrubBar.Padding.Right);
        var t = Math.Clamp((position.X - pad) / width, 0, 1);
        var total = MonitorDurationFrames;
        PlayheadFrame = Math.Min(Math.Max(0, total - 1), (int)Math.Round(t * Math.Max(0, total - 1)));
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();
    private void ToStart_Click(object sender, RoutedEventArgs e) => GoToStart();
    private void ToEnd_Click(object sender, RoutedEventArgs e) => GoToEnd();
    private void StepBack_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void StepFwd_Click(object sender, RoutedEventArgs e) => Step(1);
}
