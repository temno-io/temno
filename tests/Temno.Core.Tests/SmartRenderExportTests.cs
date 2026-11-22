using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Hardware;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// End-to-end integration tests for the smart-render export pipeline.
/// Covers frame-exact stream copy, lossless preservation, mixed plan execution,
/// conventional vs smart equivalence, verification gate fallback, and ineligible timeline exports.
/// </summary>
[Collection(SmartRenderMediaCollection.Name)]
public sealed class SmartRenderExportTests
{
    private static async Task WriteGopAlignedClipAsync(
        string path,
        int fps = 30,
        int seconds = 6,
        int width = 320,
        int height = 240,
        int gop = 30)
    {
        // The colour description must be set with `setparams` on the filtergraph, not with
        // output-side -color_primaries/-color_trc flags. Since FFmpeg 7 the encoder takes its
        // description from the frame the filtergraph produces, so those flags are silently
        // dropped — measured here: they set colorspace and range but leave primaries and transfer
        // `unknown`, which is what the exporter itself was fixed for (RENDER_AUDIT §2.4).
        // A partially untagged clip is ineligible for smart rendering, so getting this wrong makes
        // every copy test silently exercise the conventional re-encode path instead.
        var args = $"-hide_banner -loglevel error -y -f lavfi "
                 + $"-i testsrc2=size={width}x{height}:rate={fps}:duration={seconds} "
                 + $"-vf setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv "
                 + $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p -g {gop} -keyint_min {gop} -sc_threshold 0 -bf 0 "
                 + $"\"{path}\"";
        await FfmpegFixture.RunAsync(args).ConfigureAwait(false);
    }

