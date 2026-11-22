using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.Versioning;
using Temno.Core.Captions;
using Temno.Core.Models;

namespace Temno.Media.Text;

/// <summary>
/// Windows GDI+ rasterizer so captions/titles use a real sans font instead of the
/// portable stick glyphs. Stick-font remains the fallback (Mac tests / GDI failure).
/// </summary>
internal static class GdiTextRasterizer
{
    public static bool TryRasterize(
        string text,
        TextStyle style,
        int canvasWidth,
        int canvasHeight,
        double layoutCenterX,
        double layoutCenterY,
        double boxWidthNorm,
        double boxHeightNorm,
        out TextRasterization result)
    {
        result = null!;
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            result = RasterizeWindows(
                text, style, canvasWidth, canvasHeight, layoutCenterX, layoutCenterY, boxWidthNorm, boxHeightNorm);
            return true;
        }
        catch
        {
            return false;
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
        double boxHeightNorm)
    {
        using var bmp = new Bitmap(canvasWidth, canvasHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var fontPx = Math.Max(8.0, style.FontSize * Math.Max(0.05, style.FontScale) * (canvasHeight / 1080.0));
        using var family = ResolveFamily(style.FontName);
        var fontStyle = FontStyle.Regular;
        if (style.IsBold || FontNameMap.ContainsBold(style.FontName))
            fontStyle |= FontStyle.Bold;
        if (style.IsItalic)
            fontStyle |= FontStyle.Italic;
        using var font = new Font(family, (float)fontPx, fontStyle, GraphicsUnit.Pixel);

        using var format = new StringFormat
        {
            Alignment = style.Alignment switch
            {
                TextAlignment.Left => StringAlignment.Near,
                TextAlignment.Right => StringAlignment.Far,
                _ => StringAlignment.Center,
            },
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.LineLimit,
            Trimming = StringTrimming.Word,
        };

        var maxWidth = Math.Max(8, (int)Math.Round(canvasWidth * Math.Clamp(boxWidthNorm, 0.05, 1.0)));
        var size = g.MeasureString(text, font, maxWidth, format);
        var blockW = Math.Max(1, size.Width);
        var blockH = Math.Max(1, size.Height);
        var cx = Math.Clamp(layoutCenterX, 0, 1) * canvasWidth;
        var cy = Math.Clamp(layoutCenterY, 0, 1) * canvasHeight;
        var originX = (float)(cx - blockW / 2.0);
        var originY = (float)(cy - blockH / 2.0);
        var layout = new RectangleF(originX, originY, (float)blockW, (float)blockH);

        if (style.Background.Enabled)
        {
            var pad = Math.Max(4f, (float)(fontPx * 0.18));
            using var bg = new SolidBrush(ToColor(style.Background.Color));
            g.FillRectangle(bg, originX - pad, originY - pad, blockW + pad * 2, blockH + pad * 2);
        }

        if (style.Shadow.Enabled)
        {
            var canvasScale = (float)(canvasHeight / 1080.0);
            var sx = originX + (float)style.Shadow.OffsetX * canvasScale;
            var sy = originY - (float)style.Shadow.OffsetY * canvasScale;
            using var shadow = new SolidBrush(ToColor(style.Shadow.Color));
            g.DrawString(text, font, shadow, new RectangleF(sx, sy, (float)blockW, (float)blockH), format);
        }

        if (style.Border.Enabled)
        {
            using var outline = new SolidBrush(ToColor(style.Border.Color));
            var t = Math.Max(1f, (float)(fontPx * 0.05));
            (float dx, float dy)[] offsets = [(t, 0), (-t, 0), (0, t), (0, -t), (t, t), (t, -t), (-t, t), (-t, -t)];
            foreach (var (dx, dy) in offsets)
                g.DrawString(text, font, outline, new RectangleF(originX + dx, originY + dy, (float)blockW, (float)blockH), format);
        }

        using (var fill = new SolidBrush(ToColor(style.Color)))
            g.DrawString(text, font, fill, layout, format);

        return new TextRasterization(canvasWidth, canvasHeight, CopyBgra(bmp));
    }

    [SupportedOSPlatform("windows")]
    private static FontFamily ResolveFamily(string? fontName)
    {
        foreach (var candidate in FontNameMap.CandidateFamilies(fontName))
        {
            try
            {
                return new FontFamily(candidate);
            }
            catch (ArgumentException)
            {
                // try next
            }
        }

        return FontFamily.GenericSansSerif;
    }

    private static Color ToColor(TextStyle.RGBA c)
    {
        byte Ch(double v) => (byte)Math.Clamp((int)Math.Round(Math.Clamp(v, 0, 1) * 255.0), 0, 255);
        return Color.FromArgb(Ch(c.A), Ch(c.R), Ch(c.G), Ch(c.B));
    }

    [SupportedOSPlatform("windows")]
    private static byte[] CopyBgra(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var bgra = new byte[checked(bmp.Width * bmp.Height * 4)];
            var row = new byte[stride];
            for (var y = 0; y < bmp.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, row, 0, stride);
                Buffer.BlockCopy(row, 0, bgra, y * bmp.Width * 4, bmp.Width * 4);
            }

            return bgra;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
