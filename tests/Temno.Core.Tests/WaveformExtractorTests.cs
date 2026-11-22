using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

public sealed class WaveformExtractorTests
{
    [Fact]
    public void ComputePeaks_MaxAbsPerBucket_ClampedTo01()
    {
        // Two buckets: first half peaks at 0.5, second half peaks at 1.0 (a −2.0 sample clamps to 1).
        float[] samples = [0.1f, -0.5f, 0.2f, 1.0f, -2.0f, 0.3f];
        var peaks = WaveformExtractor.ComputePeaks(samples, 2);

        Assert.Equal(2, peaks.Length);
        Assert.Equal(0.5f, peaks[0], 3);
        Assert.Equal(1.0f, peaks[1], 3);
    }

    [Fact]
    public void ComputePeaks_SilenceIsZero_LoudIsOne()
    {
        Assert.All(WaveformExtractor.ComputePeaks(new float[1000], 20), p => Assert.Equal(0f, p));

        var loud = Enumerable.Repeat(0.8f, 1000).ToArray();
        Assert.All(WaveformExtractor.ComputePeaks(loud, 20), p => Assert.Equal(0.8f, p, 3));
    }

    [Fact]
    public void ComputePeaks_RequestsExactBucketCount_EvenWhenMoreBucketsThanSamples()
    {
        var peaks = WaveformExtractor.ComputePeaks([1.0f, 0.25f], 8);
        Assert.Equal(8, peaks.Length);
        // Every bucket maps to one of the two samples; no bucket is empty/NaN.
        Assert.All(peaks, p => Assert.True(p is 1.0f or 0.25f));
    }

    [Fact]
    public void ComputePeaks_EmptyOrNonPositive_ReturnsEmpty()
    {
        Assert.Empty(WaveformExtractor.ComputePeaks([], 10));
        Assert.Empty(WaveformExtractor.ComputePeaks([0.5f], 0));
        Assert.Empty(WaveformExtractor.ComputePeaks([0.5f], -1));
    }
}
