using System.Numerics;
using System.Runtime.Versioning;
using Temno.Core.Captions;
using Temno.Core.Models;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.WIC;
using D2D = Vortice.Direct2D1.D2D1;
using DWriteApi = Vortice.DirectWrite.DWrite;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using DWriteFactoryType = Vortice.DirectWrite.FactoryType;
using PixelFormat = Vortice.DCommon.PixelFormat;
using AlphaMode = Vortice.DCommon.AlphaMode;
using CoreAlign = Temno.Core.Models.TextAlignment;

namespace Temno.Media.Text;

/// <summary>
/// Windows DirectWrite/Direct2D rasterizer (Mac CoreText equivalent). Falls back to GDI+
/// when factory creation fails.
/// </summary>
internal static class DirectWriteTextRasterizer
{
    private static readonly object Gate = new();
    private static bool _initAttempted;
    private static bool _ready;
    private static ID2D1Factory? _d2d;
    private static IDWriteFactory? _dwrite;
    private static IWICImagingFactory? _wic;

    public static bool TryRasterize(
        string text,
        TextStyle style,
        int canvasWidth,
        int canvasHeight,
        double layoutCenterX,
        double layoutCenterY,
        double boxWidthNorm,
        double boxHeightNorm,
        IReadOnlyList<TextWordRun>? wordRuns,
        out TextRasterization result)
    {
        result = null!;
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            result = RasterizeWindows(
                text, style, canvasWidth, canvasHeight, layoutCenterX, layoutCenterY, boxWidthNorm, boxHeightNorm,
                wordRuns);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<string> ListFamilyNames()
    {
        if (!OperatingSystem.IsWindows() || !EnsureFactories())
            return [];

        lock (Gate)
        {
            try
            {
                return ListFamilyNamesCore();
            }
            catch
            {
                return [];
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static TextRasterization RasterizeWindows(
        string text,
        TextStyle style,
        int canvasWidth,
        int canvasHeight,
        double layoutCenterX,
        double layoutCenterY,
        double boxWidthNorm,
        double boxHeightNorm,
        IReadOnlyList<TextWordRun>? wordRuns)
    {
        if (!EnsureFactories() || _d2d is null || _dwrite is null || _wic is null)
            throw new InvalidOperationException("DirectWrite is unavailable.");

        lock (Gate)
        {
            var fontPx = (float)Math.Max(8.0, style.FontSize * Math.Max(0.05, style.FontScale) * (canvasHeight / 1080.0));
            var boxW = (float)Math.Max(8.0, canvasWidth * Math.Clamp(boxWidthNorm, 0.05, 1.0));
            var maxH = (float)Math.Max(fontPx * 2, canvasHeight);

            using var format = CreateFormat(_dwrite, style, fontPx);
            format.WordWrapping = WordWrapping.Wrap;
            format.TextAlignment = style.Alignment switch
            {
                CoreAlign.Left => Vortice.DirectWrite.TextAlignment.Leading,
                CoreAlign.Right => Vortice.DirectWrite.TextAlignment.Trailing,
                _ => Vortice.DirectWrite.TextAlignment.Center,
            };
            format.ParagraphAlignment = ParagraphAlignment.Near;

            using var layout = _dwrite.CreateTextLayout(text ?? "", format, boxW, maxH);
            var metrics = layout.Metrics;
            var blockW = Math.Max(1f, metrics.WidthIncludingTrailingWhitespace > 0
                ? metrics.WidthIncludingTrailingWhitespace
                : metrics.Width);
            var blockH = Math.Max(1f, metrics.Height);

            var cx = (float)(Math.Clamp(layoutCenterX, 0, 1) * canvasWidth);
            var cy = (float)(Math.Clamp(layoutCenterY, 0, 1) * canvasHeight);
            var originX = cx - boxW / 2f;
            var originY = cy - blockH / 2f;
            _ = boxHeightNorm;

            using var wicBitmap = _wic.CreateBitmap(
                (uint)canvasWidth,
                (uint)canvasHeight,
                Vortice.WIC.PixelFormat.Format32bppPBGRA,
                BitmapCreateCacheOption.CacheOnLoad);

            var rtp = new RenderTargetProperties
            {
                Type = RenderTargetType.Default,
                PixelFormat = new PixelFormat(Format.B8G8R8A8_UNorm, AlphaMode.Premultiplied),
                DpiX = 96,
                DpiY = 96,
                Usage = RenderTargetUsage.None,
                MinLevel = FeatureLevel.Default,
            };
            using var rt = _d2d.CreateWicBitmapRenderTarget(wicBitmap, rtp);
            rt.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            rt.AntialiasMode = AntialiasMode.PerPrimitive;
            rt.BeginDraw();
            rt.Clear(new Color4(0, 0, 0, 0));

            if (style.Background.Enabled)
            {
                var pad = Math.Max(4f, fontPx * 0.18f);
                using var bg = rt.CreateSolidColorBrush(ToColor4(style.Background.Color));
                rt.FillRectangle(
                    new Rect(originX - pad, originY - pad, boxW + pad * 2, blockH + pad * 2),
                    bg);
            }

            if (style.Shadow.Enabled)
            {
                var canvasScale = (float)(canvasHeight / 1080.0);
                using var shadow = rt.CreateSolidColorBrush(ToColor4(style.Shadow.Color));
                rt.DrawTextLayout(
                    new Vector2(
                        originX + (float)style.Shadow.OffsetX * canvasScale,
                        originY - (float)style.Shadow.OffsetY * canvasScale),
                    layout,
                    shadow);
            }

            if (style.Border.Enabled)
            {
                using var outline = rt.CreateSolidColorBrush(ToColor4(style.Border.Color));
                var t = Math.Max(1f, fontPx * 0.05f);
                ReadOnlySpan<(float dx, float dy)> offsets =
                [
                    (t, 0), (-t, 0), (0, t), (0, -t), (t, t), (t, -t), (-t, t), (-t, -t),
                ];
                foreach (var (dx, dy) in offsets)
                    rt.DrawTextLayout(new Vector2(originX + dx, originY + dy), layout, outline);
            }

            if (wordRuns is { Count: > 0 })
            {
                DrawPerWord(rt, layout, style, text ?? "", wordRuns, originX, originY, fontPx);
            }
            else
            {
                using var fill = rt.CreateSolidColorBrush(ToColor4(style.Color));
                rt.DrawTextLayout(new Vector2(originX, originY), layout, fill);
            }

            rt.EndDraw();

            var bgra = CopyStraightBgra(wicBitmap, canvasWidth, canvasHeight);
            return new TextRasterization(canvasWidth, canvasHeight, bgra);
        }
    }

    /// <summary>
    /// Mac TextFrameRenderer.renderPerWord: lay the line out once for metrics, then draw each word on
    /// its own with that word's colour, alpha, scale and vertical offset, plus an optional rounded
    /// block behind it. Words the animator dropped are simply absent from <paramref name="runs"/>.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void DrawPerWord(
        ID2D1RenderTarget rt,
        IDWriteTextLayout layout,
        TextStyle style,
        string text,
        IReadOnlyList<TextWordRun> runs,
        float originX,
        float originY,
        float fontPx)
    {
        // A word is drawn as its own layout at the rect the full-line layout put it in, so that
        // layout must place the word at its own origin. Reusing the line's format would re-apply the
        // line's centring inside the word's box and shift every word sideways.
        using var wordFormat = CreateFormat(_dwrite!, style, fontPx);
        wordFormat.TextAlignment = Vortice.DirectWrite.TextAlignment.Leading;
        wordFormat.ParagraphAlignment = ParagraphAlignment.Near;
        wordFormat.WordWrapping = WordWrapping.NoWrap;

        var saved = rt.Transform;
        foreach (var run in runs)
        {
            if (run.Start < 0 || run.Length <= 0 || run.Start + run.Length > text.Length) continue;

            var boxes = layout.HitTestTextRange((uint)run.Start, (uint)run.Length, originX, originY);
            if (boxes is null || boxes.Length == 0) continue;
            var box = boxes[0];
            if (box.Width <= 0 || box.Height <= 0) continue;

            var dy = (float)(run.OffsetY * fontPx);
            var centerX = box.Left + box.Width / 2f;
            var centerY = box.Top + box.Height / 2f;
            var scale = (float)Math.Clamp(run.Scale, 0.05, 8);

            rt.Transform =
                Matrix3x2.CreateTranslation(0, dy)
                * Matrix3x2.CreateScale(scale, scale, new Vector2(centerX, centerY))
                * saved;

            if (run.Background is { } bg && bg.A > 0.001)
            {
                // Mac drawWordBackground: corner radius 0.12em, padding 0.18em x 0.10em.
                var padX = fontPx * 0.18f;
                var padY = fontPx * 0.10f;
                var radius = fontPx * 0.12f;
                using var bgBrush = rt.CreateSolidColorBrush(ToColor4(bg));
                rt.FillRoundedRectangle(
                    new RoundedRectangle
                    {
                        Rect = new Rect(box.Left - padX, box.Top - padY, box.Width + padX * 2, box.Height + padY * 2),
                        RadiusX = radius,
                        RadiusY = radius,
                    },
                    bgBrush);
            }

            // Size the layout to the word's own hit-test rect: with Leading alignment and no wrap,
            // drawing at box.Left/box.Top reproduces the position the line layout chose.
            using var wordLayout = _dwrite!.CreateTextLayout(
                text.Substring(run.Start, run.Length), wordFormat, box.Width + fontPx, box.Height + fontPx);
            using var brush = rt.CreateSolidColorBrush(ToColor4(run.Color));
            brush.Opacity = (float)Math.Clamp(run.Opacity, 0, 1);
            rt.DrawTextLayout(new Vector2(box.Left, box.Top), wordLayout, brush);
        }
        rt.Transform = saved;
    }

    [SupportedOSPlatform("windows")]
    private static IDWriteTextFormat CreateFormat(IDWriteFactory factory, TextStyle style, float fontPx)
    {
        var weight = style.IsBold || FontNameMap.ContainsBold(style.FontName)
            ? FontWeight.Bold
            : FontWeight.Normal;
        var slant = style.IsItalic ? FontStyle.Italic : FontStyle.Normal;
        Exception? last = null;
        foreach (var family in FontNameMap.CandidateFamilies(style.FontName))
        {
            try
            {
                return factory.CreateTextFormat(family, weight, slant, FontStretch.Normal, fontPx);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("No DirectWrite font family available.");
    }

    [SupportedOSPlatform("windows")]
    private static List<string> ListFamilyNamesCore()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        using var collection = _dwrite!.GetSystemFontCollection(checkForUpdates: false);
        var count = collection.FontFamilyCount;
        for (uint i = 0; i < count; i++)
        {
            using var family = collection.GetFontFamily(i);
            using var familyNames = family.FamilyNames;
            uint index = 0;
            if (!familyNames.FindLocaleName("en-us", out index) && familyNames.Count > 0)
                index = 0;
            if (familyNames.Count == 0)
                continue;
            var name = familyNames.GetString(index);
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name);
        }

        return [.. names];
    }

    private static bool EnsureFactories()
    {
        lock (Gate)
        {
            if (_ready)
                return true;
            if (_initAttempted)
                return false;
            _initAttempted = true;
            try
            {
                _d2d = D2D.D2D1CreateFactory<ID2D1Factory>(D2DFactoryType.MultiThreaded);
                _dwrite = DWriteApi.DWriteCreateFactory<IDWriteFactory>(DWriteFactoryType.Shared);
                _wic = new IWICImagingFactory();
                _ready = _d2d is not null && _dwrite is not null && _wic is not null;
                return _ready;
            }
            catch
            {
                _ready = false;
                return false;
            }
        }
    }

    private static Color4 ToColor4(TextStyle.RGBA c)
        => new(
            (float)Math.Clamp(c.R, 0, 1),
            (float)Math.Clamp(c.G, 0, 1),
            (float)Math.Clamp(c.B, 0, 1),
            (float)Math.Clamp(c.A, 0, 1));

    [SupportedOSPlatform("windows")]
    private static byte[] CopyStraightBgra(IWICBitmap bitmap, int width, int height)
    {
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        bitmap.CopyPixels((uint)stride, pixels);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var a = pixels[i + 3];
            if (a == 0)
            {
                pixels[i] = 0;
                pixels[i + 1] = 0;
                pixels[i + 2] = 0;
                continue;
            }
            if (a == 255)
                continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
        }

        return pixels;
    }
}
