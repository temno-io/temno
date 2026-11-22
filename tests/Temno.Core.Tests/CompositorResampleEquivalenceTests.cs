using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-214 Scope 1: pins the pixel-exactness decision in <c>docs/verification/wp-214/DECISION.md</c>.
/// The fixed-point bilinear path is NOT bit-identical: max per-channel absolute delta vs the
/// pre-change <c>double</c> algorithm is 2 LSB, and 0 at integer alignments, exact 2x, solid
/// colours, rotation (old path retained) and 1:1 (memcpy path). The reference below is the old
/// algorithm copied verbatim — not the new code calling itself — so this test can actually fail.
/// WP-217 changes the three minification cases to an independent footprint integral; the old
/// reference still guards exact 2x, upscale and rotated 1:1 behavior.
/// </summary>
public sealed class CompositorResampleEquivalenceTests
{
    private static VideoFrame GradientFrame(int w, int h, int seed = 214)
    {
        var random = new Random(seed);
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            data[i] = (byte)((x * 255) / Math.Max(1, w - 1));
            data[i + 1] = (byte)((y * 255) / Math.Max(1, h - 1));
            data[i + 2] = (byte)random.Next(256);
            data[i + 3] = (byte)(128 + random.Next(128));
        }
        return new VideoFrame(w, h, data, "rgba");
    }

    private static VideoFrame SolidFrame(int w, int h, byte r, byte g, byte b, byte a = 255)
    {
        var data = new byte[w * h * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = r; data[i + 1] = g; data[i + 2] = b; data[i + 3] = a;
        }
        return new VideoFrame(w, h, data, "rgba");
    }

    private static int MaxAbsDelta(byte[] actual, byte[] expected)
    {
        Assert.Equal(expected.Length, actual.Length);
        var max = 0;
        for (var i = 0; i < actual.Length; i++)
            max = Math.Max(max, Math.Abs(actual[i] - expected[i]));
        return max;
    }

    private static byte[] ReferenceRender(VideoFrame src, Transform transform, int outW, int outH)
    {
        // Pre-change BlendLayer mapping for a single opaque Normal layer over black, with the
        // pre-change SampleBilinear. Copied from CpuCompositor before WP-214; any drift in the
        // new implementation shows up as a delta here.
        var canvas = new byte[outW * outH * 4];
        for (var i = 3; i < canvas.Length; i += 4) canvas[i] = 255;

        var dstW = transform.Width * outW;
        var dstH = transform.Height * outH;
        var dstCx = transform.CenterX * outW;
        var dstCy = transform.CenterY * outH;
        var hasRotation = Math.Abs(transform.Rotation) > 0.001;
        var cosR = Math.Cos(-transform.Rotation * Math.PI / 180.0);
        var sinR = Math.Sin(-transform.Rotation * Math.PI / 180.0);

        for (var y = 0; y < outH; y++)
        for (var x = 0; x < outW; x++)
        {
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
            if (transform.FlipHorizontal) normX = 1.0 - normX;
            if (transform.FlipVertical) normY = 1.0 - normY;

            SampleBilinearOld(src, normX * src.Width - 0.5, normY * src.Height - 0.5,
                out var sr, out var sg, out var sb, out var sa);
            var alpha = sa / 255.0;
            if (alpha <= 0) continue;
            var idx = (y * outW + x) * 4;
            if (alpha >= 1.0)
            {
                canvas[idx] = sr; canvas[idx + 1] = sg; canvas[idx + 2] = sb; canvas[idx + 3] = 255;
                continue;
            }
            var inv = 1.0 - alpha;
            canvas[idx] = ToByte(sr / 255.0 * alpha + canvas[idx] / 255.0 * inv);
            canvas[idx + 1] = ToByte(sg / 255.0 * alpha + canvas[idx + 1] / 255.0 * inv);
            canvas[idx + 2] = ToByte(sb / 255.0 * alpha + canvas[idx + 2] / 255.0 * inv);
        }
        return canvas;
    }

    private static void SampleBilinearOld(
        VideoFrame src, double sx, double sy, out byte r, out byte g, out byte b, out byte a)
    {
        var bgra = string.Equals(src.PixelFormat, "bgra", StringComparison.OrdinalIgnoreCase);
        var x0 = (int)Math.Floor(sx);
        var y0 = (int)Math.Floor(sy);
        var fx = sx - x0;
        var fy = sy - y0;
        x0 = Math.Clamp(x0, 0, src.Width - 1);
        y0 = Math.Clamp(y0, 0, src.Height - 1);
        var x1 = Math.Min(x0 + 1, src.Width - 1);
        var y1 = Math.Min(y0 + 1, src.Height - 1);

        ReadRgbaOld(src, x0, y0, bgra, out var r00, out var g00, out var b00, out var a00);
        if (x0 == x1 && y0 == y1)
        {
            r = r00; g = g00; b = b00; a = a00;
            return;
        }

        ReadRgbaOld(src, x1, y0, bgra, out var r10, out var g10, out var b10, out var a10);
        ReadRgbaOld(src, x0, y1, bgra, out var r01, out var g01, out var b01, out var a01);
        ReadRgbaOld(src, x1, y1, bgra, out var r11, out var g11, out var b11, out var a11);

        r = LerpByteOld(LerpByteOld(r00, r10, fx), LerpByteOld(r01, r11, fx), fy);
        g = LerpByteOld(LerpByteOld(g00, g10, fx), LerpByteOld(g01, g11, fx), fy);
        b = LerpByteOld(LerpByteOld(b00, b10, fx), LerpByteOld(b01, b11, fx), fy);
        a = LerpByteOld(LerpByteOld(a00, a10, fx), LerpByteOld(a01, a11, fx), fy);
    }

    private static void ReadRgbaOld(
        VideoFrame src, int x, int y, bool bgra, out byte r, out byte g, out byte b, out byte a)
    {
        var i = (y * src.Width + x) * 4;
        r = src.Data[i + (bgra ? 2 : 0)];
        g = src.Data[i + 1];
        b = src.Data[i + (bgra ? 0 : 2)];
        a = src.Data[i + 3];
    }

    private static byte LerpByteOld(byte a, byte b, double t)
        => (byte)Math.Clamp(a + (b - a) * t, 0, 255);

    private static byte ToByte(double value) => (byte)Math.Clamp(value * 255, 0, 255);

    private static int RenderDelta(VideoFrame src, Transform? transform, int outW, int outH)
    {
        var compositor = new CpuCompositor();
        using var _ = src;
        using var result = compositor.Render([new CompositorLayer(src, transform: transform ?? new Transform())], outW, outH);
        var expected = ReferenceRender(src, transform ?? new Transform(), outW, outH);
        return MaxAbsDelta(result.Data, expected);
    }

    [Fact]
    public void Exact2xDownscale_PowerOfTwo_IsBitIdentical()
    {
        // Exact 2x on power-of-two geometry: every sample phase is exactly representable, weight
        // 32768 computes exactly, so the optimisation must not move a single bit here.
        using var src = GradientFrame(128, 128);
        Assert.Equal(0, RenderDelta(src, null, 64, 64));
    }

    [Fact]
    public void Exact2xDownscale_HdRatio_StaysWithinThreeLsb()
    {
        // Same 2x ratio as the headline 4K-on-1080p case but small enough for the suite. The
        // geometry is not power-of-two, so binary phases land a rounding-error off the exact
        // half-integers (see DECISION.md): where the old truncation tips over an integer boundary
        // in all three half-weight stages at once, the delta reaches 3. Rare (4 px in 518K here),
        // invisible, and bounded.
        using var src = GradientFrame(960, 540);
        Assert.InRange(RenderDelta(src, null, 480, 270), 0, 3);
    }

    [Fact]
    public void NonIntegerRatio_UsesIntegratedFootprint()
    {
        using var src = GradientFrame(100, 80);
        using var actual = new CpuCompositor().Render([new(src)], 64, 52);
        Assert.InRange(CompositorMinificationTests.Delta(actual.Data,
            CompositorMinificationTests.Reference(src, new Transform(), 64, 52)), 0, 3);
    }

    [Fact]
    public void UglyUpscaleRatio_StaysWithinTwoLsb()
    {
        using var src = GradientFrame(96, 64);
        // WP-405: 141x94 keeps the 3:2 picture's shape, so the ugly 1.46875 ratio applies on both axes.
        Assert.InRange(RenderDelta(src, null, 141, 94), 0, 2);
    }

    [Fact]
    public void FractionalPlacement_UsesIntegratedFootprint()
    {
        using var src = GradientFrame(128, 96);
        var transform = new Transform { CenterX = 0.513, CenterY = 0.487, Width = 0.77, Height = 0.77 };
        using var actual = new CpuCompositor().Render([new(src, transform: transform)], 160, 120);
        Assert.InRange(CompositorMinificationTests.Delta(actual.Data,
            CompositorMinificationTests.Reference(src, transform, 160, 120)), 0, 3);
    }

    [Fact]
    public void SolidColour_IsBitIdenticalAtAnyRatio()
    {
        using var src = SolidFrame(100, 80, 17, 200, 33);
        Assert.Equal(0, RenderDelta(src, null, 64, 52));
    }

    [Fact]
    public void FlippedResample_UsesIntegratedFootprint()
    {
        using var src = GradientFrame(100, 80);
        var transform = new Transform { FlipHorizontal = true, FlipVertical = true };
        using var actual = new CpuCompositor().Render([new(src, transform: transform)], 64, 52);
        Assert.InRange(CompositorMinificationTests.Delta(actual.Data,
            CompositorMinificationTests.Reference(src, transform, 64, 52)), 0, 3);
    }

    [Fact]
    public void IntegerAlignedFlip_PowerOfTwo_IsBitIdentical()
    {
        // Same-size flip on power-of-two geometry lands every sample on an integer coordinate
        // through exactly-representable arithmetic (weight 0), so the fixed-point path must agree
        // with the old doubles exactly.
        using var src = GradientFrame(64, 64);
        var transform = new Transform { FlipHorizontal = true };
        Assert.Equal(0, RenderDelta(src, transform, 64, 64));
    }

    [Fact]
    public void RotatedResample_KeepsTheOldPathExactly()
    {
        using var src = GradientFrame(64, 64);
        var transform = new Transform { Rotation = 30 };
        Assert.Equal(0, RenderDelta(src, transform, 64, 64));
    }

    [Fact]
    public void OneToOne_MatchesDirectCopy()
    {
        // The 1:1 path memcpys through ReadRgba rather than resampling; for an opaque source the
        // render must equal the source bytes. (Compared against the source, not the bilinear
        // reference: at non-power-of-two sizes binary phases land a rounding-error off the exact
        // integers and the two disagree by up to 2 — a reference artifact, not a product defect.)
        using var src = SolidFrame(64, 48, 17, 200, 33);
        var compositor = new CpuCompositor();
        using var result = compositor.Render([new CompositorLayer(src)], 64, 48);
        Assert.Equal(src.Data, result.Data);
    }
}
