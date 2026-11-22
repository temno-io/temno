using System.Text.Json;
using Temno.Core.Export;
using Temno.Core.Models;
using Temno.Media.Export;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-167. Custom export size is validated before encode; presets keep today's raster.</summary>
public sealed class ExportOutputSizeTests
{
    [Fact]
    public void Preset_1080p_IsUnchangedWhenCustomIsNull()
    {
        var timeline = new Timeline { Width = 1280, Height = 720, Fps = 30 };
        var preset = new ExportPreset("n", VideoCodec.H264, ExportResolution.R1080p);
        Assert.Equal((1920, 1080), ExportOutputSize.Resolve(timeline, preset));
        Assert.Equal((1920, 1080), ExportEncodeArguments.ResolveOutputSize(timeline, preset));
    }

    [Fact]
    public void MatchTimeline_EvenRounds_SameAsBefore()
    {
        var timeline = new Timeline { Width = 1921, Height = 1081, Fps = 24 };
        var preset = new ExportPreset("n");
        Assert.Equal((1920, 1080), ExportOutputSize.Resolve(timeline, preset));
        Assert.Equal((1920, 1080), ExportEncodeArguments.ResolveOutputSize(timeline, preset));
    }

    [Fact]
    public void CustomSize_WinsOverPresetLadder()
    {
        var timeline = new Timeline { Width = 1920, Height = 1080, Fps = 30 };
        var preset = new ExportPreset("n", VideoCodec.H264, ExportResolution.R1080p)
        {
            CustomWidth = 640,
            CustomHeight = 360,
        };
        Assert.Equal((640, 360), ExportOutputSize.Resolve(timeline, preset));
        Assert.Equal((640, 360), ExportEncodeArguments.ResolveOutputSize(timeline, preset));
    }

    [Fact]
    public void CustomOddSize_FloorsEven_WhenConstructedOffTheDialog()
    {
        var timeline = new Timeline { Width = 1280, Height = 720 };
        var preset = new ExportPreset("n") { CustomWidth = 641, CustomHeight = 361 };
        Assert.Equal((640, 360), ExportOutputSize.Resolve(timeline, preset));
    }

