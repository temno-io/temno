namespace Temno.Media.Audio;

/// <summary>
/// Interleaved float32 PCM. Mac: Audio/AudioTrackReader.swift output shape.
/// </summary>
public sealed class AudioBuffer
{
    /// <summary>Interleaved samples: sample-frame i, channel c at index i * ChannelCount + c.</summary>
    public float[] Samples { get; }

    public int SampleRate { get; }

    public int ChannelCount { get; }

    /// <summary>Per-channel sample count ("sample frames" — audio domain, not timeline frames).</summary>
    public int SampleFrameCount => Samples.Length / ChannelCount;

    public AudioBuffer(float[] samples, int sampleRate, int channelCount)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        }

        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount), "Channel count must be positive.");
        }

        Samples = samples;
        SampleRate = sampleRate;
        ChannelCount = channelCount;
    }
}
