using Temno.Core.Captions;
using Temno.Core.Models;
using Temno.Mcp;
using Temno.Media.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Abstractions;

namespace Temno.Core.Tests;

/// <summary>
/// WP-125: gallery composition and selection. WP-143: 4-per-row layout math.
/// WinUI-free — the view-model is <see cref="TextAnimation"/> plus
/// <see cref="CaptionPresetGalleryLayout"/>.
/// </summary>
public sealed class CaptionPresetGalleryTests
{
    private readonly ITestOutputHelper _output;

    public CaptionPresetGalleryTests(ITestOutputHelper output) => _output = output;

    private const double Gap = 6; // AppTheme.Spacing.Sm
    private const int Four = 4;

    [Fact]
    public void DefaultCaptionsWidthNoLongerWrapsThreeAndOne()
    {
        // MediaCol 420 − rail 38 − Padding.Lg 14×2 ≈ 352. The /90 formula yields 3.
        const double defaultGalleryWidth = 352;
        Assert.Equal(3, CaptionPresetGalleryLayout.LegacyColumns(defaultGalleryWidth, Four));
        var layout = CaptionPresetGalleryLayout.ForWidth(defaultGalleryWidth, Four, Gap);
        Assert.Equal(4, layout.Columns);
    }

    [Theory]
    [InlineData(320)]
    [InlineData(352)]
    [InlineData(360)]
    [InlineData(560)]
    public void FourCellsStayOnOneRowInTheAuditWindow(double width)
    {
        var layout = CaptionPresetGalleryLayout.ForWidth(width, Four, Gap);
        Assert.Equal(4, layout.Columns);
        var span = 4 * layout.CellWidth + 3 * Gap;
        Assert.True(span <= width + 0.01, $"span {span} exceeds width {width}");
        Assert.True(layout.CellWidth <= CaptionPresetGalleryLayout.PreferredCellWidth + 0.01);
        Assert.True(layout.CellHeight <= CaptionPresetGalleryLayout.PreferredCellHeight + 0.01);
        Assert.Equal(
            CaptionPresetGalleryLayout.PreferredCellHeight / CaptionPresetGalleryLayout.PreferredCellWidth,
            layout.CellHeight / layout.CellWidth,
            6);
    }

    [Fact]
    public void BelowTheAuditWindowWrappingIsAllowed()
    {
        var layout = CaptionPresetGalleryLayout.ForWidth(200, Four, Gap);
        Assert.True(layout.Columns < 4);
        Assert.Equal(CaptionPresetGalleryLayout.PreferredCellWidth, layout.CellWidth);
    }

    [Fact]
    public void WidePanelDoesNotGrowCellsPastPreferred()
    {
        var layout = CaptionPresetGalleryLayout.ForWidth(560, Four, Gap);
        Assert.Equal(4, layout.Columns);
        Assert.Equal(CaptionPresetGalleryLayout.PreferredCellWidth, layout.CellWidth);
        Assert.Equal(CaptionPresetGalleryLayout.PreferredCellHeight, layout.CellHeight);
    }

    [Fact]
    public void GalleryHasExactlyTheEightScreenshotCellsInOrder()
    {
        Assert.Equal(["none", "popIn", "slideUp", "typewriter"], TextAnimation.GalleryPerLine);
        Assert.Equal(
            ["wordReveal", "wordSlide", "highlightPop", "highlightBlock"],
            TextAnimation.GalleryPerWord);

        var cells = TextAnimation.GalleryPerLine.Concat(TextAnimation.GalleryPerWord).ToArray();
        Assert.Equal(8, cells.Length);
        Assert.Equal(
            ["Off", "Pop In", "Slide Up", "Typewriter",
             "Word Reveal", "Word Slide", "Highlight", "Highlight Block"],
            cells.Select(TextAnimation.DisplayName).ToArray());
    }

    [Fact]
    public void GalleryListsAreNotAHandCopyOfTheJsonVocabulary()
    {
        // The full vocabulary stays on PerLine / PerWord / Presets for agents and stored projects.
        Assert.Contains("fadeIn", TextAnimation.PerLine);
        Assert.Contains("wordPop", TextAnimation.PerWord);
        Assert.Contains("wordCycle", TextAnimation.PerWord);
        Assert.Equal(10, TextAnimation.Presets.Length);

        Assert.DoesNotContain("fadeIn", TextAnimation.GalleryPerLine);
        Assert.DoesNotContain("wordPop", TextAnimation.GalleryPerWord);
        Assert.DoesNotContain("wordCycle", TextAnimation.GalleryPerWord);
        Assert.DoesNotContain("fadeIn", TextAnimation.GalleryPerWord);
    }

