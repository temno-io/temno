namespace Temno.Media.Audio;

/// <summary>
/// Dead-air (silence) detector over decoded PCM (WP-55). Mac equivalent:
/// Audio/Analysis/SpeechMaskStore.swift dead-air pass over 32 ms VoiceActivity
/// cells. The Mac gates on a Silero VAD speech mask and flags quiet non-speech
/// runs; Windows has no on-device VAD, so each cell is gated directly on its
/// peak level against the threshold.
/// </summary>
public sealed class SilenceDetector
{
    /// <summary>Analysis sample rate (same as Mac VoiceActivity).</summary>
    public const int SampleRate = 16_000;

    /// <summary>Analysis cell length. Mac: VoiceActivity.chunkDuration = 512 / 16000 = 32 ms.</summary>
    public const double CellDurationSeconds = 512.0 / 16_000.0;

    /// <summary>Level normalization floor. Mac: WaveformExtractor.noiseFloorDb.</summary>
    public const double NoiseFloorDb = -50.0;

    /// <summary>
    /// Default silence threshold in dBFS. Mac: SpeechMaskStore.noSpeechFloor = 0.56 on the
    /// normalized quietness scale, i.e. 0.56 x -50 dB = -28 dB (the absolute fallback the Mac
    /// uses when no speech exists to derive its adaptive median-speech-peak - 12 dB floor).
    /// </summary>
    public const double DefaultThresholdDb = -28.0;

    private const double TimeEpsilon = 1e-9;

    private readonly IAudioSource _audioSource;

    public SilenceDetector(IAudioSource? audioSource = null)
    {
        _audioSource = audioSource ?? new FfmpegAudioSource();
    }

    /// <summary>
    /// Decodes <paramref name="mediaPath"/> to mono 16 kHz PCM and returns the ordered
    /// silent ranges in SOURCE seconds.
    /// </summary>
    public async Task<IReadOnlyList<SilenceRange>> DetectAsync(
        string mediaPath,
        SilenceDetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(mediaPath))
        {
            throw new ArgumentException("Media path cannot be null or empty.", nameof(mediaPath));
        }

        if (!File.Exists(mediaPath))
        {
            throw new FileNotFoundException("Media file not found.", mediaPath);
        }

        options ??= new SilenceDetectionOptions();
        options.Validate();

        var buffer = await _audioSource.ReadAudioAsync(
            mediaPath,
            SampleRate,
            channelCount: 1,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (buffer.SampleFrameCount == 0)
        {
            return [];
        }

        return AnalyzeSamples(buffer.Samples, buffer.SampleRate, options);
    }

    /// <summary>
    /// Pure analysis path used by unit tests. Samples are mono float32 PCM.
    /// Mirrors SpeechMaskStore.buildDeadAirMask: bucket into ~32 ms cells, measure each
    /// cell's peak level, and flag maximal runs of quiet cells that stay at least
    /// <see cref="SilenceDetectionOptions.MinimumPauseSeconds"/> long before keeping
    /// <see cref="SilenceDetectionOptions.SpeechPaddingSeconds"/> next to adjacent loud audio.
    /// </summary>
    public static IReadOnlyList<SilenceRange> AnalyzeSamples(
        float[] samples,
        int sampleRate = SampleRate,
        SilenceDetectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        }

        options ??= new SilenceDetectionOptions();
        options.Validate();

        if (samples.Length == 0)
        {
            return [];
        }

        var cellSamples = Math.Max(1, (int)Math.Round(CellDurationSeconds * sampleRate));
        var cellCount = (samples.Length + cellSamples - 1) / cellSamples;
        var cellSeconds = (double)cellSamples / sampleRate;
        var duration = (double)samples.Length / sampleRate;

        var quiet = new bool[cellCount];
        for (var c = 0; c < cellCount; c++)
        {
            var s0 = c * cellSamples;
            var s1 = Math.Min(samples.Length, s0 + cellSamples);
            var peak = 0f;
            for (var i = s0; i < s1; i++)
            {
                var a = Math.Abs(samples[i]);
                if (a > peak)
                {
                    peak = a;
                }
            }

            // Same normalization as Mac WaveformExtractor: dBFS clamped at the -50 dB floor.
            var peakDb = peak > 0 ? Math.Max(NoiseFloorDb, 20.0 * Math.Log10(peak)) : NoiseFloorDb;
            quiet[c] = peakDb <= options.ThresholdDb;
        }

        var rawRanges = new List<SilenceRange>();
        var cell = 0;
        while (cell < cellCount)
        {
            if (!quiet[cell])
            {
                cell++;
                continue;
            }

            var runEnd = cell;
            while (runEnd < cellCount && quiet[runEnd])
            {
                runEnd++;
            }

            var start = cell * cellSeconds;
            var end = runEnd == cellCount ? duration : runEnd * cellSeconds;
            if (end > start)
                rawRanges.Add(new SilenceRange(start, end));

            cell = runEnd;
        }

        return ApplyTimingPolicy(rawRanges, duration, options);
    }

    /// <summary>
    /// Applies the UI timing policy to exact quiet spans. The minimum-pause floor is evaluated
    /// before padding, then padding shrinks only edges adjacent to non-silent media.
    /// </summary>
    public static IReadOnlyList<SilenceRange> ApplyTimingPolicy(
        IEnumerable<SilenceRange> rawRanges,
        double mediaDurationSeconds,
        SilenceDetectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rawRanges);
        if (!double.IsFinite(mediaDurationSeconds) || mediaDurationSeconds < 0)
            throw new ArgumentOutOfRangeException(
                nameof(mediaDurationSeconds), "Media duration must be finite and non-negative.");

        options ??= new SilenceDetectionOptions();
        options.Validate();

        var ranges = new List<SilenceRange>();
        foreach (var raw in rawRanges.OrderBy(r => r.Start).ThenBy(r => r.End))
        {
            var rawStart = Math.Clamp(raw.Start, 0, mediaDurationSeconds);
            var rawEnd = Math.Clamp(raw.End, 0, mediaDurationSeconds);
            if (rawEnd <= rawStart
                || rawEnd - rawStart < options.MinimumPauseSeconds - TimeEpsilon)
                continue;

            // File edges stay unpadded: padding protects speech, and there is no speech outside
            // the media bounds.
            var start = rawStart > TimeEpsilon
                ? rawStart + options.SpeechPaddingSeconds
                : rawStart;
            var end = rawEnd < mediaDurationSeconds - TimeEpsilon
                ? rawEnd - options.SpeechPaddingSeconds
                : rawEnd;
            if (end > start)
                ranges.Add(new SilenceRange(start, end));
        }

        return ranges;
    }
}
