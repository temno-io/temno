namespace Temno.Media.Audio;

/// <summary>
/// A compact peak envelope with real time indexing: bucket <c>i</c> covers source seconds
/// <c>[i / PeaksPerSecond, (i + 1) / PeaksPerSecond)</c>. Unlike the fixed-count array from
/// <see cref="WaveformExtractor.ComputePeaks"/>, this envelope supports source-time-correct
/// sampling of any visible clip window (WP-213).
/// </summary>
public sealed class WaveformEnvelope
{
    /// <summary>Buckets per source second. 200 gives 5 ms resolution; a 5-minute source
    /// compacts to ~60k floats (~240 KB).</summary>
    public const int DefaultPeaksPerSecond = 200;

    public WaveformEnvelope(float[] peaks, int peaksPerSecond = DefaultPeaksPerSecond)
    {
        ArgumentNullException.ThrowIfNull(peaks);
        if (peaksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(peaksPerSecond));
        Peaks = peaks;
        PeaksPerSecond = peaksPerSecond;
    }

    public float[] Peaks { get; }

    public int PeaksPerSecond { get; }

    /// <summary>Source seconds covered by the envelope.</summary>
    public double DurationSeconds => (double)Peaks.Length / PeaksPerSecond;

    /// <summary>
    /// Max peak over the buckets overlapping <c>[startSeconds, endSeconds)</c>. Narrow spikes
    /// stay visible because every overlapped bucket contributes its max, not a point sample.
    /// Out-of-range or empty intervals return 0 (accurate silence).
    /// </summary>
    public float PeakForRange(double startSeconds, double endSeconds)
    {
        if (Peaks.Length == 0) return 0f;
        if (!double.IsFinite(startSeconds) || !double.IsFinite(endSeconds)) return 0f;
        if (endSeconds <= startSeconds) return 0f;

        var first = (int)Math.Floor(startSeconds * PeaksPerSecond);
        var lastExclusive = (int)Math.Ceiling(endSeconds * PeaksPerSecond);
        if (lastExclusive <= first) return 0f;
        first = Math.Max(first, 0);
        lastExclusive = Math.Min(lastExclusive, Peaks.Length);
        if (lastExclusive <= first) return 0f;

        var peak = 0f;
        for (var i = first; i < lastExclusive; i++)
        {
            if (Peaks[i] > peak) peak = Peaks[i];
        }
        return peak;
    }
}
