using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Temno.Core.Account;
using Temno.Core.Editing;
using Temno.Core.Generation;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Generation;
using Temno.Media;

namespace Temno.Mcp;

// WP-232 hosted images orchestration. MCP tools (quote_image, generate_image,
// edit_image) and the native generation dialog share this implementation: the
// dialog calls ToolExecutor.Execute with the same arguments an agent would.

public static partial class ToolExecutor
{
    public static HostedImageClient? ImageClientOverride { get; set; }

    private sealed record ImageQuoteEntry(
        HostedImageQuote Quote,
        HostedImageProfile Profile,
        string Prompt,
        string Fingerprint,
        List<string> MediaRefs,
        string? MaskRef,
        DateTimeOffset AcceptedAt);

    private static readonly Dictionary<string, ImageQuoteEntry> _imageQuotes = new(StringComparer.Ordinal);
    private static readonly object _imageQuoteLock = new();
    private static HostedImageCatalog? _imageCatalog;
    private static DateTimeOffset _imageCatalogAt;
    private static string? _imageCatalogIdentity;
    private static readonly object _imageCatalogLock = new();

    public static bool IsHostedImagesConfigured()
    {
        if (ImageClientOverride is not null)
            return true;
        var (_, _, useHosted, _) = ReadGenerationConfig();
        return useHosted;
    }

    public static HostedImageClient? TryGetImageClient()
    {
        if (ImageClientOverride is not null)
            return ImageClientOverride;
        var (_, _, useHosted, _) = ReadGenerationConfig();
        if (!useHosted)
            return null;
        var gateway = HostedEndpoints.ResolveGatewayUrl();
        if (string.IsNullOrWhiteSpace(gateway))
            return null;
        return new HostedImageClient(gateway, () => HostedRuntime.AccessToken);
    }

    public static HostedImageClient GetImageClient()
        => TryGetImageClient()
            ?? throw new InvalidOperationException(
                "Hosted image generation needs a signed-in account and a gateway URL. " +
                "BYOK and demo generation do not use image quotes.");

    public static void ResetImageCatalog()
    {
        lock (_imageCatalogLock)
        {
            _imageCatalog = null;
            _imageCatalogAt = default;
            _imageCatalogIdentity = null;
        }
    }

    public static void ResetImageQuotes()
    {
        lock (_imageQuoteLock)
        {
            _imageQuotes.Clear();
        }
    }

    public static async Task<HostedImageCatalog> GetImageCatalogAsync(CancellationToken cancellationToken = default)
    {
        var identity = ReadGenerationConfig().Fingerprint + "|" + ImageClientOverride?.BaseUrl;
        lock (_imageCatalogLock)
        {
            if (_imageCatalog is not null && _imageCatalogIdentity == identity && DateTimeOffset.UtcNow - _imageCatalogAt < TimeSpan.FromMinutes(5))
                return _imageCatalog;
        }
        var catalog = await GetImageClient().GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (identity != ReadGenerationConfig().Fingerprint + "|" + ImageClientOverride?.BaseUrl)
            throw new InvalidOperationException("The account changed while loading models. Please try again.");
        lock (_imageCatalogLock)
        {
            _imageCatalog = catalog;
            _imageCatalogAt = DateTimeOffset.UtcNow;
            _imageCatalogIdentity = identity;
        }
        return catalog;
    }

    public static bool IsImagesOperation(string? operation)
        => operation is HostedImageOperations.Generate or HostedImageOperations.Edit;

    public static bool IsImagesJob(MediaManifestEntry entry)
        => IsImagesOperation(entry?.GenerationInput?.Operation);

