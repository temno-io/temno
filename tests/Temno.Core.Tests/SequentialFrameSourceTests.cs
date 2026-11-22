using System.Diagnostics;
using Temno.Media;
using Xunit;
using Xunit.Abstractions;

namespace Temno.Core.Tests;

public sealed class SequentialFrameSourceTests
{
    private readonly ITestOutputHelper _output;

    public SequentialFrameSourceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool IsFfmpegAvailable()
    {
        try
        {
            var ffmpegPath = FFmpegResolver.GetFFmpegPath();
            if (!File.Exists(ffmpegPath) && ffmpegPath.Contains(Path.DirectorySeparatorChar))
                return false;
            using var process = new Process();
            process.StartInfo.FileName = ffmpegPath;
            process.StartInfo.Arguments = "-version";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.Start();
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            return process.WaitForExit(10_000) && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Task<string> GenerateTestVideoAsync() => GenerateTestVideoAsync("testsrc");

    private static async Task<string> GenerateTestVideoAsync(string pattern)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"temno_seq_{Guid.NewGuid():N}.mp4");
        var ffmpegPath = FFmpegResolver.GetFFmpegPath();
        var arguments =
            $"-y -f lavfi -i {pattern}=duration=2:size=320x240:rate=30 -c:v libx264 -pix_fmt yuv420p \"{tempPath}\"";
        using var process = new Process();
        process.StartInfo.FileName = ffmpegPath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;
        process.Start();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg failed to generate sequential test video.");
        return tempPath;
    }

