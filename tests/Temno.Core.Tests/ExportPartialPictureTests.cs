using System.Diagnostics;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Hardware;
using Temno.Media.Export;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-154. <c>TimelineExporter</c> refused an export only when <em>every</em> visual layer had been
/// skipped. A timeline where some clips resolved and others did not rendered the survivors, dropped
/// the rest, and returned success — a file with holes in the picture, discoverable only by watching
/// it. These run a real encode through ffmpeg, in the same shape as <c>ExportAudioTests</c>.
/// </summary>
public sealed class ExportPartialPictureTests
{
    [Fact]
    public async Task PartialSkip_FailsTheExportAndNamesTheOfflineClip()
    {
        if (!IsFfmpegAvailable()) return;
        var dir = CreateTempDirectory();
        try
        {
            var (timeline, manifest) = await TwoClipTimelineAsync(dir);
            var resolver = new MediaResolver(() => manifest, () => dir);
            var output = Path.Combine(dir, "out.mp4");

            // Control: both files present, the export succeeds and drops nothing.
            var exporter = NewExporter(resolver);
            await exporter.ExportAsync(timeline, Preset(), output);
            Assert.True(File.Exists(output));
            Assert.Empty(exporter.LastVideoDiagnostics);

            File.Delete(Path.Combine(dir, "b.mp4"));

            var partial = NewExporter(resolver);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => partial.ExportAsync(timeline, Preset(), Path.Combine(dir, "partial.mp4")));

            Assert.Contains("missing picture", error.Message);
            // Named by its manifest name, not its asset id: "b-asset" tells the user nothing about
            // which clip to relink.
            Assert.Contains("second clip", error.Message);
            Assert.False(File.Exists(Path.Combine(dir, "partial.mp4")));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public async Task AllowPartialPicture_LetsTheExportThroughAndStillReportsWhatWasDropped()
    {
        if (!IsFfmpegAvailable()) return;
        var dir = CreateTempDirectory();
        try
        {
            var (timeline, manifest) = await TwoClipTimelineAsync(dir);
            var resolver = new MediaResolver(() => manifest, () => dir);
            File.Delete(Path.Combine(dir, "b.mp4"));
            var output = Path.Combine(dir, "anyway.mp4");

            var exporter = NewExporter(resolver);
            exporter.AllowPartialPicture = true;
            await exporter.ExportAsync(timeline, Preset(), output);

            // A rough cut over offline media is a legitimate thing to want, so consent gets through —
            // but the diagnostics still say what was lost, which is what the editor reports.
            Assert.True(File.Exists(output));
            var dropped = Assert.Single(exporter.LastVideoDiagnostics);
            Assert.Equal("b-asset", dropped.MediaRef);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    /// <summary>
    /// The all-black case predates this package and must keep refusing whatever the flag says:
    /// consent to a partial picture is not consent to an empty file.
    /// </summary>
    [Fact]
    public async Task EveryLayerSkipped_StillFailsEvenWithPartialPictureAllowed()
    {
        if (!IsFfmpegAvailable()) return;
        var dir = CreateTempDirectory();
        try
        {
            var (timeline, manifest) = await TwoClipTimelineAsync(dir);
            var resolver = new MediaResolver(() => manifest, () => dir);
            File.Delete(Path.Combine(dir, "a.mp4"));
            File.Delete(Path.Combine(dir, "b.mp4"));

            var exporter = NewExporter(resolver);
            exporter.AllowPartialPicture = true;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => exporter.ExportAsync(timeline, Preset(), Path.Combine(dir, "black.mp4")));

            Assert.Contains("no picture", error.Message);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void AllowPartialPicture_DefaultsToFalse()
    {
        // The default decides what a caller that cannot ask — the MCP export_project tool — does.
        Assert.False(
            new TimelineExporter(new CpuCompositor(), new ExportFrameSource(), ownsFrameSource: true)
                .AllowPartialPicture);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static TimelineExporter NewExporter(MediaResolver resolver) => new(
        new CpuCompositor(),
        new ExportFrameSource(),
        resolver,
        // Software only: an NVENC init failure would retry the whole pass and clear the skip list,
        // which would make this test depend on the machine's encoder rather than on the guard.
        acceleration: HardwareAccelerationMode.Software,
        ownsFrameSource: true);

    private static ExportPreset Preset() =>
        new("test", VideoCodec.H264, ExportResolution.MatchTimeline);

    private static async Task<(Timeline, MediaManifest)> TwoClipTimelineAsync(string dir)
    {
        foreach (var (name, colour) in new[] { ("a", "red"), ("b", "green") })
        {
            await RunFfmpegAsync(
                $"-y -f lavfi -i color=c={colour}:s=160x90:d=1:r=10 -c:v libx264 -pix_fmt yuv420p " +
                $"\"{Path.Combine(dir, name + ".mp4")}\"");
        }

        var manifest = new MediaManifest
        {
            Entries =
            {
                new MediaManifestEntry
                {
                    Id = "a-asset", Name = "first clip", Type = ClipType.video,
                    Source = MediaSource.External(Path.Combine(dir, "a.mp4")),
                },
                new MediaManifestEntry
                {
                    Id = "b-asset", Name = "second clip", Type = ClipType.video,
                    Source = MediaSource.External(Path.Combine(dir, "b.mp4")),
                },
            },
        };

        var track = new Track { Type = ClipType.video };
        track.Clips.Add(new Clip { Id = "c1", MediaRef = "a-asset", StartFrame = 0, DurationFrames = 10 });
        track.Clips.Add(new Clip { Id = "c2", MediaRef = "b-asset", StartFrame = 10, DurationFrames = 10 });
        var timeline = new Timeline { Name = "T", Fps = 10, Width = 160, Height = 90, Tracks = { track } };
        return (timeline, manifest);
    }

    private static bool IsFfmpegAvailable()
    {
        try
        {
            foreach (var tool in new[] { FFmpegResolver.GetFFmpegPath(), FFmpegResolver.GetFFprobePath() })
            {
                using var process = Process.Start(new ProcessStartInfo(tool, "-version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                process?.WaitForExit();
                if (process?.ExitCode != 0) return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task RunFfmpegAsync(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(FFmpegResolver.GetFFmpegPath(), arguments)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start FFmpeg.");
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg fixture generation failed: {stderr}");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"temno_wp154_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }
}
