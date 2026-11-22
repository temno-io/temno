using System.Text.Json;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Preview;
using Temno.Media;
using Temno.Media.Cache;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class PreviewResolutionTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _prefsFile;

    public PreviewResolutionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "temno-preview-res-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _prefsFile = Path.Combine(_tempRoot, "user-preferences.json");
        UserPreferences.SetPathForTests(_prefsFile);
    }

    public void Dispose()
    {
        UserPreferences.SetPathForTests(null);
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* ignore */ }
    }

    [Theory]
    [InlineData(null, "720p", 1280, false)]
    [InlineData("", "720p", 1280, false)]
    [InlineData("invalid", "720p", 1280, false)]
    [InlineData("full", "full", null, false)]
    [InlineData("FULL", "full", null, false)]
    [InlineData("canvas", "full", null, false)]
    [InlineData("native", "full", null, false)]
    [InlineData("2k", "2k", 2560, false)]
    [InlineData("1440p", "2k", 2560, false)]
    [InlineData("1080p", "1080p", 1920, false)]
    [InlineData("720p", "720p", 1280, false)]
    [InlineData("540p", "540p", 960, false)]
    [InlineData("360p", "360p", 640, false)]
    [InlineData("1920", "1080p", 1920, false)]
    [InlineData("1280", "720p", 1280, false)]
    [InlineData("1600", "1600", 1600, true)]
    [InlineData("1601", "1600", 1600, true)]
    [InlineData("480", "480", 480, true)]
    [InlineData("80", "720p", 1280, false)]
    [InlineData("99999", "720p", 1280, false)]
    public void Parse_ResolvesPresetsCustomAndFallback(
        string? raw, string expectedTag, int? expectedMax, bool expectedCustom)
    {
        var parsed = PreviewResolution.Parse(raw);
        Assert.Equal(expectedTag, parsed.Tag);
        Assert.Equal(expectedMax, parsed.MaxDimension);
        Assert.Equal(expectedCustom, parsed.IsCustom);
    }

    [Theory]
    [InlineData(158, false)]
    [InlineData(160, true)]
    [InlineData(161, false)]
    [InlineData(1600, true)]
    [InlineData(7680, true)]
    [InlineData(7682, false)]
    public void TryValidateCustomMaxDimension_EnforcesRangeAndEvenness(int value, bool expectedValid)
    {
        var ok = PreviewResolution.TryValidateCustomMaxDimension(value, out var error);
        Assert.Equal(expectedValid, ok);
        Assert.Equal(expectedValid, error is null);
    }

    [Theory]
    [InlineData("full", 1920, 1080, 1920, 1080)]
    [InlineData("full", 3840, 2160, 3840, 2160)]
    [InlineData("full", 1080, 1920, 1080, 1920)]
    [InlineData("2k", 3840, 2160, 2560, 1440)]
    [InlineData("2k", 1920, 1080, 1920, 1080)]
    [InlineData("1080p", 3840, 2160, 1920, 1080)]
    [InlineData("1080p", 1920, 1080, 1920, 1080)]
    [InlineData("1080p", 1280, 720, 1280, 720)]
    [InlineData("720p", 1920, 1080, 1280, 720)]
    [InlineData("720p", 1080, 1920, 720, 1280)]
    [InlineData("540p", 1920, 1080, 960, 540)]
    [InlineData("360p", 1920, 1080, 640, 360)]
    [InlineData("1600", 1920, 1080, 1600, 900)]
    [InlineData("720p", 1, 1, 1, 1)]
    public void FitDimensions_NeverUpscalesAndPreservesAspect(
        string tag, int width, int height, int expectedW, int expectedH)
    {
        var res = PreviewResolution.Parse(tag);
        Assert.Equal((expectedW, expectedH), res.FitDimensions(width, height));
    }

    [Fact]
    public void ResolveDecodeMaxDimension_TracksTimelineInFullModeAndNativeInSourceMonitor()
    {
        Assert.Equal(1920, PreviewResolution.Full.ResolveDecodeMaxDimension(1920, 1080));
        Assert.Equal(3840, PreviewResolution.Full.ResolveDecodeMaxDimension(3840, 2160));
        Assert.Equal(PreviewResolution.MaxCustomDimension, PreviewResolution.Full.ResolveDecodeMaxDimension(null, null));
        Assert.Equal(1280, PreviewResolution.R720p.ResolveDecodeMaxDimension(3840, 2160));
        Assert.Equal(960, PreviewResolution.R540p.ResolveDecodeMaxDimension(1920, 1080));
    }

    [Fact]
    public void Labels_ReflectActiveCanvasAndCompactMode()
    {
        Assert.Equal("Full · 1920×1080", PreviewResolution.Full.MenuLabel(1920, 1080));
        Assert.Equal("2K · up to 2560 px", PreviewResolution.R2K.MenuLabel(1920, 1080));
        Assert.Equal("2K · 2560×1440", PreviewResolution.R2K.MenuLabel(3840, 2160));
        Assert.Equal("1080p · 1920×1080", PreviewResolution.R1080p.MenuLabel(1920, 1080));
        Assert.Equal("720p · 1280×720", PreviewResolution.R720p.MenuLabel(1920, 1080));

        var custom = PreviewResolution.FromCustomMaxDimension(1600);
        Assert.Equal("Custom (1600×900)…", custom.CustomMenuLabel(1920, 1080));
        Assert.Equal("Custom…", PreviewResolution.R720p.CustomMenuLabel(1920, 1080));

        Assert.Equal("Frame preview · 1920×1080", PreviewResolution.Full.ButtonLabel(1920, 1080, 1920, 1080, compact: false));
        Assert.Equal("1920×1080", PreviewResolution.Full.ButtonLabel(1920, 1080, 1920, 1080, compact: true));
        Assert.Equal("Frame preview · 960×540", PreviewResolution.R540p.ButtonLabel(null, null, 1920, 1080, compact: false));
        Assert.Equal("Frame preview · Full", PreviewResolution.Full.ButtonLabel(null, null, null, null, compact: false));
    }

    [Fact]
    public void UserPreferences_PreviewResolution_RoundTripsAndSurvivesSettingsSave()
    {
        Assert.Equal(PreviewResolution.R720p, UserPreferences.Load().ResolvePreviewResolution());

        var prefs = UserPreferences.Load();
        prefs.Preview.Resolution = PreviewResolution.Full.Tag;
        UserPreferences.Save(prefs);
        Assert.Equal(PreviewResolution.Full, UserPreferences.Load().ResolvePreviewResolution());

        // Settings dialog save must not clobber preview resolution.
        var draft = SettingsPrefsMapper.FromPreferences(UserPreferences.Load());
        draft.PreferHardwareEncode = false;
        SettingsPrefsMapper.Save(draft);
        Assert.Equal(PreviewResolution.Full, UserPreferences.Load().ResolvePreviewResolution());

        // Null section and unknown fields inside preview follow WP-402 D-1.
        File.WriteAllText(_prefsFile, """{ "preview": { "resolution": "1600", "futureOption": true } }""");
        var loaded = UserPreferences.Load();
        Assert.Equal(PreviewResolution.FromCustomMaxDimension(1600), loaded.ResolvePreviewResolution());
        UserPreferences.Save(loaded);
        using var doc = JsonDocument.Parse(File.ReadAllText(_prefsFile));
        Assert.True(doc.RootElement.GetProperty("preview").GetProperty("futureOption").GetBoolean());

        File.WriteAllText(_prefsFile, """{ "preview": null }""");
        Assert.Equal(PreviewResolution.R720p, UserPreferences.Load().ResolvePreviewResolution());
    }

    [Fact]
    public async Task PreviewFrameProvider_RespectsDynamicMaxDimensionAndDoesNotCoalesceAcrossSizes()
    {
        var timeline = new Timeline
        {
            Id = "tl-1",
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
                            Id = "c1",
                            MediaRef = "a1",
                            MediaType = ClipType.video,
                            StartFrame = 0,
                            DurationFrames = 60,
                        },
                    ],
                },
            ],
        };
        var manifest = new MediaManifest
        {
            Entries = [new MediaManifestEntry { Id = "a1", Source = MediaSource.External("/media/a.mp4") }],
        };
        var resolver = new MediaResolver(() => manifest, () => _tempRoot);

        var gate = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var source = new DelegatingFrameSource((_, _) =>
        {
            var n = Interlocked.Increment(ref calls);
            if (n == 1) return gate.Task;
            return Task.FromResult(new VideoFrame(16, 9, new byte[16 * 9 * 4], "rgba"));
        });

        using var provider = new PreviewFrameProvider(source, resolver, timelineLookup: null, maxDimension: 1280);
        Assert.Equal((1280, 720), provider.PreviewSize(timeline));

        // Start a 1280x720 render that pauses on gate, then raise MaxDimension to Full (null)
        // and ask for the same frame: it must NOT coalesce onto the 1280x720 flight.
        var firstTask = provider.GetCompositedFrameAsync(timeline, 0);
        provider.MaxDimension = null;
        Assert.Equal((1920, 1080), provider.PreviewSize(timeline));

        using var fullFrame = await provider.GetCompositedFrameAsync(timeline, 0);
        Assert.NotNull(fullFrame);
        Assert.Equal(1920, fullFrame.Width);
        Assert.Equal(1080, fullFrame.Height);
        Assert.Equal(2, calls);

        gate.SetResult(new VideoFrame(16, 9, new byte[16 * 9 * 4], "rgba"));
        using var firstFrame = await firstTask;
        Assert.NotNull(firstFrame);
        Assert.Equal(1280, firstFrame.Width);
        Assert.Equal(720, firstFrame.Height);

        provider.MaxDimension = 960;
        using var halfFrame = await provider.GetCompositedFrameAsync(timeline, 0);
        Assert.NotNull(halfFrame);
        Assert.Equal(960, halfFrame.Width);
        Assert.Equal(540, halfFrame.Height);
    }

    [Fact]
    public async Task ProxySourceSelector_UpdatingPreviewMaxDimension_ReevaluatesPolicyAndTargetHeight()
    {
        var paths = new MediaCachePaths(Path.Combine(_tempRoot, "cache"));
        var clip1080 = Path.Combine(_tempRoot, "clip1080.mp4");
        var clip4k = Path.Combine(_tempRoot, "clip4k.mp4");
        File.WriteAllBytes(clip1080, [1, 2, 3, 4]);
        File.WriteAllBytes(clip4k, [5, 6, 7, 8]);

        var heightsRequested = new List<int>();
        var firstReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var selector = new ProxySourceSelector(
            new ProxyCache(paths),
            previewMaxDimension: 1280,
            transcoder: async (_, target, targetHeight, _) =>
            {
                lock (heightsRequested) heightsRequested.Add(targetHeight);
                await File.WriteAllBytesAsync(target, [9, 9, 9]);
                if (targetHeight == 720) firstReady.TrySetResult();
            });

        // At 1280 max edge, 1080p H.264 warrants a 720p proxy.
        var probe1080 = new MediaProbeResult(10, 1920, 1080, 30, HasAudio: false, HasVideo: true, VideoCodecName: "h264");
        selector.SelectDecodePath(clip1080, probe1080);
        await firstReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => selector.StatusFor(clip1080).State == ProxyState.Ready);
        Assert.NotEqual(clip1080, selector.SelectDecodePath(clip1080, probe1080));

        // Raising preview max to 1920 clears stale Ready status and decodes 1080p H.264 directly.
        selector.PreviewMaxDimension = 1920;
        Assert.Empty(selector.Statuses);
        Assert.Equal(clip1080, selector.SelectDecodePath(clip1080, probe1080));
        Assert.Equal(ProxyState.NotWarranted, selector.StatusFor(clip1080).State);

        // At 1920 max edge, a 4K H.264 clip requests a 1080p proxy instead of 720p.
        var probe4k = new MediaProbeResult(10, 3840, 2160, 30, HasAudio: false, HasVideo: true, VideoCodecName: "h264");
        selector.SelectDecodePath(clip4k, probe4k);
        await WaitUntilAsync(() => selector.StatusFor(clip4k).State == ProxyState.Ready);
        Assert.Equal(1080, selector.StatusFor(clip4k).TargetHeight);
        Assert.Contains(1080, heightsRequested);
    }

    [Fact]
    public void FrameBufferPool_ClearAndMaxRetainedBytes_TrimIdleBuffersWithoutBreakingActiveHandles()
    {
        var pool = new FrameBufferPool();
        var frameA = pool.Rent(10, 10, "bgra"); // 400 bytes
        var frameB = pool.Rent(10, 10, "bgra");
        var active = pool.Rent(10, 10, "bgra");
        frameA.Dispose();
        frameB.Dispose();
        Assert.Equal(2, pool.RetainedCount);

        pool.MaxRetainedBytes = 400;
        Assert.Equal(1, pool.RetainedCount);

        pool.Clear();
        Assert.Equal(0, pool.RetainedCount);

        // Active handle stays valid and returns cleanly when disposed.
        Assert.Equal(400, active.Data.Length);
        active.Dispose();
        Assert.Equal(1, pool.RetainedCount);
    }

    [Fact]
    public void SequentialFrameSource_MaxDimension_UpdatesPoolBudgetAndRejectsInvalidValues()
    {
        using var source = new SequentialFrameSource(maxDimension: 1280);
        Assert.Equal(1280, source.MaxDimension);
        Assert.Equal(FrameBufferPool.DefaultMaxRetainedBytes, source.FramePool.MaxRetainedBytes);

        source.FramePool.Rent(16, 9, "bgra").Dispose();
        Assert.Equal(1, source.FramePool.RetainedCount);

        source.MaxDimension = 3840;
        Assert.Equal(3840, source.MaxDimension);
        Assert.True(source.FramePool.MaxRetainedBytes > FrameBufferPool.DefaultMaxRetainedBytes);
        Assert.Equal(0, source.FramePool.RetainedCount);

        source.MaxDimension = 1920;
        Assert.Equal(FrameBufferPool.DefaultMaxRetainedBytes, source.FramePool.MaxRetainedBytes);

        Assert.Throws<ArgumentOutOfRangeException>(() => source.MaxDimension = 0);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate() && DateTime.UtcNow < deadline)
            await Task.Delay(15);
        Assert.True(predicate());
    }

    private sealed class DelegatingFrameSource(Func<string, double, Task<VideoFrame>> handler) : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
            => handler(path, sourceSeconds);
    }
}
