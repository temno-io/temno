using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-185. At 1366 px the fixed side columns left the picture about 330 px wide. These pin the
/// column-fitting arithmetic and the timeline auto-height so a receipt can be re-derived.
/// </summary>
public sealed class ResponsiveShellTests
{
    [Fact]
    public void Wide_window_keeps_the_preferred_widths_and_reports_no_fit()
    {
        var (media, inspector, fitted) = ResponsiveShell.FitSideColumns(1900, 420, 300, 300, 220, 640);
        Assert.Equal(420, media);
        Assert.Equal(300, inspector);
        Assert.False(fitted);
    }

    [Fact]
    public void Laptop_window_takes_the_deficit_from_both_columns_in_proportion_to_their_slack()
    {
        // 1366 wide, agent collapsed: 1366 - 4 padding = 1362 available.
        // Needed: 420 + 300 + 10 + 640 = 1370 → deficit 8; slack 120 (media) + 80 (inspector) = 200.
        var (media, inspector, fitted) = ResponsiveShell.FitSideColumns(1362, 420, 300, 300, 220, 640);
        Assert.True(fitted);
        Assert.Equal(415, media);      // 420 - 8 * 120/200 = 415.2
        Assert.Equal(297, inspector);  // 300 - 8 * 80/200 = 296.8
        Assert.True(media + inspector + 10 + 640 <= 1362 + 1);
    }

    [Fact]
    public void Floors_are_respected_when_the_deficit_exceeds_the_slack()
    {
        var (media, inspector, fitted) = ResponsiveShell.FitSideColumns(900, 420, 300, 300, 220, 640);
        Assert.True(fitted);
        Assert.Equal(300, media);
        Assert.Equal(220, inspector);
    }

    [Fact]
    public void A_hidden_column_is_left_alone()
    {
        var (media, inspector, fitted) = ResponsiveShell.FitSideColumns(1000, 420, 0, 300, 220, 640);
        Assert.True(fitted);
        Assert.Equal(0, inspector);
        Assert.Equal(355, media); // 420 + 5 + 640 = 1065 → deficit 65 from media alone
    }

    [Fact]
    public void Timeline_height_fits_the_tracks_but_never_beyond_its_share_of_the_window()
    {
        // 6 tracks × 50 + 24 ruler = 324 lanes, 80 chrome → 404 wanted; 1080 × 0.42 = 453 cap.
        Assert.Equal(404, ResponsiveShell.FitTimelineHeight(324, 80, 1080, 220));
        // 768 × 0.42 = 323 cap.
        Assert.Equal(323, ResponsiveShell.FitTimelineHeight(324, 80, 768, 220));
        // Two tracks never shrink below the default.
        Assert.Equal(220, ResponsiveShell.FitTimelineHeight(124, 80, 1080, 220));
    }
}
