using Temno.Core.Models;

namespace Temno.Media.Playback;

/// <summary>A decoded video layer for a timeline frame, paired with its resolved placement info.</summary>
public sealed record PreviewLayerFrame(ResolvedFrameClip Layer, VideoFrame Frame);

/// <summary>
/// Produces decoded <see cref="VideoFrame"/>s for a timeline frame by combining
/// <see cref="TimelineFrameResolver"/> with an <see cref="IFrameSource"/>.
/// Timeline positions are frames; decoding seeks by source seconds.
/// </summary>
public interface IPreviewFrameProvider
{
    /// <summary>
    /// Decodes the topmost visible decodable layer at the frame, or null when no video/image layer is
    /// visible. Layers whose media file is missing are skipped in favor of the layer beneath
    /// (Mac parity: offline clips drop out of the composition).
    /// </summary>
    Task<VideoFrame?> GetTopMostFrameAsync(Timeline timeline, int frame, CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders every visible decodable layer through the shared timeline renderer. The returned
    /// frame includes the v1 effect stack, transforms, opacity, and compositing used by export.
    /// </summary>
    Task<VideoFrame?> GetCompositedFrameAsync(Timeline timeline, int frame, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decodes every visible decodable layer at the frame, ordered bottom→top (ready for compositing).
    /// Offline layers are skipped.
    /// </summary>
    Task<IReadOnlyList<PreviewLayerFrame>> GetLayerFramesAsync(Timeline timeline, int frame, CancellationToken cancellationToken = default);
}
