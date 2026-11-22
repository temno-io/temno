using System.Text.Json;
using Temno.Core.Captions;
using Temno.Core.Editing;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media.Compositing;
using Temno.Media.Effects;
using Temno.Media.Text;

namespace Temno.Media.Playback;

/// <summary>
/// Shared frame renderer for preview and export. Resolves timeline layers, applies source-pixel
/// effects, rasterizes text (WP-94), blends transitions at cuts, and composites at output size.
/// </summary>
public sealed class TimelineFrameRenderer : IDisposable
{
    public const int MaxSequenceNestingDepth = 8;

    /// <summary>
    /// A visual layer that could not be drawn. Export must be able to say so: a clip whose media
    /// has moved is dropped silently, and a timeline of nothing but offline clips previously
    /// encoded as pure black and reported success.
    /// </summary>
    public sealed record SkippedLayer(string ClipId, string MediaRef, string Reason);

    /// <summary>Raised when a layer is dropped from a frame. Fires once per occurrence.</summary>
    public event Action<SkippedLayer>? LayerSkipped;

    private void ReportSkipped(string clipId, string mediaRef, string reason)
        => LayerSkipped?.Invoke(new SkippedLayer(clipId, mediaRef, reason));

    private readonly IFrameSource _frameSource;
    private readonly MediaResolver? _mediaResolver;
    private readonly ICompositor _compositor;
    private readonly IFrameEffectsRenderer _effectsRenderer;
    private readonly ITextRasterizer _textRasterizer;
    private readonly Func<string, Timeline?>? _timelineLookup;
    private const int MaxCachedTextRasters = 16;
    private readonly object _textCacheGate = new();
    private readonly Dictionary<TextRasterKey, LinkedListNode<(TextRasterKey Key, CachedTextRaster Value)>> _textCache = new();
    private readonly LinkedList<(TextRasterKey Key, CachedTextRaster Value)> _textLru = new();

    /// <summary>
    /// <para><b>Ownership:</b> this renderer disposes the compositor, effects renderer and text
    /// rasterizer it ends up holding — injected or defaulted — because each of the three can own an
    /// <c>ID3D11Device</c> and there is no other seam that outlives them. So pass a compositor to
    /// <em>one</em> renderer only. Every production call site passes a fresh
    /// <c>CompositorFactory.Create()</c>; <see cref="Export.TimelineExporter"/> keeps a reference to
    /// the same instance but only uses it during the export, which completes before it disposes.</para>
    /// </summary>
    public TimelineFrameRenderer(
        IFrameSource frameSource,
        MediaResolver? mediaResolver = null,
        ICompositor? compositor = null,
        IFrameEffectsRenderer? effectsRenderer = null,
        ITextRasterizer? textRasterizer = null,
        Func<string, Timeline?>? timelineLookup = null)
    {
        ArgumentNullException.ThrowIfNull(frameSource);
        _frameSource = frameSource;
        _mediaResolver = mediaResolver;
        // WP-215 R1: one selection seam, so an omitted compositor cannot silently be a
        // different implementation from the one preview or export chose.
        _compositor = compositor ?? CompositorFactory.Create();
        _effectsRenderer = effectsRenderer ?? new CpuEffectsRenderer();
        _textRasterizer = textRasterizer ?? new CpuTextRasterizer();
        _timelineLookup = timelineLookup;
    }

    /// <summary>
    /// Returns null when no picture clip contains the frame. A clip that contains the frame
    /// but is fully transparent (a fade-in at its first frame) is an opaque black canvas —
    /// callers that treat null as "no media" would otherwise report an empty playhead.
    /// Callers own the returned frame.
    /// </summary>
    public Task<VideoFrame?> RenderAsync(
        Timeline timeline,
        int frame,
        int outputWidth,
        int outputHeight,
        CancellationToken cancellationToken = default)
        => RenderAsync(timeline, frame, outputWidth, outputHeight, depth: 0, cancellationToken);

    private async Task<VideoFrame?> RenderAsync(
        Timeline timeline,
        int frame,
        int outputWidth,
        int outputHeight,
        int depth,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (outputWidth <= 0) throw new ArgumentOutOfRangeException(nameof(outputWidth));
        if (outputHeight <= 0) throw new ArgumentOutOfRangeException(nameof(outputHeight));
        if (depth > MaxSequenceNestingDepth)
            return MakePlaceholderFrame(outputWidth, outputHeight, 40, 40, 40, 255);

        var layers = new List<CompositorLayer>();
        try
        {
            await BuildMediaLayersAsync(timeline, frame, outputWidth, outputHeight, layers, depth, cancellationToken)
                .ConfigureAwait(false);
            AppendTextLayers(timeline, frame, outputWidth, outputHeight, layers);

            if (layers.Count == 0)
            {
                if (PictureAtFrameIsFullyTransparent(timeline, frame))
                    return new VideoFrame(outputWidth, outputHeight, OpaqueBlack(outputWidth, outputHeight), "rgba");
                return null;
            }

            // One opaque layer filling the canvas at 1:1 composites to a byte-for-byte copy of
            // itself. Handing the decoded frame straight back skips a full-frame read-modify-write
            // (13.7 ms per 1080p frame) to produce bytes we already have.
            if (IsFullFrameOpaquePassthrough(layers, outputWidth, outputHeight))
            {
                var passthrough = layers[0].Frame;
                layers.Clear();   // ownership moves to the caller; do not dispose it below
                return passthrough;
            }

            return _compositor.Render(layers, outputWidth, outputHeight);
        }
        finally
        {
            foreach (var layer in layers)
                layer.Frame.Dispose();
        }
    }

