using Temno.Core.Models;
using Temno.Media.Export;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// Colour description must reach the bitstream.
/// <para>
/// Output-side <c>-color_primaries</c> / <c>-color_trc</c> are not enough: since FFmpeg 7 the
/// encoder takes its colour description from the AVFrame the filtergraph produces, and a rawvideo
/// frame carries none. Measured against ffmpeg 8.1, every encoder except libx265 wrote
/// <c>color_primaries=unknown</c> and <c>color_transfer=unknown</c>, so HDR exports were not
/// recognised as HDR by any player. The filter chain stamps the description onto the frames.
/// </para>
/// </summary>
public sealed class ExportColorTaggingTests
{
    private static string Args(string encoder, VideoCodec codec) =>
        ExportEncodeArguments.BuildVideoPassArguments(
            encoder, 1920, 1080, 30, codec,
            bitrateMbps: 12, crf: 23, explicitBitrate: false,
            writeFaststart: true, outputPath: codec == VideoCodec.ProRes ? "out.mov" : "out.mp4");

    [Theory]
    [InlineData("libx264", VideoCodec.H264)]
    [InlineData("h264_nvenc", VideoCodec.H264)]
    [InlineData("h264_amf", VideoCodec.H264)]
    [InlineData("libx265", VideoCodec.H265)]
    [InlineData("hevc_nvenc", VideoCodec.H265)]
    [InlineData("prores_ks", VideoCodec.ProRes)]
    public void SdrEncoders_StampBt709OntoTheFrames(string encoder, VideoCodec codec)
    {
        var args = Args(encoder, codec);

        Assert.Contains("-vf \"", args);
        Assert.Contains("setparams=color_primaries=bt709:color_trc=bt709:colorspace=bt709:range=tv", args);
        Assert.Contains("-color_range tv", args);
    }

    [Theory]
    [InlineData("hevc_nvenc")]
    [InlineData("libx265")]
    [InlineData("hevc_amf")]
    public void HdrEncoders_ConvertRatherThanRelabel(string encoder)
    {
        var args = ExportEncodeArguments.BuildVideoPassArguments(
            encoder, 3840, 2160, 30, VideoCodec.HevcHdr,
            bitrateMbps: 50, crf: 18, explicitBitrate: false,
            writeFaststart: true, outputPath: "hdr.mov");

        // The canvas is 8-bit BT.709 sRGB. Declaring it BT.2020 without converting shifts every hue
        // (a pure red reads as orange). zscale linearises, moves the primaries, then applies HLG.
        Assert.Contains("zscale=t=linear", args);
        Assert.Contains("zscale=p=bt2020", args);
        Assert.Contains("zscale=t=arib-std-b67:m=bt2020nc:r=tv", args);
        Assert.Contains("setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc:range=tv", args);
    }

    [Fact]
    public void SourceIsDeclaredFullRangeSrgb_BeforeAnyConversion()
    {
        // The compositor canvas is straight-alpha full-range sRGB RGB. Saying so is what lets
        // swscale/zscale pick the right input transfer instead of guessing.
        var args = Args("libx264", VideoCodec.H264);
        Assert.Contains("setparams=color_primaries=bt709:color_trc=iec61966-2-1:colorspace=gbr:range=full", args);
    }

    [Fact]
    public void EncodeArgs_SilenceBannerAndProgress_SoStderrIsDiagnostic()
    {
        var args = Args("libx264", VideoCodec.H264);
        Assert.Contains("-hide_banner", args);
        Assert.Contains("-nostats", args);
        Assert.Contains("-loglevel error", args);
    }

    [Fact]
    public void AudioMux_RestatesColourOnStreamCopy()
    {
        // -c:v copy carries the encoded stream but not its colour description on every muxer, so
        // the HDR tagging the video pass established must be restated on the mux.
        var sdr = ExportEncodeArguments.BuildAudioMuxArguments(@"C:\v.mp4", @"C:\final.mp4");
        Assert.Contains("-color_primaries bt709", sdr);
        Assert.Contains("-color_range tv", sdr);

        var hdr = ExportEncodeArguments.BuildAudioMuxArguments(@"C:\v.mov", @"C:\final.mov", hdr: true);
        Assert.Contains("-color_primaries bt2020", hdr);
        Assert.Contains("-color_trc arib-std-b67", hdr);
        Assert.Contains("-colorspace bt2020nc", hdr);
    }
}
