using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temno.Core.Models;

/// <summary>Mac: Models/MediaManifest.swift</summary>
public sealed class MediaManifest
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 2;

    [JsonPropertyName("entries")]
    public List<MediaManifestEntry> Entries { get; set; } = [];

    [JsonPropertyName("folders")]
    public List<MediaFolder> Folders { get; set; } = [];

    public static MediaManifest Decode(ReadOnlySpan<byte> utf8Json)
    {
        // Mac: missing version → 1 (decodeIfPresent ?? 1). Default property is 2 for new manifests.
        var hasVersion = false;
        try
        {
            using var doc = JsonDocument.Parse(utf8Json.ToArray());
            hasVersion = doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("version", out _);
        }
        catch
        {
            // deserialize will throw with a better error
        }

        var m = JsonSerializer.Deserialize<MediaManifest>(utf8Json, JsonOptions.ForProject)
            ?? new MediaManifest();
        m.Entries ??= [];
        m.Folders ??= [];
        if (!hasVersion)
            m.Version = 1;
        return m;
    }

    public byte[] Encode()
        => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions.ForProject);
}

public sealed class MediaFolder
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString().ToUpperInvariant();

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("parentFolderId")]
    public string? ParentFolderId { get; set; }
}

public sealed class MediaManifestEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public ClipType Type { get; set; } = ClipType.video;

    [JsonPropertyName("source")]
    public MediaSource Source { get; set; } = MediaSource.External("/missing");

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("generationInput")]
    public GenerationInput? GenerationInput { get; set; }

    [JsonPropertyName("sourceWidth")]
    public int? SourceWidth { get; set; }

    [JsonPropertyName("sourceHeight")]
    public int? SourceHeight { get; set; }

    [JsonPropertyName("sourceFPS")]
    public double? SourceFps { get; set; }

    [JsonPropertyName("hasAudio")]
    public bool? HasAudio { get; set; }

    [JsonPropertyName("folderId")]
    public string? FolderId { get; set; }

    [JsonPropertyName("cachedRemoteURL")]
    public string? CachedRemoteUrl { get; set; }

    [JsonPropertyName("cachedRemoteURLExpiresAt")]
    public DateTimeOffset? CachedRemoteUrlExpiresAt { get; set; }

    [JsonPropertyName("generationStatus")]
    public string? GenerationStatus { get; set; }

    // Advances whenever a hosted generation changes. Undo keeps the higher stamp, so a finished
    // job cannot be put back to pending. Zero is omitted and means an entry from before the stamp.
    [JsonPropertyName("generationStamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long GenerationStamp { get; set; }

    [JsonPropertyName("importInput")]
    public MediaImportInput? ImportInput { get; set; }
}

public sealed class MediaImportInput
{
    [JsonPropertyName("sourceURL")]
    public string? SourceUrl { get; set; }

    [JsonPropertyName("sourcePath")]
    public string? SourcePath { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }
}

public sealed class GenerationInput
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("aspectRatio")]
    public string AspectRatio { get; set; } = "";

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("quality")]
    public string? Quality { get; set; }

    [JsonPropertyName("imageURLs")]
    public List<string>? ImageUrls { get; set; }

    [JsonPropertyName("numImages")]
    public int? NumImages { get; set; }

    [JsonPropertyName("voice")]
    public string? Voice { get; set; }

    [JsonPropertyName("lyrics")]
    public string? Lyrics { get; set; }

    [JsonPropertyName("styleInstructions")]
    public string? StyleInstructions { get; set; }

    [JsonPropertyName("instrumental")]
    public bool? Instrumental { get; set; }

    [JsonPropertyName("targetLanguage")]
    public string? TargetLanguage { get; set; }

    [JsonPropertyName("generateAudio")]
    public bool? GenerateAudio { get; set; }

    [JsonPropertyName("referenceImageURLs")]
    public List<string>? ReferenceImageUrls { get; set; }

    [JsonPropertyName("referenceVideoURLs")]
    public List<string>? ReferenceVideoUrls { get; set; }

    [JsonPropertyName("referenceAudioURLs")]
    public List<string>? ReferenceAudioUrls { get; set; }

    [JsonPropertyName("imageURLAssetIds")]
    public List<string>? ImageUrlAssetIds { get; set; }

    [JsonPropertyName("referenceImageAssetIds")]
    public List<string>? ReferenceImageAssetIds { get; set; }

    [JsonPropertyName("referenceVideoAssetIds")]
    public List<string>? ReferenceVideoAssetIds { get; set; }

    [JsonPropertyName("referenceAudioAssetIds")]
    public List<string>? ReferenceAudioAssetIds { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("backendJobId")]
    public string? BackendJobId { get; set; }

    [JsonPropertyName("outputIndex")]
    public int? OutputIndex { get; set; }

    [JsonPropertyName("resultURLs")]
    public List<string>? ResultUrls { get; set; }

    // Hosted operation provenance drives recovery routing; legacy/BYOK/fake
    // jobs leave it null. Additive fields preserve older project compatibility.
    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    [JsonPropertyName("settings")]
    public Dictionary<string, string>? Settings { get; set; }

    [JsonPropertyName("sourceAssetIds")]
    public List<string>? SourceAssetIds { get; set; }

    [JsonPropertyName("inputRoles")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? InputRoles { get; set; }

    [JsonPropertyName("maskAssetId")]
    public string? MaskAssetId { get; set; }

    [JsonPropertyName("quoteId")]
    public string? QuoteId { get; set; }

    [JsonPropertyName("quotedCredits")]
    public int? QuotedCredits { get; set; }

    [JsonPropertyName("chargedCredits")]
    public int? ChargedCredits { get; set; }

    // Stable submit idempotency key, persisted before the create call so a
    // network-lost response retries the same submission instead of a new job.
    [JsonPropertyName("submitKey")]
    public string? SubmitKey { get; set; }

    [JsonPropertyName("cancelRequested")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CancelRequested { get; set; }
}

/// <summary>Mac enum MediaSource — Swift encodes as nested single-key objects.</summary>
public abstract record MediaSource
{
    public sealed record ExternalSource(string AbsolutePath) : MediaSource;
    public sealed record ProjectSource(string RelativePath) : MediaSource;

    public static MediaSource External(string absolutePath) => new ExternalSource(absolutePath);
    public static MediaSource Project(string relativePath) => new ProjectSource(relativePath);
}

/// <summary>
/// Swift shape:
/// { "external": { "absolutePath": "..." } } or { "project": { "relativePath": "..." } }
/// </summary>
public sealed class MediaSourceJsonConverter : JsonConverter<MediaSource>
{
    public override MediaSource Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("MediaSource must be an object");

        if (root.TryGetProperty("external", out var ext))
        {
            var path = ext.GetProperty("absolutePath").GetString()
                ?? throw new JsonException("external.absolutePath missing");
            return MediaSource.External(path);
        }

        if (root.TryGetProperty("project", out var proj))
        {
            var path = proj.GetProperty("relativePath").GetString()
                ?? throw new JsonException("project.relativePath missing");
            return MediaSource.Project(path);
        }

        throw new JsonException("MediaSource must be external or project");
    }

    public override void Write(Utf8JsonWriter writer, MediaSource value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value)
        {
            case MediaSource.ExternalSource e:
                writer.WritePropertyName("external");
                writer.WriteStartObject();
                writer.WriteString("absolutePath", e.AbsolutePath);
                writer.WriteEndObject();
                break;
            case MediaSource.ProjectSource p:
                writer.WritePropertyName("project");
                writer.WriteStartObject();
                writer.WriteString("relativePath", p.RelativePath);
                writer.WriteEndObject();
                break;
            default:
                throw new JsonException($"Unknown MediaSource type {value.GetType().Name}");
        }
        writer.WriteEndObject();
    }
}