    /// <summary>
    /// True when compositing <paramref name="layers"/> would reproduce the single layer's pixels
    /// exactly: one layer, no transform, fully opaque, normal blend, already at canvas size and in
    /// the canvas pixel format. Verified byte-for-byte against <see cref="CpuCompositor"/>.
    /// </summary>
    private static bool IsFullFrameOpaquePassthrough(
        List<CompositorLayer> layers, int outputWidth, int outputHeight)
    {
        if (layers.Count != 1) return false;

        var layer = layers[0];
        if (layer.Opacity < 1.0) return false;
        if (!CpuBlendModes.IsNormal(layer.BlendMode)) return false;

        var t = layer.Transform;
        if (t.FlipHorizontal || t.FlipVertical) return false;
        if (Math.Abs(t.Rotation) > 0.001) return false;
        if (Math.Abs(t.CenterX - 0.5) > 1e-9 || Math.Abs(t.CenterY - 0.5) > 1e-9) return false;
        if (Math.Abs(t.Width - 1.0) > 1e-9 || Math.Abs(t.Height - 1.0) > 1e-9) return false;

        var frame = layer.Frame;
        if (frame.Width != outputWidth || frame.Height != outputHeight) return false;
        // The compositor's canvas is RGBA; a BGRA source would need its channels swapped.
        if (!string.Equals(frame.PixelFormat, "rgba", StringComparison.OrdinalIgnoreCase)) return false;

        var required = checked(frame.Width * frame.Height * 4);
        if (frame.Data.Length < required) return false;

        // Any translucency and the layer would blend against the opaque black canvas instead.
        for (var i = 3; i < required; i += 4)
        {
            if (frame.Data[i] != 255) return false;
        }

        return true;
    }

    private async Task BuildMediaLayersAsync(
        Timeline timeline,
        int frame,
        int outputWidth,
        int outputHeight,
        List<CompositorLayer> layers,
        int depth,
        CancellationToken cancellationToken)
    {
        var activeTransitions = ActiveTransitions(timeline, frame);
        var skipClipIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rt in activeTransitions)
        {
            skipClipIds.Add(rt.ClipA.Id);
            skipClipIds.Add(rt.ClipB.Id);
        }

        var resolved = TimelineFrameResolver.Resolve(timeline, frame, _mediaResolver);

