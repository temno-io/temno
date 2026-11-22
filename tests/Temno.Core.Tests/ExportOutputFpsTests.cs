using System.Text.Json;
using Temno.Core.Export;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Hardware;
using Xunit;

namespace Temno.Core.Tests;

public sealed class ExportOutputFpsTests : IDisposable
{
    private readonly string _root = FfmpegFixture.CreateTempDirectory();

    public void Dispose() => FfmpegFixture.DeleteTempDirectory(_root);

    [Theory]
    [InlineData(24000.0 / 1001.0, "24000/1001", "23.976 fps")]
    [InlineData(30000.0 / 1001.0, "30000/1001", "29.97 fps")]
    [InlineData(60000.0 / 1001.0, "60000/1001", "59.94 fps")]
    [InlineData(24, "24", "24 fps")]
    [InlineData(30, "30", "30 fps")]
    [InlineData(60, "60", "60 fps")]
    [InlineData(120, "120", "120 fps")]
    [InlineData(45, "45", "45 fps")]
    public void ExportFrameRate_FormatsExactFfmpegExpressionAndLabel(double fps, string expectedExpr, string expectedLabel)
    {
        Assert.True(ExportFrameRate.TryValidate(fps, out var error));
        Assert.Null(error);
        Assert.Equal(expectedExpr, ExportFrameRate.ToFfmpegExpression(fps));
        Assert.Equal(expectedLabel, ExportFrameRate.FormatLabel(fps));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-24)]
    [InlineData(121)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ExportFrameRate_RejectsOutOfRangeValues(double fps)
    {
        Assert.False(ExportFrameRate.TryValidate(fps, out var error));
        Assert.NotNull(error);
        Assert.Throws<InvalidOperationException>(() => ExportFrameRate.ResolveOverride(fps, timelineFps: 30));
    }

    [Fact]
    public void ExportFrameRate_ResolveOverride_ReturnsNullWhenMatchingTimeline()
    {
        Assert.Null(ExportFrameRate.ResolveOverride(null, 30));
        Assert.Null(ExportFrameRate.ResolveOverride(30, 30));
        Assert.Equal(60, ExportFrameRate.ResolveOverride(60, 30));
        Assert.NotNull(ExportFrameRate.ResolveOverride(30000.0 / 1001.0, 30));
    }

    [Fact]
    public void BuildVideoPassArguments_EmitsFpsFilterAndAdjustedGopOnlyWhenResampling()
    {
        var matching = ExportEncodeArguments.BuildVideoPassArguments(
            "libx264", 1920, 1080, fps: 30, VideoCodec.H264, 12, 23,
            explicitBitrate: false, writeFaststart: true, "out.mp4", outputFps: 30);
        Assert.DoesNotContain("fps=", matching);
        Assert.Contains("-r 30 -i pipe:0", matching);
        Assert.Contains("-g 60", matching);

        var ntsc = ExportEncodeArguments.BuildVideoPassArguments(
            "libx264", 1920, 1080, fps: 30, VideoCodec.H264, 12, 23,
            explicitBitrate: false, writeFaststart: true, "out.mp4", outputFps: 24000.0 / 1001.0);
        Assert.Contains("-r 30 -i pipe:0", ntsc);
        Assert.Contains("fps=24000/1001", ntsc);
        Assert.Contains("-g 48", ntsc);

        var paddedAndResampled = ExportEncodeArguments.BuildVideoPassArguments(
            "libx264", 1280, 720, fps: 30, VideoCodec.H264, 12, 23,
            explicitBitrate: false, writeFaststart: true, "out.mp4",
            codedWidth: 1280, codedHeight: 1280, outputFps: 60);
        Assert.Contains("fps=60,pad=1280:1280:0:280:color=black", paddedAndResampled);
        Assert.Contains("-g 120", paddedAndResampled);
    }

    [Fact]
    public void ExportPreset_RoundTripsCustomFpsInJson()
    {
        var preset = new ExportPreset("Custom", VideoCodec.H264, ExportResolution.MatchTimeline)
        {
            CustomFps = 24000.0 / 1001.0,
        };
        var json = JsonSerializer.Serialize(preset);
        var loaded = JsonSerializer.Deserialize<ExportPreset>(json);
        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.CustomFps);
        Assert.InRange(loaded.CustomFps!.Value, 23.975, 23.977);
    }

    [Fact]
    public async Task TimelineExporter_ResamplesOutputToCustomAndNtscFpsEndToEnd()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var timeline = new Timeline
        {
            Fps = 30,
            Width = 160,
            Height = 90,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            MediaType = ClipType.text,
                            SourceClipType = ClipType.text,
                            TextContent = "FPS TEST",
                            StartFrame = 0,
                            DurationFrames = 30,
                        },
                    ],
                },
            ],
        };

        var probe = new FfmpegStreamProbe();

        // 30 fps timeline exported at 60 fps
        var out60 = Path.Combine(_root, "out-60.mp4");
        using (var exporter = new TimelineExporter(
            new CpuCompositor(), new StubFrameSource(), acceleration: HardwareAccelerationMode.Software))
        {
            var preset60 = new ExportPreset("60fps", VideoCodec.H264, ExportResolution.MatchTimeline) { CustomFps = 60 };
            await exporter.ExportAsync(timeline, preset60, out60, overwrite: true);
            Assert.Contains("60 fps", exporter.LastSmartRenderFallbackReason ?? "");
        }
        var info60 = await probe.ProbeAsync(out60);
        Assert.NotNull(info60);
        Assert.InRange(info60!.FrameRate, 59.5, 60.5);

        // 30 fps timeline exported at 23.976 fps (24000/1001)
        var outNtsc = Path.Combine(_root, "out-23976.mp4");
        using (var exporter = new TimelineExporter(
            new CpuCompositor(), new StubFrameSource(), acceleration: HardwareAccelerationMode.Software))
        {
            var presetNtsc = new ExportPreset("NTSC", VideoCodec.H264, ExportResolution.MatchTimeline) { CustomFps = 24000.0 / 1001.0 };
            await exporter.ExportAsync(timeline, presetNtsc, outNtsc, overwrite: true);
            Assert.Contains("23.976 fps", exporter.LastSmartRenderFallbackReason ?? "");
        }
        var infoNtsc = await probe.ProbeAsync(outNtsc);
        Assert.NotNull(infoNtsc);
        Assert.InRange(infoNtsc!.FrameRate, 23.95, 24.0);
    }

    [Fact]
    public void CanvasSizePresets_MatchesStandardAndCustomValuesAndRoundsEven()
    {
        Assert.Equal("16:9", CanvasSizePresets.MatchAspectTag(1920, 1080));
        Assert.Equal("9:16", CanvasSizePresets.MatchAspectTag(1080, 1920));
        Assert.Equal("1:1", CanvasSizePresets.MatchAspectTag(1080, 1080));
        Assert.Equal("4:3", CanvasSizePresets.MatchAspectTag(1440, 1080));
        Assert.Equal("2.4:1", CanvasSizePresets.MatchAspectTag(2560, 1080));
        Assert.Null(CanvasSizePresets.MatchAspectTag(1234, 800));

        Assert.Equal("1080p", CanvasSizePresets.MatchQualityTag(1920, 1080));
        Assert.Equal("2K", CanvasSizePresets.MatchQualityTag(2560, 1440));
        Assert.Equal("4K", CanvasSizePresets.MatchQualityTag(3840, 2160));
        Assert.Null(CanvasSizePresets.MatchQualityTag(1500, 1500));

        Assert.Equal("30", CanvasSizePresets.MatchProjectFpsTag(30));
        Assert.Equal("custom", CanvasSizePresets.MatchProjectFpsTag(48));

        Assert.True(CanvasSizePresets.TryValidateCustomCanvas(1280, 720, out _));
        Assert.False(CanvasSizePresets.TryValidateCustomCanvas(1281, 720, out _));
        Assert.False(CanvasSizePresets.TryValidateCustomCanvas(8, 720, out _));

        // SetProjectSettings: 2.4:1 at 720p must stay even, custom FPS applies cleanly, invalid FPS fails.
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.SetProjectSettings(session, fps: 48, width: null, height: null, aspectRatio: "2.4:1", quality: "720p").Ok);
        Assert.Equal(48, session.ActiveTimeline.Fps);
        Assert.Equal(0, session.ActiveTimeline.Width % 2);
        Assert.Equal(720, session.ActiveTimeline.Height);
        Assert.False(EditCommands.SetProjectSettings(session, fps: 0, width: null, height: null, aspectRatio: null, quality: null).Ok);
        Assert.False(EditCommands.SetProjectSettings(session, fps: 200, width: null, height: null, aspectRatio: null, quality: null).Ok);
    }

    private sealed class StubFrameSource : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(string mediaPath, double timeSeconds, CancellationToken cancellationToken = default)
            => throw new FileNotFoundException(mediaPath);
    }
}
