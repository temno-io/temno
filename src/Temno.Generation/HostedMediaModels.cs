using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Temno.Generation;

// Local desktop transport DTOs for the public /v1/media/* JSON. The gateway
// namespace is not referenced from desktop; field names follow the wire.

public static class HostedMediaOperations
{
    public const string TextToVideo = "text_to_video";
    public const string ImageToVideo = "image_to_video";
    public const string ReferenceToVideo = "reference_to_video";
    public const string VideoEdit = "video_edit";
    public const string MotionControl = "motion_control";
    public const string LipSync = "lip_sync";
    public const string TextToMusic = "text_to_music";
    public const string VideoToMusic = "video_to_music";
    public const string TextToSfx = "text_to_sfx";
    public const string VideoToSfx = "video_to_sfx";
    public const string TextToSpeech = "text_to_speech";
    public const string Dubbing = "dubbing";
    public const string AudioIsolation = "audio_isolation";
    public const string TextToAudio = "text_to_audio";
    public const string UpscaleVideo = "upscale_video";
    public const string UpscaleImage = "upscale_image";

    public static readonly string[] All =
    [
        TextToVideo, ImageToVideo, ReferenceToVideo, VideoEdit, MotionControl, LipSync,
        TextToMusic, VideoToMusic, TextToSfx, VideoToSfx, TextToSpeech, Dubbing,
        AudioIsolation, TextToAudio, UpscaleVideo, UpscaleImage,
    ];
}

public static class HostedMediaRoles
{
    public const string FirstFrame = "first_frame";
    public const string LastFrame = "last_frame";
    public const string ReferenceImage = "reference_image";
    public const string SourceVideo = "source_video";
    public const string SourceAudio = "source_audio";
}

public sealed class HostedMediaSettingOption
{
    public string Name { get; set; } = "";
    public List<string> AllowedValues { get; set; } = [];
    public string DefaultValue { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string>? Operations { get; set; }

    // The catalog sends a free integer duration with no allowed values and states the range
    // only as "Integer {min}..{max} ..." in the description. A short range becomes a list so
    // the composer offers the same dropdown as a priced duration set.
    public List<string> ChoiceValues(int maxChoices = 30)
    {
        if (AllowedValues.Count > 0 || Name != "duration") return AllowedValues;
        var range = Regex.Match(Description, @"^Integer (\d+)\.\.(\d+)\b");
        if (!range.Success
            || !int.TryParse(range.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var min)
            || !int.TryParse(range.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var max)
            || max < min || max - min + 1 > maxChoices) return AllowedValues;
        return [.. Enumerable.Range(min, max - min + 1).Select(v => v.ToString(CultureInfo.InvariantCulture))];
    }
}

public sealed class HostedMediaInputSlot
{
    public string Role { get; set; } = "";
    public List<string> Kinds { get; set; } = [];
    public int Min { get; set; }
    public int Max { get; set; }
}

public sealed class HostedMediaOperationProfile
{
    public string Operation { get; set; } = "";
    public string? Endpoint { get; set; }
    public bool Available { get; set; }
    public string? UnavailableReason { get; set; }
    public List<HostedMediaInputSlot> Inputs { get; set; } = [];
    public int MaxOutputs { get; set; }
    public string PriceNote { get; set; } = "";
    public string PricingUnit { get; set; } = "";
    // PromptMaxLength <= 0 means the endpoint has no prompt field: any prompt
    // is rejected, never silently dropped. Otherwise empty is allowed iff
    // PromptRequired is false (optional prompts are always sent when present).
    public bool PromptRequired { get; set; } = true;
    public int PromptMaxLength { get; set; } = 2000;
    public int PromptMinLength { get; set; }

    public bool RequiresRole(string role, out HostedMediaInputSlot? slot)
    {
        slot = Inputs.FirstOrDefault(s => string.Equals(s.Role, role, StringComparison.Ordinal));
        return slot is not null && slot.Min > 0;
    }
}

public sealed class HostedMediaProfile
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Family { get; set; } = "";
    public string? Variant { get; set; }
    public string Category { get; set; } = "";
    public string Route { get; set; } = "";
    public List<HostedMediaOperationProfile> Operations { get; set; } = [];
    public List<HostedMediaSettingOption> Settings { get; set; } = [];
    public List<string> OutputKinds { get; set; } = [];
    public string? UnavailableReason { get; set; }
    public string PriceNote { get; set; } = "";

    public string Label => string.IsNullOrWhiteSpace(Variant)
        ? DisplayName
        : Family + " " + Variant;

    public HostedMediaOperationProfile? Operation(string operation)
        => Operations.FirstOrDefault(o => string.Equals(o.Operation, operation, StringComparison.Ordinal));
}

public sealed class HostedMediaUploadLimits
{
    public long Image { get; set; }
    public long Video { get; set; }
    public long Audio { get; set; }
}

