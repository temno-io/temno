using Temno.Media;
using Temno.Media.Export;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-405: a phone's portrait video is stored landscape with a display rotation, and some cameras store
/// non-square pixels. ffprobe reports the stored size; ffmpeg autorotates on decode. Temno sized every
/// decode from the stored size, so a rotated clip came back scrambled or stretched into the landscape
/// frame, and an anamorphic clip drew squeezed. The probe now reports the shown size and every decode
/// path delivers it.
/// </summary>
public sealed class ReshapedMediaTests : IAsyncLifetime
{
    private string _root = "";
    private string _rotated = "";
    private string _anamorphic = "";
    private string _plain = "";

    public async Task InitializeAsync()
    {
        _root = FfmpegFixture.CreateTempDirectory();
        if (!FfmpegFixture.IsAvailable) return;
        var halves = "color=c=red:s=32x36:d=1:r=10[l];color=c=blue:s=32x36:d=1:r=10[r];[l][r]hstack";
        _plain = Path.Combine(_root, "plain.mp4");
        await FfmpegFixture.RunAsync(
            $"-hide_banner -loglevel error -y -f lavfi -i \"{halves},format=yuv420p\" -c:v libx264 \"{_plain}\"");
        // Coded 64x36 landscape, shown turned 90 degrees: a 36x64 portrait picture.
        _rotated = Path.Combine(_root, "rotated.mp4");
        await FfmpegFixture.RunAsync(
            $"-hide_banner -loglevel error -y -display_rotation:v 90 -i \"{_plain}\" -c copy \"{_rotated}\"");
        // Coded 32x36 with 2:1 pixels: shown 64x36, red left half and blue right half.
        var narrow = "color=c=red:s=16x36:d=1:r=10[l];color=c=blue:s=16x36:d=1:r=10[r];[l][r]hstack";
        _anamorphic = Path.Combine(_root, "anamorphic.mp4");
        await FfmpegFixture.RunAsync(
            $"-hide_banner -loglevel error -y -f lavfi -i \"{narrow},setsar=2,format=yuv420p\" -c:v libx264 \"{_anamorphic}\"");
    }

    public Task DisposeAsync()
    {
        FfmpegFixture.DeleteTempDirectory(_root);
        return Task.CompletedTask;
    }

    private static (int R, int B) Pixel(VideoFrame frame, int x, int y)
    {
        var i = (y * frame.Width + x) * 4;
        var bgra = string.Equals(frame.PixelFormat, "bgra", StringComparison.OrdinalIgnoreCase);
        return bgra ? (frame.Data[i + 2], frame.Data[i]) : (frame.Data[i], frame.Data[i + 2]);
    }

    private static void AssertRed((int R, int B) p) { Assert.InRange(p.R, 180, 255); Assert.InRange(p.B, 0, 80); }
    private static void AssertBlue((int R, int B) p) { Assert.InRange(p.B, 180, 255); Assert.InRange(p.R, 0, 80); }

    /// <summary>The rotated clip shows blue on top and red below, 36 wide and 64 tall.</summary>
    private static void AssertUprightPortrait(VideoFrame frame)
    {
        Assert.Equal((36, 64), (frame.Width, frame.Height));
        AssertBlue(Pixel(frame, 18, 8));
        AssertRed(Pixel(frame, 18, 56));
    }

    /// <summary>The anamorphic clip shows 64 wide, red left and blue right.</summary>
    private static void AssertUnsqueezed(VideoFrame frame)
    {
        Assert.Equal((64, 36), (frame.Width, frame.Height));
        AssertRed(Pixel(frame, 10, 18));
        AssertBlue(Pixel(frame, 54, 18));
    }

    [Theory]
    [InlineData(1920, 1080, 90.0, "1:1", 1080, 1920, 1080, 1920, 90)]
    [InlineData(1920, 1080, -90.0, null, 1080, 1920, 1080, 1920, 270)]
    [InlineData(1920, 1080, 180.0, "0:1", 1920, 1080, 1920, 1080, 180)]
    [InlineData(720, 576, 0.0, "16:15", 768, 576, 720, 576, 0)]
    [InlineData(720, 576, 90.0, "16:15", 576, 768, 576, 720, 90)]
    public void Shape_ReportsTheShownAndDecodedSizes(
        int w, int h, double rotation, string? sar, int shownW, int shownH, int decodedW, int decodedH, int turn)
    {
        var shape = FfmpegMediaProbe.Shape(w, h, rotation, sar);
        Assert.Equal((shownW, shownH), (shape.Width, shape.Height));
        Assert.Equal((decodedW, decodedH), (shape.DecodedWidth, shape.DecodedHeight));
        Assert.Equal(turn, shape.Rotation);
    }

