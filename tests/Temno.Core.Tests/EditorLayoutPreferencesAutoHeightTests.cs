using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-185. The timeline auto-height flag has to survive the clamp, the live-size merge and the
/// JSON round trip, and default to on for layouts written before the flag existed.
/// </summary>
public sealed class EditorLayoutPreferencesAutoHeightTests
{
    [Fact]
    public void Defaults_to_auto_including_for_older_layout_files_without_the_field()
    {
        Assert.True(new EditorLayoutPreferences().TimelineHeightAuto);
        var legacy = EditorLayoutPreferences.FromJson("{\"version\":1,\"preset\":\"default\",\"timelineHeight\":240}");
        Assert.True(legacy.TimelineHeightAuto);
    }

    [Fact]
    public void A_dragged_divider_turns_auto_off_and_the_flag_survives_clamp_merge_and_json()
    {
        var prefs = new EditorLayoutPreferences { TimelineHeightAuto = false, TimelineHeight = 333 };
        var clamped = EditorLayoutPreferences.Clamp(prefs);
        Assert.False(clamped.TimelineHeightAuto);
        var merged = EditorLayoutPreferences.MergeLiveSizes(clamped, null, null, null, 400);
        Assert.False(merged.TimelineHeightAuto);
        Assert.Equal(400, merged.TimelineHeight);
        var round = EditorLayoutPreferences.FromJson(merged.ToJson());
        Assert.False(round.TimelineHeightAuto);
    }
}
