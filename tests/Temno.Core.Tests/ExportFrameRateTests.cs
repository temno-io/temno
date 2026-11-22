using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Export;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// Frame-rate conversion in the export decode pipe.
/// <para>
/// Regression cover for the defect where <see cref="ExportFrameSource"/>'s skip loop never advanced
/// its cursor: the predicate was loop-invariant, so a source faster than the timeline drained the
/// whole file into <c>/dev/null</c> and every later frame answered from the stale retained frame.
/// A 60 fps clip on a 30 fps timeline exported as a still image. The equivalent preview class,
/// <see cref="SequentialFrameSource"/>, advances its cursor and was always correct — which is why
/// preview looked right and only the export was wrong.
/// </para>
/// </summary>
public sealed class ExportFrameRateTests
{
    private const int TimelineFps = 30;
    private const int TimelineFrames = 90;   // 3.0 s

    [Theory]
    // 60 fps source: every second source frame is used — 90 distinct pictures, none repeated.
    [InlineData(60, 90, 1)]
    // 30 fps source: 1:1.
    [InlineData(30, 90, 1)]
    // 24 fps source: only 72 pictures exist in 3.0 s, so 18 frames must repeat (max run of 2).
    [InlineData(24, 72, 2)]
    public async Task GetFrameAsync_ConvertsSourceRateToTimelineRate(
        int sourceFps, int expectedDistinct, int maxAllowedRun)
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var media = Path.Combine(directory, $"src{sourceFps}.mp4");
            await FfmpegFixture.WriteTestClipAsync(media, sourceFps, seconds: 6, width: 160, height: 90);

            using var source = new ExportFrameSource();
            var hashes = new List<string>(TimelineFrames);
            for (var n = 0; n < TimelineFrames; n++)
            {
                using var frame = await source.GetFrameAsync(media, n / (double)TimelineFps);
                hashes.Add(Convert.ToHexString(MD5.HashData(frame.Data)));
            }

            var distinct = hashes.Distinct().Count();
            Assert.InRange(distinct, expectedDistinct - 2, expectedDistinct + 2);

            // A freeze shows up as a long run of byte-identical frames.
            Assert.InRange(LongestIdenticalRun(hashes), 1, maxAllowedRun);

            // The pipe must be started once, not respawned per frame.
            Assert.Equal(1, source.RestartCount);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    /// <summary>Time must move forward: frame N never shows a picture from before frame N-1.</summary>
    [Fact]
    public async Task GetFrameAsync_HighRateSource_DoesNotStall()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var media = Path.Combine(directory, "fast.mp4");
            await FfmpegFixture.WriteTestClipAsync(media, fps: 60, seconds: 6, width: 160, height: 90);

            using var source = new ExportFrameSource();
            var stopwatch = Stopwatch.StartNew();
            var hashes = new List<string>();
            for (var n = 0; n < TimelineFrames; n++)
            {
                using var frame = await source.GetFrameAsync(media, n / (double)TimelineFps);
                hashes.Add(Convert.ToHexString(MD5.HashData(frame.Data)));
            }
            stopwatch.Stop();

            // Before the fix this drained the entire source per stall; distinct dropped to ~2.
            Assert.True(hashes.Distinct().Count() > TimelineFrames * 0.9,
                $"Expected forward progress, got {hashes.Distinct().Count()} distinct of {TimelineFrames}.");
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    /// <summary>A clip that runs past the end of its media holds the last frame instead of failing.</summary>
    [Fact]
    public async Task GetFrameAsync_PastEndOfMedia_HoldsFinalFrame()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var media = Path.Combine(directory, "short.mp4");
            await FfmpegFixture.WriteTestClipAsync(media, fps: 30, seconds: 1, width: 160, height: 90);

            using var source = new ExportFrameSource();
            string? last = null;
            for (var n = 0; n < 60; n++)   // 2.0 s of a 1.0 s clip
            {
                using var frame = await source.GetFrameAsync(media, n / 30.0);
                last = Convert.ToHexString(MD5.HashData(frame.Data));
            }

            Assert.NotNull(last);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    // WP-274. Long enough for the 0.1% drift of a 1001-rate source to pass a whole frame: 60 s put a
    // 23.976 source one frame behind, 59.94 four behind (docs/verification/wp-274/finding/).
    private const int LongClipSeconds = 60;

    // WP-274. Timeline instants of a minute-long clip with one second cut out at 30 s, the kind of
    // gap remove_silence leaves. A jump that short reads through the pipe instead of restarting it,
    // so it never reset the drift.
    private static IEnumerable<int> LongWalk(int timelineFps)
        => Enumerable.Range(0, LongClipSeconds * timelineFps + 1)
            .Where(m => m <= 30 * timelineFps || m >= 31 * timelineFps);

