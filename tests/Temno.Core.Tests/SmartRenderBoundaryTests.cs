using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Hardware;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-234: smart-render copy boundaries judged by decoded picture content, not frame counts.
/// <para>
/// The audited defect: a stream copy of N frames takes the first N packets in decode order, which
/// on a B-frame source is not the first N display frames. The 32-frame cut of an I B B P source
/// came back with 32 frames whose last one was source frame 33. Every test here that touches real
/// media decodes the output and checks which source frame each output frame shows.
/// </para>
/// </summary>
[Collection(SmartRenderMediaCollection.Name)]
public sealed class SmartRenderBoundaryTests
{
    // ---------------------------------------------------------------- the copy proof

    [Theory]
    [InlineData(30, 30)]
    [InlineData(31, 31)]
    [InlineData(32, 31)]   // the audit's case: copying 32 packets would show source frame 33 last
    [InlineData(34, 34)]
    [InlineData(35, 34)]
    [InlineData(44, 43)]
    public void ExactCopyLength_ClosedIpbbGops_StopsBeforeFramesReorderedPastTheCut(int requested, int exact)
    {
        var packets = SmartRenderPacketTables.ClosedGops([0.0, 1.0, 2.0], endSeconds: 3.0, fps: 30, bFrames: 2);

        Assert.Equal(exact, SmartRenderAnalyzer.ExactCopyLength(packets, keyPacket: 0, k0: 0.0, fps: 30, maxFrames: requested));
    }

    [Fact]
    public void ExactCopyLength_WithoutBFrames_CopiesAnyLength()
    {
        var packets = SmartRenderPacketTables.ClosedGops([0.0, 1.0, 2.0], endSeconds: 3.0, fps: 30, bFrames: 0);

        for (var requested = 1; requested <= 90; requested++)
            Assert.Equal(requested, SmartRenderAnalyzer.ExactCopyLength(packets, 0, 0.0, 30, requested));
    }

    [Fact]
    public void ExactCopyLength_OpenGopKeyframe_CopiesOnlyTheKeyframe()
    {
        // The keyframe at 1 s is followed in decode order by B-frames displayed before it, which
        // reference the previous GOP: from here a copy cannot even show its second frame.
        var packets = SmartRenderPacketTables.OpenGops(gop: 30, gops: 3, fps: 30, bFrames: 2);
        var keyAtOneSecond = packets.ToList().FindIndex(p => p.IsKeyframe && Math.Abs(p.PtsSeconds - 1.0) < 1e-9);

        Assert.Equal(1, SmartRenderAnalyzer.ExactCopyLength(packets, keyAtOneSecond, 1.0, 30, 60));
        // From the first keyframe the leading pictures' references are all inside the copy.
        Assert.Equal(58, SmartRenderAnalyzer.ExactCopyLength(packets, 0, 0.0, 30, 60));
    }

    [Fact]
    public void ExactCopyLength_StopsAtHiddenMissingOffGridOrRepeatedTimestamps()
    {
        var clean = SmartRenderPacketTables.ClosedGops([0.0, 1.0], endSeconds: 2.0, fps: 30, bFrames: 0).ToList();

        var hidden = clean.ToList();
        hidden[10] = hidden[10] with { IsDiscard = true };
        Assert.Equal(10, SmartRenderAnalyzer.ExactCopyLength(hidden, 0, 0.0, 30, 60));

        var missing = clean.ToList();
        missing[10] = missing[10] with { PtsSeconds = double.NaN };
        Assert.Equal(10, SmartRenderAnalyzer.ExactCopyLength(missing, 0, 0.0, 30, 60));

        var offGrid = clean.ToList();
        offGrid[10] = offGrid[10] with { PtsSeconds = 10.5 / 30 };
        Assert.Equal(10, SmartRenderAnalyzer.ExactCopyLength(offGrid, 0, 0.0, 30, 60));

        var repeated = clean.ToList();
        repeated[10] = repeated[10] with { PtsSeconds = 9.0 / 30 };
        Assert.Equal(10, SmartRenderAnalyzer.ExactCopyLength(repeated, 0, 0.0, 30, 60));
    }

