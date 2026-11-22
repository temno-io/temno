using System.Text.RegularExpressions;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Hardware;
using Temno.Media.Playback;
using Xunit;
using Xunit.Abstractions;

namespace Temno.Core.Tests;

/// <summary>
/// WP-215. <see cref="CpuCompositor"/> is the oracle; these assert what
/// <c>docs/verification/wp-215/DECISION.md</c> put in place of byte-identity.
/// <para>
/// R1 (one selection seam) and R3 (geometry is never inside the tolerance) are asserted exactly.
/// Only R2's colour arithmetic carries a tolerance, and it is stated per case rather than as one
/// blanket number — a test that accepted ±2 everywhere would pass with red and blue swapped.
/// </para>
/// <para>
/// xUnit 2 cannot skip at runtime, so a machine with no D3D11 makes the GPU-bound facts return
/// early with their reason printed. <see cref="GpuParity_Ran_WheneverADeviceWasPresent"/> is the
/// guard that stops that from becoming a silent pass: if the capability probe finds a device, the
/// compositor is required to have used it.
/// </para>
/// </summary>
[Collection("CompositorTiming")]
public sealed partial class GpuCompositorParityTests
{
    private readonly ITestOutputHelper _output;

    public GpuCompositorParityTests(ITestOutputHelper output) => _output = output;

    // ---------------------------------------------------------------------------------------
    // Gate
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void GpuParity_Ran_WheneverADeviceWasPresent()
    {
        var caps = HardwareCapabilities.Probe();
        using var gpu = new GpuCompositor(new CpuCompositor(), caps);

        _output.WriteLine($"d3d11={caps.D3D11Available}; adapter={caps.D3D11AdapterDescription}; " +
                          $"ready={gpu.IsGpuReady}; error={gpu.LastGpuError}");

        if (!caps.D3D11Available)
        {
            // Nothing to prove about the GPU path here, and nothing is claimed about it.
            Assert.False(gpu.IsGpuReady);
            return;
        }

        // Forcing the CPU path is a deliberate configuration, not a failure.
        if (string.Equals(
                Environment.GetEnvironmentVariable(CompositorFactory.OverrideVariable),
                "cpu",
                StringComparison.OrdinalIgnoreCase))
            return;

        Assert.True(
            gpu.IsGpuReady,
            $"A D3D11 device is present ({caps.D3D11AdapterDescription}) but GpuCompositor could not " +
            $"use it, so every parity fact below no-oped: {gpu.LastGpuError}");
    }

    // ---------------------------------------------------------------------------------------
    // R2 — the exact rows
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void OneToOne_IsBitIdentical()
    {
        if (!TryGpu(out var gpu)) return;
        using var source = NoisyFrame(200, 120, seed: 11, alpha: 255);
        AssertDelta(gpu, 0, [new CompositorLayer(source)], 200, 120);
    }

    [Fact]
    public void OneToOne_PartiallyOffCanvas_IsBitIdentical()
    {
        // Exercises the clipped bounding box and the host-side in-bounds test (R3), not just the
        // happy path where the layer covers the canvas.
        if (!TryGpu(out var gpu)) return;
        using var source = NoisyFrame(120, 80, seed: 12, alpha: 255);
        var transform = new Transform { CenterX = 0.1, CenterY = 0.9, Width = 120 / 200.0, Height = 80 / 120.0 };
        AssertDelta(gpu, 0, [new CompositorLayer(source, transform: transform)], 200, 120);
    }

    [Fact]
    public void AxisAlignedDownscale_OpaqueNormal_IsBitIdentical()
    {
        if (!TryGpu(out var gpu)) return;
        using var source = NoisyFrame(512, 512, seed: 21, alpha: 255);
        AssertDelta(gpu, 0, [new CompositorLayer(source)], 256, 256);
    }

    [Fact]
    public void AxisAlignedDownscale_NonPowerOfTwo_OpaqueNormal_IsBitIdentical()
    {
        // The headline export shape: an exact 2x ratio on non-power-of-two geometry, which is where
        // WP-214's own decision found its worst resample case.
        if (!TryGpu(out var gpu)) return;
        using var source = NoisyFrame(960, 540, seed: 22, alpha: 255);
        AssertDelta(gpu, 0, [new CompositorLayer(source)], 480, 270);
    }

