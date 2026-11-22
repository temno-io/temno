using System.Text.Json;
using Temno.Core.Models;
using Temno.Core.Project;

namespace Temno.Core.IO;

/// <summary>Named project templates under ~/.temno/templates (WP-96; WP-193 dual-read).</summary>
public sealed class ProjectTemplateStore
{
    private static readonly JsonSerializerOptions TemplateJson = new() { WriteIndented = true };
    private static ProjectTemplateStore? _instance;

    public static ProjectTemplateStore Shared
    {
        get
        {
            var overrideDir = EnvCompat.Get("TEMNO_TEMPLATES_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir))
                return new ProjectTemplateStore(overrideDir);
            return _instance ??= new ProjectTemplateStore();
        }
    }

    /// <summary>Canonical directory: <c>~/.temno/templates</c>.</summary>
    public static string DefaultDirectory => Path.Combine(UserDataRoots.HomeRoot, "templates");

    /// <summary>Legacy directory: <c>~/.palmier/templates</c>. Listed for one release, never written.</summary>
    public static string LegacyDirectory => Path.Combine(UserDataRoots.LegacyHomeRoot, "templates");

    private readonly string _dir;
    private readonly string? _legacyDir;

    public ProjectTemplateStore(string? baseDirectory = null, string? legacyBaseDirectory = null)
    {
        if (baseDirectory is null)
        {
            _dir = DefaultDirectory;
            _legacyDir = legacyBaseDirectory ?? LegacyDirectory;
        }
        else
        {
            _dir = Path.Combine(baseDirectory, "templates");
            _legacyDir = legacyBaseDirectory is null ? null : Path.Combine(legacyBaseDirectory, "templates");
        }
    }

    public string TemplateDirectory => _dir;

    public IReadOnlyList<ProjectTemplateInfo> List()
    {
        var byName = new Dictionary<string, ProjectTemplateInfo>(StringComparer.OrdinalIgnoreCase);
        // Legacy first so the new directory wins on name collisions.
        if (_legacyDir is not null)
            Collect(_legacyDir, byName);
        Collect(_dir, byName);
        return byName.Values
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Collect(string dir, Dictionary<string, ProjectTemplateInfo> byName)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var path in Directory.GetFiles(dir, "*.json"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var info = new FileInfo(path);
            byName[name] = new ProjectTemplateInfo(name, path, info.LastWriteTimeUtc);
        }
    }

    public ProjectTemplateInfo Save(string name, ProjectFile project, MediaManifest? manifest = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(project);
        Directory.CreateDirectory(_dir);
        var safe = Sanitize(name);
        var path = Path.Combine(_dir, safe + ".json");
        var payload = new ProjectTemplatePayload
        {
            Name = name.Trim(),
            SavedAt = DateTimeOffset.UtcNow,
            Project = project,
            Manifest = manifest,
        };
        var json = JsonSerializer.Serialize(payload, TemplateJson);
        File.WriteAllText(path, json);
        return new ProjectTemplateInfo(name.Trim(), path, DateTimeOffset.UtcNow);
    }

    public bool Remove(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var path = Path.Combine(_dir, Sanitize(name) + ".json");
        if (!File.Exists(path) && _legacyDir is not null)
        {
            var legacyPath = Path.Combine(_legacyDir, Sanitize(name) + ".json");
            if (File.Exists(legacyPath))
                path = legacyPath;
        }
        if (!File.Exists(path))
        {
            // try case-insensitive match
            var hit = List().FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            if (hit is null) return false;
            path = hit.Path;
        }
        File.Delete(path);
        return true;
    }

    /// <summary>
    /// Loads a saved template by file name. A missing name returns null. A rooted name, a path,
    /// or a file that resolves outside the templates directory fails without returning content.
    /// </summary>
    public ProjectTemplateContents? TryLoad(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        if (Path.IsPathRooted(name)
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidOperationException($"Project template '{name}' is not a template name.");
        }

        var match = List().FirstOrDefault(t =>
            string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Name, Sanitize(name), StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return null;
        if (!IsTemplatePath(match.Path))
            throw new InvalidOperationException($"Project template '{name}' is outside the templates directory.");

        ProjectTemplatePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ProjectTemplatePayload>(File.ReadAllText(match.Path), TemplateJson);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"Project template '{name}' is unreadable.");
        }

        if (payload?.Project is null || payload.Project.Timelines.Count == 0)
            throw new InvalidOperationException($"Project template '{name}' is unreadable.");

        var project = ProjectFile.Decode(payload.Project.Encode());
        if (project.Timelines.Count == 0)
            throw new InvalidOperationException($"Project template '{name}' is unreadable.");
        MediaManifest? manifest = payload.Manifest is null
            ? null
            : MediaManifest.Decode(payload.Manifest.Encode());
        return new ProjectTemplateContents(project, manifest);
    }

    private bool IsTemplatePath(string path)
    {
        if (PathContainment.IsInside(_dir, path))
            return true;
        return _legacyDir is not null && PathContainment.IsInside(_legacyDir, path);
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}

public sealed record ProjectTemplateInfo(string Name, string Path, DateTimeOffset ModifiedUtc);

public sealed record ProjectTemplateContents(ProjectFile Project, MediaManifest? Manifest);

internal sealed class ProjectTemplatePayload
{
    public string Name { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; }
    public ProjectFile? Project { get; set; }
    public MediaManifest? Manifest { get; set; }
}