public sealed class HostedMediaCatalog
{
    public string Version { get; set; } = "";
    public bool DevelopmentPrices { get; set; }
    public bool Configured { get; set; }
    public bool AcceptJobs { get; set; }
    public bool QualificationOwner { get; set; }
    public List<string> EnabledOperations { get; set; } = [];
    public HostedMediaUploadLimits MaxUploadBytes { get; set; } = new();
    public List<HostedMediaProfile> Models { get; set; } = [];

    public HostedMediaProfile? Get(string modelId)
        => Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));

    // Mirrors the server gate: schema/pricing readiness AND (enabled op OR
    // qualification owner). The server re-checks; this only drives honest UI.
    public bool IsAvailable(HostedMediaProfile profile, string operation)
    {
        if (!Configured || !AcceptJobs || profile.UnavailableReason is not null) return false;
        var op = profile.Operation(operation);
        if (op is null || !op.Available)
            return false;
        if (QualificationOwner)
            return true;
        return EnabledOperations.Contains(profile.Id + ":" + operation, StringComparer.Ordinal);
    }

    public string? UnavailabilityReason(HostedMediaProfile profile, string operation)
    {
        var op = profile.Operation(operation);
        if (op is null)
            return "This model has no " + operation + " operation.";
        if (!op.Available)
            return string.IsNullOrWhiteSpace(op.UnavailableReason)
                ? "This operation is not available for " + profile.Label + "."
                : op.UnavailableReason;
        if (!IsAvailable(profile, operation))
            return "This operation is not enabled on the gateway yet.";
        return null;
    }
}

public sealed class HostedMediaAsset
{
    public Guid Id { get; set; }
    public string Sha256 { get; set; } = "";
    public string Kind { get; set; } = "";
    public int? Width { get; set; }
    public int? Height { get; set; }
    public decimal? DurationSeconds { get; set; }
    public decimal? Fps { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public long Bytes { get; set; }
    public string ContentType { get; set; } = "";
}

public sealed class HostedMediaUploadResult
{
    public HostedMediaAsset Asset { get; set; } = new();
    public string ExpiresAt { get; set; } = "";
}

public sealed class HostedMediaInputRef
{
    public string Role { get; set; } = "";
    public Guid AssetId { get; set; }
}

public sealed class HostedMediaQuoteRequest
{
    public string Model { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Prompt { get; set; } = "";
    public JsonObject? Settings { get; set; }
    public List<HostedMediaInputRef>? Inputs { get; set; }
}

public sealed class HostedMediaQuote
{
    public string? OutputNote { get; set; }
    public string QuoteId { get; set; } = "";
    public string Model { get; set; } = "";
    public string Operation { get; set; } = "";
    public string ResultKind { get; set; } = "";
    public int Credits { get; set; }
    public string PriceVersion { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int OutputCount { get; set; }
    public JsonObject? Settings { get; set; }
    public List<HostedMediaInputRef>? Inputs { get; set; }

    // The server plan binds the full prompt/settings/ordered roles+inputs/
    // price, so any request change invalidates a cached quote. The prompt is
    // compared by the caller (quotes never echo it); everything else here.
    public bool Matches(HostedMediaQuoteRequest request)
    {
        if (request is null)
            return false;
        if (!string.Equals(Model, request.Model, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Operation, request.Operation, StringComparison.Ordinal))
            return false;
        var want = request.Inputs ?? [];
        var have = Inputs ?? [];
        if (want.Count != have.Count)
            return false;
        for (int i = 0; i < want.Count; i++)
        {
            if (!string.Equals(want[i].Role, have[i].Role, StringComparison.Ordinal)
                || want[i].AssetId != have[i].AssetId)
                return false;
        }
        return JsonNode.DeepEquals(Settings ?? new JsonObject(), request.Settings ?? new JsonObject());
    }
}

public sealed class HostedMediaJob
{
    public string Id { get; set; } = "";
    public string Model { get; set; } = "";
    public string? Operation { get; set; }
    public string ResultKind { get; set; } = "";
    public string Status { get; set; } = "";
    public long? CostCredits { get; set; }
    public string? QuoteId { get; set; }
    public string? Error { get; set; }
    public bool ArtifactExpired { get; set; }
    public List<string>? ResultUrls { get; set; }
    public string? CreatedAt { get; set; }
    public string? CompletedAt { get; set; }

    [JsonIgnore]
    public bool IsTerminal => Status is "succeeded" or "failed" or "canceled";

    public BackendGenerationJob ToBackendJob()
    {
        var status = Status switch
        {
            "succeeded" => BackendGenerationStatus.Succeeded,
            "failed" or "canceled" => BackendGenerationStatus.Failed,
            "queued" => BackendGenerationStatus.Queued,
            _ => BackendGenerationStatus.Running,
        };
        return new BackendGenerationJob
        {
            Id = Id,
            Status = status,
            ResultUrls = ResultUrls,
            CostCredits = CostCredits is long credits ? (int)Math.Min(credits, int.MaxValue) : null,
            ErrorMessage = string.IsNullOrWhiteSpace(Error)
                ? null
                : JsonSerializer.Serialize(new { code = Error }),
        };
    }
}
