using Temno.Media;
using Xunit;

namespace Temno.Core.Tests;

public sealed class FfmpegVideoFilterTests
{
    [Fact]
    public void IdentitySize_SkipsScale()
    {
        Assert.Equal("format=bgra", FfmpegVideoFilter.BgraConvert(1920, 1080, 1920, 1080));
    }

    [Fact]
    public void Resize_UsesLanczos()
    {
        Assert.Equal("scale=1920:1080:flags=lanczos,format=bgra", FfmpegVideoFilter.BgraConvert(1920, 1080, 3840, 2160));
    }

    [Fact]
    public void UnknownSource_StillScales()
    {
        Assert.Equal("scale=1280:720:flags=lanczos,format=bgra", FfmpegVideoFilter.BgraConvert(1280, 720, 0, 0));
    }
}
