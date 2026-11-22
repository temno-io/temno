using System.Collections.Generic;
using System.Linq;
using Temno.Core.Models;
using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-158. The owner's report: "the media that the agent added did not appear in the media library."
/// Both assets existed, both were on the timeline, and both were filed into folders the panel's List
/// mode would not show — while the panel said "No media yet".
/// </summary>
public class MediaLibraryScopeTests
{
    // The owner's shape: source/ at the root, overlays/ at the root, provisional/ inside overlays.
    private static List<MediaFolder> OwnerFolders() =>
    [
        new() { Id = "source", Name = "source" },
        new() { Id = "overlays", Name = "overlays" },
        new() { Id = "provisional", Name = "provisional", ParentFolderId = "overlays" },
    ];

    private static readonly (string Id, string? FolderId)[] OwnerItems =
    [
        ("VOD", "source"),
        ("captions", "provisional"),
    ];

    private static int VisibleCount(MediaLibraryViewMode mode, string? currentFolder)
    {
        var ids = MediaLibraryScope.VisibleFolderIds(OwnerFolders(), currentFolder, mode);
        return OwnerItems.Count(i => MediaLibraryScope.ItemIsVisible(i.FolderId, ids, mode));
    }

    [Theory]
    [InlineData(MediaLibraryViewMode.Folders)]
    [InlineData(MediaLibraryViewMode.List)]
    [InlineData(MediaLibraryViewMode.Flat)]
    [InlineData(MediaLibraryViewMode.Grouped)]
    public void EveryMode_ReachesTheOwnersMedia_FromTheLibraryRoot(MediaLibraryViewMode mode)
    {
        var folders = OwnerFolders();
        var ids = MediaLibraryScope.VisibleFolderIds(folders, null, mode);
        var visibleItems = OwnerItems.Count(i => MediaLibraryScope.ItemIsVisible(i.FolderId, ids, mode));
        var visibleTiles = MediaLibraryScope.ShowsFolderTiles(mode)
            ? folders.Count(f => f.ParentFolderId is null)
            : 0;

        // The invariant: no mode may leave the root with nothing on screen and no way down.
        Assert.True(
            visibleItems > 0 || visibleTiles > 0,
            $"{mode} shows neither media nor a folder tile at the library root");
    }

    [Fact]
    public void ListMode_AtTheRoot_ShowsEverythingIncludingNestedFolders()
    {
        // The reported defect: this returned 0 and the panel said "No media yet".
        Assert.Equal(2, VisibleCount(MediaLibraryViewMode.List, currentFolder: null));
    }

    [Fact]
    public void ListMode_InsideAFolder_ShowsThatSubtreeOnly()
    {
        Assert.Equal(1, VisibleCount(MediaLibraryViewMode.List, currentFolder: "overlays"));
        Assert.Equal(1, VisibleCount(MediaLibraryViewMode.List, currentFolder: "source"));
        Assert.Equal(1, VisibleCount(MediaLibraryViewMode.List, currentFolder: "provisional"));
    }

    [Fact]
    public void FoldersMode_ShowsDirectChildrenOnly_AndKeepsItsTiles()
    {
        // Folders keeps its documented behaviour: the root holds no loose media, and that is fine
        // because the tiles are the way down.
        Assert.Equal(0, VisibleCount(MediaLibraryViewMode.Folders, currentFolder: null));
        Assert.Equal(0, VisibleCount(MediaLibraryViewMode.Folders, currentFolder: "overlays"));
        Assert.Equal(1, VisibleCount(MediaLibraryViewMode.Folders, currentFolder: "provisional"));
        Assert.True(MediaLibraryScope.ShowsFolderTiles(MediaLibraryViewMode.Folders));
        Assert.False(MediaLibraryScope.ShowsFolderTiles(MediaLibraryViewMode.List));
    }

    [Fact]
    public void FlatAndGrouped_IgnoreTheCurrentFolderEntirely()
    {
        foreach (var mode in new[] { MediaLibraryViewMode.Flat, MediaLibraryViewMode.Grouped })
        {
            Assert.Equal(2, VisibleCount(mode, currentFolder: null));
            Assert.Equal(2, VisibleCount(mode, currentFolder: "provisional"));
        }
    }

    [Fact]
    public void AnEmptyFolderIdMeansTheLibraryRoot()
    {
        var ids = MediaLibraryScope.VisibleFolderIds(OwnerFolders(), null, MediaLibraryViewMode.Folders);
        Assert.True(MediaLibraryScope.ItemIsVisible("", ids, MediaLibraryViewMode.Folders));
        Assert.True(MediaLibraryScope.ItemIsVisible(null, ids, MediaLibraryViewMode.Folders));
    }

    [Fact]
    public void ACyclicFolderTreeTerminates()
    {
        // A hand-edited or round-tripped manifest can point a parent at its own descendant. Without
        // the visited set the descendant walk would not return.
        List<MediaFolder> cyclic =
        [
            new() { Id = "a", Name = "a", ParentFolderId = "b" },
            new() { Id = "b", Name = "b", ParentFolderId = "a" },
        ];

        var ids = MediaLibraryScope.VisibleFolderIds(cyclic, "a", MediaLibraryViewMode.List);

        Assert.Contains("a", ids);
        Assert.Contains("b", ids);
    }

    [Fact]
    public void EmptyReason_SaysNoMediaYet_OnlyWhenTheProjectReallyHasNone()
    {
        Assert.Equal(
            MediaLibraryEmptyReason.ProjectHasNoMedia,
            MediaLibraryScope.EmptyReason(projectMediaCount: 0, visibleItemCount: 0, visibleFolderCount: 0));
    }

    [Fact]
    public void EmptyReason_WithMediaOutOfView_OffersTheWayBackInstead()
    {
        // This is the owner's exact situation, and the message must not be "No media yet".
        Assert.Equal(
            MediaLibraryEmptyReason.NothingInThisFolder,
            MediaLibraryScope.EmptyReason(projectMediaCount: 2, visibleItemCount: 0, visibleFolderCount: 0));
    }

    [Fact]
    public void EmptyReason_IsNone_WhenAnythingIsOnScreen()
    {
        Assert.Equal(
            MediaLibraryEmptyReason.None,
            MediaLibraryScope.EmptyReason(projectMediaCount: 2, visibleItemCount: 1, visibleFolderCount: 0));
        // Folder tiles count as content: two of them on screen under "No media yet" was the old bug.
        Assert.Equal(
            MediaLibraryEmptyReason.None,
            MediaLibraryScope.EmptyReason(projectMediaCount: 2, visibleItemCount: 0, visibleFolderCount: 2));
    }

    [Theory]
    [InlineData(0, 0, "0 items")]
    [InlineData(0, 1, "1 item")]
    [InlineData(0, 7, "7 items")]
    [InlineData(1, 0, "1 folder · 0 items")]
    [InlineData(2, 0, "2 folders · 0 items")]
    [InlineData(2, 1, "2 folders · 1 item")]
    public void CountLabel_DescribesEverythingOnScreen(int folders, int items, string expected)
        => Assert.Equal(expected, MediaLibraryScope.CountLabel(folders, items));
}