    private static async Task<int> CountVideoFramesAsync(string path)
    {
        var args = $"-v error -select_streams v:0 -count_frames -show_entries stream=nb_read_frames -of csv=p=0 \"{path}\"";
        using var process = Process.Start(new ProcessStartInfo(FFmpegResolver.GetFFprobePath(), args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start ffprobe.");

        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe frame count failed: exit {process.ExitCode}");

        return int.Parse(stdout.Trim(), CultureInfo.InvariantCulture);
    }

    private static (Timeline Timeline, MediaResolver Resolver) CreateTimeline(
        int width = 320,
        int height = 240,
        int fps = 30,
        params (string ClipId, string MediaPath, int StartFrame, int DurationFrames, int TrimStartFrame, Action<Clip>? Configure)[] clips)
    {
        var manifest = new MediaManifest
        {
            Entries = clips.Select(c => new MediaManifestEntry
            {
                Id = c.ClipId,
                Type = ClipType.video,
                Source = MediaSource.External(c.MediaPath),
            }).ToList(),
        };
        var resolver = new MediaResolver(() => manifest, () => null);

        var timelineClips = new List<Clip>();
        foreach (var c in clips)
        {
            var clip = new Clip
            {
                Id = c.ClipId,
                MediaRef = c.ClipId,
                MediaType = ClipType.video,
                SourceClipType = ClipType.video,
                StartFrame = c.StartFrame,
                DurationFrames = c.DurationFrames,
                TrimStartFrame = c.TrimStartFrame,
                Opacity = 1.0,
                Speed = 1.0,
                Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 1.0, Height = 1.0, Rotation = 0 },
                Crop = new Crop { Left = 0, Top = 0, Right = 0, Bottom = 0 },
            };
            c.Configure?.Invoke(clip);
            timelineClips.Add(clip);
        }

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
                    Clips = timelineClips,
                },
            ],
        };

        return (timeline, resolver);
    }

    [Fact]
    public async Task ExportAsync_FrameExactCopy_ProducesExactFrameCount()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var tempDir = FfmpegFixture.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source_180.mp4");
            var outputPath = Path.Combine(tempDir, "export_exact.mp4");

            // 6s @ 30fps = 180 frames with keyframes every 30 frames
            await WriteGopAlignedClipAsync(sourcePath, fps: 30, seconds: 6, width: 320, height: 240, gop: 30);

            var (timeline, resolver) = CreateTimeline(
                320, 240, 30,
                ("clip1", sourcePath, 0, 180, 0, null));

            using var frameSource = new ExportFrameSource();
            using var exporter = new TimelineExporter(
                new CpuCompositor(),
                frameSource,
                resolver,
                acceleration: HardwareAccelerationMode.Software);

            await exporter.ExportAsync(timeline, new ExportPreset("test", codec: VideoCodec.H264), outputPath);

            Assert.NotNull(exporter.LastSmartRenderPlan);
            Assert.True(exporter.LastSmartRenderPlan.HasCopySegments);
            Assert.Equal(180, exporter.LastSmartRenderPlan.CopiedFrames);
            Assert.Null(exporter.LastSmartRenderFallbackReason);
            Assert.True(File.Exists(outputPath));

            var actualFrames = await CountVideoFramesAsync(outputPath);
            Assert.Equal(180, actualFrames);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExportAsync_LosslessCopy_DecodesByteIdenticalToSource()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var tempDir = FfmpegFixture.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source_lossless.mp4");
            var outputPath = Path.Combine(tempDir, "export_lossless.mp4");

            await WriteGopAlignedClipAsync(sourcePath, fps: 30, seconds: 6, width: 320, height: 240, gop: 30);

            var (timeline, resolver) = CreateTimeline(
                320, 240, 30,
                ("clip1", sourcePath, 0, 180, 0, null));

            using var frameSource = new ExportFrameSource();
            using var exporter = new TimelineExporter(
                new CpuCompositor(),
                frameSource,
                resolver,
                acceleration: HardwareAccelerationMode.Software);

            await exporter.ExportAsync(timeline, new ExportPreset("test", codec: VideoCodec.H264), outputPath);

            Assert.NotNull(exporter.LastSmartRenderPlan);
            Assert.True(exporter.LastSmartRenderPlan.HasCopySegments);

            // Decode frames from source and exported file, verifying exact byte equality
            using var srcDecoder = new ExportFrameSource();
            using var expDecoder = new ExportFrameSource();

            for (var frame = 0; frame < 180; frame++)
            {
                using var srcFrame = await srcDecoder.GetFrameAsync(sourcePath, frame / 30.0);
                using var expFrame = await expDecoder.GetFrameAsync(outputPath, frame / 30.0);

                Assert.Equal(srcFrame.Width, expFrame.Width);
                Assert.Equal(srcFrame.Height, expFrame.Height);
                Assert.Equal(srcFrame.Data, expFrame.Data);
            }
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExportAsync_MixedPlan_AppliesEffectToMiddleAndCopiesEnds()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var tempDir = FfmpegFixture.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source_mixed.mp4");
            var outputPath = Path.Combine(tempDir, "export_mixed.mp4");

            await WriteGopAlignedClipAsync(sourcePath, fps: 30, seconds: 6, width: 320, height: 240, gop: 30);

            // Three 60-frame segments (total 180 frames):
            // Clip 1: 0..60 untouched (copy)
            // Clip 2: 60..120 with color effect (encode)
            // Clip 3: 120..180 untouched (copy)
            var (timeline, resolver) = CreateTimeline(
                320, 240, 30,
                ("clip1", sourcePath, 0, 60, 0, null),
                ("clip2", sourcePath, 60, 60, 60, clip =>
                {
                    clip.Effects =
                    [
                        new Effect
                        {
                            Type = "color.exposure",
                            Enabled = true,
                            Params = new() { ["ev"] = new EffectParam { Value = 2 } },
                        },
                    ];
                }),
                ("clip3", sourcePath, 120, 60, 120, null));

            using var frameSource = new ExportFrameSource();
            using var exporter = new TimelineExporter(
                new CpuCompositor(),
                frameSource,
                resolver,
                acceleration: HardwareAccelerationMode.Software);

            await exporter.ExportAsync(timeline, new ExportPreset("test", codec: VideoCodec.H264), outputPath);

            var plan = exporter.LastSmartRenderPlan;
            Assert.NotNull(plan);
            Assert.Equal(3, plan.Segments.Count);
            Assert.Equal(SmartRenderSegmentKind.Copy, plan.Segments[0].Kind);
            Assert.Equal(SmartRenderSegmentKind.Encode, plan.Segments[1].Kind);
            Assert.Equal(SmartRenderSegmentKind.Copy, plan.Segments[2].Kind);
            Assert.Equal(120, plan.CopiedFrames);
            Assert.Null(exporter.LastSmartRenderFallbackReason);

            var actualFrames = await CountVideoFramesAsync(outputPath);
            Assert.Equal(180, actualFrames);

            // Copied segments match source byte-for-byte; middle encoded segment differs
            using var srcDecoder = new ExportFrameSource();
            using var expDecoder = new ExportFrameSource();

            // Verify head copied segment (0..59) matches source byte-for-byte
            for (var frame = 0; frame < 60; frame++)
            {
                using var srcFrame = await srcDecoder.GetFrameAsync(sourcePath, frame / 30.0);
                using var expFrame = await expDecoder.GetFrameAsync(outputPath, frame / 30.0);
                Assert.Equal(srcFrame.Data, expFrame.Data);
            }

            // Verify middle segment (60..119) has the effect visibly applied (pixels differ from source)
            for (var frame = 60; frame < 120; frame++)
            {
                using var srcFrame = await srcDecoder.GetFrameAsync(sourcePath, frame / 30.0);
                using var expFrame = await expDecoder.GetFrameAsync(outputPath, frame / 30.0);
                Assert.NotEqual(srcFrame.Data, expFrame.Data);
            }

            // Verify tail copied segment (120..179) frames are present and valid
            using var tailFrame = await expDecoder.GetFrameAsync(outputPath, 140 / 30.0);
            Assert.NotNull(tailFrame);
            Assert.Equal(320, tailFrame.Width);
            Assert.Equal(240, tailFrame.Height);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExportAsync_Equivalence_SmartAndConventionalProduceSameDurationAndFrames()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var tempDir = FfmpegFixture.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source_equiv.mp4");
            var outputSmart = Path.Combine(tempDir, "export_smart.mp4");
            var outputConv = Path.Combine(tempDir, "export_conv.mp4");

            await WriteGopAlignedClipAsync(sourcePath, fps: 30, seconds: 6, width: 320, height: 240, gop: 30);

            var (timeline, resolver) = CreateTimeline(
                320, 240, 30,
                ("clip1", sourcePath, 0, 180, 0, null));

            // Smart render
            using (var fs = new ExportFrameSource())
            using (var exporterSmart = new TimelineExporter(new CpuCompositor(), fs, resolver, acceleration: HardwareAccelerationMode.Software))
            {
                exporterSmart.DisableSmartRender = false;
                await exporterSmart.ExportAsync(timeline, new ExportPreset("test", codec: VideoCodec.H264), outputSmart);
                Assert.NotNull(exporterSmart.LastSmartRenderPlan);
                Assert.True(exporterSmart.LastSmartRenderPlan.HasCopySegments);
            }

            // Conventional render
            using (var fs = new ExportFrameSource())
            using (var exporterConv = new TimelineExporter(new CpuCompositor(), fs, resolver, acceleration: HardwareAccelerationMode.Software))
            {
                exporterConv.DisableSmartRender = true;
                await exporterConv.ExportAsync(timeline, new ExportPreset("test", codec: VideoCodec.H264), outputConv);
                Assert.Null(exporterConv.LastSmartRenderPlan);
            }

            var framesSmart = await CountVideoFramesAsync(outputSmart);
            var framesConv = await CountVideoFramesAsync(outputConv);
            Assert.Equal(180, framesSmart);
            Assert.Equal(180, framesConv);

            var probeSmart = await new FfmpegMediaProbe().ProbeAsync(outputSmart);
            var probeConv = await new FfmpegMediaProbe().ProbeAsync(outputConv);
            Assert.InRange(Math.Abs(probeSmart.DurationSeconds - probeConv.DurationSeconds), 0, 0.05);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExportAsync_VerificationFailure_FallsBackToConventionalEncode()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var tempDir = FfmpegFixture.CreateTempDirectory();
        try
        {
            var shortSource = Path.Combine(tempDir, "short_source.mp4");
            var outputPath = Path.Combine(tempDir, "export_fallback.mp4");

            // Write a 2-second clip (60 frames)
            await WriteGopAlignedClipAsync(shortSource, fps: 30, seconds: 2, width: 320, height: 240, gop: 30);

            // Timeline requests 180 frames (6 seconds) from this source.
            // We inject a FakeVideoStreamProbe claiming the source has 180 frames with keyframes at 0, 30, etc.,
            // and a packet table to match, so SmartRenderAnalyzer proves and plans a 180-frame copy.
            // When ffmpeg copies -frames:v 180 from the 60-frame source, it only produces 60 frames.
            // The segment gate discovers 60 packets != 180 frames and triggers fallback.
            // Conventional encode renders all 180 frames (holding the last frame past end of media).
            var fakeProbe = new FakeProbe(new VideoStreamInfo(
                CodecName: "h264",
                Profile: "High",
                Width: 320,
                Height: 240,
                PixelFormat: "yuv420p",
                FrameRate: 30.0,
                ColorPrimaries: "bt709",
                ColorTransfer: "bt709",
                ColorSpace: "bt709",
                ColorRange: "tv",
                KeyframeSeconds: [0.0, 1.0, 2.0, 3.0, 4.0, 5.0],
                Packets: SmartRenderPacketTables.ClosedGops([0.0, 1.0, 2.0, 3.0, 4.0, 5.0], endSeconds: 6.0, fps: 30, bFrames: 0),
                ContainerFormat: "mov,mp4,m4a,3gp,3g2,mj2"));

            var (timeline, resolver) = CreateTimeline(
                320, 240, 30,
                ("clip1", shortSource, 0, 180, 0, null));

            using var frameSource = new ExportFrameSource();
            using var exporter = new TimelineExporter(
                new CpuCompositor(),
                frameSource,
                resolver,
                acceleration: HardwareAccelerationMode.Software,
                streamProbe: fakeProbe);

            await exporter.ExportAsync(timeline, new ExportPreset("test", codec: VideoCodec.H264), outputPath);

            Assert.True(File.Exists(outputPath));
            var actualFrames = await CountVideoFramesAsync(outputPath);
            Assert.Equal(180, actualFrames);
            Assert.True(exporter.LastSmartRenderPlan?.HasCopySegments, "the analyzer should have planned the copy");
            Assert.Contains("verification failed", exporter.LastSmartRenderFallbackReason, StringComparison.Ordinal);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ExportAsync_IneligibleTimelines_UntouchedAndExportCorrectly()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var tempDir = FfmpegFixture.CreateTempDirectory();
        try
        {
            var sourcePath = Path.Combine(tempDir, "source_ineligible.mp4");
            var proresOutput = Path.Combine(tempDir, "export_prores.mov");
            var transitionOutput = Path.Combine(tempDir, "export_trans.mp4");

            await WriteGopAlignedClipAsync(sourcePath, fps: 30, seconds: 4, width: 320, height: 240, gop: 30);

            // Case A: ProRes export target is ineligible for smart rendering
            var (timelineA, resolverA) = CreateTimeline(
                320, 240, 30,
                ("clipA", sourcePath, 0, 120, 0, null));

            using (var fsA = new ExportFrameSource())
            using (var exporterA = new TimelineExporter(new CpuCompositor(), fsA, resolverA, acceleration: HardwareAccelerationMode.Software))
            {
                await exporterA.ExportAsync(timelineA, new ExportPreset("prores", codec: VideoCodec.ProRes), proresOutput);
                Assert.NotNull(exporterA.LastSmartRenderPlan);
                Assert.False(exporterA.LastSmartRenderPlan.HasCopySegments);
                Assert.Contains("ProRes", exporterA.LastSmartRenderPlan.Reason, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(proresOutput));
                var framesA = await CountVideoFramesAsync(proresOutput);
                Assert.Equal(120, framesA);
            }

            // Case B: Timeline with a transition between clips
            var (timelineB, resolverB) = CreateTimeline(
                320, 240, 30,
                ("clip1", sourcePath, 0, 60, 0, null),
                ("clip2", sourcePath, 50, 60, 0, null));

            timelineB.Transitions =
            [
                new Transition
                {
                    ClipIdA = "clip1",
                    ClipIdB = "clip2",
                    DurationFrames = 20,
                },
            ];

            using (var fsB = new ExportFrameSource())
            using (var exporterB = new TimelineExporter(new CpuCompositor(), fsB, resolverB, acceleration: HardwareAccelerationMode.Software))
            {
                await exporterB.ExportAsync(timelineB, new ExportPreset("trans", codec: VideoCodec.H264), transitionOutput);
                Assert.True(File.Exists(transitionOutput));
                var framesB = await CountVideoFramesAsync(transitionOutput);
                Assert.Equal(timelineB.TotalFrames, framesB);
            }
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    private sealed class FakeProbe(VideoStreamInfo info) : IVideoStreamProbe
    {
        public Task<VideoStreamInfo> ProbeAsync(string path, System.Threading.CancellationToken cancellationToken = default) =>
            Task.FromResult(info);
    }
}
