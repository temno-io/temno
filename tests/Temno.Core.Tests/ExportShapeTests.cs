using System.Diagnostics;
using System.Globalization;
using Temno.Core.Export;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Hardware;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-335. A named size keeps the timeline's shape, a custom box of another shape is padded,
/// and a frame past the hardware encoder limit is encoded in software.
/// </summary>
public sealed class ExportShapeTests
{
    [Fact]
    public async Task VerticalShort_NamedSizes_MatchTheTimelineScaled()
    {
        if (!FfmpegPresent()) return;
        var dir = Directory.CreateTempSubdirectory("temno-wp335-").FullName;
        try
        {
            var source = Path.Combine(dir, "src.mp4");
            await Ffmpeg(
                "-y -f lavfi -i \"color=c=red:s=1080x1920:r=30:d=1,drawbox=x=0:y=960:w=1080:h=960:color=blue:t=fill\" " +
                "-frames:v 2 -c:v libx264 -pix_fmt yuv420p \"" + source + "\"");

            var timeline = TimelineOf(1080, 1920);
            var match = await Export(timeline, dir, "match.mp4", new ExportPreset("m"));
            var p720 = await Export(timeline, dir, "720.mp4", new ExportPreset("a", resolution: ExportResolution.R720p));
            var p1080 = await Export(timeline, dir, "1080.mp4", new ExportPreset("b", resolution: ExportResolution.R1080p));
            var p4k = await Export(timeline, dir, "4k.mp4", new ExportPreset("c", resolution: ExportResolution.R4k));

            await AssertSize(p720, 720, 1280);
            await AssertSize(p1080, 1080, 1920);
            await AssertSize(p4k, 2160, 3840);

            AssertPicture(await Rgb(p1080), 1080, 1920);
            await AssertMatchesScaled(match, p720, 720, 1280);
            await AssertMatchesScaled(match, p1080, 1080, 1920);
            await AssertMatchesScaled(match, p4k, 2160, 3840);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task CustomSize_OtherShape_IsTheBox_WithBlackBars()
    {
        if (!FfmpegPresent()) return;
        var dir = Directory.CreateTempSubdirectory("temno-wp335-pad-").FullName;
        try
        {
            var source = Path.Combine(dir, "src.mp4");
            await Ffmpeg(
                "-y -f lavfi -i \"color=c=red:s=108x192:r=10:d=1,drawbox=x=0:y=96:w=108:h=96:color=blue:t=fill\" " +
                "-frames:v 2 -c:v libx264 -pix_fmt yuv420p \"" + source + "\"");

            var timeline = TimelineOf(108, 192);
            var preset = new ExportPreset("custom") { CustomWidth = 192, CustomHeight = 108 };
            var fitted = ExportOutputSize.ResolveFrame(timeline, preset);
            Assert.True(fitted.IsPadded);
            var output = await Export(timeline, dir, "boxed.mp4", preset);

            await AssertSize(output, 192, 108);
            Assert.Equal("padded to a different shape", await File.ReadAllTextAsync(output + ".note"));

            var rgb = await Rgb(output);
            AssertBlack(rgb, 192, x: 2, y: 54);
            AssertBlack(rgb, 192, x: 189, y: 54);
            AssertRed(rgb, 192, x: 96, y: 20);
            AssertBlue(rgb, 192, x: 96, y: 88);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Wide4k_WithHardwarePreferred_EncodesInSoftware()
    {
        if (!FfmpegPresent()) return;
        var dir = Directory.CreateTempSubdirectory("temno-wp335-wide-").FullName;
        try
        {
            var source = Path.Combine(dir, "src.mp4");
            await Ffmpeg(
                "-y -f lavfi -i color=c=red:s=160x68:r=10:d=1 -frames:v 2 -c:v libx264 -pix_fmt yuv420p \"" + source + "\"");

            var timeline = TimelineOf(2560, 1080);
            var preset = new ExportPreset("wide", resolution: ExportResolution.R4k);
            var output = Path.Combine(dir, "wide.mp4");
            using var exporter = new TimelineExporter(
                new CpuCompositor(),
                new ExportFrameSource(),
                Resolver(source),
                acceleration: HardwareAccelerationMode.PreferHardware,
                ownsFrameSource: true);
            exporter.DisableSmartRender = true;
            await exporter.ExportAsync(timeline, preset, output);

            await AssertSize(output, 5120, 2160);
            Assert.DoesNotContain("nvenc", exporter.LastEncoderName ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("qsv", exporter.LastEncoderName ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("amf", exporter.LastEncoderName ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.Contains("libx26", exporter.LastEncoderName ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(exporter.LastSoftwareEncodeReason);
            Assert.Contains("software", exporter.LastSoftwareEncodeReason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("4096", exporter.LastSoftwareEncodeReason, StringComparison.Ordinal);
            Assert.Contains("5120×2160", exporter.LastSoftwareEncodeReason, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<string> Export(Timeline timeline, string dir, string name, ExportPreset preset)
    {
        var output = Path.Combine(dir, name);
        using var exporter = new TimelineExporter(
            new CpuCompositor(),
            new ExportFrameSource(),
            Resolver(Path.Combine(dir, "src.mp4")),
            acceleration: HardwareAccelerationMode.Software,
            ownsFrameSource: true);
        exporter.DisableSmartRender = true;
        await exporter.ExportAsync(timeline, preset, output);
        if (exporter.LastSmartRenderFallbackReason is not null)
            await File.WriteAllTextAsync(output + ".note", exporter.LastSmartRenderFallbackReason);
        return output;
    }

    private static Timeline TimelineOf(int width, int height)
    {
        var track = new Track { Type = ClipType.video };
        track.Clips.Add(new Clip { Id = "c1", MediaRef = "asset", StartFrame = 0, DurationFrames = 1 });
        return new Timeline
        {
            Name = "T",
            Fps = 30,
            Width = width,
            Height = height,
            Tracks = { track },
        };
    }

    private static MediaResolver Resolver(string source)
    {
        var manifest = new MediaManifest
        {
            Entries =
            {
                new MediaManifestEntry
                {
                    Id = "asset",
                    Name = "pattern",
                    Type = ClipType.video,
                    Source = MediaSource.External(source),
                },
            },
        };
        return new MediaResolver(() => manifest, () => null);
    }

    private static async Task AssertSize(string path, int width, int height)
    {
        var probe = await new FfmpegMediaProbe().ProbeAsync(path);
        Assert.True(probe.HasVideo);
        Assert.Equal(width, probe.Width);
        Assert.Equal(height, probe.Height);
    }

    private static async Task AssertMatchesScaled(string match, string export, int width, int height)
    {
        var scaled = await RgbScaled(match, width, height);
        var actual = await Rgb(export);
        Assert.Equal(scaled.Length, actual.Length);
        AssertPicture(actual, width, height);

        long sum = 0;
        for (var i = 0; i < actual.Length; i++)
            sum += Math.Abs(actual[i] - scaled[i]);
        var mean = sum / (double)actual.Length;
        Assert.True(mean < 12, $"mean abs {mean.ToString("0.00", CultureInfo.InvariantCulture)} at {width}×{height}");
    }

    private static void AssertPicture(byte[] rgb, int width, int height)
    {
        AssertRed(rgb, width, width / 2, height / 4);
        AssertBlue(rgb, width, width / 2, height * 3 / 4);
    }

    private static void AssertRed(byte[] rgb, int stride, int x, int y)
    {
        var (r, g, b) = Pixel(rgb, stride, x, y);
        Assert.True(r > 140 && r > g + 40 && r > b + 40, $"expected red at {x},{y} but got {r},{g},{b}");
    }

    private static void AssertBlue(byte[] rgb, int stride, int x, int y)
    {
        var (r, g, b) = Pixel(rgb, stride, x, y);
        Assert.True(b > 140 && b > r + 40 && b > g + 40, $"expected blue at {x},{y} but got {r},{g},{b}");
    }

    private static void AssertBlack(byte[] rgb, int stride, int x, int y)
    {
        var (r, g, b) = Pixel(rgb, stride, x, y);
        Assert.True(r < 30 && g < 30 && b < 30, $"expected black at {x},{y} but got {r},{g},{b}");
    }

    private static (byte R, byte G, byte B) Pixel(byte[] rgb, int stride, int x, int y)
    {
        var i = (y * stride + x) * 3;
        return (rgb[i], rgb[i + 1], rgb[i + 2]);
    }

    private static Task<byte[]> Rgb(string path) => Raw(
        $"-hide_banner -loglevel error -i \"{path}\" -frames:v 1 -f rawvideo -pix_fmt rgb24 pipe:1");

    private static Task<byte[]> RgbScaled(string path, int width, int height) => Raw(
        $"-hide_banner -loglevel error -i \"{path}\" -frames:v 1 -vf scale={width}:{height}:flags=bilinear -f rawvideo -pix_fmt rgb24 pipe:1");

    private static async Task<byte[]> Raw(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(FFmpegResolver.GetFFmpegPath(), arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        var stderr = process.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(ms);
        await process.WaitForExitAsync();
        var err = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(err);
        return ms.ToArray();
    }

    private static async Task Ffmpeg(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(FFmpegResolver.GetFFmpegPath(), arguments)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        var err = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(err);
    }

    private static bool FfmpegPresent()
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
}
