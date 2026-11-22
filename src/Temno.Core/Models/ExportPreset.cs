using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temno.Core.Models;

[JsonConverter(typeof(VideoCodecConverter))]
public enum VideoCodec
{
    H264,
    H265,
    ProRes,
    /// <summary>HEVC Main10 BT.2020 HLG (Mac VideoCodec.hdr / ExportFormat.hevcHDR).</summary>
    HevcHdr,
}

public class VideoCodecConverter : JsonConverter<VideoCodec>
{
    public override VideoCodec Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var val = reader.GetString();
        return val switch
        {
            "H.264" => VideoCodec.H264,
            "H.265" => VideoCodec.H265,
            "ProRes" => VideoCodec.ProRes,
            "HEVC 10-bit HDR" or "HEVC HDR" or "HDR" => VideoCodec.HevcHdr,
            _ => throw new JsonException($"Unknown codec: {val}")
        };
    }

    public override void Write(Utf8JsonWriter writer, VideoCodec value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            VideoCodec.H264 => "H.264",
            VideoCodec.H265 => "H.265",
            VideoCodec.ProRes => "ProRes",
            VideoCodec.HevcHdr => "HEVC 10-bit HDR",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        });
    }
}

[JsonConverter(typeof(ExportResolutionConverter))]
public enum ExportResolution
{
    R720p,
    R1080p,
    R1440p,
    R4k,
    MatchTimeline
}

public class ExportResolutionConverter : JsonConverter<ExportResolution>
{
    public override ExportResolution Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var val = reader.GetString();
        return val switch
        {
            "720p" => ExportResolution.R720p,
            "1080p" => ExportResolution.R1080p,
            "2K" => ExportResolution.R1440p,
            "4K" => ExportResolution.R4k,
            "Match Timeline" => ExportResolution.MatchTimeline,
            _ => throw new JsonException($"Unknown resolution: {val}")
        };
    }

    public override void Write(Utf8JsonWriter writer, ExportResolution value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            ExportResolution.R720p => "720p",
            ExportResolution.R1080p => "1080p",
            ExportResolution.R1440p => "2K",
            ExportResolution.R4k => "4K",
            ExportResolution.MatchTimeline => "Match Timeline",
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        });
    }
}

public sealed class ExportPreset
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("codec")]
    public VideoCodec Codec { get; set; } = VideoCodec.H264;

    [JsonPropertyName("resolution")]
    public ExportResolution Resolution { get; set; } = ExportResolution.MatchTimeline;

    [JsonPropertyName("bitrateMbps")]
    public double? BitrateMbps { get; set; }

    [JsonPropertyName("maxRenderQuality")]
    public bool MaxRenderQuality { get; set; } = false;

    [JsonPropertyName("maxBitDepth")]
    public bool MaxBitDepth { get; set; } = false;

    /// <summary>
    /// WP-167. When both are set, the exporter uses this raster instead of
    /// <see cref="Resolution"/>. Null keeps today's preset ladder (JSON omits the keys).
    /// </summary>
    [JsonPropertyName("customWidth")]
    public int? CustomWidth { get; set; }

    [JsonPropertyName("customHeight")]
    public int? CustomHeight { get; set; }

    /// <summary>
    /// Optional output frame rate override (1–120 fps, including NTSC broadcast rates like 23.976 /
    /// 29.97 / 59.94). Null matches the timeline's own frame rate.
    /// </summary>
    [JsonPropertyName("customFps")]
    public double? CustomFps { get; set; }

    public ExportPreset()
    {
        Id = Guid.NewGuid().ToString();
    }

    public ExportPreset(string name, VideoCodec codec = VideoCodec.H264, ExportResolution resolution = ExportResolution.MatchTimeline, double? bitrateMbps = null, bool maxRenderQuality = false, bool maxBitDepth = false)
    {
        Id = Guid.NewGuid().ToString();
        Name = name;
        Codec = codec;
        Resolution = resolution;
        BitrateMbps = bitrateMbps;
        MaxRenderQuality = maxRenderQuality;
        MaxBitDepth = maxBitDepth;
    }
}
