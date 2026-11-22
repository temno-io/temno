using Temno.Core.Models;

namespace Temno.Core.Session;

/// <summary>
/// WP-235: complete clip-state preservation across a timeline split.
/// <see cref="SplitOffRight"/> deep-copies every mutable field onto the right
/// fragment and retimes only the right side, so sampled state
/// (<c>OpacityAt</c>, <c>VolumeAt</c>, transform/crop channels) matches the
/// pre-split clip at every timeline frame inside each fragment.
/// </summary>
public static class ClipSplitPreservation
{
    private const double Epsilon = 1e-12;

    /// <summary>
    /// Deep copy of all clip state. Unlike a field-list copy, effect stacks,
    /// all six keyframe tracks, transform, crop and word timings are cloned,
    /// and <c>TextStyle</c>/<c>TextAnimation</c> are detached from the source
    /// document, so mutating one fragment never changes its neighbor.
    /// Keyframe and word-timing frames are copied verbatim; callers shift the
    /// right fragment (see <see cref="SplitOffRight"/>).
    /// </summary>
    public static Clip DeepCopy(Clip source) => new()
    {
        Id = source.Id,
        MediaRef = source.MediaRef,
        MediaType = source.MediaType,
        SourceClipType = source.SourceClipType,
        StartFrame = source.StartFrame,
        DurationFrames = source.DurationFrames,
        TrimStartFrame = source.TrimStartFrame,
        TrimEndFrame = source.TrimEndFrame,
        Speed = source.Speed,
        Volume = source.Volume,
        FadeInFrames = source.FadeInFrames,
        FadeOutFrames = source.FadeOutFrames,
        FadeInInterpolation = source.FadeInInterpolation,
        FadeOutInterpolation = source.FadeOutInterpolation,
        Opacity = source.Opacity,
        Transform = source.Transform.Clone(),
        Crop = source.Crop.Clone(),
        LinkGroupId = source.LinkGroupId,
        CaptionGroupId = source.CaptionGroupId,
        MulticamGroupId = source.MulticamGroupId,
        TextContent = source.TextContent,
        TextStyle = source.TextStyle?.Clone(),
        TextAnimation = source.TextAnimation?.Clone(),
        WordTimings = source.WordTimings?.Select(w => w.Clone()).ToList(),
        OpacityTrack = source.OpacityTrack?.Clone(),
        PositionTrack = source.PositionTrack?.Clone(p => p.Clone()),
        ScaleTrack = source.ScaleTrack?.Clone(p => p.Clone()),
        RotationTrack = source.RotationTrack?.Clone(),
        CropTrack = source.CropTrack?.Clone(crop => crop.Clone()),
        VolumeTrack = source.VolumeTrack?.Clone(),
        Effects = source.Effects?.Select(e => e.Clone()).ToList(),
        BlendMode = source.BlendMode,
    };

