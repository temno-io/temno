using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-213: time-indexed envelope aggregation semantics.</summary>
public sealed class WaveformEnvelopeTests
{
    [Fact]
    public void DurationSeconds_DividesLengthByRate()
    {
        var envelope = new WaveformEnvelope(new float[600], 200);
        Assert.Equal(3.0, envelope.DurationSeconds, 9);
    }

    [Fact]
    public void PeakForRange_AggregatesMaxOverOverlappedBuckets()
    {
        var peaks = new float[1000];
        peaks[100] = 0.25f;
        peaks[150] = 0.9f; // narrow transient inside the queried range
        peaks[151] = 0.4f;
        var envelope = new WaveformEnvelope(peaks, 200);

        Assert.Equal(0.9f, envelope.PeakForRange(0.4, 0.9));
        Assert.Equal(0.25f, envelope.PeakForRange(0.5, 0.6));
        Assert.Equal(0f, envelope.PeakForRange(0.6, 0.7));
    }

    [Fact]
    public void PeakForRange_SingleHotBucketVisibleInWideRange()
    {
        var peaks = new float[2000];
        peaks[1999] = 1.0f;
        var envelope = new WaveformEnvelope(peaks, 200);
        Assert.Equal(1.0f, envelope.PeakForRange(0, 10));
    }

    [Fact]
    public void PeakForRange_OutOfRangeReadsSilence()
    {
        var peaks = new float[200];
        Array.Fill(peaks, 0.8f);
        var envelope = new WaveformEnvelope(peaks, 200);
        Assert.Equal(0f, envelope.PeakForRange(-5, -1));
        Assert.Equal(0f, envelope.PeakForRange(1.0, 5.0));
        Assert.Equal(0f, envelope.PeakForRange(99, 100));
    }

    [Fact]
    public void PeakForRange_DegenerateRangesReadSilence()
    {
        var peaks = new float[200];
        Array.Fill(peaks, 0.8f);
        var envelope = new WaveformEnvelope(peaks, 200);
        Assert.Equal(0f, envelope.PeakForRange(0.5, 0.5));
        Assert.Equal(0f, envelope.PeakForRange(0.6, 0.5));
        Assert.Equal(0f, envelope.PeakForRange(double.NaN, 1));
        Assert.Equal(0f, envelope.PeakForRange(0, double.PositiveInfinity));
        Assert.Equal(0f, new WaveformEnvelope([], 200).PeakForRange(0, 1));
    }

    [Fact]
    public void Constructor_RejectsBadArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new WaveformEnvelope(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WaveformEnvelope([], 0));
    }
}
