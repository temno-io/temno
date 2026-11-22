using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Temno.Media;

/// <summary>
/// Probes media file metadata using the external ffprobe utility.
/// </summary>
public sealed class FfmpegMediaProbe : IMediaProbe
{
    public async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Media file not found.", path);
        }

        var ffprobePath = FFmpegResolver.GetFFprobePath();
        var arguments = $"-v error -show_format -show_streams -of json \"{path}\"";

        using var process = new Process();
        process.StartInfo.FileName = ffprobePath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start ffprobe process: '{ffprobePath}'");
        }
        using var cancellationRegistration = ProcessCancellation.KillOnCancellation(process, cancellationToken);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe exited with code {process.ExitCode}. Stderr: {stderr}");
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var output = JsonSerializer.Deserialize<FFprobeOutput>(stdout, options);
        if (output == null)
        {
            throw new InvalidOperationException("Failed to deserialize ffprobe JSON output.");
        }

        var videoStream = output.Streams?.FirstOrDefault(s => s.CodecType == "video");
        var audioStream = output.Streams?.FirstOrDefault(s => s.CodecType == "audio");

        bool hasVideo = videoStream != null;
        bool hasAudio = audioStream != null;

        var shape = Shape(videoStream?.Width, videoStream?.Height, Rotation(videoStream), videoStream?.SampleAspectRatio);

        double? fps = null;
        if (videoStream != null)
        {
            fps = ParseFps(videoStream.AvgFrameRate) ?? ParseFps(videoStream.RFrameRate);
        }

        double duration = 0.0;
        if (videoStream != null && !string.IsNullOrEmpty(videoStream.Duration))
        {
            duration = ParseDuration(videoStream.Duration, 0.0);
        }
        if (duration == 0.0 && audioStream != null && !string.IsNullOrEmpty(audioStream.Duration))
        {
            duration = ParseDuration(audioStream.Duration, 0.0);
        }
        if (duration == 0.0 && output.Format != null && !string.IsNullOrEmpty(output.Format.Duration))
        {
            duration = ParseDuration(output.Format.Duration, 0.0);
        }

        // ffmpeg adds the format start_time to every -ss, so stream starts are kept on that clock.
        // A container with no start time (a WAV file) seeks from zero.
        var containerStart = ParseStartTime(output.Format?.StartTime) ?? 0.0;

        return new MediaProbeResult(
            DurationSeconds: duration,
            Width: shape.Width,
            Height: shape.Height,
            Fps: fps,
            HasAudio: hasAudio,
            HasVideo: hasVideo,
            VideoCodecName: videoStream?.CodecName,
            AudioStartSeconds: ParseStartTime(audioStream?.StartTime) - containerStart,
            DecodedWidth: shape.DecodedWidth,
            DecodedHeight: shape.DecodedHeight,
            Rotation: shape.Rotation,
            SampleAspect: shape.SampleAspect
        );
    }

    /// <summary>
    /// WP-405: the shown size of a coded picture. ffmpeg autorotates by default, so a phone's portrait
    /// video coded 1920x1080 with a 90 degree display matrix decodes as 1080x1920; a non-square
    /// <paramref name="sampleAspectRatio"/> widens the shown picture (720x576 at 16:15 shows as 768x576).
    /// The decoded size is the coded size turned, before any pixel-aspect correction.
    /// </summary>
    /// <remarks>
    /// <c>Turned</c> is true for any display rotation, including an angle ffmpeg does not apply; such a
    /// picture decodes at its coded size, but a stream copy would still carry the rotation.
    /// </remarks>
    internal static (int? Width, int? Height, int? DecodedWidth, int? DecodedHeight, int Rotation, double SampleAspect, bool Turned) Shape(
        int? codedWidth, int? codedHeight, double? rotation, string? sampleAspectRatio)
    {
        var turn = AppliedRotation(rotation, out var turned);
        var aspect = ParseSampleAspect(sampleAspectRatio);
        if (codedWidth is not > 0 || codedHeight is not > 0)
            return (codedWidth, codedHeight, null, null, turn, aspect, turned);

        var shownCodedWidth = Math.Max(1, (int)Math.Round(codedWidth.Value * aspect));
        var sideways = turn is 90 or 270;
        return sideways
            ? (codedHeight, shownCodedWidth, codedHeight, codedWidth, turn, aspect, turned)
            : (shownCodedWidth, codedHeight, codedWidth, codedHeight, turn, aspect, turned);
    }

    private static double? Rotation(FFprobeStream? stream)
    {
        if (stream is null) return null;
        var fromMatrix = stream.SideDataList?.FirstOrDefault(s => s.Rotation is not null)?.Rotation;
        if (fromMatrix is not null) return fromMatrix;
        // Older muxers wrote a `rotate` tag instead of a display matrix.
        return stream.Tags is not null
            && stream.Tags.TryGetValue("rotate", out var tag)
            && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees)
            ? degrees
            : null;
    }

    /// <summary>
    /// The turn ffmpeg's autorotate applies. fftools <c>get_rotation</c> rounds the angle to a whole
    /// degree and wraps it into [0, 360) (it also negates it, which does not change which turns apply).
    /// Only 90, 180 and 270 are applied; any other angle is an "odd rotation angle" that ffmpeg leaves
    /// alone, so the picture decodes at its coded size.
    /// </summary>
    private static int AppliedRotation(double? degrees, out bool turned)
    {
        turned = false;
        if (degrees is not { } value || !double.IsFinite(value)) return 0;
        var theta = Math.Round(value);
        theta -= 360 * Math.Floor(theta / 360 + 0.9 / 360);
        turned = Math.Abs(theta) >= 1;
        return theta switch
        {
            90 => 90,
            180 => 180,
            270 => 270,
            _ => 0,
        };
    }

    private static double ParseSampleAspect(string? value)
    {
        if (string.IsNullOrEmpty(value)) return 1.0;
        var parts = value.Split(':');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && num > 0 && den > 0)
        {
            var aspect = num / den;
            return Math.Abs(aspect - 1) < 1e-6 ? 1.0 : aspect;
        }
        return 1.0;
    }

    private static double? ParseStartTime(string? value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && double.IsFinite(seconds)
            ? seconds
            : null;

    private static double? ParseFps(string? fpsStr)
    {
        if (string.IsNullOrEmpty(fpsStr)) return null;
        var parts = fpsStr.Split('/');
        if (parts.Length == 2)
        {
            if (double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var num) &&
                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var den) &&
                den > 0)
            {
                var res = num / den;
                return res > 0 ? res : null;
            }
        }
        else if (double.TryParse(fpsStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
        {
            return val > 0 ? val : null;
        }
        return null;
    }

    private static double ParseDuration(string? durationStr, double fallback)
    {
        if (string.IsNullOrEmpty(durationStr)) return fallback;
        if (double.TryParse(durationStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
        {
            return val;
        }
        return fallback;
    }

    private sealed class FFprobeOutput
    {
        [JsonPropertyName("streams")]
        public List<FFprobeStream>? Streams { get; set; }

        [JsonPropertyName("format")]
        public FFprobeFormat? Format { get; set; }
    }

    private sealed class FFprobeStream
    {
        [JsonPropertyName("codec_type")]
        public string? CodecType { get; set; }

        [JsonPropertyName("codec_name")]
        public string? CodecName { get; set; }

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("height")]
        public int? Height { get; set; }

        [JsonPropertyName("r_frame_rate")]
        public string? RFrameRate { get; set; }

        [JsonPropertyName("avg_frame_rate")]
        public string? AvgFrameRate { get; set; }

        [JsonPropertyName("duration")]
        public string? Duration { get; set; }

        [JsonPropertyName("start_time")]
        public string? StartTime { get; set; }

        [JsonPropertyName("sample_aspect_ratio")]
        public string? SampleAspectRatio { get; set; }

        [JsonPropertyName("side_data_list")]
        public List<FFprobeSideData>? SideDataList { get; set; }

        [JsonPropertyName("tags")]
        public Dictionary<string, string>? Tags { get; set; }
    }

    private sealed class FFprobeSideData
    {
        [JsonPropertyName("rotation")]
        public double? Rotation { get; set; }
    }

    private sealed class FFprobeFormat
    {
        [JsonPropertyName("duration")]
        public string? Duration { get; set; }

        [JsonPropertyName("start_time")]
        public string? StartTime { get; set; }
    }
}