    [Fact]
    public void AxisAlignedUpscale_NonIntegerRatio_OpaqueNormal_IsBitIdentical()
    {
        if (!TryGpu(out var gpu)) return;
        using var source = NoisyFrame(97, 61, seed: 23, alpha: 255);
        AssertDelta(gpu, 0, [new CompositorLayer(source)], 240, 160);
    }

    [Fact]
    public void Flips_OnOpaqueNormal_AreBitIdentical()
    {
        if (!TryGpu(out var gpu)) return;
        using var source = NoisyFrame(128, 64, seed: 24, alpha: 255);
        var transform = new Transform { FlipHorizontal = true, FlipVertical = true };
        AssertDelta(gpu, 0, [new CompositorLayer(source, transform: transform)], 128, 64);
    }

    [Fact]
    public void StackedOpaqueLayers_AreBitIdentical()
    {
        if (!TryGpu(out var gpu)) return;
        using var bottom = NoisyFrame(160, 90, seed: 31, alpha: 255);
        using var top = NoisyFrame(80, 45, seed: 32, alpha: 255);
        var transform = new Transform { CenterX = 0.4, CenterY = 0.6, Width = 0.5, Height = 0.5 };
        AssertDelta(
            gpu, 0,
            [new CompositorLayer(bottom, trackIndex: 1), new CompositorLayer(top, trackIndex: 0, transform: transform)],
            160, 90);
    }

    /// <summary>
    /// Each layer is its own <c>Dispatch</c> against the same canvas UAV, so a layer with a
    /// non-Normal blend mode must read what the dispatch before it wrote. D3D11 tracks that hazard
    /// for you — unlike D3D12, where the barrier is the caller's — and this is the fact that would
    /// break first if the composite were ever ported to an explicit-barrier API or a deferred
    /// context. Without the ordering the third layer would multiply against the cleared black
    /// canvas, so the assertion is not only the delta: the result must not be black.
    /// </summary>
    [Fact]
    public void BlendedLayer_ReadsWhatThePreviousDispatchWrote()
    {
        if (!TryGpu(out var gpu)) return;
        using var bottom = SolidFrame(48, 48, 200, 180, 160);
        using var middle = SolidFrame(48, 48, 120, 140, 200);
        using var top = SolidFrame(48, 48, 128, 128, 128);
        CompositorLayer[] Layers() =>
        [
            new(bottom, trackIndex: 2),
            new(middle, trackIndex: 1, transform: new Transform { CenterX = 0.4, Width = 0.6, Height = 0.6 }),
            new(top, trackIndex: 0, blendMode: "multiply"),
        ];

        using var actual = gpu.Render(Layers(), 48, 48);
        AssertDelta(gpu, 1, Layers(), 48, 48, label: "stacked multiply");

        // multiply against the accumulated background, not against the cleared canvas.
        Assert.True(actual.Data[0] > 0, "top layer multiplied against black — the dispatch ordering did not hold");
    }

    // ---------------------------------------------------------------------------------------
    // R2 — the toleranced rows
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AxisAlignedResample_WithAlpha_IsWithinOne()
    {
        if (!TryGpu(out var gpu)) return;
        using var source = NoisyFrame(300, 200, seed: 41, alpha: 137);
        AssertDelta(gpu, 1, [new CompositorLayer(source, opacity: 0.63)], 150, 100);
    }

    [Fact]
    public void Rotation_IsWithinOne()
    {
        if (!TryGpu(out var gpu)) return;
        using var source = NoisyFrame(120, 120, seed: 51, alpha: 255);
        var transform = new Transform { Rotation = 37.5, Width = 0.6, Height = 0.6 };
        AssertDelta(gpu, 1, [new CompositorLayer(source, transform: transform)], 200, 200);
    }

    [Fact]
    public void Rotation_WithFlipsAndAlpha_IsWithinOne()
    {
        if (!TryGpu(out var gpu)) return;
        using var source = NoisyFrame(90, 140, seed: 52, alpha: 190);
        var transform = new Transform
        {
            Rotation = -18.25,
            Width = 0.7,
            Height = 0.55,
            CenterX = 0.45,
            CenterY = 0.52,
            FlipHorizontal = true,
            FlipVertical = true,
        };
        AssertDelta(gpu, 1, [new CompositorLayer(source, opacity: 0.8, transform: transform)], 220, 180);
    }

