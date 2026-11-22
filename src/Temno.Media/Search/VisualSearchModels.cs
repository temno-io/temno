using System.Security.Cryptography;
using System.Text;
using Temno.Core.Models;

namespace Temno.Media.Search;

public interface IVisualSearchService
{
    Task<VisualSearchResponse> SearchAsync(VisualSearchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One local media source eligible for visual indexing.</summary>
public sealed record VisualSearchAsset(
    string MediaRef,
    string Name,
    ClipType Type,
    string? SourcePath,
    double DurationSeconds)
{
    public bool IsVisual => Type is ClipType.video or ClipType.image;
}

public sealed record VisualSearchRequest(
    string CacheScope,
    IReadOnlyList<VisualSearchAsset> Assets,
    string Query,
    string? MediaRef = null,
    int Limit = 20,
    int SampleFrameCount = 5);

public enum VisualQueryKind
{
    Visual,
    Similar,
    Semantic,
}

public sealed class VisualSearchQuery
{
    public VisualQueryKind Kind { get; init; }
    public VisualColor? TargetColor { get; init; }
    public double? TargetLuminance { get; init; }
    public double? TargetContrast { get; init; }
    public string? ReferenceMediaRef { get; init; }
    /// <summary>Free-text Moments query (WP-95).</summary>
    public string? TextQuery { get; init; }
}

public sealed class VisualSearchResponse
{
    public string Status { get; set; } = "ok";
    public string Message { get; set; } = "";
    public string QueryKind { get; set; } = "visual";
    public IReadOnlyList<VisualSearchHit> Results { get; set; } = [];
    public int IndexedAssets { get; set; }
    public int CachedAssets { get; set; }
    public int InvalidatedAssets { get; set; }
    public int SkippedOffline { get; set; }
    public int SkippedNonVisual { get; set; }
    public int SkippedUnreadable { get; set; }
    public bool CachePersisted { get; set; }
    /// <summary>Mac visual index status, plus WP-111 mode via fallback | model | modelNotInstalled.</summary>
    public string? IndexStatus { get; set; }

    /// <summary>model | fallback | unavailable — also copied onto <see cref="IndexStatus"/> for MCP.</summary>
    public string? IndexMode { get; set; }

    public string? EmbedderModelId { get; set; }
}

public sealed record VisualSearchHit(
    string MediaRef,
    string Name,
    double TimestampSeconds,
    double Score,
    string MeanColorHex);

/// <summary>
/// A local fingerprint intentionally stores hashes rather than a raw source path.
/// It detects moved/replaced files as well as content and metadata changes.
/// </summary>
public sealed class VisualSourceFingerprint
{
    public string PathIdentity { get; set; } = "";
    public long Length { get; set; }
    public long LastWriteTimeUtcTicks { get; set; }
    public string ContentHash { get; set; } = "";

    public static VisualSourceFingerprint FromFile(string sourcePath)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Visual search source is offline.", fullPath);

        return new VisualSourceFingerprint
        {
            PathIdentity = HashText(fullPath),
            Length = info.Length,
            LastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks,
            ContentHash = HashFileEdges(fullPath, info.Length),
        };
    }

    public bool Matches(VisualSourceFingerprint? other)
        => other is not null
            && Length == other.Length
            && LastWriteTimeUtcTicks == other.LastWriteTimeUtcTicks
            && string.Equals(PathIdentity, other.PathIdentity, StringComparison.Ordinal)
            && string.Equals(ContentHash, other.ContentHash, StringComparison.Ordinal);

    private static string HashText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string HashFileEdges(string path, long length)
    {
        const int edgeLength = 64 * 1024;
        var firstLength = (int)Math.Min(edgeLength, length);
        var lastLength = length > edgeLength ? (int)Math.Min(edgeLength, length - firstLength) : 0;
        var buffer = new byte[firstLength + lastLength + sizeof(long)];

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (firstLength > 0)
            stream.ReadExactly(buffer.AsSpan(0, firstLength));
        if (lastLength > 0)
        {
            stream.Position = Math.Max(0, length - lastLength);
            stream.ReadExactly(buffer.AsSpan(firstLength, lastLength));
        }

        BitConverter.TryWriteBytes(buffer.AsSpan(firstLength + lastLength), length);
        return Convert.ToHexString(SHA256.HashData(buffer));
    }
}

public sealed class VisualSearchIndex
{
    public int Version { get; set; } = 2;

    /// <summary>
    /// Active embedder id. Concept-space and SigLIP caches must not share JSON.
    /// </summary>
    public string ModelId { get; set; } = SemanticConceptEmbedder.ModelIdValue;

    public Dictionary<string, VisualMediaIndexEntry> Entries { get; set; } = new(StringComparer.Ordinal);
}

public sealed class VisualMediaIndexEntry
{
    public string MediaRef { get; set; } = "";
    public VisualSourceFingerprint Fingerprint { get; set; } = new();
    public List<VisualIndexedFrame> Frames { get; set; } = [];
}

public sealed class VisualIndexedFrame
{
    public double TimestampSeconds { get; set; }
    public VisualSignature Signature { get; set; } = new();

    /// <summary>SigLIP pixel embedding only. Heuristic frames leave this null.</summary>
    public float[]? Embedding { get; set; }
}
