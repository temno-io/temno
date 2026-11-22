using Temno.Core.IO;
using Temno.Core.Session;

namespace Temno.App.Services;

/// <summary>
/// User-local persistence for <see cref="EditorLayoutPreferences"/>.
/// Path: %LocalAppData%\Temno\editor-layout.json
/// </summary>
public static class EditorLayoutStore
{
    public static string DefaultPath =>
        Path.Combine(
            UserDataRoots.LocalAppDataRoot,
            "editor-layout.json");

    public static EditorLayoutPreferences Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        try
        {
            if (!File.Exists(file))
                return EditorLayoutPreferences.ForPreset(EditorLayoutPreset.Default);
            return EditorLayoutPreferences.FromJson(File.ReadAllText(file));
        }
        catch
        {
            return EditorLayoutPreferences.ForPreset(EditorLayoutPreset.Default);
        }
    }

    public static void Save(EditorLayoutPreferences prefs, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        var file = path ?? DefaultPath;
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var clamped = EditorLayoutPreferences.Clamp(prefs);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, clamped.ToJson());
        File.Move(tmp, file, overwrite: true);
    }
}