    [Theory]
    [MemberData(nameof(SeparableBlendModes))]
    public void SeparableBlendModes_AreWithinOne(string mode)
    {
        if (!TryGpu(out var gpu)) return;
        using var background = NoisyFrame(64, 64, seed: 61, alpha: 255);
        using var source = NoisyFrame(64, 64, seed: 62, alpha: 255);
        AssertDelta(
            gpu, 1,
            [new CompositorLayer(background, trackIndex: 1), new CompositorLayer(source, trackIndex: 0, blendMode: mode)],
            64, 64);
    }

    [Theory]
    [MemberData(nameof(NonSeparableBlendModes))]
    public void NonSeparableBlendModes_AreWithinTwo(string mode)
    {
        if (!TryGpu(out var gpu)) return;
        using var background = NoisyFrame(64, 64, seed: 63, alpha: 255);
        using var source = NoisyFrame(64, 64, seed: 64, alpha: 255);
        AssertDelta(
            gpu, 2,
            [new CompositorLayer(background, trackIndex: 1), new CompositorLayer(source, trackIndex: 0, blendMode: mode)],
            64, 64);
    }

    [Fact]
    public void NonSeparableBlendModes_OnAchromaticPixels_AreWithinTwo()
    {
        // SetSat's R == G == B branch is the one that used to throw on the CPU path, and it is where
        // ClipColor's divisions are closest to degenerate.
        if (!TryGpu(out var gpu)) return;
        using var background = GreyRamp(64, 64);
        using var source = GreyRamp(64, 64);
        foreach (var mode in new[] { "hue", "saturation", "color", "luminosity" })
        {
            AssertDelta(
                gpu, 2,
                [
                    new CompositorLayer(background, trackIndex: 1),
                    new CompositorLayer(source, trackIndex: 0, blendMode: mode),
                ],
                64, 64, label: mode);
        }
    }

    [Fact]
    public void MacBlendReferencePixels_AreWithinTolerance()
    {
        // The same 1x1 fixtures CompositorTests pins exactly on the CPU path, re-read on the GPU.
        if (!TryGpu(out var gpu)) return;
        var cpu = new CpuCompositor();
        var worst = 0;
        foreach (var mode in AllBlendModes)
        {
            using var background = SolidFrame(1, 1, 64, 128, 191);
            using var source = SolidFrame(1, 1, 204, 102, 51);
            CompositorLayer[] Layers() =>
            [
                new(background, trackIndex: 1),
                new(source, trackIndex: 0, blendMode: mode),
            ];

            using var expected = cpu.Render(Layers(), 1, 1);
            using var actual = gpu.Render(Layers(), 1, 1);
            var delta = MaxChannelDelta(expected, actual);
            worst = Math.Max(worst, delta);
            var tolerance = NonSeparable.Contains(mode) ? 2 : 1;
            Assert.True(
                delta <= tolerance,
                $"blend '{mode}': delta {delta} > {tolerance}; cpu=[{expected.Data[0]},{expected.Data[1]},{expected.Data[2]}] " +
                $"gpu=[{actual.Data[0]},{actual.Data[1]},{actual.Data[2]}]");
            Assert.Equal(255, actual.Data[3]);
        }

        _output.WriteLine($"Mac blend reference pixels: worst GPU-vs-CPU delta {worst}");
    }

    // ---------------------------------------------------------------------------------------
    // R3 — geometry is never inside the tolerance
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Coverage_IsExact_NotToleranced()
    {
        // A scaled-down layer leaves a black border. Compare which pixels are canvas and which are
        // layer, not their values: an off-by-one destination rect is a defect at magnitude 1.
        if (!TryGpu(out var gpu)) return;
        var cpu = new CpuCompositor();
        using var source = SolidFrame(40, 40, 200, 30, 90);
        var transform = new Transform { CenterX = 0.37, CenterY = 0.61, Width = 0.33, Height = 0.44 };

        using var expected = cpu.Render([new CompositorLayer(source, transform: transform)], 101, 73);
        using var actual = gpu.Render([new CompositorLayer(source, transform: transform)], 101, 73);

        for (var i = 0; i < expected.Data.Length; i += 4)
        {
            var cpuIsCanvas = expected.Data[i] == 0 && expected.Data[i + 1] == 0 && expected.Data[i + 2] == 0;
            var gpuIsCanvas = actual.Data[i] == 0 && actual.Data[i + 1] == 0 && actual.Data[i + 2] == 0;
            Assert.True(
                cpuIsCanvas == gpuIsCanvas,
                $"coverage differs at pixel {i / 4} ({(i / 4) % 101},{(i / 4) / 101}): cpu canvas={cpuIsCanvas}, gpu canvas={gpuIsCanvas}");
        }
    }

