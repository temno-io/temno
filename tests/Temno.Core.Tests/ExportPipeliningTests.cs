using System.Diagnostics;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Hardware;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// Verifies the single-producer / single-consumer frame-ahead pipelining in <see cref="TimelineExporter"/>.
/// Covers in-order frame delivery, exception isolation, and prompt cancellation.
/// </summary>
public sealed class ExportPipeliningTests
{
    private static (Timeline Timeline, MediaResolver Resolver) CreateTimelineWithResolver(
        int totalFrames = 10, int fps = 10, int width = 64, int height = 64)
    {
        var manifest = new MediaManifest
        {
            Entries =
            [
                new MediaManifestEntry
                {
                    Id = "clip",
                    Type = ClipType.video,
                    Source = MediaSource.External("mock://clip.mp4"),
                },
            ],
        };
        var resolver = new MediaResolver(() => manifest, () => null);
        var timeline = new Timeline
        {
            Fps = fps,
            Width = width,
            Height = height,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            Id = "video-clip-1",
                            MediaRef = "clip",
                            MediaType = ClipType.video,
                            SourceClipType = ClipType.video,
                            StartFrame = 0,
                            DurationFrames = totalFrames,
                        },
                    ],
                },
            ],
        };
        return (timeline, resolver);
    }

    [Fact]
    public async Task ExportAsync_PipelinedFrames_ReachEncoderInOrderAndExactlyOnce()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var tempDirectory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var outputPath = Path.Combine(tempDirectory, "in_order.mp4");
            const int totalFrames = 10;
            const int fps = 10;
            var (timeline, resolver) = CreateTimelineWithResolver(totalFrames, fps);

            var frameSource = new StampingFrameSource(fps);
            var exporter = new TimelineExporter(
                new CpuCompositor(),
                frameSource,
                resolver,
                acceleration: HardwareAccelerationMode.Software);

            var progressCalls = new List<(int Current, int Total)>();
            exporter.Progress += (current, total) => progressCalls.Add((current, total));

            await exporter.ExportAsync(timeline, new ExportPreset("test"), outputPath);

            // 1. Assert frames were rendered strictly in order 0..totalFrames-1
            Assert.Equal(Enumerable.Range(0, totalFrames), frameSource.RenderedFrames);

            // 2. Assert progress was reported for every frame handed to the encoder
            Assert.Equal(totalFrames, progressCalls.Count);
            for (var i = 0; i < totalFrames; i++)
            {
                Assert.Equal((i + 1, totalFrames), progressCalls[i]);
            }

            // 3. Assert output file exists and has video with expected duration
            Assert.True(File.Exists(outputPath));
            var probe = await new FfmpegMediaProbe().ProbeAsync(outputPath);
            Assert.True(probe.HasVideo);
            Assert.InRange(probe.DurationSeconds, 0.90, 1.10);

            // 4. Decode exported frames and verify pixel luminance values are monotonically increasing
            var decoder = new FfmpegFrameSource();
            byte lastLuma = 0;
            for (var i = 0; i < totalFrames; i++)
            {
                using var decoded = await decoder.GetFrameAsync(outputPath, i / (double)fps);
                var luma = decoded.Data[0];
                if (i > 0)
                {
                    Assert.True(luma > lastLuma,
                        $"Frame {i} luma ({luma}) should be greater than frame {i - 1} luma ({lastLuma}).");
                }
                lastLuma = luma;
            }
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task ExportAsync_RenderSideException_SurfacesAsItself_NotFfmpegEncodeException()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var tempDirectory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var outputPath = Path.Combine(tempDirectory, "render_error.mp4");
            var (timeline, resolver) = CreateTimelineWithResolver(totalFrames: 10, fps: 10);

            var frameSource = new ThrowingFrameSource(throwAtFrame: 5, fps: 10);
            var exporter = new TimelineExporter(
                new CpuCompositor(),
                frameSource,
                resolver,
                acceleration: HardwareAccelerationMode.Software);

            var ex = await Assert.ThrowsAsync<CustomRenderException>(() =>
                exporter.ExportAsync(timeline, new ExportPreset("test"), outputPath));

            Assert.Equal("Render failed at frame 5", ex.Message);
            Assert.IsNotType<FfmpegEncodeException>(ex);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task ExportAsync_RenderSideIoException_SurfacesAsItself_NotFfmpegEncodeException()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var tempDirectory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var outputPath = Path.Combine(tempDirectory, "render_io_error.mp4");
            var (timeline, resolver) = CreateTimelineWithResolver(totalFrames: 10, fps: 10);

            var frameSource = new ThrowingIoFrameSource(throwAtFrame: 5, fps: 10);
            var exporter = new TimelineExporter(
                new CpuCompositor(),
                frameSource,
                resolver,
                acceleration: HardwareAccelerationMode.Software);

            var ex = await Assert.ThrowsAsync<IOException>(() =>
                exporter.ExportAsync(timeline, new ExportPreset("test"), outputPath));

            Assert.Equal("Simulated disk read error at frame 5", ex.Message);
            Assert.IsNotType<FfmpegEncodeException>(ex);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task ExportAsync_CancellationMidExport_StopsPromptlyAndDoesNotHang()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var tempDirectory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var outputPath = Path.Combine(tempDirectory, "cancelled.mp4");
            var (timeline, resolver) = CreateTimelineWithResolver(totalFrames: 200, fps: 10);
            using var cts = new CancellationTokenSource();

            var exporter = new TimelineExporter(
                new CpuCompositor(),
                new StampingFrameSource(10),
                resolver,
                acceleration: HardwareAccelerationMode.Software);
            var progressCount = 0;
            exporter.Progress += (cur, _) =>
            {
                progressCount++;
                if (cur >= 3)
                {
                    cts.Cancel();
                }
            };

            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                exporter.ExportAsync(timeline, new ExportPreset("test"), outputPath, cts.Token));
            sw.Stop();

            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"Export cancellation took too long: {sw.Elapsed}");
            Assert.True(progressCount < 200, $"Expected early cancellation exit, but progress reached {progressCount}");
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDirectory);
        }
    }

    private sealed class CustomRenderException(string message) : Exception(message);

    private sealed class StampingFrameSource(int fps = 10) : IFrameSource
    {
        public List<int> RenderedFrames { get; } = [];

        public Task<VideoFrame> GetFrameAsync(string mediaPath, double seconds, CancellationToken ct = default)
        {
            var frameIndex = (int)Math.Round(seconds * fps);
            lock (RenderedFrames)
            {
                RenderedFrames.Add(frameIndex);
            }

            var pixels = new byte[64 * 64 * 4];
            var luma = (byte)Math.Clamp(frameIndex * 22 + 10, 0, 255);
            for (var i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = luma;
                pixels[i + 1] = luma;
                pixels[i + 2] = luma;
                pixels[i + 3] = 255;
            }
            return Task.FromResult(new VideoFrame(64, 64, pixels, "rgba"));
        }
    }

    private sealed class ThrowingFrameSource(int throwAtFrame, int fps = 10) : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(string mediaPath, double seconds, CancellationToken ct = default)
        {
            var frameIndex = (int)Math.Round(seconds * fps);
            if (frameIndex >= throwAtFrame)
                throw new CustomRenderException($"Render failed at frame {frameIndex}");

            var pixels = new byte[64 * 64 * 4];
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return Task.FromResult(new VideoFrame(64, 64, pixels, "rgba"));
        }
    }

    private sealed class ThrowingIoFrameSource(int throwAtFrame, int fps = 10) : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(string mediaPath, double seconds, CancellationToken ct = default)
        {
            var frameIndex = (int)Math.Round(seconds * fps);
            if (frameIndex >= throwAtFrame)
                throw new IOException($"Simulated disk read error at frame {frameIndex}");

            var pixels = new byte[64 * 64 * 4];
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            return Task.FromResult(new VideoFrame(64, 64, pixels, "rgba"));
        }
    }
}
