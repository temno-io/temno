using Temno.Core.Editing;
using Temno.Core.Models;

namespace Temno.Media.Compositing;

/// <summary>
/// CPU-based software compositor. Alpha-blends layers bottom-up onto an opaque
/// black canvas at the output resolution. Handles opacity, position/scale via
/// <see cref="Transform"/>, rotation, and flips.
/// Mac: Compositing/CustomVideoCompositor.swift.
/// </summary>
public sealed class CpuCompositor : ICompositor
{
    /// <summary>
    /// Alpha-blends <paramref name="layers"/> bottom-up over an opaque black
    /// canvas and returns a straight-alpha RGBA frame at the given output size.
    /// Layers with opacity ≤ 0 are skipped. Transform values are in normalized
    /// canvas units (0–1): CenterX/CenterY is the center, Width/Height is the
    /// fraction of the canvas the source occupies.
    /// </summary>
    public VideoFrame Render(IReadOnlyList<CompositorLayer> layers, int outputWidth, int outputHeight)
    {
        if (outputWidth <= 0) throw new ArgumentOutOfRangeException(nameof(outputWidth));
        if (outputHeight <= 0) throw new ArgumentOutOfRangeException(nameof(outputHeight));

        // RGBA8 canvas, initialized to opaque black (Mac base canvas).
        var canvas = new byte[outputWidth * outputHeight * 4];
        for (var i = 3; i < canvas.Length; i += 4) canvas[i] = 255; // alpha = 1

        // List is ordered bottom→top, so iterate forward.
        foreach (var layer in layers)
        {
            if (layer.Opacity <= 0) continue;
            BlendLayer(canvas, outputWidth, outputHeight, layer);
        }

        return new VideoFrame(outputWidth, outputHeight, canvas, "rgba");
    }

