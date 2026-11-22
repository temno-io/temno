using Temno.Core.Ui;

namespace Temno.App.ViewModels;

// WP-158: the view-mode enums, MediaLibraryViewState and MediaLibraryViewStore moved to
// Temno.Core.Ui so the folder-scope rules that decide what the panel shows can be reached from
// Temno.Core.Tests. MediaKindGroup stays here because it holds MediaEntryItem, an App type.

/// <summary>
/// One media-type bucket for <see cref="MediaLibraryViewMode.Grouped"/>. Derives from
/// <see cref="List{T}"/> because WinUI's <c>IsSourceGrouped</c> requires each group to be the
/// enumerable of its own children.
/// </summary>
public sealed class MediaKindGroup : List<MediaEntryItem>
{
    public MediaKindGroup(string key, IEnumerable<MediaEntryItem> items) : base(items) => Key = key;

    /// <summary>Displayed as the group header. Title-cased media kind.</summary>
    public string Key { get; }

    /// <summary>
    /// ListViewHeaderItem's UIA name is <c>ToString()</c> of the group object. Without this override
    /// the header exists but reads as the CLR type name, which is how "no header" survived UIA
    /// searches for "Video" / ControlType.Header.
    /// </summary>
    public override string ToString() => Key;

    public static List<MediaKindGroup> FromItems(IEnumerable<MediaEntryItem> items) =>
        items
            .GroupBy(item => NormalizeKind(item.Kind))
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new MediaKindGroup(DisplayName(g.Key), g))
            .ToList();

    public static string NormalizeKind(string? kind)
        => string.IsNullOrWhiteSpace(kind) ? "other" : kind.Trim().ToLowerInvariant();

    public static string DisplayName(string kind) => kind switch
    {
        "video" => "Video",
        "audio" => "Audio",
        "image" => "Image",
        "text" => "Text",
        "lottie" => "Lottie",
        "sequence" => "Sequence",
        "subtitle" => "Subtitle",
        "other" => "Other",
        _ => char.ToUpperInvariant(kind[0]) + kind[1..],
    };
}
