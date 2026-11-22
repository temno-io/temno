namespace Temno.Media;

/// <summary>
/// Mean of an RGB channel triple, each channel normalized 0–1.
/// Mac: <c>SIMD3&lt;Float&gt;</c> in Compositing/ColorScopes.swift.
/// </summary>
public readonly record struct RgbTriple(float R, float G, float B);

/// <summary>
/// Quantitative color measurements of a frame — the inspect_color backend.
/// Field-for-field mirror of the Mac <c>Scopes</c> struct
/// (Compositing/ColorScopes.swift); the Mac name is noted on each member.
/// All values are normalized fractions (0–1) unless stated otherwise; the MCP
/// readout layer renders <see cref="ClipLow"/>/<see cref="ClipHigh"/> and
/// <see cref="ColorfulPct"/> as percentages (×100).
/// </summary>
public sealed record ColorScopeReport
{
    /// <summary>Mean Rec.709 luma of all samples. Mac: <c>lumaMean</c>.</summary>
    public required float LumaMean { get; init; }

    /// <summary>2nd-percentile luma (black point). Mac: <c>lumaBlack</c>.</summary>
    public required float LumaBlack { get; init; }

    /// <summary>98th-percentile luma (white point). Mac: <c>lumaWhite</c>.</summary>
    public required float LumaWhite { get; init; }

    /// <summary>Fraction of samples with luma &lt; 0.02 (clipped shadows). Mac: <c>clipLow</c> (readout key <c>clipLowPct</c> = ×100).</summary>
    public required float ClipLow { get; init; }

    /// <summary>Fraction of samples with luma &gt; 0.98 (clipped highlights). Mac: <c>clipHigh</c> (readout key <c>clipHighPct</c> = ×100).</summary>
    public required float ClipHigh { get; init; }

    /// <summary>Luma histogram, 16 uniform bins over 0–1, normalized to sum 1. Mac: <c>lumaHistogram</c> (readout key <c>histogram16</c>).</summary>
    public required float[] LumaHistogram { get; init; }

    /// <summary>Per-channel mean RGB. Mac: <c>meanRGB</c>.</summary>
    public required RgbTriple MeanRgb { get; init; }

    /// <summary>Per-channel 2nd percentile. Mac: <c>blackRGB</c>.</summary>
    public required RgbTriple BlackRgb { get; init; }

    /// <summary>Per-channel 98th percentile. Mac: <c>whiteRGB</c>.</summary>
    public required RgbTriple WhiteRgb { get; init; }

    /// <summary>Mean RGB of samples with luma &lt; 1/3. Mac: <c>shadowRGB</c> (readout: <c>zones.shadows</c>).</summary>
    public required RgbTriple ShadowRgb { get; init; }

    /// <summary>Mean RGB of samples with 1/3 ≤ luma ≤ 2/3. Mac: <c>midRGB</c> (readout: <c>zones.mids</c>).</summary>
    public required RgbTriple MidRgb { get; init; }

    /// <summary>Mean RGB of samples with luma &gt; 2/3. Mac: <c>highRGB</c> (readout: <c>zones.highs</c>).</summary>
    public required RgbTriple HighRgb { get; init; }

    /// <summary>Mean HSV-style saturation, (max−min)/max per sample. Mac: <c>saturationMean</c> (readout key <c>saturation</c>).</summary>
    public required float SaturationMean { get; init; }

    /// <summary>meanR − meanB; positive = warm. Mac: <c>warmCoolBias</c> (readout: <c>balance.warmCool</c>).</summary>
    public required float WarmCoolBias { get; init; }

    /// <summary>meanG − (meanR + meanB)/2; positive = green. Mac: <c>greenMagentaBias</c> (readout: <c>balance.greenMagenta</c>).</summary>
    public required float GreenMagentaBias { get; init; }

    /// <summary>
    /// Hue histogram: 12 bins of 30° starting at 0°/red, saturation-weighted over
    /// samples with saturation &gt; 0.15, normalized to sum 1 (all zeros when no
    /// sample qualifies). Mac: <c>hueHistogram</c> (readout key <c>hueHistogram12</c>).
    /// </summary>
    public required float[] HueHistogram { get; init; }

    /// <summary>Fraction of samples with saturation &gt; 0.15. Mac: <c>colorfulPct</c> (readout = ×100).</summary>
    public required float ColorfulPct { get; init; }
}

/// <summary>
/// Scope measurement over a decoded RGBA/BGRA frame.
/// Mac: Compositing/ColorScopes.swift <c>ColorScopes.measure(_:gridEdge:)</c>.
/// </summary>
public static class ColorScopes
{
    /// <summary>Bins in <see cref="ColorScopeReport.LumaHistogram"/> (Mac readout key <c>histogram16</c>).</summary>
    public const int LumaHistogramBins = 16;

    /// <summary>Bins in <see cref="ColorScopeReport.HueHistogram"/> (Mac readout key <c>hueHistogram12</c>).</summary>
    public const int HueHistogramBins = 12;

    /// <summary>
    /// Measures color scopes on a uniform <paramref name="gridEdge"/>² sample grid
    /// (Mac default 256 — bounds cost independent of frame size). Accepts straight
    /// RGBA or BGRA frames only, like CpuEffectsRenderer.
    /// </summary>
    public static ColorScopeReport Measure(VideoFrame frame, int gridEdge = 256)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (gridEdge <= 0) throw new ArgumentOutOfRangeException(nameof(gridEdge));
        if (frame.Width <= 0) throw new ArgumentOutOfRangeException(nameof(frame), "Frame width must be positive.");
        if (frame.Height <= 0) throw new ArgumentOutOfRangeException(nameof(frame), "Frame height must be positive.");