    [Theory]
    [InlineData(29, 0)]
    [InlineData(30, 30)]
    [InlineData(43, 30)]
    [InlineData(60, 60)]
    [InlineData(89, 60)]
    public void KeyframeAlignedCopyLength_ClosedGops_EndsWhereTheNextKeyframeOpens(int maxFrames, int aligned)
    {
        var packets = SmartRenderPacketTables.ClosedGops([0.0, 1.0, 2.0], endSeconds: 3.0, fps: 30, bFrames: 2);

        Assert.Equal(aligned, SmartRenderAnalyzer.KeyframeAlignedCopyLength(packets, 0, 0.0, 30, maxFrames));
    }

    [Fact]
    public void KeyframeAlignedCopyLength_OpenGops_NeverAligns()
    {
        // Each keyframe's leading pictures are decoded after it, so no prefix ends just before one.
        var packets = SmartRenderPacketTables.OpenGops(gop: 30, gops: 4, fps: 30, bFrames: 2);

        Assert.Equal(0, SmartRenderAnalyzer.KeyframeAlignedCopyLength(packets, 0, 0.0, 30, 119));
    }

    [Fact]
    public void SeekInexactReason_OnlyClosedGopMp4AndMatroskaSeekExactlyAnywhere()
    {
        var closed = SmartRenderPacketTables.ClosedGops([0.0, 1.0], endSeconds: 2.0, fps: 30, bFrames: 2);
        var open = SmartRenderPacketTables.OpenGops(gop: 30, gops: 2, fps: 30, bFrames: 2);

        Assert.False(SmartRenderAnalyzer.HasLeadingPictures(closed));
        Assert.True(SmartRenderAnalyzer.HasLeadingPictures(open));
        Assert.Null(SmartRenderAnalyzer.SeekInexactReason(Info(closed, Mp4), closed));
        Assert.Null(SmartRenderAnalyzer.SeekInexactReason(Info(closed, "matroska,webm"), closed));
        Assert.Contains("mpegts", SmartRenderAnalyzer.SeekInexactReason(Info(closed, "mpegts"), closed), StringComparison.Ordinal);
        Assert.Contains("unknown", SmartRenderAnalyzer.SeekInexactReason(Info(closed, null), closed), StringComparison.Ordinal);
        Assert.Contains("open-GOP", SmartRenderAnalyzer.SeekInexactReason(Info(open, Mp4), open), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- plans

    [Theory]
    [InlineData(0, 32, "C0+31 E31+1")]
    [InlineData(0, 44, "C0+43 E43+1")]
    [InlineData(0, 60, "C0+60")]
    [InlineData(45, 99, "E0+15 C15+82 E97+2")]   // nonzero offset, head to the next keyframe, crosses GOPs
    [InlineData(45, 100, "E0+15 C15+85")]        // same cut ending on an anchor frame copies to the end
    public async Task AnalyzeAsync_BFrameSource_CopiesOnlyTheExactPart(int trim, int length, string expected)
    {
        var packets = SmartRenderPacketTables.ClosedGops([0.0, 1.0, 2.0, 3.0, 4.0, 5.0], endSeconds: 6.0, fps: 30, bFrames: 2);
        var plan = await AnalyzeSingleClipAsync(trim, length, packets);

        Assert.Equal(expected, Describe(plan));
        Assert.Equal(30, plan.Fps);
    }

    // MPEG-TS seeks exactly only to keyframes. Wherever the clip is still on screen after a copy,
    // the render there re-seeks it, so the copy is cut back to end where a keyframe opens.
    [Theory]
    [InlineData(0, 31, "C0+31")]                  // the exact copy reaches the clip's end: nothing re-seeks
    [InlineData(0, 32, "C0+30 E30+2")]
    [InlineData(0, 44, "C0+30 E30+14")]
    [InlineData(45, 99, "E0+15 C15+60 E75+24")]   // cut back from 82 frames to the keyframe at 4 s
    [InlineData(45, 100, "E0+15 C15+85")]
    public async Task AnalyzeAsync_KeyframeOnlySeekSource_EndsCopiesWhereAKeyframeOpens(int trim, int length, string expected)
    {
        var packets = SmartRenderPacketTables.ClosedGops([0.0, 1.0, 2.0, 3.0, 4.0, 5.0], endSeconds: 6.0, fps: 30, bFrames: 2);
        var plan = await AnalyzeSingleClipAsync(trim, length, packets, container: "mpegts");

        Assert.Equal(expected, Describe(plan));
    }

    [Theory]
    [InlineData(Mp4, "C0+31 E31+30")]
    [InlineData("mpegts", "C0+30 E30+31")]
    public async Task AnalyzeAsync_SplitClipContinuingTheSameMedia_ReseeksOnlyWhereExact(string container, string expected)
    {
        // v1 is the same file from where v0's copy ends; rendering it starts a fresh seek there.
        var packets = SmartRenderPacketTables.ClosedGops([0.0, 1.0, 2.0], endSeconds: 3.0, fps: 30, bFrames: 2);
        var plan = await AnalyzeClipsAsync([(0, 31, 0, false), (31, 30, 31, true)], packets, container);

        Assert.Equal(expected, Describe(plan));
    }

    [Fact]
    public async Task AnalyzeAsync_OpenGopSource_CopiesFromTheStartOnlyToTheClipsEnd()
    {
        var packets = SmartRenderPacketTables.OpenGops(gop: 30, gops: 4, fps: 30, bFrames: 2);

        // From the first keyframe the leading pictures' references are all inside the copy, and a
        // copy that runs to the clip's end leaves nothing to re-seek.
        Assert.Equal("C0+61", Describe(await AnalyzeSingleClipAsync(0, 61, packets)));

        // Ending earlier would re-seek onto a leading picture, which lands on the keyframe after it.
        var continuing = await AnalyzeSingleClipAsync(0, 60, packets);
        Assert.Equal("E0+60", Describe(continuing));
        Assert.Contains("open-GOP", continuing.Reason, StringComparison.Ordinal);

        var fromLeadingKeyframe = await AnalyzeSingleClipAsync(30, 60, packets);
        Assert.Equal("E0+60", Describe(fromLeadingKeyframe));
        Assert.Contains("exact for only 1 frames", fromLeadingKeyframe.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeAsync_WithoutPacketTable_NeverCopies()
    {
        var plan = await AnalyzeSingleClipAsync(0, 60, packets: null);

        Assert.False(plan.HasCopySegments);
        Assert.Contains("packet order", plan.Reason, StringComparison.Ordinal);
    }

    private const string Mp4 = "mov,mp4,m4a,3gp,3g2,mj2";

    private static Task<SmartRenderPlan> AnalyzeSingleClipAsync(
        int trim, int length, IReadOnlyList<VideoPacket>? packets, string? container = Mp4)
        => AnalyzeClipsAsync([(0, length, trim, false)], packets, container);

    private static async Task<SmartRenderPlan> AnalyzeClipsAsync(
        (int Start, int Duration, int Trim, bool ForceEncode)[] clips, IReadOnlyList<VideoPacket>? packets, string? container)
    {
        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var media = Path.Combine(directory, "clip.mp4");
            File.WriteAllBytes(media, []);
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
                        Clips = clips.Select((c, i) => Clip($"v{i}", "src", c.Start, c.Duration, c.Trim, c.ForceEncode)).ToList(),
                    },
                ],
            };
            var analyzer = new SmartRenderAnalyzer(new FixedProbe(Info(packets, container)), Resolver(media));
            return await analyzer.AnalyzeAsync(timeline, VideoCodec.H264, 1920, 1080, clips.Max(c => c.Start + c.Duration));
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    // ---------------------------------------------------------------- real media, decoded

    [Theory]
    [InlineData(0, 30)]
    [InlineData(0, 31)]
    [InlineData(0, 32)]
    [InlineData(0, 34)]
    [InlineData(0, 35)]
    [InlineData(0, 44)]
    [InlineData(30, 32)]
    [InlineData(15, 60)]
    [InlineData(45, 100)]
    [InlineData(60, 44)]
    public async Task ExportAsync_BFrameCuts_ShowTheIntendedSourceFrames(int trim, int length)
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var source = Path.Combine(directory, "ipbb.mp4");
            await WriteSourceAsync(source, "-bf 2 -x264-params b-adapt=0:scenecut=0:keyint=30:min-keyint=30", audio: false);
            var output = Path.Combine(directory, "out.mp4");

            var (plan, fallback) = await ExportAsync(source, output, [(0, length, trim, false)], smart: true);

            Assert.Null(fallback);
            Assert.True(plan!.HasCopySegments, plan.Reason);
            await AssertShowsSourceFramesAsync(source, output, plan, [(0, length, trim, false)]);
            await AssertOnFrameGridAsync(output, length);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    [Theory]
    [InlineData(0)]   // a copy without B-frames next to an encoded segment with them: the DTS case
    [InlineData(2)]
    public async Task ExportAsync_MixedCopyEncodeCopyWithAudio_MatchesTheConventionalTimeline(int bFrames)
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var source = Path.Combine(directory, "source.mp4");
            await WriteSourceAsync(source, $"-bf {bFrames} -x264-params b-adapt=0:scenecut=0:keyint=30:min-keyint=30", audio: true);
            (int, int, int, bool)[] clips = [(0, 62, 0, false), (62, 40, 62, true), (102, 71, 102, false)];
            var smart = Path.Combine(directory, "smart.mp4");
            var conventional = Path.Combine(directory, "conventional.mp4");

            var (plan, fallback) = await ExportAsync(source, smart, clips, smart: true);
            await ExportAsync(source, conventional, clips, smart: false);

            Assert.Null(fallback);
            Assert.Contains(plan!.Segments, s => s.Kind == SmartRenderSegmentKind.Copy);
            Assert.Contains(plan.Segments, s => s.Kind == SmartRenderSegmentKind.Encode);
            await AssertShowsSourceFramesAsync(source, smart, plan, clips);
            await AssertOnFrameGridAsync(smart, 173);

            // The audio is mixed and encoded identically on both paths, so identical packets plus a
            // picture on the same frame grid is the A/V timing receipt.
            Assert.Equal(await FramePtsAsync(conventional), await FramePtsAsync(smart));
            Assert.Equal(await AudioPacketsAsync(conventional), await AudioPacketsAsync(smart));
            Assert.NotEmpty(await AudioPacketsAsync(smart));
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task ExportAsync_OpenGopSource_CopiesOnlyWhereNothingReseeksALeadingPicture()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var source = Path.Combine(directory, "opengop.mp4");
            await WriteSourceAsync(source, "-bf 3 -x264-params scenecut=0:keyint=30:min-keyint=30:open-gop=1", audio: false);

            var fromKeyframe = Path.Combine(directory, "from-keyframe.mp4");
            var (planA, fallbackA) = await ExportAsync(source, fromKeyframe, [(0, 32, 30, false)], smart: true);
            Assert.Null(fallbackA);
            Assert.False(planA!.HasCopySegments, "a copy from an open-GOP keyframe carries its leading pictures");
            await AssertShowsSourceFramesAsync(source, fromKeyframe, planA, [(0, 32, 30, false)]);

            // Decode order ... 56 60K 58 59 63 ...: from the start, 61 frames are exact.
            var fromStart = Path.Combine(directory, "from-start.mp4");
            var (planB, fallbackB) = await ExportAsync(source, fromStart, [(0, 61, 0, false)], smart: true);
            Assert.Null(fallbackB);
            Assert.Equal("C0+61", Describe(planB!));
            await AssertShowsSourceFramesAsync(source, fromStart, planB!, [(0, 61, 0, false)]);

            // The exact copy of a 90-frame clip ends on the leading pictures of the keyframe at 3 s,
            // where the render after it would re-seek and land on the keyframe: frames 88-89 would
            // show 90-91. Nothing is copied instead.
            var continuing = Path.Combine(directory, "continuing.mp4");
            var (planC, fallbackC) = await ExportAsync(source, continuing, [(0, 90, 0, false)], smart: true);
            Assert.Null(fallbackC);
            Assert.False(planC!.HasCopySegments, planC.Reason);
            Assert.Contains("open-GOP", planC.Reason, StringComparison.Ordinal);
            await AssertShowsSourceFramesAsync(source, continuing, planC, [(0, 90, 0, false)]);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task ExportAsync_MpegTsSource_PlacesCopiesFromTheContainerStartAndEndsThemOnKeyframes()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var mp4 = Path.Combine(directory, "ipbb.mp4");
            await WriteSourceAsync(mp4, "-bf 2 -x264-params b-adapt=0:scenecut=0:keyint=30:min-keyint=30", audio: false);
            // Same packets; MPEG-TS starts its clock at 1.4 s, as camcorder .mts files do.
            var source = Path.Combine(directory, "ipbb.ts");
            await FfmpegFixture.RunAsync($"-hide_banner -loglevel error -y -i \"{mp4}\" -c copy -f mpegts \"{source}\"");

            var info = await new FfmpegStreamProbe().ProbeAsync(source);
            Assert.Equal("mpegts", info.ContainerFormat);
            Assert.Equal(0.0, info.KeyframeSeconds[0], 3);
            Assert.Equal(1.0, info.KeyframeSeconds[1], 3);

            // The exact copy from 1 s runs 43 frames, but MPEG-TS seeks exactly only to keyframes and
            // the clip continues, so the copy ends where the keyframe at 2 s opens and the tail's
            // render seeks onto it. (A clip STARTING mid-GOP in MPEG-TS is mis-seeked by the frame
            // source on every export path; that inherited defect is outside this fixture.)
            var output = Path.Combine(directory, "out.mp4");
            var (plan, fallback) = await ExportAsync(source, output, [(0, 45, 30, false)], smart: true);
            Assert.Null(fallback);
            Assert.Equal("C0+30 E30+15", Describe(plan!));
            await AssertShowsSourceFramesAsync(source, output, plan!, [(0, 45, 30, false)]);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static Clip Clip(string id, string mediaRef, int start, int duration, int trim, bool forceEncode = false) => new()
    {
        Id = id,
        MediaRef = mediaRef,
        MediaType = ClipType.video,
        SourceClipType = ClipType.video,
        StartFrame = start,
        DurationFrames = duration,
        TrimStartFrame = trim,
        // Imperceptibly translucent: forces an encode segment while the picture stays identifiable.
        Opacity = forceEncode ? 0.99 : 1.0,
        Speed = 1.0,
        Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 1.0, Height = 1.0, Rotation = 0 },
        Crop = new Crop { Left = 0, Top = 0, Right = 0, Bottom = 0 },
    };

    private static VideoStreamInfo Info(IReadOnlyList<VideoPacket>? packets, string? container)
    {
        var keyframes = packets?.Where(p => p.IsKeyframe).Select(p => p.PtsSeconds).OrderBy(t => t).ToList() ?? [0.0];
        return new VideoStreamInfo("h264", "High", 1920, 1080, "yuv420p", 30, "bt709", "bt709", "bt709", "tv", keyframes, packets, container);
    }

    private static MediaResolver Resolver(string media)
    {
        var manifest = new MediaManifest
        {
            Entries =
            [
                new MediaManifestEntry { Id = "src", Type = ClipType.video, Source = MediaSource.External(media) },
                new MediaManifestEntry { Id = "srcA", Type = ClipType.audio, Source = MediaSource.External(media) },
            ],
        };
        return new MediaResolver(() => manifest, () => null);
    }

    /// <summary>320×240 tagged BT.709, so the analyzer accepts it for copying.</summary>
    internal static Task WriteSourceAsync(string path, string x264, bool audio)
        => FfmpegFixture.RunAsync(
            "-hide_banner -loglevel error -y -f lavfi -i testsrc2=size=320x240:rate=30:duration=6 "
            + (audio ? "-f lavfi -i sine=frequency=440:sample_rate=48000:duration=6 " : "")
            + "-vf setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv "
            + $"-c:v libx264 -threads 1 -crf 18 {x264} -pix_fmt yuv420p "
            + (audio ? "-c:a aac -b:a 128k " : "")
            + $"\"{path}\"");

    private static async Task<(SmartRenderPlan? Plan, string? Fallback)> ExportAsync(
        string source, string output, (int Start, int Duration, int Trim, bool ForceEncode)[] clips, bool smart,
        CancellationToken cancellationToken = default)
    {
        var video = clips.Select((c, i) => Clip($"v{i}", "src", c.Start, c.Duration, c.Trim, c.ForceEncode)).ToList();
        var audio = clips.Select((c, i) => new Clip
        {
            Id = $"a{i}", MediaRef = "srcA", MediaType = ClipType.audio, SourceClipType = ClipType.audio,
            StartFrame = c.Start, DurationFrames = c.Duration, TrimStartFrame = c.Trim,
        }).ToList();
        var hasAudio = await HasAudioAsync(source);
        var timeline = new Timeline
        {
            Fps = 30,
            Width = 320,
            Height = 240,
            Tracks = hasAudio
                ? [new Track { Type = ClipType.video, Clips = video }, new Track { Type = ClipType.audio, Clips = audio }]
                : [new Track { Type = ClipType.video, Clips = video }],
        };

        using var frameSource = new ExportFrameSource();
        using var exporter = new TimelineExporter(new CpuCompositor(), frameSource, Resolver(source),
            acceleration: HardwareAccelerationMode.Software);
        exporter.DisableSmartRender = !smart;
        await exporter.ExportAsync(timeline, new ExportPreset("wp234", codec: VideoCodec.H264), output, cancellationToken);
        return (exporter.LastSmartRenderPlan, exporter.LastSmartRenderFallbackReason);
    }

    /// <summary>
    /// Every output frame shows its intended source frame: bit-exact where the plan copies, and
    /// nearer to it than to any neighbour where the plan re-encodes.
    /// </summary>
    private static async Task AssertShowsSourceFramesAsync(
        string source, string output, SmartRenderPlan plan, (int Start, int Duration, int Trim, bool ForceEncode)[] clips)
    {
        var sourceMd5 = await FrameMd5Async(source);
        var outputMd5 = await FrameMd5Async(output);
        var sourceGray = await GrayAsync(source);
        var outputGray = await GrayAsync(output);
        var total = clips.Max(c => c.Start + c.Duration);
        Assert.Equal(total, outputMd5.Length);

        for (var frame = 0; frame < total; frame++)
        {
            var clip = clips.Single(c => frame >= c.Start && frame < c.Start + c.Duration);
            var expected = clip.Trim + frame - clip.Start;
            var copied = plan.Segments.Single(s => frame >= s.StartFrame && frame < s.StartFrame + s.FrameCount).Kind
                == SmartRenderSegmentKind.Copy;
            if (copied)
            {
                Assert.True(outputMd5[frame] == sourceMd5[expected],
                    $"copied frame {frame} should be source frame {expected}; it is source frame {Array.IndexOf(sourceMd5, outputMd5[frame])}");
                continue;
            }

            var mse = Enumerable.Range(expected - 3, 7).Where(j => j >= 0 && j < sourceGray.Length)
                .ToDictionary(j => j, j => Mse(outputGray[frame], sourceGray[j]));
            var nearest = mse.MinBy(kv => kv.Value).Key;
            Assert.True(nearest == expected, $"encoded frame {frame} should show source frame {expected}; it is nearest {nearest}");
        }
    }

    private static async Task AssertOnFrameGridAsync(string output, int frames)
    {
        var pts = await FramePtsAsync(output);
        Assert.Equal(frames, pts.Length);
        for (var i = 0; i < pts.Length; i++)
            Assert.True(Math.Abs(pts[i] - i / 30.0) < 0.001, $"frame {i} is presented at {pts[i]}s");
    }

    private static double Mse(byte[] a, byte[] b)
    {
        long sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = a[i] - b[i];
            sum += d * d;
        }
        return sum / (double)a.Length;
    }

