using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Temno.Core.Models;

namespace Temno.Core.IO;

public class PresetStore
{
    private static PresetStore? _instance;
    public static PresetStore Shared => _instance ??= new PresetStore();

    private readonly string _presetDirectory;
    private readonly string _textStylesPath;
    private readonly string _exportPresetsPath;
    private readonly string? _legacyTextStylesPath;
    private readonly string? _legacyExportPresetsPath;

    public List<TextStylePreset> TextStyles { get; private set; } = new();
    public List<ExportPreset> ExportPresets { get; private set; } = new();

    /// <summary>Canonical base directory: <c>~/.temno</c> (WP-193).</summary>
    public static string DefaultBaseDirectory => UserDataRoots.HomeRoot;

    /// <summary>Legacy base directory: <c>~/.palmier</c>. Read for one release, never written.</summary>
    public static string LegacyBaseDirectory => UserDataRoots.LegacyHomeRoot;

    public PresetStore(string? baseDirectory = null, string? legacyBaseDirectory = null)
    {
        var baseDir = baseDirectory ?? DefaultBaseDirectory;
        _presetDirectory = Path.Combine(baseDir, "presets");
        _textStylesPath = Path.Combine(_presetDirectory, "text-styles.json");
        _exportPresetsPath = Path.Combine(_presetDirectory, "export-presets.json");

        // Explicit base directories (tests) have no legacy pair unless one is given;
        // only the default root falls back to ~/.palmier automatically.
        var legacyBase = legacyBaseDirectory ?? (baseDirectory is null ? LegacyBaseDirectory : null);
        if (legacyBase is not null)
        {
            var legacyDir = Path.Combine(legacyBase, "presets");
            _legacyTextStylesPath = Path.Combine(legacyDir, "text-styles.json");
            _legacyExportPresetsPath = Path.Combine(legacyDir, "export-presets.json");
        }

        LoadAll();
    }

    public string PresetDirectory => _presetDirectory;

    public void LoadAll()
    {
        var textStyles = Load<TextStylePreset>(ReadPath(_textStylesPath, _legacyTextStylesPath));
        var exportPresets = Load<ExportPreset>(ReadPath(_exportPresetsPath, _legacyExportPresetsPath));
        TextStyles = textStyles;
        ExportPresets = exportPresets;
    }

    /// <summary>Read-side dual-read: the new path wins when present, else the legacy path.</summary>
    private static string ReadPath(string path, string? legacyPath)
    {
        if (File.Exists(path))
            return path;
        if (legacyPath is not null && File.Exists(legacyPath))
            return legacyPath;
        return path;
    }

    private static List<T> Load<T>(string path)
    {
        if (!File.Exists(path))
        {
            return new List<T>();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<T>>(json)
                ?? throw new InvalidDataException($"Preset file must contain a JSON array: {path}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Preset file is invalid: {path}", ex);
        }
    }

    private void Save<T>(List<T> items, string path)
    {
        Directory.CreateDirectory(_presetDirectory);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); }
            catch { /* cleanup must not mask the persistence result */ }
        }
    }

    // MARK: - Text style presets

    public TextStylePreset SaveTextStyle(string name, TextStyle style)
    {
        var existing = TextStyles.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var replacement = new TextStylePreset { Id = existing.Id, Name = existing.Name, Style = style };
            var candidate = TextStyles.Select(item => ReferenceEquals(item, existing) ? replacement : item).ToList();
            Save(candidate, _textStylesPath);
            TextStyles = candidate;
            return replacement;
        }

        var preset = new TextStylePreset(name, style);
        var additions = TextStyles.Append(preset).ToList();
        Save(additions, _textStylesPath);
        TextStyles = additions;
        return preset;
    }

    public TextStylePreset? GetTextStyle(string id)
    {
        return TextStyles.FirstOrDefault(x => x.Id == id || string.Equals(x.Name, id, StringComparison.OrdinalIgnoreCase));
    }

    public bool RemoveTextStyle(string id)
    {
        var existing = TextStyles.FirstOrDefault(x => x.Id == id || string.Equals(x.Name, id, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            return false;
        }

        var candidate = TextStyles.Where(item => !ReferenceEquals(item, existing)).ToList();
        Save(candidate, _textStylesPath);
        TextStyles = candidate;
        return true;
    }

    // MARK: - Export presets

    public ExportPreset SaveExportPreset(
        string name, VideoCodec codec, ExportResolution resolution, double? bitrateMbps,
        bool maxRenderQuality, bool maxBitDepth = false)
    {
        var existing = ExportPresets.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var replacement = new ExportPreset
            {
                Id = existing.Id,
                Name = existing.Name,
                Codec = codec,
                Resolution = resolution,
                BitrateMbps = bitrateMbps,
                MaxRenderQuality = maxRenderQuality,
                MaxBitDepth = maxBitDepth,
            };
            var candidate = ExportPresets.Select(item => ReferenceEquals(item, existing) ? replacement : item).ToList();
            Save(candidate, _exportPresetsPath);
            ExportPresets = candidate;
            return replacement;
        }

        var preset = new ExportPreset(name, codec, resolution, bitrateMbps, maxRenderQuality, maxBitDepth);
        var additions = ExportPresets.Append(preset).ToList();
        Save(additions, _exportPresetsPath);
        ExportPresets = additions;
        return preset;
    }

    public ExportPreset? GetExportPreset(string id)
    {
        return ExportPresets.FirstOrDefault(x => x.Id == id || string.Equals(x.Name, id, StringComparison.OrdinalIgnoreCase));
    }

    public bool RemoveExportPreset(string id)
    {
        var existing = ExportPresets.FirstOrDefault(x => x.Id == id || string.Equals(x.Name, id, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            return false;
        }

        var candidate = ExportPresets.Where(item => !ReferenceEquals(item, existing)).ToList();
        Save(candidate, _exportPresetsPath);
        ExportPresets = candidate;
        return true;
    }
}
