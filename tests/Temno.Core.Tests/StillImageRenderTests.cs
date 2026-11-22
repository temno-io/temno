using System;
using System.IO;
using System.Threading.Tasks;
using Temno.Core.Editing;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Cache;
using Temno.Media.Export;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// Still images (especially JPEGs decoded through FFmpeg's image2 demuxer) request frame decode at 0 s.
/// Input <c>-ss 0</c> caused FFmpeg to seek past the single frame to EOF, breaking preview, export and
/// frame extraction whenever a JPEG sat on V2 (or any track). These run real FFmpeg end-to-end.
/// </summary>
public sealed class StillImageRenderTests : IDisposable
{
    private readonly string _root = FfmpegFixture.CreateTempDirectory();

    public void Dispose() => FfmpegFixture.DeleteTempDirectory(_root);

    private async Task<string> WriteSolidImageAsync(string name, string color, int width, int height)
    {
        var path = Path.Combine(_root, name);
        await FfmpegFixture.RunAsync(
            $"-hide_banner -loglevel error -y -f lavfi -i color=c={color}:s={width}x{height}:d=1 -frames:v 1 -update 1 \"{path}\"");
        return path;
    }

    private async Task<string> WriteSolidVideoAsync(string name, string color, int width, int height, int fps = 30, int seconds = 2)
    {
        var path = Path.Combine(_root, name);
        await FfmpegFixture.RunAsync(
            $"-hide_banner -loglevel error -y -f lavfi -i color=c={color}:s={width}x{height}:r={fps}:d={seconds} -c:v libx264 -pix_fmt yuv420p \"{path}\"");
        return path;
    }

    [Theory]
    [InlineData(-1.0, false)]
    [InlineData(0.0, false)]
    [InlineData(0.0000001, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(0.001, true)]
    [InlineData(1.5, true)]
    public void ShouldEmitInputSeek_OmitsZeroAndNonPositive(double seconds, bool expected)
    {
        var emitted = FfmpegPlaybackDecoder.ShouldEmitInputSeek(seconds, out var formatted);
        Assert.Equal(expected, emitted);
        if (expected) Assert.NotEmpty(formatted);
        else Assert.Empty(formatted);
    }

    [Theory]
    [InlineData("still.jpg")]
    [InlineData("still.jpeg")]
    [InlineData("imported-b78c7517.jpg")]
    [InlineData("still.png")]
    [InlineData("still.bmp")]
    [InlineData("still.tif")]
    public async Task FfmpegFrameSource_DecodesStillImageAtZero(string fileName)
    {
        if (!FfmpegFixture.IsAvailable) return;
        var path = await WriteSolidImageAsync(fileName, "0x00FF00", 128, 96);

        var source = new FfmpegFrameSource();
        using var frame = await source.GetFrameAsync(path, 0.0);

        Assert.Equal(128, frame.Width);
        Assert.Equal(96, frame.Height);
        // Center pixel must be green, not empty/black.
        var idx = ((frame.Height / 2) * frame.Width + (frame.Width / 2)) * 4;
        var isBgra = string.Equals(frame.PixelFormat, "bgra", StringComparison.OrdinalIgnoreCase);
        var r = frame.Data[idx + (isBgra ? 2 : 0)];
        var g = frame.Data[idx + 1];
        var b = frame.Data[idx + (isBgra ? 0 : 2)];
        Assert.True(g > 200 && r < 40 && b < 40, $"Expected green pixel for {fileName}, got ({r},{g},{b})");
    }

    [Theory]
    [InlineData("still.jpg", false, false)]
    [InlineData("still.jpg", true, false)]
    [InlineData("still.jpg", false, true)]
    [InlineData("still.png", false, false)]
    [InlineData("still.png", false, true)]
    public async Task FfmpegPlaybackDecoder_DecodesStillImageAtZero(string fileName, bool preferHardware, bool sizedTransport)
    {
        if (!FfmpegFixture.IsAvailable) return;
        var path = await WriteSolidImageAsync(fileName, "0x00CCFF", 160, 120);

        using var decoder = await FfmpegPlaybackDecoder.StartAsync(
            path,
            startSeconds: 0.0,
            outputFps: 30,
            width: 160,
            height: 120,
            sourceWidth: 160,
            sourceHeight: 120,
            pixelFormat: "bgra",
            preferHardware: preferHardware,
            sizedTransport: sizedTransport);
        using var frame = await decoder.ReadFrameAsync();

        Assert.Equal(160, frame.Width);
        Assert.Equal(120, frame.Height);
        Assert.Equal(160 * 120 * 4, frame.Data.Length);
    }

    [Theory]
    [InlineData("still.jpg")]
    [InlineData("still.jpeg")]
    [InlineData("still.png")]
    public async Task SequentialAndExportFrameSources_DecodeStillAcrossRepeatedFrames(string fileName)
    {
        if (!FfmpegFixture.IsAvailable) return;
        var path = await WriteSolidImageAsync(fileName, "0x00FF00", 160, 120);

        using var seq = new SequentialFrameSource(maxDimension: 320, preferHardware: true) { OutputFps = 30 };
        for (var i = 0; i < 6; i++)
        {
            using var frame = await seq.GetFrameAsync(path, 0.0);
            Assert.Equal(160, frame.Width);
            Assert.Equal(120, frame.Height);
        }
        Assert.Equal(1, seq.RestartCount);

        using var export = new ExportFrameSource();
        for (var i = 0; i < 6; i++)
        {
            using var frame = await export.GetFrameAsync(path, 0.0);
            Assert.Equal(160, frame.Width);
            Assert.Equal(120, frame.Height);
        }
        Assert.Equal(1, export.RestartCount);
    }

    [Fact]
    public async Task FfmpegPngEncoder_ExtractsStillJpegAtZero()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var path = await WriteSolidImageAsync("frame-extract.jpg", "0x3366FF", 200, 150);

        var png = await FfmpegPngEncoder.ExtractAsync(path, 0.0, maxDimension: 128);

        Assert.True(png.Length > 32);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }

