using Temno.Core.Models;
using Temno.Media.Export;
using Xunit;

namespace Temno.Core.Tests;

public sealed class ExportEncodeArgumentsTests
{
    [Fact]
    public void EvenSize_FloorsOddDimensionsFor420()
    {
        Assert.Equal((640, 360), ExportEncodeArguments.EvenSize(641, 361));
        Assert.Equal((2, 2), ExportEncodeArguments.EvenSize(1, 1));
        Assert.Equal((1280, 720), ExportEncodeArguments.EvenSize(1280, 720));
    }

    [Fact]
    public void ResolveOutputSize_MatchTimeline_EvenRounds()
    {
        var timeline = new Timeline { Width = 1921, Height = 1081, Fps = 24 };
        var size = ExportEncodeArguments.ResolveOutputSize(timeline, new ExportPreset("n"));
        Assert.Equal((1920, 1080), size);
    }

    [Fact]
    public void Libx264_UsesCrfNotAbr_AndPlayerSafeFlags()
    {
        var args = ExportEncodeArguments.BuildVideoPassArguments(
            "libx264", 1920, 1080, 30, VideoCodec.H264,
            bitrateMbps: 12.44, crf: 23, explicitBitrate: false,
            writeFaststart: true, outputPath: @"C:\out.mp4");

        Assert.Contains("-crf 23", args);
        Assert.DoesNotContain("-b:v", args);
        Assert.Contains("-maxrate 12.44M", args);
        Assert.Contains("-pix_fmt yuv420p", args);
        Assert.Contains("-profile:v high", args);
        Assert.Contains("-tag:v avc1", args);
        Assert.Contains("-g 60", args);
        Assert.Contains("-color_primaries bt709", args);
        Assert.Contains("-movflags +faststart", args);
        Assert.DoesNotContain("-level:v", args);
        Assert.DoesNotContain("-shortest", args);
    }

    [Fact]
    public void Libx264_ExplicitBitrate_DropsCrf()
    {
        var args = ExportEncodeArguments.BuildVideoPassArguments(
            "libx264", 1280, 720, 24, VideoCodec.H264,
            bitrateMbps: 8, crf: 18, explicitBitrate: true,
            writeFaststart: false, outputPath: "clip.mp4");

        Assert.Contains("-b:v 8M", args);
        Assert.DoesNotContain("-crf", args);
        Assert.DoesNotContain("-movflags +faststart", args);
    }

    [Fact]
    public void Hevc_TagsHvc1()
    {
        var args = ExportEncodeArguments.BuildVideoPassArguments(
            "libx265", 1920, 1080, 30, VideoCodec.H265,
            bitrateMbps: 8, crf: 23, explicitBitrate: false,
            writeFaststart: true, outputPath: "out.mp4");
        Assert.Contains("-tag:v hvc1", args);
        Assert.DoesNotContain("-tag:v avc1", args);
    }

    [Fact]
    public void Nvenc_ConvertsToNv12_UsesNamedPreset()
    {
        var args = ExportEncodeArguments.BuildVideoPassArguments(
            "h264_nvenc", 1920, 1080, 30, VideoCodec.H264,
            bitrateMbps: 12, crf: 23, explicitBitrate: false,
            writeFaststart: true, outputPath: "out.mp4");

        Assert.Contains("-pix_fmt nv12", args);
        Assert.Contains("-preset medium", args);
        Assert.DoesNotContain("-preset p4", args);
        Assert.Contains("-rc vbr", args);
        Assert.DoesNotContain("-crf", args);
        Assert.Contains("-tag:v avc1", args);
    }

    [Fact]
    public void ProRes_Uses422HqProfile()
    {
        var args = ExportEncodeArguments.BuildVideoPassArguments(
            "prores_ks", 1920, 1080, 24, VideoCodec.ProRes,
            bitrateMbps: 80, crf: 23, explicitBitrate: false,
            writeFaststart: true, outputPath: "out.mov");
        Assert.Contains("-pix_fmt yuv422p10le", args);
        Assert.Contains("-profile:v 3", args);
        Assert.DoesNotContain("-tag:v", args);
        Assert.DoesNotContain(" -g ", args);
    }

    [Fact]
    public void UnpaddedExport_DoesNotInsertAPadFilter()
    {
        var args = ExportEncodeArguments.BuildVideoPassArguments(
            "libx264", 1920, 1080, 30, VideoCodec.H264,
            bitrateMbps: 8, crf: 23, explicitBitrate: false,
            writeFaststart: true, outputPath: "out.mp4");
        Assert.Contains("-s 1920x1080", args);
        Assert.DoesNotContain("pad=", args);
    }

    [Fact]
    public void PaddedExport_PadsInRgbBeforeTheColourConversion()
    {
        var args = ExportEncodeArguments.BuildVideoPassArguments(
            "libx264", 606, 1080, 30, VideoCodec.H264,
            bitrateMbps: 8, crf: 23, explicitBitrate: false,
            writeFaststart: true, outputPath: "out.mp4",
            codedWidth: 1920, codedHeight: 1080);

        Assert.Contains("-s 606x1080", args);
        Assert.Contains("pad=1920:1080:657:0:color=black", args);
        var source = args.IndexOf("iec61966-2-1", StringComparison.Ordinal);
        var pad = args.IndexOf("pad=", StringComparison.Ordinal);
        var matrix = args.IndexOf("scale=out_color_matrix", StringComparison.Ordinal);
        Assert.True(source >= 0 && source < pad && pad < matrix);
    }

    [Fact]
    public void AudioMux_FaststartWithoutShortest()
    {
        var args = ExportEncodeArguments.BuildAudioMuxArguments(@"C:\v.mp4", @"C:\final.mp4");
        Assert.Contains("-c:a aac", args);
        Assert.Contains("-movflags +faststart", args);
        Assert.Contains("-f mp4", args);
        Assert.DoesNotContain("-shortest", args);
    }

    [Fact]
    public void HdrNvenc_UsesP010AndBt2020()
    {
        var args = ExportEncodeArguments.BuildVideoPassArguments(
            "hevc_nvenc", 3840, 2160, 30, VideoCodec.HevcHdr,
            bitrateMbps: 50, crf: 18, explicitBitrate: false,
            writeFaststart: true, outputPath: "hdr.mov");
        Assert.Contains("-pix_fmt p010le", args);
        Assert.Contains("-color_primaries bt2020", args);
        Assert.Contains("-tag:v hvc1", args);
        Assert.DoesNotContain("x265-params", args);
    }
}
