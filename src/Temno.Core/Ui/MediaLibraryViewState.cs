using System;
using System.IO;
using System.Text.Json;
using Temno.Core.IO;

namespace Temno.Core.Ui;

/// <summary>
/// How the media library presents its contents. WP-144.
/// </summary>
/// <remarks>
/// The owner asked for "Folders, List, Flat, or Grouped". That list mixes two axes — hierarchy
/// (do folders exist?) and presentation (rows or groups?) — so the reading below is a decision,
/// not a transcription, and the owner should correct it if it is wrong:
/// <list type="bullet">
/// <item><b>Folders</b> — folder tiles plus the current folder's items. Today's behaviour.</item>
/// <item><b>List</b> — the current folder's items only, no folder tiles. "Show me the media here."</item>
/// <item><b>Flat</b> — every item in the project, folders ignored. "Show me everything."</item>
/// <item><b>Grouped</b> — every item, grouped by media type with headers.</item>
/// </list>
/// <b>Grouped groups by media type</b> (video / audio / image), not by folder. Folders mode already
/// answers "what is in this folder"; grouping a flat view by folder would answer the same question
/// twice. Type is the axis none of the other three modes exposes — it is what makes
/// "find every audio file in this project" a single glance instead of a search.
/// </remarks>
public enum MediaLibraryViewMode
{
    Folders = 0,
    List = 1,
    Flat = 2,
    Grouped = 3,
}

/// <summary>Thumbnail edge in pixels. <see cref="Medium"/> is the pre-WP-144 fixed value.</summary>
public enum MediaThumbnailSize
{
    Small = 0,
    Medium = 1,
    Large = 2,
    ExtraLarge = 3,
}

public static class MediaThumbnailSizes
{
    /// <summary>Decoded pixel height requested from the thumbnail cache.</summary>
    public static int ToPixels(this MediaThumbnailSize size) => size switch
    {
        MediaThumbnailSize.Small => 36,
        MediaThumbnailSize.Large => 84,
        MediaThumbnailSize.ExtraLarge => 128,
        _ => 54, // Medium — what every thumbnail was before WP-144.
    };

    /// <summary>Row height that keeps a 16:9 thumbnail plus its two text lines legible.</summary>
    public static double ToRowHeight(this MediaThumbnailSize size) => size switch
    {
        MediaThumbnailSize.Small => 36,
        MediaThumbnailSize.Large => 84,
        MediaThumbnailSize.ExtraLarge => 128,
        _ => 54,
    };
}

/// <summary>User-local media library view state. Persisted beside the editor layout.</summary>
public sealed record MediaLibraryViewState(
    MediaLibraryViewMode Mode = MediaLibraryViewMode.Folders,
    MediaThumbnailSize ThumbnailSize = MediaThumbnailSize.Medium)
{
    public static MediaLibraryViewState Default { get; } = new();

    public string ToJson() => JsonSerializer.Serialize(this);

    public static MediaLibraryViewState FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<MediaLibraryViewState>(json) is { } parsed
                ? Clamp(parsed)
                : Default;
        }
        catch
        {
            return Default;
        }
    }

    /// <summary>An out-of-range enum from a hand-edited or future file falls back, never throws.</summary>
    public static MediaLibraryViewState Clamp(MediaLibraryViewState state) => new(
        Enum.IsDefined(state.Mode) ? state.Mode : MediaLibraryViewMode.Folders,
        Enum.IsDefined(state.ThumbnailSize) ? state.ThumbnailSize : MediaThumbnailSize.Medium);
}

/// <summary>
/// User-local persistence for <see cref="MediaLibraryViewState"/>.
/// Path: %LocalAppData%\Temno\media-library-view.json — same shape as EditorLayoutStore.
/// </summary>
public static class MediaLibraryViewStore
{
    public static string DefaultPath =>
        Path.Combine(
            UserDataRoots.LocalAppDataRoot,
            "media-library-view.json");

    public static MediaLibraryViewState Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        try
        {
            return File.Exists(file)
                ? MediaLibraryViewState.FromJson(File.ReadAllText(file))
                : MediaLibraryViewState.Default;
        }
        catch
        {
            return MediaLibraryViewState.Default;
        }
    }

    public static void Save(MediaLibraryViewState state, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var file = path ?? DefaultPath;
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, MediaLibraryViewState.Clamp(state).ToJson());
            File.Move(tmp, file, overwrite: true);
        }
        catch
        {
            // View state is a convenience; failing to persist it must never break the editor.
        }
    }
}