    /// <summary>
    /// WP-274. The pipe decoded 1001-rate sources at the rounded rate but stepped its cursor by the
    /// exact one, so the picture fell behind the exactly-timed audio by about 1 ms per second. Every
    /// timeline frame of a minute, across a one-second cut, must return the source frame nearest its
    /// instant from the one pipe; the integer row is the control.
    /// </summary>
    [Theory]
    [InlineData("24000/1001", 24)]
    [InlineData("30000/1001", 30)]
    [InlineData("60000/1001", 60)]
    [InlineData("60000/1001", 30)]
    [InlineData("24000/1001", 30)]
    [InlineData("60", 60)]
    public async Task GetFrameAsync_LongConstantRateSource_ReturnsTheNearestFrameToTheEnd(string rate, int timelineFps)
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var media = Path.Combine(directory, "indexed.mp4");
            await FfmpegFixture.WriteIndexedClipAsync(media, rate, LongClipSeconds + 2);
            var parts = rate.Split('/');
            long numerator = long.Parse(parts[0], CultureInfo.InvariantCulture);
            long denominator = parts.Length == 2 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : 1;

            using var source = new ExportFrameSource();
            var misses = new List<string>();
            var walk = LongWalk(timelineFps).ToArray();
            int got = -1, expected = -1;
            foreach (var m in walk)
            {
                using var frame = await source.GetFrameAsync(media, m / (double)timelineFps);
                got = FfmpegFixture.ReadFrameIndex(frame);
                // The source frame nearest m / timelineFps, in exact integer arithmetic. No instant is
                // a tie: that would need timelineFps to be a multiple of 2000.
                expected = (int)((2 * m * numerator + denominator * timelineFps) / (2 * denominator * timelineFps));
                if (got != expected) misses.Add($"t={m / (double)timelineFps:0.000}s got {got} expected {expected}");
            }

            Assert.True(misses.Count == 0,
                $"{misses.Count} of {walk.Length} frames missed the nearest source frame; first {misses.FirstOrDefault()}, last {misses.LastOrDefault()}");
            Assert.Equal(expected, got);
            Assert.Equal(1, source.RestartCount);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    /// <summary>
    /// WP-274. A variable-rate phone clip averaging 29.7 fps took the same path, and stepping by that
    /// average against a 30 fps pipe drifted 1% (0.6 s after a minute). Each returned frame, across
    /// the same one-second cut, must be one that is on screen within half a pipe frame of the
    /// instant asked for.
    /// </summary>
    [Fact]
    public async Task GetFrameAsync_LongVariableRateSource_StaysOnTheFrameShownAtEachInstant()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var media = Path.Combine(directory, "vfr.mp4");
            await FfmpegFixture.WriteIndexedClipAsync(media, FfmpegFixture.VariableRate, LongClipSeconds + 2);
            var pts = await FfmpegFixture.ReadFramePtsAsync(media);
            Assert.Equal((30, 1), ExportFrameSource.DecodeRate(await ProbedFpsAsync(media)));

            const int TimelineFps = 30;
            const double HalfPipeFrame = 0.5 / 30;
            using var source = new ExportFrameSource();
            var misses = new List<string>();
            foreach (var m in LongWalk(TimelineFps))
            {
                var t = m / (double)TimelineFps;
                using var frame = await source.GetFrameAsync(media, t);
                var k = FfmpegFixture.ReadFrameIndex(frame);
                var shownFrom = pts[k];
                var shownUntil = k + 1 < pts.Length ? pts[k + 1] : double.MaxValue;
                if (shownFrom > t + HalfPipeFrame + 1e-6 || shownUntil < t - HalfPipeFrame - 1e-6)
                    misses.Add($"t={t:0.000}s got frame {k} shown {shownFrom:0.0000}-{shownUntil:0.0000}");
            }

            Assert.True(misses.Count == 0,
                $"{misses.Count} frames were off screen at their instant; first {misses.FirstOrDefault()}, last {misses.LastOrDefault()}");
            Assert.Equal(1, source.RestartCount);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    [Theory]
    [InlineData(24000.0 / 1001, 24000, 1001)]
    [InlineData(30000.0 / 1001, 30000, 1001)]
    [InlineData(60000.0 / 1001, 60000, 1001)]
    [InlineData(120000.0 / 1001, 120000, 1001)]
    [InlineData(29.97, 30000, 1001)]   // 2997/100 is within 1e-4 of 30000/1001
    [InlineData(24.0, 24, 1)]
    [InlineData(25.0, 25, 1)]
    [InlineData(30.0, 30, 1)]
    [InlineData(60.0, 60, 1)]
    [InlineData(29.7, 30, 1)]          // a variable-rate clip's average
    [InlineData(12.5, 12, 1)]
    [InlineData(0.3, 1, 1)]            // rounded to 0 before, which the decoder refuses
    public void DecodeRate_AsksForNtscRatesExactlyAndRoundsTheRest(double sourceFps, int numerator, int denominator)
        => Assert.Equal((numerator, denominator), ExportFrameSource.DecodeRate(sourceFps));

    /// <summary>WP-274. Preview passes no denominator, so its fps filter argument must not change.</summary>
    [Theory]
    [InlineData(60, 1, "60")]
    [InlineData(24, 1, "24")]
    [InlineData(60000, 1001, "60000/1001")]
    public void FormatOutputRate_IntegerRateFormatsAsItAlwaysHas(int numerator, int denominator, string expected)
        => Assert.Equal(expected, FfmpegPlaybackDecoder.FormatOutputRate(numerator, denominator));

