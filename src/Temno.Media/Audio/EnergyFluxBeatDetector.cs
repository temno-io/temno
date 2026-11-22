using System.Diagnostics;
using Temno.Core.Audio;

namespace Temno.Media.Audio;

/// <summary>
/// Onset-based beat detector using FFmpeg mono f32le decode + spectral energy flux
/// with adaptive peak-picking. Mac equivalent uses Core ML (Beat This); this is the
/// Windows pure-C# fallback (WP-45).
/// </summary>
public sealed class EnergyFluxBeatDetector : IBeatDetector
{
    /// <summary>Model contract sample rate (same as Mac BeatDetector / Beat This).</summary>
    public const int SampleRate = 22050;

    /// <summary>Analysis frame length in samples (~46 ms at 22050 Hz).</summary>
    public const int FrameSize = 1024;

    /// <summary>Hop between frames (~11.6 ms).</summary>
    public const int HopSize = 256;

    /// <summary>Multiplier on flux std-dev for the adaptive onset threshold.</summary>
    public const double ThresholdSigma = 1.5;

    /// <summary>Minimum gap between accepted onsets (seconds) to suppress double-triggers.</summary>
    public const double MinOnsetGapSeconds = 0.08;

    private readonly IAudioSource _audioSource;

    public EnergyFluxBeatDetector(IAudioSource? audioSource = null)
    {
        _audioSource = audioSource ?? new FfmpegAudioSource();
    }

    public async Task<BeatAnalysis> DetectAsync(string mediaPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(mediaPath))
        {
            throw new ArgumentException("Media path cannot be null or empty.", nameof(mediaPath));
        }

        if (!File.Exists(mediaPath))
        {
            throw new FileNotFoundException("Media file not found.", mediaPath);
        }

        var buffer = await _audioSource.ReadAudioAsync(
            mediaPath,
            SampleRate,
            channelCount: 1,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (buffer.SampleFrameCount == 0)
        {
            return BeatAnalysis.Empty;
        }

        return AnalyzeSamples(buffer.Samples, buffer.SampleRate);
    }

    /// <summary>
    /// Pure analysis path used by unit tests: energy flux + adaptive peak pick + BPM/downbeats.
    /// Samples are mono float32 PCM.
    /// </summary>
    public static BeatAnalysis AnalyzeSamples(float[] samples, int sampleRate = SampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        }

        if (samples.Length < FrameSize)
        {
            return BeatAnalysis.Empty;
        }

        var (onsets, strengths) = DetectOnsets(samples, sampleRate);
        if (onsets.Count == 0)
        {
            return BeatAnalysis.Empty;
        }

        var bpm = EstimateBpm(onsets) ?? 0;
        var downbeats = EstimateDownbeats(onsets, strengths);

