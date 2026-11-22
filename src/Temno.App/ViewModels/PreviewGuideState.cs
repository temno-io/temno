using System.Text.Json;
using System.Text.Json.Serialization;
using Temno.Core.IO;

namespace Temno.App.ViewModels;

/// <summary>Composition grid drawn over the preview canvas. WP-145.</summary>
public enum PreviewGridMode
{
    None = 0,
    Grid2 = 2,
    Grid3 = 3,
    Grid4 = 4,
    Grid5 = 5,
}

/// <summary>
/// Broadcast safe areas, as fractions of the canvas. The percentages are the long-standing
/// convention (action 90 %, title 80 %) rather than a project-specific choice.
/// </summary>
[Flags]
public enum PreviewSafeZones
{
    None = 0,
    Action = 1 << 0,
    Title = 1 << 1,
    Center = 1 << 2,
}

/// <summary>
/// A framing reference showing how the current canvas would crop to another aspect. It never
/// changes the project's aspect — it is an overlay only.
/// </summary>
public enum PreviewFormatReference
{
    None = 0,
    Scope = 1,      // 2.39:1
    Wide = 2,       // 16:9
    Square = 3,     // 1:1
    Portrait = 4,   // 9:16
}

public static class PreviewFormatReferences
{
    /// <summary>Width ÷ height for the reference, or null for <see cref="PreviewFormatReference.None"/>.</summary>
    public static double? AspectRatio(this PreviewFormatReference format) => format switch
    {
        PreviewFormatReference.Scope => 2.39,
        PreviewFormatReference.Wide => 16.0 / 9.0,
        PreviewFormatReference.Square => 1.0,
        PreviewFormatReference.Portrait => 9.0 / 16.0,
        _ => null,
    };

    public static string Label(this PreviewFormatReference format) => format switch
    {
        PreviewFormatReference.Scope => "Scope 2.39:1",
        PreviewFormatReference.Wide => "Wide 16:9",
        PreviewFormatReference.Square => "Square 1:1",
        PreviewFormatReference.Portrait => "Portrait 9:16",
        _ => "None",
    };

    /// <summary>Action safe is 90 % of the canvas, title safe 80 % — the broadcast convention.</summary>
    public static double Inset(this PreviewSafeZones zone) => zone switch
    {
        PreviewSafeZones.Action => 0.05,
        PreviewSafeZones.Title => 0.10,
        _ => 0,
    };
}

/// <summary>
/// User-local preview guide state. <see cref="Hidden"/> is deliberately separate from the
/// individual selections so that hiding everything and showing it again restores the same guides
/// rather than resetting them.
/// </summary>
public sealed record PreviewGuideState(
    PreviewGridMode Grid = PreviewGridMode.None,
    PreviewSafeZones SafeZones = PreviewSafeZones.None,
    PreviewFormatReference Format = PreviewFormatReference.None,
    bool Hidden = false)
{
    public static PreviewGuideState Default { get; } = new();

    /// <summary>True when anything would draw. <see cref="Hidden"/> suppresses everything.</summary>
    [JsonIgnore]
    public bool AnyVisible => !Hidden &&
        (Grid != PreviewGridMode.None || SafeZones != PreviewSafeZones.None ||
         Format != PreviewFormatReference.None);

    public string ToJson() => JsonSerializer.Serialize(this);

    public static PreviewGuideState FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<PreviewGuideState>(json) is { } parsed
                ? Clamp(parsed)
                : Default;
        }
        catch
        {
            return Default;
        }
    }

    /// <summary>A hand-edited or future file falls back per field; it never throws.</summary>
    public static PreviewGuideState Clamp(PreviewGuideState state) => new(
        Enum.IsDefined(state.Grid) ? state.Grid : PreviewGridMode.None,
        (state.SafeZones & (PreviewSafeZones.Action | PreviewSafeZones.Title | PreviewSafeZones.Center)),
        Enum.IsDefined(state.Format) ? state.Format : PreviewFormatReference.None,
        state.Hidden);
}

/// <summary>
/// User-local persistence for <see cref="PreviewGuideState"/>.
/// Path: %LocalAppData%\Temno\preview-guides.json.
/// </summary>
/// <remarks>
/// Callers must load this at <b>field initialisation</b>, not in <c>OnLoaded</c>. WP-144 shipped
/// exactly that bug: its view state was loaded in <c>OnLoaded</c>, which on some open paths fires
/// after the surface has already been built and bound, so the persisted value silently never
/// applied. Measured there as a field reading <c>Folders</c> while a fresh <c>Load()</c> returned
/// <c>Grouped</c>.
/// </remarks>
public static class PreviewGuideStore
{
    public static string DefaultPath =>
        Path.Combine(
            UserDataRoots.LocalAppDataRoot,
            "preview-guides.json");

    public static PreviewGuideState Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        try
        {
            return File.Exists(file)
                ? PreviewGuideState.FromJson(File.ReadAllText(file))
                : PreviewGuideState.Default;
        }
        catch
        {
            return PreviewGuideState.Default;
        }
    }

    public static void Save(PreviewGuideState state, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var file = path ?? DefaultPath;
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, PreviewGuideState.Clamp(state).ToJson());
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            // Guide state is a convenience; failing to persist it must never break the editor.
        }
    }
}