    [Fact]
    public async Task PreviewFrameProvider_CompositesJpegOnV2OverVideoOnV1()
    {
        if (!FfmpegFixture.IsAvailable) return;
        // Reproduce the reported user workflow: red 16:9 video on V1, green square JPEG on V2.
        var videoPath = await WriteSolidVideoAsync("v1.mp4", "0xFF0000", 320, 180);
        var jpegPath = await WriteSolidImageAsync("v2-keroro.jpg", "0x00FF00", 120, 120);

        var manifest = new MediaManifest
        {
            Entries =
            [
                new MediaManifestEntry { Id = "V1-ASSET", Name = "video", Type = ClipType.video, Source = MediaSource.External(videoPath), SourceWidth = 320, SourceHeight = 180, Duration = 2 },
                new MediaManifestEntry { Id = "V2-IMG", Name = "keroro", Type = ClipType.image, Source = MediaSource.External(jpegPath), SourceWidth = 120, SourceHeight = 120, Duration = 0 },
            ],
        };
        var resolver = new MediaResolver(() => manifest, () => null);
        var timeline = new Timeline
        {
            Fps = 30,
            Width = 320,
            Height = 180,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            MediaRef = "V2-IMG",
                            MediaType = ClipType.image,
                            SourceClipType = ClipType.image,
                            StartFrame = 0,
                            DurationFrames = 60,
                            Transform = LayoutEngine.FitTransform(120, 120, 320, 180),
                        },
                    ],
                },
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            MediaRef = "V1-ASSET",
                            MediaType = ClipType.video,
                            SourceClipType = ClipType.video,
                            StartFrame = 0,
                            DurationFrames = 60,
                            Transform = LayoutEngine.FitTransform(320, 180, 320, 180),
                        },
                    ],
                },
            ],
        };

        using var sequential = new SequentialFrameSource(maxDimension: 1280, preferHardware: true) { OutputFps = 30 };
        using var provider = new PreviewFrameProvider(sequential, resolver);

        foreach (var frameIndex in new[] { 0, 15, 30, 5 })
        {
            using var composited = await provider.GetCompositedFrameAsync(timeline, frameIndex);
            Assert.NotNull(composited);
            Assert.Equal(320, composited.Width);
            Assert.Equal(180, composited.Height);

            // Center pixel must come from the green JPEG on V2.
            var centerIdx = ((composited.Height / 2) * composited.Width + (composited.Width / 2)) * 4;
            Assert.True(
                composited.Data[centerIdx + 1] > 200 && composited.Data[centerIdx] < 50,
                $"Frame {frameIndex}: expected V2 green JPEG at center, got RGB=({composited.Data[centerIdx]},{composited.Data[centerIdx + 1]},{composited.Data[centerIdx + 2]})");

            // Left edge pixel (outside the square V2 image's aspect-fitted box) must show V1's red video.
            var edgeIdx = ((composited.Height / 2) * composited.Width + 10) * 4;
            Assert.True(
                composited.Data[edgeIdx] > 200 && composited.Data[edgeIdx + 1] < 50,
                $"Frame {frameIndex}: expected V1 red video at left edge, got RGB=({composited.Data[edgeIdx]},{composited.Data[edgeIdx + 1]},{composited.Data[edgeIdx + 2]})");
        }
    }

    [Fact]
    public void TimelineFrameResolver_IncludesImageTypedTracks()
    {
        var timeline = new Timeline
        {
            Fps = 30,
            Width = 1920,
            Height = 1080,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.image,
                    Clips = [new Clip { MediaRef = "IMG-1", MediaType = ClipType.image, StartFrame = 0, DurationFrames = 60 }],
                },
                new Track
                {
                    Type = ClipType.video,
                    Clips = [new Clip { MediaRef = "VID-1", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 60 }],
                },
            ],
        };

        var layers = TimelineFrameResolver.Resolve(timeline, 10);
        Assert.Equal(2, layers.Count);
        Assert.Equal("VID-1", layers[0].MediaRef);
        Assert.Equal("IMG-1", layers[1].MediaRef);
        Assert.Equal(0.0, layers[1].SourceSeconds);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("mjpeg")]
    [InlineData("webp")]
    [InlineData("bmp")]
    [InlineData("tiff")]
    [InlineData("PNG")]
    public void ProxyPolicy_NeverProxiesStillImageCodecs(string codec)
    {
        Assert.False(ProxyPolicy.Default.ShouldProxyForPreview(3840, 2160, codec, PreviewRaster.PreviewMaxDimension));
        Assert.False(ProxyPolicy.Default.ShouldProxy(3840, 2160, codec));
    }

    [Theory]
    [InlineData("photo.heic", "hevc")]
    [InlineData("photo.heif", "hevc")]
    [InlineData("photo.avif", "av1")]
    [InlineData("photo.png", "png")]
    [InlineData("photo.jpg", "mjpeg")]
    public void ProxySourceSelector_NeverProxiesStillImageFilesEvenWithVideoCodecNames(string fileName, string codecName)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var cacheDir = Path.Combine(_root, "proxies-" + Path.GetFileNameWithoutExtension(fileName));
        var transcoderCalled = false;
        using var selector = new ProxySourceSelector(
            new ProxyCache(new MediaCachePaths(cacheDir)),
            PreviewRaster.PreviewMaxDimension,
            (_, _, _, _) => { transcoderCalled = true; return Task.CompletedTask; });
        var probe = new MediaProbeResult(0.04, 4032, 3024, 25, HasAudio: false, HasVideo: true, VideoCodecName: codecName);

        var selected = selector.SelectDecodePath(path, probe);

        Assert.Equal(path, selected);
        Assert.Equal(ProxyState.NotWarranted, selector.StatusFor(path).State);
        Assert.False(transcoderCalled);
    }

    [Fact]
    public void FfmpegThumbnailGenerator_BuildArguments_OmitsSeekForZeroTinyAndNonFinite()
    {
        foreach (var t in new[] { 0.0, -1.0, 0.0000001, double.NaN, double.PositiveInfinity })
        {
            var args = FfmpegThumbnailGenerator.BuildArguments("still.jpg", t, 96, "out.jpg");
            Assert.DoesNotContain("-ss ", args);
        }
        var positive = FfmpegThumbnailGenerator.BuildArguments("video.mp4", 1.25, 96, "out.jpg");
        Assert.Contains("-ss 1.250 ", positive);
    }

    [Fact]
    public async Task OddDimensionJpeg_DecodesAndCompositesCleanlyAcrossPreviewAndExport()
    {
        if (!FfmpegFixture.IsAvailable) return;
        // Reproduce keroro.jpg's exact odd dimensions (259x194) on V2 over 16:9 V1 video.
        var videoPath = await WriteSolidVideoAsync("bg-odd.mp4", "0x0000FF", 320, 180);
        var jpegPath = await WriteSolidImageAsync("keroro-259x194.jpg", "0xFF2020", 259, 194);

        var manifest = new MediaManifest
        {
            Entries =
            [
                new MediaManifestEntry { Id = "V1", Name = "bg", Type = ClipType.video, Source = MediaSource.External(videoPath), SourceWidth = 320, SourceHeight = 180, Duration = 2 },
                new MediaManifestEntry { Id = "V2", Name = "keroro", Type = ClipType.image, Source = MediaSource.External(jpegPath), SourceWidth = 259, SourceHeight = 194, Duration = 0 },
            ],
        };
        var resolver = new MediaResolver(() => manifest, () => null);
        var timeline = new Timeline
        {
            Fps = 30,
            Width = 320,
            Height = 180,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips = [new Clip { MediaRef = "V2", MediaType = ClipType.image, SourceClipType = ClipType.image, StartFrame = 0, DurationFrames = 30, Transform = LayoutEngine.FitTransform(259, 194, 320, 180) }],
                },
                new Track
                {
                    Type = ClipType.video,
                    Clips = [new Clip { MediaRef = "V1", MediaType = ClipType.video, SourceClipType = ClipType.video, StartFrame = 0, DurationFrames = 30 }],
                },
            ],
        };

        using var seq = new SequentialFrameSource(maxDimension: 1280, preferHardware: true) { OutputFps = 30 };
        using var provider = new PreviewFrameProvider(seq, resolver);
        using var preview = await provider.GetCompositedFrameAsync(timeline, 10);
        Assert.NotNull(preview);
        var center = ((preview.Height / 2) * preview.Width + (preview.Width / 2)) * 4;
        Assert.True(preview.Data[center] > 200 && preview.Data[center + 2] < 60,
            $"Expected red V2 odd-width JPEG at center, got RGB=({preview.Data[center]},{preview.Data[center + 1]},{preview.Data[center + 2]})");

        using var exportSrc = new ExportFrameSource();
        using var exported = await exportSrc.GetFrameAsync(jpegPath, 0.0);
        Assert.Equal(259, exported.Width);
        Assert.Equal(194, exported.Height);
    }

    [Fact]
    public void ExportPreflightAndRanges_IncludeOfflineImageOnImageTypedTrack()
    {
        var manifest = new MediaManifest
        {
            Entries =
            [
                new MediaManifestEntry { Id = "IMG-MISSING", Name = "missing.jpg", Type = ClipType.image, Source = MediaSource.External(Path.Combine(_root, "does-not-exist.jpg")) },
            ],
        };
        var timeline = new Timeline
        {
            Fps = 30,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.image,
                    Clips = [new Clip { MediaRef = "IMG-MISSING", MediaType = ClipType.image, StartFrame = 10, DurationFrames = 30 }],
                },
            ],
        };
        var resolver = new MediaResolver(() => manifest, () => null);
        var report = Temno.Core.Export.ExportMediaPreflight.Inspect(timeline, manifest, resolver, fileExists: _ => false);

        Assert.True(report.HasProblems);
        var item = Assert.Single(report.Problems);
        Assert.Equal("IMG-MISSING", item.Reference);
        Assert.Equal(Temno.Core.Export.ExportMediaState.Missing, item.State);

        var ranges = Temno.Mcp.ExportMediaRanges.Where(timeline, "IMG-MISSING");
        var span = Assert.Single(ranges);
        Assert.Equal(10, span.StartFrame);
        Assert.Equal(40, span.EndFrame);
    }
}
