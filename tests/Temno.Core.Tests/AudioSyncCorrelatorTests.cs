using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

public sealed class AudioSyncCorrelatorTests
{
    [Fact]
    public void Correlate_FindsKnownDelay()
    {
        // Reference: 200 hops of a pulse train.
        var reference = new float[200];
        for (var i = 20; i < 180; i++)
            reference[i] = (float)Math.Sin(i * 0.3);

        // Target is reference delayed by 15 hops (zero-padded front).
        const int delay = 15;
        var target = new float[200];
        for (var i = delay; i < 200; i++)
            target[i] = reference[i - delay];

        var result = AudioSyncCorrelator.Correlate(reference, target, maxLagHops: 40, minOverlapHops: 16);
        Assert.NotNull(result);
        // Target delayed in media → lag is negative (aligns with earlier ref samples).
        Assert.InRange(result!.Value.LagHops, -delay - 1, -delay + 1);
        Assert.True(result.Value.Confidence >= 0.9, $"confidence={result.Value.Confidence}");
    }

    [Fact]
    public void SeededCorrelate_RejectsBelowMinConfidence()
    {
        var a = new float[50];
        var b = new float[50];
        for (var i = 0; i < 50; i++)
        {
            a[i] = 1;
            b[i] = (i % 2 == 0) ? 1 : -1;
        }

        var result = AudioSyncCorrelator.SeededCorrelate(
            a, b, seedHops: null, seedWindowHops: 10, maxLagHops: 10,
            minOverlapHops: 16, minConfidence: 0.99);
        // Uncorrelated bipolar noise should not meet 0.99.
        Assert.True(result is null || result.Value.Confidence < 0.99);
    }

    [Fact]
    public void Envelope_RmsHops()
    {
        var sr = 16_000;
        var mono = new float[sr]; // 1 second
        for (var i = 0; i < mono.Length; i++)
            mono[i] = 0.5f;
        var env = AudioEnvelopeExtractor.Extract(mono, sr);
        Assert.Equal(100, env.Samples.Length); // 10 ms hops
        Assert.All(env.Samples, s => Assert.InRange(s, 0.49f, 0.51f));
    }
}
