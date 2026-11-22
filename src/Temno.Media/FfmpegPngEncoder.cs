using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Temno.Media;

/// <summary>
/// Encodes an in-memory RGBA frame to PNG by piping raw video through ffmpeg.
/// Used by agent inspection tools that need image payloads without a GUI imaging stack.
/// </summary>
public static class FfmpegPngEncoder
{
    /// <summary>
    /// Extracts one frame from a media file at <paramref name="sourceSeconds"/> as PNG, scaled so the
    /// longest edge is at most <paramref name="maxDimension"/> (never upscaled).
    /// </summary>
    public static async Task<byte[]> ExtractAsync(
        string mediaPath,
        double sourceSeconds,
        int maxDimension = 640,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(mediaPath)) throw new ArgumentException("Path is required.", nameof(mediaPath));
        if (!File.Exists(mediaPath)) throw new FileNotFoundException("Media file not found.", mediaPath);
        if (maxDimension <= 0) throw new ArgumentOutOfRangeException(nameof(maxDimension));

        var ffmpeg = FFmpegResolver.GetFFmpegPath();
        var seek = FfmpegPlaybackDecoder.ShouldEmitInputSeek(sourceSeconds, out var ss)
            ? $"-ss {ss} "
            : "";
        // -2 keeps the scaled edge even (required by some codecs) while preserving aspect.
        var filter = $"scale='min({maxDimension},iw)':'-2'";
        var args = $"-y {seek}-i \"{mediaPath}\" -frames:v 1 -vf \"{filter}\" -f image2pipe -c:v png pipe:1";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start FFmpeg for frame extraction.");
        using var cancellationRegistration = ProcessCancellation.KillOnCancellation(process, cancellationToken);

        using var output = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg frame extraction failed (exit {process.ExitCode}): {stderr[..Math.Min(stderr.Length, 300)]}");

        var bytes = output.ToArray();
        if (bytes.Length == 0)
            throw new InvalidOperationException("FFmpeg produced no frame data.");
        return bytes;
    }

    public static async Task<byte[]> EncodeAsync(VideoFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var rgba = ToRgba(frame);

        var ffmpeg = FFmpegResolver.GetFFmpegPath();
        var args = $"-y -f rawvideo -pix_fmt rgba -s {frame.Width}x{frame.Height} -i pipe:0 "
                 + "-frames:v 1 -f image2pipe -c:v png pipe:1";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start FFmpeg for PNG encoding.");
        using var cancellationRegistration = ProcessCancellation.KillOnCancellation(process, cancellationToken);

        // Read stdout and drain stderr concurrently with the stdin write, or ffmpeg can block on a
        // full pipe and deadlock (same failure mode fixed in TimelineExporter).
        using var output = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.StandardInput.BaseStream.WriteAsync(rgba.AsMemory(0, rgba.Length), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg PNG encode failed (exit {process.ExitCode}): {stderr[..Math.Min(stderr.Length, 300)]}");

        var bytes = output.ToArray();
        if (bytes.Length == 0)
            throw new InvalidOperationException("FFmpeg produced no PNG data.");
        return bytes;
    }

    /// <summary>WP-145: capture-frame accepts compositor RGBA and sequential-decode BGRA.</summary>
    private static byte[] ToRgba(VideoFrame frame)
    {
        var length = checked(frame.Width * frame.Height * 4);
        if (length > frame.Data.Length) length = frame.Data.Length;
        if (string.Equals(frame.PixelFormat, "rgba", StringComparison.OrdinalIgnoreCase))
        {
            if (length == frame.Data.Length) return frame.Data;
            var copy = new byte[length];
            Buffer.BlockCopy(frame.Data, 0, copy, 0, length);
            return copy;
        }

        if (!string.Equals(frame.PixelFormat, "bgra", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"PNG encoding expects an RGBA or BGRA frame, not '{frame.PixelFormat}'.");

        var rgba = new byte[length];
        var src = frame.Data;
        for (var i = 0; i + 3 < length; i += 4)
        {
            rgba[i] = src[i + 2];
            rgba[i + 1] = src[i + 1];
            rgba[i + 2] = src[i];
            rgba[i + 3] = src[i + 3];
        }
        return rgba;
    }
}
