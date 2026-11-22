using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media.Export;
using Xunit;

namespace Temno.Core.Tests;

public sealed class SmartRenderAnalyzerTests
{
    private static VideoStreamInfo CreateMatchingStreamInfo(
        string codecName = "h264",
        int width = 1920,
        int height = 1080,
        string pixelFormat = "yuv420p",
        double fps = 30.0,
        string? colorPrimaries = "bt709",
        string? colorTransfer = "bt709",
        string? colorSpace = "bt709",
        IReadOnlyList<double>? keyframes = null,
        double? packetEndSeconds = null,
        string? containerFormat = "mov,mp4,m4a,3gp,3g2,mj2")
    {
        keyframes ??= [0.0, 1.0, 2.0, 3.0];
        return new(
            CodecName: codecName,
            Profile: "High",
            Width: width,
            Height: height,
            PixelFormat: pixelFormat,
            FrameRate: fps,
            ColorPrimaries: colorPrimaries,
            ColorTransfer: colorTransfer,
            ColorSpace: colorSpace,
            ColorRange: "tv",
            KeyframeSeconds: keyframes,
            // No B-frames: decode order is display order, so these tests keep exercising
            // eligibility and head alignment. B-frame boundaries are SmartRenderBoundaryTests.
            Packets: SmartRenderPacketTables.ClosedGops(keyframes, endSeconds: packetEndSeconds ?? keyframes[^1] + 1.0, fps: (int)Math.Round(fps), bFrames: 0),
            // MP4, closed GOP: the export frame source seeks it exactly anywhere, so a copy may end
            // mid-GOP. Keyframe-only sources are SmartRenderBoundaryTests.
            ContainerFormat: containerFormat);
    }