    /// <summary>
    /// Splits <paramref name="clip"/> at timeline <paramref name="frame"/>:
    /// shrinks <paramref name="clip"/> to the head and returns the new tail.
    /// Keyframe times are clip-relative (<c>frame - StartFrame</c>), so the head
    /// keeps its original keyframe frames verbatim (including keys past the cut,
    /// which a same-angle rejoin still samples) while the tail's six keyframe
    /// tracks, effect-param tracks and word timings shift by
    /// <c>-leftDuration</c> timeline frames — never scaled by
    /// <see cref="Clip.Speed"/> (source trim alone advances by
    /// <c>Round(leftDuration * Speed)</c>).
    /// Fades are edge properties: a cut that misses both fade windows leaves the
    /// head with (fadeIn, 0), interior pieces with (0, 0) and the tail with
    /// (0, fadeOut), which reproduces every in-range sample exactly. A cut that
    /// lands inside a fade window cannot be expressed with edge fades, so the
    /// affected fragment's opacity/volume base track is baked with dense
    /// per-frame keys holding
    /// <c>preSplitSample / fragmentFade</c> (exact at every integer sample
    /// point, the only points the sampling APIs serve); <c>ClampFadesToDuration</c>
    /// is never called because clamping an intersected fade would rewrite
    /// in-range samples.
    /// </summary>
    public static Clip SplitOffRight(Clip clip, int frame)
    {
        if (frame <= clip.StartFrame || frame >= clip.EndFrame)
            throw new ArgumentOutOfRangeException(
                nameof(frame), "Split frame must be strictly inside the clip.");

        var start = clip.StartFrame;
        var duration = clip.DurationFrames;
        var leftDuration = frame - start;
        var rightDuration = clip.EndFrame - frame;
        var fadeIn = clip.FadeInFrames;
        var fadeOut = clip.FadeOutFrames;
        var inInterp = clip.FadeInInterpolation;
        var outInterp = clip.FadeOutInterpolation;
        var isAudio = clip.MediaType == ClipType.audio;

        // Capture baked base tracks from the intact clip before mutating either side.
        var leftOpacity = !isAudio
            ? BakeTrack(clip, 0, leftDuration,
                (c, rel) => c.OpacityAt(start + rel),
                (c, rel) => c.RawOpacityAt(start + rel),
                rel => FadeMath(leftDuration, fadeIn, inInterp, 0, outInterp, rel))
            : null;
        var leftVolume = BakeTrack(clip, 0, leftDuration,
            (c, rel) => c.VolumeAt(start + rel),
            (c, rel) => DbAt(c, rel),
            rel => FadeMath(leftDuration, fadeIn, inInterp, 0, outInterp, rel),
            forVolume: true);
        var rightOpacity = !isAudio
            ? BakeTrack(clip, leftDuration, rightDuration,
                (c, rel) => c.OpacityAt(start + rel),
                (c, rel) => c.RawOpacityAt(start + rel),
                rel => FadeMath(rightDuration, 0, inInterp, fadeOut, outInterp, rel))
            : null;
        var rightVolume = BakeTrack(clip, leftDuration, rightDuration,
            (c, rel) => c.VolumeAt(start + rel),
            (c, rel) => DbAt(c, rel),
            rel => FadeMath(rightDuration, 0, inInterp, fadeOut, outInterp, rel),
            forVolume: true);

        var right = DeepCopy(clip);
        right.Id = Timeline.NewId();
        right.StartFrame = frame;
        right.DurationFrames = rightDuration;
        right.TrimStartFrame = clip.TrimStartFrame + (int)Math.Round(leftDuration * clip.Speed);
        right.FadeInFrames = 0;
        right.FadeOutFrames = fadeOut;

        clip.DurationFrames = leftDuration;
        clip.FadeOutFrames = 0;

        ShiftRelativeTimings(right, -leftDuration);
        if (rightOpacity is not null) right.OpacityTrack = DenseTrack(rightOpacity);
        if (rightVolume is not null) right.VolumeTrack = DenseTrack(rightVolume);
        if (leftOpacity is not null) clip.OpacityTrack = DenseTrack(leftOpacity);
        if (leftVolume is not null) clip.VolumeTrack = DenseTrack(leftVolume);
        return right;
    }

    /// <summary>
    /// Shifts clip-relative keyframe tracks, effect-parameter tracks, and word timings by
    /// <paramref name="delta"/> timeline frames (e.g. <c>-leftDuration</c> on the right half of a
    /// split, or <c>-(newStart - oldStart)</c> on a left-edge trim).
    /// </summary>
    public static void ShiftRelativeTimings(Clip clip, int delta)
    {
        if (delta == 0) return;
        Shift(clip.OpacityTrack, delta);
        Shift(clip.PositionTrack, delta);
        Shift(clip.ScaleTrack, delta);
        Shift(clip.RotationTrack, delta);
        Shift(clip.CropTrack, delta);
        Shift(clip.VolumeTrack, delta);
        if (clip.Effects is not null)
        {
            foreach (var effect in clip.Effects)
            {
                foreach (var param in effect.Params.Values)
                    Shift(param.Track, delta);
            }
        }
        if (clip.WordTimings is not null)
        {
            foreach (var word in clip.WordTimings)
            {
                word.StartFrame += delta;
                word.EndFrame += delta;
            }
        }
    }