    private static void BlendLayer(byte[] canvas, int canvasW, int canvasH, CompositorLayer layer)
    {
        var src = layer.Frame;
        if (src.Width <= 0 || src.Height <= 0 || src.Data.Length < src.Width * src.Height * 4) return;

        // WP-405: the picture keeps its own aspect inside the transform box; it is never stretched.
        var t = layer.PlacementIsFinal
            ? layer.Transform
            : ClipPlacement.FitInBox(layer.Transform, src.Width, src.Height, canvasW, canvasH);
        var opacity = Math.Clamp(layer.Opacity, 0, 1);

        // Destination rect in canvas pixels from normalized transform.
        var dstW = t.Width * canvasW;
        var dstH = t.Height * canvasH;
        var dstCx = t.CenterX * canvasW;
        var dstCy = t.CenterY * canvasH;

        // Precompute rotation (degrees → radians, positive = clockwise on screen).
        var hasRotation = Math.Abs(t.Rotation) > 0.001;
        var cosR = Math.Cos(-t.Rotation * Math.PI / 180.0);
        var sinR = Math.Sin(-t.Rotation * Math.PI / 180.0);

        // Scan the bounding box of the destination rectangle.
        int minX, minY, maxX, maxY;
        if (hasRotation)
        {
            // Conservative AABB of the rotated rectangle.
            var hw = dstW / 2; var hh = dstH / 2;
            var bound = Math.Abs(hw * cosR) + Math.Abs(hh * sinR);
            var boundY = Math.Abs(hw * sinR) + Math.Abs(hh * cosR);
            minX = Math.Max(0, (int)(dstCx - bound));
            maxX = Math.Min(canvasW - 1, (int)(dstCx + bound));
            minY = Math.Max(0, (int)(dstCy - boundY));
            maxY = Math.Min(canvasH - 1, (int)(dstCy + boundY));
        }
        else
        {
            minX = Math.Max(0, (int)(dstCx - dstW / 2));
            maxX = Math.Min(canvasW - 1, (int)(dstCx + dstW / 2 - 0.001));
            minY = Math.Max(0, (int)(dstCy - dstH / 2));
            maxY = Math.Min(canvasH - 1, (int)(dstCy + dstH / 2 - 0.001));
        }

        // Resolve the blend mode ONCE. It used to be resolved per pixel inside CpuBlendModes.Apply,
        // which ran a string Trim() plus a sixteen-arm string switch two million times per 1080p
        // frame.
        var blendMode = CpuBlendModes.Parse(layer.BlendMode);
        var bgra = string.Equals(src.PixelFormat, "bgra", StringComparison.OrdinalIgnoreCase);

        // A layer at 1:1 scale, axis-aligned on integer pixel boundaries, samples exactly on source
        // pixel centres: the bilinear weights are 0 and it returns the source pixel unchanged. That
        // is the single-full-frame-clip case — the most common export there is — and it was paying
        // for four neighbour fetches and six lerps per pixel to copy bytes. `sourceOffset*` maps a
        // canvas pixel straight to its source pixel; `oneToOne` says that mapping is exact.
        var offsetX = dstCx - dstW / 2;
        var offsetY = dstCy - dstH / 2;
        var oneToOne =
            !hasRotation && !t.FlipHorizontal && !t.FlipVertical
            && Math.Abs(dstW - src.Width) < 1e-6 && Math.Abs(dstH - src.Height) < 1e-6
            && Math.Abs(offsetX - Math.Round(offsetX)) < 1e-6
            && Math.Abs(offsetY - Math.Round(offsetY)) < 1e-6;
        var sourceOffsetX = (int)Math.Round(offsetX);
        var sourceOffsetY = (int)Math.Round(offsetY);

        // The axis-aligned resample below is compute-bound and runs on its own fast path with
        // fixed-point weights and parallel scanlines (BlendLayerResampleFast). What follows handles
        // the 1:1 copy and the rotated resample, both serial.
        if (!oneToOne && !hasRotation)
        {
            BlendLayerResampleFast(
                canvas, canvasW, minX, minY, maxX, maxY,
                dstCx, dstCy, dstW, dstH, t.FlipHorizontal, t.FlipVertical,
                src, bgra, opacity, blendMode);
            return;
        }

        // NOTE on parallelism: scanline parallelism was measured and reverted ON THE oneToOne
        // PATH ONLY. On a 14-core machine a 4K single-layer 1:1 composite went from 43.0 ms to
        // 44.0 ms (best of 5, idle) — that loop streams a 33 MB source and a 33 MB canvas, so it
        // is memory-bound and extra cores do not help. The bilinear resample is compute-bound
        // and DOES scale with cores (see BlendLayerResampleFast); do not extend this note to it.
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                // Map canvas pixel back to normalized source coordinates.
                var relX = x + 0.5 - dstCx;
                var relY = y + 0.5 - dstCy;

                double normX, normY;
                if (hasRotation)
                {
                    var rx = relX * cosR - relY * sinR;
                    var ry = relX * sinR + relY * cosR;
                    normX = rx / dstW + 0.5;
                    normY = ry / dstH + 0.5;
                }
                else
                {
                    normX = relX / dstW + 0.5;
                    normY = relY / dstH + 0.5;
                }

                if (normX < 0 || normX >= 1 || normY < 0 || normY >= 1) continue;

                // Apply flips.
                if (t.FlipHorizontal) normX = 1.0 - normX;
                if (t.FlipVertical) normY = 1.0 - normY;

                byte sr, sg, sb, sa;
                if (oneToOne)
                {
                    // Exactly on a source pixel centre — bit-identical to the bilinear result.
                    ReadRgba(src, x - sourceOffsetX, y - sourceOffsetY, bgra, out sr, out sg, out sb, out sa);
                }
                else
                {
                    // Bilinear sample (Mac Core Image interpolates; nearest made 720p look like SD).
                    var srcXf = normX * src.Width - 0.5;
                    var srcYf = normY * src.Height - 0.5;
                    if (Minification.NeedsRotatedArea(src.Width, src.Height, dstW, dstH))
                    {
                        var projection = Math.Abs(cosR) + Math.Abs(sinR);
                        Minification.Sample(src, bgra,
                            Minification.Axis(srcXf + 0.5, src.Width / dstW * projection, src.Width),
                            Minification.Axis(srcYf + 0.5, src.Height / dstH * projection, src.Height),
                            out var ar, out var ag, out var ab, out var aa);
                        sr = (byte)ar; sg = (byte)ag; sb = (byte)ab; sa = (byte)aa;
                    }
                    else
                        SampleBilinear(src, bgra, srcXf, srcYf, out sr, out sg, out sb, out sa);
                }

                // Combined alpha = source alpha × layer opacity.
                var alpha = (sa / 255.0) * opacity;
                if (alpha <= 0) continue;

                var canvasIdx = (y * canvasW + x) * 4;

                // Fully opaque source over the canvas in Normal mode resolves to the source pixel,
                // so skip the background read, the blend and the float round trip entirely.
                if (alpha >= 1.0 && blendMode == CpuBlendModes.Kind.Normal)
                {
                    canvas[canvasIdx] = sr;
                    canvas[canvasIdx + 1] = sg;
                    canvas[canvasIdx + 2] = sb;
                    canvas[canvasIdx + 3] = 255;
                    continue;
                }

                var br = canvas[canvasIdx] / 255.0;
                var bg = canvas[canvasIdx + 1] / 255.0;
                var bb = canvas[canvasIdx + 2] / 255.0;
                var (blendR, blendG, blendB) = CpuBlendModes.Apply(
                    blendMode, br, bg, bb, sr / 255.0, sg / 255.0, sb / 255.0);

                // Normal mode is Porter-Duff source-over. For every non-normal mode Mac first
                // computes the blend result, then fades it back to the accumulated background by
                // clip opacity. With the opaque canvas the same result is this factor.
                var invAlpha = 1.0 - alpha;
                canvas[canvasIdx] = ToByte(blendR * alpha + br * invAlpha);
                canvas[canvasIdx + 1] = ToByte(blendG * alpha + bg * invAlpha);
                canvas[canvasIdx + 2] = ToByte(blendB * alpha + bb * invAlpha);
                canvas[canvasIdx + 3] = 255; // canvas stays opaque
            }
        }
    }

    private static void SampleBilinear(
        VideoFrame src, bool bgra, double sx, double sy, out byte r, out byte g, out byte b, out byte a)
    {
        var x0 = (int)Math.Floor(sx);
        var y0 = (int)Math.Floor(sy);
        var fx = sx - x0;
        var fy = sy - y0;
        x0 = Math.Clamp(x0, 0, src.Width - 1);
        y0 = Math.Clamp(y0, 0, src.Height - 1);
        var x1 = Math.Min(x0 + 1, src.Width - 1);
        var y1 = Math.Min(y0 + 1, src.Height - 1);

        ReadRgba(src, x0, y0, bgra, out var r00, out var g00, out var b00, out var a00);
        if (x0 == x1 && y0 == y1)
        {
            r = r00; g = g00; b = b00; a = a00;
            return;
        }

        ReadRgba(src, x1, y0, bgra, out var r10, out var g10, out var b10, out var a10);
        ReadRgba(src, x0, y1, bgra, out var r01, out var g01, out var b01, out var a01);
        ReadRgba(src, x1, y1, bgra, out var r11, out var g11, out var b11, out var a11);

        r = LerpByte(LerpByte(r00, r10, fx), LerpByte(r01, r11, fx), fy);
        g = LerpByte(LerpByte(g00, g10, fx), LerpByte(g01, g11, fx), fy);
        b = LerpByte(LerpByte(b00, b10, fx), LerpByte(b01, b11, fx), fy);
        a = LerpByte(LerpByte(a00, a10, fx), LerpByte(a01, a11, fx), fy);
    }

    private static void ReadRgba(
        VideoFrame src, int x, int y, bool bgra, out byte r, out byte g, out byte b, out byte a)
    {
        var i = (y * src.Width + x) * 4;
        r = src.Data[i + (bgra ? 2 : 0)];
        g = src.Data[i + 1];
        b = src.Data[i + (bgra ? 0 : 2)];
        a = src.Data[i + 3];
    }

    private static byte LerpByte(byte a, byte b, double t)
        => (byte)Math.Clamp(a + (b - a) * t, 0, 255);

    /// <summary>
    /// Fixed-point bilinear weight scale: 16 fractional bits. Weight 0, weight 32768 (exact 2x)
    /// and weight 65536 are exact, so integer-aligned samples render bit-identically to the
    /// <c>double</c> path; every other weight is within half a step (~0.002 LSB of channel value).
    /// Pixel-exactness decision: <c>docs/verification/wp-214/DECISION.md</c>.
    /// </summary>
    private const int BilinearWeightScale = 65536;
    private const int BilinearWeightShift = 16;

    /// <summary>
    /// Row counts below this render serially: threadpool dispatch costs more than it saves, and
    /// the tiny-geometry unit tests stay on one thread.
    /// </summary>
    private const int ParallelRowThreshold = 64;

    /// <summary>
    /// Axis-aligned bilinear resample: the compute-bound path the 4K-on-1080p export spends its
    /// time in. Column and row weights are computed once per frame (the mapping is separable
    /// without rotation), each channel lerps in integers, and rows run under
    /// <see cref="Parallel.For"/>. Rows are disjoint canvas ranges over read-only source data, so
    /// the bytes do not depend on scheduling. The rotated resample keeps the <c>double</c> path.
    /// </summary>
    private static void BlendLayerResampleFast(
        byte[] canvas, int canvasW,
        int minX, int minY, int maxX, int maxY,
        double dstCx, double dstCy, double dstW, double dstH,
        bool flipHorizontal, bool flipVertical,
        VideoFrame src, bool bgra, double opacity, CpuBlendModes.Kind blendMode)
    {
        var cols = maxX - minX + 1;
        var rows = maxY - minY + 1;
        if (cols <= 0 || rows <= 0) return;

        var area = Minification.NeedsArea(src.Width, dstW, dstCx - dstW / 2)
            || Minification.NeedsArea(src.Height, dstH, dstCy - dstH / 2);
        var areaColumns = area ? new Minification.Kernel[cols] : null;
        var areaRows = area ? new Minification.Kernel[rows] : null;

        // Per-column source indices and quantized weights. -1 marks a column outside [0, 1),
        // replicating the bounds check (which runs before the flip) in the same order.
        var colX0 = new int[cols];
        var colX1 = new int[cols];
        var colW = new int[cols];
        for (var xi = 0; xi < cols; xi++)
        {
            var relX = (minX + xi) + 0.5 - dstCx;
            var normX = relX / dstW + 0.5;
            if (normX < 0 || normX >= 1)
            {
                colX0[xi] = -1;
                continue;
            }
            if (flipHorizontal) normX = 1.0 - normX;
            var srcXf = normX * src.Width - 0.5;
            var x0raw = (int)Math.Floor(srcXf);
            colX0[xi] = Math.Clamp(x0raw, 0, src.Width - 1);
            colX1[xi] = Math.Min(colX0[xi] + 1, src.Width - 1);
            colW[xi] = QuantizeWeight(srcXf - x0raw);
            if (areaColumns is not null)
                areaColumns[xi] = Minification.Axis(srcXf + 0.5, src.Width / dstW, src.Width);
        }

        var rowY0 = new int[rows];
        var rowY1 = new int[rows];
        var rowW = new int[rows];
        for (var ri = 0; ri < rows; ri++)
        {
            var relY = (minY + ri) + 0.5 - dstCy;
            var normY = relY / dstH + 0.5;
            if (normY < 0 || normY >= 1)
            {
                rowY0[ri] = -1;
                continue;
            }
            if (flipVertical) normY = 1.0 - normY;
            var srcYf = normY * src.Height - 0.5;
            var y0raw = (int)Math.Floor(srcYf);
            rowY0[ri] = Math.Clamp(y0raw, 0, src.Height - 1);
            rowY1[ri] = Math.Min(rowY0[ri] + 1, src.Height - 1);
            rowW[ri] = QuantizeWeight(srcYf - y0raw);
            if (areaRows is not null)
                areaRows[ri] = Minification.Axis(srcYf + 0.5, src.Height / dstH, src.Height);
        }

        var srcData = src.Data;
        var srcW = src.Width;
        var rOff = bgra ? 2 : 0;
        var bOff = bgra ? 0 : 2;

        void RenderRow(int y)
        {
            var ri = y - minY;
            var y0 = rowY0[ri];
            if (y0 < 0) return;
            var y1 = rowY1[ri];
            var wy = rowW[ri];
            var y0Base = y0 * srcW * 4;
            var y1Base = y1 * srcW * 4;
            var rowSingle = y0 == y1; // edge-clamped row: the vertical lerp is the identity
            for (var xi = 0; xi < cols; xi++)
            {
                var x0 = colX0[xi];
                if (x0 < 0) continue;
                var x1 = colX1[xi];
                var wx = colW[xi];
                var x0o4 = x0 * 4;
                var x1o4 = x1 * 4;

                int sr, sg, sb, sa;
                if (areaColumns is not null)
                {
                    Minification.Sample(src, bgra, areaColumns[xi], areaRows![ri], out sr, out sg, out sb, out sa);
                }
                else if (rowSingle)
                {
                    sr = LerpFixed(srcData[y0Base + x0o4 + rOff], srcData[y0Base + x1o4 + rOff], wx);
                    sg = LerpFixed(srcData[y0Base + x0o4 + 1], srcData[y0Base + x1o4 + 1], wx);
                    sb = LerpFixed(srcData[y0Base + x0o4 + bOff], srcData[y0Base + x1o4 + bOff], wx);
                    sa = LerpFixed(srcData[y0Base + x0o4 + 3], srcData[y0Base + x1o4 + 3], wx);
                }
                else
                {
                    var t0r = LerpFixed(srcData[y0Base + x0o4 + rOff], srcData[y0Base + x1o4 + rOff], wx);
                    var t0g = LerpFixed(srcData[y0Base + x0o4 + 1], srcData[y0Base + x1o4 + 1], wx);
                    var t0b = LerpFixed(srcData[y0Base + x0o4 + bOff], srcData[y0Base + x1o4 + bOff], wx);
                    var t0a = LerpFixed(srcData[y0Base + x0o4 + 3], srcData[y0Base + x1o4 + 3], wx);
                    var t1r = LerpFixed(srcData[y1Base + x0o4 + rOff], srcData[y1Base + x1o4 + rOff], wx);
                    var t1g = LerpFixed(srcData[y1Base + x0o4 + 1], srcData[y1Base + x1o4 + 1], wx);
                    var t1b = LerpFixed(srcData[y1Base + x0o4 + bOff], srcData[y1Base + x1o4 + bOff], wx);
                    var t1a = LerpFixed(srcData[y1Base + x0o4 + 3], srcData[y1Base + x1o4 + 3], wx);
                    sr = LerpFixed(t0r, t1r, wy);
                    sg = LerpFixed(t0g, t1g, wy);
                    sb = LerpFixed(t0b, t1b, wy);
                    sa = LerpFixed(t0a, t1a, wy);
                }

                // What follows is the shared per-pixel tail, unchanged: combined alpha, the
                // opaque-Normal fast copy, then the blend over the accumulated background.
                var alpha = (sa / 255.0) * opacity;
                if (alpha <= 0) continue;

                var canvasIdx = (y * canvasW + minX + xi) * 4;

                if (alpha >= 1.0 && blendMode == CpuBlendModes.Kind.Normal)
                {
                    canvas[canvasIdx] = (byte)sr;
                    canvas[canvasIdx + 1] = (byte)sg;
                    canvas[canvasIdx + 2] = (byte)sb;
                    canvas[canvasIdx + 3] = 255;
                    continue;
                }

                var br = canvas[canvasIdx] / 255.0;
                var bg = canvas[canvasIdx + 1] / 255.0;
                var bb = canvas[canvasIdx + 2] / 255.0;
                var (blendR, blendG, blendB) = CpuBlendModes.Apply(
                    blendMode, br, bg, bb, sr / 255.0, sg / 255.0, sb / 255.0);

                var invAlpha = 1.0 - alpha;
                canvas[canvasIdx] = ToByte(blendR * alpha + br * invAlpha);
                canvas[canvasIdx + 1] = ToByte(blendG * alpha + bg * invAlpha);
                canvas[canvasIdx + 2] = ToByte(blendB * alpha + bb * invAlpha);
                canvas[canvasIdx + 3] = 255; // canvas stays opaque
            }
        }

        if (rows >= ParallelRowThreshold && Environment.ProcessorCount > 1)
            Parallel.For(minY, maxY + 1, RenderRow);
        else
            for (var y = minY; y <= maxY; y++) RenderRow(y);
    }

    private static int QuantizeWeight(double frac)
    {
        var w = (int)(frac * BilinearWeightScale + 0.5);
        return w < 0 ? 0 : w > BilinearWeightScale ? BilinearWeightScale : w;
    }

    private static int LerpFixed(int a, int b, int w)
        => (a * (BilinearWeightScale - w) + b * w) >> BilinearWeightShift;

    private static byte ToByte(double value) => (byte)Math.Clamp(value * 255, 0, 255);
}

