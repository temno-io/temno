using Temno.Core.Models;
using Temno.Core.Preview;
using Xunit;

namespace Temno.Core.Tests;

public sealed class PreviewTabControllerTests
{
    [Fact]
    public void StartsOnTimelineTab()
    {
        var tabs = new PreviewTabController("Seq A");
        Assert.Single(tabs.Tabs);
        Assert.Equal(PreviewMonitorTab.TimelineId, tabs.ActiveId);
        Assert.True(tabs.IsTimeline);
        Assert.Equal("Seq A", tabs.Active.Title);
        Assert.False(tabs.CanGoBack);
        Assert.False(tabs.CanGoForward);
    }

    [Fact]
    public void OpenMedia_AddsSourceTabAndActivates()
    {
        var tabs = new PreviewTabController("Timeline 1");
        var tab = tabs.OpenMedia("m1", "Clip.mp4", ClipType.video);
        Assert.Equal(2, tabs.Tabs.Count);
        Assert.False(tabs.IsTimeline);
        Assert.Equal("m1", tabs.Active.MediaRef);
        Assert.Equal(tab.Id, tabs.ActiveId);
        Assert.True(tabs.CanGoBack);
    }

    [Fact]
    public void OpenMedia_SameAsset_DoesNotDuplicate()
    {
        var tabs = new PreviewTabController();
        tabs.OpenMedia("m1", "A", ClipType.video);
        tabs.OpenMedia("m1", "A renamed", ClipType.video);
        Assert.Equal(2, tabs.Tabs.Count);
        Assert.Equal("A renamed", tabs.Active.Title);
    }

    [Fact]
    public void Close_ReturnsToHistoryFallback()
    {
        var tabs = new PreviewTabController();
        tabs.OpenMedia("m1", "A", ClipType.video);
        tabs.OpenMedia("m2", "B", ClipType.audio);
        Assert.True(tabs.Close(PreviewMonitorTab.MediaTabId("m2")));
        Assert.Equal(PreviewMonitorTab.MediaTabId("m1"), tabs.ActiveId);
        Assert.False(tabs.Close(PreviewMonitorTab.TimelineId));
    }

    [Fact]
    public void BackAndForward_WalkHistory()
    {
        var tabs = new PreviewTabController();
        tabs.OpenMedia("m1", "A", ClipType.video);
        Assert.True(tabs.Back());
        Assert.True(tabs.IsTimeline);
        Assert.True(tabs.Forward());
        Assert.Equal("m1", tabs.Active.MediaRef);
    }

    [Fact]
    public void CloseAllMedia_LeavesTimeline()
    {
        var tabs = new PreviewTabController("Main");
        tabs.OpenMedia("m1", "A", ClipType.video);
        tabs.OpenMedia("m2", "B", ClipType.image);
        tabs.CloseAllMedia();
        Assert.Single(tabs.Tabs);
        Assert.True(tabs.IsTimeline);
        Assert.Equal("Main", tabs.Active.Title);
        Assert.False(tabs.HasSourceTabs);
    }

    [Fact]
    public void HasSourceTabs_TrueOnlyWithMediaTabs()
    {
        var tabs = new PreviewTabController();
        Assert.False(tabs.HasSourceTabs);
        tabs.OpenMedia("m1", "A", ClipType.video);
        Assert.True(tabs.HasSourceTabs);
        tabs.CloseAllMedia();
        Assert.False(tabs.HasSourceTabs);
    }

    [Fact]
    public void Select_NoOpWhenAlreadyActive()
    {
        var tabs = new PreviewTabController();
        tabs.OpenMedia("m1", "A", ClipType.video);
        Assert.False(tabs.Select(tabs.ActiveId));
    }

    [Fact]
    public void PruneMissing_ClosesDeletedSourceAndFallsBack()
    {
        var tabs = new PreviewTabController();
        tabs.OpenMedia("keep", "Keep", ClipType.video);
        tabs.OpenMedia("gone", "Gone", ClipType.audio);
        Assert.True(tabs.PruneMissing(new HashSet<string> { "keep" }));
        Assert.Equal(PreviewMonitorTab.MediaTabId("keep"), tabs.ActiveId);
        Assert.Equal(2, tabs.Tabs.Count);
        Assert.False(tabs.ContainsMedia("gone"));
    }
}
