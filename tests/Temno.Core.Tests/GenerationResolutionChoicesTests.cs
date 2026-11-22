using Temno.Generation;
using Xunit;

namespace Temno.Core.Tests;

// WP-419: the Generate panel's Resolution dropdown. The descriptions below are the gateway catalog's
// strings (src/Temno.Gateway/Images/Catalog/ImageModelCatalog.cs, "Explicit WxH output size. " + the
// SizeRange text); docs/verification/wp-419 runs every offered value through the gateway itself.
public sealed class GenerationResolutionChoicesTests
{
    private const string FluxSchnell = "Explicit WxH output size. Sides 256..4096, area to 16MP (Temno bound). Presets are not admitted.";
    private const string Seedream = "Explicit WxH output size. Sides 256..4096, area 1MP..4MP, aspect 1:16..16:1. Default 1536x1536 for generate, first input dimensions for edit.";
    private const string Flux2Pro = "Explicit WxH output size. Generate only. Sides 256..2560, multiples of 16, area to 4MP.";

    private static string[] Values(ResolutionChoices c) => [.. c.Choices.Select(x => x.Value)];
    private static string[] Labels(ResolutionChoices c) => [.. c.Choices.Select(x => x.Label)];

    [Fact]
    public void An_enumerated_tier_lists_exactly_its_values_in_catalog_order()
    {
        var nano = ResolutionChoices.For("resolution", ["1K", "2K", "4K"], "1K", "Output resolution tier.")!;
        Assert.Equal(["1K", "2K", "4K"], Values(nano));
        Assert.Equal("1K", nano.Selected);
        Assert.False(nano.AllowsCustom);
        Assert.Equal("2K", nano.Read("2K"));

        // Grok sends lowercase; the label is uppercase and the wire keeps the provider's spelling.
        var grok = ResolutionChoices.For("resolution", ["1k", "2k"], "1k", "Lowercase provider spelling.")!;
        Assert.Equal(["1K", "2K"], Labels(grok));
        Assert.Equal("2k", grok.Read("2k"));

        var veo = ResolutionChoices.For("resolution", ["720p", "1080p", "4k"], "720p", "Output resolution tier.")!;
        Assert.Equal(["720p", "1080p", "4k"], Values(veo));
        Assert.Equal(["720p", "1080p", "4K"], Labels(veo));
        Assert.Equal("Output resolution", veo.Hint);

        var upscale = ResolutionChoices.For("target_resolution", ["1080p", "2k", "4k"], "1080p", "Output height class.")!;
        Assert.Equal(["1080p", "2K", "4K"], Labels(upscale));
        Assert.Equal("4k", upscale.Read("4k"));
    }

    [Fact]
    public void Enumerated_sizes_read_as_shape_ratio_and_pixels()
    {
        var gpt = ResolutionChoices.For("size",
            ["1024x768", "1024x1024", "1024x1536", "1920x1080", "2560x1440", "3840x2160"], "1024x1024", "Only priced sizes are admitted.")!;
        Assert.Equal(["Landscape 4:3 · 1024 × 768", "Square · 1024 × 1024", "Portrait 2:3 · 1024 × 1536",
            "Landscape 16:9 · 1920 × 1080", "Landscape 16:9 · 2560 × 1440", "Landscape 16:9 · 3840 × 2160"], Labels(gpt));
        Assert.Equal("1024x1024", gpt.Selected);
        Assert.False(gpt.AllowsCustom);
        Assert.Equal("Output size in pixels", gpt.Hint);
        Assert.Equal("Landscape · 1920 × 1088", ResolutionChoices.SizeLabel(1920, 1088));
    }

    [Fact]
    public void A_default_outside_the_list_adds_Automatic_which_omits_the_setting()
    {
        var c = ResolutionChoices.For("resolution", ["720p", "1080p"], "(omitted)", "")!;
        Assert.Equal(["", "720p", "1080p"], Values(c));
        Assert.Equal("Automatic", c.Choices[0].Label);
        Assert.Equal("", c.Selected);
        Assert.Null(c.Read(""));
        Assert.Null(c.Read(null));
    }