    [Fact]
    public async Task SequentialPlayback_ReadsForwardFasterThanSeekPerFrame()
    {
        if (!IsFfmpegAvailable())
            return;

        string? path = null;
        try
        {
            path = await GenerateTestVideoAsync();
            using var sequential = new SequentialFrameSource(maxDimension: 320);
            sequential.OutputFps = 30;

            var seqWatch = Stopwatch.StartNew();
            byte[]? first = null;
            byte[]? later = null;
            for (var i = 0; i < 20; i++)
            {
                using var frame = await sequential.GetFrameAsync(path, i / 30.0);
                Assert.Equal(320, frame.Width);
                Assert.Equal(240, frame.Height);
                Assert.Equal("bgra", frame.PixelFormat);
                if (i == 0) first = (byte[])frame.Data.Clone();
                if (i == 15) later = (byte[])frame.Data.Clone();
            }
            seqWatch.Stop();

            Assert.NotNull(first);
            Assert.NotNull(later);
            Assert.NotEqual(first, later);

            var seek = new FfmpegFrameSource();
            var seekWatch = Stopwatch.StartNew();
            for (var i = 0; i < 5; i++)
            {
                using var frame = await seek.GetFrameAsync(path, i / 30.0);
                Assert.Equal(320, frame.Width);
            }
            seekWatch.Stop();

            var seqPerFrame = seqWatch.Elapsed.TotalMilliseconds / 20;
            var seekPerFrame = seekWatch.Elapsed.TotalMilliseconds / 5;
            Assert.True(
                seqPerFrame < seekPerFrame,
                $"Sequential {seqPerFrame:0.0}ms/frame should beat seek {seekPerFrame:0.0}ms/frame.");
        }
        finally
        {
            if (path is not null && File.Exists(path))
                try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task PlaybackDecoder_EmitsExpectedFrameSize()
    {
        if (!IsFfmpegAvailable())
            return;

        string? path = null;
        try
        {
            path = await GenerateTestVideoAsync();
            using var decoder = await FfmpegPlaybackDecoder.StartAsync(path, 0, outputFps: 30, width: 160, height: 120);
            using var frame = await decoder.ReadFrameAsync();
            Assert.Equal(160, frame.Width);
            Assert.Equal(120, frame.Height);
            Assert.Equal(160 * 120 * 4, frame.Data.Length);
            Assert.Equal("bgra", frame.PixelFormat);
        }
        finally
        {
            if (path is not null && File.Exists(path))
                try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task DuplicateTimestamp_ReusesLastFrameWithoutRestart()
    {
        if (!IsFfmpegAvailable())
            return;

        string? path = null;
        try
        {
            path = await GenerateTestVideoAsync();
            using var sequential = new SequentialFrameSource(maxDimension: 320);
            sequential.OutputFps = 30;

            using var first = await sequential.GetFrameAsync(path, 0);
            var firstBytes = (byte[])first.Data.Clone();

            var duplicateWatch = Stopwatch.StartNew();
            using var duplicate = await sequential.GetFrameAsync(path, 0);
            duplicateWatch.Stop();

            Assert.Equal(firstBytes, duplicate.Data);
            Assert.Equal(1, sequential.RestartCount);
            var duplicateBudgetMs = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ? 250 : 80;
            Assert.True(
                duplicateWatch.ElapsedMilliseconds < duplicateBudgetMs,
                $"Duplicate timestamp took {duplicateWatch.ElapsedMilliseconds}ms (budget {duplicateBudgetMs}ms); a decoder restart would be hundreds.");

            using var next = await sequential.GetFrameAsync(path, 1 / 30.0);
            Assert.NotEqual(firstBytes, next.Data);
        }
        finally
        {
            if (path is not null && File.Exists(path))
                try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreferredHardware_PreservesSoftwareColorsAndFrameOrder(bool discardFirst)
    {
        if (!IsFfmpegAvailable()) return;
        var path = await GenerateTestVideoAsync();
        try
        {
            using var software = await FfmpegPlaybackDecoder.StartAsync(path, 0, 30, 160, 120);
            using var preferred = await FfmpegPlaybackDecoder.StartAsync(path, 0, 30, 160, 120,
                preferHardware: true);
            if (discardFirst)
            {
                await software.DiscardFrameAsync();
                await preferred.DiscardFrameAsync();
            }
            for (var i = 0; i < 3; i++)
            {
                using var expected = await software.ReadFrameAsync();
                using var actual = await preferred.ReadFrameAsync();
                Assert.Equal(expected.Data.Length, actual.Data.Length);
                var error = expected.Data.Zip(actual.Data, (a,b) => Math.Abs(a-b)).Average();
                Assert.True(error < 2, $"Frame {i}: hardware/software channel error {error}");
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preferred.ReadFrameAsync(cancelled.Token));
        }
        finally
        {
            if (File.Exists(path))
                try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    [Trait("Category", "RuntimeDiscriminator")]
    public async Task RuntimeMedia_Sequential1080pWithFrameCopy_SustainsThirtyFps()
    {
        var path = Environment.GetEnvironmentVariable("TEMNO_RUNTIME_MEDIA");
        if (string.IsNullOrWhiteSpace(path))
        {
            _output.WriteLine("TEMNO_RUNTIME_MEDIA is not set; exact-media runtime discriminator did not run.");
            return;
        }

        Assert.True(File.Exists(path), $"Runtime discriminator media does not exist: {path}");
        Assert.True(IsFfmpegAvailable(), "Runtime discriminator requires ffmpeg.");

        using var sequential = new SequentialFrameSource(maxDimension: 1920);
        sequential.OutputFps = 30;

        var startupWatch = Stopwatch.StartNew();
        using (var first = await sequential.GetFrameAsync(path, 0))
        {
            startupWatch.Stop();
            Assert.Equal(1920, first.Width);
            Assert.Equal(1080, first.Height);
        }

        const int measuredFrames = 90;
        var watch = Stopwatch.StartNew();
        for (var i = 1; i <= measuredFrames; i++)
        {
            using var frame = await sequential.GetFrameAsync(path, i / 30.0);
            var bitmapCopy = new byte[frame.Data.Length];
            frame.Data.AsSpan().CopyTo(bitmapCopy);
        }
        watch.Stop();

        var millisecondsPerFrame = watch.Elapsed.TotalMilliseconds / measuredFrames;
        _output.WriteLine(
            "startup={0:0.0}ms measured={1} total={2:0.0}ms average={3:0.00}ms/frame throughput={4:0.0}fps",
            startupWatch.Elapsed.TotalMilliseconds,
            measuredFrames,
            watch.Elapsed.TotalMilliseconds,
            millisecondsPerFrame,
            1000 / millisecondsPerFrame);

        Assert.True(
            millisecondsPerFrame < 1000.0 / 30,
            $"Sequential decode plus one 1080p frame copy averaged {millisecondsPerFrame:0.00}ms/frame; it cannot sustain 30fps.");
    }

    [Fact]
    public async Task AlternatingLayerPaths_KeepBothPipesWarmInsteadOfRestartingEveryFrame()
    {
        if (!IsFfmpegAvailable())
            return;

        var paths = new List<string>();
        try
        {
            paths.Add(await GenerateTestVideoAsync("testsrc"));
            paths.Add(await GenerateTestVideoAsync("smptebars"));

            using var sequential = new SequentialFrameSource(maxDimension: 320);
            sequential.OutputFps = 30;

            // A two-video-layer composite asks for both paths on every timeline frame.
            for (var i = 0; i < 15; i++)
            {
                foreach (var path in paths)
                {
                    using var frame = await sequential.GetFrameAsync(path, i / 30.0);
                    Assert.Equal(320, frame.Width);
                }
            }

            Assert.Equal(2, sequential.LivePipeCount);
            Assert.Equal(
                2,
                sequential.RestartCount);
        }
        finally
        {
            foreach (var path in paths)
                if (File.Exists(path)) try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task MorePathsThanPipeBudget_EvictsLeastRecentlyUsedDecoders()
    {
        if (!IsFfmpegAvailable())
            return;

        var paths = new List<string>();
        try
        {
            for (var i = 0; i < SequentialFrameSource.MaxLivePipes + 2; i++)
                paths.Add(await GenerateTestVideoAsync());

            using var sequential = new SequentialFrameSource(maxDimension: 320);
            sequential.OutputFps = 30;

            foreach (var path in paths)
            {
                using var frame = await sequential.GetFrameAsync(path, 0);
                Assert.Equal(320, frame.Width);
            }

            Assert.True(
                sequential.LivePipeCount <= SequentialFrameSource.MaxLivePipes,
                $"{sequential.LivePipeCount} live ffmpeg pipes exceeds the {SequentialFrameSource.MaxLivePipes} budget.");
        }
        finally
        {
            foreach (var path in paths)
                if (File.Exists(path)) try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Preroll_ThenDemandAtThatPosition_ServesWithoutARestart_AndTheSamePixels()
    {
        if (!IsFfmpegAvailable())
            return;

        string? path = null;
        try
        {
            path = await GenerateTestVideoAsync();
            using var cold = new SequentialFrameSource(maxDimension: 320) { OutputFps = 30 };
            using var expected = await cold.GetFrameAsync(path, 1.0);

            using var sequential = new SequentialFrameSource(maxDimension: 320) { OutputFps = 30 };
            await sequential.PrerollAsync(path, 1.0);
            Assert.Equal(1, sequential.PrerollCount);

            var watch = Stopwatch.StartNew();
            using var atCut = await sequential.GetFrameAsync(path, 1.0);
            watch.Stop();
            using var after = await sequential.GetFrameAsync(path, 1.0 + 1 / 30.0);

            Assert.Equal(0, sequential.RestartCount);
            Assert.Equal(expected.Data, atCut.Data);
            Assert.NotEqual(atCut.Data, after.Data);
            _output.WriteLine("demand read after preroll: {0:0.0} ms", watch.Elapsed.TotalMilliseconds);
        }
        finally
        {
            if (path is not null && File.Exists(path))
                try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Preroll_OfALaterPositionInTheSameFile_LeavesThePlayingPipeAlone()
    {
        if (!IsFfmpegAvailable())
            return;

        string? path = null;
        try
        {
            path = await GenerateTestVideoAsync();
            using var sequential = new SequentialFrameSource(maxDimension: 320) { OutputFps = 30 };

            // A jump cut within one source: 0.0-0.3 s plays, then the edit resumes at 1.2 s. The
            // playing pipe could read forward to it on demand; that is 27 decodes at the cut.
            for (var i = 0; i < 5; i++)
                (await sequential.GetFrameAsync(path, i / 30.0)).Dispose();
            await sequential.PrerollAsync(path, 1.2);
            for (var i = 5; i < 10; i++)
                (await sequential.GetFrameAsync(path, i / 30.0)).Dispose();
            (await sequential.GetFrameAsync(path, 1.2)).Dispose();
            (await sequential.GetFrameAsync(path, 1.2 + 1 / 30.0)).Dispose();

            Assert.Equal(1, sequential.RestartCount);   // the very first open only
            Assert.Equal(1, sequential.PrerollCount);
            Assert.Equal(2, sequential.LivePipeCount);
        }
        finally
        {
            if (path is not null && File.Exists(path))
                try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Preroll_WhenAPipeAlreadySitsAtThePosition_StartsNothing()
    {
        if (!IsFfmpegAvailable())
            return;

        string? path = null;
        try
        {
            path = await GenerateTestVideoAsync();
            using var sequential = new SequentialFrameSource(maxDimension: 320) { OutputFps = 30 };
            (await sequential.GetFrameAsync(path, 0)).Dispose();

            await sequential.PrerollAsync(path, 2 / 30.0);

            Assert.Equal(0, sequential.PrerollCount);
            Assert.Equal(1, sequential.LivePipeCount);
        }
        finally
        {
            if (path is not null && File.Exists(path))
                try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task DemandArrivingWhileAPrerollIsStarting_WaitsForItInsteadOfSpawningASecondDecoder()
    {
        if (!IsFfmpegAvailable())
            return;

        string? path = null;
        try
        {
            path = await GenerateTestVideoAsync();
            using var sequential = new SequentialFrameSource(maxDimension: 320) { OutputFps = 30 };

            // Not awaited: the preroll has registered its pipe and holds its gate by the time it
            // first yields (inside the probe), which is exactly the state a cut can arrive in.
            var preroll = sequential.PrerollAsync(path, 1.0);
            Assert.False(preroll.IsCompleted);
            using var frame = await sequential.GetFrameAsync(path, 1.0);
            await preroll;

            Assert.Equal(320, frame.Width);
            Assert.Equal(0, sequential.RestartCount);
            Assert.Equal(1, sequential.PrerollCount);
            Assert.Equal(1, sequential.LivePipeCount);
        }
        finally
        {
            if (path is not null && File.Exists(path))
                try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task Preroll_NeverDisplacesAPipeThatIsPlaying()
    {
        if (!IsFfmpegAvailable())
            return;

        var paths = new List<string>();
        try
        {
            for (var i = 0; i < SequentialFrameSource.MaxLivePipes + 1; i++)
                paths.Add(await GenerateTestVideoAsync());

            using var sequential = new SequentialFrameSource(maxDimension: 320) { OutputFps = 30 };
            // Every pipe in the budget was read just now: all of them are layers playing.
            for (var i = 0; i < SequentialFrameSource.MaxLivePipes; i++)
                (await sequential.GetFrameAsync(paths[i], 0)).Dispose();
            // The opens are cold starts; read each once more so all are inside the hot window.
            for (var i = 0; i < SequentialFrameSource.MaxLivePipes; i++)
                (await sequential.GetFrameAsync(paths[i], 1 / 30.0)).Dispose();

            await sequential.PrerollAsync(paths[^1], 0.5);

            Assert.Equal(0, sequential.PrerollCount);
            Assert.Equal(SequentialFrameSource.MaxLivePipes, sequential.LivePipeCount);
            for (var i = 0; i < SequentialFrameSource.MaxLivePipes; i++)
                (await sequential.GetFrameAsync(paths[i], 2 / 30.0)).Dispose();
            Assert.Equal(SequentialFrameSource.MaxLivePipes, sequential.RestartCount);
        }
        finally
        {
            foreach (var path in paths)
                if (File.Exists(path)) try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task HeldFrames_KeepTheirPixelsWhileThePipeDecodesOn_AndReleasedBuffersAreReused()
    {
        if (!IsFfmpegAvailable())
            return;

        string? path = null;
        try
        {
            path = await GenerateTestVideoAsync();
            using var sequential = new SequentialFrameSource(maxDimension: 320);
            sequential.OutputFps = 30;

            using (var first = await sequential.GetFrameAsync(path, 0))
                Assert.True(first.IsPooled);   // the frame prefetched at start is pooled too
            using var held = await sequential.GetFrameAsync(path, 1 / 30.0);
            Assert.True(held.IsPooled);
            var heldBytes = (byte[])held.Data.Clone();

            // WP-248: a duplicate timestamp is answered from the pipe's last frame as a second
            // read-only handle on the same pixels, not a 3.7 MB copy per repeat at preview size.
            using (var duplicate = await sequential.GetFrameAsync(path, 1 / 30.0))
                Assert.Equal(heldBytes, duplicate.Data);

            // The pipe moves on and recycles buffers; none of them may be the one still held.
            for (var i = 2; i < 12; i++)
            {
                using var next = await sequential.GetFrameAsync(path, i / 30.0);
                Assert.NotSame(held.Data, next.Data);
            }

            Assert.Equal(heldBytes, held.Data);
            Assert.Equal(1, sequential.RestartCount);

            // Released frames come back: forty more frames, disposed as read, allocate a handful.
            var before = sequential.FramePool.AllocatedCount;
            for (var i = 12; i < 52; i++)
                (await sequential.GetFrameAsync(path, i / 30.0)).Dispose();
            Assert.InRange(sequential.FramePool.AllocatedCount - before, 0, 2);
        }
        finally
        {
            if (path is not null && File.Exists(path))
                try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
