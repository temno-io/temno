using Temno.Media;
using Xunit;

namespace Temno.Core.Tests;

public sealed class PreviewPixelsTests
{
    [Fact]
    public void BgraPreservesEveryChannelValueAndSource()
    {
        var bytes = Enumerable.Range(0, 256).SelectMany(i => new byte[]
            { (byte)i, (byte)(255-i), (byte)(i ^ 127), (byte)i }).ToArray();
        var before = (byte[])bytes.Clone();
        using var frame = new VideoFrame(256, 1, bytes, "bgra");
        var display = new byte[bytes.Length];
        PreviewPixels.CopyToBgra(frame, display);
        Assert.Equal(before, display);
        Assert.Equal(before, frame.Data);
    }

    [Fact]
    public void RgbaReordersOnlyRedAndBlueIncludingAlpha()
    {
        byte[] bytes = [11,22,33,44, 0,127,255,128];
        using var frame = new VideoFrame(2, 1, bytes, "rgba");
        var display = new byte[8];
        PreviewPixels.CopyToBgra(frame, display);
        Assert.Equal(new byte[] {33,22,11,44, 255,127,0,128}, display);
        Assert.Equal(new byte[] {11,22,33,44, 0,127,255,128}, frame.Data);
    }

    [Fact]
    public void RgbaCanReorderItsOwnBuffer()
    {
        using var frame = new VideoFrame(1,1,[11,22,33,44],"rgba");
        PreviewPixels.CopyToBgra(frame, frame.Data);
        Assert.Equal(new byte[] {33,22,11,44}, frame.Data);
    }

    [Fact]
    public void RejectsIncompleteDestinationAndUnknownFormat()
    {
        using var frame = new VideoFrame(1,1,[1,2,3,4],"bgra");
        Assert.Throws<ArgumentException>(() => PreviewPixels.CopyToBgra(frame,new byte[3]));
        using var unknown = new VideoFrame(1,1,[1,2,3,4],"rgb24");
        Assert.Throws<NotSupportedException>(() => PreviewPixels.CopyToBgra(unknown,new byte[4]));
    }
}