    [Theory]
    [InlineData("fadeIn")]
    [InlineData("wordPop")]
    [InlineData("wordCycle")]
    public void HiddenPresetsRemainAgentValuesButAreNotGalleryCells(string preset)
    {
        Assert.Contains(preset, TextAnimation.AgentValues);
        Assert.Contains(preset, TextAnimation.Presets);
        Assert.False(TextAnimation.IsGalleryPreset(preset));
        Assert.Null(TextAnimation.GallerySelection(preset));
    }

    [Fact]
    public void AStoredNonGalleryPresetIsNotCoercedToOff()
    {
        foreach (var hidden in new[] { "fadeIn", "wordPop", "wordCycle" })
        {
            var anim = new TextAnimation { Preset = hidden, PerWordFrames = 6 };
            var json = JsonSerializer.Serialize(anim, JsonOptions.ForProject);
            var loaded = JsonSerializer.Deserialize<TextAnimation>(json, JsonOptions.ForProject);

            Assert.Equal(hidden, loaded!.Preset);
            Assert.Null(TextAnimation.GallerySelection(loaded.Preset));
            // Opening the Captions tab must not invent a gallery selection of Off for these.
            Assert.NotEqual("none", loaded.Preset);
        }
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData("", "none")]
    [InlineData("off", "none")]
    [InlineData("none", "none")]
    [InlineData("popIn", "popIn")]
    public void CanonicalPresetMapsOffAliases(string? raw, string expected)
        => Assert.Equal(expected, TextAnimation.CanonicalPreset(raw));

    [Theory]
    [InlineData("none", "none")]
    [InlineData("off", "none")]
    [InlineData("popIn", "popIn")]
    [InlineData("highlightBlock", "highlightBlock")]
    public void GallerySelectionHighlightsAVisibleCell(string stored, string cell)
        => Assert.Equal(cell, TextAnimation.GallerySelection(stored));

    [Fact]
    public void RestingCellsDoNotAnimate()
    {
        Assert.False(TextAnimation.GalleryCellAnimates("popIn", live: true, selected: false, pointerOver: false, focused: false));
        Assert.False(TextAnimation.GalleryCellAnimates("popIn", live: false, selected: true, pointerOver: true, focused: true));
        Assert.False(TextAnimation.GalleryCellAnimates("none", live: true, selected: true, pointerOver: true, focused: true));
        Assert.False(TextAnimation.GalleryCellAnimates("off", live: true, selected: true, pointerOver: false, focused: false));
    }

    [Fact]
    public void HoverSelectedOrFocusedCellsAnimateWhenLive()
    {
        Assert.True(TextAnimation.GalleryCellAnimates("popIn", live: true, selected: true, pointerOver: false, focused: false));
        Assert.True(TextAnimation.GalleryCellAnimates("wordReveal", live: true, selected: false, pointerOver: true, focused: false));
        Assert.True(TextAnimation.GalleryCellAnimates("highlightPop", live: true, selected: false, pointerOver: false, focused: true));
    }

    [Fact]
    public void PreviewAnimationCarriesHighlightOnlyOnPerWordCells()
    {
        var red = new TextStyle.RGBA { R = 1, G = 0, B = 0, A = 1 };

        Assert.Null(TextAnimation.GalleryPreviewAnimation("none", red));
        Assert.Null(TextAnimation.GalleryPreviewAnimation("off", red));

        var pop = TextAnimation.GalleryPreviewAnimation("popIn", red);
        Assert.NotNull(pop);
        Assert.Equal("popIn", pop!.Preset);
        Assert.Null(pop.Highlight);

        var highlight = TextAnimation.GalleryPreviewAnimation("highlightPop", red);
        Assert.NotNull(highlight);
        Assert.Equal("highlightPop", highlight!.Preset);
        Assert.NotNull(highlight.Highlight);
        Assert.Equal(1, highlight.Highlight!.R);
        Assert.Equal(0, highlight.Highlight.G);

        var fallback = TextAnimation.GalleryPreviewAnimation("highlightBlock", null);
        Assert.NotNull(fallback!.Highlight);
        Assert.Equal(TextAnimation.DefaultHighlight.R, fallback.Highlight!.R, 3);
        Assert.Equal(TextAnimation.DefaultHighlight.G, fallback.Highlight.G, 3);
    }