    [Fact]
    public void BgraSource_ChannelOrder_IsExact()
    {
        if (!TryGpu(out var gpu)) return;
        var data = new byte[4 * 4 * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = 10;       // B in a bgra frame
            data[i + 1] = 120;  // G
            data[i + 2] = 240;  // R
            data[i + 3] = 255;
        }

        using var source = new VideoFrame(4, 4, data, "bgra");
        using var actual = gpu.Render([new CompositorLayer(source)], 4, 4);
        Assert.Equal(240, actual.Data[0]);
        Assert.Equal(120, actual.Data[1]);
        Assert.Equal(10, actual.Data[2]);
        AssertDelta(gpu, 0, [new CompositorLayer(source)], 4, 4);
    }

    [Fact]
    public void CanvasAlpha_IsAlwaysOpaque()
    {
        if (!TryGpu(out var gpu)) return;
        using var source = NoisyFrame(30, 20, seed: 71, alpha: 61);
        using var actual = gpu.Render([new CompositorLayer(source, opacity: 0.4)], 30, 20);
        for (var i = 3; i < actual.Data.Length; i += 4)
            Assert.Equal(255, actual.Data[i]);
    }

    [Fact]
    public void EmptyLayers_AndZeroOpacity_MatchTheReferenceExactly()
    {
        if (!TryGpu(out var gpu)) return;
        using var source = NoisyFrame(16, 16, seed: 72, alpha: 255);
        AssertDelta(gpu, 0, [], 16, 16, label: "empty");
        AssertDelta(gpu, 0, [new CompositorLayer(source, opacity: 0)], 16, 16, label: "zero opacity");
    }

    [Fact]
    public void UnknownBlendMode_FallsBackToNormal_LikeTheReference()
    {
        if (!TryGpu(out var gpu)) return;
        using var background = NoisyFrame(32, 32, seed: 73, alpha: 255);
        using var source = NoisyFrame(32, 32, seed: 74, alpha: 255);
        AssertDelta(
            gpu, 0,
            [
                new CompositorLayer(background, trackIndex: 1),
                new CompositorLayer(source, trackIndex: 0, blendMode: "notAMode"),
            ],
            32, 32);
    }

    [Fact]
    public void RepeatedRenders_AreByteEqual()
    {
        if (!TryGpu(out var gpu)) return;
        using var source = NoisyFrame(200, 140, seed: 81, alpha: 170);
        var transform = new Transform { Rotation = 12, Width = 0.8, Height = 0.8 };
        using var first = gpu.Render([new CompositorLayer(source, opacity: 0.7, transform: transform)], 128, 96);
        using var second = gpu.Render([new CompositorLayer(source, opacity: 0.7, transform: transform)], 128, 96);
        Assert.Equal(first.Data, second.Data);
    }

    /// <summary>
    /// The measured worst case across blend modes x alphas x geometries. This is what makes the
    /// numbers in DECISION.md's R2 table observations rather than hopes, and its printed maximum is
    /// what EVIDENCE.md quotes.
    /// </summary>
    [Fact]
    public void Sweep_RecordsTheWorstObservedDelta()
    {
        if (!TryGpu(out var gpu)) return;
        var cpu = new CpuCompositor();
        var worstSeparable = 0;
        var worstNonSeparable = 0;
        string worstCase = "none";

        foreach (var mode in AllBlendModes)
        {
            foreach (var opacity in new[] { 1.0, 0.75, 0.5, 0.27, 0.03 })
            {
                foreach (var (sw, sh, cw, ch) in new[] { (64, 64, 64, 64), (128, 96, 64, 48), (37, 53, 80, 70) })
                {
                    using var background = NoisyFrame(cw, ch, seed: 91, alpha: 255);
                    using var source = NoisyFrame(sw, sh, seed: 92, alpha: 211);
                    CompositorLayer[] Layers() =>
                    [
                        new(background, trackIndex: 1),
                        new(source, trackIndex: 0, opacity: opacity, blendMode: mode),
                    ];

                    using var expected = cpu.Render(Layers(), cw, ch);
                    using var actual = gpu.Render(Layers(), cw, ch);
                    var delta = MaxChannelDelta(expected, actual);

                    if (NonSeparable.Contains(mode))
                    {
                        if (delta > worstNonSeparable)
                        {
                            worstNonSeparable = delta;
                            worstCase = $"{mode} op={opacity} {sw}x{sh}->{cw}x{ch}";
                        }
                    }
                    else if (delta > worstSeparable)
                    {
                        worstSeparable = delta;
                        worstCase = $"{mode} op={opacity} {sw}x{sh}->{cw}x{ch}";
                    }
                }
            }
        }

        _output.WriteLine(
            $"sweep: worst separable delta {worstSeparable}, worst non-separable delta {worstNonSeparable} " +
            $"(worst case: {worstCase})");
        Assert.True(worstSeparable <= 1, $"separable blend delta {worstSeparable} > 1 ({worstCase})");
        Assert.True(worstNonSeparable <= 2, $"non-separable blend delta {worstNonSeparable} > 2 ({worstCase})");
    }

