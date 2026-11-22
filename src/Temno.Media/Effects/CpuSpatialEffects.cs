using System.Runtime.CompilerServices;

namespace Temno.Media.Effects;

/// <summary>
/// Frame-level CPU effects for WP-103 (blur / stylize / detail / chroma key).
/// Operates on straight-alpha RGBA/BGRA buffers; does not allocate VideoFrame.
/// </summary>
public static class CpuSpatialEffects
{
    private const double RedLuma = 0.2126;
    private const double GreenLuma = 0.7152;
    private const double BlueLuma = 0.0722;

    public static void GaussianBlur(byte[] data, int w, int h, int redIndex, int blueIndex, double radius)
    {
        var r = ClampRadius(radius, w, h);
        if (r <= 0) return;
        // Separable box blur ×3 approximates Gaussian.
        BoxBlur(data, w, h, redIndex, blueIndex, r);
        BoxBlur(data, w, h, redIndex, blueIndex, r);
        BoxBlur(data, w, h, redIndex, blueIndex, r);
    }

    public static void Sharpen(byte[] data, int w, int h, int redIndex, int blueIndex, double amount)
    {
        amount = Math.Clamp(amount, 0, 2);
        if (amount <= 1e-6) return;
        var blur = (byte[])data.Clone();
        BoxBlur(blur, w, h, redIndex, blueIndex, 1);
        for (var i = 0; i < data.Length; i += 4)
        {
            for (var c = 0; c < 3; c++)
            {
                var idx = i + (c == 0 ? redIndex : c == 2 ? blueIndex : 1);
                var o = data[idx] / 255.0;
                var b = blur[idx] / 255.0;
                data[idx] = ToByte(o + amount * (o - b));
            }
        }
    }

    public static void NoiseReduction(byte[] data, int w, int h, int redIndex, int blueIndex, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        if (amount <= 1e-6) return;
        var blur = (byte[])data.Clone();
        var r = Math.Max(1, (int)Math.Round(1 + amount * 2));
        BoxBlur(blur, w, h, redIndex, blueIndex, r);
        for (var i = 0; i < data.Length; i += 4)
        {
            for (var c = 0; c < 4; c++)
            {
                var o = data[i + c] / 255.0;
                var b = blur[i + c] / 255.0;
                data[i + c] = ToByte(o * (1 - amount) + b * amount);
            }
        }
    }