    private static async Task<double> ProbedFpsAsync(string path)
        => (await new FfmpegMediaProbe().ProbeAsync(path)).Fps ?? 0;

    private static int LongestIdenticalRun(IReadOnlyList<string> hashes)
    {
        var longest = hashes.Count == 0 ? 0 : 1;
        var run = 1;
        for (var i = 1; i < hashes.Count; i++)
        {
            run = hashes[i] == hashes[i - 1] ? run + 1 : 1;
            if (run > longest) longest = run;
        }
        return longest;
    }
}

/// <summary>Shared ffmpeg helpers for export tests that need real media.</summary>
internal static class FfmpegFixture
{
    public static bool IsAvailable
    {
        get
        {
            try { return File.Exists(FFmpegResolver.GetFFmpegPath()) || RunsOnPath(); }
            catch { return false; }
        }
    }

    private static bool RunsOnPath()
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(FFmpegResolver.GetFFmpegPath(), "-version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (probe is null) return false;
            _ = probe.StandardOutput.ReadToEndAsync();
            _ = probe.StandardError.ReadToEndAsync();
            return probe.WaitForExit(10_000) && probe.ExitCode == 0;
        }
        catch { return false; }
    }

    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"temno_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public static void DeleteTempDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Writes a clip whose every frame differs, so duplicate frames are detectable.</summary>
    public static async Task WriteTestClipAsync(string path, int fps, int seconds, int width, int height)
    {
        var args = $"-hide_banner -loglevel error -y -f lavfi "
                 + $"-i testsrc2=size={width}x{height}:rate={fps}:duration={seconds} "
                 + $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p -g 12 \"{path}\"";
        await RunAsync(args);
    }

    /// <summary>Passed as the rate to <see cref="WriteIndexedClipAsync"/> for a variable-rate clip.</summary>
    public const string VariableRate = "vfr";

    /// <summary>
    /// WP-274. Writes a lossless 128x16 H.264 clip whose frames carry their own index as 16
    /// white/black bands, 8 px each, least significant bit on the left (<see cref="ReadFrameIndex"/>).
    /// <paramref name="rate"/> is an ffmpeg rate such as <c>60000/1001</c>, or
    /// <see cref="VariableRate"/> for a phone-style clip: about 29.7 fps on average, every interval
    /// different, on a 1/90000 time base.
    /// </summary>
    public static async Task WriteIndexedClipAsync(string path, string rate, int seconds)
    {
        const string Index = "format=gray,geq=lum='255*gt(bitand(N,pow(2,floor(X/8))),0)'";
        var variable = rate == VariableRate;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            variable
                ? $"nullsrc=s=128x16:r=30:d={seconds + 2},{Index},settb=1/90000,setpts='(N/29.7+0.004*sin(N*1.7))/TB'"
                : $"nullsrc=s=128x16:r={rate}:d={seconds},{Index}",
            "-c:v", "libx264", "-preset", "ultrafast", "-qp", "0", "-g", "30", "-bf", "2", "-pix_fmt", "yuv420p" };
        if (variable)
            args.AddRange(["-fps_mode", "passthrough", "-video_track_timescale", "90000", "-t", seconds.ToString(CultureInfo.InvariantCulture)]);
        args.Add(path);
        await RunAsync([.. args]);
    }

    /// <summary>The index <see cref="WriteIndexedClipAsync"/> burned into a decoded RGBA frame.</summary>
    public static int ReadFrameIndex(VideoFrame frame)
    {
        var index = 0;
        var row = frame.Height / 2 * frame.Width;
        for (var bit = 0; bit < 16; bit++)
        {
            if (frame.Data[(row + bit * 8 + 4) * 4] > 127)
                index |= 1 << bit;
        }
        return index;
    }

    /// <summary>Display-order presentation times, rebased so the first frame is 0 as the decoder sees it.</summary>
    public static async Task<double[]> ReadFramePtsAsync(string path)
    {
        var info = new ProcessStartInfo(FFmpegResolver.GetFFprobePath())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", path })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Failed to start ffprobe.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe failed: {await stderr}");
        var pts = (await stdout).Split('\n').Select(line => line.Trim().TrimEnd(','))
            .Where(line => line.Length > 0 && line != "N/A")
            .Select(line => double.Parse(line, CultureInfo.InvariantCulture)).ToArray();
        Array.Sort(pts);
        return pts.Select(p => p - pts[0]).ToArray();
    }

    public static async Task RunAsync(params string[] arguments)
    {
        var info = new ProcessStartInfo(FFmpegResolver.GetFFmpegPath())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        await stdout;
        var err = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg fixture failed: {err}");
    }

    public static async Task RunAsync(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(FFmpegResolver.GetFFmpegPath(), arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        await stdout;
        var err = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg fixture failed: {err}");
    }
}
