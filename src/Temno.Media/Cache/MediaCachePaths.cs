using System;
using System.IO;
using Temno.Core.IO;

namespace Temno.Media.Cache;

/// <summary>
/// Platform-correct cache root plus the thumbnail/proxy subdirectories.
/// Mac: Utilities/DiskCache.swift (root = ~/Library/Caches/TemnoPro).
/// Windows analogue: %LOCALAPPDATA%\Temno\cache, and %LOCALAPPDATA%\Temno-Dev\cache for a development build (WP-334).
/// </summary>
public sealed class MediaCachePaths
{
    /// <summary>Environment override so tests and portable installs can redirect the cache.</summary>
    public const string EnvironmentOverrideVariable = "TEMNO_CACHE_DIR";

    public const string ThumbnailsSubdirectory = "thumbnails";
    public const string ProxiesSubdirectory = "proxies";

    public MediaCachePaths(string rootDirectory)
    {
        if (string.IsNullOrEmpty(rootDirectory))
        {
            throw new ArgumentException("Root directory cannot be null or empty.", nameof(rootDirectory));
        }

        Root = rootDirectory;
    }

    /// <summary>Cache paths at the platform default root (recomputed per access so env overrides apply).</summary>
    public static MediaCachePaths Default => new(DefaultRoot());

    public string Root { get; }

    public string ThumbnailsDirectory => Path.Combine(Root, ThumbnailsSubdirectory);

    public string ProxiesDirectory => Path.Combine(Root, ProxiesSubdirectory);

    public string EnsureThumbnailsDirectory()
    {
        var path = ThumbnailsDirectory;
        Directory.CreateDirectory(path);
        return path;
    }

    public string EnsureProxiesDirectory()
    {
        var path = ProxiesDirectory;
        Directory.CreateDirectory(path);
        return path;
    }

    public static string DefaultRoot()
    {
        var overridePath = EnvCompat.Get(EnvironmentOverrideVariable);
        if (!string.IsNullOrEmpty(overridePath))
        {
            return overridePath;
        }

        if (OperatingSystem.IsWindows())
        {
            // WP-334: this build's root, so Temno-Dev never fills or clears the production app's cache.
            return Path.Combine(UserDataRoots.LocalAppDataRoot, "cache");
        }

        if (OperatingSystem.IsMacOS())
        {
            // Mirrors the Mac app exactly: ~/Library/Caches/TemnoPro.
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library",
                "Caches",
                "TemnoPro");
        }

        var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (!string.IsNullOrEmpty(xdgCache))
        {
            return Path.Combine(xdgCache, "TemnoPro");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "TemnoPro");
    }
}