    // ---------------------------------------------------------------------------------------
    // Fallback and selection — Scope 3 and 4
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void WithoutD3D11_TheReferencePathRuns_AndSaysSo()
    {
        var noDevice = new HardwareCapabilities
        {
            IsWindows = true,
            D3D11Available = false,
            D3D11Error = "test: pretending there is no adapter",
        };
        using var gpu = new GpuCompositor(new CpuCompositor(), noDevice);
        Assert.False(gpu.IsGpuReady);

        using var source = NoisyFrame(24, 16, seed: 101, alpha: 255);
        using var expected = new CpuCompositor().Render([new CompositorLayer(source)], 24, 16);
        using var actual = gpu.Render([new CompositorLayer(source)], 24, 16);

        Assert.Equal(expected.Data, actual.Data);
        Assert.Equal(CompositorPath.Cpu, CompositorDiagnostics.LastPath);
        Assert.Contains("test: pretending there is no adapter", CompositorDiagnostics.LastDetail);
        Assert.DoesNotContain("No frame composited yet", CompositorDiagnostics.Summary);
    }

    /// <summary>
    /// R1's sharpest edge. Preview is built with the default <c>PreferHardware</c>
    /// (<c>PreviewHostControl</c> passes no mode) while export passes the user's saved "Prefer
    /// hardware encoding" setting. A compositor selected from that enum would therefore differ
    /// between preview and export for any user who turned hardware <i>encoding</i> off. The factory
    /// takes no mode, and this pins the shape so the parameter cannot come back.
    /// </summary>
    [Fact]
    public void Factory_TakesNoAccelerationMode_SoPreviewAndExportCannotDiverge()
    {
        var parameters = typeof(CompositorFactory)
            .GetMethod(nameof(CompositorFactory.Create))!
            .GetParameters();
        Assert.True(
            parameters.Length == 0,
            "CompositorFactory.Create must take no arguments: the only enum available to pass is the " +
            "encoder preference, which preview and export do not agree on (WP-215 R1). Parameters: " +
            string.Join(", ", parameters.Select(p => p.ParameterType.Name)));

        var first = CompositorFactory.Create();
        var second = CompositorFactory.Create();
        try
        {
            Assert.Equal(first.GetType(), second.GetType());
        }
        finally
        {
            (first as IDisposable)?.Dispose();
            (second as IDisposable)?.Dispose();
        }
    }

    [Fact]
    public void Factory_MatchesItsOwnDescription()
    {
        // The Settings row and the object that actually renders must not be able to disagree.
        var compositor = CompositorFactory.Create();
        var description = CompositorFactory.DescribeSelection();
        if (compositor is GpuCompositor)
            Assert.StartsWith("GPU", description);
        else
            Assert.StartsWith("CPU reference", description);
    }