        return new BeatAnalysis
        {
            Bpm = bpm,
            Beats = onsets.ToArray(),
            Downbeats = downbeats.ToArray(),
        };
    }

    /// <summary>60 / median inter-onset interval. Mac: BeatDetector.estimateBPM.</summary>
    public static double? EstimateBpm(IReadOnlyList<double> beats)
    {
        if (beats.Count <= 2)
        {
            return null;
        }

        var intervals = new double[beats.Count - 1];
        for (var i = 0; i < intervals.Length; i++)
        {
            intervals[i] = beats[i + 1] - beats[i];
        }

        Array.Sort(intervals);
        var median = intervals[intervals.Length / 2];
        return median > 0 ? 60.0 / median : null;
    }

    /// <summary>
    /// Spectral energy flux: per-frame energy, half-wave rectified first difference,
    /// peak-pick above median + 1.5σ with local-max and min-gap constraints.
    /// </summary>
    public static (List<double> Onsets, List<double> Strengths) DetectOnsets(float[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }

        var frameCount = 1 + (samples.Length - FrameSize) / HopSize;
        if (frameCount < 3)
        {
            return ([], []);
        }

        var energy = new double[frameCount];
        for (var f = 0; f < frameCount; f++)
        {
            var start = f * HopSize;
            double sum = 0;
            for (var i = 0; i < FrameSize; i++)
            {
                var s = samples[start + i];
                sum += s * s;
            }

            energy[f] = sum;
        }

        // Energy flux (onset strength = positive energy increases).
        var flux = new double[frameCount];
        for (var f = 1; f < frameCount; f++)
        {
            var delta = energy[f] - energy[f - 1];
            flux[f] = delta > 0 ? delta : 0;
        }

        var mean = 0.0;
        for (var f = 0; f < frameCount; f++)
        {
            mean += flux[f];
        }

        mean /= frameCount;

        var variance = 0.0;
        for (var f = 0; f < frameCount; f++)
        {
            var d = flux[f] - mean;
            variance += d * d;
        }

        variance /= frameCount;
        var std = Math.Sqrt(variance);

        var sorted = (double[])flux.Clone();
        Array.Sort(sorted);
        var median = sorted[sorted.Length / 2];
        var threshold = median + ThresholdSigma * std;

        // Flat / near-silence: still require a positive threshold so noise zeros do not spike.
        if (threshold <= 0)
        {
            threshold = Math.Max(mean + ThresholdSigma * Math.Max(std, 1e-12), 1e-12);
        }

        var onsets = new List<double>();
        var strengths = new List<double>();
        var minGapFrames = Math.Max(1, (int)Math.Round(MinOnsetGapSeconds * sampleRate / HopSize));
        var lastAccepted = -minGapFrames;

        for (var f = 1; f < frameCount - 1; f++)
        {
            if (flux[f] < threshold)
            {
                continue;
            }

            // Local maximum.
            if (flux[f] < flux[f - 1] || flux[f] <= flux[f + 1])
            {
                continue;
            }

            if (f - lastAccepted < minGapFrames)
            {
                // Keep the stronger of two close peaks.
                if (strengths.Count > 0 && flux[f] > strengths[^1])
                {
                    onsets[^1] = FrameToSeconds(f, sampleRate);
                    strengths[^1] = flux[f];
                    lastAccepted = f;
                }

                continue;
            }

            onsets.Add(FrameToSeconds(f, sampleRate));
            strengths.Add(flux[f]);
            lastAccepted = f;
        }

        return (onsets, strengths);
    }

    /// <summary>
    /// Every 4th beat starting from the strongest onset (phase aligned to the loudest peak).
    /// </summary>
    public static List<double> EstimateDownbeats(IReadOnlyList<double> beats, IReadOnlyList<double> strengths)
    {
        if (beats.Count == 0)
        {
            return [];
        }

        var strongestIndex = 0;
        if (strengths.Count == beats.Count)
        {
            var max = strengths[0];
            for (var i = 1; i < strengths.Count; i++)
            {
                if (strengths[i] > max)
                {
                    max = strengths[i];
                    strongestIndex = i;
                }
            }
        }

        // Phase so strongest is a downbeat: index ≡ strongestIndex (mod 4).
        var phase = ((strongestIndex % 4) + 4) % 4;
        var downbeats = new List<double>();
        for (var i = 0; i < beats.Count; i++)
        {
            if (i % 4 == phase)
            {
                downbeats.Add(beats[i]);
            }
        }

        return downbeats;
    }

    private static double FrameToSeconds(int frameIndex, int sampleRate)
        => (double)frameIndex * HopSize / sampleRate;

    /// <summary>
    /// Direct ffmpeg decode to mono f32le @ 22050 Hz (same args pattern as FfmpegAudioSource).
    /// </summary>
    public static async Task<float[]> DecodeMonoAsync(string mediaPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(mediaPath))
        {
            throw new ArgumentException("Media path cannot be null or empty.", nameof(mediaPath));
        }

        if (!File.Exists(mediaPath))
        {
            throw new FileNotFoundException("Media file not found.", mediaPath);
        }

        var ffmpegPath = FFmpegResolver.GetFFmpegPath();
        var arguments =
            $"-y -i \"{mediaPath}\" -vn -f f32le -acodec pcm_f32le -ar {SampleRate} -ac 1 -";

        using var process = new Process();
        process.StartInfo.FileName = ffmpegPath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start ffmpeg process: '{ffmpegPath}'");
        }
        using var cancellationRegistration = ProcessCancellation.KillOnCancellation(process, cancellationToken);

        using var ms = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(ms, cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg exited with code {process.ExitCode}. Stderr: {stderr}");
        }

        var data = ms.ToArray();
        var usableBytes = data.Length - data.Length % sizeof(float);
        var samples = new float[usableBytes / sizeof(float)];
        Buffer.BlockCopy(data, 0, samples, 0, usableBytes);
        return samples;
    }
}