    public static void MotionBlur(byte[] data, int w, int h, int redIndex, int blueIndex, double radius, double angleDeg)
    {
        var r = ClampRadius(radius, w, h);
        if (r <= 0) return;
        var rad = angleDeg * Math.PI / 180.0;
        var dx = Math.Cos(rad);
        var dy = -Math.Sin(rad); // screen Y down
        var src = (byte[])data.Clone();
        var samples = Math.Max(3, r * 2 + 1);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            double sr = 0, sg = 0, sb = 0, sa = 0;
            for (var s = 0; s < samples; s++)
            {
                var t = (s / (double)(samples - 1) - 0.5) * 2 * r;
                var sx = (int)Math.Round(x + dx * t);
                var sy = (int)Math.Round(y + dy * t);
                sx = Math.Clamp(sx, 0, w - 1);
                sy = Math.Clamp(sy, 0, h - 1);
                var i = (sy * w + sx) * 4;
                sr += src[i + redIndex];
                sg += src[i + 1];
                sb += src[i + blueIndex];
                sa += src[i + 3];
            }
            var o = (y * w + x) * 4;
            data[o + redIndex] = (byte)Math.Round(sr / samples);
            data[o + 1] = (byte)Math.Round(sg / samples);
            data[o + blueIndex] = (byte)Math.Round(sb / samples);
            data[o + 3] = (byte)Math.Round(sa / samples);
        }
    }

    public static void Grain(byte[] data, int w, int h, int redIndex, int blueIndex, double amount, double size, int frame)
    {
        amount = Math.Clamp(amount, 0, 1);
        if (amount <= 1e-6) return;
        size = Math.Clamp(size, 0.5, 4);
        var cell = Math.Max(1, (int)Math.Round(size));
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var n = HashNoise(x / cell, y / cell, frame) * 2 - 1; // -1..1
            var o = (y * w + x) * 4;
            var a = data[o + 3] / 255.0;
            if (a <= 0) continue;
            var boost = n * amount * 0.25;
            data[o + redIndex] = ToByte(data[o + redIndex] / 255.0 + boost);
            data[o + 1] = ToByte(data[o + 1] / 255.0 + boost);
            data[o + blueIndex] = ToByte(data[o + blueIndex] / 255.0 + boost);
        }
    }

    public static void Vignette(
        byte[] data, int w, int h, int redIndex, int blueIndex,
        double amount, double midpoint, double roundness, double feather)
    {
        if (Math.Abs(amount) < 1e-6) return;
        midpoint = Math.Clamp(midpoint, 0, 1);
        feather = Math.Clamp(feather, 0, 1);
        roundness = Math.Clamp(roundness, -1, 1);
        var cx = (w - 1) / 2.0;
        var cy = (h - 1) / 2.0;
        var aspect = w / (double)Math.Max(1, h);
        // Roundness: >0 more circular relative to frame; <0 more rectangular.
        var rx = cx * (1 + Math.Max(0, -roundness) * 0.5);
        var ry = cy * (1 + Math.Max(0, -roundness) * 0.5);
        if (roundness > 0)
        {
            rx = Math.Max(rx, ry * aspect) * (1 - roundness * 0.2);
            ry = Math.Max(ry, rx / aspect) * (1 - roundness * 0.2);
        }
        var inner = midpoint;
        var outer = Math.Clamp(midpoint + Math.Max(0.05, feather), 0, 1.5);

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var nx = (x - cx) / Math.Max(1e-6, rx);
            var ny = (y - cy) / Math.Max(1e-6, ry);
            var d = Math.Sqrt(nx * nx + ny * ny);
            var t = Smoothstep(inner, outer, d);
            var mul = amount >= 0 ? 1 - amount * t : 1 + (-amount) * t; // negative amount = edge brighten
            var o = (y * w + x) * 4;
            data[o + redIndex] = ToByte(data[o + redIndex] / 255.0 * mul);
            data[o + 1] = ToByte(data[o + 1] / 255.0 * mul);
            data[o + blueIndex] = ToByte(data[o + blueIndex] / 255.0 * mul);
        }
    }

    public static void Glow(
        byte[] data, int w, int h, int redIndex, int blueIndex,
        double intensity, double radius, double threshold, double warmth)
    {
        intensity = Math.Clamp(intensity, 0, 1);
        if (intensity <= 1e-6) return;
        threshold = Math.Clamp(threshold, 0, 1);
        warmth = Math.Clamp(warmth, 0, 1);
        var bright = new byte[data.Length];
        for (var i = 0; i < data.Length; i += 4)
        {
            var r = data[i + redIndex] / 255.0;
            var g = data[i + 1] / 255.0;
            var b = data[i + blueIndex] / 255.0;
            var luma = r * RedLuma + g * GreenLuma + b * BlueLuma;
            var m = Smoothstep(threshold, Math.Min(1, threshold + 0.2), luma) * intensity;
            bright[i + redIndex] = ToByte(r * m * (1 + warmth * 0.3));
            bright[i + 1] = ToByte(g * m * (1 + warmth * 0.1));
            bright[i + blueIndex] = ToByte(b * m * (1 - warmth * 0.2));
            bright[i + 3] = data[i + 3];
        }
        var rpx = ClampRadius(radius, w, h);
        if (rpx > 0)
        {
            BoxBlur(bright, w, h, redIndex, blueIndex, rpx);
            BoxBlur(bright, w, h, redIndex, blueIndex, rpx);
        }
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i + redIndex] = ToByte(data[i + redIndex] / 255.0 + bright[i + redIndex] / 255.0);
            data[i + 1] = ToByte(data[i + 1] / 255.0 + bright[i + 1] / 255.0);
            data[i + blueIndex] = ToByte(data[i + blueIndex] / 255.0 + bright[i + blueIndex] / 255.0);
        }
    }

    public static void Clarity(
        byte[] data, int w, int h, int redIndex, int blueIndex, double clarity, double dehaze)
    {
        clarity = Math.Clamp(clarity, -1, 1);
        dehaze = Math.Clamp(dehaze, -1, 1);
        if (Math.Abs(clarity) > 1e-6)
        {
            var blur = (byte[])data.Clone();
            BoxBlur(blur, w, h, redIndex, blueIndex, 2);
            BoxBlur(blur, w, h, redIndex, blueIndex, 2);
            for (var i = 0; i < data.Length; i += 4)
            {
                for (var c = 0; c < 3; c++)
                {
                    var idx = i + (c == 0 ? redIndex : c == 2 ? blueIndex : 1);
                    var o = data[idx] / 255.0;
                    var b = blur[idx] / 255.0;
                    data[idx] = ToByte(o + clarity * (o - b));
                }
            }
        }
        if (Math.Abs(dehaze) > 1e-6)
        {
            // Simple dehaze: lift contrast + mid sat.
            var contrast = 1 + dehaze * 0.35;
            var sat = 1 + dehaze * 0.2;
            for (var i = 0; i < data.Length; i += 4)
            {
                var r = data[i + redIndex] / 255.0;
                var g = data[i + 1] / 255.0;
                var b = data[i + blueIndex] / 255.0;
                r = Clamp01((r - 0.5) * contrast + 0.5);
                g = Clamp01((g - 0.5) * contrast + 0.5);
                b = Clamp01((b - 0.5) * contrast + 0.5);
                var luma = r * RedLuma + g * GreenLuma + b * BlueLuma;
                r = Clamp01(luma + (r - luma) * sat);
                g = Clamp01(luma + (g - luma) * sat);
                b = Clamp01(luma + (b - luma) * sat);
                data[i + redIndex] = ToByte(r);
                data[i + 1] = ToByte(g);
                data[i + blueIndex] = ToByte(b);
            }
        }
    }

    /// <summary>Chroma key: reduces alpha where hue is near keyHue. Also despill toward neutral.</summary>
    public static void ChromaKey(
        byte[] data, int w, int h, int redIndex, int blueIndex,
        double keyHue, double tolerance, double softness, double spill)
    {
        keyHue = keyHue - Math.Floor(keyHue); // 0..1
        tolerance = Math.Clamp(tolerance, 0, 1);
        softness = Math.Clamp(softness, 0, 1);
        spill = Math.Clamp(spill, 0, 1);
        var t0 = tolerance * 0.5;
        var t1 = tolerance * 0.5 + Math.Max(0.01, softness * 0.5);

        for (var i = 0; i < data.Length; i += 4)
        {
            var r = data[i + redIndex] / 255.0;
            var g = data[i + 1] / 255.0;
            var b = data[i + blueIndex] / 255.0;
            RgbToHsv(r, g, b, out var hHue, out var s, out var v);
            if (s < 0.08 || v < 0.05)
                continue; // leave neutrals
            var dh = HueDistance(hHue, keyHue);
            var mask = 1 - Smoothstep(t0, t1, dh); // 1 = keyed out
            if (mask <= 0) continue;
            var a = data[i + 3] / 255.0;
            data[i + 3] = ToByte(a * (1 - mask));
            if (spill > 0 && mask > 0)
            {
                // Pull keyed channel toward luma
                var luma = r * RedLuma + g * GreenLuma + b * BlueLuma;
                var k = spill * mask;
                r = r * (1 - k) + luma * k;
                g = g * (1 - k) + luma * k;
                b = b * (1 - k) + luma * k;
                data[i + redIndex] = ToByte(r);
                data[i + 1] = ToByte(g);
                data[i + blueIndex] = ToByte(b);
            }
        }
    }

    // --- helpers ---

    private static void BoxBlur(byte[] data, int w, int h, int redIndex, int blueIndex, int radius)
    {
        if (radius <= 0) return;
        var tmp = new byte[data.Length];
        // Horizontal
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                int sr = 0, sg = 0, sb = 0, sa = 0, n = 0;
                var x0 = Math.Max(0, x - radius);
                var x1 = Math.Min(w - 1, x + radius);
                for (var xx = x0; xx <= x1; xx++)
                {
                    var i = (y * w + xx) * 4;
                    sr += data[i + redIndex];
                    sg += data[i + 1];
                    sb += data[i + blueIndex];
                    sa += data[i + 3];
                    n++;
                }
                var o = (y * w + x) * 4;
                tmp[o + redIndex] = (byte)(sr / n);
                tmp[o + 1] = (byte)(sg / n);
                tmp[o + blueIndex] = (byte)(sb / n);
                tmp[o + 3] = (byte)(sa / n);
            }
        }
        // Vertical
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                int sr = 0, sg = 0, sb = 0, sa = 0, n = 0;
                var y0 = Math.Max(0, y - radius);
                var y1 = Math.Min(h - 1, y + radius);
                for (var yy = y0; yy <= y1; yy++)
                {
                    var i = (yy * w + x) * 4;
                    sr += tmp[i + redIndex];
                    sg += tmp[i + 1];
                    sb += tmp[i + blueIndex];
                    sa += tmp[i + 3];
                    n++;
                }
                var o = (y * w + x) * 4;
                data[o + redIndex] = (byte)(sr / n);
                data[o + 1] = (byte)(sg / n);
                data[o + blueIndex] = (byte)(sb / n);
                data[o + 3] = (byte)(sa / n);
            }
        }
    }

    private static int ClampRadius(double radius, int w, int h)
    {
        if (radius <= 0) return 0;
        var r = (int)Math.Round(radius);
        // Cap for perf; Mac allows 100 but small frames don't need it.
        var max = Math.Max(1, Math.Min(64, Math.Max(w, h) / 2));
        return Math.Clamp(r, 0, max);
    }

    private static double HashNoise(int x, int y, int frame)
    {
        unchecked
        {
            var n = (uint)(x * 374761393 + y * 668265263 + frame * 982451653);
            n = (n ^ (n >> 13)) * 1274126177u;
            n ^= n >> 16;
            return (n & 0xFFFFFF) / (double)0xFFFFFF;
        }
    }

    private static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        v = max;
        var d = max - min;
        s = max <= 1e-9 ? 0 : d / max;
        if (d <= 1e-9) { h = 0; return; }
        if (max == r) h = (((g - b) / d) % 6) / 6;
        else if (max == g) h = ((b - r) / d + 2) / 6;
        else h = ((r - g) / d + 4) / 6;
        if (h < 0) h += 1;
    }

    private static double HueDistance(double a, double b)
    {
        var d = Math.Abs(a - b);
        return Math.Min(d, 1 - d);
    }

    private static double Smoothstep(double edge0, double edge1, double x)
    {
        var t = Clamp01((x - edge0) / Math.Max(1e-9, edge1 - edge0));
        return t * t * (3 - 2 * t);
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0, 1);

    private static byte ToByte(double value)
        => (byte)Math.Clamp((int)Math.Round(Clamp01(value) * 255.0, MidpointRounding.AwayFromZero), 0, 255);
}
