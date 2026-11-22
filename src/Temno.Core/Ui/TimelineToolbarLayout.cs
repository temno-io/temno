namespace Temno.Core.Ui;

/// <summary>
/// Computed responsive layout for the timeline toolbar (tab strip + editing tools + zoom cluster).
/// Keeps tabs expanding naturally to push tools right when room allows, then progressively hides
/// non-essential readouts, collapses secondary tools into the overflow menu on smaller screens,
/// and enables horizontal tab scrolling with an "All timelines" picker before anything clips.
/// </summary>
public readonly record struct TimelineToolbarMetrics(
    double MaxTabsScrollWidth,
    bool OverflowSecondaryTools,
    bool HideZoomReadout,
    bool CompactZoomSlider,
    bool ShowAllTimelinesButton,
    bool HideZoomControls = false);

public static class TimelineToolbarLayout
{
    public const double TabChromeWidth = 62;           // tab host padding (16) + '+' button (29) + divider (9) + gaps (8)
    public const double AllTimelinesButtonWidth = 33;  // chevron picker button (29) + gap (4)
    public const double CoreToolsWidth = 234;          // undo/redo + divider + select/trim/blade/split (213) + host padding (16) + gap (5)
    public const double SecondaryToolsWidth = 324;     // divider + ripple/overwrite/snap/fit + divider + add-text (322 measured + 2)
    public const double OverflowButtonWidth = 35;      // '...' overflow button (32) + gap (2) + 1px buffer
    public const double ZoomSliderFullWidth = 100;
    public const double ZoomSliderCompactWidth = 68;
    public const double ZoomBaseFullWidth = 120;       // search icon (12) + spacing (6) + 100px slider + 2px buffer
    public const double ZoomBaseCompactWidth = 88;     // search icon (12) + spacing (6) + 68px slider + 2px buffer
    public const double ZoomReadoutWidth = 80;         // spacing (6) + "12:30 in view" (74)
    public const double MinTabsScrollWidth = 120;      // minimum viewport so active tab + close stays usable
    public const double ComfortableTwoTabWidth = 216;  // room for two typical tabs with close buttons