    private static async Task<string[]> FrameMd5Async(string file)
    {
        var text = await RunAsync(FFmpegResolver.GetFFmpegPath(),
            "-v", "error", "-i", file, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-");
        return text.Split('\n').Where(s => s.Length > 0 && !s.StartsWith('#')).Select(s => s.Split(',').Last().Trim()).ToArray();
    }

    private static async Task<byte[][]> GrayAsync(string file)
    {
        const int size = 320 * 240;
        var bytes = await RunBytesAsync(FFmpegResolver.GetFFmpegPath(),
            "-v", "error", "-i", file, "-map", "0:v:0", "-fps_mode", "passthrough",
            "-vf", "format=gray", "-f", "rawvideo", "-");
        return Enumerable.Range(0, bytes.Length / size).Select(i => bytes.AsSpan(i * size, size).ToArray()).ToArray();
    }

    private static async Task<double[]> FramePtsAsync(string file)
    {
        var text = await RunAsync(FFmpegResolver.GetFFprobePath(),
            "-v", "error", "-select_streams", "v:0", "-show_entries", "frame=pts_time", "-of", "csv=p=0", file);
        return text.Split('\n').Select(s => s.Trim().TrimEnd(',')).Where(s => s.Length > 0)
            .Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
    }

    private static async Task<string[]> AudioPacketsAsync(string file)
    {
        var text = await RunAsync(FFmpegResolver.GetFFmpegPath(),
            "-v", "error", "-i", file, "-map", "0:a:0?", "-c", "copy", "-f", "framemd5", "-");
        return text.Split('\n').Where(s => s.Length > 0 && !s.StartsWith('#')).Select(s => s.Trim()).ToArray();
    }

    private static async Task<bool> HasAudioAsync(string file)
    {
        var text = await RunAsync(FFmpegResolver.GetFFprobePath(),
            "-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", file);
        return text.Trim().Length > 0;
    }

    private static string Describe(SmartRenderPlan plan)
        => string.Join(" ", plan.Segments.Select(s => $"{(s.Kind == SmartRenderSegmentKind.Copy ? "C" : "E")}{s.StartFrame}+{s.FrameCount}"));

    internal static async Task<string> RunAsync(string executable, params string[] arguments)
        => System.Text.Encoding.UTF8.GetString(await RunBytesAsync(executable, arguments));

    private static async Task<byte[]> RunBytesAsync(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Failed to start {executable}.");
        using var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await copy;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed: {await stderr}");
        return stdout.ToArray();
    }

    private sealed class FixedProbe(VideoStreamInfo info) : IVideoStreamProbe
    {
        public Task<VideoStreamInfo> ProbeAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(info);
    }
}

/// <summary>
/// Cancellation must not publish a file or strand intermediates. Serialized: it counts every
/// <c>temno_smart_*</c> / <c>temno_export_*</c> entry in %TEMP%, which a concurrent export would add.
/// </summary>
[Collection(SmartRenderMediaCollection.Name)]
public sealed class SmartRenderCancellationTests
{
    [Theory]
    [InlineData(1)]     // inside the encoded middle, just after the first copy
    [InlineData(80)]    // deep in the encoded middle
    [InlineData(150)]   // after the last copy: the join, the verification or the audio mux
    public async Task ExportAsync_CancelledMidSmartRender_LeavesNoOutputOrIntermediates(int cancelAtFrame)
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var source = Path.Combine(directory, "source.mp4");
            await SmartRenderBoundaryTests.WriteSourceAsync(source, "-bf 2 -x264-params b-adapt=0:scenecut=0:keyint=30:min-keyint=30", audio: true);
            var output = Path.Combine(directory, "out.mp4");
            var before = Intermediates();

