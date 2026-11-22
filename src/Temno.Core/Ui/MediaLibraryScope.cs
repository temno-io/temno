using System.Collections.Generic;
using System.Linq;
using Temno.Core.Models;

namespace Temno.Core.Ui;

/// <summary>Why the media panel is showing its empty state. WP-158.</summary>
public enum MediaLibraryEmptyReason
{
    /// <summary>Something is on screen; the empty state stays collapsed.</summary>
    None,

    /// <summary>The project genuinely holds no media. "No media yet" is true here and nowhere else.</summary>
    ProjectHasNoMedia,

    /// <summary>The project holds media, but none of it is in view. Offer the way back.</summary>
    NothingInThisFolder,
}

/// <summary>
/// Which media the panel shows for one (view mode, current folder) pair, and what the empty state may
/// claim. WinUI-free so `Temno.Core.Tests` can reach it — the WP-152 pattern.
/// </summary>
/// <remarks>
/// WP-158. Before this, <b>List</b> was the only mode that applied the folder filter while rendering
/// no folder tiles, and the breadcrumb showed no children — so media inside a folder was invisible
/// *and* unreachable, under the words "No media yet". The owner hit exactly that: an agent filed two
/// imports into folders, the panel was in List mode, and the library read as empty while both clips
/// sat on the timeline.
///
/// <para><b>The reading this restores.</b> <see cref="MediaLibraryViewMode"/>'s own doc comment says
/// its four modes were "a decision, not a transcription, and the owner should correct it if it is
/// wrong". The correction here is the smallest one that makes every mode reachable while keeping all
/// four distinct:</para>
/// <list type="bullet">
/// <item><b>Folders</b> — folder tiles plus the current folder's own items. Unchanged.</item>
/// <item><b>List</b> — the current folder <i>and everything beneath it</i>, media only, no tiles.
/// "Show me the media here" now includes what is filed below here, so at the library root it shows
/// the whole project and the reported state cannot occur.</item>
/// <item><b>Flat</b> — every item, folders ignored. Unchanged.</item>
/// <item><b>Grouped</b> — every item, grouped by media type. Unchanged.</item>
/// </list>
/// <para>The alternative — give List folder tiles — was rejected because it makes List identical to
/// Folders in behaviour, leaving a mode that differs from another mode in nothing at all.</para>
/// </remarks>
public static class MediaLibraryScope
{
    /// <summary>Folders is the only mode that draws folder tiles.</summary>
    public static bool ShowsFolderTiles(MediaLibraryViewMode mode)
        => mode == MediaLibraryViewMode.Folders;

    /// <summary>Flat and Grouped ignore the current folder entirely.</summary>
    public static bool IgnoresFolders(MediaLibraryViewMode mode)
        => mode is MediaLibraryViewMode.Flat or MediaLibraryViewMode.Grouped;

    /// <summary>List reaches through the current folder into its descendants; Folders does not.</summary>
    public static bool IncludesDescendants(MediaLibraryViewMode mode)
        => mode == MediaLibraryViewMode.List;

    /// <summary>
    /// The folder ids whose items are in view, including <paramref name="currentFolderId"/> itself
    /// (null means the library root). Meaningless for the modes where
    /// <see cref="IgnoresFolders"/> is true — those show everything.
    /// </summary>
    public static IReadOnlyCollection<string?> VisibleFolderIds(
        IReadOnlyList<MediaFolder> folders,
        string? currentFolderId,
        MediaLibraryViewMode mode)
    {
        var visible = new HashSet<string?> { currentFolderId };
        if (!IncludesDescendants(mode) || folders is null || folders.Count == 0)
            return visible;

        // Breadth-first down the tree. `seen` is not an optimisation: a hand-edited or round-tripped
        // manifest can contain a parent cycle, and without it this loop would not terminate.
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        var frontier = new Queue<string?>();
        frontier.Enqueue(currentFolderId);
        while (frontier.Count > 0)
        {
            var parent = frontier.Dequeue();
            foreach (var child in folders)
            {
                if (child.ParentFolderId != parent) continue;
                if (string.IsNullOrEmpty(child.Id) || !seen.Add(child.Id)) continue;
                visible.Add(child.Id);
                frontier.Enqueue(child.Id);
            }
        }

        return visible;
    }

    /// <summary>Is this item's folder in view?</summary>
    public static bool ItemIsVisible(
        string? itemFolderId,
        IReadOnlyCollection<string?> visibleFolderIds,
        MediaLibraryViewMode mode)
        => IgnoresFolders(mode) || visibleFolderIds.Contains(NormalizeFolderId(itemFolderId));

    /// <summary>An empty string in the manifest means "library root", the same as null.</summary>
    public static string? NormalizeFolderId(string? folderId)
        => string.IsNullOrEmpty(folderId) ? null : folderId;

    /// <summary>
    /// What the empty state is allowed to say. "No media yet" is reserved for a project that really
    /// has none — the defect this package exists to fix was that message shown over two assets.
    /// </summary>
    public static MediaLibraryEmptyReason EmptyReason(
        int projectMediaCount,
        int visibleItemCount,
        int visibleFolderCount)
    {
        if (visibleItemCount > 0 || visibleFolderCount > 0) return MediaLibraryEmptyReason.None;
        return projectMediaCount == 0
            ? MediaLibraryEmptyReason.ProjectHasNoMedia
            : MediaLibraryEmptyReason.NothingInThisFolder;
    }

    /// <summary>
    /// The panel's count label. It must describe everything on screen: a run that showed two folder
    /// tiles still read "0 items", which is the same class of untruth as the empty state.
    /// </summary>
    public static string CountLabel(int folderCount, int itemCount)
    {
        var items = itemCount == 1 ? "1 item" : $"{itemCount} items";
        if (folderCount <= 0) return items;
        var folders = folderCount == 1 ? "1 folder" : $"{folderCount} folders";
        return $"{folders} · {items}";
    }
}
