namespace Temno.Media.Audio;

/// <summary>
/// Mac: Audio/AudioSyncCorrelator.swift — normalized cross-correlation over lag hops.
/// Positive lag means target aligns with a later portion of the reference envelope
/// (target should start later on the timeline by lag × hop when speeds match).
/// </summary>
public static class AudioSyncCorrelator
{
    public const int MinOverlapHops = 16;

    public readonly record struct Result(int LagHops, double Confidence);

    /// <summary>
    /// Optional seed lag search, then full window. Returns null when best confidence &lt; minConfidence.
    /// </summary>
    public static Result? SeededCorrelate(
        float[] reference,
        float[] target,
        int? seedHops,
        int seedWindowHops,
        int maxLagHops,
        int minOverlapHops,
        double minConfidence)
    {
        if (seedHops is { } seed)
        {
            var seeded = Correlate(reference, target, seedWindowHops, centerLagHops: seed, minOverlapHops);
            if (seeded is { } s && s.Confidence >= minConfidence)
                return s;
        }

        var full = Correlate(reference, target, maxLagHops, centerLagHops: 0, minOverlapHops);
        if (full is { } f && f.Confidence >= minConfidence)
            return f;
        return null;
    }

    public static Result? Correlate(
        float[] reference,
        float[] target,
        int maxLagHops,
        int centerLagHops = 0,
        int minOverlapHops = MinOverlapHops)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(target);
        if (reference.Length == 0 || target.Length == 0 || maxLagHops < 0)
            return null;

        Result? best = null;
        var lo = centerLagHops - maxLagHops;
        var hi = centerLagHops + maxLagHops;
        for (var lag = lo; lag <= hi; lag++)
        {
            // x = target[iStart..iEnd), y = reference[iStart+lag .. iEnd+lag)
            var iStart = Math.Max(0, -lag);
            var iEnd = Math.Min(target.Length, reference.Length - lag);
            var n = iEnd - iStart;
            if (n < minOverlapHops) continue;

            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            for (var i = 0; i < n; i++)
            {
                var x = (double)target[iStart + i];
                var y = (double)reference[iStart + lag + i];
                sx += x;
                sy += y;
                sxx += x * x;
                syy += y * y;
                sxy += x * y;
            }

            var nD = (double)n;
            var cov = sxy - sx * sy / nD;
            var vx = sxx - sx * sx / nD;
            var vy = syy - sy * sy / nD;
            var denom = Math.Sqrt(vx * vy);
            if (denom <= 0) continue;

            var score = Math.Max(0, cov / denom);
            if (best is null || score > best.Value.Confidence)
                best = new Result(lag, score);
        }

        return best;
    }
}