            var manifest = new MediaManifest
            {
                Entries =
                [
                    new MediaManifestEntry { Id = "src", Type = ClipType.video, Source = MediaSource.External(source) },
                    new MediaManifestEntry { Id = "srcA", Type = ClipType.audio, Source = MediaSource.External(source) },
                ],
            };
            var timeline = new Timeline
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
                            VideoClip("v0", 0, 62, 0, 1.0),
                            VideoClip("v1", 62, 40, 62, 0.99),
                            VideoClip("v2", 102, 71, 102, 1.0),
                        ],
                    },
                    new Track
                    {
                        Type = ClipType.audio,
                        Clips = [new Clip { Id = "a0", MediaRef = "srcA", MediaType = ClipType.audio, SourceClipType = ClipType.audio, StartFrame = 0, DurationFrames = 173 }],
                    },
                ],
            };

            using var cts = new CancellationTokenSource();
            using var frameSource = new ExportFrameSource();
            using var exporter = new TimelineExporter(new CpuCompositor(), frameSource,
                new MediaResolver(() => manifest, () => null), acceleration: HardwareAccelerationMode.Software);
            exporter.Progress += (done, _) =>
            {
                if (done >= cancelAtFrame) cts.Cancel();
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => exporter.ExportAsync(timeline, new ExportPreset("wp234", codec: VideoCodec.H264), output, cts.Token));

            Assert.True(exporter.LastSmartRenderPlan?.HasCopySegments, "the cancellation should land inside a smart render");
            Assert.False(File.Exists(output));
            Assert.Empty(Intermediates().Except(before));
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    private static Clip VideoClip(string id, int start, int duration, int trim, double opacity) => new()
    {
        Id = id,
        MediaRef = "src",
        MediaType = ClipType.video,
        SourceClipType = ClipType.video,
        StartFrame = start,
        DurationFrames = duration,
        TrimStartFrame = trim,
        Opacity = opacity,
        Speed = 1.0,
        Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 1.0, Height = 1.0, Rotation = 0 },
        Crop = new Crop { Left = 0, Top = 0, Right = 0, Bottom = 0 },
    };

    private static string[] Intermediates()
    {
        var temp = Path.GetTempPath();
        return Directory.EnumerateFileSystemEntries(temp, "temno_smart_*")
            .Concat(Directory.EnumerateFileSystemEntries(temp, "temno_export_*"))
            .ToArray();
    }
}