    [Theory]
    [InlineData(60.0, true)]
    [InlineData(10.0, true)]
    [InlineData(-30.0, true)]
    [InlineData(0.4, false)]
    [InlineData(360.0, false)]
    public void OddRotation_DecodesAtTheCodedSize_ButStillCountsAsTurned(double rotation, bool turned)
    {
        // WP-405 T1 finding: ffmpeg's autorotate applies only quarter turns; 60 degrees decodes 1920x1080.
        var shape = FfmpegMediaProbe.Shape(1920, 1080, rotation, null);
        Assert.Equal((1920, 1080), (shape.Width, shape.Height));
        Assert.Equal((1920, 1080), (shape.DecodedWidth, shape.DecodedHeight));
        Assert.Equal(0, shape.Rotation);
        Assert.Equal(turned, shape.Turned);
    }

    [Fact]
    public async Task OddRotationFile_DecodesUnturned_AndIsNeverCopied()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var odd = Path.Combine(_root, "odd.mp4");
        await FfmpegFixture.RunAsync(
            $"-hide_banner -loglevel error -y -display_rotation:v 60 -i \"{_plain}\" -c copy \"{odd}\"");

        var info = await new FfmpegMediaProbe().ProbeAsync(odd);
        Assert.Equal((64, 36), (info.Width, info.Height));
        using (var frame = await new FfmpegFrameSource().GetFrameAsync(odd, 0.2))
        {
            Assert.Equal((64, 36), (frame.Width, frame.Height));
            AssertRed(Pixel(frame, 10, 18));
            AssertBlue(Pixel(frame, 54, 18));
        }
        Assert.True((await new FfmpegStreamProbe().ProbeAsync(odd)).Reshaped);
    }

    [Fact]
    public async Task Probe_ReportsTheShownSize()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var probe = new FfmpegMediaProbe();

        var rotated = await probe.ProbeAsync(_rotated);
        Assert.Equal((36, 64), (rotated.Width, rotated.Height));
        Assert.Equal((36, 64), rotated.DecodedSize);
        Assert.Equal(90, rotated.Rotation);
        Assert.True(rotated.IsReshaped);

        var anamorphic = await probe.ProbeAsync(_anamorphic);
        Assert.Equal((64, 36), (anamorphic.Width, anamorphic.Height));
        Assert.Equal((32, 36), anamorphic.DecodedSize);
        Assert.Equal(2.0, anamorphic.SampleAspect, 6);

        var plain = await probe.ProbeAsync(_plain);
        Assert.Equal((64, 36), (plain.Width, plain.Height));
        Assert.False(plain.IsReshaped);
    }

    [Fact]
    public async Task SeekDecode_DeliversTheShownPicture()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var source = new FfmpegFrameSource();
        using (var frame = await source.GetFrameAsync(_rotated, 0.2)) AssertUprightPortrait(frame);
        using (var frame = await source.GetFrameAsync(_anamorphic, 0.2)) AssertUnsqueezed(frame);
    }

    [Fact]
    public async Task PlaybackDecode_DeliversTheShownPicture()
    {
        if (!FfmpegFixture.IsAvailable) return;
        using var source = new SequentialFrameSource();
        using (var frame = await source.GetFrameAsync(_rotated, 0.2)) AssertUprightPortrait(frame);
        using (var frame = await source.GetFrameAsync(_anamorphic, 0.2)) AssertUnsqueezed(frame);
    }

    [Fact]
    public async Task ExportDecode_DeliversTheShownPicture()
    {
        if (!FfmpegFixture.IsAvailable) return;
        using var source = new ExportFrameSource();
        using (var frame = await source.GetFrameAsync(_rotated, 0.2)) AssertUprightPortrait(frame);
        using (var frame = await source.GetFrameAsync(_anamorphic, 0.2)) AssertUnsqueezed(frame);
    }

    [Theory]
    [InlineData("rotated", 20, 36)]
    [InlineData("anamorphic", 64, 36)]
    [InlineData("plain", 64, 36)]
    public async Task Thumbnail_HasTheShownAspect(string clip, int width, int height)
    {
        if (!FfmpegFixture.IsAvailable) return;
        var path = clip switch { "rotated" => _rotated, "anamorphic" => _anamorphic, _ => _plain };
        var poster = Path.Combine(_root, $"{clip}.jpg");
        await FfmpegFixture.RunAsync(Temno.Media.Cache.FfmpegThumbnailGenerator.BuildArguments(path, 0.2, 36, poster));

        Assert.True(Temno.Generation.ImageDimensions.TryRead(poster, out var w, out var h));
        Assert.Equal((width, height), (w, h));
    }

    [Fact]
    public async Task SmartRender_RefusesToCopyAReshapedSource()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var probe = new FfmpegStreamProbe();
        Assert.True((await probe.ProbeAsync(_rotated)).Reshaped);
        Assert.True((await probe.ProbeAsync(_anamorphic)).Reshaped);
        Assert.False((await probe.ProbeAsync(_plain)).Reshaped);
    }
}
