using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Cache;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-164: the proxy pipeline once it is actually wired. WP-25's own tests
/// (<see cref="MediaCacheTests"/>) cover the cache primitives in isolation; these cover the parts
/// that only exist now that preview decodes through them - the generation trigger, the budget and
/// eviction under a real proxy cache, invalidation when the source changes, and the guarantee that
/// none of it reaches export.
/// </summary>
public sealed class ProxyPipelineTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly MediaCachePaths _paths;

    public ProxyPipelineTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"TemnoProxyPipelineTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _paths = new MediaCachePaths(Path.Combine(_tempRoot, "cache"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); }
        catch { /* cleanup only */ }
    }

    // ---- target resolution ----

    [Theory]
    // 4K landscape and 1440p land exactly on the preview raster.
    [InlineData(3840, 2160, 1280, 720)]
    [InlineData(2560, 1440, 1280, 720)]
    // Portrait is the case a fixed 720 "short side" gets wrong: scaling HEIGHT to 720 would leave a
    // 405-wide proxy that preview then has to upscale back to 720x1280.
    [InlineData(2160, 3840, 720, 1280)]
    [InlineData(1080, 1920, 720, 1280)]
    // Already at or under the preview raster: the target is the source's own height.
    [InlineData(1280, 720, 1280, 720)]
    public void TargetHeightFor_LandsOnTheRasterPreviewAlreadyDecodesAt(
        int sourceWidth, int sourceHeight, int expectedWidth, int expectedHeight)
    {
        var target = ProxyPolicy.TargetHeightFor(sourceWidth, sourceHeight, PreviewRaster.PreviewMaxDimension);

        Assert.Equal(expectedHeight, target);
        var fit = PreviewRaster.Fit(sourceWidth, sourceHeight, PreviewRaster.PreviewMaxDimension);
        Assert.Equal((expectedWidth, expectedHeight), fit);
    }

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(2560, 1440)]
    [InlineData(2160, 3840)]
    [InlineData(1920, 1080)]
    public void ProxySizedToTheRaster_DecodesToTheSameFrameSizeAsTheOriginal(int sourceWidth, int sourceHeight)
    {
        // WP-163 made the composite fast partly by handing a full-canvas opaque layer straight
        // through (TimelineFrameRenderer.IsFullFrameOpaquePassthrough), which requires the decoded
        // frame to be exactly canvas-sized. Sizing the proxy to Fit() keeps that true: fitting an
        // already-fitted frame is the identity, so swapping in a proxy cannot change the raster.
        var original = PreviewRaster.Fit(sourceWidth, sourceHeight, PreviewRaster.PreviewMaxDimension);
        var targetHeight = ProxyPolicy.TargetHeightFor(sourceWidth, sourceHeight, PreviewRaster.PreviewMaxDimension);
        var proxyWidth = original.Width;

        var viaProxy = PreviewRaster.Fit(proxyWidth, targetHeight, PreviewRaster.PreviewMaxDimension);

        Assert.Equal(original, viaProxy);
    }

    // ---- policy ----

    [Theory]
    [InlineData(3840, 2160, "h264", true)]   // bigger than preview ever shows
    [InlineData(2560, 1440, "h264", true)]
    [InlineData(2160, 3840, "h264", true)]   // portrait, judged on the long edge
    [InlineData(1280, 720, "h264", false)]   // already the preview raster
    [InlineData(1920, 1080, "hevc", true)]   // expensive decode is worth a rewrap
    [InlineData(1280, 720, "av1", true)]
    [InlineData(1280, 720, null, false)]
    [InlineData(null, null, "hevc", true)]   // unknown size + expensive codec: cannot rule it out
    [InlineData(null, null, null, false)]    // nothing known: do not transcode on a guess
    public void ShouldProxyForPreview_JudgesAgainstThePreviewRaster(
        int? width, int? height, string? codec, bool expected)
    {
        Assert.Equal(
            expected,
            ProxyPolicy.Default.ShouldProxyForPreview(width, height, codec, PreviewRaster.PreviewMaxDimension));
    }

    // ---- generation trigger ----

    [Fact]
    public async Task FirstPreviewDemand_ReturnsTheOriginal_AndQueuesTheProxy()
    {
        var source = WriteSourceFile();
        var transcodes = 0;
        using var selector = NewSelector(
            (_, target, height, _) => { Interlocked.Increment(ref transcodes); return WriteProxyAsync(target, height); });

        // The trigger is first demand, not import: preview must not block on a transcode, so the
        // very first call still decodes the original.
        var first = selector.SelectDecodePath(source, Probe(3840, 2160));
        Assert.Equal(source, first);
        Assert.Equal(ProxyState.Generating, selector.StatusFor(source).State);

        await WaitForStateAsync(selector, source, ProxyState.Ready);

        var second = selector.SelectDecodePath(source, Probe(3840, 2160));
        Assert.NotEqual(source, second);
        Assert.True(File.Exists(second));
        Assert.Equal(_paths.ProxiesDirectory, Path.GetDirectoryName(second));
        Assert.Equal(1, transcodes);

        // A second demand for a cached proxy must not re-transcode.
        selector.SelectDecodePath(source, Probe(3840, 2160));
        Assert.Equal(1, transcodes);
    }

    [Fact]
    public void SourceThatDoesNotWarrantAProxy_IsNeverTranscoded()
    {
        var source = WriteSourceFile();
        using var selector = NewSelector((_, _, _, _) => throw new InvalidOperationException("must not transcode"));

        var selected = selector.SelectDecodePath(source, Probe(1280, 720, "h264"));

        Assert.Equal(source, selected);
        Assert.Equal(ProxyState.NotWarranted, selector.StatusFor(source).State);
    }

    [Fact]
    public void UnknownSourceDimensions_FallBackToTheOriginal()
    {
        var source = WriteSourceFile();
        using var selector = NewSelector((_, _, _, _) => throw new InvalidOperationException("must not transcode"));

        // ShouldProxyForPreview says yes on an expensive codec alone, but with no dimensions there
        // is nothing to size a proxy against.
        var selected = selector.SelectDecodePath(source, Probe(null, null, "hevc"));

        Assert.Equal(source, selected);
        Assert.Equal(ProxyState.NotWarranted, selector.StatusFor(source).State);
    }

    [Fact]
    public async Task FailedTranscode_KeepsPreviewOnTheOriginal_AndIsNotRetried()
    {
        var source = WriteSourceFile();
        var attempts = 0;
        using var selector = NewSelector((_, _, _, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("ffmpeg exited with code 1");
        });

        selector.SelectDecodePath(source, Probe(3840, 2160));
        await WaitForStateAsync(selector, source, ProxyState.Failed);

        Assert.Equal(source, selector.SelectDecodePath(source, Probe(3840, 2160)));
        Assert.Equal(source, selector.SelectDecodePath(source, Probe(3840, 2160)));
        Assert.Equal(1, attempts);
        Assert.Contains("ffmpeg exited", selector.StatusFor(source).Detail);
    }

    [Fact]
    public async Task ProxyThatGoesMissingAfterBeingHandedOut_IsRetired()
    {
        var source = WriteSourceFile();
        using var selector = NewSelector((_, target, height, _) => WriteProxyAsync(target, height));

        selector.SelectDecodePath(source, Probe(3840, 2160));
        await WaitForStateAsync(selector, source, ProxyState.Ready);
        var proxy = selector.SelectDecodePath(source, Probe(3840, 2160));
        Assert.NotEqual(source, proxy);

        // Evicted, or deleted underneath us. Preview reports it and drops back.
        File.Delete(proxy);
        selector.ReportProxyUnusable(source, "file missing");

        Assert.Equal(source, selector.SelectDecodePath(source, Probe(3840, 2160)));
        Assert.Equal(ProxyState.Failed, selector.StatusFor(source).State);
    }

    // ---- invalidation ----

    [Fact]
    public async Task ChangedSource_IsNotServedTheOldProxy()
    {
        var source = WriteSourceFile();
        using var selector = NewSelector((_, target, height, _) => WriteProxyAsync(target, height));

        selector.SelectDecodePath(source, Probe(3840, 2160));
        await WaitForStateAsync(selector, source, ProxyState.Ready);
        var firstProxy = selector.SelectDecodePath(source, Probe(3840, 2160));
        Assert.NotEqual(source, firstProxy);

        // Re-render the source: different bytes, different mtime. The key is derived from
        // path + size + mtime, so the stale proxy is simply never looked up again.
        File.WriteAllText(source, "different media bytes entirely");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(30));

        using var afterChange = NewSelector((_, target, height, _) => WriteProxyAsync(target, height));
        var selected = afterChange.SelectDecodePath(source, Probe(3840, 2160));

        Assert.Equal(source, selected);
        Assert.Equal(ProxyState.Generating, afterChange.StatusFor(source).State);
        await WaitForStateAsync(afterChange, source, ProxyState.Ready);
        var secondProxy = afterChange.SelectDecodePath(source, Probe(3840, 2160));
        Assert.NotEqual(firstProxy, secondProxy);
    }

    [Fact]
    public async Task FailedTranscode_IsRetriedAfterTheSourceIsReplaced()
    {
        // WP-164 T1 finding (P2, reproduced). A failed transcode must retire that CONTENT, not the
        // path: the app holds one selector for the life of the preview surface, so keying the
        // failure by path alone meant a file that failed once could never be proxied again for the
        // session - even after the owner re-rendered it into something that transcodes fine. That
        // is the opposite of the invalidation this class documents.
        var source = WriteSourceFile();
        var attempts = 0;
        var failFirstContent = true;

        using var selector = NewSelector((_, target, height, _) =>
        {
            Interlocked.Increment(ref attempts);
            if (failFirstContent)
                throw new InvalidOperationException("ffmpeg exited with code 1");
            return WriteProxyAsync(target, height);
        });

        selector.SelectDecodePath(source, Probe(3840, 2160));
        await WaitForStateAsync(selector, source, ProxyState.Failed);
        Assert.Equal(source, selector.SelectDecodePath(source, Probe(3840, 2160)));
        Assert.Equal(1, attempts);

        // Same path, different content: new size and mtime, so a new cache key.
        failFirstContent = false;
        File.WriteAllText(source, "a re-rendered source with quite different bytes");
        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddSeconds(30));

        // The SAME long-lived selector must try again rather than stay retired on the old failure.
        Assert.Equal(source, selector.SelectDecodePath(source, Probe(3840, 2160)));
        await WaitForStateAsync(selector, source, ProxyState.Ready);
        Assert.Equal(2, attempts);

        var proxy = selector.SelectDecodePath(source, Probe(3840, 2160));
        Assert.NotEqual(source, proxy);
        Assert.True(File.Exists(proxy));
    }

    [Fact]
    public void TrimAndSpeed_DoNotEnterTheProxyKey()
    {
        // Deliberate, and worth pinning: a proxy is a whole-file substitute addressed on the same
        // source-seconds axis as the original, so re-trimming or retiming a clip changes which
        // offset is asked for, never whether the proxy is still valid. Putting trim or speed in the
        // key would churn a full transcode on every trim handle drag.
        var source = WriteSourceFile();

        var key = ProxyCache.KeyFor(source, 720);
        var sameKeyAgain = ProxyCache.KeyFor(source, 720);
        var differentHeight = ProxyCache.KeyFor(source, 1280);

        Assert.Equal(key, sameKeyAgain);
        Assert.NotEqual(key, differentHeight);
    }

    // ---- budget and eviction ----

    [Fact]
    public async Task ProxyCache_EvictsOldestOnceOverBudget_AndKeepsTheFreshProxy()
    {
        // WP-179: the handout grace is switched off here so raw LRU ordering is what is under test.
        // The grace has its own tests in MediaCacheTests; leaving it on would make this pass because
        // the proxy was handed out a moment ago rather than because it is the freshest entry.
        var cache = new ProxyCache(_paths, maxTotalBytes: 4_096, handoutGrace: TimeSpan.Zero);
        var oldSource = WriteSourceFile("old.mp4");
        var newSource = WriteSourceFile("new.mp4");

        var oldProxy = await cache.GetOrCreateAsync(
            oldSource, 720, (target, _) => WriteProxyAsync(target, 720, bytes: 3_072));
        // Make the first proxy unambiguously the least-recently-used entry.
        File.SetLastWriteTimeUtc(oldProxy, DateTime.UtcNow.AddHours(-2));

        var newProxy = await cache.GetOrCreateAsync(
            newSource, 720, (target, _) => WriteProxyAsync(target, 720, bytes: 3_072));

        Assert.True(File.Exists(newProxy), "the proxy that was just created is protected from its own eviction");
        Assert.False(File.Exists(oldProxy), "the oldest proxy is pruned to get back under budget");
        Assert.True(cache.CurrentSizeBytes() <= 4_096);
    }

    [Fact]
    public async Task Eviction_SkipsAProxyThatIsOpenForReading()
    {
        // The real case this protects: ffmpeg has the proxy open for a live preview pipe when some
        // other file pushes the cache over budget. Windows refuses the delete, DiskCache catches it
        // and moves on - the pipe must not have the file pulled out from under it.
        // WP-179: the handout grace is off so this still tests the OS-lock fallback specifically -
        // with the grace on, the pin would protect the proxy before the delete was ever attempted.
        var cache = new ProxyCache(_paths, maxTotalBytes: 4_096, handoutGrace: TimeSpan.Zero);
        var openSource = WriteSourceFile("open.mp4");
        var otherSource = WriteSourceFile("other.mp4");

        var openProxy = await cache.GetOrCreateAsync(
            openSource, 720, (target, _) => WriteProxyAsync(target, 720, bytes: 3_072));
        File.SetLastWriteTimeUtc(openProxy, DateTime.UtcNow.AddHours(-2));

        using (var held = new FileStream(openProxy, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var otherProxy = await cache.GetOrCreateAsync(
                otherSource, 720, (target, _) => WriteProxyAsync(target, 720, bytes: 3_072));

            Assert.True(File.Exists(otherProxy));
            if (OperatingSystem.IsWindows())
            {
                // FileShare.Read denies delete, so eviction has to skip it rather than fail.
                Assert.True(File.Exists(openProxy), "an open proxy must survive an eviction pass");
                Assert.True(held.CanRead);
            }
        }
    }

    // ---- export uses the original ----

    [Fact]
    public void ExportFrameSource_HasNoWayToBeGivenAProxySelector()
    {
        // The export-uses-original guarantee is structural, so this asserts the structure: nothing
        // in the export decode path can be handed a proxy selector, and the preview one can.
        var exportParameters = typeof(ExportFrameSource)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();
        Assert.DoesNotContain(typeof(ProxySourceSelector), exportParameters);
        Assert.DoesNotContain(typeof(ProxyCache), exportParameters);

        var exporterParameters = typeof(TimelineExporter)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();
        Assert.DoesNotContain(typeof(ProxySourceSelector), exporterParameters);
        Assert.DoesNotContain(typeof(ProxyCache), exporterParameters);

        Assert.Contains(
            typeof(SequentialFrameSource).GetConstructors().SelectMany(c => c.GetParameters()),
            p => p.ParameterType == typeof(ProxySourceSelector));
    }

    [Fact]
    public async Task ExportRender_DecodesTheOriginalPath_EvenWithAReadyProxyOnDisk()
    {
        var source = WriteSourceFile("export-source.mp4");

        // A proxy exists and is warm - the exact condition under which a leak would be invisible.
        var cache = new ProxyCache(_paths);
        var proxy = await cache.GetOrCreateAsync(source, 720, (target, _) => WriteProxyAsync(target, 720));
        Assert.True(File.Exists(proxy));

        var recorder = new RecordingFrameSource();
        var manifest = new MediaManifest
        {
            Entries =
            [
                new MediaManifestEntry
                {
                    Id = "asset-1",
                    Name = "export-source.mp4",
                    Type = ClipType.video,
                    Source = MediaSource.External(source),
                },
            ],
        };
        var resolver = new MediaResolver(() => manifest, () => null);
        using var renderer = new TimelineFrameRenderer(
            recorder, resolver, compositor: new CpuCompositor());
        var timeline = new Timeline
        {
            Id = "T-EXPORT",
            Fps = 30,
            Width = 1920,
            Height = 1080,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            Id = "clip",
                            MediaRef = "asset-1",
                            MediaType = ClipType.video,
                            SourceClipType = ClipType.video,
                            StartFrame = 0,
                            DurationFrames = 10,
                        },
                    ],
                },
            ],
        };

        using var rendered = await renderer.RenderAsync(timeline, 3, 1920, 1080);

        var decoded = Assert.Single(recorder.Paths);
        Assert.Equal(source, decoded);
        Assert.DoesNotContain(recorder.Paths, p => p.StartsWith(_paths.ProxiesDirectory, StringComparison.OrdinalIgnoreCase));
    }

    // ---- end to end, when ffmpeg is on the machine ----

    [Fact]
    public async Task RealTranscode_ProducesAProxyAtThePreviewRaster()
    {
        if (!IsFfmpegAvailable()) return;

        string? video = null;
        try
        {
            video = await GenerateTestVideoAsync(2560, 1440);
            using var selector = new ProxySourceSelector(new ProxyCache(_paths), PreviewRaster.PreviewMaxDimension);

            Assert.Equal(video, selector.SelectDecodePath(video, Probe(2560, 1440)));
            await WaitForStateAsync(selector, video, ProxyState.Ready);

            var proxy = selector.SelectDecodePath(video, Probe(2560, 1440));
            Assert.NotEqual(video, proxy);

            var info = await new FfmpegMediaProbe().ProbeAsync(proxy);
            Assert.True(info.HasVideo);
            // Exactly the raster preview decodes at, so the decode needs no scale filter at all.
            Assert.Equal(PreviewRaster.Fit(2560, 1440, PreviewRaster.PreviewMaxDimension), (info.Width, info.Height));
            Assert.Equal(
                $"format=bgra",
                FfmpegVideoFilter.Convert(info.Width!.Value, info.Height!.Value, info.Width!.Value, info.Height!.Value, "bgra"));
            Assert.True(new FileInfo(proxy).Length < new FileInfo(video).Length);
        }
        finally
        {
            TryDeleteFile(video);
        }
    }

    [Fact]
    public async Task Probe_ReportsTheVideoCodecName()
    {
        if (!IsFfmpegAvailable()) return;

        string? video = null;
        try
        {
            video = await GenerateTestVideoAsync(320, 240);
            var info = await new FfmpegMediaProbe().ProbeAsync(video);
            Assert.Equal("h264", info.VideoCodecName);
        }
        finally
        {
            TryDeleteFile(video);
        }
    }

    // ---- helpers ----

    private ProxySourceSelector NewSelector(Func<string, string, int, CancellationToken, Task> transcoder)
        => new(new ProxyCache(_paths), PreviewRaster.PreviewMaxDimension, transcoder);

    private static MediaProbeResult Probe(int? width, int? height, string? codec = "h264")
        => new(DurationSeconds: 10, Width: width, Height: height, Fps: 30, HasAudio: false, HasVideo: true, VideoCodecName: codec);

    private string WriteSourceFile(string name = "source.mp4", string content = "fake media bytes")
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static Task WriteProxyAsync(string targetPath, int height, int bytes = 64)
        => File.WriteAllBytesAsync(targetPath, new byte[Math.Max(1, bytes)]);

    /// <summary>
    /// Waits on the selector's own StateChanged event rather than polling. Generation runs on the
    /// thread pool, and when the ffmpeg-backed tests in this suite run alongside these, a polling
    /// wait loses its own timeslice and times out on a selector that was working fine - a flake in
    /// the test, not the product. Subscribing first and then re-reading the state closes the race
    /// where the transition lands between the two.
    /// </summary>
    private static async Task WaitForStateAsync(
        ProxySourceSelector selector, string mediaPath, ProxyState state, int timeoutSeconds = 120)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnChanged(ProxyStatus status)
        {
            if (string.Equals(status.MediaPath, mediaPath, StringComparison.OrdinalIgnoreCase)
                && status.State == state)
                reached.TrySetResult();
        }

        selector.StateChanged += OnChanged;
        try
        {
            if (selector.StatusFor(mediaPath).State == state) return;
            var finished = await Task.WhenAny(reached.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
            if (finished != reached.Task && selector.StatusFor(mediaPath).State != state)
            {
                Assert.Fail(
                    $"Proxy state for '{mediaPath}' never reached {state} within {timeoutSeconds}s; " +
                    $"last was {selector.StatusFor(mediaPath).State} ({selector.StatusFor(mediaPath).Detail}).");
            }
        }
        finally
        {
            selector.StateChanged -= OnChanged;
        }
    }

    private sealed class RecordingFrameSource : IFrameSource
    {
        public List<string> Paths { get; } = [];

        public Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
        {
            lock (Paths) Paths.Add(path);
            var data = new byte[16 * 16 * 4];
            for (var i = 3; i < data.Length; i += 4) data[i] = 255;
            return Task.FromResult(new VideoFrame(16, 16, data, "rgba"));
        }
    }

    /// <summary>
    /// Same shape as <see cref="MediaCacheTests"/>: a resolved path is not proof until the
    /// file exists or the process starts. On Windows the resolver returns a file path and
    /// does not search PATH (WP-264).
    /// </summary>
    private static bool IsFfmpegAvailable()
    {
        try
        {
            if (File.Exists(FFmpegResolver.GetFFmpegPath()) && File.Exists(FFmpegResolver.GetFFprobePath()))
                return true;

            using var process = new Process();
            process.StartInfo.FileName = FFmpegResolver.GetFFmpegPath();
            process.StartInfo.Arguments = "-version";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            process.WaitForExit();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<string> GenerateTestVideoAsync(int width, int height)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"temno_proxy_test_{Guid.NewGuid():N}.mp4");
        using var process = new Process();
        process.StartInfo.FileName = FFmpegResolver.GetFFmpegPath();
        process.StartInfo.Arguments =
            $"-y -f lavfi -i testsrc=duration=2:size={width}x{height}:rate=30 " +
            $"-c:v libx264 -pix_fmt yuv420p \"{tempPath}\"";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        if (!process.Start())
            throw new InvalidOperationException("Failed to start ffmpeg to generate the test video.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed to generate the test video (exit {process.ExitCode}).");
        return tempPath;
    }

    private static void TryDeleteFile(string? path)
    {
        if (path is null || !File.Exists(path)) return;
        try { File.Delete(path); } catch { /* cleanup only */ }
    }
}
