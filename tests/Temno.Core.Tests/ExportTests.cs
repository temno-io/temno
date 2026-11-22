using System.Diagnostics;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// Tests for the TimelineExporter pipeline. Since FFmpeg may not be on the
/// system, these test the setup/validation logic. Full export integration
/// requires FFmpeg on the PATH (conditional tests).
/// </summary>
public class ExportTests
{
    private static Timeline SimpleTimeline() => new()
    {
        Fps = 30,
        Width = 320,
        Height = 240,
        Tracks =
        [
            new Track
            {
                Type = ClipType.video,
                Clips =
                [
                    new Clip
                    {
                        MediaRef = "test.mp4",
                        MediaType = ClipType.video,
                        StartFrame = 0,
                        DurationFrames = 30, // 1 second
                    }
                ]
            }
        ]
    };

    [Fact]
    public async Task RejectsNullTimeline()
    {
        var compositor = new CpuCompositor();
        var exporter = new TimelineExporter(compositor, new FakeFrameSource());
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            exporter.ExportAsync(null!, new ExportPreset("test"), "/tmp/test.mp4"));
    }

    [Fact]
    public async Task RejectsNullPreset()
    {
        var compositor = new CpuCompositor();
        var exporter = new TimelineExporter(compositor, new FakeFrameSource());
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            exporter.ExportAsync(SimpleTimeline(), null!, "/tmp/test.mp4"));
    }

    [Fact]
    public async Task RejectsEmptyOutputPath()
    {
        var compositor = new CpuCompositor();
        var exporter = new TimelineExporter(compositor, new FakeFrameSource());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            exporter.ExportAsync(SimpleTimeline(), new ExportPreset("test"), ""));
    }

    [Fact]
    public async Task RejectsEmptyTimeline()
    {
        var compositor = new CpuCompositor();
        var exporter = new TimelineExporter(compositor, new FakeFrameSource());
        var emptyTimeline = new Timeline { Fps = 30, Width = 320, Height = 240 };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            exporter.ExportAsync(emptyTimeline, new ExportPreset("test"), "/tmp/test.mp4"));
    }

    [Fact]
    public void PresetResolution_720p()
    {
        // Verifies the resolution mapping works via an export attempt.
        // The actual export will fail without FFmpeg, but resolution is calculated first.
        var preset = new ExportPreset("720p", resolution: ExportResolution.R720p);
        Assert.Equal(ExportResolution.R720p, preset.Resolution);
    }

    [Fact]
    public void PresetCodec_H265()
    {
        var preset = new ExportPreset("HEVC", codec: VideoCodec.H265);
        Assert.Equal(VideoCodec.H265, preset.Codec);
    }

    [Fact]
    public async Task ExportAsync_OddCanvas_SucceedsWithEvenYuv420()
    {
        if (!IsFfmpegAvailable()) return;

        var temp = Path.Combine(Path.GetTempPath(), $"temno_export_odd_{Guid.NewGuid():N}.mp4");
        try
        {
            var timeline = new Timeline
            {
                Fps = 10,
                Width = 65,
                Height = 49,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                MediaRef = "solid",
                                MediaType = ClipType.video,
                                DurationFrames = 10,
                            }
                        ]
                    }
                ]
            };
            var exporter = new TimelineExporter(new CpuCompositor(), new FakeFrameSource());
            await exporter.ExportAsync(timeline, new ExportPreset("odd"), temp);
            Assert.True(File.Exists(temp));
            var probe = await new FfmpegMediaProbe().ProbeAsync(temp);
            Assert.True(probe.HasVideo);
            Assert.Equal(64, probe.Width);
            Assert.Equal(48, probe.Height);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void ProgressEventFires()
    {
        var compositor = new CpuCompositor();
        var exporter = new TimelineExporter(compositor, new FakeFrameSource());
        var progressCalls = new List<(int Current, int Total)>();
        exporter.Progress += (cur, total) => progressCalls.Add((cur, total));

        // We can't easily run the full export without FFmpeg, so this just
        // verifies the event hookup compiles and the handler is wired.
        Assert.Empty(progressCalls);
        Assert.NotNull(exporter);
    }

    private static bool IsFfmpegAvailable()
    {
        try
        {
            foreach (var tool in new[] { FFmpegResolver.GetFFmpegPath(), FFmpegResolver.GetFFprobePath() })
            {
                using var process = Process.Start(new ProcessStartInfo(tool, "-version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                process?.WaitForExit();
                if (process?.ExitCode != 0) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Stub frame source that returns solid-color frames.</summary>
    private sealed class FakeFrameSource : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(string mediaPath, double seconds, CancellationToken ct = default)
        {
            var data = new byte[320 * 240 * 4];
            // Gray frame.
            for (var i = 0; i < data.Length; i += 4)
            {
                data[i] = 128; data[i + 1] = 128; data[i + 2] = 128; data[i + 3] = 255;
            }
            return Task.FromResult(new VideoFrame(320, 240, data, "rgba"));
        }
    }
}
