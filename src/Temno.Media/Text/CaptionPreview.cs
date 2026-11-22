using Temno.Core.Models;
using Temno.Media.Playback;

namespace Temno.Media.Text;

/// <summary>
/// Builds a throwaway caption clip and renders it through the **real** timeline renderer, so a
/// preview is the render rather than an imitation of it. Port of Mac
/// <c>MediaPanel/CaptionsTab/CaptionPreviewRender.swift</c>.
/// <para>
/// This lives in Temno.Media, not Temno.App, so the clip construction and loop policy are unit
/// testable without WinUI. The App layer wraps <see cref="RenderAsync"/> in a <c>WriteableBitmap</c>.
/// </para>
/// </summary>
public static class CaptionPreview
{
    /// <summary>Mac CaptionTab.previewText — the string the placement preview shows.</summary>
    public const string SampleText = "Captions will look like this";

    /// <summary>Mac CaptionPresetCell.sample — short enough to read in a gallery tile.</summary>
    public const string GallerySample = "Aa Bb Cc";

    /// <summary>Mac CaptionPreviewRender.loopFrames.</summary>
    public const int EntranceLoopFrames = 28;

    /// <summary>Mac CaptionPreviewRender.loopFrames for per-word presets, which need longer to read.</summary>
    public const int PerWordLoopFrames = 54;

    /// <summary>How many frames one preview loop runs for before repeating.</summary>
    public static int LoopFrames(string? preset)
        => TextAnimation.ModeOf(preset) == TextAnimation.RenderMode.PerWord
            ? PerWordLoopFrames
            : EntranceLoopFrames;

    /// <summary>Mac CaptionPresetCell.previewClip transform — fills most of a gallery tile.</summary>
    public static Transform GalleryTransform() => new()
    {
        CenterX = 0.5,
        CenterY = 0.5,
        Width = 0.92,
        Height = 0.55,
    };

    /// <summary>
    /// A synthetic text clip carrying the caption's content, style, placement and animation. It is
    /// never added to a project — it exists only to be handed to the renderer.
    /// </summary>
    public static Clip BuildClip(
        string content,
        TextStyle style,
        Transform transform,
        TextAnimation? animation,
        IReadOnlyList<WordTiming>? wordTimings = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        ArgumentNullException.ThrowIfNull(transform);

        var duration = LoopFrames(animation?.Preset);
        var clip = new Clip
        {
            MediaRef = "",
            MediaType = ClipType.text,
            SourceClipType = ClipType.text,
            StartFrame = 0,
            DurationFrames = duration,
            Opacity = 1,
            TextContent = content ?? "",
            Transform = transform,
            TextStyle = System.Text.Json.JsonSerializer.SerializeToElement(style, JsonOptions.ForProject),
        };

        if (animation is { } anim && anim.Preset is not (null or "" or "none" or "off"))
        {
            clip.TextAnimation =
                System.Text.Json.JsonSerializer.SerializeToElement(anim, JsonOptions.ForProject);
            // Per-word presets need timings; without them TextAnimator falls back to an even split
            // across the loop, which is exactly what a preview wants.
            clip.WordTimings = wordTimings?.Select(w => w.Clone()).ToList();
        }

        return clip;
    }

    /// <summary>
    /// Renders one preview frame through <see cref="TimelineFrameRenderer"/> — the same renderer the
    /// preview transport and the exporter use, so a preview cannot drift from the final pixels.
    /// </summary>
    /// <param name="frame">Frame within the loop; callers should pass <c>n % LoopFrames(preset)</c>.</param>
    public static Task<VideoFrame?> RenderAsync(
        Clip clip,
        int frame,
        int pixelWidth,
        int pixelHeight,
        int fps = 30,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (pixelWidth <= 0) throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        if (pixelHeight <= 0) throw new ArgumentOutOfRangeException(nameof(pixelHeight));

        var timeline = new Timeline
        {
            Fps = Math.Max(1, fps),
            Width = pixelWidth,
            Height = pixelHeight,
            Tracks = [new Track { Type = ClipType.video, Clips = [clip] }],
        };
        return RenderOnceAsync(timeline, frame, pixelWidth, pixelHeight, cancellationToken);
    }

    /// <summary>
    /// One renderer per preview frame, released before the task completes. The renderer owns a
    /// compositor, and since WP-215 that compositor can own a D3D11 device; a caption gallery draws
    /// one of these per item.
    /// </summary>
    private static async Task<VideoFrame?> RenderOnceAsync(
        Timeline timeline, int frame, int pixelWidth, int pixelHeight, CancellationToken cancellationToken)
    {
        using var renderer = new TimelineFrameRenderer(TextOnlyFrameSource.Instance);
        return await renderer
            .RenderAsync(timeline, frame, pixelWidth, pixelHeight, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A preview timeline holds one text clip and nothing that decodes, so any request for media
    /// frames is a bug rather than a condition to handle.
    /// </summary>
    private sealed class TextOnlyFrameSource : IFrameSource
    {
        public static readonly TextOnlyFrameSource Instance = new();

        public Task<VideoFrame> GetFrameAsync(
            string path, double sourceSeconds, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(
                "Caption previews render text only; nothing should ask this source for media frames.");
    }
}