    private static void Shift<T>(KeyframeTrack<T>? track, int delta)
    {
        if (track is null) return;
        foreach (var kf in track.Keyframes)
            kf.Frame += delta;
    }

    private static double DbAt(Clip clip, int clipRelativeFrame)
        => KeyframeMath.Sample(clip.VolumeTrack, clipRelativeFrame, 0);

    /// <summary>
    /// Returns dense per-frame base values over <c>[baseOffset, baseOffset + fragmentDuration)</c>
    /// (in pre-split clip-relative frames) when the fragment's assigned fades alone would
    /// change an in-range sample, else null. Opacity values are absolute base opacity;
    /// volume values are dB. Returns null without allocating when every sample matches.
    /// </summary>
    private static List<double>? BakeTrack(
        Clip pre,
        int baseOffset,
        int fragmentDuration,
        Func<Clip, int, double> preFull,
        Func<Clip, int, double> preRaw,
        Func<int, double> postFade,
        bool forVolume = false)
    {
        // Scan first: when the fragment's assigned fades already reproduce every
        // pre-split sample, return null so both fragments keep their tracks as-is.
        var mismatch = false;
        for (var rel = 0; rel < fragmentDuration; rel++)
        {
            var preMul = FadeMath(
                pre.DurationFrames, pre.FadeInFrames, pre.FadeInInterpolation,
                pre.FadeOutFrames, pre.FadeOutInterpolation, baseOffset + rel);
            if (Math.Abs(preMul - postFade(rel)) > Epsilon)
            {
                mismatch = true;
                break;
            }
        }
        if (!mismatch) return null;

        var baked = new List<double>(fragmentDuration);
        for (var rel = 0; rel < fragmentDuration; rel++)
        {
            var preRel = baseOffset + rel;
            var post = postFade(rel);
            if (!forVolume)
            {
                baked.Add(Math.Abs(post) > Epsilon ? preFull(pre, preRel) / post : preRaw(pre, preRel));
                continue;
            }
            if (Math.Abs(post) <= Epsilon || Math.Abs(pre.Volume) <= Epsilon)
            {
                baked.Add(DbAt(pre, preRel));
                continue;
            }
            var scaled = preFull(pre, preRel) / (pre.Volume * post);
            baked.Add(scaled <= 0 ? VolumeScale.FloorDb : VolumeScale.DbFromLinear(scaled));
        }
        return baked;
    }

    private static KeyframeTrack<double> DenseTrack(List<double> values)
    {
        var track = new KeyframeTrack<double>();
        for (var i = 0; i < values.Count; i++)
        {
            track.Keyframes.Add(new Keyframe<double>
            {
                Frame = i,
                Value = values[i],
                InterpolationOut = Interpolation.linear,
            });
        }
        return track;
    }

    /// <summary>Bit-for-bit replica of <see cref="Clip.FadeMultiplier"/> over explicit parameters.</summary>
    private static double FadeMath(
        int duration, int fadeIn, Interpolation inInterp, int fadeOut, Interpolation outInterp, int rel)
    {
        if (rel < 0 || rel > duration) return 0;

        double inMul = 1.0;
        if (fadeIn > 0)
        {
            var t = Math.Min(1.0, (double)rel / fadeIn);
            inMul = inInterp == Interpolation.smooth ? KeyframeMath.Smoothstep(t) : t;
        }

        var outRem = duration - rel;
        double outMul = 1.0;
        if (fadeOut > 0)
        {
            var t = Math.Min(1.0, (double)outRem / fadeOut);
            outMul = outInterp == Interpolation.smooth ? KeyframeMath.Smoothstep(t) : t;
        }

        return Math.Min(inMul, outMul);
    }
}
