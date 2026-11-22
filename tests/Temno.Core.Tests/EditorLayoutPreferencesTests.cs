using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public class EditorLayoutPreferencesTests
{
    [Fact]
    public void ForPreset_Default_Media_Vertical_AreDistinct()
    {
        var d = EditorLayoutPreferences.ForPreset(EditorLayoutPreset.Default);
        var m = EditorLayoutPreferences.ForPreset(EditorLayoutPreset.Media);
        var v = EditorLayoutPreferences.ForPreset(EditorLayoutPreset.Vertical);
        var p = EditorLayoutPreferences.ForPreset(EditorLayoutPreset.Preview);

        Assert.Equal("default", d.Preset);
        Assert.False(d.AgentVisible); // Mac empty-project silhouette: agent collapsed
        Assert.True(d.MediaVisible);

        Assert.Equal("media", m.Preset);
        Assert.False(m.AgentVisible);
        Assert.True(m.MediaVisible);
        Assert.True(m.MediaWidth >= d.MediaWidth);

        Assert.Equal("vertical", v.Preset);
        Assert.False(v.AgentVisible);
        Assert.True(v.MediaVisible);
        Assert.True(v.InspectorVisible);
        Assert.True(v.InspectorWidth >= EditorLayoutPreferences.DefaultInspectorWidth);
        Assert.True(v.TimelineHeight > d.TimelineHeight);

        Assert.Equal("preview", p.Preset);
        Assert.False(p.MediaVisible);
        Assert.False(p.InspectorVisible);
        // Preview must not be an alias of Vertical.
        Assert.NotEqual(v.Preset, p.Preset);
        Assert.NotEqual(v.MediaVisible, p.MediaVisible);
    }

    [Fact]
    public void Clamp_ReplacesCollapsedAndOutOfRange()
    {
        var raw = new EditorLayoutPreferences
        {
            AgentWidth = 0,
            MediaWidth = 1, // star-like
            InspectorWidth = 9000,
            TimelineHeight = double.NaN,
            Preset = "VERTICAL",
        };
        var c = EditorLayoutPreferences.Clamp(raw);
        Assert.Equal(EditorLayoutPreferences.DefaultAgentWidth, c.AgentWidth);
        Assert.Equal(EditorLayoutPreferences.DefaultMediaWidth, c.MediaWidth);
        Assert.Equal(EditorLayoutPreferences.MaxInspectorWidth, c.InspectorWidth);
        Assert.Equal(EditorLayoutPreferences.DefaultTimelineHeight, c.TimelineHeight);
        Assert.Equal("vertical", c.Preset);
    }

    [Fact]
    public void MergeLiveSizes_IgnoresCollapsedAndStarValues()
    {
        var basePrefs = EditorLayoutPreferences.ForPreset(EditorLayoutPreset.Default);
        basePrefs.AgentWidth = 300;
        basePrefs.MediaWidth = 350;

        var merged = EditorLayoutPreferences.MergeLiveSizes(
            basePrefs,
            agentWidth: 0,
            mediaWidth: 1,
            inspectorWidth: 200,
            timelineHeight: 240,
            agentVisible: false);

        Assert.Equal(300, merged.AgentWidth); // not overwritten by 0
        Assert.Equal(350, merged.MediaWidth); // not overwritten by 1
        Assert.Equal(200, merged.InspectorWidth);
        Assert.Equal(240, merged.TimelineHeight);
        Assert.False(merged.AgentVisible);
    }

    [Fact]
    public void RoundTrip_Json_PreservesValues()
    {
        var original = EditorLayoutPreferences.Clamp(new EditorLayoutPreferences
        {
            Preset = "media",
            AgentWidth = 300,
            MediaWidth = 410,
            InspectorWidth = 255,
            TimelineHeight = 275,
            AgentVisible = false,
            MediaVisible = true,
            InspectorVisible = true,
        });
        var json = original.ToJson();
        var restored = EditorLayoutPreferences.FromJson(json);
        Assert.Equal(original.Preset, restored.Preset);
        Assert.Equal(original.AgentWidth, restored.AgentWidth);
        Assert.Equal(original.MediaWidth, restored.MediaWidth);
        Assert.Equal(original.InspectorWidth, restored.InspectorWidth);
        Assert.Equal(original.TimelineHeight, restored.TimelineHeight);
        Assert.Equal(original.AgentVisible, restored.AgentVisible);
        Assert.Equal(original.MediaVisible, restored.MediaVisible);
        Assert.Equal(original.InspectorVisible, restored.InspectorVisible);
    }

    [Fact]
    public void FromJson_CorruptOrEmpty_ReturnsDefault()
    {
        var empty = EditorLayoutPreferences.FromJson("");
        Assert.Equal("default", empty.Preset);
        Assert.False(empty.AgentVisible);
        Assert.True(empty.MediaVisible);

        var bad = EditorLayoutPreferences.FromJson("{not json");
        Assert.Equal("default", bad.Preset);
    }
}
