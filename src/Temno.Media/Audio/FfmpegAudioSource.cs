using System.Diagnostics;
using System.Globalization;

namespace Temno.Media.Audio;

/// <summary>
/// Decodes audio using the external ffmpeg utility (-f f32le to stdout).
/// Mirrors the FfmpegFrameSource process pattern. Mac: Audio/AudioTrackReader.swift.
/// </summary>
public sealed class FfmpegAudioSource : IAudioSource
{
    private readonly IMediaProbe _probe;

    public FfmpegAudioSource(IMediaProbe? probe = null)
    {
        _probe = probe ?? new FfmpegMediaProbe();
    }

    public async Task<AudioBuffer> ReadAudioAsync(
        string path,
        int sampleRate,
        int channelCount,
        double startSeconds = 0,
        double? durationSeconds = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Media file not found.", path);
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be positive.");
        }

        if (channelCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCount), "Channel count must be positive.");
        }

        if (startSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startSeconds), "Start time cannot be negative.");
        }

        if (durationSeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "Duration cannot be negative.");
        }

        var info = await _probe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        if (!info.HasAudio)
        {
            throw new InvalidOperationException("The media file does not contain an audio stream.");
        }

        var plan = PlanRead(info.AudioStartSeconds, startSeconds, durationSeconds, sampleRate);
        var decoded = plan.DecodeSeconds is <= 0
            ? []
            : await DecodeAsync(path, sampleRate, channelCount, plan.SeekSeconds, plan.DecodeSeconds, cancellationToken)
                .ConfigureAwait(false);

        var decodedFrames = decoded.Length / channelCount;
        var wantedFrames = durationSeconds is { } duration
            ? (long)Math.Round(duration * sampleRate, MidpointRounding.AwayFromZero)
            : long.MaxValue;
        var leadFrames = checked((int)Math.Min(plan.LeadFrames, wantedFrames));
        var totalFrames = checked((int)Math.Min(leadFrames + (long)decodedFrames, wantedFrames));
        if (leadFrames == 0 && totalFrames == decodedFrames)
        {
            return new AudioBuffer(decoded, sampleRate, channelCount);
        }

        var samples = new float[checked(totalFrames * channelCount)];
        Array.Copy(decoded, 0, samples, leadFrames * channelCount, (totalFrames - leadFrames) * channelCount);
        return new AudioBuffer(samples, sampleRate, channelCount);
    }

    /// <summary>
    /// WP-336: what to decode for a window of source seconds. Source seconds are on the container
    /// clock, the one <c>-ss</c> seeks on and the picture's decoders read, which is also the Mac's movie
    /// time. An audio stream that starts after the container start is preceded by silence. Audio that
    /// starts before a later picture is not skipped (owner, 2026-10-05): skipping it synced a whole clip
    /// whose first frame the picture pipe snaps to zero, but made trimmed clips sound early by the gap.
    ///
    /// The seek always lands where the stream has audio. Raw PCM keeps no timestamps, so ffmpeg drops
    /// a gap before the first sample, and it cuts <c>-t</c> to the sample only when its output starts at
    /// zero: from inside the gap it cut by whole packets (measured on WP-274's late-audio fixture).
    /// A start within half a sample of the stream's start is that start, so a file whose audio starts
    /// with the container seeks exactly as it did before.
    /// </summary>
    internal static AudioReadPlan PlanRead(
        double? audioStartSeconds,
        double startSeconds,
        double? durationSeconds,
        int sampleRate)
    {
        var audioStart = audioStartSeconds ?? 0;
        var from = startSeconds;
        var offsetFrames = (from - audioStart) * sampleRate;

        var leadFrames = offsetFrames <= -0.5
            ? (long)Math.Round(-offsetFrames, MidpointRounding.AwayFromZero)
            : 0;
        var seekSeconds = offsetFrames >= 0.5 ? from : Math.Max(0, audioStart);
        var decodeSeconds = durationSeconds is { } duration
            ? Math.Max(0, duration - leadFrames / (double)sampleRate)
            : (double?)null;
        return new AudioReadPlan(leadFrames, seekSeconds, decodeSeconds);
    }

    private static async Task<float[]> DecodeAsync(
        string path,
        int sampleRate,
        int channelCount,
        double seekSeconds,
        double? durationSeconds,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = FFmpegResolver.GetFFmpegPath();
        var arguments = "-y ";
        if (seekSeconds > 0)
        {
            arguments += $"-ss {seekSeconds.ToString("0.000###", CultureInfo.InvariantCulture)} ";
        }

        arguments += $"-i \"{path}\" ";
        if (durationSeconds is not null)
        {
            arguments += $"-t {durationSeconds.Value.ToString("0.000###", CultureInfo.InvariantCulture)} ";
        }

        // The first audio stream, as on Mac (AudioTrackReader), and the one the probe's start describes.
        arguments += $"-map 0:a:0 -vn -f f32le -acodec pcm_f32le -ar {sampleRate} -ac {channelCount} -";

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
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}. Stderr: {stderr}");
        }

        var data = ms.ToArray();
        // Truncate any trailing partial sample frame so channels stay aligned.
        var usableBytes = data.Length - data.Length % (sizeof(float) * channelCount);
        var samples = new float[usableBytes / sizeof(float)];
        // f32le matches host byte order on every supported platform (x64/arm64 are little-endian).
        Buffer.BlockCopy(data, 0, samples, 0, usableBytes);
        return samples;
    }
}

/// <param name="LeadFrames">Silent sample frames before the stream's first decoded sample.</param>
/// <param name="SeekSeconds">Where the decode starts, in seconds after the container start; 0 for no seek.</param>
/// <param name="DecodeSeconds">How much to decode after the lead; null to the end of the stream.</param>
internal readonly record struct AudioReadPlan(long LeadFrames, double SeekSeconds, double? DecodeSeconds);
