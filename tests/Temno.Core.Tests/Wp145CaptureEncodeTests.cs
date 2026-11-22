using Temno.Media;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-145: declared extra. Capture encodes compositor RGBA and sequential BGRA through
/// <see cref="FfmpegPngEncoder"/>; the export/media tree must not mention preview guides.
/// </summary>
public sealed class Wp145CaptureEncodeTests
{
    [Fact]
    public async Task EncodeAsync_AcceptsRgbaAndBgra()
    {
        var redRgba = new byte[] { 255, 0, 0, 255 };
        var redBgra = new byte[] { 0, 0, 255, 255 };
        var pngRgba = await FfmpegPngEncoder.EncodeAsync(new VideoFrame(1, 1, redRgba, "rgba"));
        var pngBgra = await FfmpegPngEncoder.EncodeAsync(new VideoFrame(1, 1, redBgra, "bgra"));
        Assert.True(pngRgba.Length > 8);
        Assert.True(pngBgra.Length > 8);
        Assert.Equal(0x89, pngRgba[0]);
        Assert.Equal(0x89, pngBgra[0]);
        Assert.Equal(pngRgba, pngBgra);
    }

    [Fact]
    public void MediaAndExportSourcesDoNotReferencePreviewGuides()
    {
        var mediaRoot = FindRepoDirectory("src", "Temno.Media");
        Assert.True(Directory.Exists(mediaRoot), mediaRoot);
        foreach (var file in Directory.EnumerateFiles(mediaRoot, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("PreviewGuide", text);
            Assert.DoesNotContain("GuideOverlay", File.ReadAllText(file));
        }
    }

    [Fact]
    public async Task TimelineRenderer_IgnoresPreviewGuideJsonOnRealProject()
    {
        var package = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Temno Pro", "Projects", "55.palmier");
        if (!Directory.Exists(package))
            return;

        var guides = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temno Pro", "preview-guides.json");
        var previous = File.Exists(guides) ? File.ReadAllText(guides) : null;
        var session = Temno.Core.Session.ProjectSession.Open(package);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(guides)!);
            File.WriteAllText(guides, """{"Grid":5,"SafeZones":7,"Format":4,"Hidden":false}""");
            using var renderer = new TimelineFrameRenderer(new FfmpegFrameSource(), session.Resolver);
            using var on = await renderer.RenderAsync(session.ActiveTimeline, 0, 320, 180);
            File.WriteAllText(guides, """{"Grid":0,"SafeZones":0,"Format":0,"Hidden":true}""");
            using var off = await renderer.RenderAsync(session.ActiveTimeline, 0, 320, 180);
            Assert.NotNull(on);
            Assert.NotNull(off);
            var pngOn = await FfmpegPngEncoder.EncodeAsync(on!);
            var pngOff = await FfmpegPngEncoder.EncodeAsync(off!);
            Assert.Equal(pngOn, pngOff);
        }
        finally
        {
            if (previous is null)
            {
                if (File.Exists(guides)) File.Delete(guides);
            }
            else File.WriteAllText(guides, previous);
        }
    }

    private static string FindRepoDirectory(params string[] relativeParts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeParts]);
            if (Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine([AppContext.BaseDirectory, .. relativeParts]);
    }
}