    [Fact]
    public void ChangingHighlightRebuildsOnlyThePreviewPayloadForPerWordCells()
    {
        var yellow = TextAnimation.DefaultHighlight;
        var green = new TextStyle.RGBA { R = 0, G = 1, B = 0, A = 1 };

        var before = TextAnimation.GalleryPreviewAnimation("wordSlide", yellow);
        var after = TextAnimation.GalleryPreviewAnimation("wordSlide", green);
        Assert.Equal(yellow.G, before!.Highlight!.G, 3);
        Assert.Equal(1, after!.Highlight!.G, 3);

        var lineBefore = TextAnimation.GalleryPreviewAnimation("slideUp", yellow);
        var lineAfter = TextAnimation.GalleryPreviewAnimation("slideUp", green);
        Assert.Null(lineBefore!.Highlight);
        Assert.Null(lineAfter!.Highlight);
    }

    [Fact]
    public void AddCaptionsStillAdvertisesHiddenPresets()
    {
        var schema = McpToolRegistry.Tools.Single(t => t.Name == "add_captions").InputSchema;
        var animation = (JsonObject)((JsonObject)schema["properties"]!)["animation"]!;
        var values = animation["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("fadeIn", values);
        Assert.Contains("wordPop", values);
        Assert.Contains("wordCycle", values);
    }

    [Fact]
    public async Task EveryGalleryCellRendersAPoster()
    {
        var style = new TextStyle
        {
            FontSize = 300,
            Color = new TextStyle.RGBA { R = 1, G = 1, B = 1, A = 1 },
            Shadow = { Enabled = false },
        };

        foreach (var preset in TextAnimation.GalleryPerLine.Concat(TextAnimation.GalleryPerWord))
        {
            var clip = CaptionPreview.BuildClip(
                CaptionPreview.GallerySample,
                style,
                CaptionPreview.GalleryTransform(),
                TextAnimation.GalleryPreviewAnimation(preset, TextAnimation.DefaultHighlight));
            var poster = CaptionPreview.LoopFrames(preset) - 1;
            using var frame = await CaptionPreview.RenderAsync(clip, poster, 84, 48);
            Assert.NotNull(frame);
            Assert.Equal(84, frame!.Width);
            Assert.Equal(48, frame.Height);
        }
    }

    [Fact]
    public async Task OneAnimatingCellCostsFarLessThanAnimatingTheWholeGrid()
    {
        var style = new TextStyle
        {
            FontSize = 300,
            Color = new TextStyle.RGBA { R = 1, G = 1, B = 1, A = 1 },
            Shadow = { Enabled = false },
        };

        var presets = TextAnimation.GalleryPerLine.Concat(TextAnimation.GalleryPerWord).ToArray();
        var posterMs = await TimeAsync(async () =>
        {
            foreach (var preset in presets)
            {
                var clip = Clip(style, preset);
                using var _ = await CaptionPreview.RenderAsync(
                    clip, CaptionPreview.LoopFrames(preset) - 1, 84, 48);
            }
        });

        var clip = Clip(style, "highlightPop");
        var frames = CaptionPreview.LoopFrames("highlightPop");
        var animateMs = await TimeAsync(async () =>
        {
            for (var f = 0; f < frames; f++)
            {
                using var _ = await CaptionPreview.RenderAsync(clip, f, 84, 48);
            }
        });

        _output.WriteLine(
            $"WP-125 cost: 8 posters {posterMs:0} ms; 1 cell × {frames} frames {animateMs:0} ms ({animateMs / frames:0.0} ms/frame).");
        Assert.True(posterMs < 8_000, $"8 posters took {posterMs:0} ms");
        Assert.True(animateMs < 15_000, $"54 animation frames took {animateMs:0} ms");
    }

    private static Clip Clip(TextStyle style, string preset) =>
        CaptionPreview.BuildClip(
            CaptionPreview.GallerySample,
            style,
            CaptionPreview.GalleryTransform(),
            TextAnimation.GalleryPreviewAnimation(preset, TextAnimation.DefaultHighlight));

    private static async Task<double> TimeAsync(Func<Task> work)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await work();
        return sw.Elapsed.TotalMilliseconds;
    }
}
