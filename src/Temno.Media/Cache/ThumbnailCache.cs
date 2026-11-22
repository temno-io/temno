using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Temno.Media.Cache;

/// <summary>
/// Disk cache of per-frame JPEG thumbnails under {cacheRoot}/thumbnails, keyed by source identity
/// (path + size + mtime) plus time offset and height.
/// Mac: Timeline/MediaVisualCache.swift (video thumbnails; the Mac sprite-sheet layout is replaced by
/// one file per (time, height) request on Windows).
/// </summary>
public sealed class ThumbnailCache
{
    public const string FileExtension = ".jpg";

    private readonly DiskCache _cache;

    public ThumbnailCache(
        MediaCachePaths paths,
        long maxTotalBytes = DiskCache.DefaultMaxTotalBytes,
        TimeSpan? handoutGrace = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _cache = new DiskCache(paths.ThumbnailsDirectory, maxTotalBytes, handoutGrace);
    }

    public string DirectoryPath => _cache.DirectoryPath;

    public long MaxTotalBytes => _cache.MaxTotalBytes;

    /// <summary>Stats the media file; changed size/mtime yields a different key.</summary>
    public static string KeyFor(string mediaPath, double sourceSeconds, int height)
    {
        return MediaCacheKey.Compute(mediaPath, Parameters(sourceSeconds, height));
    }

    /// <summary>Pure form for callers that already hold the file identity.</summary>
    public static string KeyFor(in MediaIdentity identity, double sourceSeconds, int height)
    {
        return MediaCacheKey.Compute(identity, Parameters(sourceSeconds, height));
    }

    public bool TryGet(string mediaPath, double sourceSeconds, int height, out string path)
    {
        return _cache.TryGet(KeyFor(mediaPath, sourceSeconds, height), FileExtension, out path);
    }

    /// <summary>
    /// WP-179: a hit held against eviction until the lease is disposed, for a caller that keeps the
    /// file open longer than <see cref="DiskCache.HandoutGrace"/>. Null on a miss.
    /// </summary>
    public CacheLease? Rent(string mediaPath, double sourceSeconds, int height)
    {
        return _cache.Rent(KeyFor(mediaPath, sourceSeconds, height), FileExtension);
    }

    /// <summary>
    /// Returns the cached thumbnail path, or invokes <paramref name="generator"/> with a temp path to
    /// write the image to. Safe under concurrent calls for the same key.
    /// </summary>
    public Task<string> GetOrCreateAsync(
        string mediaPath,
        double sourceSeconds,
        int height,
        Func<string, CancellationToken, Task> generator,
        CancellationToken cancellationToken = default)
    {
        var key = KeyFor(mediaPath, sourceSeconds, height);
        return _cache.GetOrCreateAsync(key, FileExtension, generator, cancellationToken);
    }

    /// <summary>Convenience overload: generates the thumbnail via ffmpeg (WP-20 frame extraction).</summary>
    public Task<string> GetOrCreateAsync(
        string mediaPath,
        double sourceSeconds,
        int height,
        CancellationToken cancellationToken = default)
    {
        var generator = new FfmpegThumbnailGenerator();
        return GetOrCreateAsync(
            mediaPath,
            sourceSeconds,
            height,
            (targetPath, ct) => generator.GenerateAsync(mediaPath, sourceSeconds, height, targetPath, ct),
            cancellationToken);
    }

    public long CurrentSizeBytes() => _cache.CurrentSizeBytes();

    public int Evict() => _cache.Evict();

    public void Clear() => _cache.Clear();

    private static string Parameters(double sourceSeconds, int height)
    {
        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        // Millisecond precision keeps keys stable across double round-trips.
        var time = sourceSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"thumb|t={time}|h={height}");
    }
}
