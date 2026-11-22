using Temno.Core.Models;
using Temno.Media.Export;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// Codec ↔ container validation, and the diagnostics that replaced "The pipe has been ended."
/// </summary>
public sealed class ExportContainerTests
{
    [Theory]
    [InlineData(VideoCodec.H264, ".mp4", true)]
    [InlineData(VideoCodec.H264, ".mov", true)]
    [InlineData(VideoCodec.H265, ".mp4", true)]
    [InlineData(VideoCodec.HevcHdr, ".mov", true)]
    [InlineData(VideoCodec.ProRes, ".mov", true)]
    // ffmpeg's mp4 muxer refuses a ProRes stream: "Could not find tag for codec prores".
    [InlineData(VideoCodec.ProRes, ".mp4", false)]
    [InlineData(VideoCodec.ProRes, ".mkv", false)]
    public void IsSupported_MatchesFfmpegMuxerReality(VideoCodec codec, string extension, bool expected)
        => Assert.Equal(expected, ExportContainer.IsSupported(codec, "C:\\out" + extension));

    [Fact]
    public void EnsureCodecMatchesContainer_ProResIntoMp4_ThrowsBeforeRendering()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ExportContainer.EnsureCodecMatchesContainer(VideoCodec.ProRes, @"C:\movies\cut.mp4"));

        Assert.Contains("ProRes", ex.Message);
        Assert.Contains(".mov", ex.Message);
        Assert.Contains("cut.mov", ex.Message);   // actionable suggestion, not just a refusal
    }

    [Fact]
    public void EnsureCodecMatchesContainer_ValidPairing_DoesNotThrow()
    {
        ExportContainer.EnsureCodecMatchesContainer(VideoCodec.ProRes, @"C:\movies\cut.mov");
        ExportContainer.EnsureCodecMatchesContainer(VideoCodec.H264, @"C:\movies\cut.mp4");
    }

    [Fact]
    public void CoerceExtension_RewritesOnlyWhenNecessary()
    {
        // The agent path defaults every video export to .mp4 regardless of codec. Only ProRes has
        // to move: hvc1-tagged 10-bit HEVC muxes into mp4 fine, so HDR is left where it was asked for.
        Assert.Equal(@"C:\d\p.mov", ExportContainer.CoerceExtension(VideoCodec.ProRes, @"C:\d\p.mp4"));
        Assert.Equal(@"C:\d\p.mp4", ExportContainer.CoerceExtension(VideoCodec.HevcHdr, @"C:\d\p.mp4"));
        Assert.Equal(@"C:\d\p.mov", ExportContainer.CoerceExtension(VideoCodec.HevcHdr, @"C:\d\p.mov"));
        Assert.Equal(@"C:\d\p.mp4", ExportContainer.CoerceExtension(VideoCodec.H264, @"C:\d\p.mp4"));
        Assert.Equal(@"C:\d\p.mov", ExportContainer.CoerceExtension(VideoCodec.H264, @"C:\d\p.mov"));
    }

    [Fact]
    public void SummarizeStderr_KeepsTheReason_DropsTheNoise()
    {
        const string stderr = """
            [mp4 @ 0000] Could not find tag for codec prores in stream #0, codec not currently supported in container
            [out#0/mp4 @ 0000] Could not write header (incorrect codec parameters ?): Invalid argument
            frame=  120 fps=0.0 q=-1.0 Lsize=       0KiB time=N/A bitrate=N/A
            [vost#0:0 @ 0000] Terminating thread with return code -22 (Invalid argument)
            Conversion failed!
            """;

        var summary = FfmpegEncodeException.SummarizeStderr(stderr);

        Assert.Contains("Could not find tag for codec prores", summary);
        Assert.Contains("Could not write header", summary);
        Assert.DoesNotContain("frame=", summary);
        Assert.DoesNotContain("Terminating thread", summary);
        Assert.DoesNotContain("Conversion failed", summary);
    }

    [Fact]
    public void EncodeException_CarriesTheRealReason_NotABrokenPipe()
    {
        var ex = FfmpegEncodeException.Create(
            "video encode", "hevc_nvenc", "-y -i pipe:0 ...", exitCode: -22,
            stderr: "[hevc_nvenc @ 0] No capable devices found", framesWritten: 0, bytesWritten: 0);

        Assert.Contains("hevc_nvenc", ex.Message);
        Assert.Contains("No capable devices found", ex.Message);
        Assert.DoesNotContain("pipe has been ended", ex.Message);
        Assert.True(ex.IsInitializationFailure);
    }

    [Fact]
    public void IsInitializationFailure_DistinguishesEncoderRefusalFromMidRenderFault()
    {
        // Encoder never opened: retrying on the software encoder is worth it.
        Assert.True(FfmpegEncodeException.Create(
            "video encode", "h264_nvenc", "", -22, "Error while opening encoder", 0, 0).IsInitializationFailure);

        // A small canvas fits several frames inside the OS pipe buffer, so writes keep succeeding
        // after ffmpeg has already gone — frame count alone would misclassify this.
        Assert.True(FfmpegEncodeException.Create(
            "video encode", "h264_nvenc", "", -22, "Error while opening encoder", 6, 73_728).IsInitializationFailure);

        // 40 000 frames in: a disk or driver fault, not encoder selection. Re-rendering from frame 0
        // would silently double an hour-long export.
        Assert.False(FfmpegEncodeException.Create(
            "video encode", "h264_nvenc", "", 1, "Error writing trailer: No space left on device",
            40_000, 40_000L * 8_294_400).IsInitializationFailure);
    }
}