        // WP-163: layer decodes fan out. A composited frame decodes one path per visible layer;
        // awaiting them back-to-back serialized the layers (~17 ms each on the owner project).
        // Slots keep resolve order — the compositor blends the list bottom→top — while the
        // decodes overlap on their own per-path pipes. Sequence/lottie branches still render
        // inline in pass 1, overlapping the plain decodes they were started alongside.
        var slots = new CompositorLayer?[resolved.Count];
        var pending = new List<(int Slot, ResolvedFrameClip Layer, Task<VideoFrame> Decode)>();
        try
        {
            for (var i = 0; i < resolved.Count; i++)
            {
                var layer = resolved[i];
                if (skipClipIds.Contains(layer.ClipId)) continue;

                // WP-106: nested sequence — mediaRef is a timeline id.
                if (layer.MediaType == ClipType.sequence)
                {
                    slots[i] = await RenderSequenceLayerAsync(layer, frame, timeline, depth, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                // WP-106: Lottie — true runtime unsupported; accept pre-flattened still if path is image.
                if (layer.MediaType == ClipType.lottie)
                {
                    slots[i] = await RenderLottieLayerAsync(layer, outputWidth, outputHeight, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (layer.MediaPath is null)
                {
                    ReportSkipped(layer.ClipId, layer.MediaRef, "media could not be resolved to a file on disk");
                    continue;
                }

                // WP-252: started on the pool. A request the running decoder cannot serve (any step
                // backwards) disposes and restarts an ffmpeg pipe before the frame source first awaits,
                // and the preview calls this on the UI thread: 30 to 110 ms stalls per backward scrub step.
                var (path, seconds) = (layer.MediaPath, layer.SourceSeconds);
                pending.Add((i, layer, Task.Run(() => _frameSource.GetFrameAsync(path, seconds, cancellationToken), cancellationToken)));
            }

            // Pass 2, in resolve order: effects and skip reporting read exactly as before.
            foreach (var (slot, layer, decode) in pending)
            {
                VideoFrame decoded;
                try
                {
                    decoded = await decode.ConfigureAwait(false);
                }
                catch (FileNotFoundException)
                {
                    // Offline layers drop out of the frame, but the export must still be told.
                    ReportSkipped(layer.ClipId, layer.MediaRef, "media file is missing (offline)");
                    continue;
                }

                if (CropToVisible(decoded, layer.Crop, layer.Transform, outputWidth, outputHeight) is not { } visible)
                    continue;
                // Alpha is preserved through color ops; chroma key may reduce it (WP-103).
                var effected = _effectsRenderer.Apply(visible.Frame, layer.Effects, layer.ClipRelativeFrame);
                // An identity stack hands the decoded frame straight back; only release the
                // decode when the renderer produced a separate buffer.
                if (!ReferenceEquals(effected, visible.Frame))
                    visible.Frame.Dispose();
                slots[slot] = new CompositorLayer(
                    effected,
                    layer.TrackOrderIndex,
                    layer.Opacity,
                    visible.Placement,
                    layer.BlendMode,
                    visible.Final);
            }
        }
        catch
        {
            // Pass 1 threw (a nested render faulted): pass 2 never runs, so observe the
            // started decodes here rather than dropping them as unobserved task exceptions.
            foreach (var (_, _, decode) in pending)
            {
                try { (await decode.ConfigureAwait(false)).Dispose(); }
                catch { /* the original throw carries the frame's failure */ }
            }
            foreach (var slot in slots)
                slot?.Frame.Dispose();
            throw;
        }

        foreach (var slot in slots)
        {
            if (slot is not null) layers.Add(slot);
        }

        // Transition layers: blend A→B pre-composited to timeline size (straight alpha).
        foreach (var rt in activeTransitions.OrderBy(t => t.TrackIndex).Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progress = TransitionProgress(rt, frame);
            var outLayer = await DecodeClipAsLayerAsync(
                    timeline, rt.ClipA, frame, rt.TrackIndex, depth, cancellationToken)
                .ConfigureAwait(false);
            var inLayer = await DecodeClipAsLayerAsync(
                    timeline, rt.ClipB, frame, rt.TrackIndex, depth, cancellationToken)
                .ConfigureAwait(false);
            if (outLayer is null && inLayer is null) continue;

            // Flatten each side to full-frame layers for TransitionRenderer.
            var sideA = FlattenSide(outLayer, outputWidth, outputHeight);
            var sideB = FlattenSide(inLayer, outputWidth, outputHeight);
            if (sideA is null && sideB is null) continue;

            var aFrame = sideA ?? new VideoFrame(outputWidth, outputHeight, OpaqueBlack(outputWidth, outputHeight), "rgba");
            var bFrame = sideB ?? new VideoFrame(outputWidth, outputHeight, OpaqueBlack(outputWidth, outputHeight), "rgba");
            try
            {
                var blended = TransitionRenderer.Render(
                    new CompositorLayer(aFrame, rt.TrackIndex),
                    new CompositorLayer(bFrame, rt.TrackIndex),
                    rt.Transition.Type,
                    progress);
                layers.Add(new CompositorLayer(
                    blended,
                    rt.TrackIndex,
                    opacity: 1,
                    transform: new Transform { CenterX = 0.5, CenterY = 0.5, Width = 1, Height = 1 }));
            }
            finally
            {
                aFrame.Dispose();
                bFrame.Dispose();
            }
        }
    }

    private async Task<CompositorLayer?> RenderSequenceLayerAsync(
        ResolvedFrameClip layer, int frame, Timeline timeline, int depth, CancellationToken cancellationToken)
    {
        var nested = ResolveNestedTimeline(layer.MediaRef);
        if (nested is null)
        {
            ReportSkipped(layer.ClipId, layer.MediaRef, "nested sequence could not be resolved");
            return null;
        }
        var nestedFrame = NestedTimelineFrame(layer, frame, timeline.Fps, nested.Fps);
        var nestedW = Math.Max(1, nested.Width);
        var nestedH = Math.Max(1, nested.Height);
        var nestedRendered = await RenderAsync(
                nested, nestedFrame, nestedW, nestedH, depth + 1, cancellationToken)
            .ConfigureAwait(false);
        if (nestedRendered is null) return null;
        if (CropToVisible(nestedRendered, layer.Crop, layer.Transform, timeline.Width, timeline.Height) is not { } visible)
            return null;
        var nestedFx = _effectsRenderer.Apply(visible.Frame, layer.Effects, layer.ClipRelativeFrame);
        if (!ReferenceEquals(nestedFx, visible.Frame))
            visible.Frame.Dispose();
        return new CompositorLayer(
            nestedFx,
            layer.TrackOrderIndex,
            layer.Opacity,
            visible.Placement,
            layer.BlendMode,
            visible.Final);
    }

    private async Task<CompositorLayer?> RenderLottieLayerAsync(
        ResolvedFrameClip layer, int outputWidth, int outputHeight, CancellationToken cancellationToken)
    {
        var lottieFrame = await TryDecodeLottieOrPlaceholderAsync(
                layer, outputWidth, outputHeight, cancellationToken)
            .ConfigureAwait(false);
        if (lottieFrame is null) return null;
        if (CropToVisible(lottieFrame, layer.Crop, layer.Transform, outputWidth, outputHeight) is not { } visible)
            return null;
        var lottieFx = _effectsRenderer.Apply(visible.Frame, layer.Effects, layer.ClipRelativeFrame);
        if (!ReferenceEquals(lottieFx, visible.Frame))
            visible.Frame.Dispose();
        return new CompositorLayer(
            lottieFx,
            layer.TrackOrderIndex,
            layer.Opacity,
            visible.Placement,
            layer.BlendMode,
            visible.Final);
    }

    private void AppendTextLayers(
        Timeline timeline, int frame, int outputWidth, int outputHeight, List<CompositorLayer> layers)
    {
        for (var trackIndex = timeline.Tracks.Count - 1; trackIndex >= 0; trackIndex--)
        {
            var track = timeline.Tracks[trackIndex];
            if (track.Hidden) continue;

            foreach (var clip in track.Clips)
            {
                if (!clip.Contains(frame)) continue;
                if (clip.MediaType != ClipType.text && string.IsNullOrEmpty(clip.TextContent)) continue;
                var text = clip.TextContent ?? "";
                if (text.Length == 0) continue;

                var opacity = clip.OpacityAt(frame);
                if (opacity <= 0) continue;

                var rel = Math.Max(0, frame - clip.StartFrame);
                var anim = TextAnimator.ReadAnimation(clip);
                var style = CpuTextRasterizer.StyleFromClip(clip);
                var state = TextAnimator.Evaluate(
                    anim, text, rel, clip.DurationFrames, clip.WordTimings, style.Color);
                opacity *= state.Opacity;
                if (opacity <= 0) continue;
                var visible = state.VisibleText ?? text;
                if (visible.Length == 0) continue;
                // A per-word preset with every word still at zero opacity draws nothing. An empty run
                // list must not fall through to the plain whole-line draw.
                if (state.Words is { Count: 0 }) continue;

                var transform = clip.TransformAt(frame);
                var crop = clip.CropAt(frame);
                if (CropHidesPicture(crop)) continue;

                var boxWidthNorm = Math.Clamp(clip.Transform.Width, 0.05, 1);
                var boxHeightNorm = Math.Clamp(clip.Transform.Height, 0.05, 1);
                var wordRuns = WordRuns(state);
                var cached = GetOrRasterizeText(visible, style, outputWidth, outputHeight, boxWidthNorm, boxHeightNorm, wordRuns, crop);
                if (cached.Bgra is null)
                    continue;

                var keyScaleX = clip.ScaleTrack is { IsActive: true } && clip.Transform.Width > 1e-6
                    ? transform.Width / clip.Transform.Width
                    : 1.0;
                var keyScaleY = clip.ScaleTrack is { IsActive: true } && clip.Transform.Height > 1e-6
                    ? transform.Height / clip.Transform.Height
                    : 1.0;
                // Keep effective scale (FontScale * animation * keyframe) within [0.05, 4.0] rather
                // than clamping the ratio alone, so text with FontScale != 1 reaches the full range.
                var baseFontScale = Math.Max(0.05, style.FontScale > 0 ? style.FontScale : 1.0);
                var minRatio = 0.05 / baseFontScale;
                var maxRatio = 4.0 / baseFontScale;
                var scaleX = Math.Clamp(state.Scale * keyScaleX, minRatio, maxRatio);
                var scaleY = Math.Clamp(state.Scale * keyScaleY, minRatio, maxRatio);
                var frameBuf = new VideoFrame(cached.Width, cached.Height, cached.Bgra, "bgra");
                var placement = new Transform
                {
                    CenterX = transform.CenterX,
                    CenterY = transform.CenterY + state.OffsetY,
                    Width = scaleX * cached.WidthFraction,
                    Height = scaleY * cached.HeightFraction,
                    Rotation = transform.Rotation,
                    FlipHorizontal = transform.FlipHorizontal,
                    FlipVertical = transform.FlipVertical,
                };
                layers.Add(new CompositorLayer(
                    frameBuf, trackIndex, opacity, placement, clip.BlendMode, placementIsFinal: true));
            }
        }
    }

    private sealed record CachedTextRaster(int Width, int Height, byte[]? Bgra, double WidthFraction, double HeightFraction)
    {
        public static readonly CachedTextRaster Empty = new(0, 0, null, 0, 0);
    }

    private readonly record struct TextRasterKey(
        string Visible,
        int OutputWidth,
        int OutputHeight,
        double BoxWidthNorm,
        double BoxHeightNorm,
        string FontName,
        double FontSize,
        double FontScale,
        bool IsBold,
        bool IsItalic,
        TextAlignment Alignment,
        double ColorR, double ColorG, double ColorB, double ColorA,
        bool BgEnabled, double BgR, double BgG, double BgB, double BgA,
        bool BorderEnabled, double BorderR, double BorderG, double BorderB, double BorderA,
        bool ShadowEnabled, double ShadowR, double ShadowG, double ShadowB, double ShadowA, double ShadowOffsetX, double ShadowOffsetY, double ShadowBlur,
        double CropLeft, double CropTop, double CropRight, double CropBottom,
        string? WordRunsSignature);

    private CachedTextRaster GetOrRasterizeText(
        string visible,
        TextStyle style,
        int outputWidth,
        int outputHeight,
        double boxWidthNorm,
        double boxHeightNorm,
        IReadOnlyList<TextWordRun>? wordRuns,
        Crop crop)
    {
        var key = BuildTextRasterKey(visible, style, outputWidth, outputHeight, boxWidthNorm, boxHeightNorm, wordRuns, crop);
        lock (_textCacheGate)
        {
            if (_textCache.TryGetValue(key, out var node))
            {
                _textLru.Remove(node);
                _textLru.AddFirst(node);
                return node.Value.Value;
            }
        }

        // Rasterize centered in the output canvas, then let the compositor place, scale,
        // rotate, and flip around the text block's own center (supports off-canvas movement,
        // rotation, flips, crop, and keyframed position/scale/rotation).
        var raster = _textRasterizer is CpuTextRasterizer cpu
            ? cpu.Rasterize(
                visible, style, outputWidth, outputHeight,
                layoutCenterX: 0.5, layoutCenterY: 0.5,
                boxWidthNorm, boxHeightNorm,
                wordRuns)
            : _textRasterizer.Rasterize(visible, style, outputWidth, outputHeight);

        CachedTextRaster result;
        if (!crop.IsIdentity && !ApplyTextCropInPlace(raster.Bgra, raster.Width, raster.Height, crop))
            result = CachedTextRaster.Empty;
        else
            result = CropTextCenterSymmetric(raster.Bgra, raster.Width, raster.Height);

        lock (_textCacheGate)
        {
            if (!_textCache.TryGetValue(key, out var existing))
            {
                var node = new LinkedListNode<(TextRasterKey, CachedTextRaster)>((key, result));
                _textLru.AddFirst(node);
                _textCache[key] = node;
                while (_textCache.Count > MaxCachedTextRasters && _textLru.Last is { } oldest)
                {
                    _textLru.RemoveLast();
                    _textCache.Remove(oldest.Value.Key);
                }
            }
            else
            {
                _textLru.Remove(existing);
                _textLru.AddFirst(existing);
                return existing.Value.Value;
            }
        }

        return result;
    }

    /// <summary>
    /// Tightens a centered text canvas to the smallest sub-rectangle symmetric about (W/2, H/2)
    /// that contains every visible pixel plus a 1 px transparent guard border. Keeping the sub-rect
    /// symmetric about the canvas center preserves rotation, flip, and crop pivots identically while
    /// shrinking 4K full-canvas text layers (~33 MB) by >50x for GPU upload and compositing.
    /// </summary>
    private static CachedTextRaster CropTextCenterSymmetric(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
            return CachedTextRaster.Empty;

        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                if (bgra[row + x * 4 + 3] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < minX || maxY < minY)
            return CachedTextRaster.Empty;

        // 2 px transparent guard border keeps bilinear edge-clamping (where Floor(-0.25) clamps
        // y0=0 with y1=1 and fy=0.75) strictly inside transparent pixels, matching the full canvas.
        var halfW = (int)Math.Ceiling(Math.Max(width / 2.0 - minX, (maxX + 1) - width / 2.0)) + 2;
        var halfH = (int)Math.Ceiling(Math.Max(height / 2.0 - minY, (maxY + 1) - height / 2.0)) + 2;
        var subW = Math.Min(width, Math.Max(2, halfW * 2 + (width & 1)));
        var subH = Math.Min(height, Math.Max(2, halfH * 2 + (height & 1)));

        if (subW >= width && subH >= height)
            return new CachedTextRaster(width, height, bgra, 1.0, 1.0);

        var x0 = (width - subW) / 2;
        var y0 = (height - subH) / 2;
        var tight = new byte[checked(subW * subH * 4)];
        var rowBytes = subW * 4;
        var srcStride = width * 4;
        for (var y = 0; y < subH; y++)
        {
            Buffer.BlockCopy(bgra, (y0 + y) * srcStride + x0 * 4, tight, y * rowBytes, rowBytes);
        }

        return new CachedTextRaster(subW, subH, tight, (double)subW / width, (double)subH / height);
    }

    private static TextRasterKey BuildTextRasterKey(
        string visible,
        TextStyle style,
        int outputWidth,
        int outputHeight,
        double boxWidthNorm,
        double boxHeightNorm,
        IReadOnlyList<TextWordRun>? wordRuns,
        Crop crop)
    {
        string? runsSig = null;
        if (wordRuns is { Count: > 0 })
        {
            var sb = new System.Text.StringBuilder(wordRuns.Count * 32);
            foreach (var r in wordRuns)
            {
                sb.Append(r.Start).Append(':').Append(r.Length).Append(':')
                    .Append(Math.Round(r.Opacity, 4)).Append(':')
                    .Append(Math.Round(r.Scale, 4)).Append(':')
                    .Append(Math.Round(r.OffsetY, 4)).Append(':')
                    .Append(Math.Round(r.Color.R, 3)).Append(',')
                    .Append(Math.Round(r.Color.G, 3)).Append(',')
                    .Append(Math.Round(r.Color.B, 3)).Append(',')
                    .Append(Math.Round(r.Color.A, 3));
                if (r.Background is { } bg)
                {
                    sb.Append('/').Append(Math.Round(bg.R, 3)).Append(',')
                        .Append(Math.Round(bg.G, 3)).Append(',')
                        .Append(Math.Round(bg.B, 3)).Append(',')
                        .Append(Math.Round(bg.A, 3));
                }
                sb.Append(';');
            }
            runsSig = sb.ToString();
        }

        return new TextRasterKey(
            visible,
            outputWidth,
            outputHeight,
            boxWidthNorm,
            boxHeightNorm,
            style.FontName ?? "",
            style.FontSize,
            style.FontScale,
            style.IsBold,
            style.IsItalic,
            style.Alignment,
            style.Color.R, style.Color.G, style.Color.B, style.Color.A,
            style.Background.Enabled, style.Background.Color.R, style.Background.Color.G, style.Background.Color.B, style.Background.Color.A,
            style.Border.Enabled, style.Border.Color.R, style.Border.Color.G, style.Border.Color.B, style.Border.Color.A,
            style.Shadow.Enabled, style.Shadow.Color.R, style.Shadow.Color.G, style.Shadow.Color.B, style.Shadow.Color.A,
            style.Shadow.OffsetX, style.Shadow.OffsetY, style.Shadow.Blur,
            crop.Left, crop.Top, crop.Right, crop.Bottom,
            runsSig);
    }

    public void ClearTextCache()
    {
        lock (_textCacheGate)
        {
            _textCache.Clear();
            _textLru.Clear();
        }
    }

    /// <summary>
    /// Crops a centered text raster relative to its visible non-transparent bounds so a percentage
    /// crop trims the text itself instead of invisible canvas margin. Clearing pixels in place keeps
    /// the remaining text anchored before rotation and flips run in the compositor.
    /// </summary>
    private static bool ApplyTextCropInPlace(byte[] bgra, int width, int height, Crop crop)
    {
        if (CropHidesPicture(crop) || width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
            return false;

        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                if (bgra[row + x * 4 + 3] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        if (maxX < minX || maxY < minY)
            return false;

        var boxW = maxX - minX + 1;
        var boxH = maxY - minY + 1;
        var keepX0 = minX + (int)Math.Round(Math.Clamp(crop.Left, 0, 1) * boxW);
        var keepX1 = maxX + 1 - (int)Math.Round(Math.Clamp(crop.Right, 0, 1) * boxW);
        var keepY0 = minY + (int)Math.Round(Math.Clamp(crop.Top, 0, 1) * boxH);
        var keepY1 = maxY + 1 - (int)Math.Round(Math.Clamp(crop.Bottom, 0, 1) * boxH);
        if (keepX1 <= keepX0 || keepY1 <= keepY0)
            return false;

        for (var y = minY; y <= maxY; y++)
        {
            var row = y * width * 4;
            if (y < keepY0 || y >= keepY1)
            {
                Array.Clear(bgra, row + minX * 4, boxW * 4);
                continue;
            }
            if (keepX0 > minX)
                Array.Clear(bgra, row + minX * 4, (keepX0 - minX) * 4);
            if (keepX1 <= maxX)
                Array.Clear(bgra, row + keepX1 * 4, (maxX + 1 - keepX1) * 4);
        }
        return true;
    }

    /// <summary>Projects the animator's per-word states onto the rasterizer's run contract.</summary>
    private static IReadOnlyList<TextWordRun>? WordRuns(TextAnimator.ClipState state)
    {
        if (state.Words is not { Count: > 0 } words) return null;
        var runs = new List<TextWordRun>(words.Count);
        foreach (var w in words)
            runs.Add(new TextWordRun(w.Start, w.Length, w.Opacity, w.Scale, w.OffsetY, w.Color, w.Background));
        return runs;
    }

    private async Task<CompositorLayer?> DecodeClipAsLayerAsync(
        Timeline timeline, Clip clip, int frame, int trackIndex, int depth, CancellationToken cancellationToken)
    {
        if (clip.MediaType is ClipType.text or ClipType.audio) return null;
        var opacity = clip.OpacityAt(frame);
        if (opacity <= 0) return null;
        var edge = Math.Clamp(frame, clip.StartFrame, clip.EndFrame - 1);
        var opacityFrame = clip.Contains(frame) ? frame : edge;
        opacity = clip.OpacityAt(opacityFrame);

        if (clip.MediaType == ClipType.sequence)
        {
            var nested = ResolveNestedTimeline(clip.MediaRef);
            if (nested is null) return null;
            var nestedFrame = NestedTimelineFrameForClip(clip, opacityFrame, timeline.Fps, nested.Fps);
            var rendered = await RenderAsync(
                    nested, nestedFrame, Math.Max(1, nested.Width), Math.Max(1, nested.Height),
                    depth + 1, cancellationToken)
                .ConfigureAwait(false);
            if (rendered is null) return null;
            if (CropToVisible(rendered, clip.CropAt(opacityFrame), clip.TransformAt(opacityFrame), timeline.Width, timeline.Height)
                is not { } visibleNested)
                return null;
            var effected = _effectsRenderer.Apply(visibleNested.Frame, clip.Effects, Math.Max(0, opacityFrame - clip.StartFrame));
            if (!ReferenceEquals(effected, visibleNested.Frame))
                visibleNested.Frame.Dispose();
            return new CompositorLayer(
                effected, trackIndex, opacity, visibleNested.Placement, clip.BlendMode, visibleNested.Final);
        }

        var path = _mediaResolver?.ResolvePath(clip.MediaRef);
        if (string.IsNullOrEmpty(path) && clip.MediaType != ClipType.lottie) return null;

        var sourceSeconds = clip.MediaType is ClipType.image or ClipType.lottie
            ? 0.0
            : TimelineFrameResolver.SourceSecondsAt(clip, opacityFrame, timeline.Fps);

        try
        {
            VideoFrame decoded;
            if (clip.MediaType == ClipType.lottie)
            {
                var placeholder = await TryDecodeLottieOrPlaceholderAsync(
                        new ResolvedFrameClip(
                            clip.Id, clip.MediaRef, clip.MediaType, path, 0,
                            trackIndex, opacity, clip.TransformAt(opacityFrame), clip.BlendMode,
                            clip.Effects, Math.Max(0, opacityFrame - clip.StartFrame)),
                        Math.Max(1, timeline.Width), Math.Max(1, timeline.Height), cancellationToken)
                    .ConfigureAwait(false);
                if (placeholder is null) return null;
                decoded = placeholder;
            }
            else
            {
                // WP-252: on the pool, like the layer decodes above: inside a transition nothing may
                // have awaited yet, so a decoder restart here would still run on the preview's UI thread.
                decoded = await Task.Run(() => _frameSource.GetFrameAsync(path!, sourceSeconds, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (CropToVisible(decoded, clip.CropAt(opacityFrame), clip.TransformAt(opacityFrame), timeline.Width, timeline.Height)
                is not { } visible)
                return null;
            var effected = _effectsRenderer.Apply(visible.Frame, clip.Effects, Math.Max(0, opacityFrame - clip.StartFrame));
            if (!ReferenceEquals(effected, visible.Frame))
                visible.Frame.Dispose();
            return new CompositorLayer(
                effected,
                trackIndex,
                opacity,
                visible.Placement,
                clip.BlendMode,
                visible.Final);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Releases the effects renderer this instance created. <see cref="GpuEffectsRenderer"/> owns an
    /// ID3D11Device, context and compute shader; before this existed every export and every preview
    /// provider leaked one for the lifetime of the process.
    /// </summary>
    public void Dispose()
    {
        ClearTextCache();
        // WP-215: the compositor belongs in here for the same reason the effects renderer does —
        // GpuCompositor owns an ID3D11Device. Leaving it out leaked one device per renderer, which
        // is every export job and every preview rebind.
        (_compositor as IDisposable)?.Dispose();
        (_effectsRenderer as IDisposable)?.Dispose();
        (_textRasterizer as IDisposable)?.Dispose();
    }

    private Timeline? ResolveNestedTimeline(string mediaRef)
    {
        if (string.IsNullOrEmpty(mediaRef)) return null;
        return _timelineLookup?.Invoke(mediaRef);
    }

    /// <summary>Map parent clip local time into nested timeline frames (trim+speed aware).</summary>
    private static int NestedTimelineFrame(ResolvedFrameClip layer, int parentFrame, int parentFps, int nestedFps)
    {
        parentFps = Math.Max(1, parentFps);
        nestedFps = Math.Max(1, nestedFps);
        // SourceSecondsAt already encodes trim/speed in parent fps units as seconds.
        return Math.Max(0, (int)Math.Round(layer.SourceSeconds * nestedFps));
    }

    private static int NestedTimelineFrameForClip(Clip clip, int parentFrame, int parentFps, int nestedFps)
    {
        parentFps = Math.Max(1, parentFps);
        nestedFps = Math.Max(1, nestedFps);
        var seconds = TimelineFrameResolver.SourceSecondsAt(clip, parentFrame, parentFps);
        return Math.Max(0, (int)Math.Round(seconds * nestedFps));
    }

    /// <summary>
    /// WP-405, Mac <c>FrameRenderer.applyClipPipeline</c>: crop, then effects, then placement. Crop
    /// trims <paramref name="picture"/> to its visible region, which draws where it sat in the whole
    /// picture (<see cref="ClipPlacement.VisibleRegion"/>). Without a crop the picture and transform
    /// pass through untouched. Takes ownership of <paramref name="picture"/>: a crop that leaves
    /// nothing visible releases it and returns null.
    /// </summary>
    private static (VideoFrame Frame, Transform Placement, bool Final)? CropToVisible(
        VideoFrame picture, Crop? crop, Transform transform, int canvasWidth, int canvasHeight)
    {
        if (crop is null || crop.IsIdentity) return (picture, transform, false);
        if (picture.Data.Length < (long)picture.Width * picture.Height * 4)
            return (picture, transform, false);

        var placed = ClipPlacement.FitInBox(transform, picture.Width, picture.Height, canvasWidth, canvasHeight);
        var region = ClipPlacement.VisibleRegion(placed, picture.Width, picture.Height, crop, canvasWidth, canvasHeight);
        if (region is null)
        {
            picture.Dispose();
            return null;
        }

        var rowBytes = region.Width * 4;
        var stride = picture.Width * 4;
        var data = new byte[region.Height * rowBytes];
        for (var y = 0; y < region.Height; y++)
            Buffer.BlockCopy(picture.Data, (region.Y + y) * stride + region.X * 4, data, y * rowBytes, rowBytes);
        var cropped = new VideoFrame(region.Width, region.Height, data, picture.PixelFormat);
        picture.Dispose();
        // The region's placement already holds the fit; the compositor must not fit it again.
        return (cropped, region.Placement, true);
    }

    private async Task<VideoFrame?> TryDecodeLottieOrPlaceholderAsync(
        ResolvedFrameClip layer, int width, int height, CancellationToken cancellationToken)
    {
        // Pre-flattened still (png/jpg/webp) at the media path is accepted as the Windows fallback.
        if (!string.IsNullOrEmpty(layer.MediaPath) && File.Exists(layer.MediaPath))
        {
            var ext = Path.GetExtension(layer.MediaPath).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")
            {
                try
                {
                    return await _frameSource.GetFrameAsync(layer.MediaPath, 0, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (FileNotFoundException)
                {
                    // fall through to placeholder
                }
                catch (NotSupportedException)
                {
                    // fall through
                }
            }
        }

        // Honest visible fallback: magenta checker — Lottie runtime not on Windows.
        return MakePlaceholderFrame(width, height, 180, 40, 180, 255);
    }

    private static VideoFrame MakePlaceholderFrame(int width, int height, byte r, byte g, byte b, byte a)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var data = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var checker = ((x / 8) + (y / 8)) % 2 == 0;
            var i = (y * width + x) * 4;
            data[i] = checker ? r : (byte)(r / 2);
            data[i + 1] = checker ? g : (byte)(g / 2);
            data[i + 2] = checker ? b : (byte)(b / 2);
            data[i + 3] = a;
        }
        return new VideoFrame(width, height, data, "rgba");
    }

    private VideoFrame? FlattenSide(CompositorLayer? layer, int width, int height)
    {
        if (layer is null) return null;
        try
        {
            return _compositor.Render([layer], width, height);
        }
        finally
        {
            layer.Frame.Dispose();
        }
    }

    /// <summary>
    /// True when at least one picture clip contains <paramref name="frame"/> and every one of
    /// them is fully transparent. Fade-in is 0 on the clip's first frame
    /// (<see cref="Clip.FadeMultiplier"/>), and the resolver drops those layers, so an empty
    /// composite here is black — not a gap. A clip whose own opacity is above 0 but whose
    /// text animation draws nothing stays absent; that case still returns null.
    /// </summary>
    private static bool PictureAtFrameIsFullyTransparent(Timeline timeline, int frame)
    {
        var found = false;
        foreach (var track in timeline.Tracks)
        {
            if (track.Hidden) continue;
            foreach (var clip in track.Clips)
            {
                if (!clip.Contains(frame) || !IsPictureClip(clip)) continue;
                // WP-405: a crop that leaves nothing visible draws nothing, like zero opacity.
                if (clip.OpacityAt(frame) > 0 && !CropHidesPicture(clip.CropAt(frame))) return false;
                found = true;
            }
        }

        return found;
    }

    private static bool CropHidesPicture(Crop crop)
        => crop.Left + crop.Right >= 1 || crop.Top + crop.Bottom >= 1;

    private static bool IsPictureClip(Clip clip)
    {
        if (clip.MediaType is ClipType.audio) return false;
        if (clip.MediaType is ClipType.text)
            return !string.IsNullOrEmpty(clip.TextContent);
        return clip.MediaType is ClipType.video or ClipType.image or ClipType.sequence or ClipType.lottie;
    }

    private static byte[] OpaqueBlack(int width, int height)
    {
        var data = new byte[width * height * 4];
        for (var i = 3; i < data.Length; i += 4) data[i] = 255;
        return data;
    }

    private static List<ResolvedTransition> ActiveTransitions(Timeline timeline, int frame)
    {
        var list = new List<ResolvedTransition>();
        foreach (var rt in timeline.ResolvedTransitions)
        {
            if (rt.IsActiveAt(frame))
                list.Add(rt);
        }
        return list;
    }

    private static double TransitionProgress(ResolvedTransition rt, int frame)
    {
        if (rt.Frames <= 1) return 1;
        return Math.Clamp((frame - rt.StartFrame) / (double)rt.Frames, 0, 1);
    }
}