    private static (Timeline Timeline, string TempDir, string FilePath) CreateSingleClipTimeline(
        int durationFrames = 60,
        int trimStartFrame = 0,
        int fps = 30,
        int width = 1920,
        int height = 1080,
        Action<Clip>? configureClip = null)
    {
        var tempDir = FfmpegFixture.CreateTempDirectory();
        var filePath = Path.Combine(tempDir, "clip.mp4");
        File.WriteAllBytes(filePath, []);

        var clip = new Clip
        {
            Id = "clip1",
            MediaRef = "clip1",
            MediaType = ClipType.video,
            SourceClipType = ClipType.video,
            StartFrame = 0,
            DurationFrames = durationFrames,
            TrimStartFrame = trimStartFrame,
            Opacity = 1.0,
            Speed = 1.0,
            Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 1.0, Height = 1.0, Rotation = 0 },
            Crop = new Crop { Left = 0, Top = 0, Right = 0, Bottom = 0 },
        };
        configureClip?.Invoke(clip);

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
                    Clips = [clip],
                },
            ],
        };

        return (timeline, tempDir, filePath);
    }

    private static MediaResolver CreateResolver(params (string Id, string Path)[] entries)
    {
        var manifest = new MediaManifest
        {
            Entries = entries.Select(e => new MediaManifestEntry
            {
                Id = e.Id,
                Type = ClipType.video,
                Source = MediaSource.External(e.Path),
            }).ToList(),
        };
        return new MediaResolver(() => manifest, () => null);
    }

    private static void AssertTiling(SmartRenderPlan plan, int expectedTotalFrames)
    {
        Assert.NotNull(plan);
        Assert.Equal(expectedTotalFrames, plan.TotalFrames);
        if (expectedTotalFrames == 0)
        {
            Assert.Empty(plan.Segments);
            Assert.Equal(0, plan.CopiedFrames);
            Assert.False(plan.HasCopySegments);
            return;
        }

        Assert.NotEmpty(plan.Segments);
        Assert.Equal(0, plan.Segments[0].StartFrame);

        var currentFrame = 0;
        var copied = 0;
        for (var i = 0; i < plan.Segments.Count; i++)
        {
            var seg = plan.Segments[i];
            Assert.True(seg.FrameCount > 0, $"Segment {i} has non-positive FrameCount: {seg.FrameCount}");
            Assert.Equal(currentFrame, seg.StartFrame);
            if (seg.Kind == SmartRenderSegmentKind.Copy)
            {
                Assert.False(string.IsNullOrEmpty(seg.SourcePath), $"Copy segment {i} has null or empty SourcePath");
                copied += seg.FrameCount;
            }
            currentFrame += seg.FrameCount;
        }

        Assert.Equal(expectedTotalFrames, currentFrame);
        Assert.Equal(copied, plan.CopiedFrames);
        Assert.Equal(copied > 0, plan.HasCopySegments);
    }

    /// <summary>
    /// The mux pass stamps BT.709 on the finished file unconditionally. A stream copy does not
    /// convert pixels, so an untagged SD source — conventionally BT.601 — would be silently
    /// relabelled BT.709 and every hue in it would shift. Untagged sources may only be copied at HD.
    /// </summary>
    [Fact]
    public async Task AnalyzeAsync_UntaggedSdSource_IsNotCopied()
    {
        var (timeline, tempDir, filePath) = CreateSingleClipTimeline(durationFrames: 60, width: 640, height: 480);
        try
        {
            var fakeProbe = new FakeVideoStreamProbe(CreateMatchingStreamInfo(
                width: 640, height: 480,
                colorPrimaries: null, colorTransfer: null, colorSpace: null,
                keyframes: [0.0, 1.0, 2.0]));
            var resolver = CreateResolver(("clip1", filePath));
            var analyzer = new SmartRenderAnalyzer(fakeProbe, resolver);

            var plan = await analyzer.AnalyzeAsync(timeline, VideoCodec.H264, 640, 480, 60);

            AssertTiling(plan, 60);
            Assert.False(plan.HasCopySegments);
            Assert.Equal(0, plan.CopiedFrames);
            Assert.Contains("untagged", plan.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    /// <summary>An untagged HD source resolves to BT.709 by the same convention, so it stays eligible.</summary>
    [Fact]
    public async Task AnalyzeAsync_UntaggedHdSource_IsStillCopied()
    {
        var (timeline, tempDir, filePath) = CreateSingleClipTimeline(durationFrames: 60);
        try
        {
            var fakeProbe = new FakeVideoStreamProbe(CreateMatchingStreamInfo(
                colorPrimaries: null, colorTransfer: null, colorSpace: null,
                keyframes: [0.0, 1.0, 2.0]));
            var resolver = CreateResolver(("clip1", filePath));
            var analyzer = new SmartRenderAnalyzer(fakeProbe, resolver);

            var plan = await analyzer.AnalyzeAsync(timeline, VideoCodec.H264, 1920, 1080, 60);

            AssertTiling(plan, 60);
            Assert.True(plan.HasCopySegments);
            Assert.Equal(60, plan.CopiedFrames);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    /// <summary>An explicitly BT.601-tagged source must never be copied and relabelled.</summary>
    [Fact]
    public async Task AnalyzeAsync_ExplicitBt601Source_IsNotCopied()
    {
        var (timeline, tempDir, filePath) = CreateSingleClipTimeline(durationFrames: 60);
        try
        {
            var fakeProbe = new FakeVideoStreamProbe(CreateMatchingStreamInfo(
                colorPrimaries: "smpte170m", colorTransfer: "smpte170m", colorSpace: "smpte170m",
                keyframes: [0.0, 1.0, 2.0]));
            var resolver = CreateResolver(("clip1", filePath));
            var analyzer = new SmartRenderAnalyzer(fakeProbe, resolver);

            var plan = await analyzer.AnalyzeAsync(timeline, VideoCodec.H264, 1920, 1080, 60);

            AssertTiling(plan, 60);
            Assert.False(plan.HasCopySegments);
            Assert.Contains("BT.709", plan.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_SingleFullLengthMatchingClip_ProducesOneCopySegment()
    {
        var (timeline, tempDir, filePath) = CreateSingleClipTimeline(durationFrames: 60);
        try
        {
            var fakeProbe = new FakeVideoStreamProbe(CreateMatchingStreamInfo(keyframes: [0.0, 1.0, 2.0]));
            var resolver = CreateResolver(("clip1", filePath));
            var analyzer = new SmartRenderAnalyzer(fakeProbe, resolver);

            var plan = await analyzer.AnalyzeAsync(timeline, VideoCodec.H264, 1920, 1080, 60);

            AssertTiling(plan, 60);
            Assert.True(plan.HasCopySegments);
            Assert.Equal(60, plan.CopiedFrames);
            var seg = Assert.Single(plan.Segments);
            Assert.Equal(SmartRenderSegmentKind.Copy, seg.Kind);
            Assert.Equal(0, seg.StartFrame);
            Assert.Equal(60, seg.FrameCount);
            Assert.Equal(filePath, seg.SourcePath);
            Assert.Equal(0.0, seg.SourceStartSeconds);
            Assert.Contains("copying 60 of 60 frames", plan.Reason);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_MatchingClipWithUnalignedHead_SplitsIntoEncodeAndCopy()
    {
        // TrimStartFrame = 15 frames (0.5s at 30 fps). Keyframes at 0.0s and 1.0s (frame 30 in source).
        // First keyframe >= 0.5 - 0.0166s is 1.0s.
        // Copy starts at 0 + (1.0 - 0.5) * 30 = frame 15.
        // Copy count = 60 - 15 = 45 frames (>= MinCopyFrames 30).
        var (timeline, tempDir, filePath) = CreateSingleClipTimeline(durationFrames: 60, trimStartFrame: 15);
        try
        {
            var fakeProbe = new FakeVideoStreamProbe(CreateMatchingStreamInfo(keyframes: [0.0, 1.0, 2.0]));
            var resolver = CreateResolver(("clip1", filePath));
            var analyzer = new SmartRenderAnalyzer(fakeProbe, resolver);

            var plan = await analyzer.AnalyzeAsync(timeline, VideoCodec.H264, 1920, 1080, 60);

            AssertTiling(plan, 60);
            Assert.True(plan.HasCopySegments);
            Assert.Equal(45, plan.CopiedFrames);
            Assert.Equal(2, plan.Segments.Count);

            Assert.Equal(SmartRenderSegmentKind.Encode, plan.Segments[0].Kind);
            Assert.Equal(0, plan.Segments[0].StartFrame);
            Assert.Equal(15, plan.Segments[0].FrameCount);

            Assert.Equal(SmartRenderSegmentKind.Copy, plan.Segments[1].Kind);
            Assert.Equal(15, plan.Segments[1].StartFrame);
            Assert.Equal(45, plan.Segments[1].FrameCount);
            Assert.Equal(filePath, plan.Segments[1].SourcePath);
            Assert.Equal(1.0, plan.Segments[1].SourceStartSeconds);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_ClipCarryingEffect_ForcesEncodeAndNamesEffectInReason()
    {
        var (timeline, tempDir, filePath) = CreateSingleClipTimeline(
            durationFrames: 60,
            configureClip: clip =>
            {
                clip.Effects =
                [
                    new Effect
                    {
                        Type = "color.exposure",
                        Enabled = true,
                        Params = new() { ["ev"] = new EffectParam { Value = 1 } },
                    },
                ];
            });

        try
        {
            var fakeProbe = new FakeVideoStreamProbe(CreateMatchingStreamInfo());
            var resolver = CreateResolver(("clip1", filePath));
            var analyzer = new SmartRenderAnalyzer(fakeProbe, resolver);

            var plan = await analyzer.AnalyzeAsync(timeline, VideoCodec.H264, 1920, 1080, 60);

            AssertTiling(plan, 60);
            Assert.False(plan.HasCopySegments);
            var seg = Assert.Single(plan.Segments);
            Assert.Equal(SmartRenderSegmentKind.Encode, seg.Kind);
            Assert.Equal(0, seg.StartFrame);
            Assert.Equal(60, seg.FrameCount);
            Assert.Contains("color.exposure", plan.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Theory]
    [InlineData("opacity")]
    [InlineData("blendMode")]
    [InlineData("rotation")]
    [InlineData("position")]
    [InlineData("scale")]
    [InlineData("flipH")]
    [InlineData("flipV")]
    [InlineData("speed")]
    [InlineData("crop")]
    public async Task AnalyzeAsync_IneligibleClipProperties_ForcesEncode(string variant)
    {
        var (timeline, tempDir, filePath) = CreateSingleClipTimeline(
            durationFrames: 60,
            configureClip: clip =>
            {
                switch (variant)
                {
                    case "opacity":
                        clip.Opacity = 0.75;
                        break;
                    case "blendMode":
                        clip.BlendMode = "multiply";
                        break;
                    case "rotation":
                        clip.Transform.Rotation = 15.0;
                        break;
                    case "position":
                        clip.Transform.CenterX = 0.6;
                        break;
                    case "scale":
                        clip.Transform.Width = 0.8;
                        break;
                    case "flipH":
                        clip.Transform.FlipHorizontal = true;
                        break;
                    case "flipV":
                        clip.Transform.FlipVertical = true;
                        break;
                    case "speed":
                        clip.Speed = 1.25;
                        break;
                    case "crop":
                        clip.Crop = new Crop { Left = 0.1 };
                        break;
                }
            });

        try
        {
            var fakeProbe = new FakeVideoStreamProbe(CreateMatchingStreamInfo());
            var resolver = CreateResolver(("clip1", filePath));
            var analyzer = new SmartRenderAnalyzer(fakeProbe, resolver);

            var plan = await analyzer.AnalyzeAsync(timeline, VideoCodec.H264, 1920, 1080, 60);

            AssertTiling(plan, 60);
            Assert.False(plan.HasCopySegments);
            Assert.Equal(0, plan.CopiedFrames);
            var seg = Assert.Single(plan.Segments);
            Assert.Equal(SmartRenderSegmentKind.Encode, seg.Kind);
            Assert.Equal(60, seg.FrameCount);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_TransitionWindow_EncodesTransitionAndCopiesSurrounding()
    {
        var tempDir = FfmpegFixture.CreateTempDirectory();
        var fileA = Path.Combine(tempDir, "clipA.mp4");
        var fileB = Path.Combine(tempDir, "clipB.mp4");
        File.WriteAllBytes(fileA, []);
        File.WriteAllBytes(fileB, []);

        try
        {
            var clipA = new Clip
            {
                Id = "clipA",
                MediaRef = "clipA",
                MediaType = ClipType.video,
                SourceClipType = ClipType.video,
                StartFrame = 0,
                DurationFrames = 60,
                TrimStartFrame = 0,
            };
            var clipB = new Clip
            {
                Id = "clipB",
                MediaRef = "clipB",
                MediaType = ClipType.video,
                SourceClipType = ClipType.video,
                StartFrame = 60,
                DurationFrames = 60,
                TrimStartFrame = 0,
            };

            var timeline = new Timeline
            {
                Fps = 30,
                Width = 1920,
                Height = 1080,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips = [clipA, clipB],
                    },
                ],
                Transitions =
                [
                    new Transition
                    {
                        ClipIdA = "clipA",
                        ClipIdB = "clipB",
                        DurationFrames = 20,
                    },
                ],
            };

            // Transition window: ClipB starts at 60, duration 20 -> [50, 70)
            // Clip A frames [0, 50) -> 50 frames (>= 30) -> Copy from fileA
            // Transition frames [50, 70) -> 20 frames -> Encode
            // Clip B frames [70, 120) -> 50 frames (>= 30). For Clip B, timeline frame 70 is local frame 10 (10/30s = 0.3333s).
            // Keyframe at 10/30s allows GOP alignment at frame 70.
            var fakeProbe = new FakeVideoStreamProbe();
            // Each packet table must cover every source frame its clip shows (A: [0, 50), B: [10, 60)):
            // a copy is proven only over packets that exist.
            fakeProbe.SetProbe(fileA, CreateMatchingStreamInfo(keyframes: [0.0], packetEndSeconds: 2.0));
            fakeProbe.SetProbe(fileB, CreateMatchingStreamInfo(keyframes: [0.0, 10.0 / 30.0], packetEndSeconds: 2.0));

            var resolver = CreateResolver(("clipA", fileA), ("clipB", fileB));
            var analyzer = new SmartRenderAnalyzer(fakeProbe, resolver);

            var plan = await analyzer.AnalyzeAsync(timeline, VideoCodec.H264, 1920, 1080, 120);

            AssertTiling(plan, 120);
            Assert.True(plan.HasCopySegments);
            Assert.Equal(100, plan.CopiedFrames);
            Assert.Equal(3, plan.Segments.Count);

            Assert.Equal(SmartRenderSegmentKind.Copy, plan.Segments[0].Kind);
            Assert.Equal(0, plan.Segments[0].StartFrame);
            Assert.Equal(50, plan.Segments[0].FrameCount);
            Assert.Equal(fileA, plan.Segments[0].SourcePath);

            Assert.Equal(SmartRenderSegmentKind.Encode, plan.Segments[1].Kind);
            Assert.Equal(50, plan.Segments[1].StartFrame);
            Assert.Equal(20, plan.Segments[1].FrameCount);

            Assert.Equal(SmartRenderSegmentKind.Copy, plan.Segments[2].Kind);
            Assert.Equal(70, plan.Segments[2].StartFrame);
            Assert.Equal(50, plan.Segments[2].FrameCount);
            Assert.Equal(fileB, plan.Segments[2].SourcePath);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Theory]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("fps")]
    [InlineData("pix_fmt")]
    [InlineData("codec")]
    [InlineData("color")]
    public async Task AnalyzeAsync_SourceMismatches_ForcesEncode(string mismatch)
    {
        var (timeline, tempDir, filePath) = CreateSingleClipTimeline(durationFrames: 60);
        try
        {
            var info = mismatch switch
            {
                "width" => CreateMatchingStreamInfo(width: 1280),
                "height" => CreateMatchingStreamInfo(height: 720),
                "fps" => CreateMatchingStreamInfo(fps: 24.0),
                "pix_fmt" => CreateMatchingStreamInfo(pixelFormat: "yuv422p"),
                "codec" => CreateMatchingStreamInfo(codecName: "hevc"),
                "color" => CreateMatchingStreamInfo(colorPrimaries: "bt2020"),
                _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
            };

            var fakeProbe = new FakeVideoStreamProbe(info);
            var resolver = CreateResolver(("clip1", filePath));
            var analyzer = new SmartRenderAnalyzer(fakeProbe, resolver);

            var plan = await analyzer.AnalyzeAsync(timeline, VideoCodec.H264, 1920, 1080, 60);

            AssertTiling(plan, 60);
            Assert.False(plan.HasCopySegments);
            Assert.Equal(0, plan.CopiedFrames);
            var seg = Assert.Single(plan.Segments);
            Assert.Equal(SmartRenderSegmentKind.Encode, seg.Kind);
            Assert.Equal(60, seg.FrameCount);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Theory]
    [InlineData(VideoCodec.ProRes, "ProRes")]
    [InlineData(VideoCodec.HevcHdr, "HDR")]
    public async Task AnalyzeAsync_ProResAndHevcHdrTargets_ForcesEncodeWithReason(VideoCodec codec, string expectedKeyword)
    {
        var (timeline, tempDir, filePath) = CreateSingleClipTimeline(durationFrames: 60);
        try
        {
            var fakeProbe = new FakeVideoStreamProbe(CreateMatchingStreamInfo());
            var resolver = CreateResolver(("clip1", filePath));
            var analyzer = new SmartRenderAnalyzer(fakeProbe, resolver);

            var plan = await analyzer.AnalyzeAsync(timeline, codec, 1920, 1080, 60);

            AssertTiling(plan, 60);
            Assert.False(plan.HasCopySegments);
            var seg = Assert.Single(plan.Segments);
            Assert.Equal(SmartRenderSegmentKind.Encode, seg.Kind);
            Assert.Equal(60, seg.FrameCount);
            Assert.Contains(expectedKeyword, plan.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public async Task AnalyzeAsync_CopyRunShorterThanMinCopyFrames_ForcesEncode()
    {
        // 25 frames < MinCopyFrames (30)
        var (timeline, tempDir, filePath) = CreateSingleClipTimeline(durationFrames: 25);
        try
        {
            var fakeProbe = new FakeVideoStreamProbe(CreateMatchingStreamInfo(keyframes: [0.0]));
            var resolver = CreateResolver(("clip1", filePath));
            var analyzer = new SmartRenderAnalyzer(fakeProbe, resolver);

            var plan = await analyzer.AnalyzeAsync(timeline, VideoCodec.H264, 1920, 1080, 25);

            AssertTiling(plan, 25);
            Assert.False(plan.HasCopySegments);
            var seg = Assert.Single(plan.Segments);
            Assert.Equal(SmartRenderSegmentKind.Encode, seg.Kind);
            Assert.Equal(25, seg.FrameCount);
            Assert.Contains("30", plan.Reason);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(tempDir);
        }
    }

    [Fact]
    public void SmartRenderPlan_Validate_CatchesTilingViolations()
    {
        // Gap: 0..10, 12..20
        Assert.Throws<InvalidOperationException>(() =>
            SmartRenderPlan.Validate(
                [
                    new SmartRenderSegment(SmartRenderSegmentKind.Encode, 0, 10),
                    new SmartRenderSegment(SmartRenderSegmentKind.Encode, 12, 8),
                ],
                20));

        // Overlap: 0..15, 10..20
        Assert.Throws<InvalidOperationException>(() =>
            SmartRenderPlan.Validate(
                [
                    new SmartRenderSegment(SmartRenderSegmentKind.Encode, 0, 15),
                    new SmartRenderSegment(SmartRenderSegmentKind.Encode, 10, 10),
                ],
                20));

        // Doesn't start at 0: 5..20
        Assert.Throws<InvalidOperationException>(() =>
            SmartRenderPlan.Validate(
                [
                    new SmartRenderSegment(SmartRenderSegmentKind.Encode, 5, 15),
                ],
                20));

        // Sum != TotalFrames: 0..15 for totalFrames = 20
        Assert.Throws<InvalidOperationException>(() =>
            SmartRenderPlan.Validate(
                [
                    new SmartRenderSegment(SmartRenderSegmentKind.Encode, 0, 15),
                ],
                20));

        // Negative frame count
        Assert.Throws<InvalidOperationException>(() =>
            SmartRenderPlan.Validate(
                [
                    new SmartRenderSegment(SmartRenderSegmentKind.Encode, 0, -5),
                ],
                20));

        // Copy segment missing SourcePath
        Assert.Throws<InvalidOperationException>(() =>
            SmartRenderPlan.Validate(
                [
                    new SmartRenderSegment(SmartRenderSegmentKind.Copy, 0, 20, SourcePath: null),
                ],
                20));
    }

    [Fact]
    public async Task FfmpegStreamProbe_RealGeneratedClip_ProbesMetadataAndKeyframes()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var media = Path.Combine(directory, "probe_test.mp4");
            await FfmpegFixture.WriteTestClipAsync(media, fps: 30, seconds: 2, width: 320, height: 240);

            var probe = new FfmpegStreamProbe();
            var info = await probe.ProbeAsync(media);

            Assert.Equal("h264", info.CodecName);
            Assert.Equal(320, info.Width);
            Assert.Equal(240, info.Height);
            Assert.Equal("yuv420p", info.PixelFormat);
            Assert.InRange(info.FrameRate, 29.9, 30.1);
            Assert.NotEmpty(info.KeyframeSeconds);

            // Assert strictly ascending keyframe timestamps
            for (var i = 1; i < info.KeyframeSeconds.Count; i++)
            {
                Assert.True(info.KeyframeSeconds[i] > info.KeyframeSeconds[i - 1],
                    $"Keyframe {i} ({info.KeyframeSeconds[i]}) is not greater than previous ({info.KeyframeSeconds[i - 1]})");
            }

            // WP-234: the decode-order packet table every stream copy is proven against.
            Assert.NotNull(info.Packets);
            Assert.Equal(60, info.Packets.Count);
            Assert.True(info.Packets[0].IsKeyframe);
            Assert.Equal(0.0, info.KeyframeSeconds[0], 3);
            Assert.Contains("mp4", info.ContainerFormat, StringComparison.Ordinal);

            // Assert caching returns identical instance
            var cachedInfo = await probe.ProbeAsync(media);
            Assert.Same(info, cachedInfo);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    private sealed class FakeVideoStreamProbe : IVideoStreamProbe
    {
        private readonly Dictionary<string, VideoStreamInfo> _probes = new(StringComparer.OrdinalIgnoreCase);
        private readonly VideoStreamInfo? _defaultInfo;

        public FakeVideoStreamProbe(VideoStreamInfo? defaultInfo = null)
        {
            _defaultInfo = defaultInfo;
        }

        public void SetProbe(string path, VideoStreamInfo info)
        {
            _probes[path] = info;
        }

        public Task<VideoStreamInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
        {
            if (_probes.TryGetValue(path, out var info))
                return Task.FromResult(info);

            if (_defaultInfo != null)
                return Task.FromResult(_defaultInfo);

            throw new FileNotFoundException("No fake probe info registered for path", path);
        }
    }
}
