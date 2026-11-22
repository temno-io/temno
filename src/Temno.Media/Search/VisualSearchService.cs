namespace Temno.Media.Search;

/// <summary>
/// Private local visual search: deterministic signatures (WP-46) + free-text Moments
/// via <see cref="ISemanticVisualEmbedder"/> (WP-95 concept embedder by default;
/// WP-111 ONNX SigLIP when a real pixel encoder is Ready).
/// </summary>
public sealed class VisualSearchService : IVisualSearchService
{
    private const int DefaultSampleFrameCount = 5;
    private const int MaxSampleFrameCount = 12;
    private const float HeuristicRelativeCutoff = 0.55f;
    private const float HeuristicMinScore = 0.08f;
    private const float OnnxRelativeCutoff = 0.85f;
    private const float OnnxMinScore = 0.05f;

    private readonly IFrameSource _frameSource;
    private readonly VisualSearchCache _cache;
    private readonly ISemanticVisualEmbedder _embedder;
    private readonly SemaphoreSlim _searchGate = new(1, 1);

    public VisualSearchService(
        IFrameSource frameSource,
        VisualSearchCache? cache = null,
        ISemanticVisualEmbedder? semanticEmbedder = null)
    {
        _frameSource = frameSource ?? throw new ArgumentNullException(nameof(frameSource));
        _cache = cache ?? new VisualSearchCache();
        _embedder = semanticEmbedder ?? SearchIndexConfig.CreateDefault();
    }

