using Temno.Media.Hardware;

namespace Temno.Media.Compositing;

/// <summary>
/// The one place a compositor is chosen (WP-215). Preview and export used to be byte-identical
/// because both wrote <c>new CpuCompositor()</c> by hand; with a second implementation that
/// construction no longer holds, so it moves up one level: every production render path resolves
/// through here. See <c>docs/verification/wp-215/DECISION.md</c> rule R1.
/// <para>
/// <b>This deliberately takes no <see cref="HardwareAccelerationMode"/>.</b> That enum is the
/// <i>encoder</i> preference, and preview and export do not agree on it: preview is built with the
/// default <c>PreferHardware</c> (<c>PreviewHostControl</c> passes no mode) while export passes the
/// user's saved "Prefer hardware encoding" setting. Keying the compositor on it would mean that
/// turning off hardware <i>encoding</i> silently put preview and export on <i>different
/// compositors</i> — precisely the divergence R1 exists to prevent. Compositing is not encoding, so
/// it gets one process-wide answer that every call site receives.
/// </para>
/// </summary>
public static class CompositorFactory
{
    /// <summary>Overrides the choice without a rebuild: <c>gpu</c> or <c>cpu</c>.</summary>
    public const string OverrideVariable = "TEMNO_COMPOSITOR";

    /// <summary>
    /// Whether the GPU compositor is taken when D3D11 is present and no override says otherwise.
    /// <para>
    /// Set by the threshold fixed in <c>DECISION.md</c> before the shader was written: the GPU path
    /// defaults on only if it beats the post-WP-214 CPU path by at least 1.5x on the 4K to 1080p
    /// fixture, measured same-box. Measured 1.9x-2.6x there and 6.5x on the 4K 1:1 case WP-214 could
    /// not improve, so it is on. The numbers, and the end-to-end share they are a fraction of, are
    /// in <c>docs/verification/wp-215/EVIDENCE.md</c>.
    /// </para>
    /// </summary>
    public const bool GpuIsDefault = true;

    /// <summary>
    /// The compositor every render path in the product uses. <see cref="GpuCompositor"/> when it is
    /// enabled and the capability probe finds a device, otherwise the reference path — and
    /// <see cref="GpuCompositor"/> itself still falls back per frame if the device fails later.
    /// </summary>
    public static ICompositor Create()
    {
        if (!UseGpu())
            return new ReportedReferenceCompositor(DescribeSelection());

        var caps = HardwareCapabilities.Probe();
        if (!caps.D3D11Available)
            return new ReportedReferenceCompositor(DescribeSelection());

        return new GpuCompositor(new CpuCompositor(), caps);
    }

    /// <summary>
    /// What <see cref="Create"/> would pick and why, for the Settings row — answerable before a
    /// frame has been rendered, which <see cref="CompositorDiagnostics.Summary"/> is not.
    /// </summary>
    public static string DescribeSelection()
    {
        var forced = Environment.GetEnvironmentVariable(OverrideVariable);
        if (string.Equals(forced, "cpu", StringComparison.OrdinalIgnoreCase))
            return $"CPU reference — forced by {OverrideVariable}=cpu";

        if (!UseGpu())
            return $"CPU reference — GPU compositing is off in this build; set {OverrideVariable}=gpu to use it";

        var caps = HardwareCapabilities.Probe();
        if (!caps.D3D11Available)
            return "CPU reference — no D3D11 device" +
                   (caps.D3D11Error is { Length: > 0 } e ? $" ({e})" : "");

        return "GPU (D3D11)" +
               (caps.D3D11AdapterDescription is { Length: > 0 } a ? $" — {a}" : "");
    }

    private static bool UseGpu()
    {
        var forced = Environment.GetEnvironmentVariable(OverrideVariable);
        if (string.Equals(forced, "cpu", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(forced, "gpu", StringComparison.OrdinalIgnoreCase)) return true;
        return GpuIsDefault;
    }

    /// <summary>
    /// <see cref="CpuCompositor"/> with the one thing it cannot report about itself: that it is what
    /// ran. <see cref="GpuCompositor"/> records both of its outcomes, so without this the CPU path
    /// would be the only one invisible to Scope 4 — Settings would read "No frame composited yet"
    /// through an entire export, and the log would hold nothing. It is a decorator rather than a
    /// call inside <see cref="CpuCompositor"/> because that class is WP-214's, and because the
    /// reference implementation used by the parity tests must stay free of process-global state.
    /// </summary>
    private sealed class ReportedReferenceCompositor(string reason) : ICompositor
    {
        private readonly CpuCompositor _inner = new();

        public VideoFrame Render(IReadOnlyList<CompositorLayer> layers, int outputWidth, int outputHeight)
        {
            var frame = _inner.Render(layers, outputWidth, outputHeight);
            CompositorDiagnostics.RecordFrame(CompositorPath.Cpu, reason);
            return frame;
        }
    }
}
