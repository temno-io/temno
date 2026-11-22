using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Temno.Media;

/// <summary>
/// Decodes video frames using the external ffmpeg utility.
/// Caches probe results so scrubbing/playback does not spawn ffprobe on every frame.
/// </summary>
public sealed class FfmpegFrameSource : IFrameSource
{
    private readonly IMediaProbe _probe;
    private readonly ConcurrentDictionary<string, MediaProbeResult> _probeCache = new(StringComparer.OrdinalIgnoreCase);

    public FfmpegFrameSource(IMediaProbe? probe = null)
    {
        _probe = probe ?? new FfmpegMediaProbe();
    }

    public void ClearProbeCache() => _probeCache.Clear();

    public async Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
        => await GetFrameAsync(path, sourceSeconds, outputWidth: null, outputHeight: null, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Preview seek: one BGRA frame scaled to <paramref name="maxDimension"/>. Playback should
    /// use <see cref="SequentialFrameSource"/> instead of calling this every clock tick.
    /// </summary>
    public async Task<VideoFrame> GetPreviewFrameAsync(
        string path,
        double sourceSeconds,
        int maxDimension,
        CancellationToken cancellationToken = default)
    {
        var info = await GetProbeAsync(path, cancellationToken).ConfigureAwait(false);
        if (!info.HasVideo || info.Width is null || info.Height is null)
            throw new InvalidOperationException("The media file does not contain a valid video stream.");
        var (w, h) = PreviewRaster.Fit(info.Width.Value, info.Height.Value, maxDimension);
        return await GetFrameAsync(path, sourceSeconds, w, h, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VideoFrame> GetFrameAsync(
        string path,
        double sourceSeconds,
        int? outputWidth,
        int? outputHeight,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));

        if (!File.Exists(path))
            throw new FileNotFoundException("Media file not found.", path);

        var info = await GetProbeAsync(path, cancellationToken).ConfigureAwait(false);
        if (!info.HasVideo || info.Width is null || info.Height is null)
            throw new InvalidOperationException("The media file does not contain a valid video stream.");

        var nativeW = info.Width.Value;
        var nativeH = info.Height.Value;
        var w = outputWidth ?? nativeW;
        var h = outputHeight ?? nativeH;
        if (w <= 0 || h <= 0)
            throw new ArgumentOutOfRangeException(outputWidth is null ? nameof(outputHeight) : nameof(outputWidth));
        var expected = checked(w * h * 4);
        // WP-405: ffmpeg emits the turned coded size (its default autorotate); a non-square pixel aspect
        // needs a scale even at the native shown size.
        var (decodedW, decodedH) = info.DecodedSize ?? (nativeW, nativeH);
        var scaled = w != decodedW || h != decodedH;
        var pixelFormat = scaled ? "bgra" : "rgba";

        var ffmpegPath = FFmpegResolver.GetFFmpegPath();
        var seek = FfmpegPlaybackDecoder.ShouldEmitInputSeek(sourceSeconds, out var ssStr)
            ? $"-ss {ssStr} "
            : "";

        var arguments = scaled
            ? $"-hide_banner -loglevel error {seek}-i \"{path}\" " +
              $"-frames:v 1 -vf {FfmpegVideoFilter.BgraConvert(w, h, decodedW, decodedH)} -pix_fmt bgra -f rawvideo -"
            : $"-hide_banner -loglevel error {seek}-i \"{path}\" " +
              $"-frames:v 1 -f rawvideo -pix_fmt rgba -";

        using var process = new Process();
        process.StartInfo.FileName = ffmpegPath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start ffmpeg process: '{ffmpegPath}'");
        using var cancellationRegistration = ProcessCancellation.KillOnCancellation(process, cancellationToken);

        using var ms = new MemoryStream(expected);
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(ms, cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);

        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}. Stderr: {stderr}");

        var data = ms.ToArray();
        if (data.Length == 0)
            throw new InvalidOperationException("No frame data was written by ffmpeg.");

        // Pad/truncate if stream dimensions disagreed slightly with probe.
        if (data.Length < expected)
        {
            var padded = new byte[expected];
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            data = padded;
        }
        else if (data.Length > expected)
        {
            var trimmed = new byte[expected];
            Buffer.BlockCopy(data, 0, trimmed, 0, expected);
            data = trimmed;
        }

        return new VideoFrame(w, h, data, pixelFormat);
    }

    private async Task<MediaProbeResult> GetProbeAsync(string path, CancellationToken cancellationToken)
    {
        var key = Path.GetFullPath(path);
        if (_probeCache.TryGetValue(key, out var cached))
            return cached;

        var info = await _probe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        _probeCache[key] = info;
        return info;
    }
}
