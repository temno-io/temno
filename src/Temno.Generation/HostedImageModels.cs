using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Temno.Generation;

// Local desktop transport DTOs for the public /v1/images/* JSON. The gateway
// namespace is not referenced from desktop; field names follow the wire.

public static class HostedImageOperations
{
    public const string Generate = "generate_image";
    public const string Edit = "edit_image";
}

public sealed class HostedImageSettingOption
{
    public string Name { get; set; } = "";
    public List<string> AllowedValues { get; set; } = [];
    public string DefaultValue { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string>? Operations { get; set; }
}

public sealed class HostedImageOperationProfile
{
    public string Operation { get; set; } = "";
    public string? Endpoint { get; set; }
    public bool Available { get; set; }
    public string? UnavailableReason { get; set; }
    public int MinInputs { get; set; }
    public int MaxInputs { get; set; }
    public bool SupportsMask { get; set; }
    public int MaxOutputs { get; set; }
    public string PriceNote { get; set; } = "";
}

public sealed class HostedImageProfile
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Family { get; set; } = "";
    public string? Variant { get; set; }
    public List<HostedImageOperationProfile> Operations { get; set; } = [];
    public List<HostedImageSettingOption> Settings { get; set; } = [];
    public List<string> OutputFormats { get; set; } = [];
    public string? DefaultOutputFormat { get; set; }
    public int MaxReferences { get; set; }
    public bool SupportsMask { get; set; }
    public string? UnavailableReason { get; set; }
    public string PriceNote { get; set; } = "";

    public string Label => string.IsNullOrWhiteSpace(Variant) || string.Equals(Variant, Family, StringComparison.OrdinalIgnoreCase)
        ? DisplayName
        : Family + " " + Variant;

    public HostedImageOperationProfile? Operation(string operation)
        => Operations.FirstOrDefault(o => string.Equals(o.Operation, operation, StringComparison.Ordinal));
}

public sealed class HostedImageCatalog
{
    public string Version { get; set; } = "";
    public bool DevelopmentPrices { get; set; }
    public bool Configured { get; set; }
    public bool AcceptJobs { get; set; }
    public bool QualificationOwner { get; set; }
    public List<string> EnabledOperations { get; set; } = [];
    public long MaxUploadBytes { get; set; }
    public List<HostedImageProfile> Models { get; set; } = [];

    public HostedImageProfile? Get(string modelId)
        => Models.FirstOrDefault(m => string.Equals(m.Id, modelId, StringComparison.OrdinalIgnoreCase));

    // Mirrors the server gate: schema/pricing readiness AND (enabled op OR
    // qualification owner). The server re-checks; this only drives honest UI.
    public bool IsAvailable(HostedImageProfile profile, string operation)
    {
        if (!Configured || !AcceptJobs || profile.UnavailableReason is not null) return false;
        var op = profile.Operation(operation);
        if (op is null || !op.Available)
            return false;
        if (QualificationOwner)
            return true;
        return EnabledOperations.Contains(profile.Id + ":" + operation, StringComparer.Ordinal);
    }

    public string? UnavailabilityReason(HostedImageProfile profile, string operation)
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

public sealed class HostedImageAsset
{
    public Guid Id { get; set; }
    public string Sha256 { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public long Bytes { get; set; }
    public string ContentType { get; set; } = "";
}

public sealed class HostedImageUploadResult
{
    public HostedImageAsset Asset { get; set; } = new();
    public string ExpiresAt { get; set; } = "";
}

public sealed class HostedImageQuoteRequest
{
    public string Model { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Prompt { get; set; } = "";
    public JsonObject? Settings { get; set; }
    public List<Guid>? InputIds { get; set; }
    public Guid? MaskId { get; set; }
}

public sealed class HostedImageQuote
{
    public string QuoteId { get; set; } = "";
    public string Model { get; set; } = "";
    public string Operation { get; set; } = "";
    public int Credits { get; set; }
    public string PriceVersion { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public int OutputCount { get; set; }
    public JsonObject? Settings { get; set; }
    public List<Guid>? InputIds { get; set; }
    public Guid? MaskId { get; set; }

    // The server plan binds the full prompt/settings/ordered inputs/mask/price,
    // so any request change invalidates a cached quote. The prompt is compared
    // by the caller (quotes never echo it); everything else is compared here.
    public bool Matches(HostedImageQuoteRequest request)
    {
        if (request is null)
            return false;
        if (!string.Equals(Model, request.Model, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Operation, request.Operation, StringComparison.Ordinal))
            return false;
        if (MaskId != request.MaskId)
            return false;
        var want = request.InputIds ?? [];
        var have = InputIds ?? [];
        if (!want.SequenceEqual(have))
            return false;
        return JsonNode.DeepEquals(Settings ?? new JsonObject(), request.Settings ?? new JsonObject());
    }
}

public sealed class HostedImageJob
{
    public string Id { get; set; } = "";
    public string Model { get; set; } = "";
    public string? Operation { get; set; }
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
