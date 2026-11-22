using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Temno.Core.IO;

namespace Temno.Media.Search;

/// <summary>
/// Local per-user cache for visual-search signatures. It deliberately never
/// reads from or writes to a project package.
/// </summary>
public sealed class VisualSearchCache
{
    private const int CurrentVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public VisualSearchCache(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory
            ?? EnvCompat.Get("TEMNO_VISUAL_SEARCH_CACHE_DIR")
            ?? Path.Combine(
                UserDataRoots.LocalAppDataRoot,
                "visual-search");
    }

    public string RootDirectory { get; }

    /// <summary>
    /// WP-397: bytes the index holds on disk under <see cref="RootDirectory"/>, for Settings → Search.
    /// A missing folder is 0, and a file that vanishes or is locked mid-walk is skipped.
    /// </summary>
    public long MeasureBytes()
    {
        if (!Directory.Exists(RootDirectory)) return 0;
        long total = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var file in new DirectoryInfo(RootDirectory).EnumerateFiles("*", options))
            {
                try { total += file.Length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return total;
    }

    /// <summary>The same size wording as Settings → Privacy → Clear cache.</summary>
    public static string FormatSize(long bytes) => Temno.Media.Cache.CacheTreeClearResult.FormatByteSize(bytes);

    public VisualSearchIndex Load(string cacheScope, string? modelId = null)
    {
        var expectedModel = string.IsNullOrWhiteSpace(modelId)
            ? SemanticConceptEmbedder.ModelIdValue
            : modelId;
        var empty = new VisualSearchIndex { Version = CurrentVersion, ModelId = expectedModel };
        var path = GetCachePath(cacheScope, expectedModel);
        if (!File.Exists(path)) return empty;

        try
        {
            var index = JsonSerializer.Deserialize<VisualSearchIndex>(File.ReadAllText(path), JsonOptions);
            if (index is null || index.Version != CurrentVersion)
                return empty;
            if (!string.Equals(index.ModelId, expectedModel, StringComparison.Ordinal))
                return empty;

            index.Entries ??= new Dictionary<string, VisualMediaIndexEntry>(StringComparer.Ordinal);
            return index;
        }
        catch (JsonException)
        {
            return empty;
        }
        catch (IOException)
        {
            return empty;
        }
        catch (UnauthorizedAccessException)
        {
            return empty;
        }
    }

    public void Save(string cacheScope, VisualSearchIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        index.Version = CurrentVersion;
        if (string.IsNullOrWhiteSpace(index.ModelId))
            index.ModelId = SemanticConceptEmbedder.ModelIdValue;
        index.Entries ??= new Dictionary<string, VisualMediaIndexEntry>(StringComparer.Ordinal);

        Directory.CreateDirectory(RootDirectory);
        var path = GetCachePath(cacheScope, index.ModelId);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(index, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { /* best-effort cache cleanup */ }
            }
        }
    }

    public string GetCachePath(string cacheScope, string? modelId = null)
    {
        if (string.IsNullOrWhiteSpace(cacheScope))
            throw new ArgumentException("A non-empty cache scope is required.", nameof(cacheScope));

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheScope)));
        var modelKey = SanitizeModelKey(modelId ?? SemanticConceptEmbedder.ModelIdValue);
        return Path.Combine(RootDirectory, $"visual-index-v{CurrentVersion}-{modelKey}-{hash}.json");
    }

    private static string SanitizeModelKey(string modelId)
    {
        var chars = modelId.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(chars[i]) && chars[i] is not '-' and not '_')
                chars[i] = '-';
        }

        var key = new string(chars).Trim('-');
        return string.IsNullOrEmpty(key) ? "model" : key;
    }
}
