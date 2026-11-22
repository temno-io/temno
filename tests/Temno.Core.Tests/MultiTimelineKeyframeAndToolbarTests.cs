using System.Text.Json;
using Temno.Core.Captions;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Ui;
using Temno.Media;
using Temno.Media.Cache;
using Temno.Media.Compositing;
using Temno.Media.Effects;
using Temno.Media.Playback;
using Temno.Media.Text;
using Xunit;

namespace Temno.Core.Tests;

public sealed class MultiTimelineKeyframeAndToolbarTests
{
    private const double Tol = 1e-4;

    // -------------------------------------------------------------------------
    // 1. Project-wide canvas & FPS settings across all timelines
    // -------------------------------------------------------------------------

    [Fact]
    public void SetProjectSettings_UpdatesAllTimelinesAndRefitsEveryTimelineAtomically()
    {
        var session = ProjectSession.CreateNew(name: "Timeline 1", fps: 30, width: 1920, height: 1080);
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "M1",
            Name = "clip.mp4",
            Type = ClipType.video,
            SourceWidth = 1920,
            SourceHeight = 1080,
            Duration = 5,
        });
        var tl1 = session.ActiveTimeline;
        var c1 = new Clip
        {
            Id = "C1",
            MediaRef = "M1",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 60,
            Transform = LayoutEngine.FitTransform(session.Manifest.Entries[0], tl1),
        };
        Assert.True(EditCommands.AddClip(session, 0, c1).Ok);

        Assert.True(TimelineCommands.AddTimeline(session, "Timeline 2", null, out var tl2Id).Ok);
        var tl2 = session.ActiveTimeline;
        Assert.Equal(tl2Id, tl2.Id);
        // Simulate a legacy project where Timeline 2 drifted to 4K @ 60 fps.
        tl2.Width = 3840;
        tl2.Height = 2160;
        tl2.Fps = 60;
        var c2 = new Clip
        {
            Id = "C2",
            MediaRef = "M1",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 60,
            Transform = LayoutEngine.FitTransform(session.Manifest.Entries[0], tl2),
        };
        Assert.True(EditCommands.AddClip(session, 0, c2).Ok);

        // Switch back to Timeline 1 and apply 9:16 1080p @ 24 fps.
        Assert.True(TimelineCommands.SetActiveTimeline(session, tl1.Id).Ok);
        Assert.True(EditCommands.SetProjectSettings(session, fps: 24, width: null, height: null, aspectRatio: "9:16", quality: "1080p").Ok);

        foreach (var tl in session.ProjectFile.Timelines)
        {
            Assert.Equal(1080, tl.Width);
            Assert.Equal(1920, tl.Height);
            Assert.Equal(24, tl.Fps);
            Assert.True(tl.SettingsConfigured);
        }

        // Both timelines' 16:9 clips refit to 9:16 (width = 1.0, height = (9/16)/(16/9) = 0.31640625).
        var expectedH = (9.0 / 16.0) / (16.0 / 9.0);
        Assert.Equal(expectedH, tl1.Tracks[0].Clips[0].Transform.Height, Tol);
        Assert.Equal(expectedH, tl2.Tracks[0].Clips[0].Transform.Height, Tol);

        // Single undo restores both timelines' settings and clip transforms.
        session.Undo();
        var restored1 = session.ProjectFile.Timelines.Single(t => t.Id == tl1.Id);
        var restored2 = session.ProjectFile.Timelines.Single(t => t.Id == tl2Id);
        Assert.Equal((1920, 1080, 30), (restored1.Width, restored1.Height, restored1.Fps));
        Assert.Equal((3840, 2160, 60), (restored2.Width, restored2.Height, restored2.Fps));
        Assert.Equal(1.0, restored1.Tracks[0].Clips[0].Transform.Height, Tol);
        Assert.Equal(1.0, restored2.Tracks[0].Clips[0].Transform.Height, Tol);
    }

    [Fact]
    public void AddTimeline_InheritsCanvasFpsAndSettingsConfigured()
    {
        var session = ProjectSession.CreateNew(name: "Timeline 1", fps: 30, width: 1920, height: 1080);
        Assert.True(EditCommands.SetProjectSettings(session, fps: 60, width: null, height: null, aspectRatio: "16:9", quality: "720p").Ok);

        Assert.True(TimelineCommands.AddTimeline(session, "Timeline 2", null, out var id).Ok);
        var created = session.ProjectFile.Timelines.Single(t => t.Id == id);
        Assert.Equal((1280, 720, 60, true), (created.Width, created.Height, created.Fps, created.SettingsConfigured));
    }

    // -------------------------------------------------------------------------
    // 2. Keyframe-aware reads, writes, fallbacks, text refit, and split/trim
    // -------------------------------------------------------------------------

    [Fact]
    public void WriteInPlace_RoutesActiveTracksToKeyframes_AndPreservesInterpolation()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip
        {
            Id = "t1",
            MediaRef = "",
            MediaType = ClipType.text,
            TextContent = "Hello",
            StartFrame = 10,
            DurationFrames = 100,
            Opacity = 1.0,
            Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.4, Height = 0.2, Rotation = 0 },
            Crop = new Crop { Left = 0.0, Top = 0.1, Right = 0.0, Bottom = 0.0 },
        };
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);

        // Stamp all 5 visual tracks at frame 10 (offset 0), then mark opacity key as linear.
        Assert.True(KeyframeCommands.StampOpacity(session, "t1", 10).Ok);
        Assert.True(KeyframeCommands.StampScale(session, "t1", 10).Ok);
        Assert.True(KeyframeCommands.StampPosition(session, "t1", 10).Ok);
        Assert.True(KeyframeCommands.StampRotation(session, "t1", 10).Ok);
        Assert.True(KeyframeCommands.StampCrop(session, "t1", 10).Ok);
        clip = session.ActiveTimeline.Tracks[0].Clips[0];
        clip.OpacityTrack!.Keyframes[0].InterpolationOut = Interpolation.linear;

        // Edit at frame 60 (offset 50): must auto-create second keyframes without corrupting static values.
        Assert.True(KeyframeCommands.WriteOpacityInPlace(clip, 60, 0.25));
        Assert.True(KeyframeCommands.WriteScaleSizeInPlace(clip, 60, 0.8, 0.4));
        Assert.True(KeyframeCommands.WritePositionCenterInPlace(clip, 60, centerX: 0.75, centerY: null));
        Assert.True(KeyframeCommands.WriteRotationInPlace(clip, 60, 90));
        Assert.True(KeyframeCommands.WriteCropInPlace(clip, 60, left: 0.3));

        // Static properties untouched.
        Assert.Equal(1.0, clip.Opacity, Tol);
        Assert.Equal(0.4, clip.Transform.Width, Tol);
        Assert.Equal(0.5, clip.Transform.CenterX, Tol);
        Assert.Equal(0.0, clip.Transform.Rotation, Tol);
        Assert.Equal(0.0, clip.Crop.Left, Tol);

        // Frame 10 keeps original stamped values; frame 60 reflects edited values.
        Assert.Equal(1.0, clip.RawOpacityAt(10), Tol);
        Assert.Equal(0.25, clip.RawOpacityAt(60), Tol);
        Assert.Equal(0.625, clip.RawOpacityAt(35), Tol); // linear midpoint

        Assert.Equal(0.4, clip.SizeAt(10).X, Tol);
        Assert.Equal(0.8, clip.SizeAt(60).X, Tol);
        Assert.Equal(0.6, clip.SizeAt(35).X, Tol);

        // WriteScaleSizeInPlace anchors the visual center in PositionTrack so scaling at frame 60
        // keeps CenterY == 0.5 instead of drifting off-center as TopLeft stays pinned.
        Assert.Equal(0.75, clip.TransformAt(60).CenterX, Tol);
        Assert.Equal(0.5, clip.TransformAt(60).CenterY, Tol);
        // Editing CenterY alone at frame 60 must preserve CenterX = 0.75.
        Assert.True(KeyframeCommands.WritePositionCenterInPlace(clip, 60, centerX: null, centerY: 0.25));
        Assert.Equal(0.75, clip.TransformAt(60).CenterX, Tol);
        Assert.Equal(0.25, clip.TransformAt(60).CenterY, Tol);

        Assert.Equal(0.0, clip.RotationAt(10), Tol);
        Assert.Equal(90.0, clip.RotationAt(60), Tol);

        Assert.Equal(0.0, clip.CropAt(10).Left, Tol);
        Assert.Equal(0.3, clip.CropAt(60).Left, Tol);
        Assert.Equal(0.1, clip.CropAt(60).Top, Tol); // preserved other edge!

        // Overwriting existing keyframe at frame 10 preserves linear interpolation.
        Assert.True(KeyframeCommands.WriteOpacityInPlace(clip, 10, 0.9));
        Assert.Equal(Interpolation.linear, clip.OpacityTrack.Keyframes[0].InterpolationOut);
        Assert.Equal(0.9, clip.RawOpacityAt(10), Tol);
    }

    [Fact]
    public void WriteVolumeDbInPlace_TargetsTrackInsideClip_AndStaticOutsideClip()
    {
        var session = ProjectSession.CreateNew();
        var audio = new Clip
        {
            Id = "a1",
            MediaRef = "M1",
            MediaType = ClipType.audio,
            StartFrame = 20,
            DurationFrames = 60,
            Volume = 1.0,
        };
        Assert.True(EditCommands.AddClip(session, 2, audio).Ok);

        // Inactive track -> writes static Volume.
        Assert.False(KeyframeCommands.WriteVolumeDbInPlace(audio, 30, -6.0));
        Assert.Equal(VolumeScale.LinearFromDb(-6.0), audio.Volume, Tol);

        // Stamp volume at frame 20 (offset 0, 0 dB envelope), then edit at frame 50.
        Assert.True(KeyframeCommands.StampVolume(session, "a1", 20).Ok);
        audio = session.ActiveTimeline.Tracks[2].Clips[0];
        Assert.Equal(0.0, KeyframeCommands.LiveVolumeKfDb(audio, 20)!.Value, Tol);
        Assert.True(KeyframeCommands.WriteVolumeDbInPlace(audio, 50, -12.0));
        Assert.Equal(-12.0, KeyframeCommands.LiveVolumeKfDb(audio, 50)!.Value, Tol);
        Assert.Equal(VolumeScale.LinearFromDb(-6.0), audio.Volume, Tol);

        // Outside clip -> LiveVolumeKfDb is null and write falls back to static Volume.
        Assert.Null(KeyframeCommands.LiveVolumeKfDb(audio, 5));
        Assert.False(KeyframeCommands.WriteVolumeDbInPlace(audio, 5, 0.0));
        Assert.Equal(1.0, audio.Volume, Tol);
    }

    [Fact]
    public void SampleScalarAtClip_AndUpsertPosition_UseClipDefaultsNotZeroOrMidRange()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip
        {
            Id = "v1",
            MediaRef = "M1",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 50,
            Opacity = 0.85,
            Transform = new Transform { CenterX = 0.7, CenterY = 0.3, Width = 0.4, Height = 0.2, Rotation = 15 },
            Crop = new Crop { Left = 0.05, Top = 0.1, Right = 0.15, Bottom = 0.2 },
        };
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);

        Assert.Equal(0.85, KeyframeCommands.SampleScalarAtClip(clip, "opacity", 10), Tol);
        Assert.Equal(0.0, KeyframeCommands.SampleScalarAtClip(clip, "volume", 10), Tol);
        Assert.Equal(15.0, KeyframeCommands.SampleScalarAtClip(clip, "rotation", 10), Tol);
        Assert.Equal(0.4, KeyframeCommands.SampleScalarAtClip(clip, "scale.a", 10), Tol);
        Assert.Equal(0.2, KeyframeCommands.SampleScalarAtClip(clip, "scale.b", 10), Tol);
        Assert.Equal(0.5, KeyframeCommands.SampleScalarAtClip(clip, "position.a", 10), Tol); // 0.7 - 0.4/2
        Assert.Equal(0.2, KeyframeCommands.SampleScalarAtClip(clip, "position.b", 10), Tol); // 0.3 - 0.2/2

        // Upserting position.a on an unkeyed clip must preserve TopLeft.Y = 0.2 (not snap to 0!).
        Assert.True(KeyframeCommands.UpsertScalarKey(session, "v1", "position.a", 10, 0.25).Ok);
        clip = session.ActiveTimeline.Tracks[0].Clips[0];
        var kf = Assert.Single(clip.PositionTrack!.Keyframes);
        Assert.Equal(0.25, kf.Value.A, Tol);
        Assert.Equal(0.2, kf.Value.B, Tol);
    }

    [Fact]
    public void TextUpdate_WithFitToContent_PreservesRelativeScaleTrackRatios()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(TextClipCommands.AddTexts(session,
        [
            new TextClipSpec(null, 0, 60, "Hi", new TextStyle { FontSize = 64 }, null, null),
        ], out var ids).Ok);
        var id = Assert.Single(ids);
        var clip = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == id);
        var origW = clip.Transform.Width;
        var origH = clip.Transform.Height;

        Assert.True(KeyframeCommands.StampScale(session, id, 0).Ok);
        clip = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == id);
        Assert.True(KeyframeCommands.WriteScaleSizeInPlace(clip, 30, origW * 2.0, origH * 2.0));

        // Update text to a much longer string so fitToContent widens clip.Transform.Width.
        Assert.True(TextClipCommands.UpdateText(
            session, [id], null,
            new TextPatch { HasContent = true, Content = "A Much Longer Title Line Here" },
            out _, out _).Ok);

        clip = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == id);
        Assert.True(clip.Transform.Width > origW);
        // Scale ratios at frame 0 (1.0x) and frame 30 (2.0x) must remain intact!
        Assert.Equal(1.0, clip.SizeAt(0).X / clip.Transform.Width, Tol);
        Assert.Equal(2.0, clip.SizeAt(30).X / clip.Transform.Width, Tol);
        Assert.Equal(2.0, clip.SizeAt(30).Y / clip.Transform.Height, Tol);
    }

    [Fact]
    public void SplitClip_AndLeftTrim_ShiftKeyframesSoEvaluatedValuesStayContinuous()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip
        {
            Id = "k1",
            MediaRef = "M1",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 100,
            OpacityTrack = new KeyframeTrack<double>
            {
                Keyframes =
                [
                    new Keyframe<double> { Frame = 0, Value = 0.0, InterpolationOut = Interpolation.linear },
                    new Keyframe<double> { Frame = 100, Value = 1.0, InterpolationOut = Interpolation.linear },
                ]
            },
            RotationTrack = new KeyframeTrack<double>
            {
                Keyframes =
                [
                    new Keyframe<double> { Frame = 0, Value = 0.0, InterpolationOut = Interpolation.linear },
                    new Keyframe<double> { Frame = 100, Value = 100.0, InterpolationOut = Interpolation.linear },
                ]
            },
        };
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);

        var expectedOp70 = clip.RawOpacityAt(70);
        var expectedRot70 = clip.RotationAt(70);

        Assert.True(EditCommands.SplitClip(session, "k1", 40).Ok);
        var right = session.ActiveTimeline.Tracks[0].Clips[1];
        Assert.Equal(40, right.StartFrame);
        Assert.Equal(expectedOp70, right.RawOpacityAt(70), Tol);
        Assert.Equal(expectedRot70, right.RotationAt(70), Tol);

        // Trim the right half's left edge from 40 to 55: frame 70 must STILL evaluate identically!
        Assert.True(EditCommands.TrimClipEdge(session, right.Id, leftEdge: true, edgeFrame: 55).Ok);
        right = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == right.Id);
        Assert.Equal(55, right.StartFrame);
        Assert.Equal(expectedOp70, right.RawOpacityAt(70), Tol);
        Assert.Equal(expectedRot70, right.RotationAt(70), Tol);
    }

    // -------------------------------------------------------------------------
    // 3. Timeline toolbar responsive layout
    // -------------------------------------------------------------------------

    [Fact]
    public void TimelineToolbarLayout_ExpandsTabsOnWideScreens_AndAdaptsOnSmallScreens()
    {
        // Wide desktop (1280px), 2 tabs (220px): all tabs + close buttons + full tools + zoom readout fit.
        var wide2 = TimelineToolbarLayout.Compute(1280, desiredTabsWidth: 220, openTabCount: 2, totalTimelineCount: 2);
        Assert.Equal(220, wide2.MaxTabsScrollWidth);
        Assert.False(wide2.OverflowSecondaryTools);
        Assert.False(wide2.HideZoomReadout);
        Assert.False(wide2.CompactZoomSlider);
        Assert.False(wide2.ShowAllTimelinesButton);

        // Wide desktop (1280px), 8 tabs (860px): keep full tools, hide zoom readout, scroll tabs + show picker.
        var wide8 = TimelineToolbarLayout.Compute(1280, desiredTabsWidth: 860, openTabCount: 8, totalTimelineCount: 8);
        Assert.False(wide8.OverflowSecondaryTools);
        Assert.True(wide8.HideZoomReadout);
        Assert.True(wide8.ShowAllTimelinesButton);
        Assert.True(wide8.MaxTabsScrollWidth >= TimelineToolbarLayout.ComfortableTwoTabWidth);
        Assert.True(wide8.MaxTabsScrollWidth < 860);

        // Smaller screen (900px), 2 tabs (220px): collapse secondary tools so BOTH tabs stay unclipped!
        var small2 = TimelineToolbarLayout.Compute(900, desiredTabsWidth: 220, openTabCount: 2, totalTimelineCount: 2);
        Assert.True(small2.OverflowSecondaryTools);
        Assert.Equal(220, small2.MaxTabsScrollWidth);
        Assert.False(small2.ShowAllTimelinesButton);

        // Narrow screen (640px), 5 tabs (530px): collapse secondary tools, compact zoom slider, scroll + picker.
        var narrow5 = TimelineToolbarLayout.Compute(640, desiredTabsWidth: 530, openTabCount: 5, totalTimelineCount: 5);
        Assert.True(narrow5.OverflowSecondaryTools);
        Assert.True(narrow5.HideZoomReadout);
        Assert.True(narrow5.CompactZoomSlider);
        Assert.True(narrow5.ShowAllTimelinesButton);
        Assert.True(narrow5.MaxTabsScrollWidth >= TimelineToolbarLayout.MinTabsScrollWidth);

        // Closed timelines always surface the All timelines picker even when 1 tab is open on wide screen.
        var closed = TimelineToolbarLayout.Compute(1440, desiredTabsWidth: 120, openTabCount: 1, totalTimelineCount: 3);
        Assert.True(closed.ShowAllTimelinesButton);

        // Defensive edge inputs never produce NaN or sub-minimum scroll width.
        var degenerate = TimelineToolbarLayout.Compute(double.NaN, double.NegativeInfinity, 0, 0);
        Assert.Equal(TimelineToolbarLayout.MinTabsScrollWidth, degenerate.MaxTabsScrollWidth);
    }

    // -------------------------------------------------------------------------
    // 4. Proxy cancellation & Text raster caching + tight-crop pixel parity
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProxySourceSelector_CancelPendingTranscodes_CancelsInFlightWorkWithoutRetiringKey()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"temno-proxy-cancel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var mediaPath = Path.Combine(tempRoot, "4k.mp4");
            await File.WriteAllBytesAsync(mediaPath, new byte[4096]);
            var probe = new MediaProbeResult(
                DurationSeconds: 30,
                Width: 3840,
                Height: 2160,
                Fps: 30,
                HasAudio: false,
                HasVideo: true,
                VideoCodecName: "hevc");

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var invocations = 0;

            using var selector = new ProxySourceSelector(
                new ProxyCache(new MediaCachePaths(Path.Combine(tempRoot, "cache"))),
                previewMaxDimension: 1280,
                transcoder: async (_, targetPath, _, ct) =>
                {
                    var n = Interlocked.Increment(ref invocations);
                    if (n == 1)
                    {
                        started.TrySetResult();
                        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                        catch (OperationCanceledException)
                        {
                            canceled.TrySetResult();
                            // Hold unwind open until the caller re-requests the same key on the new timeline.
                            await releaseFirst.Task;
                            throw;
                        }
                    }
                    await File.WriteAllBytesAsync(targetPath, [1, 2, 3, 4], ct);
                });

            Assert.Equal(mediaPath, selector.SelectDecodePath(mediaPath, probe));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Simulate timeline switch cancelling outgoing proxy transcode and IMMEDIATELY requesting
            // the same 4K asset on the incoming timeline before the cancelled ffmpeg unwinds.
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            selector.StateChanged += s => { if (s.State == ProxyState.Ready) ready.TrySetResult(); };

            selector.CancelPendingTranscodes();
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Re-queue immediately (no sleep!) while generation 0 is still unwinding.
            _ = selector.SelectDecodePath(mediaPath, probe);
            releaseFirst.TrySetResult();

            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ProxyState.Ready, selector.StatusFor(mediaPath).State);
            Assert.Equal(2, Volatile.Read(ref invocations));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private sealed class CountingTextRasterizer : ITextRasterizer
    {
        public int Calls;

        public TextRasterization Rasterize(string text, TextStyle style, int canvasWidth, int canvasHeight)
        {
            Interlocked.Increment(ref Calls);
            var bgra = new byte[canvasWidth * canvasHeight * 4];
            // Draw an asymmetric 24x12 colored patch near canvas center so rotation/flip/crop are sensitive.
            var cx = canvasWidth / 2;
            var cy = canvasHeight / 2;
            for (var y = cy - 6; y < cy + 6; y++)
            {
                for (var x = cx - 12; x < cx + 12; x++)
                {
                    if (x < 0 || x >= canvasWidth || y < 0 || y >= canvasHeight) continue;
                    var i = (y * canvasWidth + x) * 4;
                    bgra[i] = (byte)(x < cx ? 240 : 40);      // B
                    bgra[i + 1] = (byte)(y < cy ? 200 : 80);  // G
                    bgra[i + 2] = (byte)(text.Length * 25);   // R
                    bgra[i + 3] = 255;                        // A
                }
            }
            return new TextRasterization(canvasWidth, canvasHeight, bgra);
        }
    }

    private sealed class NullFrameSource : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(string mediaPath, double timeSeconds, CancellationToken cancellationToken = default)
            => throw new FileNotFoundException(mediaPath);
    }

    [Fact]
    public async Task TimelineFrameRenderer_TextCache_ReusesRasterAcrossKeyframedTransformAndOpacity()
    {
        var rasterizer = new CountingTextRasterizer();
        using var renderer = new TimelineFrameRenderer(
            new NullFrameSource(),
            compositor: new CpuCompositor(),
            effectsRenderer: new CpuEffectsRenderer(),
            textRasterizer: rasterizer);

        var clip = new Clip
        {
            Id = "txt",
            MediaType = ClipType.text,
            TextContent = "Title",
            StartFrame = 0,
            DurationFrames = 60,
            Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.3, Height = 0.15 },
            PositionTrack = new KeyframeTrack<AnimPair>
            {
                Keyframes =
                [
                    new Keyframe<AnimPair> { Frame = 0, Value = new AnimPair(0.1, 0.2), InterpolationOut = Interpolation.linear },
                    new Keyframe<AnimPair> { Frame = 59, Value = new AnimPair(0.5, 0.6), InterpolationOut = Interpolation.linear },
                ]
            },
            ScaleTrack = new KeyframeTrack<AnimPair>
            {
                Keyframes =
                [
                    new Keyframe<AnimPair> { Frame = 0, Value = new AnimPair(0.3, 0.15), InterpolationOut = Interpolation.linear },
                    new Keyframe<AnimPair> { Frame = 59, Value = new AnimPair(0.6, 0.3), InterpolationOut = Interpolation.linear },
                ]
            },
            RotationTrack = new KeyframeTrack<double>
            {
                Keyframes =
                [
                    new Keyframe<double> { Frame = 0, Value = 0, InterpolationOut = Interpolation.linear },
                    new Keyframe<double> { Frame = 59, Value = 45, InterpolationOut = Interpolation.linear },
                ]
            },
            OpacityTrack = new KeyframeTrack<double>
            {
                Keyframes =
                [
                    new Keyframe<double> { Frame = 0, Value = 0.4, InterpolationOut = Interpolation.linear },
                    new Keyframe<double> { Frame = 59, Value = 1.0, InterpolationOut = Interpolation.linear },
                ]
            },
        };
        var timeline = new Timeline
        {
            Width = 320,
            Height = 180,
            Fps = 30,
            Tracks = [new Track { Type = ClipType.video, Clips = [clip] }],
        };

        VideoFrame? f0 = null;
        VideoFrame? f59 = null;
        try
        {
            for (var f = 0; f < 60; f += 6)
            {
                var frame = await renderer.RenderAsync(timeline, f, 320, 180);
                Assert.NotNull(frame);
                if (f == 0) f0 = frame;
                else frame!.Dispose();
            }
            f59 = await renderer.RenderAsync(timeline, 59, 320, 180);
            Assert.NotNull(f59);

            // Rasterizer ran ONLY ONCE across all animated frames!
            Assert.Equal(1, rasterizer.Calls);
            // Frame 0 and Frame 59 must actually look different (keyframes moved/scaled/rotated/faded it!).
            Assert.False(f0!.Data.SequenceEqual(f59!.Data));

            // Changing text content must miss cache and re-rasterize once.
            clip.TextContent = "Updated";
            using var fUpdated = await renderer.RenderAsync(timeline, 59, 320, 180);
            Assert.NotNull(fUpdated);
            Assert.Equal(2, rasterizer.Calls);
        }
        finally
        {
            f0?.Dispose();
            f59?.Dispose();
        }
    }

    [Fact]
    public async Task TimelineFrameRenderer_CenterSymmetricTextCrop_MatchesFullCanvasReferencePixels()
    {
        const int W = 200;
        const int H = 120;
        var rasterizer = new CountingTextRasterizer();
        var fullRaster = rasterizer.Rasterize("Test", new TextStyle(), W, H);
        var compositor = new CpuCompositor();

        var cases = new (double cx, double cy, double sx, double sy, double rot, bool fh, bool fv)[]
        {
            (0.5, 0.5, 1.0, 1.0, 0, false, false),
            (0.32, 0.68, 1.5, 1.5, 37.0, false, false),
            (0.65, 0.4, 0.75, 1.25, -24.0, true, false),
            (0.45, 0.55, 1.8, 1.8, 90.0, true, true),
        };

        using var renderer = new TimelineFrameRenderer(
            new NullFrameSource(),
            compositor: new CpuCompositor(),
            effectsRenderer: new CpuEffectsRenderer(),
            textRasterizer: new CountingTextRasterizer());

        foreach (var (cx, cy, sx, sy, rot, fh, fv) in cases)
        {
            // Reference: full W x H text canvas composited directly with scale (sx, sy).
            using var refFrame = compositor.Render(
                [
                    new CompositorLayer(
                        new VideoFrame(W, H, fullRaster.Bgra, "bgra"),
                        0, 1.0,
                        new Transform
                        {
                            CenterX = cx, CenterY = cy,
                            Width = sx, Height = sy,
                            Rotation = rot, FlipHorizontal = fh, FlipVertical = fv,
                        },
                        "normal",
                        placementIsFinal: true),
                ],
                W, H);

            var clip = new Clip
            {
                Id = "t",
                MediaType = ClipType.text,
                TextContent = "Test",
                StartFrame = 0,
                DurationFrames = 10,
                Transform = new Transform
                {
                    CenterX = cx, CenterY = cy,
                    Width = 0.2, Height = 0.2,
                    Rotation = rot, FlipHorizontal = fh, FlipVertical = fv,
                },
                ScaleTrack = new KeyframeTrack<AnimPair>
                {
                    Keyframes = [new Keyframe<AnimPair> { Frame = 0, Value = new AnimPair(0.2 * sx, 0.2 * sy) }],
                },
            };
            var timeline = new Timeline
            {
                Width = W, Height = H, Fps = 30,
                Tracks = [new Track { Type = ClipType.video, Clips = [clip] }],
            };

            using var actual = await renderer.RenderAsync(timeline, 0, W, H);
            Assert.NotNull(actual);

            // Allow ±1 channel delta only for floating-point texel rounding on rotated edges.
            var maxDelta = 0;
            var diffPixels = 0;
            string? firstDiff = null;
            for (var i = 0; i < refFrame.Data.Length; i += 4)
            {
                var d = Math.Max(
                    Math.Max(Math.Abs(refFrame.Data[i] - actual!.Data[i]), Math.Abs(refFrame.Data[i + 1] - actual.Data[i + 1])),
                    Math.Max(Math.Abs(refFrame.Data[i + 2] - actual.Data[i + 2]), Math.Abs(refFrame.Data[i + 3] - actual.Data[i + 3])));
                if (d > maxDelta) maxDelta = d;
                if (d > 2)
                {
                    diffPixels++;
                    firstDiff ??= $"px({(i / 4) % W},{(i / 4) / W}) ref=({refFrame.Data[i]},{refFrame.Data[i + 1]},{refFrame.Data[i + 2]},{refFrame.Data[i + 3]}) act=({actual.Data[i]},{actual.Data[i + 1]},{actual.Data[i + 2]},{actual.Data[i + 3]})";
                }
            }
            Assert.True(diffPixels == 0, $"Mismatch for ({cx},{cy},{sx},{sy},{rot},{fh},{fv}): maxDelta={maxDelta}, diffPixels={diffPixels}, first={firstDiff}");
        }
    }

    [Fact]
    public async Task ProxySourceSelector_PreviewMaxDimensionChange_CancelsInFlightStaleTranscode()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"temno-proxy-res-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var mediaPath = Path.Combine(tempRoot, "4k.mp4");
            await File.WriteAllBytesAsync(mediaPath, new byte[4096]);
            var probe = new MediaProbeResult(30, 3840, 2160, 30, HasAudio: false, HasVideo: true, VideoCodecName: "hevc");

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var selector = new ProxySourceSelector(
                new ProxyCache(new MediaCachePaths(Path.Combine(tempRoot, "cache"))),
                previewMaxDimension: 1920,
                transcoder: async (_, targetPath, _, ct) =>
                {
                    started.TrySetResult();
                    try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
                    catch (OperationCanceledException)
                    {
                        canceled.TrySetResult();
                        throw;
                    }
                    await File.WriteAllBytesAsync(targetPath, [1, 2, 3, 4], ct);
                });

            _ = selector.SelectDecodePath(mediaPath, probe);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            selector.PreviewMaxDimension = 1280;
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private sealed class SequencedFakeFrameSource : IFrameSource
    {
        private readonly Func<int, Task<VideoFrame>> _handler;
        public int Calls;

        public SequencedFakeFrameSource(Func<int, Task<VideoFrame>> handler) => _handler = handler;

        public Task<VideoFrame> GetFrameAsync(string mediaPath, double timeSeconds, CancellationToken cancellationToken = default)
            => _handler(Interlocked.Increment(ref Calls));
    }

    [Fact]
    public async Task PreviewFrameProvider_Invalidate_PreventsJoiningPreEditInFlightRender()
    {
        var firstFlight = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SequencedFakeFrameSource(call =>
        {
            if (call == 1) return firstFlight.Task;
            var green = new byte[4 * 4 * 4];
            for (var i = 0; i < green.Length; i += 4) { green[i + 1] = 255; green[i + 3] = 255; }
            return Task.FromResult(new VideoFrame(4, 4, green, "rgba"));
        });
        var timeline = new Timeline
        {
            Id = "tl1",
            Width = 4,
            Height = 4,
            Fps = 30,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips = [new Clip { Id = "c1", MediaRef = "M1", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 30 }],
                },
            ],
        };
        var manifest = new MediaManifest
        {
            Entries = [new MediaManifestEntry { Id = "M1", Name = "a.mp4", Type = ClipType.video, Source = MediaSource.External("a.mp4") }],
        };
        var resolver = new Temno.Core.IO.MediaResolver(() => manifest, () => null);
        using var provider = new PreviewFrameProvider(source, resolver, timelineLookup: null, maxDimension: null);

        // Start a pre-edit decode on frame 5, then invalidate before it finishes.
        using var cts1 = new CancellationTokenSource();
        var staleTask = provider.GetCompositedFrameAsync(timeline, 5, cts1.Token);
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref source.Calls) == 1, TimeSpan.FromSeconds(2)));

        provider.Invalidate();
        cts1.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staleTask);

        // Post-edit request on the exact same (timelineId, frame) must start call #2 immediately instead of waiting on call #1!
        using var fresh = await provider.GetCompositedFrameAsync(timeline, 5).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(fresh);
        Assert.Equal(2, Volatile.Read(ref source.Calls));
        Assert.Equal(255, fresh!.Data[1]); // green post-edit frame

        // Complete the old flight cleanly so nothing leaks.
        var red = new byte[4 * 4 * 4];
        for (var i = 0; i < red.Length; i += 4) { red[i] = 255; red[i + 3] = 255; }
        firstFlight.TrySetResult(new VideoFrame(4, 4, red, "rgba"));
    }

    [Fact]
    public async Task TimelineFrameRenderer_OddCanvasAndTextCrop_RendersCleanlyAndCachesCrop()
    {
        var rasterizer = new CountingTextRasterizer();
        using var renderer = new TimelineFrameRenderer(
            new NullFrameSource(),
            compositor: new CpuCompositor(),
            effectsRenderer: new CpuEffectsRenderer(),
            textRasterizer: rasterizer);

        var clip = new Clip
        {
            Id = "odd",
            MediaType = ClipType.text,
            TextContent = "Odd",
            StartFrame = 0,
            DurationFrames = 20,
            Transform = new Transform { CenterX = 0.45, CenterY = 0.55, Width = 0.25, Height = 0.2, Rotation = 23 },
            Crop = new Crop { Left = 0.25, Top = 0.1, Right = 0.1, Bottom = 0.2 },
        };
        var timeline = new Timeline
        {
            Width = 201,
            Height = 121,
            Fps = 30,
            Tracks = [new Track { Type = ClipType.video, Clips = [clip] }],
        };

        using var f1 = await renderer.RenderAsync(timeline, 0, 201, 121);
        using var f2 = await renderer.RenderAsync(timeline, 5, 201, 121);
        Assert.NotNull(f1);
        Assert.NotNull(f2);
        Assert.Equal(1, rasterizer.Calls);
        Assert.True(f1!.Data.SequenceEqual(f2!.Data));

        // Changing crop invalidates the cached cropped raster.
        clip.Crop.Left = 0.5;
        using var f3 = await renderer.RenderAsync(timeline, 5, 201, 121);
        Assert.NotNull(f3);
        Assert.Equal(2, rasterizer.Calls);
        Assert.False(f2.Data.SequenceEqual(f3!.Data));
    }

    [Fact]
    public void WriteScaleSizeInPlace_AnchorsCenterAcrossBothKeyframedAndStaticScale()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip
        {
            Id = "v1",
            MediaRef = "M1",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 60,
            Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 1.0, Height = 1.0 },
        };
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);
        Assert.True(KeyframeCommands.StampPosition(session, "v1", 0).Ok);
        clip = session.ActiveTimeline.Tracks[0].Clips[0];

        // ScaleTrack is inactive, PositionTrack is active: scaling statically to 50% at frame 20
        // must keep visual center at (0.5, 0.5) rather than pinning TopLeft=(0,0) and drifting to (0.25, 0.25).
        Assert.True(KeyframeCommands.WriteScaleSizeInPlace(clip, 20, 0.5, 0.5));
        Assert.Equal(0.5, clip.Transform.Width, Tol);
        Assert.Equal(0.5, clip.Transform.Height, Tol);
        Assert.Equal(0.5, clip.TransformAt(20).CenterX, Tol);
        Assert.Equal(0.5, clip.TransformAt(20).CenterY, Tol);
    }

    [Fact]
    public void TextUpdate_WithFitToContent_PreservesPositionTrackCenter_AndCopiesPerWordFrames()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(TextClipCommands.AddTexts(session,
        [
            new TextClipSpec(null, 0, 60, "Hi", new TextStyle { FontSize = 64 }, null, null),
        ], out var ids).Ok);
        var id = Assert.Single(ids);
        var clip = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == id);
        Assert.True(KeyframeCommands.StampPosition(session, id, 0).Ok);
        clip = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == id);
        var origCenter = (clip.TransformAt(0).CenterX, clip.TransformAt(0).CenterY);

        Assert.True(TextClipCommands.UpdateText(
            session, [id], null,
            new TextPatch
            {
                HasContent = true,
                Content = "Much Wider Multi Word Title",
                SetAnimation = true,
                Animation = new TextAnimation { Preset = "wordReveal", PerWordFrames = 12 },
            },
            out _, out _).Ok);

        clip = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == id);
        // Visual center at frame 0 stays anchored despite box width growing.
        Assert.Equal(origCenter.CenterX, clip.TransformAt(0).CenterX, Tol);
        Assert.Equal(origCenter.CenterY, clip.TransformAt(0).CenterY, Tol);
        // PerWordFrames is preserved in serialized TextAnimation.
        Assert.NotNull(clip.TextAnimation);
        var anim = JsonSerializer.Deserialize<TextAnimation>(clip.TextAnimation.Value.GetRawText(), JsonOptions.ForProject);
        Assert.NotNull(anim);
        Assert.Equal(12, anim!.PerWordFrames);
    }

    [Fact]
    public void UpsertScalarKey_PreservesStaticCrop_AllowsSmall4KTextScale_AndClampsVolume()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip
        {
            Id = "c1",
            MediaType = ClipType.text,
            TextContent = "4K",
            StartFrame = 0,
            DurationFrames = 60,
            Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.047, Height = 0.037 },
            Crop = new Crop { Left = 0.05, Top = 0.02, Right = 0.0, Bottom = 0.0 },
        };
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);

        // Editing crop.left via graph scalar upsert must NOT mutate static clip.Crop by reference.
        Assert.True(KeyframeCommands.UpsertScalarKey(session, "c1", "crop.left", 10, 0.35).Ok);
        clip = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.Equal(0.05, clip.Crop.Left, Tol);
        Assert.Equal(0.35, clip.CropAt(10).Left, Tol);

        // Upserting scale.b on a 4K text box (0.037 < 0.05) must NOT clamp up to 0.05 and distort it.
        Assert.True(KeyframeCommands.UpsertScalarKey(session, "c1", "scale.b", 10, 0.037).Ok);
        clip = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.Equal(0.037, clip.SizeAt(10).Y, Tol);

        // Volume graph upserts are clamped to [FloorDb, CeilingDb].
        Assert.True(KeyframeCommands.UpsertScalarKey(session, "c1", "volume", 10, 999.0).Ok);
        clip = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.Equal(VolumeScale.CeilingDb, clip.VolumeTrack!.Keyframes[0].Value, Tol);
    }

    [Fact]
    public async Task TimelineFrameRenderer_TextScaleTrack_ReachesFull400PercentWhenBaseFontScaleIsSmall()
    {
        var style = new TextStyle { FontScale = 0.25 };
        using var renderer = new TimelineFrameRenderer(
            new NullFrameSource(),
            compositor: new CpuCompositor(),
            effectsRenderer: new CpuEffectsRenderer(),
            textRasterizer: new CountingTextRasterizer());

        // Base FontScale = 0.25, Transform box = (0.05, 0.05).
        // Frame 0: effective scale 100% -> ratio = 1.0 / 0.25 = 4.0 -> size = (0.20, 0.20).
        // Frame 10: effective scale 300% -> ratio = 3.0 / 0.25 = 12.0 -> size = (0.60, 0.60).
        // Previously ratio was clamped to 4.0, making frame 10 render identically to frame 0!
        var clip = new Clip
        {
            Id = "txt",
            MediaType = ClipType.text,
            TextContent = "Hi",
            TextStyle = JsonSerializer.SerializeToElement(style, JsonOptions.ForProject),
            StartFrame = 0,
            DurationFrames = 20,
            Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.05, Height = 0.05 },
            ScaleTrack = new KeyframeTrack<AnimPair>
            {
                Keyframes =
                [
                    new Keyframe<AnimPair> { Frame = 0, Value = new AnimPair(0.20, 0.20), InterpolationOut = Interpolation.linear },
                    new Keyframe<AnimPair> { Frame = 10, Value = new AnimPair(0.60, 0.60), InterpolationOut = Interpolation.linear },
                ]
            },
        };
        var timeline = new Timeline
        {
            Width = 200,
            Height = 120,
            Fps = 30,
            Tracks = [new Track { Type = ClipType.video, Clips = [clip] }],
        };

        using var f0 = await renderer.RenderAsync(timeline, 0, 200, 120);
        using var f10 = await renderer.RenderAsync(timeline, 10, 200, 120);
        Assert.NotNull(f0);
        Assert.NotNull(f10);

        static int ColoredPixels(VideoFrame frame)
        {
            var count = 0;
            for (var i = 0; i < frame.Data.Length; i += 4)
                if (frame.Data[i] + frame.Data[i + 1] + frame.Data[i + 2] > 10) count++;
            return count;
        }

        Assert.True(ColoredPixels(f10!) > ColoredPixels(f0!) * 2,
            $"Expected 300% effective scale to render substantially larger than 100%, got f0={ColoredPixels(f0!)} vs f10={ColoredPixels(f10!)}");
    }

    private sealed class TokenObservingFrameSource : IFrameSource
    {
        private readonly Func<int, CancellationToken, Task<VideoFrame>> _handler;
        public int Calls;

        public TokenObservingFrameSource(Func<int, CancellationToken, Task<VideoFrame>> handler) => _handler = handler;

        public Task<VideoFrame> GetFrameAsync(string mediaPath, double timeSeconds, CancellationToken cancellationToken = default)
            => _handler(Interlocked.Increment(ref Calls), cancellationToken);
    }

    [Fact]
    public async Task PreviewFrameProvider_Invalidate_MaxDimensionChange_AndDispose_CancelStaleInFlightRenderToken()
    {
        var timeline = new Timeline
        {
            Id = "tl1",
            Width = 16,
            Height = 16,
            Fps = 30,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips = [new Clip { Id = "c1", MediaRef = "M1", MediaType = ClipType.video, StartFrame = 0, DurationFrames = 30 }],
                },
            ],
        };
        var manifest = new MediaManifest
        {
            Entries = [new MediaManifestEntry { Id = "M1", Name = "a.mp4", Type = ClipType.video, Source = MediaSource.External("a.mp4") }],
        };
        var resolver = new Temno.Core.IO.MediaResolver(() => manifest, () => null);

        var started = new TaskCompletionSource[3];
        var canceled = new TaskCompletionSource[3];
        for (var i = 0; i < 3; i++)
        {
            started[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            canceled[i] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var source = new TokenObservingFrameSource(async (call, ct) =>
        {
            var idx = call - 1;
            started[idx].TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                canceled[idx].TrySetResult();
                throw;
            }
            return new VideoFrame(16, 16, new byte[16 * 16 * 4], "rgba");
        });

        using var provider = new PreviewFrameProvider(source, resolver, timelineLookup: null, maxDimension: 1920);

        // 1. Invalidate() cancels in-flight decode token (and cancelling caller token alone does not).
        using var callerCts = new CancellationTokenSource();
        var req1 = provider.GetCompositedFrameAsync(timeline, 1, callerCts.Token);
        await started[0].Task.WaitAsync(TimeSpan.FromSeconds(3));
        callerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => req1);
        Assert.False(canceled[0].Task.IsCompleted);

        provider.Invalidate();
        await canceled[0].Task.WaitAsync(TimeSpan.FromSeconds(3));

        // 2. MaxDimension change cancels in-flight decode token.
        var req2 = provider.GetCompositedFrameAsync(timeline, 2);
        await started[1].Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.MaxDimension = 720;
        await canceled[1].Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => req2);

        // 3. Dispose() cancels in-flight decode token.
        var req3 = provider.GetCompositedFrameAsync(timeline, 3);
        await started[2].Task.WaitAsync(TimeSpan.FromSeconds(3));
        provider.Dispose();
        await canceled[2].Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => req3);
    }

    [Fact]
    public void TimelineToolbarLayout_UltraNarrowWidth_HidesZoomControlsAndPreservesTabFloor()
    {
        // Ultra-narrow toolbar (520px) with 3 open tabs: keeping zoom slider would squeeze tabs below
        // MinTabsScrollWidth (120px), so Stage 5 moves zoom into overflow while keeping tabs + core tools unclipped.
        var ultra = TimelineToolbarLayout.Compute(520, desiredTabsWidth: 340, openTabCount: 3, totalTimelineCount: 3);
        Assert.True(ultra.HideZoomControls);
        Assert.True(ultra.OverflowSecondaryTools);
        Assert.True(ultra.HideZoomReadout);
        Assert.True(ultra.ShowAllTimelinesButton);
        Assert.True(ultra.MaxTabsScrollWidth >= TimelineToolbarLayout.MinTabsScrollWidth);
        var usedWidth = ultra.MaxTabsScrollWidth
            + TimelineToolbarLayout.TabChromeWidth
            + TimelineToolbarLayout.AllTimelinesButtonWidth
            + TimelineToolbarLayout.CoreToolsWidth
            + TimelineToolbarLayout.OverflowButtonWidth;
        Assert.True(usedWidth <= 520, $"Expected Stage 5 layout ({usedWidth}px) to fit within 520px.");

        // Extremely narrow / zero widths never drop below MinTabsScrollWidth.
        var tiny = TimelineToolbarLayout.Compute(200, desiredTabsWidth: 220, openTabCount: 2, totalTimelineCount: 2);
        Assert.True(tiny.HideZoomControls);
        Assert.Equal(TimelineToolbarLayout.MinTabsScrollWidth, tiny.MaxTabsScrollWidth);
    }

    [Fact]
    public void TextClipCommands_FitTransform_ScalesProportionallyAcross720p1080p4K()
    {
        var style = new TextStyle { FontSize = 64, FontScale = 1.0, IsBold = true };
        var fit720 = TextClipCommands.FitTransform("Title", style, 1280, 720, 0.5, 0.5);
        var fit1080 = TextClipCommands.FitTransform("Title", style, 1920, 1080, 0.5, 0.5);
        var fit4k = TextClipCommands.FitTransform("Title", style, 3840, 2160, 0.5, 0.5);

        Assert.Equal(fit1080.Width, fit720.Width, Tol);
        Assert.Equal(fit1080.Height, fit720.Height, Tol);
        Assert.Equal(fit1080.Width, fit4k.Width, Tol);
        Assert.Equal(fit1080.Height, fit4k.Height, Tol);
        Assert.True(fit4k.Width > 0.08, $"Expected 4K title width to match 1080p (~0.093), got {fit4k.Width:0.####}");
    }

    [Fact]
    public void KeyframeCommands_GetOffsets_Nav_AndCount_IgnoreOutOfRangeKeysAfterSplitOrTrim()
    {
        var session = ProjectSession.CreateNew();
        var clip = new Clip
        {
            Id = "k1",
            MediaRef = "M1",
            MediaType = ClipType.video,
            StartFrame = 10,
            DurationFrames = 100,
            OpacityTrack = new KeyframeTrack<double>
            {
                Keyframes =
                [
                    new Keyframe<double> { Frame = 0, Value = 1.0, InterpolationOut = Interpolation.linear },
                    new Keyframe<double> { Frame = 20, Value = 0.5, InterpolationOut = Interpolation.linear },
                    new Keyframe<double> { Frame = 40, Value = 0.2, InterpolationOut = Interpolation.linear },
                    new Keyframe<double> { Frame = 80, Value = 0.9, InterpolationOut = Interpolation.linear },
                ],
            },
        };
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);

        // Split at timeline frame 60 (clip offset 50): left half retains offset 80 for interpolation
        // continuity, but visible offsets/count/nav must ignore offset 80 (>= DurationFrames=50).
        Assert.True(EditCommands.SplitClip(session, "k1", 60).Ok);
        var left = session.ActiveTimeline.Tracks[0].Clips[0];
        Assert.Equal(50, left.DurationFrames);
        Assert.Equal([0, 20, 40], KeyframeCommands.GetOffsets(left, "opacity"));
        Assert.Equal(3, KeyframeCommands.KeyframeCount(left, "opacity"));
        Assert.Null(KeyframeCommands.NextTimelineFrame(left, "opacity", 50)); // offset 40 is last visible key
        Assert.Equal(50, KeyframeCommands.PreviousTimelineFrame(left, "opacity", 59));
    }

    [Fact]
    public void KeyframeCommands_DisplayRange_AdaptsForSmallTextScaleAndExtremeValues()
    {
        var textClip = new Clip
        {
            Id = "txt",
            MediaType = ClipType.text,
            TextContent = "Title",
            StartFrame = 0,
            DurationFrames = 60,
            Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.093, Height = 0.074 },
            ScaleTrack = new KeyframeTrack<AnimPair>
            {
                Keyframes =
                [
                    new Keyframe<AnimPair> { Frame = 0, Value = new AnimPair(0.047, 0.037), InterpolationOut = Interpolation.linear },
                    new Keyframe<AnimPair> { Frame = 30, Value = new AnimPair(0.186, 0.148), InterpolationOut = Interpolation.linear },
                ],
            },
        };

        var (sMin, sMax) = KeyframeCommands.DisplayRange(textClip, "scale.a");
        Assert.Equal(0.0, sMin, Tol);
        Assert.InRange(sMax, 0.25, 0.35);

        var videoClip = new Clip
        {
            Id = "vid",
            MediaType = ClipType.video,
            StartFrame = 0,
            DurationFrames = 60,
            Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 1.0, Height = 1.0, Rotation = 540 },
            PositionTrack = new KeyframeTrack<AnimPair>
            {
                Keyframes = [new Keyframe<AnimPair> { Frame = 0, Value = new AnimPair(-2.2, 0.0) }],
            },
        };

        var (vMin, vMax) = KeyframeCommands.DisplayRange(videoClip, "scale.a");
        Assert.Equal(0.0, vMin, Tol);
        Assert.Equal(4.0, vMax, Tol);

        var (rMin, rMax) = KeyframeCommands.DisplayRange(videoClip, "rotation");
        Assert.Equal(-360.0, rMin, Tol);
        Assert.True(rMax >= 540.0);

        var (pMin, pMax) = KeyframeCommands.DisplayRange(videoClip, "position.a");
        Assert.True(pMin <= -2.2);
        Assert.Equal(2.0, pMax, Tol);
    }

    [Fact]
    public void TimelineCommands_EnsureOpenTimelineIds_AndSetActiveTimeline_KeepActiveTabVisible()
    {
        var session = ProjectSession.CreateNew(name: "Timeline 1");
        var tl1Id = session.ActiveTimeline.Id;
        Assert.True(TimelineCommands.AddTimeline(session, "Timeline 2", null, out var tl2Id).Ok);

        // Simulate a loaded project whose OpenTimelineIds only listed Timeline 1 while Timeline 2 is active.
        session.ProjectFile.OpenTimelineIds = [tl1Id];
        session.ProjectFile.ActiveTimelineId = tl2Id;

        TimelineCommands.EnsureOpenTimelineIds(session);
        Assert.Contains(tl2Id, session.ProjectFile.OpenTimelineIds);

        // Also test SetActiveTimeline when target is already active but missing from OpenTimelineIds.
        session.ProjectFile.OpenTimelineIds = [tl1Id];
        Assert.True(TimelineCommands.SetActiveTimeline(session, tl2Id).Ok);
        Assert.Contains(tl2Id, session.ProjectFile.OpenTimelineIds);
    }

    [Fact]
    public void CpuTextRasterizer_AllowsFivePercentFontScale_BelowOldTenPercentFloor()
    {
        var rasterizer = new CpuTextRasterizer();
        var style5 = new TextStyle { FontSize = 360, FontScale = 0.05 };  // 18px at 1080p
        var style10 = new TextStyle { FontSize = 360, FontScale = 0.10 }; // 36px at 1080p

        var r5 = rasterizer.Rasterize("H", style5, 320, 1080, 0.5, 0.5, 0.5, 0.5);
        var r10 = rasterizer.Rasterize("H", style10, 320, 1080, 0.5, 0.5, 0.5, 0.5);

        static int OpaquePixels(TextRasterization r)
        {
            var n = 0;
            for (var i = 3; i < r.Bgra.Length; i += 4)
                if (r.Bgra[i] > 16) n++;
            return n;
        }

        var px5 = OpaquePixels(r5);
        var px10 = OpaquePixels(r10);
        Assert.True(px5 > 0, "Expected 5% text to rasterize visible pixels.");
        Assert.True(px10 > px5 * 2, $"Expected 10% scale ({px10}px) to be strictly larger than 5% scale ({px5}px).");
    }

    [Fact]
    public void PreviewPixels_CopyToBgra_SimdSwizzleMatchesScalarAcrossAllLengthsAndInPlace()
    {
        (int W, int H)[] sizes = [(1, 1), (3, 1), (4, 1), (7, 1), (8, 1), (9, 1), (11, 3), (65, 5)];
        foreach (var (w, h) in sizes)
        {
            var len = w * h * 4;
            var rgba = new byte[len];
            for (var i = 0; i < len; i++)
                rgba[i] = (byte)((i * 37 + 13) & 0xFF);

            var expected = new byte[len];
            for (var i = 0; i < len; i += 4)
            {
                expected[i] = rgba[i + 2];
                expected[i + 1] = rgba[i + 1];
                expected[i + 2] = rgba[i];
                expected[i + 3] = rgba[i + 3];
            }

            var actual = new byte[len];
            PreviewPixels.CopyToBgra(new VideoFrame(w, h, rgba, "rgba"), actual);
            Assert.Equal(expected, actual);

            // In-place aliasing (source == destination) must also match byte-for-byte.
            var inPlace = (byte[])rgba.Clone();
            PreviewPixels.CopyToBgra(new VideoFrame(w, h, inPlace, "rgba"), inPlace);
            Assert.Equal(expected, inPlace);

            // BGRA passthrough copies verbatim without channel swap.
            var bgraOut = new byte[len];
            PreviewPixels.CopyToBgra(new VideoFrame(w, h, rgba, "bgra"), bgraOut);
            Assert.Equal(rgba, bgraOut);
        }
    }

    [Fact]
    public void ClipRefit_ForCanvasChange_TextClipsScaleWidthWithAspectAndPreserveKeyframes()
    {
        var session = ProjectSession.CreateNew(name: "Timeline 1", fps: 30, width: 3840, height: 2160);
        Assert.True(TextClipCommands.AddTexts(session,
        [
            new TextClipSpec(null, 0, 90, "Title", new TextStyle { FontSize = 96 }, null, null),
        ], out var ids).Ok);
        var id = Assert.Single(ids);
        var clip = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == id);
        var origW = clip.Transform.Width;
        var origH = clip.Transform.Height;

        Assert.True(KeyframeCommands.StampScale(session, id, 0).Ok);
        Assert.True(KeyframeCommands.StampPosition(session, id, 0).Ok);
        clip = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == id);
        Assert.True(KeyframeCommands.WriteScaleSizeInPlace(clip, 30, origW * 2.0, origH * 2.0));
        Assert.True(KeyframeCommands.WritePositionCenterInPlace(clip, 30, centerX: 0.65, centerY: 0.4));

        // Switch 16:9 4K -> 9:16 1080p: text box width must widen by (16/9)/(9/16) so "Title" stays single-line!
        Assert.True(EditCommands.SetProjectSettings(session, null, null, null, "9:16", "1080p").Ok);
        clip = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == id);
        var aspectRatioScale = (3840.0 / 2160.0) / (1080.0 / 1920.0);
        Assert.Equal(Math.Clamp(origW * aspectRatioScale, 0.05, 0.96), clip.Transform.Width, Tol);
        Assert.Equal(origH, clip.Transform.Height, Tol);

        // ScaleTrack ratios (1.0x at f0, 2.0x at f30) and PositionTrack centers stay exact.
        Assert.Equal(1.0, clip.SizeAt(0).X / clip.Transform.Width, Tol);
        Assert.Equal(2.0, clip.SizeAt(30).X / clip.Transform.Width, Tol);
        Assert.Equal(0.5, clip.TransformAt(0).CenterX, Tol);
        Assert.Equal(0.65, clip.TransformAt(30).CenterX, Tol);
        Assert.Equal(0.4, clip.TransformAt(30).CenterY, Tol);

        // Round-trip back to 16:9 4K restores exact original width and keyframe centers.
        Assert.True(EditCommands.SetProjectSettings(session, null, null, null, "16:9", "4K").Ok);
        clip = session.ActiveTimeline.Tracks[0].Clips.Single(c => c.Id == id);
        Assert.Equal(origW, clip.Transform.Width, Tol);
        Assert.Equal(0.65, clip.TransformAt(30).CenterX, Tol);
    }

    [Fact]
    public void KeyframeCommands_ToggleStampInPlace_CombinesWithLiveEditAsSingleUndoStep()
    {
        var session = ProjectSession.CreateNew(name: "Timeline 1", fps: 30, width: 1920, height: 1080);
        var clip = new Clip
        {
            Id = "t-atomic",
            MediaRef = "",
            MediaType = ClipType.text,
            TextContent = "Title",
            StartFrame = 0,
            DurationFrames = 90,
            Opacity = 1.0,
            Transform = new Transform { CenterX = 0.5, CenterY = 0.5, Width = 0.2, Height = 0.1 },
        };
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);

        // Simulate typing 50% opacity on an unkeyframed clip and clicking ◆ in the same live gesture.
        var live = new ClipLiveEdit();
        live.Begin(session);
        live.Mutate(() =>
        {
            var c = session.ActiveTimeline.Tracks[0].Clips.Single(x => x.Id == "t-atomic");
            c.Opacity = 0.5;
            Assert.True(KeyframeCommands.ToggleStampInPlace(c, "opacity", 0).Ok);
        });
        live.End(keep: true);

        var after = session.ActiveTimeline.Tracks[0].Clips.Single(x => x.Id == "t-atomic");
        Assert.Equal(0.5, after.Opacity, Tol);
        Assert.True(KeyframeCommands.HasKeyframeAt(after, "opacity", 0));

        // ONE undo reverts BOTH the opacity value AND the newly stamped keyframe.
        session.Undo();
        var undone = session.ActiveTimeline.Tracks[0].Clips.Single(x => x.Id == "t-atomic");
        Assert.Equal(1.0, undone.Opacity, Tol);
        Assert.False(KeyframeCommands.HasKeyframeAt(undone, "opacity", 0));
    }

    [Fact]
    public void TimelineToolbarLayout_CalibratedWidths_FitFourTabsWithoutPrematureScrolling()
    {
        // Four typical tabs (~480 DIP realized) on a 1222 DIP wide desktop (1860px @ 150% DPI):
        // all 4 tabs and full editing tools fit without scrolling or showing the All-timelines chevron.
        var wide4 = TimelineToolbarLayout.Compute(1222, desiredTabsWidth: 480, openTabCount: 4, totalTimelineCount: 4);
        Assert.Equal(480, wide4.MaxTabsScrollWidth);
        Assert.False(wide4.OverflowSecondaryTools);
        Assert.False(wide4.ShowAllTimelinesButton);
        Assert.True(wide4.HideZoomReadout);

        // Same 4 tabs on a 957 DIP compact window (1462px @ 150% DPI): collapsing secondary tools
        // keeps all 4 tabs completely unclipped without scrolling!
        var compact4 = TimelineToolbarLayout.Compute(957, desiredTabsWidth: 480, openTabCount: 4, totalTimelineCount: 4);
        Assert.Equal(480, compact4.MaxTabsScrollWidth);
        Assert.True(compact4.OverflowSecondaryTools);
        Assert.False(compact4.ShowAllTimelinesButton);
    }
}
