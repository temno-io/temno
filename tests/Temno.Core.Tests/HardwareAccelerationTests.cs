using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Effects;
using Temno.Media.Export;
using Temno.Media.Hardware;
using Xunit;

namespace Temno.Core.Tests;

public sealed class HardwareAccelerationTests
{
    [Fact]
    public void HardwareCapabilities_Probe_DoesNotThrow()
    {
        HardwareCapabilities.ResetCache();
        var caps = HardwareCapabilities.Probe();
        Assert.NotNull(caps.Summary);
        Assert.Contains("d3d11=", caps.Summary);
        Assert.Contains("nvenc_h264=", caps.Summary);
    }

    [Fact]
    public void EncoderCandidates_HardwareFirst_OrdersNvencBeforeSoftware()
    {
        var h264 = FfmpegEncoderCapabilities.EncoderCandidatesHardwareFirst(VideoCodec.H264);
        Assert.Equal("h264_nvenc", h264[0]);
        Assert.Contains("libx264", h264);
        Assert.True(h264.ToList().IndexOf("h264_nvenc") < h264.ToList().IndexOf("libx264"));

        var soft = FfmpegEncoderCapabilities.EncoderCandidates(VideoCodec.H264);
        Assert.Equal("libx264", soft[0]);
    }

    [Fact]
    public void GpuEffectsRenderer_FallsBackForSpatialEffects()
    {
        Assert.False(GpuEffectsRenderer.CanAccelerate(
        [
            new Effect { Type = "blur.gaussian", Params = new() { ["radius"] = new EffectParam { Value = 2 } } },
        ]));
        Assert.True(GpuEffectsRenderer.CanAccelerate(
        [
            new Effect { Type = "color.exposure", Params = new() { ["ev"] = new EffectParam { Value = 0.5 } } },
            new Effect { Type = "color.contrast", Params = new() { ["amount"] = new EffectParam { Value = 1.1 } } },
        ]));
    }

    [Fact]
    public void GpuEffectsRenderer_MatchesCpuOracle_WithinOneLevel()
    {
        var data = new byte[16 * 16 * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = (byte)(i % 200);
            data[i + 1] = (byte)((i * 3) % 200);
            data[i + 2] = (byte)((i * 7) % 200);
            data[i + 3] = 200;
        }

        using var source = new VideoFrame(16, 16, data, "rgba");
        var effects = new List<Effect>
        {
            new()
            {
                Type = "color.exposure",
                Params = new() { ["ev"] = new EffectParam { Value = 0.5 } },
            },
            new()
            {
                Type = "color.saturation",
                Params = new() { ["amount"] = new EffectParam { Value = 1.2 } },
            },
            new()
            {
                Type = "color.contrast",
                Params = new() { ["amount"] = new EffectParam { Value = 1.1 } },
            },
        };

        var cpu = new CpuEffectsRenderer();
        using var gpu = new GpuEffectsRenderer(cpu, HardwareCapabilities.Probe());
        using var cpuOut = cpu.Apply(source, effects, 0);
        using var gpuOut = gpu.Apply(source, effects, 0);

        Assert.Equal(cpuOut.Data.Length, gpuOut.Data.Length);
        var maxDelta = 0;
        for (var i = 0; i < cpuOut.Data.Length; i++)
        {
            // Alpha channel must match exactly; RGB may differ by ±1 from float rounding on GPU.
            if (i % 4 == 3)
            {
                Assert.Equal(cpuOut.Data[i], gpuOut.Data[i]);
                continue;
            }
            maxDelta = Math.Max(maxDelta, Math.Abs(cpuOut.Data[i] - gpuOut.Data[i]));
        }

        // If GPU is not ready, GpuEffectsRenderer returns the oracle clone — delta is 0.
        // If GPU ran, allow ±1 for sRGB float path differences.
        Assert.True(maxDelta <= 1, $"max channel delta {maxDelta} (gpuReady={gpu.IsGpuReady}, err={gpu.LastGpuError})");
    }

    [Fact]
    public void EffectsRendererFactory_Software_ReturnsCpu()
    {
        var r = EffectsRendererFactory.Create(HardwareAccelerationMode.Software);
        Assert.IsType<CpuEffectsRenderer>(r);
    }

    [Fact]
    public void EffectsRendererFactory_PreferHardware_ReturnsGpuWhenD3DAvailable()
    {
        var caps = HardwareCapabilities.Probe();
        var r = EffectsRendererFactory.Create(HardwareAccelerationMode.PreferHardware);
        if (caps.CanGpuColorEffects)
            Assert.IsType<GpuEffectsRenderer>(r);
        else
            Assert.IsType<CpuEffectsRenderer>(r);
    }

    [Fact]
    public async Task HardwareBenchmark_SoftwareEncode_CompletesOrReportsError()
    {
        // Requires FFmpeg on PATH; still must not throw out of the helper.
        var sample = await HardwareBenchmark.MeasureEncodeAsync(
            HardwareAccelerationMode.Software,
            frames: 8,
            width: 64,
            height: 48);
        Assert.Equal(8, sample.Frames);
        if (sample.Error is null)
        {
            Assert.True(sample.VideoEncodeMilliseconds > 0);
            Assert.False(string.IsNullOrEmpty(sample.Encoder));
        }
        else
        {
            Assert.NotEmpty(sample.Error);
        }
    }

    [Fact]
    public void RequireEncoder_PreferHardware_DoesNotThrowForH264WhenAnyCandidatePresent()
    {
        try
        {
            var soft = FfmpegEncoderCapabilities.RequireEncoder(VideoCodec.H264, preferHardware: false);
            var hard = FfmpegEncoderCapabilities.RequireEncoder(VideoCodec.H264, preferHardware: true);
            Assert.False(string.IsNullOrEmpty(soft));
            Assert.False(string.IsNullOrEmpty(hard));
            // Hardware-first should pick nvenc when present.
            if (FfmpegEncoderCapabilities.IsEncoderAvailable("h264_nvenc"))
                Assert.Equal("h264_nvenc", hard);
            else
                Assert.Equal(soft, hard);
        }
        catch (InvalidOperationException)
        {
            // No ffmpeg encoders in environment — acceptable for headless without media tools.
        }
    }
}
