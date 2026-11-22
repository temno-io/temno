using Temno.Core.Models;

namespace Temno.Media.Compositing;

/// <summary>
/// One video/image layer to composite. Values are already sampled for the frame
/// being rendered: <see cref="Opacity"/> from <c>Clip.OpacityAt</c> (keyframes x
/// fades) and <see cref="Transform"/> from <c>Clip.TransformAt</c>.
/// Mac: Compositing/CompositorInstruction.swift LayerPlan.
/// </summary>
public sealed class CompositorLayer
{
    public CompositorLayer(
        VideoFrame frame,
        int trackIndex = 0,
        double opacity = 1.0,
        Transform? transform = null,
        string? blendMode = null,
        bool placementIsFinal = false)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
        TrackIndex = trackIndex;
        Opacity = opacity;
        Transform = transform ?? new Transform();
        BlendMode = blendMode;
        PlacementIsFinal = placementIsFinal;
    }

    /// <summary>Straight-alpha source frame ("bgra" canonical, "rgba" accepted).</summary>
    public VideoFrame Frame { get; }

    /// <summary>
    /// Mirrors Timeline.Tracks order: index 0 is the TOPMOST track (Mac:
    /// CompositionBuilder iterates tracks reversed to paint bottom-up). Layers
    /// sharing a track keep list order, later entries painting above — matches
    /// Mac transition echoes appended after the track's own clips.
    /// </summary>
    public int TrackIndex { get; }

    /// <summary>Sampled opacity 0..1; clamped at render time, ≤0 skips the layer.</summary>
    public double Opacity { get; }

    /// <summary>Normalized canvas-unit placement (center/size/rotation/flips).</summary>
    public Transform Transform { get; }

    /// <summary>
    /// Persisted Mac blend mode. Null and unknown values are treated as normal source-over by the
    /// CPU reference path so forward-compatible packages remain renderable.
    /// </summary>
    public string? BlendMode { get; }

    /// <summary>
    /// WP-405: <see cref="Transform"/> is already where the picture lands (a cropped region placed by
    /// <c>ClipPlacement.VisibleRegion</c>), so the compositor draws it as given instead of fitting the
    /// picture into it again. A second fit could narrow a crop of a picture drawn within tolerance.
    /// </summary>
    public bool PlacementIsFinal { get; }
}
