using Temno.Media.Hardware;

namespace Temno.Media.Effects;

/// <summary>Builds the active effects renderer for preview/export (WP-114).</summary>
public static class EffectsRendererFactory
{
    /// <summary>
    /// Default: PreferHardware when D3D11 is present, otherwise CPU oracle.
    /// </summary>
    public static IFrameEffectsRenderer Create(HardwareAccelerationMode mode = HardwareAccelerationMode.PreferHardware)
    {
        if (mode == HardwareAccelerationMode.Software)
            return new CpuEffectsRenderer();

        var caps = HardwareCapabilities.Probe();
        if (!caps.CanGpuColorEffects)
            return new CpuEffectsRenderer();

        return new GpuEffectsRenderer(new CpuEffectsRenderer(), caps);
    }
}
