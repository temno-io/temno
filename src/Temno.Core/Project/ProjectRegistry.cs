using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Temno.Core.IO;

namespace Temno.Core.Project;

public sealed class ProjectEntry
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("createdDate")]
    public DateTimeOffset CreatedDate { get; set; }

    [JsonPropertyName("lastOpenedDate")]
    public DateTimeOffset LastOpenedDate { get; set; }

    [JsonIgnore]
    public string Name
    {
        get
        {
            try
            {
                var path = Url.StartsWith("file:") ? new Uri(Url).LocalPath : Url;
                var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return Path.GetFileNameWithoutExtension(trimmed);
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    [JsonIgnore]
    public bool IsAccessible
    {
        get
        {
            try
            {
                var path = Url.StartsWith("file:") ? new Uri(Url).LocalPath : Url;
                return Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }
    }
}

public static class ProjectRegistry
{
    private static string RegistryPath =>
        EnvCompat.Get("TEMNO_REGISTRY_PATH")
        ?? Path.Combine(
            UserDataRoots.DocumentsRoot,
            "project-registry.json"
        );

    public static List<ProjectEntry> Load()
    {
        if (!File.Exists(RegistryPath))
            return [];

        try
        {
            var json = File.ReadAllBytes(RegistryPath);
            return JsonSerializer.Deserialize<List<ProjectEntry>>(json)
                ?? throw new InvalidDataException("Project registry must contain a JSON array.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Project registry is invalid: {RegistryPath}", ex);
        }
    }

    public static void Save(List<ProjectEntry> entries)
    {
        var path = RegistryPath;
        var dir = Path.GetDirectoryName(path);
        if (dir != null)
            Directory.CreateDirectory(dir);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllBytes(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* cleanup must not mask the persistence result */ }
        }
    }

    public static ProjectEntry Register(string path)
    {
        var entries = Load();
        var normalizedPath = Path.GetFullPath(path);
        var fileUrl = new Uri(normalizedPath).AbsoluteUri;

        var existing = entries.Find(e => PathEquals(e.Url, fileUrl) || PathEquals(e.Url, normalizedPath));
        if (existing != null)
        {
            existing.LastOpenedDate = DateTimeOffset.UtcNow;
            existing.Url = fileUrl; // ensure file URL format
            Save(entries);
            return existing;
        }
        else
        {
            var entry = new ProjectEntry
            {
                Id = Guid.NewGuid(),
                Url = fileUrl,
                CreatedDate = DateTimeOffset.UtcNow,
                LastOpenedDate = DateTimeOffset.UtcNow
            };
            entries.Add(entry);
            Save(entries);
            return entry;
        }
    }

    public static void Remove(string path)
    {
        var entries = Load();
        var normalizedPath = Path.GetFullPath(path);
        var fileUrl = new Uri(normalizedPath).AbsoluteUri;

        entries.RemoveAll(e => PathEquals(e.Url, fileUrl) || PathEquals(e.Url, normalizedPath));
        Save(entries);
    }

    private static bool PathEquals(string url1, string url2)
    {
        try
        {
            var path1 = url1.StartsWith("file:") ? new Uri(url1).LocalPath : url1;
            var path2 = url2.StartsWith("file:") ? new Uri(url2).LocalPath : url2;
            return string.Equals(Path.GetFullPath(path1), Path.GetFullPath(path2), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(url1, url2, StringComparison.OrdinalIgnoreCase);
        }
    }
}
