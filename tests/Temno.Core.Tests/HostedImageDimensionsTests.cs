using System;
using System.IO;
using Temno.Generation;
using Xunit;

namespace Temno.Core.Tests;

public class HostedImageDimensionsTests
{
    private static byte[] Png(int width, int height, byte colorType = 6)
    {
        var bytes = new byte[32];
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
        bytes[4] = 0x0D; bytes[5] = 0x0A; bytes[6] = 0x1A; bytes[7] = 0x0A;
        bytes[12] = (byte)'I'; bytes[13] = (byte)'H'; bytes[14] = (byte)'D'; bytes[15] = (byte)'R';
        bytes[16] = (byte)(width >> 24); bytes[17] = (byte)(width >> 16);
        bytes[18] = (byte)(width >> 8); bytes[19] = (byte)width;
        bytes[20] = (byte)(height >> 24); bytes[21] = (byte)(height >> 16);
        bytes[22] = (byte)(height >> 8); bytes[23] = (byte)height;
        bytes[24] = 8; bytes[25] = colorType;
        return bytes;
    }

    private static byte[] Jpeg(int width, int height)
        =>
        [
            0xFF, 0xD8,
            0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00,
            0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x0B, 0x08,
            (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width,
            0x01, 0x01, 0x11, 0x00,
        ];

    private static byte[] WebP(string fourCc, byte[] payload)
    {
        var bytes = new byte[12 + 8 + payload.Length];
        bytes[0] = (byte)'R'; bytes[1] = (byte)'I'; bytes[2] = (byte)'F'; bytes[3] = (byte)'F';
        bytes[8] = (byte)'W'; bytes[9] = (byte)'E'; bytes[10] = (byte)'B'; bytes[11] = (byte)'P';
        bytes[12] = (byte)fourCc[0]; bytes[13] = (byte)fourCc[1];
        bytes[14] = (byte)fourCc[2]; bytes[15] = (byte)fourCc[3];
        Array.Copy(payload, 0, bytes, 20, payload.Length);
        return bytes;
    }

    [Fact]
    public void TryRead_PngReportsActualSize()
    {
        using var stream = new MemoryStream(Png(320, 240));
        Assert.True(ImageDimensions.TryRead(stream, out var w, out var h));
        Assert.Equal(320, w);
        Assert.Equal(240, h);
    }

    [Fact]
    public void TryRead_AlphaPngReportsActualSize()
    {
        using var stream = new MemoryStream(Png(64, 64, colorType: 6));
        Assert.True(ImageDimensions.TryRead(stream, out var w, out var h));
        Assert.Equal(64, w);
        Assert.Equal(64, h);
    }

    [Fact]
    public void TryRead_JpegSkipsAppSegmentsToSof()
    {
        using var stream = new MemoryStream(Jpeg(1920, 1080));
        Assert.True(ImageDimensions.TryRead(stream, out var w, out var h));
        Assert.Equal(1920, w);
        Assert.Equal(1080, h);
    }

    [Fact]
    public void TryRead_WebPLossy()
    {
        var payload = new byte[] { 0x00, 0x00, 0x00, 0x9D, 0x01, 0x2A, 0x40, 0x01, 0xF0, 0x00 };
        using var stream = new MemoryStream(WebP("VP8 ", payload));
        Assert.True(ImageDimensions.TryRead(stream, out var w, out var h));
        Assert.Equal(320, w);
        Assert.Equal(240, h);
    }

    [Fact]
    public void TryRead_WebPLossless()
    {
        // 8x8: width-1 and height-1 packed as 14-bit little-endian fields.
        var payload = new byte[] { 0x2F, 0x07, 0xC0, 0x01, 0x00 };
        using var stream = new MemoryStream(WebP("VP8L", payload));
        Assert.True(ImageDimensions.TryRead(stream, out var w, out var h));
        Assert.Equal(8, w);
        Assert.Equal(8, h);
    }

    [Fact]
    public void TryRead_WebPExtended()
    {
        var payload = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x07, 0x00, 0x00 };
        using var stream = new MemoryStream(WebP("VP8X", payload));
        Assert.True(ImageDimensions.TryRead(stream, out var w, out var h));
        Assert.Equal(8, w);
        Assert.Equal(8, h);
    }

    [Fact]
    public void TryRead_RejectsGarbageAndTruncation()
    {
        using (var garbage = new MemoryStream("dummy media data, not an image at all........"u8.ToArray()))
            Assert.False(ImageDimensions.TryRead(garbage, out _, out _));
        using (var truncated = new MemoryStream([0x89, 0x50, 0x4E]))
            Assert.False(ImageDimensions.TryRead(truncated, out _, out _));
        Assert.False(ImageDimensions.TryRead(Path.Combine(Path.GetTempPath(), "temno-nope-" + Guid.NewGuid().ToString("N")), out _, out _));
    }

    [Fact]
    public void TryRead_PathOverloadReadsFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"temno-dim-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, Png(17, 33));
            Assert.True(ImageDimensions.TryRead(path, out var w, out var h));
            Assert.Equal(17, w);
            Assert.Equal(33, h);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
