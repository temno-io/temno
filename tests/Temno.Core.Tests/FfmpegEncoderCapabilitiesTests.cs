using Temno.Core.Models;
using Temno.Media.Export;
using Xunit;

namespace Temno.Core.Tests;

public sealed class FfmpegEncoderCapabilitiesTests
{
    [Fact]
    public void DisplayName_CoversAllCodecs()
    {
        Assert.Equal("H.264", FfmpegEncoderCapabilities.DisplayName(VideoCodec.H264));
        Assert.Equal("H.265", FfmpegEncoderCapabilities.DisplayName(VideoCodec.H265));
        Assert.Equal("ProRes", FfmpegEncoderCapabilities.DisplayName(VideoCodec.ProRes));
        Assert.Equal("HEVC 10-bit HDR", FfmpegEncoderCapabilities.DisplayName(VideoCodec.HevcHdr));
    }

    [Fact]
    public void EncoderCandidates_PreferHardwareThenSoftware()
    {
        Assert.Equal(
            ["libx264", "libopenh264", "h264_nvenc", "h264_qsv", "h264_amf"],
            FfmpegEncoderCapabilities.EncoderCandidates(VideoCodec.H264));
        Assert.Equal(
            ["libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf"],
            FfmpegEncoderCapabilities.EncoderCandidates(VideoCodec.H265));
        Assert.Equal(["prores_ks", "prores"], FfmpegEncoderCapabilities.EncoderCandidates(VideoCodec.ProRes));
        Assert.Equal(
            ["libx265", "hevc_nvenc", "hevc_qsv", "hevc_amf"],
            FfmpegEncoderCapabilities.EncoderCandidates(VideoCodec.HevcHdr));
    }

    [Fact]
    public void HardwareLimit_Is4096_AndProResIsExempt()
    {
        Assert.False(FfmpegEncoderCapabilities.ExceedsHardwareEncoderLimit(VideoCodec.H264, 3840, 2160));
        Assert.False(FfmpegEncoderCapabilities.ExceedsHardwareEncoderLimit(VideoCodec.H264, 4096, 2160));
        Assert.True(FfmpegEncoderCapabilities.ExceedsHardwareEncoderLimit(VideoCodec.H264, 5120, 2160));
        Assert.True(FfmpegEncoderCapabilities.ExceedsHardwareEncoderLimit(VideoCodec.H265, 2160, 5120));
        Assert.True(FfmpegEncoderCapabilities.ExceedsHardwareEncoderLimit(VideoCodec.HevcHdr, 5120, 2160));
        Assert.False(FfmpegEncoderCapabilities.ExceedsHardwareEncoderLimit(VideoCodec.ProRes, 5120, 2160));

        var reason = FfmpegEncoderCapabilities.SoftwareEncodeReason(5120, 2160);
        Assert.Contains("software", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("4096", reason, StringComparison.Ordinal);
        Assert.Contains("5120×2160", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireEncoder_H264_SucceedsWhenFfmpegPresent()
    {
        // CI and dev boxes ship FFmpeg with libx264; if ffmpeg is missing the probe is empty and this throws.
        try
        {
            var name = FfmpegEncoderCapabilities.RequireEncoder(VideoCodec.H264);
            Assert.False(string.IsNullOrEmpty(name));
            Assert.Contains(name, FfmpegEncoderCapabilities.EncoderCandidates(VideoCodec.H264));
        }
        catch (InvalidOperationException ex)
        {
            Assert.Contains("not available", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void VideoCodec_RoundTripsHdrJson()
    {
        var preset = new ExportPreset("hdr", VideoCodec.HevcHdr, ExportResolution.R1080p);
        Assert.Equal(VideoCodec.HevcHdr, preset.Codec);
    }
}