/// <summary>
/// Smart-render tests that run real exports. Not parallel with the rest of the suite: they spawn
/// full-core ffmpeg encodes that would otherwise run beside wall-clock budgets such as
/// <c>CompositorThroughputBudgetTests</c>, and the cancellation tests count <c>%TEMP%</c> entries
/// a concurrent export would add.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SmartRenderMediaCollection
{
    public const string Name = "SmartRenderMedia";
}

/// <summary>Synthetic decode-order packet tables shaped like real encoder output.</summary>
internal static class SmartRenderPacketTables
{
    /// <summary>
    /// Closed GOPs from each keyframe to the next (or <paramref name="endSeconds"/>). Within a GOP,
    /// decode order is I, then each anchor followed by the <paramref name="bFrames"/> B-frames
    /// displayed before it; the GOP's last frame is always an anchor, as x264 closes a GOP.
    /// </summary>
    public static IReadOnlyList<VideoPacket> ClosedGops(IReadOnlyList<double> keyframes, double endSeconds, int fps, int bFrames)
    {
        var bounds = keyframes.Select(k => (int)Math.Round(k * fps)).Append((int)Math.Round(endSeconds * fps)).ToArray();
        var packets = new List<VideoPacket>();
        for (var g = 0; g < bounds.Length - 1; g++)
        {
            var start = bounds[g];
            var end = bounds[g + 1];
            packets.Add(new VideoPacket(start / (double)fps, IsKeyframe: true));
            var anchor = start;
            while (anchor < end - 1)
            {
                var next = Math.Min(anchor + bFrames + 1, end - 1);
                packets.Add(new VideoPacket(next / (double)fps, IsKeyframe: false));
                for (var b = anchor + 1; b < next; b++)
                    packets.Add(new VideoPacket(b / (double)fps, IsKeyframe: false));
                anchor = next;
            }
        }
        return packets;
    }

    /// <summary>
    /// Open GOPs, x264 <c>open-gop=1</c> style: every keyframe after the first is decoded before the
    /// B-frames displayed just ahead of it, which reference the previous GOP.
    /// </summary>
    public static IReadOnlyList<VideoPacket> OpenGops(int gop, int gops, int fps, int bFrames)
    {
        var frames = gop * gops;
        var packets = new List<VideoPacket>();
        var previous = 0;
        packets.Add(new VideoPacket(0, IsKeyframe: true));
        for (var anchor = bFrames + 1; anchor < frames; anchor += bFrames + 1)
        {
            packets.Add(new VideoPacket(anchor / (double)fps, IsKeyframe: anchor % gop == 0));
            for (var b = previous + 1; b < anchor; b++)
                packets.Add(new VideoPacket(b / (double)fps, IsKeyframe: false));
            previous = anchor;
        }
        for (var tail = previous + 1; tail < frames; tail++)
            packets.Add(new VideoPacket(tail / (double)fps, IsKeyframe: false));
        return packets;
    }
}