    [Fact]
    public void Flux_schnell_offers_every_common_size_its_limits_admit_plus_custom()
    {
        var c = ResolutionChoices.For("size", [], "1024x1024", FluxSchnell)!;
        Assert.Equal(new ResolutionSizeLimits(256, 4096, 256 * 256, 16L << 20, 1, 256m / 4096m, 16m), c.Limits);
        Assert.Equal(["512x512", "1024x1024", "1536x1536", "2048x2048", "1024x768", "1536x1024", "1280x720", "1920x1080",
            "2560x1440", "3840x2160", "768x1024", "1024x1536", "720x1280", "1080x1920", "1440x2560", "2160x3840"], Values(c));
        Assert.Equal("1024x1024", c.Selected);
        Assert.True(c.AllowsCustom);
        Assert.Equal((1024, 1024), c.DefaultSize);
        Assert.Equal("Output size in pixels. Each side 256 to 4096 px, up to 16 MP.", c.Hint);

        Assert.Equal("1920x1080", c.Read("1920x1080"));
        Assert.Equal("1280x704", c.Read(ResolutionChoices.Custom, 1280, 704));
        Assert.Equal("Each side must be 256 to 4096 px.",
            Assert.Throws<InvalidOperationException>(() => c.Read(ResolutionChoices.Custom, 5000, 1000)).Message);
        Assert.Equal("Each side must be 256 to 4096 px.",
            Assert.Throws<InvalidOperationException>(() => c.Read(ResolutionChoices.Custom, 200, 1000)).Message);
        Assert.Equal("Enter a whole number of pixels for width and height.",
            Assert.Throws<InvalidOperationException>(() => c.Read(ResolutionChoices.Custom, 1024.5, 1024)).Message);
        Assert.Throws<InvalidOperationException>(() => c.Read(ResolutionChoices.Custom));
    }

    [Fact]
    public void Seedream_keeps_only_sizes_between_one_and_four_megapixels()
    {
        var c = ResolutionChoices.For("size", [], "1536x1536", Seedream)!;
        Assert.Equal(["1024x1024", "1536x1536", "2048x2048", "1536x1024", "1920x1080", "2560x1440", "1024x1536",
            "1080x1920", "1440x2560"], Values(c));
        Assert.Equal("1536x1536", c.Selected);
        Assert.Equal(1L << 20, c.Limits!.MinArea);
        Assert.Equal(4L << 20, c.Limits.MaxArea);
        Assert.Equal(1m / 16m, c.Limits.MinAspect);
        Assert.Equal("Output size in pixels. Each side 256 to 4096 px, up to 4 MP and at least 1 MP.", c.Hint);
        Assert.Equal("That size is under this model's 1 MP minimum.",
            Assert.Throws<InvalidOperationException>(() => c.Read(ResolutionChoices.Custom, 1280, 720)).Message);
        Assert.Equal("That size is over this model's 4 MP limit.",
            Assert.Throws<InvalidOperationException>(() => c.Read(ResolutionChoices.Custom, 3840, 2160)).Message);
        Assert.Equal("That shape is wider or taller than this model allows.",
            new ResolutionSizeLimits(256, 4096, 1, long.MaxValue, 1, 0.5m, 2m).Problem(1024, 256));
    }

    [Fact]
    public void Flux_2_pro_drops_sizes_that_are_not_multiples_of_16()
    {
        var c = ResolutionChoices.For("size", [], "1024x1024", Flux2Pro)!;
        Assert.DoesNotContain("1920x1080", Values(c));
        Assert.DoesNotContain("1080x1920", Values(c));
        Assert.DoesNotContain("3840x2160", Values(c));
        Assert.Contains("2560x1440", Values(c));
        Assert.Contains("2048x2048", Values(c));
        Assert.All(c.Choices, x => Assert.True(c.Limits!.Admits(ResolutionChoices.ParseSize(x.Value)!.Value.W,
            ResolutionChoices.ParseSize(x.Value)!.Value.H)));
        Assert.Equal("Output size in pixels. Each side 256 to 2560 px in steps of 16, up to 4 MP.", c.Hint);
        Assert.Equal("Width and height must be multiples of 16 px.",
            Assert.Throws<InvalidOperationException>(() => c.Read(ResolutionChoices.Custom, 1920, 1080)).Message);
        Assert.Equal("1920x1088", c.Read(ResolutionChoices.Custom, 1920, 1088));
    }

    [Fact]
    public void A_free_size_with_unreadable_limits_offers_the_default_and_custom()
    {
        var c = ResolutionChoices.For("size", [], "1024x1024", "Explicit WxH output size.")!;
        Assert.Equal(["1024x1024"], Values(c));
        Assert.True(c.AllowsCustom);
        Assert.Null(c.Limits);
        Assert.Equal("640x480", c.Read(ResolutionChoices.Custom, 640, 480));

        var noDefault = ResolutionChoices.For("size", [], "", FluxSchnell)!;
        Assert.Equal("", noDefault.Selected);
        Assert.Equal("Automatic", noDefault.Choices[0].Label);
        Assert.Null(noDefault.DefaultSize);
    }

    [Fact]
    public void Other_settings_and_free_tiers_are_not_dropdowns()
    {
        Assert.Null(ResolutionChoices.For("aspect_ratio", ["1:1", "16:9"], "1:1", ""));
        Assert.Null(ResolutionChoices.For("count", [], "1", ""));
        Assert.Null(ResolutionChoices.For("resolution", [], "", "Free text."));
        Assert.False(ResolutionChoices.IsResolution("quality"));
        Assert.Null(ResolutionChoices.ParseSize("1024 x 1024"));
        Assert.Null(ResolutionChoices.ParseSize("0x1024"));
        Assert.Equal((1280, 720), ResolutionChoices.ParseSize("1280x720"));
    }
}
