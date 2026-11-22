using System.Diagnostics;
using Temno.Core.Models;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Playback;

namespace Temno.Media.Hardware;

/// <summary>
/// Lightweight encode timing for WP-114 performance receipts.
/// Renders a short synthetic timeline and measures video-pass wall time.
/// </summary>
public static class HardwareBenchmark
{
    public sealed class EncodeSample
    {
        public required string Encoder { get; init; }
        public required HardwareAccelerationMode Mode { get; init; }
        public required double VideoEncodeMilliseconds { get; init; }
        public required int Frames { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public string? Error { get; init; }
    }

    public static async Task<EncodeSample> MeasureEncodeAsync(
        HardwareAccelerationMode mode,
        VideoCodec codec = VideoCodec.H264,
        int frames = 30,
        int width = 320,
        int height = 240,
        CancellationToken cancellationToken = default)
    {
        var timeline = new Timeline
        {
            Fps = 30,
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
                            MediaRef = "bench",
                            MediaType = ClipType.video,
                            DurationFrames = frames,
                            Effects =
                            [
                                new Effect
                                {
                                    Type = "color.exposure",
                                    Params = new() { ["ev"] = new EffectParam { Value = 0.25 } },
                                },
                            ],
                        },
                    ],
                },
            ],
        };

        var rgba = new byte[width * height * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 40;
            rgba[i + 1] = 80;
            rgba[i + 2] = 160;
            rgba[i + 3] = 255;
        }

        var source = new SolidSource(new VideoFrame(width, height, rgba, "rgba"));
        using var exporter = new TimelineExporter(
            CompositorFactory.Create(),
            source,
            mediaResolver: null,
            acceleration: mode);
        var outPath = Path.Combine(Path.GetTempPath(), $"temno-bench-{Guid.NewGuid():N}.mp4");
        try
        {
            await exporter.ExportAsync(
                timeline,
                new ExportPreset("bench", codec, ExportResolution.MatchTimeline),
                outPath,
                cancellationToken).ConfigureAwait(false);

            return new EncodeSample
            {
                Encoder = exporter.LastEncoderName ?? "?",
                Mode = mode,
                VideoEncodeMilliseconds = exporter.LastVideoEncodeMilliseconds,
                Frames = frames,
                Width = width,
                Height = height,
            };
        }
        catch (Exception ex)
        {
            return new EncodeSample
            {
                Encoder = exporter.LastEncoderName ?? "?",
                Mode = mode,
                VideoEncodeMilliseconds = exporter.LastVideoEncodeMilliseconds,
                Frames = frames,
                Width = width,
                Height = height,
                Error = ex.Message,
            };
        }
        finally
        {
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { /* ignore */ }
        }
    }

    private sealed class SolidSource(VideoFrame frame) : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
        {
            var copy = new byte[frame.Data.Length];
            Buffer.BlockCopy(frame.Data, 0, copy, 0, copy.Length);
            return Task.FromResult(new VideoFrame(frame.Width, frame.Height, copy, frame.PixelFormat));
        }
    }
}