// Shared source-footprint arithmetic for CPU samples and the GPU's integer tap tables.
internal static class Minification
{
    internal const int Scale = 65536;

    internal readonly record struct Kernel(int First, int Last, double Left, double Width, int BilinearWeight = -1)
    {
        internal int Weight(int index)
        {
            if (BilinearWeight >= 0)
                return First == Last ? Scale : index == First ? Scale - BilinearWeight : BilinearWeight;
            if (double.IsPositiveInfinity(Width))
                return First == Last ? Scale : index == First || index == Last ? Scale / 2 : 0;
            var previous = index == First ? 0 : Quantize((index - Left) / Width);
            var next = index == Last ? Scale : Quantize((index + 1 - Left) / Width);
            return next - previous;
        }
    }

    internal static bool NeedsArea(int source, double destination, double offset)
        => source > destination && destination > 0
            && !(Math.Abs(source - 2 * destination) < 1e-6 && Math.Abs(offset - Math.Round(offset)) < 1e-6);

    internal static bool NeedsRotatedArea(int width, int height, double dstW, double dstH)
        => dstW > 0 && dstH > 0 && (width > dstW || height > dstH);

    internal static Kernel Axis(double center, double width, int extent)
    {
        if (width <= 1)
        {
            var raw = (int)Math.Floor(center - 0.5);
            var first = Math.Clamp(raw, 0, extent - 1);
            return new(first, Math.Min(first + 1, extent - 1), 0, 0, Quantize(center - 0.5 - raw));
        }
        var left = center - width / 2;
        var right = center + width / 2;
        return new((int)Math.Clamp(Math.Floor(left), 0, extent - 1),
            (int)Math.Clamp(Math.Ceiling(right) - 1, 0, extent - 1), left, width);
    }

    internal static int Quantize(double value) => (int)Math.Clamp(value * Scale + 0.5, 0, Scale);

    internal static void Sample(VideoFrame src, bool bgra, Kernel x, Kernel y,
        out int r, out int g, out int b, out int a)
    {
        int vr = 0, vg = 0, vb = 0, va = 0;
        var bytes = src.Data;
        for (var sy = y.First; sy <= y.Last; sy++)
        {
            var wy = y.Weight(sy);
            if (wy == 0) continue;
            int hr = 0, hg = 0, hb = 0, ha = 0;
            var row = sy * src.Width * 4;
            for (var sx = x.First; sx <= x.Last; sx++)
            {
                var wx = x.Weight(sx);
                var i = row + sx * 4;
                hr += bytes[i] * wx; hg += bytes[i + 1] * wx;
                hb += bytes[i + 2] * wx; ha += bytes[i + 3] * wx;
            }
            vr += (hr >> 16) * wy; vg += (hg >> 16) * wy;
            vb += (hb >> 16) * wy; va += (ha >> 16) * wy;
        }
        r = (bgra ? vb : vr) >> 16; g = vg >> 16;
        b = (bgra ? vr : vb) >> 16; a = va >> 16;
    }
}