    /// <summary>
    /// R1: preview and export stay on one compositor because every production render path resolves
    /// through <see cref="CompositorFactory"/>. That is a property of the code shape, so it is read
    /// from the source — a comment saying "use the factory" cannot fail a build.
    /// </summary>
    [Fact]
    public void ProductionCallSites_ResolveThroughTheFactory()
    {
        var offenders = new List<string>();
        foreach (var project in new[] { "Temno.Media", "Temno.App", "Temno.Mcp" })
        {
            foreach (var file in Directory.EnumerateFiles(LibraryPath(project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                    continue;

                var name = Path.GetFileName(file);
                // The factory and the GPU path legitimately construct the reference implementation:
                // one to return it, the other to hold it as its oracle and its fallback.
                if (name is "CompositorFactory.cs" or "GpuCompositor.cs" or "CpuCompositor.cs") continue;

                if (DirectConstruction().IsMatch(File.ReadAllText(file)))
                    offenders.Add(Path.Combine(project, Path.GetFileName(file)));
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These construct a compositor directly instead of resolving through CompositorFactory, " +
            "which is how preview and export drift onto different implementations (WP-215 R1): " +
            string.Join(", ", offenders));
    }

    /// <summary>
    /// T2 finding (Cursor Grok 4.6, round 1, P1): `ICompositor` is not `IDisposable`, and
    /// `TimelineFrameRenderer.Dispose` released the effects renderer and the text rasterizer —
    /// explicitly because they can own an `ID3D11Device` — but not the compositor. Once
    /// `CompositorFactory.Create()` started returning a `GpuCompositor`, that leaked one device per
    /// renderer: every export job, and every preview rebind.
    /// </summary>
    [Fact]
    public void RendererDispose_ReleasesTheCompositor()
    {
        var compositor = new DisposalProbe();
        var renderer = new TimelineFrameRenderer(NullFrameSource.Instance, compositor: compositor);
        Assert.False(compositor.Disposed);
        renderer.Dispose();
        Assert.True(
            compositor.Disposed,
            "TimelineFrameRenderer must release its compositor: GpuCompositor owns an ID3D11Device");
    }

    /// <summary>
    /// The preview provider is rebuilt on every project switch and every edit refresh, so it needs
    /// a seam to release the renderer it holds. It had none.
    /// </summary>
    [Fact]
    public void PreviewFrameProvider_IsDisposable()
    {
        Assert.True(
            typeof(IDisposable).IsAssignableFrom(typeof(PreviewFrameProvider)),
            "PreviewFrameProvider holds a TimelineFrameRenderer and is replaced on every rebind");
        using var provider = new PreviewFrameProvider(NullFrameSource.Instance);
    }

    /// <summary>
    /// The contract the GPU path must never break: it either returns the same picture the reference
    /// would, or it returns the reference's picture. After disposal the device is gone, so every
    /// later frame has to come from the reference — not from a stale buffer reported as a GPU frame.
    /// </summary>
    [Fact]
    public void AfterDispose_RenderFallsBackToTheReference_AndSaysSo()
    {
        if (!TryGpu(out var gpu)) return;
        using var source = NoisyFrame(64, 48, seed: 97, alpha: 255);
        CompositorLayer[] Layers() => [new CompositorLayer(source)];

        using (var warm = gpu.Render(Layers(), 64, 48))
            Assert.Equal(CompositorPath.Gpu, CompositorDiagnostics.LastPath);

        gpu.Dispose();

        var cpu = new CpuCompositor();
        using var expected = cpu.Render(Layers(), 64, 48);
        using var actual = gpu.Render(Layers(), 64, 48);
        Assert.Equal(0, MaxChannelDelta(expected, actual));
        Assert.Equal(CompositorPath.Cpu, CompositorDiagnostics.LastPath);
    }

    /// <summary>
    /// T2 finding (Cursor Grok 4.6, round 2, P1): the round-1 disposal fix had a hole. `EnsureDevice`
    /// guards on `_initAttempted`, not on `_disposed`, and `Dispose` is one-shot. A compositor that
    /// was disposed **before its first frame** — exactly what `Bind` does to an outgoing provider
    /// whose decode has not reached `Render` yet — would then build a fresh D3D11 device that nothing
    /// can ever release. The round-1 leak, re-entering through a race instead of a guaranteed drop.
    /// </summary>
    [Fact]
    public void DisposedBeforeFirstFrame_NeverBuildsADevice()
    {
        var gpu = new GpuCompositor();
        gpu.Dispose();   // never rendered: _initAttempted is still false

        using var source = NoisyFrame(32, 32, seed: 99, alpha: 255);
        CompositorLayer[] Layers() => [new CompositorLayer(source)];

        using var actual = gpu.Render(Layers(), 32, 32);

        Assert.False(
            gpu.IsGpuReady,
            "a compositor disposed before its first frame must not build a device afterwards: " +
            "Dispose has already run and will not run again, so that device would never be released");

        var cpu = new CpuCompositor();
        using var expected = cpu.Render(Layers(), 32, 32);
        Assert.Equal(0, MaxChannelDelta(expected, actual));
        Assert.Equal(CompositorPath.Cpu, CompositorDiagnostics.LastPath);
    }

    private sealed class DisposalProbe : ICompositor, IDisposable
    {
        private readonly CpuCompositor _inner = new();
        public bool Disposed { get; private set; }

        public VideoFrame Render(IReadOnlyList<CompositorLayer> layers, int outputWidth, int outputHeight)
            => _inner.Render(layers, outputWidth, outputHeight);

        public void Dispose() => Disposed = true;
    }

    private sealed class NullFrameSource : IFrameSource
    {
        public static readonly NullFrameSource Instance = new();

        public Task<VideoFrame> GetFrameAsync(
            string path, double sourceSeconds, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("This fixture never decodes.");
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static readonly HashSet<string> NonSeparable =
        new(StringComparer.Ordinal) { "hue", "saturation", "color", "luminosity" };

    private static readonly string[] AllBlendModes =
    [
        "normal", "darken", "multiply", "colorBurn", "lighten", "screen", "colorDodge", "overlay",
        "softLight", "hardLight", "difference", "exclusion", "hue", "saturation", "color",
        "luminosity", "add",
    ];

    public static TheoryData<string> SeparableBlendModes()
    {
        var data = new TheoryData<string>();
        foreach (var mode in AllBlendModes)
            if (!NonSeparable.Contains(mode))
                data.Add(mode);
        return data;
    }

    public static TheoryData<string> NonSeparableBlendModes()
    {
        var data = new TheoryData<string>();
        foreach (var mode in NonSeparable.OrderBy(m => m, StringComparer.Ordinal))
            data.Add(mode);
        return data;
    }

    private bool TryGpu(out GpuCompositor compositor)
    {
        compositor = new GpuCompositor();
        if (compositor.IsGpuReady) return true;
        _output.WriteLine($"no GPU composite path on this machine, fact not exercised: {compositor.LastGpuError}");
        compositor.Dispose();
        return false;
    }

    /// <summary>
    /// T2 finding (Cursor Grok 4.6, round 1, P1): `DECISION.md` bounded every `oneToOne` pixel at 0
    /// on the grounds that "the shader copies the same bytes", and `DESIGN.md` §6 carried that
    /// through to a claim that caption pixels are bit-identical. The shader copies bytes only on the
    /// opaque-Normal fast path; a caption's antialiased edge is `sa` in 1..254 and takes the `float`
    /// blend tail against the CPU's `double`. This measures the case the claim was made about — every
    /// alpha value 0..255, BGRA, canvas-size, identity transform, over opaque video — so the bound is
    /// a number rather than an argument.
    /// </summary>
    [Fact]
    public void OneToOne_WithPerPixelAlpha_IsTheCaptionCase_AndIsNotExact()
    {
        if (!TryGpu(out var gpu)) return;
        using var background = NoisyFrame(256, 64, seed: 91, alpha: 255);
        using var caption = AlphaRampFrame(256, 64, "bgra");

        CompositorLayer[] Layers() =>
        [
            new(background, trackIndex: 1),
            new(caption, trackIndex: 0),
        ];

        // The honest bound: this is the "with alpha" row, not the oneToOne row.
        AssertDelta(gpu, 1, Layers(), 256, 64, label: "oneToOne + per-pixel alpha (caption edge)");

        var cpu = new CpuCompositor();
        using var expected = cpu.Render(Layers(), 256, 64);
        using var actual = gpu.Render(Layers(), 256, 64);
        var observed = MaxChannelDelta(expected, actual);
        _output.WriteLine($"oneToOne + per-pixel alpha: observed max delta {observed}");

        // A fully opaque column and a fully transparent column are still exact: those are the two
        // ends the fast path and the no-op cover, and they are what keeps R3 out of the tolerance.
        AssertColumnExact(expected, actual, 256, 64, alphaColumnFor: 255);
        AssertColumnExact(expected, actual, 256, 64, alphaColumnFor: 0);
    }

    /// <summary>
    /// The other half of the split: `oneToOne` with a fully opaque source in Normal mode stays
    /// exact, so the 0 bound is still asserted where it is actually true.
    /// </summary>
    [Fact]
    public void OneToOne_OpaqueNormal_StaysExact()
    {
        if (!TryGpu(out var gpu)) return;
        using var background = NoisyFrame(256, 64, seed: 93, alpha: 255);
        using var over = NoisyFrame(256, 64, seed: 94, alpha: 255);
        AssertDelta(
            gpu,
            0,
            [new CompositorLayer(background, trackIndex: 1), new CompositorLayer(over, trackIndex: 0)],
            256,
            64,
            label: "oneToOne opaque Normal");
    }

    /// <summary>
    /// One column per alpha value 0..255, so a 256-wide frame covers the whole antialiasing range.
    /// Colour varies down each column so the blend is never a no-op.
    /// </summary>
    private static VideoFrame AlphaRampFrame(int width, int height, string pixelFormat)
    {
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                data[i] = (byte)((x * 7) + y);
                data[i + 1] = (byte)(255 - ((x * 3) + (y * 5)));
                data[i + 2] = (byte)((x + (y * 11)) & 0xFF);
                data[i + 3] = (byte)(x % 256);
            }
        }
        return new VideoFrame(width, height, data, pixelFormat);
    }

    private static void AssertColumnExact(VideoFrame expected, VideoFrame actual, int width, int height, int alphaColumnFor)
    {
        var x = alphaColumnFor % 256;
        for (var y = 0; y < height; y++)
        {
            var i = ((y * width) + x) * 4;
            for (var c = 0; c < 4; c++)
            {
                Assert.True(
                    expected.Data[i + c] == actual.Data[i + c],
                    $"alpha={alphaColumnFor} column must be exact: byte {i + c} cpu={expected.Data[i + c]} gpu={actual.Data[i + c]}");
            }
        }
    }

    private void AssertDelta(
        GpuCompositor gpu, int tolerance, CompositorLayer[] layers, int width, int height, string? label = null)
    {
        var cpu = new CpuCompositor();
        using var expected = cpu.Render(layers, width, height);
        using var actual = gpu.Render(layers, width, height);

        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        Assert.Equal(expected.PixelFormat, actual.PixelFormat);

        var (delta, index) = WorstChannel(expected, actual);
        Assert.True(
            delta <= tolerance,
            $"{label ?? "case"}: max channel delta {delta} > {tolerance} at byte {index} " +
            $"(pixel {index / 4} channel {index % 4}): cpu={expected.Data[index]}, gpu={actual.Data[index]}");
        if (delta > 0) _output.WriteLine($"{label ?? "case"}: max delta {delta} (tolerance {tolerance})");
    }

    private static int MaxChannelDelta(VideoFrame a, VideoFrame b) => WorstChannel(a, b).Delta;

    private static (int Delta, int Index) WorstChannel(VideoFrame a, VideoFrame b)
    {
        Assert.Equal(a.Data.Length, b.Data.Length);
        var worst = 0;
        var index = 0;
        for (var i = 0; i < a.Data.Length; i++)
        {
            var delta = Math.Abs(a.Data[i] - b.Data[i]);
            if (delta > worst) { worst = delta; index = i; }
        }
        return (worst, index);
    }

    private static VideoFrame SolidFrame(int width, int height, byte r, byte g, byte b, byte a = 255)
    {
        var data = new byte[width * height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = r; data[i + 1] = g; data[i + 2] = b; data[i + 3] = a;
        }
        return new VideoFrame(width, height, data, "rgba");
    }

    /// <summary>Deterministic pseudo-random pixels: uncorrelated channels are the worst case for a lerp.</summary>
    private static VideoFrame NoisyFrame(int width, int height, int seed, byte alpha)
    {
        var random = new Random(seed);
        var data = new byte[width * height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            data[i] = (byte)random.Next(256);
            data[i + 1] = (byte)random.Next(256);
            data[i + 2] = (byte)random.Next(256);
            data[i + 3] = alpha;
        }
        return new VideoFrame(width, height, data, "rgba");
    }

    /// <summary>R == G == B everywhere: SetSat's degenerate branch and ClipColor's smallest divisors.</summary>
    private static VideoFrame GreyRamp(int width, int height)
    {
        var data = new byte[width * height * 4];
        for (var i = 0; i < data.Length; i += 4)
        {
            var v = (byte)((i / 4) % 256);
            data[i] = v; data[i + 1] = v; data[i + 2] = v; data[i + 3] = 255;
        }
        return new VideoFrame(width, height, data, "rgba");
    }

    private static string LibraryPath(string project)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", project)))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", project);
    }

    // Optional namespace qualification: T2 round 1 noted the unqualified form would miss
    // `new Temno.Media.Compositing.CpuCompositor(`. No offender existed, but a guard that a
    // rename-to-fully-qualified walks straight past is not a guard.
    [GeneratedRegex(@"new\s+(?:[A-Za-z_][A-Za-z0-9_]*\s*\.\s*)*(?:Cpu|Gpu)Compositor\s*\(")]
    private static partial Regex DirectConstruction();
}
