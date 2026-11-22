using System.Text;
using System.Text.Json;
using Temno.Core.Captions;
using Temno.Core.Models;

namespace Temno.Media.Text;

/// <summary>
/// Portable CPU text rasterizer (WP-94). Windows preview/export uses DirectWrite
/// (system fonts, grayscale AA) with GDI+ then stick-font fallbacks. Helvetica/SF
/// names map to Segoe UI; project JSON fontName is not rewritten.
/// </summary>
public sealed class CpuTextRasterizer : ITextRasterizer
{
    public TextRasterization Rasterize(string text, TextStyle style, int canvasWidth, int canvasHeight)
        => Rasterize(text, style, canvasWidth, canvasHeight, layoutCenterX: 0.5, layoutCenterY: 0.5);

    public TextRasterization Rasterize(
        string text,
        TextStyle style,
        int canvasWidth,
        int canvasHeight,
        double layoutCenterX,
        double layoutCenterY)
        => Rasterize(text, style, canvasWidth, canvasHeight, layoutCenterX, layoutCenterY, boxWidthNorm: 0.9, boxHeightNorm: 1);

    /// <param name="wordRuns">
    /// Optional per-word draw states for a per-word animation preset (Mac renderPerWord). Honoured in
    /// full by the DirectWrite path. The stick-font fallback applies colour, opacity, vertical offset
    /// and background but **not** per-word scale, because its integer glyph cell cannot scale a single
    /// word without shifting the rest of the line. The GDI+ fallback ignores runs entirely and draws
    /// one flat line.
    /// </param>
    /// <param name="layoutCenterX">Normalized 0–1 canvas X for the text block center (from clip transform).</param>
    /// <param name="layoutCenterY">Normalized 0–1 canvas Y for the text block center.</param>
    /// <param name="boxWidthNorm">Normalized wrap width (Mac clip transform.width).</param>
    /// <param name="boxHeightNorm">Normalized clip box height.</param>
    public TextRasterization Rasterize(
        string text,
        TextStyle style,
        int canvasWidth,
        int canvasHeight,
        double layoutCenterX,
        double layoutCenterY,
        double boxWidthNorm,
        double boxHeightNorm,
        IReadOnlyList<TextWordRun>? wordRuns = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (canvasWidth <= 0) throw new ArgumentOutOfRangeException(nameof(canvasWidth));
        if (canvasHeight <= 0) throw new ArgumentOutOfRangeException(nameof(canvasHeight));

        text ??= "";
        wordRuns = NormalizeRuns(wordRuns, text.Length);
        if (DirectWriteTextRasterizer.TryRasterize(
                text, style, canvasWidth, canvasHeight, layoutCenterX, layoutCenterY, boxWidthNorm, boxHeightNorm, wordRuns, out var dwrite))
            return dwrite;
        if (GdiTextRasterizer.TryRasterize(
                text, style, canvasWidth, canvasHeight, layoutCenterX, layoutCenterY, boxWidthNorm, boxHeightNorm, out var gdi))
            return gdi;

        var bgra = new byte[checked(canvasWidth * canvasHeight * 4)]; // transparent

        var fontPx = Math.Max(8.0, style.FontSize * Math.Max(0.05, style.FontScale) * (canvasHeight / 1080.0));
        if (style.IsBold) fontPx *= 1.08;
        var scale = Math.Max(1, (int)Math.Round(fontPx / 8.0)); // stick font base cell 8×8
        var lineHeight = scale * 10;
        var charWidth = scale * (style.IsBold ? 7 : 6);

        var lines = WrapLines(text, canvasWidth, charWidth, maxWidthRatio: Math.Clamp(boxWidthNorm, 0.05, 1.0));
        if (lines.Count == 0) return new TextRasterization(canvasWidth, canvasHeight, bgra);
        // Per-word placement works in whitespace-token space, not character offsets: WrapLines
        // collapses runs of spaces, so a wrapped line is not always a literal substring of the source.
        var lineFirstToken = LineFirstTokens(lines);
        var runsByToken = RunsByToken(text, wordRuns);

        var blockW = lines.Max(l => MeasureLine(l, charWidth));
        var blockH = lines.Count * lineHeight;
        var cx = Math.Clamp(layoutCenterX, 0, 1) * canvasWidth;
        var cy = Math.Clamp(layoutCenterY, 0, 1) * canvasHeight;
        var originY = cy - blockH / 2.0;
        var blockOriginX = cx - blockW / 2.0;

        if (style.Background.Enabled)
        {
            var pad = Math.Max(scale, (int)(fontPx * 0.15));
            var bx0 = (int)Math.Floor(blockOriginX - pad);
            var by0 = (int)Math.Floor(originY - pad);
            var bx1 = (int)Math.Ceiling(blockOriginX + blockW + pad);
            var by1 = (int)Math.Ceiling(originY + blockH + pad);
            FillRect(bgra, canvasWidth, canvasHeight, bx0, by0, bx1, by1, style.Background.Color, premultiply: true);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var lineW = MeasureLine(line, charWidth);
            var lineX = style.Alignment switch
            {
                TextAlignment.Left => blockOriginX,
                TextAlignment.Right => blockOriginX + (blockW - lineW),
                _ => blockOriginX + (blockW - lineW) / 2.0,
            };
            var lineY = originY + i * lineHeight;

            if (runsByToken is not null)
            {
                // Per-word preset: each word carries its own colour, alpha, offset and background, and
                // a word the animator dropped is simply not drawn (Mac skips opacity <= 0).
                DrawWordsOnLine(
                    bgra, canvasWidth, canvasHeight, line, lineFirstToken[i], runsByToken,
                    lineX, lineY, scale, charWidth, lineHeight, fontPx, style);
                continue;
            }

            if (style.Shadow.Enabled)
            {
                var sx = lineX + style.Shadow.OffsetX * scale * 0.25;
                var sy = lineY + (-style.Shadow.OffsetY) * scale * 0.25; // Mac Y often up; screen Y down
                DrawLineText(bgra, canvasWidth, canvasHeight, line, sx, sy, scale, charWidth, style.Shadow.Color,
                    outline: false, bold: style.IsBold);
            }

            if (style.Border.Enabled)
            {
                DrawLineText(bgra, canvasWidth, canvasHeight, line, lineX, lineY, scale, charWidth, style.Border.Color,
                    outline: true, bold: style.IsBold);
            }

            DrawLineText(bgra, canvasWidth, canvasHeight, line, lineX, lineY, scale, charWidth, style.Color,
                outline: false, bold: style.IsBold);
        }

        return new TextRasterization(canvasWidth, canvasHeight, bgra);
    }