    public static Guid StableGuid(string purpose, string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(purpose + "\u001f" + key));
        return new Guid(hash.AsSpan(0, 16));
    }

    // Deterministic manifest id per backend job + output index, so a retry or a
    // reopen upserts the same entries instead of duplicating them.
    public static string SiblingOutputId(string jobKey, int index)
        => StableGuid("temno-image-output-v1", jobKey + "#" + index).ToString().ToUpperInvariant();

    public static Guid SubmitKeyFor(string placeholderId)
        => StableGuid("temno-image-job-v1", placeholderId);

    // Hosted image catalog rows for list_models. A failing images catalog never
    // breaks the rest of the list; quote_image surfaces the real error.
    private static List<CatalogEntry> ListHostedImageModels()
    {
        try
        {
            if (!IsHostedImagesConfigured())
                return [];
            var catalog = GetImageCatalogAsync().GetAwaiter().GetResult();
            return catalog.Models.Select(profile =>
            {
                var ops = profile.Operations
                    .Where(o => catalog.IsAvailable(profile, o.Operation))
                    .Select(o => o.Operation)
                    .ToList();
                return new CatalogEntry
                {
                    Id = profile.Id,
                    Kind = GenerationKind.Image,
                    DisplayName = ops.Count > 0
                        ? $"{profile.Label} ({string.Join(", ", ops)})"
                        : $"{profile.Label} (unavailable)",
                    PaidOnly = true,
                    AllowedEndpoints = ops,
                    ImageProfile = profile,
                    QuoteRequired = true,
                };
            }).ToList();
        }
        catch
        {
            return [];
        }
    }

    public static ToolCallResult ExecuteGenerationForProject(ProjectSession project, string name, JsonObject args, string? expectedUserId = null)
    {
        if (name is not ("quote_image" or "generate_image" or "edit_image" or "quote_media" or "generate_video" or "generate_audio" or "upscale_media"))
            return Error("Only generation tools can use a bound generation project.");
        if (name is not ("quote_image" or "quote_media"))
            return ProjectHub.RunSerialized(() => ReferenceEquals(ProjectHub.ActiveSession, project)
                ? expectedUserId == HostedRuntime.Session?.UserId ? Execute(name, args) : Error("The account changed. Open the generation panel again.")
                : Error("The active project changed. Open the generation panel again."));
        if (!ReferenceEquals(ProjectHub.ActiveSession, project))
            return Error("The active project changed. Open the generation panel again.");
        if (expectedUserId != HostedRuntime.Session?.UserId)
            return Error("The account changed. Open the generation panel again.");
        // A quote may upload sources while the non-modal editor changes account.
        // Keep those uploads on the token captured for the original request.
        var token = HostedRuntime.AccessToken;
        try
        {
            if (name == "quote_media")
            {
                var mediaClient = MediaClientOverride ?? new HostedMediaClient(HostedEndpoints.ResolveGatewayUrl(), () => token);
                return Success(QuoteMedia(args, project, mediaClient));
            }
            var client = ImageClientOverride ?? new HostedImageClient(HostedEndpoints.ResolveGatewayUrl(), () => token);
            return Success(QuoteImage(args, project, client));
        }
        catch (Exception ex) { return Error(ex.Message); }
    }

    private static object QuoteImage(JsonObject args, ProjectSession? boundProject = null, HostedImageClient? boundClient = null)
    {
        var session = boundProject ?? GetSession();
        var client = boundClient ?? GetImageClient();
        var requestIdentity = ReadGenerationConfig().Fingerprint;
        var prompt = args["prompt"]?.GetValue<string>()
            ?? throw new InvalidOperationException("prompt is required");
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("prompt is required");
        var modelId = args["model"]?.GetValue<string>()
            ?? throw new InvalidOperationException("model is required. Pick one from the hosted image catalog.");
        var operation = args["operation"]?.GetValue<string>() ?? HostedImageOperations.Generate;
        if (!IsImagesOperation(operation))
            throw new InvalidOperationException("operation must be generate_image or edit_image.");

        var catalog = GetImageCatalogAsync().GetAwaiter().GetResult();
        var profile = catalog.Get(modelId)
            ?? throw new InvalidOperationException($"Unknown hosted image model '{modelId}'.");
        if (catalog.UnavailabilityReason(profile, operation) is { } reason)
            throw new InvalidOperationException(reason);
        var op = profile.Operation(operation)!;

        var settings = ParseImageSettings(args["settings"], args, profile);
        var mediaRefs = ParseStringArray(args["referenceMediaRefs"]);
        var maskRef = args["maskMediaRef"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(maskRef))
            maskRef = null;
        if (operation == HostedImageOperations.Edit && mediaRefs.Count == 0)
            throw new InvalidOperationException("edit_image needs at least one source image (referenceMediaRefs).");
        if (mediaRefs.Count < op.MinInputs || mediaRefs.Count > op.MaxInputs)
            throw new InvalidOperationException(
                $"{profile.Label} {operation} takes {op.MinInputs}..{op.MaxInputs} source images; got {mediaRefs.Count}.");
        if (maskRef is not null && !op.SupportsMask)
            throw new InvalidOperationException($"{profile.Label} {operation} does not accept a mask.");

        var inputIds = UploadImageAssetsAsync(session, client, mediaRefs, CancellationToken.None)
            .GetAwaiter().GetResult();
        Guid? maskId = null;
        if (maskRef is not null)
        {
            var mask = UploadImageAssetsAsync(session, client, [maskRef], CancellationToken.None)
                .GetAwaiter().GetResult();
            maskId = mask[0];
        }

        // The quote binds this exact request; uploads are reused, never repeated.
        var request = new HostedImageQuoteRequest
        {
            Model = profile.Id,
            Operation = operation,
            Prompt = prompt,
            Settings = settings,
            InputIds = inputIds,
            MaskId = maskId,
        };
        var quote = client.QuoteAsync(request).GetAwaiter().GetResult();
        var (_, _, _, fingerprint) = ReadGenerationConfig();
        if (fingerprint != requestIdentity)
            throw new InvalidOperationException("The account or connection changed while checking the price. Please try again.");
        lock (_imageQuoteLock)
        {
            foreach (var stale in _imageQuotes
                .Where(kv => DateTimeOffset.UtcNow - kv.Value.AcceptedAt > TimeSpan.FromHours(24))
                .Select(kv => kv.Key).ToList())
                _imageQuotes.Remove(stale);
            _imageQuotes[quote.QuoteId] = new ImageQuoteEntry(quote, profile, prompt, fingerprint, mediaRefs, maskRef, DateTimeOffset.UtcNow);
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
            ["settings"] = quote.Settings,
            ["inputIds"] = quote.InputIds?.Select(g => g.ToString("D")).ToList(),
            ["maskId"] = quote.MaskId?.ToString("D"),
            ["developmentPrices"] = catalog.DevelopmentPrices,
        };
        if (catalog.DevelopmentPrices)
            result["note"] = "Development pricing: the quoted credits are reserved on submission and deducted on delivery.";
        return result;
    }

    // Reads Temno-named settings plus the three legacy top-level aliases.
    // Unknown keys are rejected: a setting the profile does not offer cannot
    // silently ride along, and the server additionally rejects per-operation
    // mismatches with invalid_request.
    private static JsonObject ParseImageSettings(JsonNode? node, JsonObject args, HostedImageProfile profile)
    {
        JsonObject settings;
        if (node is null)
            settings = new JsonObject();
        else if (node is JsonObject obj)
            settings = (JsonObject)obj.DeepClone();
        else
            throw new InvalidOperationException("settings must be an object of Temno setting names to values.");

        foreach (var (legacy, temno) in new[] { ("aspectRatio", "aspect_ratio"), ("resolution", "resolution"), ("quality", "quality") })
        {
            var value = args[legacy]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (settings.ContainsKey(temno) && settings[temno]?.ToString() != value)
                throw new InvalidOperationException($"Conflicting values for '{temno}': '{legacy}' and 'settings' disagree.");
            settings[temno] = value;
        }

        foreach (var kv in settings.ToList())
        {
            var option = profile.Settings.FirstOrDefault(o => string.Equals(o.Name, kv.Key, StringComparison.Ordinal));
            if (option is null)
                throw new InvalidOperationException(
                    $"Setting '{kv.Key}' is not supported by {profile.Label}.");
            if (kv.Value is null || (kv.Value is JsonValue j && j.ToString() == "null"))
            {
                settings.Remove(kv.Key);
                continue;
            }
            if (kv.Value is not JsonValue)
                throw new InvalidOperationException($"Setting '{kv.Key}' must be a single value.");
            if (option.AllowedValues.Count > 0)
            {
                var text = kv.Value is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                if (text is null || !option.AllowedValues.Contains(text, StringComparer.Ordinal))
                    throw new InvalidOperationException(
                        $"Setting '{kv.Key}' must be one of [{string.Join(", ", option.AllowedValues)}]; got '{kv.Value}'.");
            }
        }
        return settings;
    }

    private static async Task<List<Guid>> UploadImageAssetsAsync(
        ProjectSession session,
        HostedImageClient client,
        List<string> mediaRefs,
        CancellationToken cancellationToken)
    {
        var files = ProjectHub.RunSerialized(() => mediaRefs
            .Select(mediaRef =>
            {
                var entry = session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef)
                    ?? throw new InvalidOperationException($"Media asset not found: {mediaRef}");
                // WP-399: a file outside the project the user has not allowed is refused by name.
                var resolvedPath = UsablePath(session, entry, mediaRef);
                if (resolvedPath is null || !File.Exists(resolvedPath))
                    throw new InvalidOperationException($"Media {mediaRef} is offline; cannot upload it.");
                return (MediaRef: mediaRef, Path: resolvedPath, Entry: entry);
            })
            .ToList());
        // WP-399: before any byte leaves, an allowed external file must still lead where it did.
        foreach (var file in files)
            TransferPath(session, file.Entry, file.MediaRef);

        var ids = new List<Guid>(files.Count);
        foreach (var file in files)
        {
            if (new FileInfo(file.Path).Length > HostedImageClient.MaxUploadBytes)
                throw new GenerationException("Reference images must be under 12 MB.", code: "image_too_large");
            using var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var contentType = Path.GetExtension(file.Path).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                _ => throw new GenerationException(
                    $"Reference '{Path.GetFileName(file.Path)}' must be PNG, JPEG, or WebP.",
                    code: "invalid_image_type"),
            };
            // Content-derived key: quoting the same file twice reuses its upload.
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            var key = StableGuid("temno-image-upload-v2", contentType + ":" + sha + ":" + DateTimeOffset.UtcNow.ToString("yyyyMMdd"));
            var upload = await client.UploadFileAsync(file.Path, contentType, key, cancellationToken, sha).ConfigureAwait(false);
            ids.Add(upload.Asset.Id);
        }
        return ids;
    }

    // Hosted image generation is quote-only (WP-329). Saved hosted jobs and
    // BYOK/demo calls keep their original clients.
    private static bool IsImagesSubmit(JsonObject args)
    {
        if (!IsHostedImagesConfigured())
            return false;
        return true;
    }

    private static string QuoteRequired(string quoteTool, string submitTool)
        => $"Hosted generation is quote-only: call list_models, then {quoteTool}, then {submitTool} with the returned quoteId.";

    private static object SubmitImageJob(JsonObject args, string operation)
    {
        var session = GetSession();
        _ = GetImageClient();
        var quoteId = args["quoteId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(quoteId))
            throw new InvalidOperationException(QuoteRequired("quote_image", operation));

        ImageQuoteEntry entry;
        lock (_imageQuoteLock)
        {
            if (!_imageQuotes.TryGetValue(quoteId, out entry!))
                throw new InvalidOperationException(
                    "Unknown or expired quote. Call quote_image first; quotes cannot be reused across restarts.");
        }
        var quote = entry.Quote;
        if (!DateTimeOffset.TryParse(quote.ExpiresAt, out var expires) || expires <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("That quote has expired. Get a new quote before submitting.");

        // A quote accepted under another account/session must never be spent here.
        var (_, _, _, fingerprint) = ReadGenerationConfig();
        if (!string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "That quote was accepted under a different account or endpoint. Call quote_image again.");

        if (!string.Equals(quote.Operation, operation, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Quote {quoteId} is for {quote.Operation}, not {operation}. Call quote_image again.");
        var argModel = args["model"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(argModel)
            && !string.Equals(argModel, quote.Model, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Quote {quoteId} is for model '{quote.Model}', not '{argModel}'. Call quote_image again.");
        var argPrompt = args["prompt"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(argPrompt) && argPrompt != entry.Prompt)
            throw new InvalidOperationException(
                "The prompt differs from the accepted quote. Call quote_image again.");
        var argRefs = args["referenceMediaRefs"] is null ? null : ParseStringArray(args["referenceMediaRefs"]);
        if (argRefs is not null && !argRefs.SequenceEqual(entry.MediaRefs, StringComparer.Ordinal))
            throw new InvalidOperationException(
                "The source images differ from the accepted quote. Call quote_image again.");
        var argMask = args["maskMediaRef"]?.GetValue<string>();
        if (args["maskMediaRef"] is not null && !string.Equals(argMask, entry.MaskRef, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "The mask differs from the accepted quote. Call quote_image again.");
        if (args["settings"] is not null || args["quality"] is not null || args["resolution"] is not null || args["aspectRatio"] is not null)
        {
            var normalized = ParseImageSettings(args["settings"], args, entry.Profile);
            if (!JsonNode.DeepEquals(quote.Settings ?? new JsonObject(), normalized))
                throw new InvalidOperationException(
                    "The settings differ from the accepted quote. Call quote_image again.");
        }
        if (args["maxCredits"] is not null)
        {
            if (args["maxCredits"] is not JsonValue capValue || !capValue.TryGetValue(out int maxCredits) || maxCredits < 1)
                throw new InvalidOperationException("maxCredits must be a positive integer.");
            if (quote.Credits > maxCredits) throw new InvalidOperationException($"The quote is {quote.Credits} credits, above the accepted cap of {maxCredits}.");
        }
        if (AlreadyOpenQuote(quoteId) is { } alreadyImage)
            return alreadyImage;

        var prompt = entry.Prompt;
        var name = args["name"]?.GetValue<string>();
        var folder = args["folder"]?.GetValue<string>();
        var placeholderId = Guid.NewGuid().ToString().ToUpperInvariant();
        var placeholderName = name ?? (prompt.Length > 30 ? prompt.Substring(0, 30) : prompt);
        var folderId = ResolveFolderId(session, folder);
        var submitKey = SubmitKeyFor(placeholderId);

        // All provenance, including the submit key and accepted quote, is
        // persisted before the create call, so a network-lost response still
        // retries the same submission instead of minting a fresh job.
        var placeholder = new MediaManifestEntry
        {
            Id = placeholderId,
            Name = placeholderName,
            Type = ClipType.image,
            Source = MediaSource.Project($"media/gen-{placeholderId.Substring(0, 8)}.png"),
            Duration = 0,
            FolderId = folderId,
            GenerationStatus = "preparing",
            GenerationInput = new GenerationInput
            {
                Prompt = prompt,
                Model = quote.Model,
                Operation = operation,
                Settings = quote.Settings?.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? ""),
                SourceAssetIds = entry.MediaRefs.Count > 0 ? new List<string>(entry.MediaRefs) : null,
                MaskAssetId = entry.MaskRef,
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
            EnqueueAndWaitAsync(session, placeholderId, "image", ClipType.image.ToString(), token));

        return GenerationAccepted(
            placeholderId,
            $"Generation started. Placeholder asset ID: {placeholderId}. Model: {quote.Model}, {operation}, {quote.Credits} credits quoted.");
    }

    private static async Task LandImageOutputsAsync(
        ProjectSession session,
        string placeholderId,
        string packagePath,
        IReadOnlyList<string> resultUrls,
        string? jobId,
        CancellationToken token,
        GenerationService service)
    {
        if (resultUrls.Count == 0)
            throw new InvalidOperationException("Generation succeeded but returned no result URLs.");
        UpdatePlaceholderStatus(session, placeholderId, "downloading", jobId, resultUrls.ToList());
        var landed = await service.DownloadAndLandAssetsAsync(resultUrls, packagePath, placeholderId, token)
            .ConfigureAwait(false);
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
                placeholder.Duration = 0;
                ApplyImageDimensions(placeholder, landed[0]);
                RefitClipsPlacedBeforeSize(session, placeholder, hadSize);
                var input = placeholder.GenerationInput;
                if (input is not null && !string.IsNullOrEmpty(jobId))
                    input.BackendJobId = jobId;

                var jobKey = !string.IsNullOrEmpty(jobId) ? "job:" + jobId : "placeholder:" + placeholderId;
                for (var index = 1; index < landed.Count; index++)
                {
                    var siblingId = SiblingOutputId(jobKey, index);
                    var sibling = session.Manifest.Entries.FirstOrDefault(e => e.Id == siblingId);
                    if (sibling is null)
                    {
                        sibling = new MediaManifestEntry
                        {
                            Id = siblingId,
                            Name = placeholder.Name + " " + (index + 1),
                            Type = ClipType.image,
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
                                MaskAssetId = input?.MaskAssetId,
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
                        if (sibling.GenerationInput is not null && !string.IsNullOrEmpty(jobId))
                            sibling.GenerationInput.BackendJobId = jobId;
                    }
                    ApplyImageDimensions(sibling, landed[index]);
                }

                session.MarkDirty();
                session.Save();
            }
        });
    }

    private static bool HasSourceSize(MediaManifestEntry entry)
        => entry.SourceWidth is > 0 && entry.SourceHeight is > 0;

    /// <summary>
    /// WP-405: a clip placed on a generating placeholder had no size to fit, so it got the full-canvas
    /// box. When the result lands with a size, such clips take their real fit.
    /// </summary>
    private static void RefitClipsPlacedBeforeSize(ProjectSession session, MediaManifestEntry entry, bool hadSize)
    {
        if (hadSize || !HasSourceSize(entry)) return;
        ClipRefit.AfterSourceSizeKnown(session.ProjectFile.Timelines, entry);
    }

    /// <summary>WP-405: the landed picture's size, read from the file, or null for audio and unreadable files.</summary>
    private static async Task<(int Width, int Height)?> LandedPictureSizeAsync(string landedPath, CancellationToken token)
    {
        if (ImageDimensions.TryRead(landedPath, out var width, out var height) && width > 0 && height > 0)
            return (width, height);
        if (!MediaLibraryCommands.TryGetTypeFromExtension(Path.GetExtension(landedPath), out var type)
            || type != ClipType.video)
            return null;
        try
        {
            var meta = await new FfmpegMediaProbe().ProbeAsync(landedPath, token).ConfigureAwait(false);
            return meta.Width is > 0 && meta.Height is > 0 ? (meta.Width.Value, meta.Height.Value) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The landing still succeeds; the clip draws at the decoded picture's own shape regardless.
            return null;
        }
    }

    private static void ApplyImageDimensions(MediaManifestEntry entry, string landedPath)
    {
        if (ImageDimensions.TryRead(landedPath, out var width, out var height))
        {
            entry.SourceWidth = width;
            entry.SourceHeight = height;
        }
    }

    private static void RefreshImageBalance()
        => ProjectHub.RunSerialized(() => { _ = HostedRuntime.Session?.RefreshBalanceAsync(); });

    private static async Task ConfirmImageCancellationAsync(ProjectSession session, string id)
    {
        var input = ProjectHub.RunSerialized(() => session.Manifest.Entries.FirstOrDefault(e => e.Id == id)?.GenerationInput);
        if (input is null || !IsImagesOperation(input.Operation)) return;
        var client = GetImageClient();
        if (!string.IsNullOrEmpty(input.QuoteId) && Guid.TryParse(input.SubmitKey, out var key))
            await client.CancelSubmissionAsync(input.QuoteId, key).ConfigureAwait(false);
        else if (!string.IsNullOrEmpty(input.BackendJobId))
            await client.CancelJobAsync(input.BackendJobId).ConfigureAwait(false);
        else throw new GenerationException("No saved submission was found.");
        RefreshImageBalance();
    }

    // Puts the saved job back on the account watcher. A lost submit response is retried with the
    // same quote and key, so the server returns the same job instead of charging a second one.
    private static bool TryRecoverImagesJob(ProjectSession session, MediaManifestEntry entry, string packagePath)
    {
        var input = entry.GenerationInput;
        if (!IsImagesJob(entry) || input is null)
            return false;
        if (TryGetImageClient() is null)
            throw new InvalidOperationException(GenerationUnavailableMessage());

        var urls = input.ResultUrls;
        var jobId = input.BackendJobId;
        var hasKey = Guid.TryParseExact(input.SubmitKey, "D", out _);
        if (urls is not { Count: > 0 } && string.IsNullOrEmpty(jobId) && (string.IsNullOrEmpty(input.QuoteId) || !hasKey))
            return false;

        var id = entry.Id;
        RememberHostedJob(session, id, "image", ClipType.image.ToString());
        if (input.CancelRequested == true)
            PendingGenerations.Update(id, job => job.CancelRequested = true);
        EnsureHostedWatcher();
        return true;
    }
}
