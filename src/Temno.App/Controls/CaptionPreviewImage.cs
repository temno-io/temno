using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Temno.Core.Models;
using Temno.Media.Text;

namespace Temno.App.Controls;

/// <summary>
/// Shows a looping caption preview, rendered by <see cref="CaptionPreview"/> — which is the real
/// timeline renderer — so what this shows is what an export produces. Mac: `CaptionAnimatedPreview`
/// and the per-cell preview in `CaptionPresetGallery`.
/// <para>
/// At rest it holds a single **poster frame** and subscribes to nothing. Only while
/// <see cref="Animate"/> is true does it join the shared 30 fps ticker, so a grid of resting cells
/// costs one bitmap each.
/// </para>
/// </summary>
public sealed partial class CaptionPreviewImage : UserControl
{
    private const int Fps = 30;

    private readonly Image _image = new()
    {
        Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private TextStyle _style = new();
    private Transform _transform = CaptionPreview.GalleryTransform();
    private TextAnimation? _animation;
    private string _content = CaptionPreview.GallerySample;

    private WriteableBitmap? _bitmap;
    private byte[]? _scratch;
    private int _frame;
    private bool _rendering;
    private bool _joined;

    private XamlRoot? _hookedRoot;

    public CaptionPreviewImage()
    {
        Content = _image;
        IsHitTestVisible = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Device pixels after <see cref="XamlRoot.RasterizationScale"/>. Callers pass CSS / canvas
    /// pixels in <see cref="RenderPixelWidth"/>; this is what actually gets rasterized.
    /// </summary>
    public int EffectiveRenderWidth => ScalePixels(RenderPixelWidth);

    public int EffectiveRenderHeight => ScalePixels(RenderPixelHeight);

    public double CurrentRasterizationScale => XamlRoot?.RasterizationScale ?? 1.0;

    /// <summary>Runs the loop. False shows the settled poster frame and does no per-frame work.</summary>
    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
        nameof(Animate), typeof(bool), typeof(CaptionPreviewImage),
        new PropertyMetadata(false, (d, _) => ((CaptionPreviewImage)d).Sync()));

    public bool Animate
    {
        get => (bool)GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    /// <summary>Pixel size to rasterize at, in CSS / canvas pixels. Device pixels are this times <see cref="XamlRoot.RasterizationScale"/>.</summary>
    public static readonly DependencyProperty RenderPixelWidthProperty = DependencyProperty.Register(
        nameof(RenderPixelWidth), typeof(int), typeof(CaptionPreviewImage),
        new PropertyMetadata(168, (d, _) => ((CaptionPreviewImage)d).Invalidate()));

    public int RenderPixelWidth
    {
        get => (int)GetValue(RenderPixelWidthProperty);
        set => SetValue(RenderPixelWidthProperty, value);
    }

    public static readonly DependencyProperty RenderPixelHeightProperty = DependencyProperty.Register(
        nameof(RenderPixelHeight), typeof(int), typeof(CaptionPreviewImage),
        new PropertyMetadata(96, (d, _) => ((CaptionPreviewImage)d).Invalidate()));

    public int RenderPixelHeight
    {
        get => (int)GetValue(RenderPixelHeightProperty);
        set => SetValue(RenderPixelHeightProperty, value);
    }

    /// <summary>Replaces what the preview shows and restarts its loop from frame 0.</summary>
    public void Configure(string content, TextStyle style, Transform transform, TextAnimation? animation)
    {
        _content = content ?? "";
        _style = style ?? new TextStyle();
        _transform = transform ?? CaptionPreview.GalleryTransform();
        _animation = animation;
        _frame = 0;
        Invalidate();
    }

    /// <summary>
    /// Re-rasterize the current clip without restarting the loop. Overlay drag mutates
    /// <see cref="Transform.CenterX"/> / <c>CenterY</c> in place.
    /// </summary>
    public void Refresh() => Invalidate();

    private int LoopFrames => Math.Max(1, CaptionPreview.LoopFrames(_animation?.Preset));

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookXamlRoot();
        Sync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookXamlRoot();
        Leave();
    }

    private void HookXamlRoot()
    {
        if (XamlRoot is null || ReferenceEquals(XamlRoot, _hookedRoot)) return;
        UnhookXamlRoot();
        _hookedRoot = XamlRoot;
        _hookedRoot.Changed += OnXamlRootChanged;
    }

    private void UnhookXamlRoot()
    {
        if (_hookedRoot is null) return;
        _hookedRoot.Changed -= OnXamlRootChanged;
        _hookedRoot = null;
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Invalidate();

    private int ScalePixels(int cssPixels)
    {
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        return Math.Clamp((int)Math.Round(Math.Max(1, cssPixels) * scale), 1, 4096);
    }

    private void Sync()
    {
        if (Animate && IsLoaded && Visibility == Visibility.Visible) Join();
        else Leave();
        Invalidate();
    }

    private void Join()
    {
        if (_joined) return;
        _joined = true;
        PreviewTicker.Add(this);
    }

    private void Leave()
    {
        if (!_joined) return;
        _joined = false;
        PreviewTicker.Remove(this);
        // Settle on the poster frame so a cell that stops animating does not freeze mid-word.
        _frame = LoopFrames - 1;
        Invalidate();
    }

    internal void Tick()
    {
        _frame = (_frame + 1) % LoopFrames;
        Invalidate();
    }

    private async void Invalidate()
    {
        if (_rendering) return;              // drop a tick rather than queue work behind a slow frame
        if (!IsLoaded) return;
        HookXamlRoot();
        var width = EffectiveRenderWidth;
        var height = EffectiveRenderHeight;
        var frame = Animate ? _frame : LoopFrames - 1;

        _rendering = true;
        try
        {
            var clip = CaptionPreview.BuildClip(_content, _style, _transform, _animation);
            using var rendered = await CaptionPreview.RenderAsync(clip, frame, width, height);
            if (rendered is null)
            {
                _image.Source = null;
                return;
            }
            _image.Source = ToBitmap(rendered);
        }
        catch (Exception)
        {
            // A preview must never take the editor down. Showing nothing is the honest failure.
            _image.Source = null;
        }
        finally
        {
            _rendering = false;
        }
    }

    private WriteableBitmap ToBitmap(Temno.Media.VideoFrame frame)
    {
        var length = checked(frame.Width * frame.Height * 4);
        if (length > frame.Data.Length) length = frame.Data.Length;

        var bgra = _scratch;
        if (bgra is null || bgra.Length != length)
        {
            bgra = new byte[length];
            _scratch = bgra;
        }

        if (string.Equals(frame.PixelFormat, "bgra", StringComparison.OrdinalIgnoreCase))
        {
            Buffer.BlockCopy(frame.Data, 0, bgra, 0, length);
        }
        else
        {
            for (var i = 0; i + 3 < length; i += 4)
            {
                bgra[i] = frame.Data[i + 2];
                bgra[i + 1] = frame.Data[i + 1];
                bgra[i + 2] = frame.Data[i];
                bgra[i + 3] = frame.Data[i + 3];
            }
        }

        var bitmap = _bitmap;
        if (bitmap is null || bitmap.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
        {
            bitmap = new WriteableBitmap(frame.Width, frame.Height);
            _bitmap = bitmap;
        }
        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(bgra, 0, Math.Min(bgra.Length, (int)bitmap.PixelBuffer.Capacity));
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>
    /// One rendering subscription for every animating preview in the app, rather than a timer each.
    /// A gallery of cells with one hovered must cost one hovered cell, not N timers.
    /// </summary>
    private static class PreviewTicker
    {
        private static readonly List<CaptionPreviewImage> Active = [];
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1.0 / Fps);
        private static bool _hooked;
        private static DateTime _last = DateTime.MinValue;

        public static void Add(CaptionPreviewImage image)
        {
            if (Active.Contains(image)) return;
            Active.Add(image);
            if (_hooked) return;
            _hooked = true;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
        }

        public static void Remove(CaptionPreviewImage image)
        {
            Active.Remove(image);
            if (Active.Count > 0 || !_hooked) return;
            _hooked = false;
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        }

        private static void OnRendering(object? sender, object e)
        {
            // CompositionTarget.Rendering fires at display refresh, which is usually faster than the
            // 30 fps the previews are authored at; throttle rather than render every vsync.
            var now = DateTime.UtcNow;
            if (now - _last < Interval) return;
            _last = now;
            for (var i = Active.Count - 1; i >= 0; i--) Active[i].Tick();
        }
    }
}
