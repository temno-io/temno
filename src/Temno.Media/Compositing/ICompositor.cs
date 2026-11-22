namespace Temno.Media.Compositing;

/// <summary>
/// Composites a stack of video/image layers into a single frame at timeline
/// resolution. Seam for a future DirectX/GPU implementation;
/// <see cref="CpuCompositor"/> is the cross-platform reference path.
/// Mac: Preview/FrameRenderer.swift.
/// </summary>
public interface ICompositor
{
    /// <summary>
    /// Alpha-blends <paramref name="layers"/> bottom-up over an opaque black
    /// canvas (the Mac base canvas) and returns a straight-alpha frame of
    /// <paramref name="outputWidth"/> x <paramref name="outputHeight"/>.
    /// Callers are responsible for excluding hidden tracks and sampling
    /// per-frame opacity/transform values before building layers.
    /// </summary>
    VideoFrame Render(IReadOnlyList<CompositorLayer> layers, int outputWidth, int outputHeight);
}
