using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temno.Generation;

public enum GenerationKind
{
    [JsonPropertyName("video")] Video,
    [JsonPropertyName("image")] Image,
    [JsonPropertyName("audio")] Audio,
    [JsonPropertyName("upscale")] Upscale
}

public sealed class CatalogEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("kind")]
    [JsonConverter(typeof(JsonStringEnumConverter<GenerationKind>))]
    public GenerationKind Kind { get; set; }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("allowedEndpoints")]
    public List<string>? AllowedEndpoints { get; set; }

    [JsonPropertyName("responseShape")]
    public string? ResponseShape { get; set; }

    [JsonPropertyName("paidOnly")]
    public bool PaidOnly { get; set; }

    [JsonPropertyName("creditsPerSecond")]
    public Dictionary<string, double>? CreditsPerSecond { get; set; }

    [JsonPropertyName("creditsPerImage")]
    public Dictionary<string, double>? CreditsPerImage { get; set; }

    [JsonPropertyName("qualities")]
    public List<string>? Qualities { get; set; }

    [JsonPropertyName("imageProfile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HostedImageProfile? ImageProfile { get; set; }

    [JsonPropertyName("quoteRequired")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? QuoteRequired { get; set; }

    // WP-331: a hosted media model's settings and each operation's input roles, so an agent picks values
    // quote_media accepts instead of learning them from refused quotes.
    [JsonPropertyName("settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CatalogSetting>? Settings { get; set; }

    [JsonPropertyName("inputs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, List<CatalogInput>>? Inputs { get; set; }
}

public sealed class CatalogSetting
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    // "choice" (one of values, sent as a string), "boolean", "number" or "string".
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("values")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Values { get; set; }

    // Typed as the gateway reads it; null when the setting is optional and omitted unless set.
    [JsonPropertyName("default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public System.Text.Json.Nodes.JsonNode? Default { get; set; }

    [JsonPropertyName("optional")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Optional { get; set; }

    // The range or format rule for a free-form value.
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    // Null when the setting applies to every operation.
    [JsonPropertyName("operations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Operations { get; set; }
}

public sealed class CatalogInput
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("kinds")]
    public List<string> Kinds { get; set; } = [];

    [JsonPropertyName("min")]
    public int Min { get; set; }

    [JsonPropertyName("max")]
    public int Max { get; set; }
}

public enum BackendGenerationStatus
{
    Queued,
    Running,
    Succeeded,
    Failed
}

public class BackendGenerationStatusConverter : JsonConverter<BackendGenerationStatus>
{
    public override BackendGenerationStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var val = reader.GetString()?.ToLowerInvariant();
        return val switch
        {
            "queued" => BackendGenerationStatus.Queued,
            "running" => BackendGenerationStatus.Running,
            "succeeded" => BackendGenerationStatus.Succeeded,
            "failed" => BackendGenerationStatus.Failed,
            _ => throw new JsonException($"Unknown status: {val}")
        };
    }

    public override void Write(Utf8JsonWriter writer, BackendGenerationStatus value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString().ToLowerInvariant());
    }
}

public class BackendGenerationJob
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("status")]
    [JsonConverter(typeof(BackendGenerationStatusConverter))]
    public BackendGenerationStatus Status { get; set; }

    [JsonPropertyName("resultUrls")]
    public List<string>? ResultUrls { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("costCredits")]
    public int? CostCredits { get; set; }

    [JsonPropertyName("completedAt")]
    public double? CompletedAt { get; set; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(VideoGenerationParams), typeDiscriminator: "video")]
[JsonDerivedType(typeof(ImageGenerationParams), typeDiscriminator: "image")]
[JsonDerivedType(typeof(AudioGenerationParams), typeDiscriminator: "audio")]
[JsonDerivedType(typeof(UpscaleGenerationParams), typeDiscriminator: "upscale")]
public abstract record BackendGenerationParams
{
}

public sealed record VideoGenerationParams : BackendGenerationParams
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = "";

    [JsonPropertyName("duration")]
    public int Duration { get; init; }

    [JsonPropertyName("aspectRatio")]
    public string AspectRatio { get; init; } = "";

    [JsonPropertyName("resolution")]
    public string? Resolution { get; init; }

    [JsonPropertyName("sourceVideoURL")]
    public string? SourceVideoUrl { get; init; }

    [JsonPropertyName("startFrameURL")]
    public string? StartFrameUrl { get; init; }

    [JsonPropertyName("endFrameURL")]
    public string? EndFrameUrl { get; init; }

    [JsonPropertyName("referenceImageURLs")]
    public List<string> ReferenceImageUrls { get; init; } = new();

    [JsonPropertyName("referenceVideoURLs")]
    public List<string> ReferenceVideoUrls { get; init; } = new();

    [JsonPropertyName("referenceAudioURLs")]
    public List<string> ReferenceAudioUrls { get; init; } = new();

    [JsonPropertyName("generateAudio")]
    public bool GenerateAudio { get; init; } = true;
}

public sealed record ImageGenerationParams : BackendGenerationParams
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = "";

    [JsonPropertyName("aspectRatio")]
    public string AspectRatio { get; init; } = "";

    [JsonPropertyName("resolution")]
    public string? Resolution { get; init; }

    [JsonPropertyName("quality")]
    public string? Quality { get; init; }

    [JsonPropertyName("imageURLs")]
    public List<string> ImageUrls { get; init; } = new();

    [JsonPropertyName("numImages")]
    public int NumImages { get; init; } = 1;
}

public sealed record AudioGenerationParams : BackendGenerationParams
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; init; } = "";

    [JsonPropertyName("voice")]
    public string? Voice { get; init; }

    [JsonPropertyName("lyrics")]
    public string? Lyrics { get; init; }

    [JsonPropertyName("styleInstructions")]
    public string? StyleInstructions { get; init; }

    [JsonPropertyName("instrumental")]
    public bool Instrumental { get; init; }

    [JsonPropertyName("durationSeconds")]
    public int? DurationSeconds { get; init; }

    [JsonPropertyName("videoURL")]
    public string? VideoUrl { get; init; }
}

public sealed record UpscaleGenerationParams : BackendGenerationParams
{
    [JsonPropertyName("sourceURL")]
    public string SourceUrl { get; init; } = "";

    [JsonPropertyName("durationSeconds")]
    public int DurationSeconds { get; init; }
}
