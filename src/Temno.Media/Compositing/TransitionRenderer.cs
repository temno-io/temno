using Temno.Core.Models;

namespace Temno.Media.Compositing;

/// <summary>
/// Pure-CPU transition blend between two layers. Operates on straight-alpha RGBA
/// byte arrays; transform/opacity on the input layers are ignored (caller should
/// pre-composite each side to timeline resolution when needed).
/// </summary>
public static class TransitionRenderer
{
    /// <summary>
    /// Blends outgoing clip A with incoming clip B at normalized
    /// <paramref name="progress"/> in [0, 1]. Returns a new straight-alpha RGBA frame
    /// sized to the larger of the two inputs.
    /// </summary>
    public static VideoFrame Render(
        CompositorLayer outgoing,
        CompositorLayer incoming,
        TransitionType type,
        double progress)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);

        var a = outgoing.Frame;
        var b = incoming.Frame;
        if (a.Width <= 0 || a.Height <= 0)
            throw new ArgumentException("Outgoing frame must have positive dimensions.", nameof(outgoing));
        if (b.Width <= 0 || b.Height <= 0)
            throw new ArgumentException("Incoming frame must have positive dimensions.", nameof(incoming));

        var p = Math.Clamp(progress, 0.0, 1.0);
        var width = Math.Max(a.Width, b.Width);
        var height = Math.Max(a.Height, b.Height);
        var dst = new byte[width * height * 4];

        switch (type)
        {
            case TransitionType.CrossDissolve:
                CrossDissolve(a, b, dst, width, height, p);
                break;
            case TransitionType.FadeToBlack:
                FadeThroughColor(a, b, dst, width, height, p, 0, 0, 0);
                break;
            case TransitionType.FadeToWhite:
                FadeThroughColor(a, b, dst, width, height, p, 255, 255, 255);
                break;
            case TransitionType.SlideLeft:
                Slide(a, b, dst, width, height, p, dx: 1, dy: 0);
                break;
            case TransitionType.SlideRight:
                Slide(a, b, dst, width, height, p, dx: -1, dy: 0);
                break;
            case TransitionType.SlideUp:
                Slide(a, b, dst, width, height, p, dx: 0, dy: 1);
                break;
            case TransitionType.SlideDown:
                Slide(a, b, dst, width, height, p, dx: 0, dy: -1);
                break;
            case TransitionType.Wipe:
                Wipe(a, b, dst, width, height, p);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown transition type.");
        }

        return new VideoFrame(width, height, dst, "rgba");
    }

    /// <summary>Convenience: same as <see cref="Render"/> wrapped in a full-opacity layer.</summary>
    public static CompositorLayer RenderLayer(
        CompositorLayer outgoing,
        CompositorLayer incoming,
        TransitionType type,
        double progress,
        int trackIndex = 0)
        => new(Render(outgoing, incoming, type, progress), trackIndex);

    private static void CrossDissolve(
        VideoFrame a, VideoFrame b, byte[] dst, int width, int height, double p)
    {
        // Exact endpoints avoid rounding drift so progress 0/1 match source frames.
        if (p <= 0.0)
        {
            CopyScaled(a, dst, width, height);
            return;
        }
        if (p >= 1.0)
        {
            CopyScaled(b, dst, width, height);
            return;
        }

        var inv = 1.0 - p;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Sample(a, x, y, width, height, out var ar, out var ag, out var ab, out var aa);
                Sample(b, x, y, width, height, out var br, out var bg, out var bb, out var ba);
                var i = (y * width + x) * 4;
                dst[i]     = LerpByte(ar, br, inv, p);
                dst[i + 1] = LerpByte(ag, bg, inv, p);
                dst[i + 2] = LerpByte(ab, bb, inv, p);
                dst[i + 3] = LerpByte(aa, ba, inv, p);
            }
        }
    }

    private static void FadeThroughColor(
        VideoFrame a, VideoFrame b, byte[] dst, int width, int height, double p,
        byte cr, byte cg, byte cb)
    {
        if (p <= 0.0)
        {
            CopyScaled(a, dst, width, height);
            return;
        }
        if (p >= 1.0)
        {
            CopyScaled(b, dst, width, height);
            return;
        }

        // First half: A → color; second half: color → B. Midpoint is pure color.
        if (p <= 0.5)
        {
            var t = p * 2.0; // 0 → 1
            var keep = 1.0 - t;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    Sample(a, x, y, width, height, out var ar, out var ag, out var ab, out var aa);
                    var i = (y * width + x) * 4;
                    dst[i]     = LerpByte(ar, cr, keep, t);
                    dst[i + 1] = LerpByte(ag, cg, keep, t);
                    dst[i + 2] = LerpByte(ab, cb, keep, t);
                    dst[i + 3] = 255;
                }
            }
        }
        else
        {
            var t = (p - 0.5) * 2.0; // 0 → 1
            var keep = 1.0 - t;
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    Sample(b, x, y, width, height, out var br, out var bg, out var bb, out _);
                    var i = (y * width + x) * 4;
                    dst[i]     = LerpByte(cr, br, keep, t);
                    dst[i + 1] = LerpByte(cg, bg, keep, t);
                    dst[i + 2] = LerpByte(cb, bb, keep, t);
                    dst[i + 3] = 255;
                }
            }
        }
    }

    /// <summary>
    /// Push-style slide. <paramref name="dx"/>/<paramref name="dy"/> are unit direction of A's exit
    /// (and of B's entry opposite). SlideLeft: A exits left (dx=1 sample shift), B enters from right.
    /// </summary>
    private static void Slide(
        VideoFrame a, VideoFrame b, byte[] dst, int width, int height, double p,
        int dx, int dy)
    {
        if (p <= 0.0)
        {
            CopyScaled(a, dst, width, height);
            return;
        }
        if (p >= 1.0)
        {
            CopyScaled(b, dst, width, height);
            return;
        }

        var shiftX = p * width;
        var shiftY = p * height;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                // Map canvas pixel into A's sliding coordinate system.
                var aSrcX = x + dx * shiftX;
                var aSrcY = y + dy * shiftY;

                byte r, g, bl, al;
                if (aSrcX >= 0 && aSrcX < width && aSrcY >= 0 && aSrcY < height)
                {
                    Sample(a, aSrcX, aSrcY, width, height, out r, out g, out bl, out al);
                }
                else
                {
                    // B trails A by one full frame in the exit direction.
                    var bSrcX = x + dx * shiftX - dx * width;
                    var bSrcY = y + dy * shiftY - dy * height;
                    Sample(b, bSrcX, bSrcY, width, height, out r, out g, out bl, out al);
                }

                var i = (y * width + x) * 4;
                dst[i] = r;
                dst[i + 1] = g;
                dst[i + 2] = bl;
                dst[i + 3] = al;
            }
        }
    }

    private static void Wipe(
        VideoFrame a, VideoFrame b, byte[] dst, int width, int height, double p)
    {
        if (p <= 0.0)
        {
            CopyScaled(a, dst, width, height);
            return;
        }
        if (p >= 1.0)
        {
            CopyScaled(b, dst, width, height);
            return;
        }

        // Boundary moves left→right: left of boundary is B (revealed), right remains A.
        var boundary = p * width;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                byte r, g, bl, al;
                if (x + 0.5 < boundary)
                    Sample(b, x, y, width, height, out r, out g, out bl, out al);
                else
                    Sample(a, x, y, width, height, out r, out g, out bl, out al);

                var i = (y * width + x) * 4;
                dst[i] = r;
                dst[i + 1] = g;
                dst[i + 2] = bl;
                dst[i + 3] = al;
            }
        }
    }

    private static void CopyScaled(VideoFrame src, byte[] dst, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Sample(src, x, y, width, height, out var r, out var g, out var b, out var a);
                var i = (y * width + x) * 4;
                dst[i] = r;
                dst[i + 1] = g;
                dst[i + 2] = b;
                dst[i + 3] = a;
            }
        }
    }

    /// <summary>
    /// Nearest-neighbor sample of <paramref name="src"/> mapped into an output canvas of
    /// <paramref name="outW"/>×<paramref name="outH"/>. Coordinates outside [0,out) clamp.
    /// </summary>
    private static void Sample(
        VideoFrame src, double outX, double outY, int outW, int outH,
        out byte r, out byte g, out byte b, out byte a)
    {
        if (outX < 0 || outY < 0 || outX >= outW || outY >= outH ||
            src.Width <= 0 || src.Height <= 0 || src.Data.Length < src.Width * src.Height * 4)
        {
            r = g = b = 0;
            a = 0;
            return;
        }

        var sx = Math.Min((int)(outX * src.Width / (double)outW), src.Width - 1);
        var sy = Math.Min((int)(outY * src.Height / (double)outH), src.Height - 1);
        if (sx < 0) sx = 0;
        if (sy < 0) sy = 0;

        var idx = (sy * src.Width + sx) * 4;
        r = src.Data[idx];
        g = src.Data[idx + 1];
        b = src.Data[idx + 2];
        a = src.Data[idx + 3];
    }

    private static byte LerpByte(byte from, byte to, double wFrom, double wTo)
        => (byte)Math.Clamp((int)Math.Round(from * wFrom + to * wTo), 0, 255);
}
