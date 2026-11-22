using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Temno.Media.Cache;

/// <summary>
/// Disk cache of scrub proxies under {cacheRoot}/proxies, keyed by source identity (path + size + mtime)
/// plus the proxy height, so replacing the source invalidates its proxy. See ProxyPolicy for when a
/// proxy is warranted and ProxyTranscoder for the ffmpeg transcode itself.
/// </summary>
public sealed class ProxyCache
{
    public const string FileExtension = ".mp4";

    private readonly DiskCache _cache;

    public ProxyCache(
        MediaCachePaths paths,
        ProxyPolicy? policy = null,
        long maxTotalBytes = DiskCache.DefaultMaxTotalBytes,
        TimeSpan? handoutGrace = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Policy = policy ?? ProxyPolicy.Default;
        _cache = new DiskCache(paths.ProxiesDirectory, maxTotalBytes, handoutGrace);
    }

    public ProxyPolicy Policy { get; }

    public string DirectoryPath => _cache.DirectoryPath;

    public long MaxTotalBytes => _cache.MaxTotalBytes;

    /// <summary>Stats the media file; changed size/mtime yields a different key.</summary>
    public static string KeyFor(string mediaPath, int proxyHeight)
    {
        return MediaCacheKey.Compute(mediaPath, Parameters(proxyHeight));
    }

    /// <summary>Pure form for callers that already hold the file identity.</summary>
    public static string KeyFor(in MediaIdentity identity, int proxyHeight)
    {
        return MediaCacheKey.Compute(identity, Parameters(proxyHeight));
    }

    /// <summary>
    /// Deterministic target path a transcode should land at for this source + height.
    /// Reserves nothing on disk beyond ensuring the proxies directory exists.
    /// </summary>
    public string ReservePath(string mediaPath, int proxyHeight)
    {
        var path = _cache.PathForKey(KeyFor(mediaPath, proxyHeight), FileExtension);
        // WP-179: also applies the key-format sweep, so a transcode landing at this path cannot be
        // swept away by the first cache read that happens after it.
        _cache.EnsureDirectory();
        return path;
    }

    public bool TryGet(string mediaPath, int proxyHeight, out string path)
    {
        return _cache.TryGet(KeyFor(mediaPath, proxyHeight), FileExtension, out path);
    }

    /// <summary>
    /// WP-179: a hit held against eviction until the lease is disposed, for a caller that keeps the
    /// proxy open longer than <see cref="DiskCache.HandoutGrace"/>. Null on a miss.
    /// </summary>
    public CacheLease? Rent(string mediaPath, int proxyHeight)
    {
        return _cache.Rent(KeyFor(mediaPath, proxyHeight), FileExtension);
    }

    /// <summary>
    /// Returns the cached proxy path, or invokes <paramref name="transcoder"/> with a temp path to
    /// write the proxy to. Safe under concurrent calls for the same key.
    /// </summary>
    public Task<string> GetOrCreateAsync(
        string mediaPath,
        int proxyHeight,
        Func<string, CancellationToken, Task> transcoder,
        CancellationToken cancellationToken = default)
    {
        var key = KeyFor(mediaPath, proxyHeight);
        return _cache.GetOrCreateAsync(key, FileExtension, transcoder, cancellationToken);
    }

    /// <summary>Convenience overload: transcodes via ffmpeg (ProxyTranscoder).</summary>
    public Task<string> GetOrCreateAsync(
        string mediaPath,
        int proxyHeight,
        CancellationToken cancellationToken = default)
    {
        var transcoder = new ProxyTranscoder();
        return GetOrCreateAsync(
            mediaPath,
            proxyHeight,
            (targetPath, ct) => transcoder.TranscodeAsync(mediaPath, targetPath, proxyHeight, ct),
            cancellationToken);
    }

    public long CurrentSizeBytes() => _cache.CurrentSizeBytes();

    public int Evict() => _cache.Evict();

    public void Clear() => _cache.Clear();

    private static string Parameters(int proxyHeight)
    {
        if (proxyHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(proxyHeight), "Proxy height must be positive.");
        }

        return string.Create(CultureInfo.InvariantCulture, $"proxy|h={proxyHeight}");
    }
}