        var requiredLength = checked(frame.Width * frame.Height * 4);
        if (frame.Data.Length < requiredLength)
            throw new ArgumentException("Frame data is smaller than its declared RGBA/BGRA dimensions.", nameof(frame));

        var format = frame.PixelFormat.Trim();
        var isRgba = string.Equals(format, "rgba", StringComparison.OrdinalIgnoreCase);
        var isBgra = string.Equals(format, "bgra", StringComparison.OrdinalIgnoreCase);
        if (!isRgba && !isBgra)
            throw new NotSupportedException($"Color scopes require RGBA or BGRA frames, not '{frame.PixelFormat}'.");

        var redIndex = isRgba ? 0 : 2;
        var blueIndex = isRgba ? 2 : 0;

        var n = gridEdge;
        var count = n * n;
        var data = frame.Data;
        var width = frame.Width;
        var height = frame.Height;

        var rs = new float[count];
        var gs = new float[count];
        var bs = new float[count];
        var ys = new float[count];
        float sumR = 0, sumG = 0, sumB = 0, sumY = 0, sumSat = 0;
        float shadowR = 0, shadowG = 0, shadowB = 0;
        float midR = 0, midG = 0, midB = 0;
        float highR = 0, highG = 0, highB = 0;
        int nShadow = 0, nMid = 0, nHigh = 0;
        int clipLow = 0, clipHigh = 0;
        var hist = new float[LumaHistogramBins];
        var hueHist = new float[HueHistogramBins];
        float hueWeight = 0;
        var nColorful = 0;

        var i = 0;
        for (var gy = 0; gy < n; gy++)
        {
            // Pixel-center mapping of the grid onto the source (nearest sample).
            var srcY = Math.Min(height - 1, (int)(((gy + 0.5) * height) / n));
            var rowOffset = srcY * width;
            for (var gx = 0; gx < n; gx++, i++)
            {
                var srcX = Math.Min(width - 1, (int)(((gx + 0.5) * width) / n));
                var offset = (rowOffset + srcX) * 4;

                var r = data[offset + redIndex] / 255f;
                var g = data[offset + 1] / 255f;
                var b = data[offset + blueIndex] / 255f;
                var y = 0.2126f * r + 0.7152f * g + 0.0722f * b;

                rs[i] = r; gs[i] = g; bs[i] = b; ys[i] = y;
                sumR += r; sumG += g; sumB += b; sumY += y;

                var mx = Math.Max(r, Math.Max(g, b));
                var mn = Math.Min(r, Math.Min(g, b));
                var sat = mx > 0 ? (mx - mn) / mx : 0;
                sumSat += sat;

                if (sat > 0.15f)
                {
                    nColorful++;
                    var d = mx - mn;
                    float h = 0;
                    if (d > 1e-6f)
                    {
                        if (mx == r) h = (g - b) / d;
                        else if (mx == g) h = 2 + (b - r) / d;
                        else h = 4 + (r - g) / d;
                        h /= 6;
                        if (h < 0) h += 1;
                    }
                    hueHist[Math.Min(HueHistogramBins - 1, (int)(h * HueHistogramBins))] += sat;
                    hueWeight += sat;
                }

                if (y < 1f / 3) { shadowR += r; shadowG += g; shadowB += b; nShadow++; }
                else if (y > 2f / 3) { highR += r; highG += g; highB += b; nHigh++; }
                else { midR += r; midG += g; midB += b; nMid++; }

                if (y < 0.02f) clipLow++;
                if (y > 0.98f) clipHigh++;
                hist[Math.Min(LumaHistogramBins - 1, (int)(y * LumaHistogramBins))] += 1;
            }
        }

        Array.Sort(rs);
        Array.Sort(gs);
        Array.Sort(bs);
        Array.Sort(ys);

        var fc = (float)count;
        for (var k = 0; k < hist.Length; k++) hist[k] /= fc;
        if (hueWeight > 0)
            for (var k = 0; k < hueHist.Length; k++) hueHist[k] /= hueWeight;

        // Mac pct(): index by truncation of p * (count − 1).
        static float Pct(float[] sorted, float p) =>
            sorted[Math.Min(sorted.Length - 1, Math.Max(0, (int)(p * (sorted.Length - 1))))];

        static RgbTriple Zone(float r, float g, float b, int c) =>
            c > 0 ? new RgbTriple(r / c, g / c, b / c) : default;

        var meanR = sumR / fc;
        var meanG = sumG / fc;
        var meanB = sumB / fc;

        return new ColorScopeReport
        {
            LumaMean = sumY / fc,
            LumaBlack = Pct(ys, 0.02f),
            LumaWhite = Pct(ys, 0.98f),
            ClipLow = clipLow / fc,
            ClipHigh = clipHigh / fc,
            LumaHistogram = hist,
            MeanRgb = new RgbTriple(meanR, meanG, meanB),
            BlackRgb = new RgbTriple(Pct(rs, 0.02f), Pct(gs, 0.02f), Pct(bs, 0.02f)),
            WhiteRgb = new RgbTriple(Pct(rs, 0.98f), Pct(gs, 0.98f), Pct(bs, 0.98f)),
            ShadowRgb = Zone(shadowR, shadowG, shadowB, nShadow),
            MidRgb = Zone(midR, midG, midB, nMid),
            HighRgb = Zone(highR, highG, highB, nHigh),
            SaturationMean = sumSat / fc,
            WarmCoolBias = meanR - meanB,
            GreenMagentaBias = meanG - (meanR + meanB) / 2,
            HueHistogram = hueHist,
            ColorfulPct = nColorful / fc,
        };
    }
}
