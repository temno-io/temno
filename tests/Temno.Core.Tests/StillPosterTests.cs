using System;
using System.IO;
using System.Threading.Tasks;
using Temno.Media.Cache;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-420. A still asks for its poster at 0 s. With `-ss` before `-i`, even a seek to 0 left FFmpeg with no
/// frame of a JPEG, so every .jpg kept the type icon in the Media panel while PNGs got thumbnails.
/// These run the real FFmpeg through the product's generator.
/// </summary>
public sealed class StillPosterTests : IDisposable
{
    private readonly string _root = FfmpegFixture.CreateTempDirectory();

    public void Dispose() => FfmpegFixture.DeleteTempDirectory(_root);

    [Theory]
    [InlineData("square.jpg", 256, 256, 36, 36)]
    [InlineData("wide.jpg", 1920, 1080, 64, 36)]
    [InlineData("square.jpeg", 256, 256, 36, 36)]
    [InlineData("square.png", 256, 256, 36, 36)]
    public async Task StillPoster_IsWrittenAtZero(string name, int width, int height, int posterWidth, int posterHeight)
    {
        if (!FfmpegFixture.IsAvailable) return;
        var still = Path.Combine(_root, name);
        await FfmpegFixture.RunAsync(
            $"-hide_banner -loglevel error -y -f lavfi -i testsrc2=size={width}x{height}:rate=1 -frames:v 1 -update 1 \"{still}\"");
        var poster = Path.Combine(_root, name + ".poster.jpg");

        await new FfmpegThumbnailGenerator().GenerateAsync(still, 0, 36, poster);

        Assert.True(Temno.Generation.ImageDimensions.TryRead(poster, out var w, out var h));
        Assert.Equal((posterWidth, posterHeight), (w, h));
    }

    [Fact]
    public async Task VideoPoster_AtZero_IsTheFirstFrame()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var clip = Path.Combine(_root, "clip.mp4");
        await FfmpegFixture.WriteTestClipAsync(clip, fps: 25, seconds: 2, width: 320, height: 180);
        var poster = Path.Combine(_root, "clip.poster.jpg");

        await new FfmpegThumbnailGenerator().GenerateAsync(clip, 0, 36, poster);

        Assert.True(Temno.Generation.ImageDimensions.TryRead(poster, out var w, out var h));
        Assert.Equal((64, 36), (w, h));
    }
}
