using System.Runtime.CompilerServices;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

public class FrameRequestTests
{
    private sealed class FakeFrameSource : IFrameSource
    {
        public Dictionary<string, VideoFrame> FramesByPath { get; } = [];
        public HashSet<string> MissingPaths { get; } = [];
        public List<(string Path, double Seconds)> Calls { get; } = [];
        public Func<string, double, Task<VideoFrame>>? Handler { get; set; }

        public Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add((path, sourceSeconds));
            }

            if (Handler is not null) return Handler(path, sourceSeconds);
            if (MissingPaths.Contains(path))
                return Task.FromException<VideoFrame>(new FileNotFoundException("Media file not found.", path));

            lock (Calls)
            {
                if (!FramesByPath.TryGetValue(path, out var frame))
                {
                    frame = NewFrame();
                    FramesByPath[path] = frame;
                }

                return Task.FromResult(frame);
            }
        }

        public static VideoFrame NewFrame() => new(1, 1, new byte[4], "rgba");
    }

    private static Clip MakeClip(
        string mediaRef,
        int start,
        int duration,
        int trimStart = 0,
        double speed = 1.0,
        ClipType type = ClipType.video)
        => new()
        {
            MediaRef = mediaRef,
            MediaType = type,
            SourceClipType = type,
            StartFrame = start,
            DurationFrames = duration,
            TrimStartFrame = trimStart,
            Speed = speed,
        };

    private static Track VideoTrack(params Clip[] clips) => new() { Type = ClipType.video, Clips = [.. clips] };

    private static Timeline MakeTimeline(params Track[] tracks) => new() { Fps = 30, Tracks = [.. tracks] };

    private static MediaResolver MakeResolver(params (string Id, string Path)[] entries)
    {
        var manifest = new MediaManifest
        {
            Entries = entries
                .Select(e => new MediaManifestEntry { Id = e.Id, Source = MediaSource.External(e.Path) })
                .ToList(),
        };
        return new MediaResolver(() => manifest, () => null);
    }

    [Fact]
    public void SourceSeconds_Respects_Trim()
    {
        var clip = MakeClip("a", start: 0, duration: 60, trimStart: 15);
        Assert.Equal(1.5, TimelineFrameResolver.SourceSecondsAt(clip, 30, fps: 30), precision: 9);
    }

    [Fact]
    public void SourceSeconds_Respects_Speed_And_StartFrame()
    {
        var clip = MakeClip("a", start: 100, duration: 30, trimStart: 30, speed: 2.0);
        // source frame = 30 + (130 - 100) * 2 = 90 → 3.0 s at 30 fps
        Assert.Equal(3.0, TimelineFrameResolver.SourceSecondsAt(clip, 130, fps: 30), precision: 9);
        // 3.0s is the exclusive out-point (EndFrame 130); inverse clamps to the last visible frame.
        Assert.Equal(129, TimelineFrameResolver.TimelineFrameAt(clip, 3.0, fps: 30));
    }

    [Fact]
    public void TimelineFrameAt_Inverts_SourceSecondsAt()
    {
        var clip = MakeClip("a", start: 0, duration: 60, trimStart: 15);
        for (var frame = 0; frame < 60; frame += 7)
        {
            var seconds = TimelineFrameResolver.SourceSecondsAt(clip, frame, fps: 30);
            Assert.Equal(frame, TimelineFrameResolver.TimelineFrameAt(clip, seconds, fps: 30));
        }
    }

    [Fact]
    public void Resolve_Uses_HalfOpen_Clip_Interval()
    {
        var timeline = MakeTimeline(VideoTrack(MakeClip("a", start: 10, duration: 20)));

        Assert.Empty(TimelineFrameResolver.Resolve(timeline, 9));
        Assert.Single(TimelineFrameResolver.Resolve(timeline, 10));
        Assert.Single(TimelineFrameResolver.Resolve(timeline, 29));
        Assert.Empty(TimelineFrameResolver.Resolve(timeline, 30));
    }

    [Fact]
    public void Resolve_Skips_Overlapping_Clip_Entirely()
    {
        var a = MakeClip("a", start: 0, duration: 60);
        var b = MakeClip("b", start: 30, duration: 60); // overlaps a → skipped entirely (Mac rule)
        var timeline = MakeTimeline(VideoTrack(a, b));

        var at40 = TimelineFrameResolver.Resolve(timeline, 40);
        Assert.Single(at40);
        Assert.Equal(a.Id, at40[0].ClipId);

        // b does not become visible even after a ends
        Assert.Empty(TimelineFrameResolver.Resolve(timeline, 70));
    }

    [Fact]
    public void Resolve_Excludes_Hidden_Audio_And_Text()
    {
        var hiddenTrack = VideoTrack(MakeClip("hidden", 0, 100));
        hiddenTrack.Hidden = true;
        var audioTrack = new Track { Type = ClipType.audio, Clips = [MakeClip("aud", 0, 100, type: ClipType.audio)] };
        var textOnVideoTrack = VideoTrack(MakeClip("txt", 0, 100, type: ClipType.text));
        var visible = VideoTrack(MakeClip("vis", 0, 100));
        var timeline = MakeTimeline(hiddenTrack, audioTrack, textOnVideoTrack, visible);

        var layers = TimelineFrameResolver.Resolve(timeline, 50);
        Assert.Single(layers);
        Assert.Equal("vis", layers[0].MediaRef);
    }

    [Fact]
    public void Resolve_Orders_Layers_BottomToTop()
    {
        var top = VideoTrack(MakeClip("top", 0, 100));
        var bottom = VideoTrack(MakeClip("bottom", 0, 100));
        var timeline = MakeTimeline(top, bottom); // Tracks[0] is topmost

        var layers = TimelineFrameResolver.Resolve(timeline, 10);
        Assert.Equal(2, layers.Count);
        Assert.Equal("bottom", layers[0].MediaRef);
        Assert.Equal(1, layers[0].TrackOrderIndex);
        Assert.Equal("top", layers[1].MediaRef);
        Assert.Equal(0, layers[1].TrackOrderIndex);
    }

    [Fact]
    public void Resolve_Skips_Zero_Opacity_Layer()
    {
        var clip = MakeClip("a", 0, 100);
        clip.Opacity = 0.0;
        var timeline = MakeTimeline(VideoTrack(clip));

        Assert.Empty(TimelineFrameResolver.Resolve(timeline, 10));
    }

    [Fact]
    public void Image_Clip_SourceSeconds_Is_Zero()
    {
        var clip = MakeClip("img", 0, 100, trimStart: 90, type: ClipType.image);
        var timeline = MakeTimeline(VideoTrack(clip));

        var layers = TimelineFrameResolver.Resolve(timeline, 50);
        Assert.Single(layers);
        Assert.Equal(0.0, layers[0].SourceSeconds);
    }

    [Fact]
    public void Resolve_Maps_MediaPath_Via_MediaResolver()
    {
        var timeline = MakeTimeline(VideoTrack(MakeClip("asset-a", 0, 100)));
        var resolver = MakeResolver(("asset-a", "/media/a.mp4"));

        var withResolver = TimelineFrameResolver.Resolve(timeline, 10, resolver);
        Assert.Equal("/media/a.mp4", withResolver[0].MediaPath);

        var withoutResolver = TimelineFrameResolver.Resolve(timeline, 10);
        Assert.Null(withoutResolver[0].MediaPath);
    }

    [Fact]
    public async Task Provider_Returns_TopMost_Frame_With_Correct_Source_Seconds()
    {
        var top = VideoTrack(MakeClip("top", 0, 100, trimStart: 30));
        var bottom = VideoTrack(MakeClip("bottom", 0, 100));
        var timeline = MakeTimeline(top, bottom);
        var resolver = MakeResolver(("top", "/media/top.mp4"), ("bottom", "/media/bottom.mp4"));
        var source = new FakeFrameSource();
        var provider = new PreviewFrameProvider(source, resolver);

        var frame = await provider.GetTopMostFrameAsync(timeline, 15);

        Assert.NotNull(frame);
        Assert.Same(source.FramesByPath["/media/top.mp4"], frame);
        var call = Assert.Single(source.Calls);
        Assert.Equal("/media/top.mp4", call.Path);
        Assert.Equal((30 + 15) / 30.0, call.Seconds, precision: 9);
    }

    [Fact]
    public async Task Provider_Falls_Back_When_TopMost_Is_Offline()
    {
        var top = VideoTrack(MakeClip("top", 0, 100));
        var bottom = VideoTrack(MakeClip("bottom", 0, 100));
        var timeline = MakeTimeline(top, bottom);
        var resolver = MakeResolver(("top", "/media/top.mp4"), ("bottom", "/media/bottom.mp4"));
        var source = new FakeFrameSource();
        source.MissingPaths.Add("/media/top.mp4");
        var provider = new PreviewFrameProvider(source, resolver);

        var frame = await provider.GetTopMostFrameAsync(timeline, 10);

        Assert.NotNull(frame);
        Assert.Same(source.FramesByPath["/media/bottom.mp4"], frame);
    }

    [Fact]
    public async Task Provider_Returns_Null_When_No_Visible_Layer()
    {
        var timeline = MakeTimeline(VideoTrack());
        var provider = new PreviewFrameProvider(new FakeFrameSource(), MakeResolver());

        Assert.Null(await provider.GetTopMostFrameAsync(timeline, 0));
    }

    [Fact]
    public async Task Provider_GetLayerFrames_Returns_All_Layers_BottomToTop()
    {
        var top = VideoTrack(MakeClip("top", 0, 100));
        var bottom = VideoTrack(MakeClip("bottom", 0, 100));
        var timeline = MakeTimeline(top, bottom);
        var resolver = MakeResolver(("top", "/media/top.mp4"), ("bottom", "/media/bottom.mp4"));
        var source = new FakeFrameSource();
        var provider = new PreviewFrameProvider(source, resolver);

        var layers = await provider.GetLayerFramesAsync(timeline, 10);

        Assert.Equal(2, layers.Count);
        Assert.Equal("bottom", layers[0].Layer.MediaRef);
        Assert.Same(source.FramesByPath["/media/bottom.mp4"], layers[0].Frame);
        Assert.Equal("top", layers[1].Layer.MediaRef);
        Assert.Same(source.FramesByPath["/media/top.mp4"], layers[1].Frame);
    }

    [Fact]
    public async Task Provider_Coalesces_Duplicate_Inflight_Requests()
    {
        var timeline = MakeTimeline(VideoTrack(MakeClip("a", 0, 100)));
        var resolver = MakeResolver(("a", "/media/a.mp4"));
        var source = new FakeFrameSource();
        var provider = new PreviewFrameProvider(source, resolver);

        var tcs = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Handler = (_, _) => tcs.Task;

        var first = provider.GetTopMostFrameAsync(timeline, 10);
        var second = provider.GetTopMostFrameAsync(timeline, 10);
        var otherFrame = provider.GetTopMostFrameAsync(timeline, 11);

        Assert.Equal(2, source.Calls.Count); // frame 10 decoded once, frame 11 separately

        var decoded = FakeFrameSource.NewFrame();
        tcs.SetResult(decoded);

        Assert.Same(decoded, await first);
        Assert.Same(decoded, await second);
        Assert.Same(decoded, await otherFrame);

        // Completed requests are not cached: a later request decodes again.
        await provider.GetTopMostFrameAsync(timeline, 10);
        Assert.Equal(3, source.Calls.Count);
    }

    [Fact]
    public async Task Provider_Cancellation_Abandons_Wait_Without_Failing_Other_Waiters()
    {
        var timeline = MakeTimeline(VideoTrack(MakeClip("a", 0, 100)));
        var resolver = MakeResolver(("a", "/media/a.mp4"));
        var source = new FakeFrameSource();
        var provider = new PreviewFrameProvider(source, resolver);

        var tcs = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Handler = (_, _) => tcs.Task;

        using var cts = new CancellationTokenSource();
        var cancelled = provider.GetTopMostFrameAsync(timeline, 10, cts.Token);
        var surviving = provider.GetTopMostFrameAsync(timeline, 10);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        var decoded = FakeFrameSource.NewFrame();
        tcs.SetResult(decoded);
        Assert.Same(decoded, await surviving);
    }

    [Fact]
    public async Task CoalescedCallers_OfAPooledFrame_EachOwnAHandle_AndTheBufferReturnsAfterTheLast()
    {
        var timeline = MakeTimeline(VideoTrack(MakeClip("a", 0, 100)));
        var resolver = MakeResolver(("a", "/media/a.mp4"));
        var source = new FakeFrameSource();
        var provider = new PreviewFrameProvider(source, resolver);
        var pool = new FrameBufferPool();

        var tcs = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Handler = (_, _) => tcs.Task;

        var first = provider.GetTopMostFrameAsync(timeline, 10);
        var second = provider.GetTopMostFrameAsync(timeline, 10);
        var decoded = pool.Rent(1, 1, "rgba");
        tcs.SetResult(decoded);

        var a = await first;
        var b = await second;
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a, b);
        Assert.Same(decoded.Data, a.Data);
        Assert.Same(decoded.Data, b.Data);

        // WP-248: one caller finishing must not recycle pixels the other is still reading.
        a.Dispose();
        Assert.Equal(0, pool.RetainedCount);
        b.Dispose();
        Assert.Equal(1, pool.RetainedCount);
    }

    [Fact]
    public async Task APooledFrameEveryCallerGaveUpOn_IsReleasedWhenTheDecodeFinishes()
    {
        var timeline = MakeTimeline(VideoTrack(MakeClip("a", 0, 100)));
        var resolver = MakeResolver(("a", "/media/a.mp4"));
        var source = new FakeFrameSource();
        var provider = new PreviewFrameProvider(source, resolver);
        var pool = new FrameBufferPool();

        var tcs = new TaskCompletionSource<VideoFrame>();
        source.Handler = (_, _) => tcs.Task;

        using var cts = new CancellationTokenSource();
        var abandoned = provider.GetTopMostFrameAsync(timeline, 10, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        tcs.SetResult(pool.Rent(1, 1, "rgba"));

        Assert.True(SpinWait.SpinUntil(() => pool.RetainedCount == 1, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Provider_CancelledCallerBeforeFaultingDecode_DoesNotHangOrLeaveUnobservedException()
    {
        var timeline = MakeTimeline(VideoTrack(MakeClip("a", 0, 100)));
        var resolver = MakeResolver(("a", "/media/a.mp4"));
        var source = new FakeFrameSource();
        using var provider = new PreviewFrameProvider(source, resolver);
        var sentinel = $"cancelled-decode-fault-{Guid.NewGuid():N}";
        var unobserved = new List<Exception>();

        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, args) =>
        {
            if (args.Exception.Flatten().InnerExceptions.Any(ex => ex.Message.Contains(sentinel, StringComparison.Ordinal)))
            {
                lock (unobserved)
                    unobserved.Add(args.Exception);
                args.SetObserved();
            }
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            await TriggerCancelledFaultingFlightsAsync(provider, timeline, source, sentinel);

            // Yield briefly so the faulted RunAsync continuation completes and drops its references,
            // then force finalization so any unobserved Task exception would fire deterministically.
            await Task.Delay(25);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }

            Assert.Empty(unobserved);

            // Confirm the faulted flight was evicted from _inflight so subsequent decodes succeed.
            var recovered = await provider.GetTopMostFrameAsync(timeline, 10);
            Assert.NotNull(recovered);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task TriggerCancelledFaultingFlightsAsync(
        PreviewFrameProvider provider,
        Timeline timeline,
        FakeFrameSource source,
        string sentinel)
    {
        // Case 1: caller cancels while async decode is in flight, then decode faults.
        var tcs = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Handler = (_, _) => tcs.Task;

        using (var cts = new CancellationTokenSource())
        {
            var abandoned = provider.GetTopMostFrameAsync(timeline, 10, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        tcs.SetException(new InvalidOperationException($"{sentinel}-async"));

        // Case 2: pre-cancelled token on an async decode that faults after the caller abandons it.
        var tcs2 = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Handler = (_, _) => tcs2.Task;
        using (var preCancelled = new CancellationTokenSource())
        {
            preCancelled.Cancel();
            var abandonedPreCancelled = provider.GetTopMostFrameAsync(timeline, 11, preCancelled.Token);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandonedPreCancelled.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        tcs2.SetException(new InvalidOperationException($"{sentinel}-precancelled"));

        // Case 3: GetCompositedFrameAsync (the exact live preview entry point) cancelled before faulting.
        var started3 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs3 = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Handler = (_, _) =>
        {
            started3.TrySetResult();
            return tcs3.Task;
        };
        using (var compositedCts = new CancellationTokenSource())
        {
            var abandonedComposited = provider.GetCompositedFrameAsync(timeline, 12, compositedCts.Token);
            await started3.Task.WaitAsync(TimeSpan.FromSeconds(5));
            compositedCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandonedComposited.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        tcs3.SetException(new InvalidOperationException($"{sentinel}-composited"));

        // Case 4: synchronous fault with a pre-cancelled caller.
        source.Handler = (_, _) => throw new InvalidOperationException($"{sentinel}-sync");
        using (var syncCts = new CancellationTokenSource())
        {
            syncCts.Cancel();
            var abandonedSync = provider.GetTopMostFrameAsync(timeline, 13, syncCts.Token);
            await Assert.ThrowsAnyAsync<Exception>(() => abandonedSync.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        source.Handler = null;
    }

    [Fact]
    public async Task Provider_CancelledCallerBeforeFaultingDecode_SurvivingCallerStillObservesFault()
    {
        var timeline = MakeTimeline(VideoTrack(MakeClip("a", 0, 100)));
        var resolver = MakeResolver(("a", "/media/a.mp4"));
        var source = new FakeFrameSource();
        using var provider = new PreviewFrameProvider(source, resolver);

        var tcs = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Handler = (_, _) => tcs.Task;

        using var cts = new CancellationTokenSource();
        var cancelled = provider.GetTopMostFrameAsync(timeline, 20, cts.Token);
        var surviving = provider.GetTopMostFrameAsync(timeline, 20);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(5)));

        tcs.SetException(new InvalidOperationException("shared-decode-fault"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => surviving.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("shared-decode-fault", ex.Message);
    }

    [Fact]
    public async Task Provider_GetLayerFrames_ReleasesEarlierPooledFrameWhenLaterLayerFaults()
    {
        var top = VideoTrack(MakeClip("top", 0, 100));
        var bottom = VideoTrack(MakeClip("bottom", 0, 100));
        var timeline = MakeTimeline(top, bottom);
        var resolver = MakeResolver(("top", "/media/top.mp4"), ("bottom", "/media/bottom.mp4"));
        var source = new FakeFrameSource();
        var pool = new FrameBufferPool();
        source.Handler = (path, _) => path.EndsWith("bottom.mp4", StringComparison.Ordinal)
            ? Task.FromResult(pool.Rent(1, 1, "rgba"))
            : Task.FromException<VideoFrame>(new InvalidOperationException("top-layer-fault"));
        using var provider = new PreviewFrameProvider(source, resolver);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetLayerFramesAsync(timeline, 10));
        Assert.Equal("top-layer-fault", ex.Message);
        Assert.Equal(1, pool.RetainedCount);
    }
}