    [Fact]
    public void Validate_AcceptsEvenDownscale()
    {
        Assert.True(ExportOutputSize.TryValidateCustom(640, 360, 1280, 720, allowUpscale: false, requireEven: true, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Validate_RefusesOdd()
    {
        Assert.False(ExportOutputSize.TryValidateCustom(641, 360, 1280, 720, allowUpscale: false, requireEven: true, out var error));
        Assert.Contains("even", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RefusesUpscaleUnlessAllowed()
    {
        Assert.False(ExportOutputSize.TryValidateCustom(1920, 1080, 1280, 720, allowUpscale: false, requireEven: true, out var blocked));
        Assert.Contains("upscale", blocked, StringComparison.OrdinalIgnoreCase);
        Assert.True(ExportOutputSize.TryValidateCustom(1920, 1080, 1280, 720, allowUpscale: true, requireEven: true, out var allowed));
        Assert.Null(allowed);
    }

    [Fact]
    public void Validate_RefusesZero_AndOversize()
    {
        Assert.False(ExportOutputSize.TryValidateCustom(0, 360, 1280, 720, false, true, out _));
        Assert.False(ExportOutputSize.TryValidateCustom(7682, 4320, 1280, 720, true, true, out var over));
        Assert.Contains("cannot exceed", over, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PresetJson_OmitsCustomKeysWhenNull()
    {
        var preset = new ExportPreset("n", VideoCodec.H264, ExportResolution.R1080p);
        var json = JsonSerializer.Serialize(preset, JsonOptions.ForProject);
        Assert.DoesNotContain("customWidth", json, StringComparison.Ordinal);
        Assert.DoesNotContain("customHeight", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1920, 1080, ExportResolution.R720p, 1280, 720)]
    [InlineData(1920, 1080, ExportResolution.R1080p, 1920, 1080)]
    [InlineData(1920, 1080, ExportResolution.R1440p, 2560, 1440)]
    [InlineData(1920, 1080, ExportResolution.R4k, 3840, 2160)]
    [InlineData(1080, 1920, ExportResolution.R720p, 720, 1280)]
    [InlineData(1080, 1920, ExportResolution.R1080p, 1080, 1920)]
    [InlineData(1080, 1920, ExportResolution.R1440p, 1440, 2560)]
    [InlineData(1080, 1920, ExportResolution.R4k, 2160, 3840)]
    [InlineData(1080, 1080, ExportResolution.R720p, 720, 720)]
    [InlineData(1080, 1080, ExportResolution.R1080p, 1080, 1080)]
    [InlineData(1080, 1080, ExportResolution.R1440p, 1440, 1440)]
    [InlineData(1080, 1080, ExportResolution.R4k, 2160, 2160)]
    [InlineData(1440, 1080, ExportResolution.R720p, 960, 720)]
    [InlineData(1440, 1080, ExportResolution.R1080p, 1440, 1080)]
    [InlineData(1440, 1080, ExportResolution.R1440p, 1920, 1440)]
    [InlineData(1440, 1080, ExportResolution.R4k, 2880, 2160)]
    [InlineData(2560, 1080, ExportResolution.R720p, 1706, 720)]
    [InlineData(2560, 1080, ExportResolution.R1080p, 2560, 1080)]
    [InlineData(2560, 1080, ExportResolution.R1440p, 3414, 1440)]
    [InlineData(2560, 1080, ExportResolution.R4k, 5120, 2160)]
    [InlineData(1080, 1680, ExportResolution.R720p, 720, 1120)]
    [InlineData(1080, 1680, ExportResolution.R1080p, 1080, 1680)]
    [InlineData(1080, 1680, ExportResolution.R1440p, 1440, 2240)]
    [InlineData(1080, 1680, ExportResolution.R4k, 2160, 3360)]
    public void NamedSize_SetsTheShortSide_AndKeepsTheShape(
        int timelineWidth, int timelineHeight, ExportResolution resolution, int width, int height)
    {
        var timeline = new Timeline { Width = timelineWidth, Height = timelineHeight };
        var preset = new ExportPreset("n", resolution: resolution);
        var size = ExportOutputSize.Resolve(timeline, preset);
        Assert.Equal((width, height), size);
        Assert.Equal(0, size.Width % 2);
        Assert.Equal(0, size.Height % 2);
        Assert.Equal(ExportOutputSize.ShortSide(resolution), Math.Min(size.Width, size.Height));

        var frame = ExportOutputSize.ResolveFrame(timeline, preset);
        Assert.False(frame.IsPadded);
        var aspectError = Math.Abs(size.Width / (double)size.Height - timelineWidth / (double)timelineHeight);
        Assert.InRange(aspectError, 0, 0.002);
    }

    [Fact]
    public void ChoiceLabel_NamesThePixelsForThisTimeline()
    {
        Assert.Equal("1080p · 1080 × 1920", ExportOutputSize.ChoiceLabel(ExportResolution.R1080p, 1080, 1920));
        Assert.Equal("1080p · 1920 × 1080", ExportOutputSize.ChoiceLabel(ExportResolution.R1080p, 1920, 1080));
        Assert.Equal("Match Timeline · 1080 × 1920", ExportOutputSize.ChoiceLabel(ExportResolution.MatchTimeline, 1080, 1920));
        Assert.Equal("4K · 5120 × 2160", ExportOutputSize.ChoiceLabel(ExportResolution.R4k, 2560, 1080));
    }

    [Fact]
    public void NamedSize_PastMaxDimension_IsRefused_NotClamped()
    {
        var preset = new ExportPreset("n", resolution: ExportResolution.R4k);
        Assert.False(ExportOutputSize.TryResolve(8000, 1000, preset, out var w, out var h, out var error));
        Assert.Equal(17280, w);
        Assert.Equal(2160, h);
        Assert.Contains("17280", error);
        Assert.Contains("7680", error);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ExportOutputSize.Resolve(new Timeline { Width = 8000, Height = 1000 }, preset));
        Assert.Contains("17280×2160", ex.Message);
        Assert.DoesNotContain("7680×", ex.Message);
    }

    [Fact]
    public void CustomSize_SameShape_IsNotPadded()
    {
        var preset = new ExportPreset("n") { CustomWidth = 640, CustomHeight = 360 };
        var frame = ExportOutputSize.ResolveFrame(1920, 1080, preset);
        Assert.Equal(new ExportOutputSize.FittedExport(640, 360, 640, 360, 0, 0), frame);
        Assert.False(frame.IsPadded);
    }

    [Fact]
    public void CustomSize_OtherShape_FitsInsideWithBars()
    {
        var preset = new ExportPreset("n") { CustomWidth = 1920, CustomHeight = 1080 };
        var frame = ExportOutputSize.ResolveFrame(1080, 1920, preset);
        Assert.Equal(1920, frame.Width);
        Assert.Equal(1080, frame.Height);
        Assert.Equal(606, frame.ContentWidth);
        Assert.Equal(1080, frame.ContentHeight);
        Assert.Equal(657, frame.PadX);
        Assert.Equal(0, frame.PadY);
        Assert.True(frame.IsPadded);
        Assert.Equal(frame.Width, frame.PadX * 2 + frame.ContentWidth);
        Assert.InRange(Math.Abs(frame.ContentWidth / (double)frame.ContentHeight - 1080 / 1920.0), 0, 0.01);
    }

    [Fact]
    public void PresetJson_WritesCustomKeysWhenSet()
    {
        var preset = new ExportPreset("n") { CustomWidth = 640, CustomHeight = 360 };
        var json = JsonSerializer.Serialize(preset, JsonOptions.ForProject);
        Assert.Contains("\"customWidth\": 640", json, StringComparison.Ordinal);
        Assert.Contains("\"customHeight\": 360", json, StringComparison.Ordinal);
    }
}
