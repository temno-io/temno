using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Temno.Core.Models;

namespace Temno.Core.Export;

public static class ClipExtensions
{
    public static bool IsVisual(this ClipType type) => type != ClipType.audio;

    private static int ToOffset(this Clip clip, int timelineFrame) => timelineFrame - clip.StartFrame;
    private static int ToAbs(this Clip clip, int offset) => clip.StartFrame + offset;

    public static List<int> GetKeyframeFrames(this Clip clip, AnimatableProperty property)
    {
        IEnumerable<int> offsets = property switch
        {
            AnimatableProperty.Opacity => clip.OpacityTrack?.Keyframes.Select(k => k.Frame) ?? Array.Empty<int>(),
            AnimatableProperty.Position => clip.PositionTrack?.Keyframes.Select(k => k.Frame) ?? Array.Empty<int>(),
            AnimatableProperty.Scale => clip.ScaleTrack?.Keyframes.Select(k => k.Frame) ?? Array.Empty<int>(),
            AnimatableProperty.Rotation => clip.RotationTrack?.Keyframes.Select(k => k.Frame) ?? Array.Empty<int>(),
            AnimatableProperty.Crop => clip.CropTrack?.Keyframes.Select(k => k.Frame) ?? Array.Empty<int>(),
            AnimatableProperty.Volume => clip.VolumeTrack?.Keyframes.Select(k => k.Frame) ?? Array.Empty<int>(),
            _ => Array.Empty<int>()
        };
        return offsets.Select(clip.ToAbs).ToList();
    }

    public static Interpolation? GetInterpolation(this Clip clip, AnimatableProperty property, int frame)
    {
        var o = clip.ToOffset(frame);
        return property switch
        {
            AnimatableProperty.Opacity => clip.OpacityTrack?.Keyframes.FirstOrDefault(k => k.Frame == o)?.InterpolationOut,
            AnimatableProperty.Position => clip.PositionTrack?.Keyframes.FirstOrDefault(k => k.Frame == o)?.InterpolationOut,
            AnimatableProperty.Scale => clip.ScaleTrack?.Keyframes.FirstOrDefault(k => k.Frame == o)?.InterpolationOut,
            AnimatableProperty.Rotation => clip.RotationTrack?.Keyframes.FirstOrDefault(k => k.Frame == o)?.InterpolationOut,
            AnimatableProperty.Crop => clip.CropTrack?.Keyframes.FirstOrDefault(k => k.Frame == o)?.InterpolationOut,
            AnimatableProperty.Volume => clip.VolumeTrack?.Keyframes.FirstOrDefault(k => k.Frame == o)?.InterpolationOut,
            _ => null
        };
    }
}
