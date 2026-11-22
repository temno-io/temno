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

namespace Temno.Media.Export;

/// <summary>
/// Probes video stream metadata, keyframe positions and the decode-order packet table using ffprobe.
/// Results are cached per media path for thread-safe reuse.
/// </summary>
public sealed class FfmpegStreamProbe : IVideoStreamProbe
{
    private readonly object _lock = new();
    private readonly Dictionary<string, VideoStreamInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<VideoStreamInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Media file not found.", path);
        }

        lock (_lock)
        {
            if (_cache.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        var streamArgs = $"-v error -select_streams v:0 -show_entries stream=codec_name,profile,width,height,pix_fmt,avg_frame_rate,color_primaries,color_transfer,color_space,color_range,sample_aspect_ratio:stream_side_data=rotation:stream_tags=rotate:format=start_time,format_name -of json \"{path}\"";
        var stdout = await RunFFprobeAsync(streamArgs, cancellationToken).ConfigureAwait(false);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var output = JsonSerializer.Deserialize<FFprobeOutput>(stdout, options);
        var videoStream = output?.Streams?.FirstOrDefault();
        if (videoStream == null)
        {
            throw new InvalidOperationException($"No video stream found in '{path}'.");
        }

        // ffprobe reports absolute timestamps, but `-ss` — in the stream copy and in the export
        // frame source that defines a clip's source time — seeks from the container start. An
        // MPEG-TS or camcorder .mts file starts well after zero, so absolute keyframe times put
        // every copy at the wrong timeline frame.
        var origin = double.TryParse(output?.Format?.StartTime, NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
            ? start
            : 0;

        // A demux-only listing: no decoding, and decode order is the order a stream copy takes.
        var packetArgs = $"-v error -select_streams v:0 -show_entries packet=pts_time,flags -of csv=p=0 \"{path}\"";
        var packetStdout = await RunFFprobeAsync(packetArgs, cancellationToken).ConfigureAwait(false);

        var packets = new List<VideoPacket>();
        var keyframes = new List<double>();
        using (var reader = new StringReader(packetStdout))
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                var pts = double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var absolute)
                    ? absolute - origin
                    : double.NaN;
                var flags = parts.Length > 1 ? parts[1] : "";
                var packet = new VideoPacket(pts, IsKeyframe: flags.Contains('K'), IsDiscard: flags.Contains('D'));
                packets.Add(packet);
                if (packet.IsKeyframe && !packet.IsDiscard && !double.IsNaN(pts))
                {
                    keyframes.Add(pts);
                }
            }
        }
        keyframes.Sort();

        var info = new VideoStreamInfo(
            CodecName: videoStream.CodecName ?? "",
            Profile: videoStream.Profile,
            Width: videoStream.Width ?? 0,
            Height: videoStream.Height ?? 0,
            PixelFormat: videoStream.PixFmt ?? "",
            FrameRate: ParseFps(videoStream.AvgFrameRate),
            ColorPrimaries: videoStream.ColorPrimaries,
            ColorTransfer: videoStream.ColorTransfer,
            ColorSpace: videoStream.ColorSpace,
            ColorRange: videoStream.ColorRange,
            KeyframeSeconds: keyframes.AsReadOnly(),
            Packets: packets.AsReadOnly(),
            ContainerFormat: output?.Format?.FormatName,
            Reshaped: IsReshaped(videoStream));

        lock (_lock)
        {
            _cache[path] = info;
        }

        return info;
    }

    // WP-405: the decode path turns and un-stretches such a picture; a copied packet would not be.
    private static bool IsReshaped(FFprobeStream stream)
    {
        double? rotation = stream.SideDataList?.FirstOrDefault(s => s.Rotation is not null)?.Rotation;
        if (rotation is null && stream.Tags is not null && stream.Tags.TryGetValue("rotate", out var tag)
            && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees))
            rotation = degrees;
        var shape = FfmpegMediaProbe.Shape(stream.Width, stream.Height, rotation, stream.SampleAspectRatio);
        return shape.Turned || Math.Abs(shape.SampleAspect - 1) > 1e-6;
    }

    private static async Task<string> RunFFprobeAsync(string arguments, CancellationToken cancellationToken)
    {
        var ffprobePath = FFmpegResolver.GetFFprobePath();

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

        return stdout;
    }

    private static double ParseFps(string? fpsStr)
    {
        if (string.IsNullOrEmpty(fpsStr)) return 0;
        var parts = fpsStr.Split('/');
        if (parts.Length == 2)
        {
            if (double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var num) &&
                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var den) &&
                den > 0)
            {
                var res = num / den;
                return res > 0 ? res : 0;
            }
            return 0;
        }
        if (double.TryParse(fpsStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var val))
        {
            return val > 0 ? val : 0;
        }
        return 0;
    }

    private sealed class FFprobeOutput
    {
        [JsonPropertyName("streams")]
        public List<FFprobeStream>? Streams { get; set; }

        [JsonPropertyName("format")]
        public FFprobeFormat? Format { get; set; }
    }

    private sealed class FFprobeFormat
    {
        [JsonPropertyName("start_time")]
        public string? StartTime { get; set; }

        [JsonPropertyName("format_name")]
        public string? FormatName { get; set; }
    }

    private sealed class FFprobeStream
    {
        [JsonPropertyName("codec_name")]
        public string? CodecName { get; set; }

        [JsonPropertyName("profile")]
        public string? Profile { get; set; }

        [JsonPropertyName("width")]
        public int? Width { get; set; }

        [JsonPropertyName("height")]
        public int? Height { get; set; }

        [JsonPropertyName("pix_fmt")]
        public string? PixFmt { get; set; }

        [JsonPropertyName("avg_frame_rate")]
        public string? AvgFrameRate { get; set; }

        [JsonPropertyName("color_primaries")]
        public string? ColorPrimaries { get; set; }

        [JsonPropertyName("color_transfer")]
        public string? ColorTransfer { get; set; }

        [JsonPropertyName("color_space")]
        public string? ColorSpace { get; set; }

        [JsonPropertyName("color_range")]
        public string? ColorRange { get; set; }

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
}
