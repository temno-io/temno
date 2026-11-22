using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Temno.Core.IO;
using Temno.Core.Models;

namespace Temno.Core.Transcription;

/// <summary>
/// Per-user disk cache for media transcripts (Mac: TranscriptCache).
/// Never writes into project packages.
/// </summary>
public static class TranscriptDiskCache
{
    private static readonly object Gate = new();
    private static string? _rootOverride;
    private static string? _defaultRootOverride;

    /// <summary>
    /// Per-test root. <c>null</c> falls back to <see cref="SetDefaultRootForTests"/>, never straight
    /// to the live per-user root, so one test resetting the seam cannot strand a parallel test's
    /// writes in the user's cache.
    /// </summary>
    public static void SetRootForTests(string? rootDirectory) => _rootOverride = rootDirectory;

    /// <summary>
    /// Process-wide fallback for test hosts. <c>Temno.Core.Tests</c> installs a scratch directory
    /// here at assembly load, so a test that never calls <see cref="SetRootForTests"/> still writes
    /// under the temp directory instead of the user's LocalAppData transcript cache. Before this
    /// seam existed, about 600 fixture transcripts had accumulated there from ordinary test runs
    /// (WP-194).
    /// </summary>
    public static void SetDefaultRootForTests(string? rootDirectory) => _defaultRootOverride = rootDirectory;

    public static string RootDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_rootOverride))
                return _rootOverride!;
            if (!string.IsNullOrWhiteSpace(_defaultRootOverride))
                return _defaultRootOverride!;
            return Path.Combine(UserDataRoots.LocalAppDataRoot, "transcripts");
        }
    }

    public static string KeyForPath(string mediaPath)
    {
        var full = Path.GetFullPath(mediaPath);
        var info = new FileInfo(full);
        var identity = string.Join('\u001f',
            full.ToLowerInvariant(),
            info.Exists ? info.Length : 0,
            info.Exists ? info.LastWriteTimeUtc.Ticks : 0);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return hash.ToLowerInvariant();
    }

    public static string PathForKey(string key)
        => Path.Combine(RootDirectory, key + ".json");

    public static bool TryLoad(string mediaPath, out TranscriptionResult? result)
    {
        result = null;
        try
        {
            if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath))
                return false;
            var path = PathForKey(KeyForPath(mediaPath));
            if (!File.Exists(path)) return false;
            var bytes = File.ReadAllBytes(path);
            result = JsonSerializer.Deserialize<TranscriptionResult>(bytes, JsonOptions.ForProject);
            return result is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void Store(string mediaPath, TranscriptionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath)) return;

        lock (Gate)
        {
            Directory.CreateDirectory(RootDirectory);
            var path = PathForKey(KeyForPath(mediaPath));
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions.ForProject);
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
    }

    public static bool HasCached(string mediaPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(mediaPath) || !File.Exists(mediaPath)) return false;
            return File.Exists(PathForKey(KeyForPath(mediaPath)));
        }
        catch
        {
            return false;
        }
    }
}
