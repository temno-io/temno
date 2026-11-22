using System.Diagnostics;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-214 Scope 5: a throughput budget the suite asserts, so the next change cannot quietly give
/// the fixed-point + parallel-scanline win back. Thresholds are wall-clock on a named fixture —
/// generous (order-of-magnitude headroom over the measured numbers in
/// <c>docs/verification/wp-214/EVIDENCE.md</c>), warmup + best-of-N, so they catch an algorithmic
/// regression (back to serial doubles) rather than a busy machine.
/// </summary>
/// <remarks>
/// WP-215: shares <c>CompositorTiming</c> with <c>GpuCompositorParityTests</c>. These budgets are
/// wall-clock, and that class composites hundreds of frames on both paths — run concurrently they
/// measure the machine, not the algorithm.
/// </remarks>
[Collection("CompositorTiming")]
public sealed class CompositorThroughputBudgetTests
{
    /// <summary>
    /// The WP-214 headline case: 4K source on a 1080p canvas. Measured best-of-5 on the author's
    /// box (AMD 7950X3D, Release, quiet): 57.8 ms before, 10.2 ms after — so 40 ms fails the old
    /// code and clears the new one with 4x headroom. Recalibrate with a note if slower hardware
    /// trips it; do not silently raise it.
    /// </summary>
    public const double FourKToHdBudgetMs = 40.0;

    /// <summary>
    /// 720p source upscaled to a 1080p canvas. Same box: 55.8 ms before, 11.2 ms after.
    /// </summary>
    public const double HdUpscaleBudgetMs = 45.0;

    /// <summary>
    /// Guards the <c>oneToOne</c> memcpy path WP-214 must not regress (baselines 3.9 ms at 720p,
    /// 73.3 ms at 4K). Generous: it only trips if someone routes 1:1 through the resampler.
    /// </summary>
    public const double OneToOne720pBudgetMs = 40.0;

    /// <summary>
    /// The baseline constants above were calibrated on an 8+ core desktop CPU (where CpuCompositor's
    /// parallel scanlines run 4K->1080p in ~10.2 ms). On lower-core hosts (&lt; 8 logical CPUs) or shared
    /// CI VMs (e.g. 4-vCPU runners with hypervisor jitter), scale the wall-clock ceiling while keeping
    /// the baseline strict on 8+ core local machines.
    /// </summary>
    private static double EffectiveBudgetMs(double baselineMs)
    {
        var cores = Math.Max(1, Environment.ProcessorCount);
        var coreScale = cores < 8 ? 8.0 / cores : 1.0;
        var isCi = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
        var scale = isCi ? Math.Max(coreScale, 2.5) : coreScale;
        return baselineMs * scale;
    }

    private static VideoFrame GradientFrame(int w, int h)
    {
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var i = (y * w + x) * 4;
            data[i] = (byte)((x * 251 + y * 17) & 0xFF);
            data[i + 1] = (byte)((x * 13 + y * 241) & 0xFF);
            data[i + 2] = (byte)((x * 97 + y * 61) & 0xFF);
            data[i + 3] = 255;
        }
        return new VideoFrame(w, h, data, "rgba");
    }

    private static double BestOfMs(VideoFrame src, int outW, int outH, int iterations = 5)
    {
        var compositor = new CpuCompositor();
        var layer = new CompositorLayer(src);
        using (compositor.Render([layer], outW, outH)) { } // warmup: JIT + threadpool
        var best = double.MaxValue;
        for (var i = 0; i < iterations; i++)
        {
            var watch = Stopwatch.StartNew();
            using var frame = compositor.Render([layer], outW, outH);
            watch.Stop();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    [Fact]
    public void FourKSource_OnHdCanvas_MeetsBudget()
    {
        using var src = GradientFrame(3840, 2160);
        var best = BestOfMs(src, 1920, 1080);
        var budget = EffectiveBudgetMs(FourKToHdBudgetMs);
        Assert.True(best < budget,
            $"4K->1080p composite took {best:F1} ms/frame (best of 5), budget {budget:F1} ms ({Environment.ProcessorCount} logical CPUs).");
    }

    [Fact]
    public void HdSource_UpscaledToFullHd_MeetsBudget()
    {
        using var src = GradientFrame(1280, 720);
        var best = BestOfMs(src, 1920, 1080);
        var budget = EffectiveBudgetMs(HdUpscaleBudgetMs);
        Assert.True(best < budget,
            $"720p->1080p composite took {best:F1} ms/frame (best of 5), budget {budget:F1} ms ({Environment.ProcessorCount} logical CPUs).");
    }

    [Fact]
    public void OneToOne720p_NotRegressed()
    {
        using var src = GradientFrame(1280, 720);
        var best = BestOfMs(src, 1280, 720);
        var budget = EffectiveBudgetMs(OneToOne720pBudgetMs);
        Assert.True(best < budget,
            $"720p 1:1 composite took {best:F1} ms/frame (best of 5), budget {budget:F1} ms ({Environment.ProcessorCount} logical CPUs).");
    }

    [Fact]
    public void ParallelRender_IsDeterministic()
    {
        // Rows render on worker threads; the bytes must not depend on scheduling.
        using var src = GradientFrame(640, 360);
        var compositor = new CpuCompositor();
        using var first = compositor.Render([new CompositorLayer(src)], 1280, 720);
        using var second = compositor.Render([new CompositorLayer(src)], 1280, 720);
        Assert.Equal(first.Data, second.Data);
    }
}
