namespace Temno.Media.Audio;

/// <summary>Mac: Audio/AudioEnvelope.swift — RMS envelope at fixed hop.</summary>
public sealed class AudioEnvelope
{
    public double HopSeconds { get; }
    public float[] Samples { get; }

    public double DurationSeconds => Samples.Length * HopSeconds;

    public AudioEnvelope(double hopSeconds, float[] samples)
    {
        if (hopSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(hopSeconds));
        ArgumentNullException.ThrowIfNull(samples);
        HopSeconds = hopSeconds;
        Samples = samples;
    }
}

/// <summary>Mac: AudioEnvelopeExtractor — mono RMS per 10 ms hop at 16 kHz analysis rate.</summary>
public static class AudioEnvelopeExtractor
{
    public const double SampleRate = 16_000;
    public const double HopSeconds = 0.01;

    /// <summary>Build envelope from mono float32 PCM (already at any rate).</summary>
    public static AudioEnvelope Extract(float[] monoSamples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(monoSamples);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        var hopSize = Math.Max(1, (int)Math.Round(sampleRate * HopSeconds));
        if (monoSamples.Length == 0)
            return new AudioEnvelope(HopSeconds, []);

        var hops = (monoSamples.Length + hopSize - 1) / hopSize;
        var samples = new float[hops];
        for (var h = 0; h < hops; h++)
        {
            var start = h * hopSize;
            var end = Math.Min(monoSamples.Length, start + hopSize);
            double sumSq = 0;
            for (var i = start; i < end; i++)
            {
                var v = monoSamples[i];
                sumSq += v * v;
            }
            var n = end - start;
            samples[h] = n > 0 ? (float)Math.Sqrt(sumSq / n) : 0f;
        }
        return new AudioEnvelope(HopSeconds, samples);
    }

    /// <summary>Decode media to mono 16 kHz and extract envelope for an optional source range.</summary>
    public static async Task<AudioEnvelope> ExtractFromFileAsync(
        string path,
        double startSeconds = 0,
        double? durationSeconds = null,
        IAudioSource? audioSource = null,
        CancellationToken cancellationToken = default)
    {
        var source = audioSource ?? new FfmpegAudioSource();
        var buffer = await source.ReadAudioAsync(
            path,
            sampleRate: (int)SampleRate,
            channelCount: 1,
            startSeconds: startSeconds,
            durationSeconds: durationSeconds,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return Extract(buffer.Samples, buffer.SampleRate);
    }
}
