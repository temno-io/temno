using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Temno.Core.Export;

namespace Temno.Media;

/// <summary>
/// Reads embedded source timecode tags via ffprobe (format/stream tags.timecode).
/// Windows substitute for Mac AVAsset timecode tracks (WP-107).
/// </summary>
public static class FfmpegSourceTimecodeReader
{
    public static async Task<SourceTimecode?> ReadAsync(
        string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

        var ffprobePath = FFmpegResolver.GetFFprobePath();
        var arguments = $"-v error -show_format -show_streams -of json \"{path}\"";

        using var process = new Process();
        process.StartInfo.FileName = ffprobePath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        if (!process.Start()) return null;
        using var cancellationRegistration = ProcessCancellation.KillOnCancellation(process, cancellationToken);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0) return null;

        try
        {
            var output = JsonSerializer.Deserialize<ProbeDoc>(stdout, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
            if (output is null) return null;

            var quanta = 30;
            var video = output.Streams?.FirstOrDefault(s => s.CodecType == "video");
            if (video is not null)
            {
                var fps = ParseFps(video.AvgFrameRate) ?? ParseFps(video.RFrameRate);
                if (fps is > 0)
                    quanta = Math.Max(1, (int)Math.Round(fps.Value));
            }

            // Prefer data/timecode stream tags, then video stream, then format.
            foreach (var stream in output.Streams ?? [])
            {
                if (TryFromTags(stream.Tags, quanta, out var tc)) return tc;
            }
            if (TryFromTags(output.Format?.Tags, quanta, out var formatTc)) return formatTc;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryFromTags(Dictionary<string, string>? tags, int quanta, out SourceTimecode tc)
    {
        tc = default;
        if (tags is null) return false;
        foreach (var key in new[] { "timecode", "TIMECODE", "Timecode" })
        {
            if (tags.TryGetValue(key, out var raw) && SourceTimecode.TryParseSmpte(raw, quanta, out tc))
                return true;
        }
        // Case-insensitive fallback.
        foreach (var (k, v) in tags)
        {
            if (k.Equals("timecode", StringComparison.OrdinalIgnoreCase)
                && SourceTimecode.TryParseSmpte(v, quanta, out tc))
                return true;
        }
        return false;
    }

    private static double? ParseFps(string? fpsStr)
    {
        if (string.IsNullOrEmpty(fpsStr)) return null;
        var parts = fpsStr.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var den)
            && den > 0)
        {
            var res = num / den;
            return res > 0 ? res : null;
        }
        if (double.TryParse(fpsStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var val) && val > 0)
            return val;
        return null;
    }

    private sealed class ProbeDoc
    {
        [JsonPropertyName("streams")]
        public List<ProbeStream>? Streams { get; set; }

        [JsonPropertyName("format")]
        public ProbeFormat? Format { get; set; }
    }

    private sealed class ProbeStream
    {
        [JsonPropertyName("codec_type")]
        public string? CodecType { get; set; }

        [JsonPropertyName("r_frame_rate")]
        public string? RFrameRate { get; set; }

        [JsonPropertyName("avg_frame_rate")]
        public string? AvgFrameRate { get; set; }

        [JsonPropertyName("tags")]
        public Dictionary<string, string>? Tags { get; set; }
    }

    private sealed class ProbeFormat
    {
        [JsonPropertyName("tags")]
        public Dictionary<string, string>? Tags { get; set; }
    }
}