    /// <summary>
    /// Index of the first whitespace-token of each wrapped line, counted over the source string.
    /// WrapLines neither drops nor reorders tokens, so counting them is exact even when it collapses
    /// runs of spaces (which is what makes a character-offset search unreliable here).
    /// </summary>
    private static int[] LineFirstTokens(List<string> lines)
    {
        var first = new int[lines.Count];
        var token = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            first[i] = token;
            token += lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        }
        return first;
    }

    /// <summary>Whitespace-token index containing <paramref name="offset"/>, or -1.</summary>
    private static int TokenIndexAt(string text, int offset)
    {
        var token = 0;
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) break;
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            if (offset >= start && offset < i) return token;
            if (offset < start) return -1;
            token++;
        }
        return -1;
    }

    /// <summary>Drops empty or out-of-range runs so downstream paths can assume each is drawable.</summary>
    private static IReadOnlyList<TextWordRun>? NormalizeRuns(
        IReadOnlyList<TextWordRun>? runs, int textLength)
    {
        if (runs is null || runs.Count == 0) return null;
        var kept = new List<TextWordRun>(runs.Count);
        foreach (var run in runs)
        {
            var start = Math.Clamp(run.Start, 0, Math.Max(0, textLength));
            var length = Math.Clamp(run.Length, 0, Math.Max(0, textLength - start));
            if (length <= 0 || run.Opacity <= 0) continue;
            kept.Add(run with { Start = start, Length = length });
        }
        return kept.Count == 0 ? null : kept;
    }

    /// <summary>Indexes runs by whitespace-token position, which survives WrapLines' space collapsing.</summary>
    private static Dictionary<int, TextWordRun>? RunsByToken(
        string text, IReadOnlyList<TextWordRun>? runs)
    {
        if (runs is null || runs.Count == 0) return null;
        var byToken = new Dictionary<int, TextWordRun>(runs.Count);
        foreach (var run in runs)
        {
            var token = TokenIndexAt(text, run.Start);
            if (token >= 0) byToken[token] = run;
        }
        return byToken.Count == 0 ? null : byToken;
    }

    /// <summary>
    /// Draws one wrapped line word by word. The stick font has a fixed advance, so each word's pen
    /// position is arithmetic; per-word scale is deliberately not applied here (see Rasterize).
    /// </summary>
    private static void DrawWordsOnLine(
        byte[] bgra,
        int canvasWidth,
        int canvasHeight,
        string line,
        int firstToken,
        Dictionary<int, TextWordRun> runsByToken,
        double lineX,
        double lineY,
        int scale,
        int charWidth,
        int lineHeight,
        double fontPx,
        TextStyle style)
    {
        var wordsOnLine = line.Split(' ', StringSplitOptions.None);
        var penX = lineX;
        for (var w = 0; w < wordsOnLine.Length; w++)
        {
            var word = wordsOnLine[w];
            var wordWidth = MeasureLine(word, charWidth);
            if (word.Length > 0 && runsByToken.TryGetValue(firstToken + w, out var run))
            {
                var y = lineY + run.OffsetY * fontPx;
                if (run.Background is { } bg && bg.A > 0.001)
                {
                    var padX = Math.Max(1, fontPx * 0.18);
                    var padY = Math.Max(1, fontPx * 0.10);
                    FillRect(
                        bgra, canvasWidth, canvasHeight,
                        (int)Math.Floor(penX - padX), (int)Math.Floor(y - padY),
                        (int)Math.Ceiling(penX + wordWidth + padX), (int)Math.Ceiling(y + lineHeight + padY),
                        bg, premultiply: true);
                }
                DrawLineText(
                    bgra, canvasWidth, canvasHeight, word, penX, y, scale, charWidth,
                    Faded(run.Color, run.Opacity), outline: false, bold: style.IsBold);
            }
            penX += wordWidth + charWidth; // advance past the word and its trailing space
        }
    }

    private static TextStyle.RGBA Faded(TextStyle.RGBA color, double opacity) => new()
    {
        R = color.R,
        G = color.G,
        B = color.B,
        A = color.A * Math.Clamp(opacity, 0, 1),
    };

    /// <summary>Parse clip.TextStyle JSON or default style.</summary>
    public static TextStyle StyleFromClip(Clip clip)
    {
        if (clip.TextStyle is { } el)
        {
            try
            {
                var s = JsonSerializer.Deserialize<TextStyle>(el.GetRawText(), JsonOptions.ForProject);
                if (s is not null) return s;
            }
            catch
            {
                // fall through
            }
        }
        return new TextStyle();
    }

    private static List<string> WrapLines(string text, int canvasWidth, int charWidth, double maxWidthRatio)
    {
        var maxChars = Math.Max(1, (int)(canvasWidth * maxWidthRatio / Math.Max(1, charWidth)));
        var result = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                result.Add("");
                continue;
            }
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
            {
                result.Add("");
                continue;
            }
            var sb = new StringBuilder();
            foreach (var word in words)
            {
                if (sb.Length == 0)
                {
                    sb.Append(word);
                    continue;
                }
                if (sb.Length + 1 + word.Length <= maxChars)
                {
                    sb.Append(' ').Append(word);
                }
                else
                {
                    result.Add(sb.ToString());
                    sb.Clear();
                    sb.Append(word);
                }
            }
            if (sb.Length > 0) result.Add(sb.ToString());
        }
        return result;
    }

    private static int MeasureLine(string line, int charWidth)
        => Math.Max(0, line.Length) * charWidth;

    private static void DrawLineText(
        byte[] bgra, int w, int h, string line, double x, double y, int scale, int charWidth,
        TextStyle.RGBA color, bool outline, bool bold)
    {
        var cx = x;
        foreach (var ch in line)
        {
            DrawGlyph(bgra, w, h, (int)Math.Round(cx), (int)Math.Round(y), ch, scale, color, outline, bold);
            cx += charWidth;
        }
    }

    private static void DrawGlyph(
        byte[] bgra, int w, int h, int x, int y, char ch, int scale,
        TextStyle.RGBA color, bool outline, bool bold)
    {
        var strokes = StickFont.Strokes(ch);
        var thickness = Math.Max(1, scale / (outline ? 2 : 3));
        if (bold) thickness = Math.Max(thickness + 1, (int)(thickness * 1.4));
        if (outline) thickness = Math.Max(thickness + scale / 2, thickness + 1);

        foreach (var (x0, y0, x1, y1) in strokes)
        {
            var ax = x + x0 * scale;
            var ay = y + y0 * scale;
            var bx = x + x1 * scale;
            var by = y + y1 * scale;
            DrawThickLine(bgra, w, h, ax, ay, bx, by, thickness, color);
        }
    }

    private static void DrawThickLine(
        byte[] bgra, int w, int h, int x0, int y0, int x1, int y1, int thickness, TextStyle.RGBA color)
    {
        // Bresenham with circular brush.
        var dx = Math.Abs(x1 - x0);
        var dy = Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var err = dx - dy;
        var r = Math.Max(0, thickness / 2);
        while (true)
        {
            FillCircle(bgra, w, h, x0, y0, r, color);
            if (x0 == x1 && y0 == y1) break;
            var e2 = 2 * err;
            if (e2 > -dy) { err -= dy; x0 += sx; }
            if (e2 < dx) { err += dx; y0 += sy; }
        }
    }

    private static void FillCircle(byte[] bgra, int w, int h, int cx, int cy, int r, TextStyle.RGBA color)
    {
        for (var y = -r; y <= r; y++)
        for (var x = -r; x <= r; x++)
        {
            if (x * x + y * y > r * r) continue;
            Plot(bgra, w, h, cx + x, cy + y, color);
        }
    }

    private static void FillRect(
        byte[] bgra, int w, int h, int x0, int y0, int x1, int y1, TextStyle.RGBA color, bool premultiply)
    {
        x0 = Math.Clamp(x0, 0, w);
        x1 = Math.Clamp(x1, 0, w);
        y0 = Math.Clamp(y0, 0, h);
        y1 = Math.Clamp(y1, 0, h);
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
            Plot(bgra, w, h, x, y, color);
    }

    private static void Plot(byte[] bgra, int w, int h, int x, int y, TextStyle.RGBA color)
    {
        if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
        var a = Clamp01(color.A);
        if (a <= 0) return;
        var i = (y * w + x) * 4;
        // Straight-alpha BGRA (CpuCompositor expects straight alpha).
        var srcA = a;
        var dstA = bgra[i + 3] / 255.0;
        var outA = srcA + dstA * (1 - srcA);
        if (outA <= 1e-6) return;
        var inv = 1 - srcA;
        double Blend(double s, double d) => (s * srcA + d * dstA * inv) / outA;
        var sb = Clamp01(color.B);
        var sg = Clamp01(color.G);
        var sr = Clamp01(color.R);
        var db = bgra[i + 0] / 255.0;
        var dg = bgra[i + 1] / 255.0;
        var dr = bgra[i + 2] / 255.0;
        bgra[i + 0] = (byte)Math.Clamp((int)Math.Round(Blend(sb, db) * 255), 0, 255);
        bgra[i + 1] = (byte)Math.Clamp((int)Math.Round(Blend(sg, dg) * 255), 0, 255);
        bgra[i + 2] = (byte)Math.Clamp((int)Math.Round(Blend(sr, dr) * 255), 0, 255);
        bgra[i + 3] = (byte)Math.Clamp((int)Math.Round(outA * 255), 0, 255);
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);
}

