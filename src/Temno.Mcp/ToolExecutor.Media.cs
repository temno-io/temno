using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Temno.Core.Account;
using Temno.Core.Generation;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Generation;
using Temno.Media;

namespace Temno.Mcp;

// WP-242 hosted video/audio/upscale orchestration. Mirrors ToolExecutor.Images.cs:
// MCP tools (quote_media, generate_video, generate_audio, upscale_media) and the
// native generation composer share this implementation. Hosted generation is
// quote-only (WP-329): every hosted submit routes here, and BYOK/fake calls keep
// their direct paths. Client-quoted durations are never trusted: the server
// prices on probed media and placeholders take real metadata at landing.

public static partial class ToolExecutor
{
    public static HostedMediaClient? MediaClientOverride { get; set; }

    private static readonly HashSet<string> VideoOperations = new(StringComparer.Ordinal)
    {
        HostedMediaOperations.TextToVideo, HostedMediaOperations.ImageToVideo,
        HostedMediaOperations.ReferenceToVideo, HostedMediaOperations.VideoEdit,
        HostedMediaOperations.MotionControl, HostedMediaOperations.LipSync,
    };

    private static readonly HashSet<string> AudioOperations = new(StringComparer.Ordinal)
    {
        HostedMediaOperations.TextToMusic, HostedMediaOperations.VideoToMusic,
        HostedMediaOperations.TextToSfx, HostedMediaOperations.VideoToSfx,
        HostedMediaOperations.TextToSpeech, HostedMediaOperations.Dubbing,
        HostedMediaOperations.AudioIsolation, HostedMediaOperations.TextToAudio,
    };

    private static readonly HashSet<string> UpscaleOperations = new(StringComparer.Ordinal)
    {
        HostedMediaOperations.UpscaleVideo, HostedMediaOperations.UpscaleImage,
    };

    private static readonly HashSet<string> MediaRoles = new(StringComparer.Ordinal)
    {
        HostedMediaRoles.FirstFrame, HostedMediaRoles.LastFrame, HostedMediaRoles.ReferenceImage,
        HostedMediaRoles.SourceVideo, HostedMediaRoles.SourceAudio,
    };

    private sealed record MediaQuotedInput(string Role, string MediaRef, string Sha256);

    private sealed record MediaQuoteEntry(
        HostedMediaQuote Quote,
        HostedMediaProfile Profile,
        string Category,
        string Operation,
        string Prompt,
        string Fingerprint,
        string? ProjectPath,
        List<MediaQuotedInput> Inputs,
        DateTimeOffset AcceptedAt);

    private static readonly Dictionary<string, MediaQuoteEntry> _mediaQuotes = new(StringComparer.Ordinal);
    private static readonly object _mediaQuoteLock = new();
    private static HostedMediaCatalog? _mediaCatalog;
    private static DateTimeOffset _mediaCatalogAt;
    private static string? _mediaCatalogIdentity;
    private static readonly object _mediaCatalogLock = new();

    public static bool IsHostedMediaConfigured()
    {
        if (MediaClientOverride is not null)
            return true;
        var (_, _, useHosted, _) = ReadGenerationConfig();
        return useHosted;
    }

    public static HostedMediaClient? TryGetMediaClient()
    {
        if (MediaClientOverride is not null)
            return MediaClientOverride;
        var (_, _, useHosted, _) = ReadGenerationConfig();
        if (!useHosted)
            return null;
        var gateway = HostedEndpoints.ResolveGatewayUrl();
        if (string.IsNullOrWhiteSpace(gateway))
            return null;
        return new HostedMediaClient(gateway, () => HostedRuntime.AccessToken);
    }

    public static HostedMediaClient GetMediaClient()
        => TryGetMediaClient()
            ?? throw new InvalidOperationException(
                "Hosted media generation needs a signed-in account and a gateway URL. " +
                "BYOK and demo generation do not use media quotes.");

    public static void ResetMediaCatalog()
    {
        lock (_mediaCatalogLock)
        {
            _mediaCatalog = null;
            _mediaCatalogAt = default;
            _mediaCatalogIdentity = null;
        }
    }

    public static void ResetMediaQuotes()
    {
        lock (_mediaQuoteLock)
        {
            _mediaQuotes.Clear();
            _mediaUploads.Clear();
        }
    }

    public static async Task<HostedMediaCatalog> GetMediaCatalogAsync(CancellationToken cancellationToken = default)
    {
        var identity = ReadGenerationConfig().Fingerprint + "|" + MediaClientOverride?.BaseUrl;
        lock (_mediaCatalogLock)
        {
            if (_mediaCatalog is not null && _mediaCatalogIdentity == identity && DateTimeOffset.UtcNow - _mediaCatalogAt < TimeSpan.FromMinutes(5))
                return _mediaCatalog;
        }
        var catalog = await GetMediaClient().GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (identity != ReadGenerationConfig().Fingerprint + "|" + MediaClientOverride?.BaseUrl)
            throw new InvalidOperationException("The account changed while loading models. Please try again.");
        lock (_mediaCatalogLock)
        {
            _mediaCatalog = catalog;
            _mediaCatalogAt = DateTimeOffset.UtcNow;
            _mediaCatalogIdentity = identity;
        }
        return catalog;
    }

    public static bool IsMediaOperation(string? operation)
        => operation is not null
            && (VideoOperations.Contains(operation) || AudioOperations.Contains(operation) || UpscaleOperations.Contains(operation));