    public async Task<VisualSearchResponse> SearchAsync(VisualSearchRequest request, CancellationToken cancellationToken = default)
    {
        await _searchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SearchCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _searchGate.Release();
        }
    }

    private async Task<VisualSearchResponse> SearchCoreAsync(VisualSearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Assets);
        cancellationToken.ThrowIfCancellationRequested();

        if (!VisualSearchQueryParser.TryParse(request.Query, out var query, out var parseError))
        {
            return new VisualSearchResponse
            {
                Status = "unsupported_query",
                Message = parseError ?? "Unsupported visual query.",
                QueryKind = "unsupported",
            };
        }

        var cacheIndex = _cache.Load(request.CacheScope, _embedder.ModelId);
        cacheIndex.ModelId = _embedder.ModelId;
        var indexed = new Dictionary<string, IndexedAsset>(StringComparer.Ordinal);
        var activeMediaRefs = new HashSet<string>(request.Assets.Select(asset => asset.MediaRef), StringComparer.Ordinal);
        var indexedAssets = 0;
        var cachedAssets = 0;
        var invalidatedAssets = 0;
        var skippedOffline = 0;
        var skippedNonVisual = 0;
        var skippedUnreadable = 0;
        var cacheChanged = false;

        foreach (var asset in request.Assets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!asset.IsVisual)
            {
                skippedNonVisual++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(asset.SourcePath) || !File.Exists(asset.SourcePath))
            {
                skippedOffline++;
                cacheChanged |= cacheIndex.Entries.Remove(asset.MediaRef);
                continue;
            }

            VisualSourceFingerprint fingerprint;
            try
            {
                fingerprint = VisualSourceFingerprint.FromFile(asset.SourcePath);
            }
            catch (FileNotFoundException)
            {
                skippedOffline++;
                cacheChanged |= cacheIndex.Entries.Remove(asset.MediaRef);
                continue;
            }
            catch (IOException)
            {
                skippedUnreadable++;
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                skippedUnreadable++;
                continue;
            }
            catch (ArgumentException)
            {
                skippedUnreadable++;
                continue;
            }

            if (cacheIndex.Entries.TryGetValue(asset.MediaRef, out var cachedEntry)
                && cachedEntry is not null
                && cachedEntry.Fingerprint is not null
                && cachedEntry.Fingerprint.Matches(fingerprint)
                && cachedEntry.Frames is { Count: > 0 }
                && cachedEntry.Frames.All(frame => frame.Signature is not null)
                && (!_embedder.EncodesPixels
                    || cachedEntry.Frames.All(frame => frame.Embedding is { Length: > 0 })))
            {
                indexed[asset.MediaRef] = new IndexedAsset(asset, cachedEntry);
                cachedAssets++;
                continue;
            }

            if (cachedEntry is not null)
            {
                invalidatedAssets++;
                cacheIndex.Entries.Remove(asset.MediaRef);
                cacheChanged = true;
            }

            try
            {
                var frames = await SampleAsync(asset, request.SampleFrameCount, cancellationToken).ConfigureAwait(false);
                if (frames.Count == 0)
                {
                    skippedUnreadable++;
                    continue;
                }

                var entry = new VisualMediaIndexEntry
                {
                    MediaRef = asset.MediaRef,
                    Fingerprint = fingerprint,
                    Frames = frames,
                };
                cacheIndex.Entries[asset.MediaRef] = entry;
                indexed[asset.MediaRef] = new IndexedAsset(asset, entry);
                indexedAssets++;
                cacheChanged = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FileNotFoundException)
            {
                skippedOffline++;
            }
            catch (IOException)
            {
                skippedUnreadable++;
            }
            catch (InvalidOperationException)
            {
                skippedUnreadable++;
            }
            catch (NotSupportedException)
            {
                skippedUnreadable++;
            }
            catch (ArgumentException)
            {
                skippedUnreadable++;
            }
        }

        foreach (var staleMediaRef in cacheIndex.Entries.Keys.Where(key => !activeMediaRefs.Contains(key)).ToList())
        {
            cacheIndex.Entries.Remove(staleMediaRef);
            cacheChanged = true;
        }

        var cachePersisted = true;
        if (cacheChanged)
        {
            try
            {
                _cache.Save(request.CacheScope, cacheIndex);
            }
            catch (IOException)
            {
                cachePersisted = false;
            }
            catch (UnauthorizedAccessException)
            {
                cachePersisted = false;
            }
        }

        var baseResponse = new SearchCounters(
            indexedAssets,
            cachedAssets,
            invalidatedAssets,
            skippedOffline,
            skippedNonVisual,
            skippedUnreadable,
            cachePersisted);
        return query!.Kind switch
        {
            VisualQueryKind.Similar => SearchSimilar(query, request, indexed, baseResponse),
            VisualQueryKind.Semantic => SearchSemantic(query, request, indexed, baseResponse),
            _ => SearchVisual(query, request, indexed, baseResponse),
        };
    }

    private async Task<List<VisualIndexedFrame>> SampleAsync(
        VisualSearchAsset asset,
        int requestedSampleFrameCount,
        CancellationToken cancellationToken)
    {
        var path = asset.SourcePath!;
        var sampleFrameCount = Math.Clamp(
            requestedSampleFrameCount <= 0 ? DefaultSampleFrameCount : requestedSampleFrameCount,
            1,
            MaxSampleFrameCount);
        var timestamps = GetSampleTimestamps(asset, sampleFrameCount);
        var frames = new List<VisualIndexedFrame>(timestamps.Count);

        foreach (var timestamp in timestamps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var frame = await _frameSource.GetFrameAsync(path, timestamp, cancellationToken).ConfigureAwait(false);
            var signature = VisualSignature.FromFrame(frame);
            float[]? embedding = null;
            if (_embedder.EncodesPixels)
                embedding = _embedder.EncodeFrame(frame);
            frames.Add(new VisualIndexedFrame
            {
                TimestampSeconds = timestamp,
                Signature = signature,
                Embedding = embedding,
            });
        }

        return frames;
    }

    private static IReadOnlyList<double> GetSampleTimestamps(VisualSearchAsset asset, int sampleFrameCount)
    {
        if (asset.Type == Temno.Core.Models.ClipType.image || asset.DurationSeconds <= 0)
            return [0.0];

        var duration = Math.Max(0, asset.DurationSeconds);
        var timestamps = new double[sampleFrameCount];
        for (var index = 0; index < sampleFrameCount; index++)
            timestamps[index] = duration * (index + 0.5) / sampleFrameCount;
        return timestamps;
    }

    private VisualSearchResponse SearchVisual(
        VisualSearchQuery query,
        VisualSearchRequest request,
        IReadOnlyDictionary<string, IndexedAsset> indexed,
        SearchCounters counters)
    {
        var hits = new List<VisualSearchHit>();
        foreach (var item in indexed.Values)
        {
            if (!MatchesMediaFilter(item.Asset, request.MediaRef)) continue;
            foreach (var frame in item.Entry.Frames)
            {
                var distance = QueryDistance(frame.Signature, query);
                hits.Add(new VisualSearchHit(
                    item.Asset.MediaRef,
                    item.Asset.Name,
                    frame.TimestampSeconds,
                    Score(distance),
                    frame.Signature.MeanColorHex));
            }
        }

        return MakeResponse(
            hits,
            request.Limit,
            "visual",
            counters,
            "No locally indexable visual media matched the query.");
    }

    private VisualSearchResponse SearchSemantic(
        VisualSearchQuery query,
        VisualSearchRequest request,
        IReadOnlyDictionary<string, IndexedAsset> indexed,
        SearchCounters counters)
    {
        if (_embedder.Status != SemanticEmbedderStatus.Ready)
        {
            return new VisualSearchResponse
            {
                Status = "model_not_installed",
                Message = _embedder.Message
                    ?? "Semantic visual search needs an embedding model.",
                QueryKind = "semantic",
                IndexStatus = "modelNotInstalled",
                IndexMode = "unavailable",
                EmbedderModelId = _embedder.ModelId,
                IndexedAssets = counters.IndexedAssets,
                CachedAssets = counters.CachedAssets,
                InvalidatedAssets = counters.InvalidatedAssets,
                SkippedOffline = counters.SkippedOffline,
                SkippedNonVisual = counters.SkippedNonVisual,
                SkippedUnreadable = counters.SkippedUnreadable,
                CachePersisted = counters.CachePersisted,
            };
        }

        var onnxActive = _embedder.EncodesPixels;
        var minScore = onnxActive ? OnnxMinScore : HeuristicMinScore;
        var relativeCutoff = onnxActive ? OnnxRelativeCutoff : HeuristicRelativeCutoff;

        var textVec = _embedder.EncodeText(query.TextQuery ?? "");
        if (textVec is null)
        {
            return MakeResponse(
                [],
                request.Limit,
                "semantic",
                counters,
                "Could not embed the query text — try a color word, scene cue, or similar:<mediaRef>.",
                status: "unsupported_query");
        }

        var hits = new List<VisualSearchHit>();
        foreach (var item in indexed.Values)
        {
            if (!MatchesMediaFilter(item.Asset, request.MediaRef)) continue;
            foreach (var frame in item.Entry.Frames)
            {
                var frameVec = onnxActive
                    ? frame.Embedding
                    : _embedder.EncodeSignature(frame.Signature);
                if (frameVec is null) continue;
                var score = SemanticConceptEmbedder.Cosine(textVec, frameVec);
                if (score < minScore) continue;
                hits.Add(new VisualSearchHit(
                    item.Asset.MediaRef,
                    item.Asset.Name,
                    frame.TimestampSeconds,
                    score,
                    frame.Signature.MeanColorHex));
            }
        }

        if (hits.Count > 0)
        {
            var top = hits.Max(h => h.Score);
            var floor = top * relativeCutoff;
            hits = hits.Where(h => h.Score >= floor).ToList();
        }

        var response = MakeResponse(
            hits,
            request.Limit,
            "semantic",
            counters,
            "No locally indexable visual media matched the Moments query.");
        if (response.Status == "ok")
        {
            response.Message = onnxActive
                ? $"Moments search via {_embedder.ModelId} (on-device)."
                : $"Moments search via {_embedder.ModelId} (on-device; SigLIP2 ONNX not installed).";
        }

        return response;
    }

    private VisualSearchResponse SearchSimilar(
        VisualSearchQuery query,
        VisualSearchRequest request,
        IReadOnlyDictionary<string, IndexedAsset> indexed,
        SearchCounters counters)
    {
        var referenceMediaRef = query.ReferenceMediaRef!;
        if (!indexed.TryGetValue(referenceMediaRef, out var reference))
        {
            return MakeResponse(
                [],
                request.Limit,
                "similar",
                counters,
                $"No locally indexed visual media exists for similar:{referenceMediaRef}.",
                status: "reference_not_found");
        }

        var hits = new List<VisualSearchHit>();
        foreach (var item in indexed.Values)
        {
            if (string.Equals(item.Asset.MediaRef, referenceMediaRef, StringComparison.Ordinal)) continue;
            if (!MatchesMediaFilter(item.Asset, request.MediaRef)) continue;

            foreach (var frame in item.Entry.Frames)
            {
                var distance = reference.Entry.Frames.Min(referenceFrame => frame.Signature.DistanceTo(referenceFrame.Signature));
                hits.Add(new VisualSearchHit(
                    item.Asset.MediaRef,
                    item.Asset.Name,
                    frame.TimestampSeconds,
                    Score(distance),
                    frame.Signature.MeanColorHex));
            }
        }

        return MakeResponse(
            hits,
            request.Limit,
            "similar",
            counters,
            "No other locally indexed visual media matched the similar-media query.");
    }

    private VisualSearchResponse MakeResponse(
        IReadOnlyList<VisualSearchHit> hits,
        int requestedLimit,
        string queryKind,
        SearchCounters counters,
        string noResultsMessage,
        string? status = null,
        string? indexStatus = null,
        string? modelId = null)
    {
        var limit = Math.Clamp(requestedLimit <= 0 ? 20 : requestedLimit, 1, 100);
        var results = hits
            .OrderByDescending(hit => hit.Score)
            .ThenBy(hit => hit.MediaRef, StringComparer.Ordinal)
            .ThenBy(hit => hit.TimestampSeconds)
            .Take(limit)
            .ToList();

        var mode = MapIndexMode(_embedder.Mode);
        return new VisualSearchResponse
        {
            Status = status ?? (results.Count > 0 ? "ok" : "no_results"),
            Message = results.Count > 0
                ? "Visual search ran locally with deterministic sampled-frame signatures."
                : noResultsMessage,
            QueryKind = queryKind,
            Results = results,
            IndexedAssets = counters.IndexedAssets,
            CachedAssets = counters.CachedAssets,
            InvalidatedAssets = counters.InvalidatedAssets,
            SkippedOffline = counters.SkippedOffline,
            SkippedNonVisual = counters.SkippedNonVisual,
            SkippedUnreadable = counters.SkippedUnreadable,
            CachePersisted = counters.CachePersisted,
            IndexMode = mode,
            IndexStatus = indexStatus ?? MapIndexStatus(_embedder.Mode),
            EmbedderModelId = modelId ?? _embedder.ModelId,
        };
    }

    private static string MapIndexStatus(SemanticSearchMode mode) => mode switch
    {
        SemanticSearchMode.Model => "model",
        SemanticSearchMode.Fallback => "fallback",
        _ => "modelNotInstalled",
    };

    private static string MapIndexMode(SemanticSearchMode mode) => mode switch
    {
        SemanticSearchMode.Model => "model",
        SemanticSearchMode.Fallback => "fallback",
        _ => "unavailable",
    };

    private static double QueryDistance(VisualSignature signature, VisualSearchQuery query)
    {
        var distance = 0.0;
        var weight = 0.0;
        if (query.TargetColor is { } color)
        {
            distance += signature.ColorDistance(color) * 0.75;
            distance += (1.0 - signature.DistributionPresence(color)) * 0.15;
            weight += 0.9;
        }
        if (query.TargetLuminance is { } luminance)
        {
            distance += Math.Abs(signature.MeanLuminance - luminance) * 0.7;
            weight += 0.7;
        }
        if (query.TargetContrast is { } contrast)
        {
            distance += Math.Abs(signature.LuminanceContrast - contrast) * 0.4;
            weight += 0.4;
        }

        return weight > 0 ? Math.Clamp(distance / weight, 0.0, 1.0) : 1.0;
    }

    private static bool MatchesMediaFilter(VisualSearchAsset asset, string? mediaRef)
        => string.IsNullOrWhiteSpace(mediaRef)
            || string.Equals(asset.MediaRef, mediaRef, StringComparison.Ordinal);

    private static double Score(double distance) => 1.0 / (1.0 + Math.Max(0.0, distance));

    private sealed record IndexedAsset(VisualSearchAsset Asset, VisualMediaIndexEntry Entry);

    private readonly record struct SearchCounters(
        int IndexedAssets,
        int CachedAssets,
        int InvalidatedAssets,
        int SkippedOffline,
        int SkippedNonVisual,
        int SkippedUnreadable,
        bool CachePersisted);
}