/// <summary>Unit-cell stick strokes in 0..6 × 0..7 for printable ASCII-ish glyphs.</summary>
internal static class StickFont
{
    public static IReadOnlyList<(int X0, int Y0, int X1, int Y1)> Strokes(char c)
    {
        c = char.ToUpperInvariant(c);
        return c switch
        {
            ' ' => Array.Empty<(int, int, int, int)>(),
            'A' => S((0, 7, 3, 0), (3, 0, 6, 7), (1, 4, 5, 4)),
            'B' => S((0, 0, 0, 7), (0, 0, 4, 0), (4, 0, 5, 1), (5, 1, 5, 2), (5, 2, 4, 3), (4, 3, 0, 3),
                (4, 3, 5, 4), (5, 4, 5, 6), (5, 6, 4, 7), (4, 7, 0, 7)),
            'C' => S((5, 1, 3, 0), (3, 0, 1, 0), (1, 0, 0, 2), (0, 2, 0, 5), (0, 5, 1, 7), (1, 7, 3, 7), (3, 7, 5, 6)),
            'D' => S((0, 0, 0, 7), (0, 0, 3, 0), (3, 0, 5, 2), (5, 2, 5, 5), (5, 5, 3, 7), (3, 7, 0, 7)),
            'E' => S((5, 0, 0, 0), (0, 0, 0, 7), (0, 7, 5, 7), (0, 3, 4, 3)),
            'F' => S((5, 0, 0, 0), (0, 0, 0, 7), (0, 3, 4, 3)),
            'G' => S((5, 1, 3, 0), (3, 0, 1, 0), (1, 0, 0, 2), (0, 2, 0, 5), (0, 5, 1, 7), (1, 7, 3, 7), (3, 7, 5, 5),
                (5, 5, 5, 4), (5, 4, 3, 4)),
            'H' => S((0, 0, 0, 7), (6, 0, 6, 7), (0, 3, 6, 3)),
            'I' => S((1, 0, 5, 0), (3, 0, 3, 7), (1, 7, 5, 7)),
            'J' => S((2, 0, 6, 0), (5, 0, 5, 5), (5, 5, 3, 7), (3, 7, 1, 7), (1, 7, 0, 5)),
            'K' => S((0, 0, 0, 7), (5, 0, 0, 3), (0, 3, 5, 7)),
            'L' => S((0, 0, 0, 7), (0, 7, 5, 7)),
            'M' => S((0, 7, 0, 0), (0, 0, 3, 4), (3, 4, 6, 0), (6, 0, 6, 7)),
            'N' => S((0, 7, 0, 0), (0, 0, 6, 7), (6, 7, 6, 0)),
            'O' => S((1, 0, 4, 0), (4, 0, 6, 2), (6, 2, 6, 5), (6, 5, 4, 7), (4, 7, 1, 7), (1, 7, 0, 5), (0, 5, 0, 2), (0, 2, 1, 0)),
            'P' => S((0, 7, 0, 0), (0, 0, 4, 0), (4, 0, 5, 1), (5, 1, 5, 2), (5, 2, 4, 3), (4, 3, 0, 3)),
            'Q' => S((1, 0, 4, 0), (4, 0, 6, 2), (6, 2, 6, 5), (6, 5, 4, 7), (4, 7, 1, 7), (1, 7, 0, 5), (0, 5, 0, 2), (0, 2, 1, 0),
                (3, 4, 6, 7)),
            'R' => S((0, 7, 0, 0), (0, 0, 4, 0), (4, 0, 5, 1), (5, 1, 5, 2), (5, 2, 4, 3), (4, 3, 0, 3), (2, 3, 5, 7)),
            'S' => S((5, 1, 3, 0), (3, 0, 1, 0), (1, 0, 0, 1), (0, 1, 0, 2), (0, 2, 1, 3), (1, 3, 4, 3), (4, 3, 5, 4),
                (5, 4, 5, 6), (5, 6, 3, 7), (3, 7, 1, 7), (1, 7, 0, 6)),
            'T' => S((0, 0, 6, 0), (3, 0, 3, 7)),
            'U' => S((0, 0, 0, 5), (0, 5, 1, 7), (1, 7, 5, 7), (5, 7, 6, 5), (6, 5, 6, 0)),
            'V' => S((0, 0, 3, 7), (3, 7, 6, 0)),
            'W' => S((0, 0, 1, 7), (1, 7, 3, 3), (3, 3, 5, 7), (5, 7, 6, 0)),
            'X' => S((0, 0, 6, 7), (6, 0, 0, 7)),
            'Y' => S((0, 0, 3, 3), (6, 0, 3, 3), (3, 3, 3, 7)),
            'Z' => S((0, 0, 6, 0), (6, 0, 0, 7), (0, 7, 6, 7)),
            '0' => S((1, 0, 4, 0), (4, 0, 6, 2), (6, 2, 6, 5), (6, 5, 4, 7), (4, 7, 1, 7), (1, 7, 0, 5), (0, 5, 0, 2), (0, 2, 1, 0),
                (1, 6, 5, 1)),
            '1' => S((2, 1, 3, 0), (3, 0, 3, 7), (1, 7, 5, 7)),
            '2' => S((0, 2, 1, 0), (1, 0, 4, 0), (4, 0, 5, 1), (5, 1, 5, 2), (5, 2, 0, 7), (0, 7, 5, 7)),
            '3' => S((0, 1, 1, 0), (1, 0, 4, 0), (4, 0, 5, 1), (5, 1, 5, 2), (5, 2, 3, 3), (3, 3, 5, 4), (5, 4, 5, 6),
                (5, 6, 4, 7), (4, 7, 1, 7), (1, 7, 0, 6)),
            '4' => S((4, 0, 0, 5), (0, 5, 6, 5), (4, 0, 4, 7)),
            '5' => S((5, 0, 0, 0), (0, 0, 0, 3), (0, 3, 4, 3), (4, 3, 5, 4), (5, 4, 5, 6), (5, 6, 3, 7), (3, 7, 1, 7), (1, 7, 0, 6)),
            '6' => S((4, 0, 1, 0), (1, 0, 0, 2), (0, 2, 0, 6), (0, 6, 1, 7), (1, 7, 4, 7), (4, 7, 5, 6), (5, 6, 5, 4),
                (5, 4, 4, 3), (4, 3, 0, 3)),
            '7' => S((0, 0, 5, 0), (5, 0, 2, 7)),
            '8' => S((1, 0, 4, 0), (4, 0, 5, 1), (5, 1, 5, 2), (5, 2, 4, 3), (4, 3, 1, 3), (1, 3, 0, 2), (0, 2, 0, 1),
                (0, 1, 1, 0), (1, 3, 0, 4), (0, 4, 0, 6), (0, 6, 1, 7), (1, 7, 4, 7), (4, 7, 5, 6), (5, 6, 5, 4), (5, 4, 4, 3)),
            '9' => S((5, 4, 1, 4), (1, 4, 0, 3), (0, 3, 0, 1), (0, 1, 1, 0), (1, 0, 4, 0), (4, 0, 5, 1), (5, 1, 5, 5),
                (5, 5, 4, 7), (4, 7, 1, 7)),
            '.' => S((2, 6, 3, 6), (2, 7, 3, 7)),
            ',' => S((2, 6, 3, 6), (3, 6, 2, 8)),
            '!' => S((3, 0, 3, 5), (3, 6, 3, 7)),
            '?' => S((0, 2, 1, 0), (1, 0, 4, 0), (4, 0, 5, 1), (5, 1, 5, 2), (5, 2, 3, 3), (3, 3, 3, 4), (3, 6, 3, 7)),
            '-' => S((1, 3, 5, 3)),
            '_' => S((0, 7, 6, 7)),
            ':' => S((3, 2, 3, 2), (3, 5, 3, 5)),
            '\'' => S((3, 0, 3, 2)),
            '"' => S((2, 0, 2, 2), (4, 0, 4, 2)),
            '/' => S((5, 0, 1, 7)),
            '\\' => S((1, 0, 5, 7)),
            '+' => S((1, 3, 5, 3), (3, 1, 3, 5)),
            '=' => S((1, 2, 5, 2), (1, 5, 5, 5)),
            _ => S((0, 0, 5, 0), (5, 0, 5, 7), (5, 7, 0, 7), (0, 7, 0, 0)), // box for unknown
        };
    }

    private static (int, int, int, int)[] S(params (int, int, int, int)[] segs) => segs;
}
