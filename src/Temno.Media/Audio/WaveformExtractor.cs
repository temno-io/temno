namespace Temno.Media.Audio;

/// <summary>
/// Produces a compact peak envelope (values 0–1) for drawing an audio clip's waveform on the
/// timeline. Mac: Audio/WaveformExtractor.swift. A low sample rate is plenty for a peak envelope.
/// </summary>
public sealed class WaveformExtractor
{
    public const int SampleRate = 8_000;

    /// <summary>
    /// Source seconds decoded per <see cref="IAudioSource"/> call while building an envelope.
    /// One 30-second mono chunk at 8 kHz is ~1 MB of PCM working memory regardless of file length.
    /// </summary>
    public const double EnvelopeChunkSeconds = 30;

    private readonly IAudioSource _audioSource;

    public WaveformExtractor(IAudioSource? audioSource = null)
    {
        _audioSource = audioSource ?? new FfmpegAudioSource();
    }

    /// <summary>
    /// Max-abs peak per bucket over mono float PCM, clamped to 0–1. Buckets divide the samples as
    /// evenly as possible; empty input or non-positive bucketCount returns an empty array.
    /// </summary>
    public static float[] ComputePeaks(float[] monoSamples, int bucketCount)
    {
        ArgumentNullException.ThrowIfNull(monoSamples);
        if (bucketCount <= 0 || monoSamples.Length == 0) return [];

        var peaks = new float[bucketCount];
        for (var b = 0; b < bucketCount; b++)
        {
            var start = (long)b * monoSamples.Length / bucketCount;
            var end = (long)(b + 1) * monoSamples.Length / bucketCount;
            if (end <= start) end = Math.Min(monoSamples.Length, start + 1);

            var peak = 0f;
            for (var i = start; i < end; i++)
            {
                var a = Math.Abs(monoSamples[i]);
                if (a > peak) peak = a;
            }
            peaks[b] = Math.Min(1f, peak);
        }
        return peaks;
    }

    /// <summary>Decodes the file to mono PCM and returns a <paramref name="bucketCount"/>-length peak envelope.</summary>
    public async Task<float[]> ExtractAsync(string path, int bucketCount, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path is required.", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("Media file not found.", path);

        var buffer = await _audioSource
            .ReadAudioAsync(path, SampleRate, channelCount: 1, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return ComputePeaks(buffer.Samples, bucketCount);
    }

    /// <summary>
    /// Decodes the file in sequential bounded chunks and returns a time-indexed peak envelope
    /// (WP-213). Chunked reads keep PCM working memory bounded (~1 MB) at any file length and
    /// decoding runs to end-of-stream with no file-duration cutoff. A read that advances no
    /// time throws rather than looping. Throws <see cref="OperationCanceledException"/> on
    /// cancellation and <see cref="InvalidOperationException"/> when the file has no audio stream.
    /// </summary>
    public async Task<WaveformEnvelope> ExtractEnvelopeAsync(
        string path,
        int peaksPerSecond = WaveformEnvelope.DefaultPeaksPerSecond,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path is required.", nameof(path));
        if (!File.Exists(path)) throw new FileNotFoundException("Media file not found.", path);
        if (peaksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(peaksPerSecond));

        var samplesPerPeak = (double)SampleRate / peaksPerSecond;
        var peaks = new List<float>();
        var bucketPeak = 0f;
        var bucketFill = 0.0; // samples accumulated in the open bucket, in sample units
        var totalSamples = 0L;
        var offsetSeconds = 0.0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = await _audioSource
                .ReadAudioAsync(path, SampleRate, channelCount: 1, offsetSeconds, EnvelopeChunkSeconds, cancellationToken)
                .ConfigureAwait(false);
            var frames = buffer.SampleFrameCount;
            if (frames == 0) break;

            var samples = buffer.Samples;
            var channels = buffer.ChannelCount;
            for (var f = 0; f < frames; f++)
            {
                // Downmix defensively: mono is requested, but a source may still interleave.
                var a = 0f;
                for (var c = 0; c < channels; c++)
                {
                    var v = Math.Abs(samples[f * channels + c]);
                    if (v > a) a = v;
                }
                if (a > bucketPeak) bucketPeak = a;
                bucketFill += 1.0;
                totalSamples++;
                if (bucketFill >= samplesPerPeak)
                {
                    peaks.Add(Math.Min(1f, bucketPeak));
                    bucketPeak = 0f;
                    bucketFill -= samplesPerPeak;
                }
            }

            var advancedSeconds = (double)frames / SampleRate;
            if (advancedSeconds <= 0)
                throw new InvalidOperationException("Audio source returned samples without advancing; cannot complete the waveform envelope.");
            offsetSeconds += advancedSeconds;
            var requestedFrames = (long)(EnvelopeChunkSeconds * SampleRate);
            if (frames < requestedFrames) break; // short read: end of stream
        }

        if (bucketFill > 0) peaks.Add(Math.Min(1f, bucketPeak));
        if (peaks.Count == 0 && totalSamples == 0)
        {
            // Decoded zero samples with no source error: genuinely empty audio reads as silence.
            return new WaveformEnvelope([], peaksPerSecond);
        }
        return new WaveformEnvelope([.. peaks], peaksPerSecond);
    }
}