    public static string? MediaCategoryOf(string? operation)
    {
        if (operation is null)
            return null;
        if (VideoOperations.Contains(operation))
            return "video";
        if (AudioOperations.Contains(operation))
            return "audio";
        if (UpscaleOperations.Contains(operation))
            return "upscale";
        return null;
    }

    public static bool IsMediaJob(MediaManifestEntry entry)
        => IsMediaOperation(entry?.GenerationInput?.Operation);

    public static Guid SubmitMediaKeyFor(string placeholderId)
        => StableGuid("temno-media-job-v1", placeholderId);

    // Deterministic manifest id per backend job + output index, so a retry or a
    // reopen upserts the same entries instead of duplicating them. A separate
    // purpose from image siblings so the two kinds can never collide.
    public static string SiblingMediaOutputId(string jobKey, int index)
        => StableGuid("temno-media-output-v1", jobKey + "#" + index).ToString().ToUpperInvariant();

    // Hosted media catalog rows for list_models. A failing media catalog never
    // breaks the rest of the list; quote_media surfaces the real error.
    // Async so the only blocking call lives in the already-listed ListModels.
    private static async Task<List<CatalogEntry>> ListHostedMediaModelsAsync()
    {
        try
        {
            if (!IsHostedMediaConfigured())
                return [];
            var catalog = await GetMediaCatalogAsync().ConfigureAwait(false);
            return catalog.Models.Select(profile =>
            {
                var ops = profile.Operations
                    .Where(o => catalog.IsAvailable(profile, o.Operation))
                    .Select(o => o.Operation)
                    .ToList();
                GenerationKind kind = string.Equals(profile.Category, "audio", StringComparison.OrdinalIgnoreCase)
                    ? GenerationKind.Audio
                    : string.Equals(profile.Category, "upscale", StringComparison.OrdinalIgnoreCase)
                        ? GenerationKind.Upscale
                        : GenerationKind.Video;
                return new CatalogEntry
                {
                    Id = profile.Id,
                    Kind = kind,
                    DisplayName = ops.Count > 0
                        ? $"{profile.DisplayName} ({string.Join(", ", ops)})"
                        : $"{profile.DisplayName} (unavailable)",
                    PaidOnly = true,
                    AllowedEndpoints = ops,
                    QuoteRequired = true,
                    // WP-331: the allowed values before the first quote, not after a refused one.
                    Settings = ops.Count == 0 ? null : profile.Settings
                        .Where(o => o.Operations is not { Count: > 0 } limited || limited.Any(ops.Contains))
                        .Select(o => new CatalogSetting
                        {
                            Name = o.Name,
                            Type = MediaSettingType(o),
                            Values = o.AllowedValues.Count > 0 ? o.AllowedValues : null,
                            Default = TypedMediaDefault(o),
                            Optional = o.DefaultValue == OmittedMediaDefault,
                            Description = string.IsNullOrWhiteSpace(o.Description) ? null : o.Description,
                            Operations = o.Operations is { Count: > 0 } limited ? limited.Where(ops.Contains).ToList() : null,
                        }).ToList() is { Count: > 0 } settings ? settings : null,
                    Inputs = profile.Operations
                        .Where(o => ops.Contains(o.Operation) && o.Inputs.Count > 0)
                        .ToDictionary(o => o.Operation, o => o.Inputs
                            .Select(i => new CatalogInput { Role = i.Role, Kinds = i.Kinds, Min = i.Min, Max = i.Max }).ToList())
                        is { Count: > 0 } inputs ? inputs : null,
                };
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static object QuoteMedia(JsonObject args, ProjectSession? boundProject = null, HostedMediaClient? boundClient = null)
    {
        var session = boundProject ?? GetSession();
        var client = boundClient ?? GetMediaClient();
        var requestIdentity = ReadGenerationConfig().Fingerprint;
        var prompt = args["prompt"]?.GetValue<string>() ?? "";
        var modelId = args["model"]?.GetValue<string>()
            ?? throw new InvalidOperationException("model is required. Pick one from the hosted media catalog.");
        var operation = args["operation"]?.GetValue<string>()
            ?? throw new InvalidOperationException("operation is required. Pick one from the hosted media catalog.");
        if (!IsMediaOperation(operation))
            throw new InvalidOperationException($"Unknown hosted media operation '{operation}'. Pick one from the hosted media catalog.");

        var catalog = GetMediaCatalogAsync().GetAwaiter().GetResult();
        var profile = catalog.Get(modelId)
            ?? throw new InvalidOperationException($"Unknown hosted media model '{modelId}'.");
        if (catalog.UnavailabilityReason(profile, operation) is { } reason)
            throw new InvalidOperationException(reason);
        var op = profile.Operation(operation)
            ?? throw new InvalidOperationException($"This model has no {operation} operation.");

        var settings = ParseMediaSettings(args["settings"], profile, operation);
        var rawInputs = ParseMediaInputs(args["inputs"]);
        if (string.IsNullOrWhiteSpace(prompt) && op.PromptRequired)
            throw new InvalidOperationException("prompt is required for this operation. Input-only operations may leave it empty.");
        if (prompt.Length > op.PromptMaxLength)
            throw new InvalidOperationException(op.PromptMaxLength == 0
                ? "This operation does not accept a prompt."
                : $"This operation accepts up to {op.PromptMaxLength} prompt characters.");
        if (prompt.Length > 0 && prompt.Length < op.PromptMinLength)
            throw new InvalidOperationException($"This operation needs at least {op.PromptMinLength} prompt characters.");
        var validated = ValidateMediaInputs(session, rawInputs, op, profile);

        var uploaded = UploadMediaAssetsAsync(session, client, validated, requestIdentity, CancellationToken.None)
            .GetAwaiter().GetResult();

        // The quote binds this exact request; uploads are reused, never repeated.
        var request = new HostedMediaQuoteRequest
        {
            Model = profile.Id,
            Operation = operation,
            Prompt = prompt,
            Settings = settings,
            Inputs = uploaded.Select(u => new HostedMediaInputRef { Role = u.Role, AssetId = u.AssetId }).ToList(),
        };
        var quote = client.QuoteAsync(request).GetAwaiter().GetResult();
        var (_, _, _, fingerprint) = ReadGenerationConfig();
        if (fingerprint != requestIdentity)
            throw new InvalidOperationException("The account or connection changed while checking the price. Please try again.");
        lock (_mediaQuoteLock)
        {
            foreach (var stale in _mediaQuotes
                .Where(kv => DateTimeOffset.UtcNow - kv.Value.AcceptedAt > TimeSpan.FromHours(24))
                .Select(kv => kv.Key).ToList())
                _mediaQuotes.Remove(stale);
            _mediaQuotes[quote.QuoteId] = new MediaQuoteEntry(
                quote, profile, profile.Category, operation, prompt, fingerprint,
                session.PackagePath, uploaded.Select(u => new MediaQuotedInput(u.Role, u.MediaRef, u.Sha256)).ToList(),
                DateTimeOffset.UtcNow);
        }

        var result = new Dictionary<string, object?>
        {
            ["quoteId"] = quote.QuoteId,
            ["model"] = quote.Model,
            ["operation"] = quote.Operation,
            ["credits"] = quote.Credits,
            ["priceVersion"] = quote.PriceVersion,
            ["expiresAt"] = quote.ExpiresAt,
            ["fingerprint"] = quote.Fingerprint,
            ["outputCount"] = quote.OutputCount,
            ["outputNote"] = quote.OutputNote,
            ["settings"] = quote.Settings,
            ["inputs"] = quote.Inputs?.Select(i => new Dictionary<string, string> { ["role"] = i.Role, ["assetId"] = i.AssetId.ToString("D") }).ToList(),
            ["developmentPrices"] = catalog.DevelopmentPrices,
        };
        if (catalog.DevelopmentPrices)
            result["note"] = "Development pricing: the quoted credits are reserved on submission and deducted on delivery.";
        return result;
    }

    // Reads Temno-named settings only: there are no legacy top-level aliases on
    // this path. Unknown keys and per-operation mismatches are rejected, and the
    // server additionally validates with invalid_request. Provider durations and
    // prices are server-probed; no duration-like setting is trusted for pricing.
    // WP-331: the gateway's scalar rules, which MediaDefaultsTests pins for every shipped model: a choice is sent as
    // a string, a free-form setting takes the type of its default, and "(omitted)" marks an optional setting with no
    // default (its description says the type).
    private const string OmittedMediaDefault = "(omitted)";

    private static string MediaSettingType(HostedMediaSettingOption option) =>
        option.AllowedValues.Count > 0 ? "choice"
        : option.DefaultValue is "true" or "false" ? "boolean"
        : decimal.TryParse(option.DefaultValue, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
          || (option.DefaultValue == OmittedMediaDefault && option.Description.StartsWith("Optional integer", StringComparison.OrdinalIgnoreCase)) ? "number"
        : "string";

    private static JsonNode? TypedMediaDefault(HostedMediaSettingOption option)
    {
        if (option.DefaultValue == OmittedMediaDefault)
            return null;
        return MediaSettingType(option) switch
        {
            "boolean" => JsonValue.Create(option.DefaultValue == "true"),
            "number" => MediaNumber(option.DefaultValue),
            _ => JsonValue.Create(option.DefaultValue),
        };
    }

    private static JsonValue? MediaNumber(string text)
    {
        var style = System.Globalization.NumberStyles.Float;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (long.TryParse(text, System.Globalization.NumberStyles.Integer, culture, out var whole))
            return JsonValue.Create(whole);
        return decimal.TryParse(text, style, culture, out var number) ? JsonValue.Create(number) : null;
    }

    // A value of the wrong JSON type for its setting is converted when the conversion is exact (8 for a choice
    // listing "8", "5" for a number, "true" for a boolean); anything else is left for the gateway to refuse.
    private static JsonNode CoerceMediaSetting(HostedMediaSettingOption option, JsonValue value)
    {
        var isString = value.TryGetValue<string>(out var text);
        switch (MediaSettingType(option))
        {
            case "choice" when !isString:
                return JsonValue.Create(value.ToJsonString());
            case "boolean" when isString && text is "true" or "false":
                return JsonValue.Create(text == "true");
            case "number" when isString && MediaNumber(text!.Trim()) is { } number:
                return number;
            default:
                return value.DeepClone();
        }
    }

    private static JsonObject ParseMediaSettings(JsonNode? node, HostedMediaProfile profile, string operation)
    {
        JsonObject settings;
        if (node is null)
            settings = new JsonObject();
        else if (node is JsonObject obj)
            settings = (JsonObject)obj.DeepClone();
        else
            throw new InvalidOperationException("settings must be an object of Temno setting names to values.");

        foreach (var kv in settings.ToList())
        {
            var option = profile.Settings.FirstOrDefault(o => string.Equals(o.Name, kv.Key, StringComparison.Ordinal)
                && (o.Operations is not { Count: > 0 } || o.Operations.Contains(operation, StringComparer.Ordinal)));
            if (option is null)
                throw new InvalidOperationException(
                    $"Setting '{kv.Key}' is not supported by {profile.DisplayName}.");
            if (option.Operations is { Count: > 0 } scoped
                && !scoped.Contains(operation, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"Setting '{kv.Key}' is not supported for {operation}.");
            if (kv.Value is null || (kv.Value is JsonValue j && j.ToString() == "null"))
            {
                settings.Remove(kv.Key);
                continue;
            }
            if (kv.Value is not JsonValue scalar)
                throw new InvalidOperationException($"Setting '{kv.Key}' must be a single value.");
            settings[kv.Key] = CoerceMediaSetting(option, scalar);
            if (option.AllowedValues.Count > 0)
            {
                var text = kv.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : kv.Value.ToJsonString();
                if (text is null || !option.AllowedValues.Contains(text, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        $"Setting '{kv.Key}' must be one of [{string.Join(", ", option.AllowedValues)}]; got '{kv.Value}'.");
            }
        }
        return settings;
    }

    private static List<(string Role, string MediaRef)> ParseMediaInputs(JsonNode? node)
    {
        var parsed = new List<(string Role, string MediaRef)>();
        if (node is null)
            return parsed;
        if (node is not JsonArray array)
            throw new InvalidOperationException("inputs must be an array of {role, mediaRef} objects.");
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
                throw new InvalidOperationException("Each input must be a {role, mediaRef} object.");
            var role = obj["role"]?.GetValue<string>();
            var mediaRef = obj["mediaRef"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(role) || !MediaRoles.Contains(role))
                throw new InvalidOperationException(
                    $"Unknown input role '{role}'. Roles: {string.Join(", ", MediaRoles)}.");
            if (string.IsNullOrWhiteSpace(mediaRef))
                throw new InvalidOperationException("Each input needs a mediaRef.");
            parsed.Add((role, mediaRef));
        }
        return parsed;
    }

    // Checks every input against the operation's typed slots: the role must be
    // accepted, the count within [min, max], required roles present, and each
    // asset's kind admitted by its slot. Only owned project media (a manifest
    // entry with a locally present file) can be quoted.
    private static List<(string Role, string MediaRef, ClipType Type)> ValidateMediaInputs(
        ProjectSession session,
        List<(string Role, string MediaRef)> raw,
        HostedMediaOperationProfile op,
        HostedMediaProfile profile)
    {
        var slots = op.Inputs ?? [];
        var byRole = raw.GroupBy(r => r.Role, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var role in byRole.Keys)
        {
            if (slots.All(s => !string.Equals(s.Role, role, StringComparison.Ordinal)))
                throw new InvalidOperationException(
                    $"{profile.DisplayName} {op.Operation} does not accept '{role}' input.");
        }
        var validated = new List<(string Role, string MediaRef, ClipType Type)>();
        var resolved = ProjectHub.RunSerialized(() => raw
            .Select(input =>
            {
                var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == input.MediaRef)
                    ?? throw new InvalidOperationException($"Media asset not found: {input.MediaRef}");
                // WP-399: a file outside the project the user has not allowed is refused by name.
                var resolvedPath = UsablePath(session, entry, input.MediaRef);
                if (resolvedPath is null || !File.Exists(resolvedPath))
                    throw new InvalidOperationException($"Media {input.MediaRef} is offline; cannot upload it.");
                return (input.Role, input.MediaRef, entry.Type, Path: resolvedPath);
            })
            .ToList());
        foreach (var slot in slots)
        {
            var count = byRole.TryGetValue(slot.Role, out var group) ? group.Count : 0;
            if (count < slot.Min || count > slot.Max)
                throw new InvalidOperationException(
                    $"{profile.DisplayName} {op.Operation} takes {slot.Min}..{slot.Max} '{slot.Role}' inputs; got {count}.");
        }
        foreach (var item in resolved)
        {
            var slot = slots.First(s => string.Equals(s.Role, item.Role, StringComparison.Ordinal));
            var kind = item.Type switch
            {
                ClipType.image => "image",
                ClipType.video => "video",
                ClipType.audio => "audio",
                _ => null,
            };
            if (kind is null || !(slot.Kinds ?? []).Contains(kind, StringComparer.Ordinal))
                throw new InvalidOperationException(
                    $"Media {item.MediaRef} is {item.Type}; '{item.Role}' takes {(slot.Kinds is { Count: > 0 } k ? string.Join("/", k) : "no inputs")}.");
            validated.Add((item.Role, item.MediaRef, item.Type));
        }
        return validated;
    }

    private sealed record MediaUpload(string Role, string MediaRef, Guid AssetId, string Sha256);
    private sealed record CachedMediaUpload(Guid AssetId, DateTimeOffset ExpiresAt);
    private static readonly Dictionary<string, CachedMediaUpload> _mediaUploads = new(StringComparer.Ordinal);

    private static async Task<List<MediaUpload>> UploadMediaAssetsAsync(
        ProjectSession session,
        HostedMediaClient client,
        List<(string Role, string MediaRef, ClipType Type)> inputs,
        string requestIdentity,
        CancellationToken cancellationToken)
    {
        // Snapshot owned paths under the session gate; bytes and uploads happen
        // outside it so the editor stays usable, exactly like image quotes.
        var files = ProjectHub.RunSerialized(() => inputs
            .Select(input =>
            {
                var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == input.MediaRef)
                    ?? throw new InvalidOperationException($"Media asset not found: {input.MediaRef}");
                var resolvedPath = UsablePath(session, entry, input.MediaRef);
                if (resolvedPath is null || !File.Exists(resolvedPath))
                    throw new InvalidOperationException($"Media {input.MediaRef} is offline; cannot upload it.");
                return (input.Role, input.MediaRef, Path: resolvedPath, Entry: entry);
            })
            .ToList());
        // WP-399: outside the gate and before any byte is read, an allowed external file must still
        // lead where it did when the user allowed it.
        foreach (var file in files)
            TransferPath(session, file.Entry, file.MediaRef);

        // Hash each distinct file without retaining its contents. The gateway requires a distinct asset id
        // per input entry, so each (role, file) entry uploads separately with a
        // role-scoped key: the same image may serve first and last frame, and
        // each role gets its own asset. Quoting the same role+file twice reuses
        // its upload via the content-derived key.
        var identity = requestIdentity + "|" + client.BaseUrl;
        var hashesByRef = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!hashesByRef.ContainsKey(file.MediaRef))
            {
                var kind = HostedMediaClient.KindFor(MediaContentTypeFor(file.Path));
                using var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (source.Length == 0 || source.Length > HostedMediaClient.MaxBytesFor(kind))
                    throw new GenerationException("Reference media exceeds the upload limit.", code: "media_too_large");
                hashesByRef[file.MediaRef] = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            }
        }
        var uploads = new List<MediaUpload>(files.Count);
        foreach (var file in files)
        {
            var contentType = MediaContentTypeFor(file.Path);
            var sha = hashesByRef[file.MediaRef];
            var key = StableGuid("temno-media-upload-v1", file.Role + ":" + contentType + ":" + sha + ":" + DateTimeOffset.UtcNow.ToString("yyyyMMdd"));
            var cacheKey = identity + "|" + key;
            CachedMediaUpload? cached;
            lock (_mediaQuoteLock) _mediaUploads.TryGetValue(cacheKey, out cached);
            if (cached is not null && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(3))
            {
                uploads.Add(new MediaUpload(file.Role, file.MediaRef, cached.AssetId, sha));
                continue;
            }
            var upload = await client.UploadFileAsync(file.Path, contentType, key, cancellationToken, sha).ConfigureAwait(false);
            if (DateTimeOffset.TryParse(upload.ExpiresAt, out var expiresAt)
                && identity == ReadGenerationConfig().Fingerprint + "|" + client.BaseUrl)
            {
                lock (_mediaQuoteLock)
                {
                    foreach (var stale in _mediaUploads.Where(kv => kv.Value.ExpiresAt <= DateTimeOffset.UtcNow).Select(kv => kv.Key).ToArray())
                        _mediaUploads.Remove(stale);
                    if (_mediaUploads.Count >= 256) _mediaUploads.Clear();
                    _mediaUploads[cacheKey] = new(upload.Asset.Id, expiresAt);
                }
            }
            uploads.Add(new MediaUpload(file.Role, file.MediaRef, upload.Asset.Id, sha));
        }
        return uploads;
    }

    private static string MediaContentTypeFor(string path)
    {
        var fileName = Path.GetFileName(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".flac" => "audio/flac",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            var ext => throw new GenerationException(
                $"Reference '{fileName}' has unsupported extension '{ext}'. " +
                "Images: PNG, JPEG, WebP. Video: MP4, WebM, MOV. Audio: MP3, WAV, OGG, FLAC, M4A, AAC.",
                code: "invalid_media_type"),
        };
    }

    // Every hosted submit takes the quote path; without a quoteId it is refused
    // there before any placeholder or network call. BYOK and fake fall through.
    private static bool TryRouteMediaSubmit(JsonObject args, string expectedCategory, out object? routed)
    {
        routed = null;
        if (!IsHostedMediaConfigured())
            return false;
        routed = SubmitMediaJob(args, expectedCategory);
        return true;
    }

    // Legacy scalar/reference args are never silently reinterpreted on this
    // path: the server quote is authoritative, so any of them is a loud error.
    private static readonly HashSet<string> MediaLegacyArgs = new(StringComparer.Ordinal)
    {
        "duration", "aspectRatio", "resolution", "quality",
        "voice", "lyrics", "styleInstructions", "instrumental",
        "startFrameMediaRef", "endFrameMediaRef", "sourceVideoMediaRef", "sourceClipId",
        "referenceImageMediaRefs", "referenceVideoMediaRefs", "referenceAudioMediaRefs",
        "videoSourceMediaRef", "videoSourceStartFrame", "videoSourceEndFrame",
        "mediaRef",
    };

    private static object SubmitMediaJob(JsonObject args, string expectedCategory)
    {
        var session = GetSession();
        _ = GetMediaClient();
        var quoteId = args["quoteId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(quoteId))
            throw new InvalidOperationException(QuoteRequired("quote_media", expectedCategory switch
            {
                "video" => "generate_video",
                "audio" => "generate_audio",
                _ => "upscale_media",
            }));

        MediaQuoteEntry entry;
        lock (_mediaQuoteLock)
        {
            if (!_mediaQuotes.TryGetValue(quoteId, out entry!))
                throw new InvalidOperationException(
                    "Unknown or expired quote. Call quote_media first; quotes cannot be reused across restarts.");
        }
        var quote = entry.Quote;
        if (!DateTimeOffset.TryParse(quote.ExpiresAt, out var expires) || expires <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("That quote has expired. Get a new quote before submitting.");

        // A quote accepted under another account/session must never be spent here.
        var (_, _, _, fingerprint) = ReadGenerationConfig();
        if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "That quote was accepted under a different account or endpoint. Call quote_media again.");
        if (!PathEquals(entry.ProjectPath, session.PackagePath))
            throw new InvalidOperationException(
                "That quote was accepted for a different project. Call quote_media again.");

        if (!string.Equals(entry.Category, expectedCategory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Quote {quoteId} is for {entry.Category} operation '{entry.Operation}', not {expectedCategory}. Call quote_media again.");
        var argOperation = args["operation"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(argOperation)
            && !string.Equals(argOperation, entry.Operation, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Quote {quoteId} is for operation '{entry.Operation}', not '{argOperation}'. Call quote_media again.");
        var argModel = args["model"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(argModel)
            && !string.Equals(argModel, quote.Model, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Quote {quoteId} is for model '{quote.Model}', not '{argModel}'. Call quote_media again.");
        if (args["prompt"] is not null)
        {
            var argPrompt = args["prompt"]?.GetValue<string>() ?? "";
            if (argPrompt != entry.Prompt)
                throw new InvalidOperationException(
                    "The prompt differs from the accepted quote. Call quote_media again.");
        }
        if (args["settings"] is not null)
        {
            var normalized = ParseMediaSettings(args["settings"], entry.Profile, entry.Operation);
            if (!JsonNode.DeepEquals(quote.Settings ?? new JsonObject(), normalized))
                throw new InvalidOperationException(
                    "The settings differ from the accepted quote. Call quote_media again.");
        }
        if (args["inputs"] is not null)
        {
            var argInputs = ParseMediaInputs(args["inputs"]);
            if (argInputs.Count != entry.Inputs.Count
                || argInputs.Where((echo, i) => echo.Role != entry.Inputs[i].Role || echo.MediaRef != entry.Inputs[i].MediaRef).Any())
                throw new InvalidOperationException(
                    "The inputs differ from the accepted quote. Call quote_media again.");
        }
        foreach (var legacy in MediaLegacyArgs)
        {
            if (args[legacy] is null)
                continue;
            // upscale_media historically requires mediaRef: accept it only when it
            // names the single quoted source, otherwise the quote is stale.
            if (legacy == "mediaRef" && entry.Inputs.Count == 1
                && string.Equals(args[legacy]?.GetValue<string>(), entry.Inputs[0].MediaRef, StringComparison.Ordinal))
                continue;
            throw new InvalidOperationException(
                $"'{legacy}' is not used with hosted quotes; it was bound at quote_media time. Omit it or call quote_media again.");
        }
        if (args["maxCredits"] is not null)
        {
            if (args["maxCredits"] is not JsonValue capValue || !capValue.TryGetValue(out int maxCredits) || maxCredits < 1)
                throw new InvalidOperationException("maxCredits must be a positive integer.");
            if (quote.Credits > maxCredits) throw new InvalidOperationException($"The quote is {quote.Credits} credits, above the accepted cap of {maxCredits}.");
        }

        // The quoted sources must be byte-identical now: any change, removal or
        // offline asset rejects before the hold, so a stale quote can never be
        // spent and never charges twice.
        var sourcePaths = ProjectHub.RunSerialized(() => entry.Inputs
            .Select(input =>
            {
                var manifestEntry = session.Manifest.Entries.FirstOrDefault(e => e.Id == input.MediaRef)
                    ?? throw new InvalidOperationException(
                        $"Source {input.MediaRef} is no longer in the project. Call quote_media again.");
                var resolvedPath = UsablePath(session, manifestEntry, input.MediaRef);
                if (resolvedPath is null || !File.Exists(resolvedPath))
                    throw new InvalidOperationException(
                        $"Source {input.MediaRef} is offline. Call quote_media again.");
                return (input.MediaRef, input.Sha256, manifestEntry.Type, manifestEntry.Duration, manifestEntry.HasAudio, Path: resolvedPath, Entry: manifestEntry);
            })
            .ToList());
        foreach (var source in sourcePaths)
        {
            TransferPath(session, source.Entry, source.MediaRef); // WP-399
            var bytes = File.ReadAllBytes(source.Path);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(sha, source.Sha256, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Source {source.MediaRef} changed since the quote. Call quote_media again.");
        }

        var prompt = entry.Prompt;
        var name = args["name"]?.GetValue<string>();
        var folder = args["folder"]?.GetValue<string>();
        var placeholderId = Guid.NewGuid().ToString().ToUpperInvariant();
        var placeholderName = name
            ?? (!string.IsNullOrWhiteSpace(prompt) ? (prompt.Length > 30 ? prompt.Substring(0, 30) : prompt) : null)
            ?? $"{entry.Operation} ({quote.Model})";
        var folderId = ResolveFolderId(session, folder);
        var submitKey = SubmitMediaKeyFor(placeholderId);

        // Output kind comes from the quoted category/operation, never a default:
        // video ops land video, audio ops land audio, upscale inherits the source.
        ClipType outputType = entry.Operation == HostedMediaOperations.Dubbing
            ? sourcePaths.Single().Type : entry.Category switch
        {
            "audio" => ClipType.audio,
            "video" => ClipType.video,
            _ => sourcePaths.Count > 0 ? sourcePaths[0].Type : ClipType.video,
        };
        if (!string.IsNullOrEmpty(quote.ResultKind))
        {
            var quotedType = quote.ResultKind switch
            {
                "image" => ClipType.image, "audio" => ClipType.audio, "video" => ClipType.video,
                _ => throw new InvalidOperationException("The gateway returned an unsupported output kind.")
            };
            if (quotedType != outputType)
                throw new InvalidOperationException("The quoted output type does not match this operation. Check the price again.");
        }
        if (outputType is not (ClipType.video or ClipType.audio or ClipType.image))
            throw new InvalidOperationException($"Quote {quoteId} has an unsupported output kind.");
        if (AlreadyOpenQuote(quoteId) is { } alreadyMedia)
            return alreadyMedia;
        var ext = outputType switch
        {
            ClipType.audio => "mp3",
            ClipType.image => "png",
            _ => "mp4",
        };

        // All provenance, including the submit key and accepted quote, is
        // persisted before the create call, so a network-lost response still
        // retries the same submission instead of minting a fresh job.
        var placeholder = new MediaManifestEntry
        {
            Id = placeholderId,
            Name = placeholderName,
            Type = outputType,
            Source = MediaSource.Project($"media/gen-{placeholderId.Substring(0, 8)}.{ext}"),
            Duration = entry.Category == "upscale" && sourcePaths.Count > 0 ? sourcePaths[0].Duration : 0,
            HasAudio = entry.Category == "audio" ? true : sourcePaths.Count > 0 ? sourcePaths[0].HasAudio : null,
            FolderId = folderId,
            GenerationStatus = "preparing",
            GenerationInput = new GenerationInput
            {
                Prompt = prompt,
                Model = quote.Model,
                Operation = entry.Operation,
                Settings = quote.Settings?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? ""),
                SourceAssetIds = entry.Inputs.Select(i => i.MediaRef).ToList(),
                InputRoles = entry.Inputs.Select(i => i.Role).ToList(),
                QuoteId = quote.QuoteId,
                QuotedCredits = quote.Credits,
                SubmitKey = submitKey.ToString("D"),
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };

        lock (_sessionLock)
        {
            session.Manifest.Entries.Add(placeholder);
            session.MarkDirty();
            session.Save();
        }
        if (string.IsNullOrEmpty(session.PackagePath))
            throw new InvalidOperationException("Open project has no package path.");

        RunGenerationBackground(session, placeholderId, token =>
            EnqueueAndWaitAsync(session, placeholderId, "media", outputType.ToString(), token));

        return GenerationAccepted(
            placeholderId,
            $"Generation started. Placeholder asset ID: {placeholderId}. Model: {quote.Model}, {entry.Operation}, {quote.Credits} credits quoted.");
    }

    private static async Task LandMediaOutputsAsync(
        ProjectSession session,
        string placeholderId,
        string packagePath,
        IReadOnlyList<string> resultUrls,
        string? jobId,
        ClipType outputType,
        CancellationToken token,
        GenerationService service)
    {
        if (resultUrls.Count == 0)
            throw new InvalidOperationException("Generation succeeded but returned no result URLs.");
        UpdatePlaceholderStatus(session, placeholderId, "downloading", jobId, resultUrls.ToList());
        var landed = await service.DownloadAndLandAssetsAsync(resultUrls, packagePath, placeholderId, token)
            .ConfigureAwait(false);
        var probed = await ProbeLandedMediaAsync(landed[0], outputType, token).ConfigureAwait(false);
        ProjectHub.RunSerialized(() =>
        {
            lock (_sessionLock)
            {
                var placeholder = session.Manifest.Entries.FirstOrDefault(e => e.Id == placeholderId);
                if (placeholder is null)
                    return;
                var hadSize = HasSourceSize(placeholder);
                placeholder.Source = MediaSource.Project("media/" + Path.GetFileName(landed[0]));
                placeholder.GenerationStatus = CompletedGenerationStatus();
                GenerationEntryGuard.Touch(placeholder);
                placeholder.Type = outputType;
                ApplyProbedMetadata(placeholder, probed, landed[0], outputType);
                RefitClipsPlacedBeforeSize(session, placeholder, hadSize);
                var input = placeholder.GenerationInput;
                if (input is not null && !string.IsNullOrEmpty(jobId))
                    input.BackendJobId = jobId;

                var jobKey = !string.IsNullOrEmpty(jobId) ? "job:" + jobId : "placeholder:" + placeholderId;
                for (var index = 1; index < landed.Count; index++)
                {
                    var siblingId = SiblingMediaOutputId(jobKey, index);
                    var sibling = session.Manifest.Entries.FirstOrDefault(e => e.Id == siblingId);
                    if (sibling is null)
                    {
                        sibling = new MediaManifestEntry
                        {
                            Id = siblingId,
                            Name = placeholder.Name + " " + (index + 1),
                            Type = outputType,
                            Source = MediaSource.Project("media/" + Path.GetFileName(landed[index])),
                            Duration = 0,
                            FolderId = placeholder.FolderId,
                            GenerationInput = new GenerationInput
                            {
                                Prompt = input?.Prompt ?? "",
                                Model = input?.Model ?? "",
                                Operation = input?.Operation,
                                Settings = input?.Settings is null ? null : new Dictionary<string, string>(input.Settings),
                                SourceAssetIds = input?.SourceAssetIds is null ? null : new List<string>(input.SourceAssetIds),
                                InputRoles = input?.InputRoles is null ? null : new List<string>(input.InputRoles),
                                QuoteId = input?.QuoteId,
                                QuotedCredits = input?.QuotedCredits,
                                ChargedCredits = input?.ChargedCredits,
                                BackendJobId = jobId,
                                OutputIndex = index,
                                ResultUrls = resultUrls.ToList(),
                                CreatedAt = input?.CreatedAt,
                            },
                        };
                        session.Manifest.Entries.Add(sibling);
                    }
                    else
                    {
                        sibling.Source = MediaSource.Project("media/" + Path.GetFileName(landed[index]));
                        sibling.GenerationStatus = CompletedGenerationStatus();
                        GenerationEntryGuard.Touch(sibling);
                        sibling.Type = outputType;
                        if (sibling.GenerationInput is not null && !string.IsNullOrEmpty(jobId))
                            sibling.GenerationInput.BackendJobId = jobId;
                    }
                    ApplyProbedMetadata(sibling, probed, landed[index], outputType);
                }

                session.MarkDirty();
                session.Save();
            }
        });
    }

    private sealed record ProbedMedia(double? Duration, int? Width, int? Height, double? Fps, bool? HasAudio);

    private static async Task<ProbedMedia> ProbeLandedMediaAsync(string landedPath, ClipType outputType, CancellationToken token)
    {
        if (outputType == ClipType.image)
            return new ProbedMedia(null, null, null, null, false);
        try
        {
            var meta = await new FfmpegMediaProbe().ProbeAsync(landedPath, token).ConfigureAwait(false);
            return new ProbedMedia(
                meta.DurationSeconds > 0 ? meta.DurationSeconds : null,
                meta.Width, meta.Height, meta.Fps,
                meta.HasAudio);
        }
        catch
        {
            // Keep placeholder metadata; landing still succeeds.
            return new ProbedMedia(null, null, null, null, outputType == ClipType.audio ? true : null);
        }
    }

    private static void ApplyProbedMetadata(MediaManifestEntry entry, ProbedMedia probed, string landedPath, ClipType outputType)
    {
        if (outputType == ClipType.image)
        {
            entry.Duration = 0;
            ApplyImageDimensions(entry, landedPath);
            return;
        }
        if (probed.Duration.HasValue)
            entry.Duration = probed.Duration.Value;
        if (probed.Width.HasValue)
            entry.SourceWidth = probed.Width.Value;
        if (probed.Height.HasValue)
            entry.SourceHeight = probed.Height.Value;
        if (probed.Fps.HasValue)
            entry.SourceFps = probed.Fps.Value;
        if (probed.HasAudio.HasValue)
            entry.HasAudio = probed.HasAudio.Value;
    }

    private static async Task ConfirmMediaCancellationAsync(ProjectSession session, string id)
    {
        var input = ProjectHub.RunSerialized(() => session.Manifest.Entries.FirstOrDefault(e => e.Id == id)?.GenerationInput);
        if (input is null || !IsMediaOperation(input.Operation)) return;
        var client = GetMediaClient();
        if (!string.IsNullOrEmpty(input.QuoteId) && Guid.TryParse(input.SubmitKey, out var key))
            await client.CancelSubmissionAsync(input.QuoteId, key).ConfigureAwait(false);
        else if (!string.IsNullOrEmpty(input.BackendJobId))
            await client.CancelJobAsync(input.BackendJobId).ConfigureAwait(false);
        else throw new GenerationException("No saved submission was found.");
        RefreshImageBalance();
    }

    // Puts the saved job back on the account watcher. A lost submit response is retried with the
    // same quote and key, so the server returns the same job instead of charging a second one.
    private static bool TryRecoverMediaJob(ProjectSession session, MediaManifestEntry entry, string packagePath)
    {
        var input = entry.GenerationInput;
        if (!IsMediaJob(entry) || input is null)
            return false;
        if (TryGetMediaClient() is null)
            throw new InvalidOperationException(GenerationUnavailableMessage());

        var urls = input.ResultUrls;
        var jobId = input.BackendJobId;
        var hasKey = Guid.TryParseExact(input.SubmitKey, "D", out _);
        if (urls is not { Count: > 0 } && string.IsNullOrEmpty(jobId) && (string.IsNullOrEmpty(input.QuoteId) || !hasKey))
            return false;

        var id = entry.Id;
        var outputType = entry.Type;
        RememberHostedJob(session, id, "media", outputType.ToString());
        if (input.CancelRequested == true)
            PendingGenerations.Update(id, job => job.CancelRequested = true);
        EnsureHostedWatcher();
        return true;
    }
}
