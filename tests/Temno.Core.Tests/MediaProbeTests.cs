using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Temno.Media;
using Xunit;

namespace Temno.Core.Tests;

public class MediaProbeTests
{
    private bool IsFfmpegAvailable()
    {
        try
        {
            var ffmpegPath = FFmpegResolver.GetFFmpegPath();
            var ffprobePath = FFmpegResolver.GetFFprobePath();

            // Check if files exist or are resolvable
            if (File.Exists(ffmpegPath) && File.Exists(ffprobePath))
            {
                return true;
            }

            // Fallback: see if we can start them
            using var process = new Process();
            process.StartInfo.FileName = ffmpegPath;
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

    private async Task<string> GenerateTestVideoAsync()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"temno_test_{Guid.NewGuid():N}.mp4");
        var ffmpegPath = FFmpegResolver.GetFFmpegPath();

        // Generate 1 second of 1280x720 30fps video pattern
        var arguments = $"-y -f lavfi -i testsrc=duration=1:size=1280x720:rate=30 -c:v libx264 -pix_fmt yuv420p \"{tempPath}\"";

        using var process = new Process();
        process.StartInfo.FileName = ffmpegPath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg to generate test video.");
        }

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed to generate test video. Exit code: {process.ExitCode}");
        }

        return tempPath;
    }

    [Fact]
    public async Task Test_MediaProbe_And_FrameSource_Succeeds()
    {
        if (!IsFfmpegAvailable())
        {
            // Skip gracefully if ffmpeg/ffprobe is missing in the environment
            return;
        }

        string? tempVideoPath = null;
        try
        {
            tempVideoPath = await GenerateTestVideoAsync();

            // 1. Test FfmpegMediaProbe
            var prober = new FfmpegMediaProbe();
            var info = await prober.ProbeAsync(tempVideoPath);

            Assert.NotNull(info);
            Assert.True(info.HasVideo);
            Assert.False(info.HasAudio);
            Assert.Equal(1280, info.Width);
            Assert.Equal(720, info.Height);
            Assert.Equal(30.0, info.Fps);
            Assert.InRange(info.DurationSeconds, 0.9, 1.1);

            // 2. Test FfmpegFrameSource
            var frameSource = new FfmpegFrameSource(prober);
            using var frame = await frameSource.GetFrameAsync(tempVideoPath, 0.5);

            Assert.NotNull(frame);
            Assert.Equal(1280, frame.Width);
            Assert.Equal(720, frame.Height);
            Assert.Equal("rgba", frame.PixelFormat);
            Assert.NotNull(frame.Data);
            Assert.Equal(1280 * 720 * 4, frame.Data.Length);
        }
        finally
        {
            if (tempVideoPath != null && File.Exists(tempVideoPath))
            {
                try
                {
                    File.Delete(tempVideoPath);
                }
                catch
                {
                    // Ignore cleanup exceptions
                }
            }
        }
    }

    [Fact]
    public async Task Test_MediaProbe_Throws_On_MissingFile()
    {
        var prober = new FfmpegMediaProbe();
        var missingPath = Path.Combine(Path.GetTempPath(), $"temno_missing_{Guid.NewGuid():N}.mp4");

        await Assert.ThrowsAsync<FileNotFoundException>(() => prober.ProbeAsync(missingPath));
    }

    [Fact]
    public async Task Test_MediaProbe_Throws_On_EmptyPath()
    {
        var prober = new FfmpegMediaProbe();
        await Assert.ThrowsAsync<ArgumentException>(() => prober.ProbeAsync(""));
    }
}
