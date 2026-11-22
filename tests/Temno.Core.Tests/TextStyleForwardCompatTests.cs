using System.Text.Json;
using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public sealed class TextStyleForwardCompatTests
{
    [Fact]
    public void TextStyle_UnknownKeysRoundTripAtTheirOriginalNesting()
    {
        const string json = """
            {
              "fontName": "Helvetica-Bold",
              "fontSize": 96,
              "fontScale": 1,
              "isBold": true,
              "isItalic": false,
              "color": { "r": 1, "g": 1, "b": 1, "a": 1 },
              "alignment": "center",
              "shadow": {
                "enabled": true,
                "color": { "r": 0, "g": 0, "b": 0, "a": 0.6 },
                "offsetX": 0,
                "offsetY": -2,
                "blur": 6,
                "futureShadowOpacity": 0.42
              },
              "background": {
                "enabled": false,
                "color": { "r": 0, "g": 0, "b": 0, "a": 0.6 },
                "futurePadding": { "x": 12, "y": 7 }
              },
              "border": {
                "enabled": false,
                "color": { "r": 0, "g": 0, "b": 0, "a": 1 },
                "futureJoin": "round"
              },
              "futureTracking": 1.25
            }
            """;

        var style = JsonSerializer.Deserialize<TextStyle>(json, JsonOptions.ForProject);
        Assert.NotNull(style);
        Assert.Single(style!.ExtensionData!);
        Assert.Single(style.Shadow.ExtensionData!);
        Assert.Single(style.Background.ExtensionData!);
        Assert.Single(style.Border.ExtensionData!);
        Assert.Equal(1.25, style.ExtensionData!["futureTracking"].GetDouble());
        Assert.Equal(0.42, style.Shadow.ExtensionData!["futureShadowOpacity"].GetDouble());
        Assert.Equal(12, style.Background.ExtensionData!["futurePadding"].GetProperty("x").GetInt32());
        Assert.Equal("round", style.Border.ExtensionData!["futureJoin"].GetString());

        var encoded = JsonSerializer.SerializeToElement(style, JsonOptions.ForProject);
        Assert.Equal(1.25, encoded.GetProperty("futureTracking").GetDouble());
        Assert.Equal(
            0.42,
            encoded.GetProperty("shadow").GetProperty("futureShadowOpacity").GetDouble());
        Assert.Equal(
            7,
            encoded.GetProperty("background").GetProperty("futurePadding").GetProperty("y").GetInt32());
        Assert.Equal(
            "round",
            encoded.GetProperty("border").GetProperty("futureJoin").GetString());
        Assert.False(encoded.GetProperty("shadow").TryGetProperty("futureTracking", out _));
        Assert.False(encoded.GetProperty("background").TryGetProperty("futureTracking", out _));
    }

    [Fact]
    public void TextStyle_EditPathPatchesKnownColorAndPreservesUnknownKeys()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(
            TextClipCommands.AddTexts(
                session,
                [new TextClipSpec(null, 0, 30, "future style", new TextStyle(), null, null)],
                out var ids).Ok);

        var clip = FindClip(session, ids[0]);
        clip.TextStyle = JsonDocument.Parse(
            """
            {
              "fontName": "Future Sans",
              "fontSize": 72,
              "fontScale": 1,
              "isBold": false,
              "isItalic": false,
              "color": { "r": 1, "g": 1, "b": 1, "a": 1 },
              "alignment": "center",
              "shadow": {
                "enabled": true,
                "color": { "r": 0, "g": 0, "b": 0, "a": 0.6 },
                "offsetX": 0,
                "offsetY": -2,
                "blur": 6,
                "futureShadowOpacity": 0.5
              },
              "background": {
                "enabled": false,
                "color": { "r": 0, "g": 0, "b": 0, "a": 0.6 },
                "futurePadding": [8, 4]
              },
              "border": {
                "enabled": false,
                "color": { "r": 0, "g": 0, "b": 0, "a": 1 }
              },
              "futureTracking": 2.5
            }
            """).RootElement.Clone();

        var result = TextClipCommands.UpdateText(
            session,
            ids,
            null,
            new TextPatch
            {
                Style = new TextStylePatch
                {
                    Color = TextClipCommands.ParseHexColor("#123456"),
                },
            },
            out _,
            out _);

        Assert.True(result.Ok, result.Error);
        var patched = FindClip(session, ids[0]).TextStyle!.Value;
        Assert.Equal(0x12 / 255.0, patched.GetProperty("color").GetProperty("r").GetDouble(), 6);
        Assert.Equal(0x34 / 255.0, patched.GetProperty("color").GetProperty("g").GetDouble(), 6);
        Assert.Equal(0x56 / 255.0, patched.GetProperty("color").GetProperty("b").GetDouble(), 6);
        Assert.Equal(2.5, patched.GetProperty("futureTracking").GetDouble());
        Assert.Equal(
            0.5,
            patched.GetProperty("shadow").GetProperty("futureShadowOpacity").GetDouble());
        Assert.Equal(
            8,
            patched.GetProperty("background").GetProperty("futurePadding")[0].GetInt32());
    }

    [Fact]
    public void KnownTextStyleSerializationIsByteIdenticalAndHasNoExtensionProperty()
    {
        var style = new TextStyle();
        var before = JsonSerializer.SerializeToUtf8Bytes(style, JsonOptions.ForProject);
        var restored = JsonSerializer.Deserialize<TextStyle>(before, JsonOptions.ForProject);
        var after = JsonSerializer.SerializeToUtf8Bytes(restored, JsonOptions.ForProject);

        Assert.Equal(before, after);
        var encoded = JsonDocument.Parse(after).RootElement;
        Assert.False(encoded.TryGetProperty("ExtensionData", out _));
        Assert.False(encoded.TryGetProperty("extensionData", out _));
        Assert.Null(restored!.ExtensionData);
        Assert.Null(restored.Shadow.ExtensionData);
        Assert.Null(restored.Background.ExtensionData);
        Assert.Null(restored.Border.ExtensionData);
    }

    [Fact]
    public void TextAnimation_UnknownKeysRoundTripAndKnownSerializationIsByteIdentical()
    {
        const string json = """
            {
              "preset": "wordSlide",
              "perWordFrames": 8,
              "highlight": { "r": 1, "g": 0.85, "b": 0, "a": 1 },
              "futureEasing": { "name": "spring", "strength": 0.75 }
            }
            """;

        var animation = JsonSerializer.Deserialize<TextAnimation>(json, JsonOptions.ForProject);
        Assert.NotNull(animation);
        Assert.Equal(
            "spring",
            animation!.ExtensionData!["futureEasing"].GetProperty("name").GetString());

        var encoded = JsonSerializer.SerializeToElement(animation, JsonOptions.ForProject);
        Assert.Equal(
            0.75,
            encoded.GetProperty("futureEasing").GetProperty("strength").GetDouble());
        Assert.False(encoded.TryGetProperty("ExtensionData", out _));

        var known = new TextAnimation
        {
            Preset = "highlightPop",
            PerWordFrames = 4,
            Highlight = TextAnimation.DefaultHighlight,
        };
        var before = JsonSerializer.SerializeToUtf8Bytes(known, JsonOptions.ForProject);
        var restored = JsonSerializer.Deserialize<TextAnimation>(before, JsonOptions.ForProject);
        var after = JsonSerializer.SerializeToUtf8Bytes(restored, JsonOptions.ForProject);

        Assert.Equal(before, after);
        Assert.Null(restored!.ExtensionData);
    }

    [Fact]
    public void TextStylePreset_PreservesEmbeddedUnknownStyleKeys()
    {
        const string json = """
            {
              "id": "future-preset",
              "name": "Future Look",
              "style": {
                "fontName": "Future Sans",
                "fontSize": 84,
                "fontScale": 1,
                "isBold": true,
                "isItalic": false,
                "color": { "r": 1, "g": 1, "b": 1, "a": 1 },
                "alignment": "center",
                "shadow": {
                  "enabled": false,
                  "color": { "r": 0, "g": 0, "b": 0, "a": 0.6 },
                  "offsetX": 0,
                  "offsetY": -2,
                  "blur": 6
                },
                "background": {
                  "enabled": false,
                  "color": { "r": 0, "g": 0, "b": 0, "a": 0.6 }
                },
                "border": {
                  "enabled": false,
                  "color": { "r": 0, "g": 0, "b": 0, "a": 1 }
                },
                "futureTracking": 3
              }
            }
            """;

        var preset = JsonSerializer.Deserialize<TextStylePreset>(json, JsonOptions.ForProject);
        var encoded = JsonSerializer.SerializeToElement(preset, JsonOptions.ForProject);

        Assert.Equal(
            3,
            encoded.GetProperty("style").GetProperty("futureTracking").GetInt32());
    }

    [Fact]
    public void TextAnimation_EditPathPreservesUnknownKeys()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(
            TextClipCommands.AddTexts(
                session,
                [new TextClipSpec(null, 0, 30, "future animation", new TextStyle(), null, null)],
                out var ids).Ok);

        var clip = FindClip(session, ids[0]);
        clip.TextAnimation = JsonDocument.Parse(
            """
            {
              "preset": "wordReveal",
              "perWordFrames": 9,
              "futureEasing": "spring"
            }
            """).RootElement.Clone();

        var result = TextClipCommands.UpdateText(
            session,
            ids,
            null,
            new TextPatch
            {
                HighlightOnly = TextClipCommands.ParseHexColor("#FFD900"),
            },
            out _,
            out _);

        Assert.True(result.Ok, result.Error);
        var patched = FindClip(session, ids[0]).TextAnimation!.Value;
        Assert.Equal("wordReveal", patched.GetProperty("preset").GetString());
        Assert.Equal(9, patched.GetProperty("perWordFrames").GetInt32());
        Assert.Equal("spring", patched.GetProperty("futureEasing").GetString());
        Assert.Equal(1.0, patched.GetProperty("highlight").GetProperty("r").GetDouble(), 6);
    }

    private static Clip FindClip(ProjectSession session, string id)
        => session.ActiveTimeline.Tracks.SelectMany(track => track.Clips).Single(clip => clip.Id == id);
}
