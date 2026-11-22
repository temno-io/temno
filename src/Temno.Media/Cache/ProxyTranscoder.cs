using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Temno.Media.Cache;

/// <summary>
/// Transcodes a source file to a scrub-friendly H.264 proxy via the external ffmpeg utility.
/// Fast-decode settings: veryfast preset, short GOP (-g 30) for snappy seeking, faststart moov.
/// </summary>
public sealed class ProxyTranscoder
{
    /// <summary>Pure, testable ffmpeg argument string for the proxy transcode.</summary>
    public static string BuildArguments(string sourcePath, string outputPath, int proxyHeight)
    {
        if (string.IsNullOrEmpty(sourcePath))
        {
            throw new ArgumentException("Path cannot be null or empty.", nameof(sourcePath));
        }

        if (string.IsNullOrEmpty(outputPath))
        {
            throw new ArgumentException("Output path cannot be null or empty.", nameof(outputPath));
        }

        if (proxyHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(proxyHeight), "Proxy height must be positive.");
        }

        return $"-y -i \"{sourcePath}\" -vf scale=-2:{proxyHeight} -c:v libx264 -preset veryfast -crf 23 " +
               $"-g 30 -pix_fmt yuv420p -c:a aac -b:a 128k -movflags +faststart -f mp4 \"{outputPath}\"";
    }

    public async Task TranscodeAsync(
        string sourcePath,
        string outputPath,
        int proxyHeight,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Media file not found.", sourcePath);
        }

        var ffmpegPath = FFmpegResolver.GetFFmpegPath();
        var arguments = BuildArguments(sourcePath, outputPath, proxyHeight);

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
        // Keep background proxy transcodes from starving interactive preview decode and playback.
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { /* process may have exited already or OS denied priority change */ }
        using var cancellationRegistration = ProcessCancellation.KillOnCancellation(process, cancellationToken);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}. Stderr: {stderr}");
        }
    }
}