    public static TimelineToolbarMetrics Compute(
        double toolbarWidth,
        double desiredTabsWidth,
        int openTabCount,
        int totalTimelineCount)
    {
        var width = double.IsFinite(toolbarWidth) ? Math.Max(0, toolbarWidth) : 0;
        var desiredTabs = double.IsFinite(desiredTabsWidth)
            ? Math.Max(MinTabsScrollWidth, Math.Ceiling(desiredTabsWidth))
            : MinTabsScrollWidth;
        var hasClosedTimelines = totalTimelineCount > Math.Max(0, openTabCount);

        static double Chrome(bool showMenu)
            => TabChromeWidth + (showMenu ? AllTimelinesButtonWidth : 0);

        const double fullToolsWidth = CoreToolsWidth + SecondaryToolsWidth;
        const double collapsedToolsWidth = CoreToolsWidth + OverflowButtonWidth;

        // Stage 0: Everything fits naturally — tabs expand and push tools to the right.
        if (Chrome(hasClosedTimelines) + desiredTabs + fullToolsWidth + ZoomBaseFullWidth + ZoomReadoutWidth <= width)
        {
            return new TimelineToolbarMetrics(
                MaxTabsScrollWidth: desiredTabs,
                OverflowSecondaryTools: false,
                HideZoomReadout: false,
                CompactZoomSlider: false,
                ShowAllTimelinesButton: hasClosedTimelines);
        }

        // Stage 1: Hide the zoom text readout first so full tools and all tabs remain visible.
        if (Chrome(hasClosedTimelines) + desiredTabs + fullToolsWidth + ZoomBaseFullWidth <= width)
        {
            return new TimelineToolbarMetrics(
                MaxTabsScrollWidth: desiredTabs,
                OverflowSecondaryTools: false,
                HideZoomReadout: true,
                CompactZoomSlider: false,
                ShowAllTimelinesButton: hasClosedTimelines);
        }

        // Stage 2: On wider windows with many open tabs, keep full tools intact and cap/scroll the
        // tab strip once it has at least a comfortable multi-tab viewport.
        var roomWithFullTools = width - (fullToolsWidth + ZoomBaseFullWidth + Chrome(showMenu: true));
        var collapseFloor = openTabCount >= 2 ? ComfortableTwoTabWidth : MinTabsScrollWidth + 32;
        if (roomWithFullTools >= collapseFloor)
        {
            return new TimelineToolbarMetrics(
                MaxTabsScrollWidth: Math.Floor(roomWithFullTools),
                OverflowSecondaryTools: false,
                HideZoomReadout: true,
                CompactZoomSlider: false,
                ShowAllTimelinesButton: true);
        }

        // Stage 3: Smaller screens — collapse secondary tools into the overflow menu so tabs stay unclipped.
        var neededCollapsed = Chrome(hasClosedTimelines) + desiredTabs + collapsedToolsWidth + ZoomBaseFullWidth;
        if (neededCollapsed <= width)
        {
            var hideReadout = neededCollapsed + ZoomReadoutWidth > width;
            return new TimelineToolbarMetrics(
                MaxTabsScrollWidth: desiredTabs,
                OverflowSecondaryTools: true,
                HideZoomReadout: hideReadout,
                CompactZoomSlider: false,
                ShowAllTimelinesButton: hasClosedTimelines);
        }

        // Stage 4: Narrow screen AND many tabs — collapse secondary tools, compact zoom slider if
        // tight, scroll the tab strip, and surface the "All timelines" picker.
        var roomCollapsedFullZoom = width - (collapsedToolsWidth + ZoomBaseFullWidth + Chrome(showMenu: true));
        var compactSlider = roomCollapsedFullZoom < ComfortableTwoTabWidth;
        var zoomWidth = compactSlider ? ZoomBaseCompactWidth : ZoomBaseFullWidth;
        var availableTabs = Math.Floor(width - (collapsedToolsWidth + zoomWidth + Chrome(showMenu: true)));

        // Stage 5: Ultra-narrow toolbar where keeping the zoom slider would squeeze tabs below
        // MinTabsScrollWidth or clip controls off the right edge — move zoom into overflow menu.
        if (availableTabs < MinTabsScrollWidth)
        {
            var roomNoZoom = Math.Floor(width - (collapsedToolsWidth + Chrome(showMenu: true)));
            var ultraScroll = Math.Min(desiredTabs, Math.Max(MinTabsScrollWidth, roomNoZoom));
            return new TimelineToolbarMetrics(
                MaxTabsScrollWidth: Math.Max(MinTabsScrollWidth, ultraScroll),
                OverflowSecondaryTools: true,
                HideZoomReadout: true,
                CompactZoomSlider: true,
                ShowAllTimelinesButton: true,
                HideZoomControls: true);
        }

        return new TimelineToolbarMetrics(
            MaxTabsScrollWidth: Math.Min(desiredTabs, availableTabs),
            OverflowSecondaryTools: true,
            HideZoomReadout: true,
            CompactZoomSlider: compactSlider,
            ShowAllTimelinesButton: true,
            HideZoomControls: false);
    }

    /// <summary>Fallback estimate for tab strip width before WinUI measures realized item containers.</summary>
    public static double EstimateTabsWidth(IEnumerable<string> tabNames, bool showClose)
    {
        var total = 0.0;
        var count = 0;
        foreach (var name in tabNames)
        {
            var chars = string.IsNullOrEmpty(name) ? 8 : name.Length;
            var labelW = Math.Min(156, Math.Max(48, chars * 7.5 + 8));
            var tabW = labelW + (showClose ? 22 : 0) + 18;
            total += tabW;
            count++;
        }

        if (count > 1)
            total += (count - 1) * 4;
        return Math.Max(MinTabsScrollWidth, total);
    }
}
