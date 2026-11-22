using Temno.Core.Models;

namespace Temno.Core.Session;

/// <summary>
/// Stamp / remove / navigate keyframes at the playhead (timeline frames).
/// Frames on tracks are clip-relative (Mac AnimatableProperty offsets).
/// Supported properties: opacity, volume, rotation (scalar);
/// scale, position (AnimPair); crop (Crop).
/// </summary>
public static class KeyframeCommands
{
    public static readonly IReadOnlyList<string> SupportedProperties =
    [
        "opacity", "volume", "rotation", "scale", "position", "crop",
    ];

    // --- Convenience stamps (scalar) ---

    public static EditResult StampOpacity(ProjectSession session, string clipId, int timelineFrame)
        => Stamp(session, clipId, "opacity", timelineFrame);

    public static EditResult StampVolume(ProjectSession session, string clipId, int timelineFrame)
        => Stamp(session, clipId, "volume", timelineFrame);

    public static EditResult StampRotation(ProjectSession session, string clipId, int timelineFrame)
        => Stamp(session, clipId, "rotation", timelineFrame);

    public static EditResult StampScale(ProjectSession session, string clipId, int timelineFrame)
        => Stamp(session, clipId, "scale", timelineFrame);

    public static EditResult StampPosition(ProjectSession session, string clipId, int timelineFrame)
        => Stamp(session, clipId, "position", timelineFrame);

    public static EditResult StampCrop(ProjectSession session, string clipId, int timelineFrame)
        => Stamp(session, clipId, "crop", timelineFrame);

    // --- Convenience toggles ---

    public static EditResult ToggleStampOpacity(ProjectSession session, string clipId, int timelineFrame)
        => ToggleStamp(session, clipId, "opacity", timelineFrame);

    public static EditResult ToggleStampVolume(ProjectSession session, string clipId, int timelineFrame)
        => ToggleStamp(session, clipId, "volume", timelineFrame);

    public static EditResult ToggleStampRotation(ProjectSession session, string clipId, int timelineFrame)
        => ToggleStamp(session, clipId, "rotation", timelineFrame);

    public static EditResult ToggleStampScale(ProjectSession session, string clipId, int timelineFrame)
        => ToggleStamp(session, clipId, "scale", timelineFrame);

    public static EditResult ToggleStampPosition(ProjectSession session, string clipId, int timelineFrame)
        => ToggleStamp(session, clipId, "position", timelineFrame);

    public static EditResult ToggleStampCrop(ProjectSession session, string clipId, int timelineFrame)
        => ToggleStamp(session, clipId, "crop", timelineFrame);

    /// <summary>
    /// Unified stamp: writes the clip's current sampled value at <paramref name="timelineFrame"/>
    /// onto the named property track (one undo entry).
    /// </summary>
    public static EditResult Stamp(ProjectSession session, string clipId, string property, int timelineFrame)
    {
        var prop = Normalize(property);
        if (prop is null)
            return EditResult.Fail($"Unknown keyframe property: {property}");

        return WithUndo(session, () =>
        {
            var found = FindEditableClip(session, clipId, out var clip);
            if (!found.Ok) return found;
            var result = StampInPlace(clip!, prop, timelineFrame);
            if (!result.Ok) return result;
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>
    /// Stamps the clip's current sampled value at <paramref name="timelineFrame"/> in place without
    /// opening a separate undo transaction (for callers already inside an active live-edit snapshot).
    /// </summary>
    public static EditResult StampInPlace(Clip clip, string property, int timelineFrame)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var prop = Normalize(property);
        if (prop is null)
            return EditResult.Fail($"Unknown keyframe property: {property}");
        if (!TryOffset(clip, timelineFrame, out var offset))
            return EditResult.Fail("Playhead is outside the clip.");

        switch (prop)
        {
            case "opacity":
                UpsertDouble(clip, "opacity", offset, clip.RawOpacityAt(timelineFrame));
                break;
            case "volume":
                // Volume track stores dB envelope (Mac). Stamp current sample or 0 dB.
                var volDb = clip.VolumeTrack is { IsActive: true }
                    ? KeyframeMath.Sample(clip.VolumeTrack, offset, 0)
                    : 0;
                UpsertDouble(clip, "volume", offset, volDb);
                break;
            case "rotation":
                UpsertDouble(clip, "rotation", offset, clip.RotationAt(timelineFrame));
                break;
            case "scale":
            {
                var sz = clip.SizeAt(timelineFrame);
                UpsertPair(clip, "scale", offset, new AnimPair(sz.X, sz.Y));
                break;
            }
            case "position":
            {
                // Position track stores top-left (Mac AnimPair), not center.
                var tl = clip.TopLeftAt(timelineFrame);
                UpsertPair(clip, "position", offset, new AnimPair(tl.X, tl.Y));
                break;
            }
            case "crop":
                UpsertCrop(clip, offset, CloneCrop(clip.CropAt(timelineFrame)));
                break;
        }

        return EditResult.Success();
    }

    /// <summary>
    /// Toggle stamp at playhead in place (remove if present, else stamp current value) without
    /// opening a separate undo transaction.
    /// </summary>
    public static EditResult ToggleStampInPlace(Clip clip, string property, int timelineFrame)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var prop = Normalize(property);
        if (prop is null)
            return EditResult.Fail($"Unknown keyframe property: {property}");
        if (!TryOffset(clip, timelineFrame, out var offset))
            return EditResult.Fail("Playhead is outside the clip.");

        if (HasKeyframeAt(clip, prop, timelineFrame))
        {
            _ = prop switch
            {
                "opacity" or "volume" or "rotation" => RemoveDoubleAt(clip, prop, offset),
                "scale" or "position" => RemovePairAt(clip, prop, offset),
                "crop" => RemoveCropAt(clip, offset),
                _ => 0,
            };
            return EditResult.Success();
        }

        return StampInPlace(clip, prop, timelineFrame);
    }

    /// <summary>Toggle stamp at playhead: remove if present, else stamp current value.</summary>
    public static EditResult ToggleStamp(ProjectSession session, string clipId, string property, int timelineFrame)
    {
        var found = FindEditableClip(session, clipId, out var clip);
        if (!found.Ok) return found;
        if (HasKeyframeAt(clip!, property, timelineFrame))
            return RemoveAt(session, clipId, property, timelineFrame);
        return Stamp(session, clipId, property, timelineFrame);
    }

    public static EditResult RemoveAt(ProjectSession session, string clipId, string property, int timelineFrame)
    {
        var prop = Normalize(property);
        if (prop is null)
            return EditResult.Fail($"Unknown keyframe property: {property}");

        return WithUndo(session, () =>
        {
            var found = FindEditableClip(session, clipId, out var clip);
            if (!found.Ok) return found;
            if (!TryOffset(clip!, timelineFrame, out var offset))
                return EditResult.Fail("Playhead is outside the clip.");

            var removed = prop switch
            {
                "opacity" or "volume" or "rotation" => RemoveDoubleAt(clip!, prop, offset),
                "scale" or "position" => RemovePairAt(clip!, prop, offset),
                "crop" => RemoveCropAt(clip!, offset),
                _ => 0,
            };
            if (removed == 0) return EditResult.Success();
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>Previous keyframe timeline frame, or null.</summary>
    public static int? PreviousTimelineFrame(Clip clip, string property, int timelineFrame)
    {
        var offsets = GetOffsets(clip, property);
        if (offsets.Count == 0) return null;
        var offset = timelineFrame - clip.StartFrame;
        var prev = offsets.Where(f => f < offset).DefaultIfEmpty(-1).Max();
        return prev < 0 ? null : clip.StartFrame + prev;
    }

    /// <summary>Next keyframe timeline frame, or null.</summary>
    public static int? NextTimelineFrame(Clip clip, string property, int timelineFrame)
    {
        var offsets = GetOffsets(clip, property);
        if (offsets.Count == 0) return null;
        var offset = timelineFrame - clip.StartFrame;
        var next = offsets.Where(f => f > offset).DefaultIfEmpty(int.MaxValue).Min();
        return next == int.MaxValue ? null : clip.StartFrame + next;
    }

    public static bool HasKeyframeAt(Clip clip, string property, int timelineFrame)
    {
        if (!TryOffset(clip, timelineFrame, out var offset)) return false;
        return GetOffsets(clip, property).Contains(offset);
    }

    public static int KeyframeCount(Clip clip, string property)
        => GetOffsets(clip, property).Count;

    /// <summary>
    /// Move a keyframe from <paramref name="fromTimelineFrame"/> to <paramref name="toTimelineFrame"/>
    /// (clip-relative retime). Value is preserved; colliding destination replaces existing.
    /// One undo entry.
    /// </summary>
    public static EditResult MoveKeyframe(
        ProjectSession session,
        string clipId,
        string property,
        int fromTimelineFrame,
        int toTimelineFrame)
    {
        var prop = Normalize(property);
        if (prop is null)
            return EditResult.Fail($"Unknown keyframe property: {property}");

        return WithUndo(session, () =>
        {
            var found = FindEditableClip(session, clipId, out var clip);
            if (!found.Ok) return found;
            if (!TryOffset(clip!, fromTimelineFrame, out var fromOff))
                return EditResult.Fail("Source keyframe is outside the clip.");
            if (!TryOffset(clip!, toTimelineFrame, out var toOff))
                return EditResult.Fail("Destination is outside the clip.");
            if (fromOff == toOff)
                return EditResult.Success();

            switch (prop)
            {
                case "opacity" or "volume" or "rotation":
                {
                    var track = GetDoubleTrack(clip!, prop);
                    if (track is null) return EditResult.Fail("No keyframe track.");
                    var idx = track.Keyframes.FindIndex(k => k.Frame == fromOff);
                    if (idx < 0) return EditResult.Fail("Keyframe not found.");
                    var kf = track.Keyframes[idx];
                    track.Keyframes.RemoveAt(idx);
                    track.Keyframes.RemoveAll(k => k.Frame == toOff);
                    kf.Frame = toOff;
                    track.Upsert(kf);
                    SetDoubleTrack(clip!, prop, track);
                    break;
                }
                case "scale" or "position":
                {
                    var track = GetPairTrack(clip!, prop);
                    if (track is null) return EditResult.Fail("No keyframe track.");
                    var idx = track.Keyframes.FindIndex(k => k.Frame == fromOff);
                    if (idx < 0) return EditResult.Fail("Keyframe not found.");
                    var kf = track.Keyframes[idx];
                    track.Keyframes.RemoveAt(idx);
                    track.Keyframes.RemoveAll(k => k.Frame == toOff);
                    kf.Frame = toOff;
                    track.Upsert(kf);
                    SetPairTrack(clip!, prop, track);
                    break;
                }
                case "crop":
                {
                    var track = clip!.CropTrack;
                    if (track is null) return EditResult.Fail("No keyframe track.");
                    var idx = track.Keyframes.FindIndex(k => k.Frame == fromOff);
                    if (idx < 0) return EditResult.Fail("Keyframe not found.");
                    var kf = track.Keyframes[idx];
                    track.Keyframes.RemoveAt(idx);
                    track.Keyframes.RemoveAll(k => k.Frame == toOff);
                    kf.Frame = toOff;
                    track.Upsert(kf);
                    clip!.CropTrack = track;
                    break;
                }
            }

            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>In-range clip-relative keyframe offsets for a property (sorted ascending).</summary>
    public static IReadOnlyList<int> GetOffsets(Clip clip, string property)
    {
        var prop = Normalize(property);
        if (prop is null || clip.DurationFrames <= 0) return Array.Empty<int>();

        IEnumerable<int>? frames = prop switch
        {
            "opacity" => clip.OpacityTrack?.Keyframes.Select(k => k.Frame),
            "volume" => clip.VolumeTrack?.Keyframes.Select(k => k.Frame),
            "rotation" => clip.RotationTrack?.Keyframes.Select(k => k.Frame),
            "scale" or "scale.a" or "scale.b" => clip.ScaleTrack?.Keyframes.Select(k => k.Frame),
            "position" or "position.a" or "position.b" => clip.PositionTrack?.Keyframes.Select(k => k.Frame),
            "crop" => clip.CropTrack?.Keyframes.Select(k => k.Frame),
            _ => null,
        };
        if (frames is null) return Array.Empty<int>();
        return frames
            .Where(f => f >= 0 && f < clip.DurationFrames)
            .OrderBy(f => f)
            .ToList();
    }

    public sealed record ScalarKey(int Offset, double Value, Interpolation InterpolationOut);

    /// <summary>
    /// Scalar graph channels: opacity, volume, rotation, scale.a/b, position.a/b, crop edges.
    /// </summary>
    public static IReadOnlyList<ScalarKey> GetScalarKeys(Clip clip, string channel)
    {
        var ch = NormalizeChannel(channel);
        if (ch is null) return [];
        return ch switch
        {
            "opacity" => MapDouble(clip.OpacityTrack),
            "volume" => MapDouble(clip.VolumeTrack),
            "rotation" => MapDouble(clip.RotationTrack),
            "scale.a" => MapPair(clip.ScaleTrack, a: true),
            "scale.b" => MapPair(clip.ScaleTrack, a: false),
            "position.a" => MapPair(clip.PositionTrack, a: true),
            "position.b" => MapPair(clip.PositionTrack, a: false),
            "crop.left" => MapCrop(clip.CropTrack, static c => c.Left),
            "crop.top" => MapCrop(clip.CropTrack, static c => c.Top),
            "crop.right" => MapCrop(clip.CropTrack, static c => c.Right),
            "crop.bottom" => MapCrop(clip.CropTrack, static c => c.Bottom),
            _ => [],
        };

        static List<ScalarKey> MapDouble(KeyframeTrack<double>? track)
            => track?.Keyframes
                   .OrderBy(k => k.Frame)
                   .Select(k => new ScalarKey(k.Frame, k.Value, k.InterpolationOut))
                   .ToList()
               ?? [];

        static List<ScalarKey> MapPair(KeyframeTrack<AnimPair>? track, bool a)
            => track?.Keyframes
                   .OrderBy(k => k.Frame)
                   .Select(k => new ScalarKey(k.Frame, a ? k.Value.A : k.Value.B, k.InterpolationOut))
                   .ToList()
               ?? [];

        static List<ScalarKey> MapCrop(KeyframeTrack<Crop>? track, Func<Crop, double> component)
            => track?.Keyframes
                   .OrderBy(k => k.Frame)
                   .Select(k => new ScalarKey(k.Frame, component(k.Value), k.InterpolationOut))
                   .ToList()
               ?? [];
    }

    /// <summary>True when at least one of the six keyframe tracks has keys.</summary>
    public static bool HasAnyActiveTrack(Clip clip) =>
        clip.OpacityTrack is { IsActive: true }
        || clip.PositionTrack is { IsActive: true }
        || clip.ScaleTrack is { IsActive: true }
        || clip.RotationTrack is { IsActive: true }
        || clip.CropTrack is { IsActive: true }
        || clip.VolumeTrack is { IsActive: true };

    /// <summary>
    /// Mac Clip.liveVolumeKfDb(at:) — sampled dB keyframe value when the clip has an active volume
    /// track and contains <paramref name="timelineFrame"/>, otherwise null.
    /// </summary>
    public static double? LiveVolumeKfDb(Clip clip, int timelineFrame)
        => clip.Contains(timelineFrame) && clip.VolumeTrack is { IsActive: true }
            ? KeyframeMath.Sample(clip.VolumeTrack, timelineFrame - clip.StartFrame, 0)
            : null;

    private static int ClampedOffset(Clip clip, int timelineFrame)
        => Math.Clamp(timelineFrame - clip.StartFrame, 0, Math.Max(0, clip.DurationFrames - 1));

    /// <summary>
    /// Animation-aware live write (Mac EditorViewModel.writeOpacity): upserts into OpacityTrack
    /// at the clamped playhead offset when the track is active, else updates static Opacity.
    /// </summary>
    public static bool WriteOpacityInPlace(Clip clip, int timelineFrame, double opacity)
    {
        var clamped = Math.Clamp(opacity, 0, 1);
        if (clip.OpacityTrack is { IsActive: true })
        {
            UpsertDouble(clip, "opacity", ClampedOffset(clip, timelineFrame), clamped);
            return true;
        }
        clip.Opacity = clamped;
        return false;
    }

    /// <summary>
    /// Animation-aware live write (Mac EditorViewModel.writeRotation): upserts into RotationTrack
    /// at the clamped playhead offset when active, else updates static Transform.Rotation.
    /// </summary>
    public static bool WriteRotationInPlace(Clip clip, int timelineFrame, double rotationDeg)
    {
        if (clip.RotationTrack is { IsActive: true })
        {
            UpsertDouble(clip, "rotation", ClampedOffset(clip, timelineFrame), rotationDeg);
            return true;
        }
        clip.Transform.Rotation = rotationDeg;
        return false;
    }

    /// <summary>
    /// Animation-aware live write for Center X / Center Y: when PositionTrack is active, converts
    /// center coordinates using the sampled size at <paramref name="timelineFrame"/> into top-left
    /// and upserts into PositionTrack; else writes static Transform.CenterX / CenterY.
    /// </summary>
    public static bool WritePositionCenterInPlace(Clip clip, int timelineFrame, double? centerX, double? centerY)
    {
        if (clip.PositionTrack is { IsActive: true })
        {
            var current = clip.TransformAt(timelineFrame);
            var sz = clip.SizeAt(timelineFrame);
            var cx = centerX ?? current.CenterX;
            var cy = centerY ?? current.CenterY;
            UpsertPair(clip, "position", ClampedOffset(clip, timelineFrame),
                new AnimPair(cx - sz.X / 2.0, cy - sz.Y / 2.0));
            return true;
        }
        if (centerX is { } x) clip.Transform.CenterX = x;
        if (centerY is { } y) clip.Transform.CenterY = y;
        return false;
    }

    /// <summary>
    /// Animation-aware live write for normalized transform size: when ScaleTrack is active, upserts
    /// into ScaleTrack without mutating static Transform; else writes static Transform.Width / Height.
    /// When PositionTrack is also active (which stores top-left for Mac interchange), keeps the clip's
    /// visual center anchored instead of drifting down-right as the box grows or shrinks.
    /// </summary>
    public static bool WriteScaleSizeInPlace(Clip clip, int timelineFrame, double width, double height)
    {
        var w = Math.Clamp(width, 1e-4, 16.0);
        var h = Math.Clamp(height, 1e-4, 16.0);
        if (clip.ScaleTrack is { IsActive: true })
        {
            var offset = ClampedOffset(clip, timelineFrame);
            var anchorCenter = clip.PositionTrack is { IsActive: true }
                ? clip.TransformAt(timelineFrame).Center
                : (X: 0.0, Y: 0.0);
            UpsertPair(clip, "scale", offset, new AnimPair(w, h));
            if (clip.PositionTrack is { IsActive: true })
            {
                UpsertPair(clip, "position", offset,
                    new AnimPair(anchorCenter.X - w / 2.0, anchorCenter.Y - h / 2.0));
            }
            return true;
        }

        var oldW = clip.Transform.Width;
        var oldH = clip.Transform.Height;
        clip.Transform.Width = w;
        clip.Transform.Height = h;
        if (clip.PositionTrack is { IsActive: true })
        {
            var dx = (w - oldW) / 2.0;
            var dy = (h - oldH) / 2.0;
            foreach (var kf in clip.PositionTrack.Keyframes)
                kf.Value = new AnimPair(kf.Value.A - dx, kf.Value.B - dy);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Animation-aware live write for crop edges: when CropTrack is active, starts from the sampled
    /// crop at <paramref name="timelineFrame"/>, replaces specified edges, and upserts into CropTrack;
    /// else writes static Crop.
    /// </summary>
    public static bool WriteCropInPlace(
        Clip clip,
        int timelineFrame,
        double? left = null,
        double? top = null,
        double? right = null,
        double? bottom = null)
    {
        if (clip.CropTrack is { IsActive: true })
        {
            var crop = CloneCrop(clip.CropAt(timelineFrame));
            if (left is { } l) crop.Left = Math.Clamp(l, 0, 1);
            if (top is { } t) crop.Top = Math.Clamp(t, 0, 1);
            if (right is { } r) crop.Right = Math.Clamp(r, 0, 1);
            if (bottom is { } b) crop.Bottom = Math.Clamp(b, 0, 1);
            UpsertCrop(clip, ClampedOffset(clip, timelineFrame), crop);
            return true;
        }
        if (left is { } sl) clip.Crop.Left = Math.Clamp(sl, 0, 1);
        if (top is { } st) clip.Crop.Top = Math.Clamp(st, 0, 1);
        if (right is { } sr) clip.Crop.Right = Math.Clamp(sr, 0, 1);
        if (bottom is { } sb) clip.Crop.Bottom = Math.Clamp(sb, 0, 1);
        return false;
    }

    /// <summary>
    /// Animation-aware live write for volume (dB, Mac EditorViewModel.writeVolume): when VolumeTrack
    /// is active and playhead is inside the clip, upserts the dB keyframe; else writes static Volume.
    /// </summary>
    public static bool WriteVolumeDbInPlace(Clip clip, int timelineFrame, double valueDb)
    {
        var clampedDb = Math.Clamp(valueDb, VolumeScale.FloorDb, VolumeScale.CeilingDb);
        if (LiveVolumeKfDb(clip, timelineFrame) is not null)
        {
            UpsertDouble(clip, "volume", timelineFrame - clip.StartFrame, clampedDb);
            return true;
        }
        clip.Volume = VolumeScale.LinearFromDb(clampedDb);
        return false;
    }

    /// <summary>
    /// Sample a scalar channel at a clip-relative offset using the clip's own static values as
    /// fallback when the channel has no keyframes yet (avoids artificial mid-range defaults).
    /// </summary>
    public static double SampleScalarAtClip(Clip clip, string channel, int clipOffset)
    {
        var ch = NormalizeChannel(channel);
        if (ch is null) return 0;
        var frame = clip.StartFrame + clipOffset;
        return ch switch
        {
            "opacity" => KeyframeMath.Sample(clip.OpacityTrack, clipOffset, clip.Opacity),
            "volume" => KeyframeMath.Sample(clip.VolumeTrack, clipOffset, 0),
            "rotation" => KeyframeMath.Sample(clip.RotationTrack, clipOffset, clip.Transform.Rotation),
            "scale.a" => clip.SizeAt(frame).X,
            "scale.b" => clip.SizeAt(frame).Y,
            "position.a" => clip.TopLeftAt(frame).X,
            "position.b" => clip.TopLeftAt(frame).Y,
            "crop.left" => KeyframeMath.Sample(clip.CropTrack, clipOffset, clip.Crop).Left,
            "crop.top" => KeyframeMath.Sample(clip.CropTrack, clipOffset, clip.Crop).Top,
            "crop.right" => KeyframeMath.Sample(clip.CropTrack, clipOffset, clip.Crop).Right,
            "crop.bottom" => KeyframeMath.Sample(clip.CropTrack, clipOffset, clip.Crop).Bottom,
            _ => 0,
        };
    }

    /// <summary>Sample a scalar channel at a clip-relative offset (for graph polylines).</summary>
    public static double SampleScalar(Clip clip, string channel, int clipOffset, double fallback)
    {
        var ch = NormalizeChannel(channel);
        if (ch is null) return fallback;
        return ch switch
        {
            "opacity" => KeyframeMath.Sample(clip.OpacityTrack, clipOffset, fallback),
            "volume" => KeyframeMath.Sample(clip.VolumeTrack, clipOffset, fallback),
            "rotation" => KeyframeMath.Sample(clip.RotationTrack, clipOffset, fallback),
            "scale.a" => KeyframeMath.Sample(clip.ScaleTrack, clipOffset, new AnimPair(fallback, fallback)).A,
            "scale.b" => KeyframeMath.Sample(clip.ScaleTrack, clipOffset, new AnimPair(fallback, fallback)).B,
            "position.a" => KeyframeMath.Sample(clip.PositionTrack, clipOffset, new AnimPair(fallback, fallback)).A,
            "position.b" => KeyframeMath.Sample(clip.PositionTrack, clipOffset, new AnimPair(fallback, fallback)).B,
            "crop.left" => KeyframeMath.Sample(clip.CropTrack, clipOffset, clip.Crop).Left,
            "crop.top" => KeyframeMath.Sample(clip.CropTrack, clipOffset, clip.Crop).Top,
            "crop.right" => KeyframeMath.Sample(clip.CropTrack, clipOffset, clip.Crop).Right,
            "crop.bottom" => KeyframeMath.Sample(clip.CropTrack, clipOffset, clip.Crop).Bottom,
            _ => fallback,
        };
    }

    public static (double Min, double Max) ScalarRange(string channel) => NormalizeChannel(channel) switch
    {
        "opacity" => (0, 1),
        "volume" => (-60, 15), // dB envelope
        "rotation" => (-360, 360),
        "scale.a" or "scale.b" => (0.05, 4),
        "position.a" or "position.b" => (-1, 2),
        "crop.left" or "crop.top" or "crop.right" or "crop.bottom" => (0, 1),
        _ => (0, 1),
    };

    /// <summary>
    /// Adaptive Y-axis bounds for the value×time keyframe graph. Keeps small normalized text boxes
    /// (e.g. width 0.04–0.25) legible and draggable without snapping below 0.05, and expands bounds
    /// whenever existing keys exceed the default range.
    /// </summary>
    public static (double Min, double Max) DisplayRange(Clip clip, string channel)
    {
        var ch = NormalizeChannel(channel);
        var (vmin, vmax) = ScalarRange(channel);
        if (ch is null || clip is null) return (vmin, vmax);

        var minObs = double.PositiveInfinity;
        var maxObs = double.NegativeInfinity;
        void Observe(double v)
        {
            if (!double.IsFinite(v)) return;
            if (v < minObs) minObs = v;
            if (v > maxObs) maxObs = v;
        }

        foreach (var k in GetScalarKeys(clip, ch))
        {
            if (k.Offset >= 0 && k.Offset < clip.DurationFrames)
                Observe(k.Value);
        }
        Observe(SampleScalarAtClip(clip, ch, 0));
        if (clip.DurationFrames > 1)
            Observe(SampleScalarAtClip(clip, ch, clip.DurationFrames - 1));

        if (!double.IsFinite(minObs) || !double.IsFinite(maxObs))
            return (vmin, vmax);

        switch (ch)
        {
            case "scale.a":
            case "scale.b":
            {
                const double minScale = 0.0;
                double maxScale;
                if (clip.MediaType == ClipType.text || maxObs < 0.5)
                {
                    maxScale = Math.Max(0.25, Math.Ceiling(maxObs * 1.5 * 20.0) / 20.0);
                    maxScale = Math.Min(16.0, maxScale);
                }
                else
                {
                    maxScale = Math.Min(16.0, Math.Max(4.0, Math.Ceiling(maxObs * 1.25)));
                }
                return (minScale, Math.Max(minScale + 0.1, maxScale));
            }
            case "rotation":
            {
                var rmin = minObs < vmin ? Math.Floor(minObs / 90.0) * 90.0 : vmin;
                var rmax = maxObs > vmax ? Math.Ceiling(maxObs / 90.0) * 90.0 : vmax;
                return (rmin, rmax);
            }
            case "position.a":
            case "position.b":
            {
                var pmin = minObs < vmin ? Math.Floor(minObs) - 0.5 : vmin;
                var pmax = maxObs > vmax ? Math.Ceiling(maxObs) + 0.5 : vmax;
                return (pmin, pmax);
            }
            default:
                return (vmin, vmax);
        }
    }

    /// <summary>
    /// Upsert a scalar key at clip-relative <paramref name="offset"/> with value + optional interpolation.
    /// One undo. For pair channels, preserves the other component from existing key or current sample.
    /// </summary>
    public static EditResult UpsertScalarKey(
        ProjectSession session,
        string clipId,
        string channel,
        int offset,
        double value,
        Interpolation? interpolationOut = null)
    {
        var ch = NormalizeChannel(channel);
        if (ch is null) return EditResult.Fail($"Unknown graph channel: {channel}");

        return WithUndo(session, () =>
        {
            var found = FindEditableClip(session, clipId, out var clip);
            if (!found.Ok) return found;
            if (offset < 0 || offset >= clip!.DurationFrames)
                return EditResult.Fail("Offset outside clip.");

            var interp = interpolationOut ?? Interpolation.smooth;
            switch (ch)
            {
                case "opacity":
                    UpsertDouble(clip, "opacity", offset, Math.Clamp(value, 0, 1), interp);
                    break;
                case "volume":
                    UpsertDouble(clip, "volume", offset, Math.Clamp(value, VolumeScale.FloorDb, VolumeScale.CeilingDb), interp);
                    break;
                case "rotation":
                    UpsertDouble(clip, "rotation", offset, value, interp);
                    break;
                case "scale.a":
                case "scale.b":
                {
                    var track = clip.ScaleTrack ?? new KeyframeTrack<AnimPair>();
                    var existing = track.Keyframes.FirstOrDefault(k => k.Frame == offset);
                    var pair = existing?.Value
                        ?? KeyframeMath.Sample(clip.ScaleTrack, offset, new AnimPair(clip.Transform.Width, clip.Transform.Height));
                    pair = ch == "scale.a"
                        ? new AnimPair(Math.Clamp(value, 1e-4, 16.0), pair.B)
                        : new AnimPair(pair.A, Math.Clamp(value, 1e-4, 16.0));
                    track.Upsert(new Keyframe<AnimPair> { Frame = offset, Value = pair, InterpolationOut = interp });
                    clip.ScaleTrack = track;
                    break;
                }
                case "position.a":
                case "position.b":
                {
                    var track = clip.PositionTrack ?? new KeyframeTrack<AnimPair>();
                    var existing = track.Keyframes.FirstOrDefault(k => k.Frame == offset);
                    var topLeft = clip.TopLeftAt(clip.StartFrame + offset);
                    var pair = existing?.Value
                        ?? KeyframeMath.Sample(clip.PositionTrack, offset, new AnimPair(topLeft.X, topLeft.Y));
                    pair = ch == "position.a"
                        ? new AnimPair(value, pair.B)
                        : new AnimPair(pair.A, value);
                    track.Upsert(new Keyframe<AnimPair> { Frame = offset, Value = pair, InterpolationOut = interp });
                    clip.PositionTrack = track;
                    break;
                }
                case "crop.left":
                case "crop.top":
                case "crop.right":
                case "crop.bottom":
                {
                    var track = clip.CropTrack ?? new KeyframeTrack<Crop>();
                    var existing = track.Keyframes.FirstOrDefault(k => k.Frame == offset);
                    var crop = CloneCrop(existing?.Value
                        ?? KeyframeMath.Sample(clip.CropTrack, offset, clip.Crop));
                    SetCropComponent(crop, ch, value);
                    track.Upsert(new Keyframe<Crop> { Frame = offset, Value = crop, InterpolationOut = interp });
                    clip.CropTrack = track;
                    break;
                }
            }

            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>Move + optionally revalue a scalar key (graph drag). One undo.</summary>
    public static EditResult MoveScalarKey(
        ProjectSession session,
        string clipId,
        string channel,
        int fromOffset,
        int toOffset,
        double value,
        Interpolation? interpolationOut = null)
    {
        var ch = NormalizeChannel(channel);
        if (ch is null) return EditResult.Fail($"Unknown graph channel: {channel}");

        return WithUndo(session, () =>
        {
            var found = FindEditableClip(session, clipId, out var clip);
            if (!found.Ok) return found;
            if (fromOffset < 0 || fromOffset >= clip!.DurationFrames)
                return EditResult.Fail("Source outside clip.");
            if (toOffset < 0 || toOffset >= clip.DurationFrames)
                return EditResult.Fail("Destination outside clip.");

            // Capture interp from source key when not specified
            var keys = GetScalarKeys(clip, ch);
            var src = keys.FirstOrDefault(k => k.Offset == fromOffset);
            if (src is null)
                return EditResult.Fail("Keyframe not found.");
            if (fromOffset != toOffset && keys.Any(k => k.Offset == toOffset))
                return EditResult.Fail("A keyframe already exists at the destination frame.");
            var interp = interpolationOut ?? src?.InterpolationOut ?? Interpolation.smooth;

            EditResult r;
            if (ch is "scale.a" or "scale.b")
            {
                r = MovePairScalarKey(clip, "scale", ch, fromOffset, toOffset, value, interp);
            }
            else if (ch is "position.a" or "position.b")
            {
                r = MovePairScalarKey(clip, "position", ch, fromOffset, toOffset, value, interp);
            }
            else if (ch.StartsWith("crop.", StringComparison.Ordinal))
            {
                r = MoveCropScalarKey(clip, ch, fromOffset, toOffset, value, interp);
            }
            else
            {
                if (fromOffset != toOffset)
                    RemoveDoubleAt(clip, ch, fromOffset);
                r = UpsertScalarKeyNoUndo(clip, ch, toOffset, value, interp);
            }
            if (!r.Ok) return r;
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static EditResult SetInterpolation(
        ProjectSession session, string clipId, string channel, int offset, Interpolation interpolation)
    {
        var ch = NormalizeChannel(channel);
        if (ch is null) return EditResult.Fail($"Unknown graph channel: {channel}");

        return WithUndo(session, () =>
        {
            var found = FindEditableClip(session, clipId, out var clip);
            if (!found.Ok) return found;
            var keys = GetScalarKeys(clip!, ch);
            var key = keys.FirstOrDefault(k => k.Offset == offset);
            if (key is null) return EditResult.Fail("Keyframe not found.");
            var r = UpsertScalarKeyNoUndo(clip!, ch, offset, key.Value, interpolation);
            if (r.Ok) session.MarkDirty();
            return r;
        });
    }

    private static EditResult UpsertScalarKeyNoUndo(
        Clip clip, string ch, int offset, double value, Interpolation interp)
    {
        switch (ch)
        {
            case "opacity":
                UpsertDouble(clip, "opacity", offset, Math.Clamp(value, 0, 1), interp);
                return EditResult.Success();
            case "volume":
                UpsertDouble(clip, "volume", offset, Math.Clamp(value, VolumeScale.FloorDb, VolumeScale.CeilingDb), interp);
                return EditResult.Success();
            case "rotation":
                UpsertDouble(clip, "rotation", offset, value, interp);
                return EditResult.Success();
            case "scale.a":
            case "scale.b":
            {
                var track = clip.ScaleTrack ?? new KeyframeTrack<AnimPair>();
                var existing = track.Keyframes.FirstOrDefault(k => k.Frame == offset);
                var pair = existing?.Value
                    ?? KeyframeMath.Sample(clip.ScaleTrack, offset, new AnimPair(clip.Transform.Width, clip.Transform.Height));
                pair = ch == "scale.a"
                    ? new AnimPair(Math.Clamp(value, 1e-4, 16.0), pair.B)
                    : new AnimPair(pair.A, Math.Clamp(value, 1e-4, 16.0));
                track.Upsert(new Keyframe<AnimPair> { Frame = offset, Value = pair, InterpolationOut = interp });
                clip.ScaleTrack = track;
                return EditResult.Success();
            }
            case "position.a":
            case "position.b":
            {
                var track = clip.PositionTrack ?? new KeyframeTrack<AnimPair>();
                var existing = track.Keyframes.FirstOrDefault(k => k.Frame == offset);
                var topLeft = clip.TopLeftAt(clip.StartFrame + offset);
                var pair = existing?.Value
                    ?? KeyframeMath.Sample(clip.PositionTrack, offset, new AnimPair(topLeft.X, topLeft.Y));
                pair = ch == "position.a" ? new AnimPair(value, pair.B) : new AnimPair(pair.A, value);
                track.Upsert(new Keyframe<AnimPair> { Frame = offset, Value = pair, InterpolationOut = interp });
                clip.PositionTrack = track;
                return EditResult.Success();
            }
            case "crop.left":
            case "crop.top":
            case "crop.right":
            case "crop.bottom":
            {
                var track = clip.CropTrack ?? new KeyframeTrack<Crop>();
                var existing = track.Keyframes.FirstOrDefault(k => k.Frame == offset);
                var crop = CloneCrop(existing?.Value
                    ?? KeyframeMath.Sample(clip.CropTrack, offset, clip.Crop));
                SetCropComponent(crop, ch, value);
                track.Upsert(new Keyframe<Crop> { Frame = offset, Value = crop, InterpolationOut = interp });
                clip.CropTrack = track;
                return EditResult.Success();
            }
            default:
                return EditResult.Fail("Unsupported channel.");
        }
    }

    private static EditResult MovePairScalarKey(
        Clip clip, string property, string channel, int fromOffset, int toOffset, double value, Interpolation interpolation)
    {
        var track = GetPairTrack(clip, property);
        var source = track?.Keyframes.FirstOrDefault(k => k.Frame == fromOffset);
        if (source is null) return EditResult.Fail("Keyframe not found.");

        var pair = new AnimPair(source.Value.A, source.Value.B);
        if (channel.EndsWith(".a", StringComparison.Ordinal))
            pair.A = property == "scale" ? Math.Clamp(value, 1e-4, 16.0) : value;
        else
            pair.B = property == "scale" ? Math.Clamp(value, 1e-4, 16.0) : value;

        if (fromOffset != toOffset)
            track!.Keyframes.RemoveAll(k => k.Frame == fromOffset);
        track!.Upsert(new Keyframe<AnimPair> { Frame = toOffset, Value = pair, InterpolationOut = interpolation });
        SetPairTrack(clip, property, track);
        return EditResult.Success();
    }

    private static EditResult MoveCropScalarKey(
        Clip clip, string channel, int fromOffset, int toOffset, double value, Interpolation interpolation)
    {
        var track = clip.CropTrack;
        var source = track?.Keyframes.FirstOrDefault(k => k.Frame == fromOffset);
        if (source is null) return EditResult.Fail("Keyframe not found.");

        var crop = new Crop
        {
            Left = source.Value.Left,
            Top = source.Value.Top,
            Right = source.Value.Right,
            Bottom = source.Value.Bottom,
        };
        SetCropComponent(crop, channel, value);
        if (fromOffset != toOffset)
            track!.Keyframes.RemoveAll(k => k.Frame == fromOffset);
        track!.Upsert(new Keyframe<Crop> { Frame = toOffset, Value = crop, InterpolationOut = interpolation });
        clip.CropTrack = track;
        return EditResult.Success();
    }

    private static void SetCropComponent(Crop crop, string channel, double value)
    {
        value = Math.Clamp(value, 0, 1);
        switch (channel)
        {
            case "crop.left": crop.Left = value; break;
            case "crop.top": crop.Top = value; break;
            case "crop.right": crop.Right = value; break;
            case "crop.bottom": crop.Bottom = value; break;
        }
    }

    private static string? NormalizeChannel(string channel)
    {
        var p = channel.Trim().ToLowerInvariant();
        return p switch
        {
            "opacity" or "volume" or "rotation"
                or "scale.a" or "scale.b" or "position.a" or "position.b"
                or "crop.left" or "crop.top" or "crop.right" or "crop.bottom" => p,
            "scale" => "scale.a",
            "position" => "position.a",
            "crop" => "crop.left",
            _ => null,
        };
    }

    // --- Internals ---

    private static EditResult WithUndo(ProjectSession session, Func<EditResult> action)
    {
        session.RecordBeforeEdit();
        var result = action();
        if (!result.Ok) session.RollbackLastRecord();
        return result;
    }

    private static EditResult FindEditableClip(ProjectSession session, string clipId, out Clip? clip)
    {
        clip = null;
        foreach (var track in session.ActiveTimeline.Tracks)
        {
            clip = track.Clips.FirstOrDefault(c => c.Id == clipId);
            if (clip is not null)
                return track.Locked ? EditResult.Fail(EditCommands.TrackLockedError) : EditResult.Success();
        }
        return EditResult.Fail($"Clip not found: {clipId}");
    }

    private static bool TryOffset(Clip clip, int timelineFrame, out int offset)
    {
        offset = timelineFrame - clip.StartFrame;
        return offset >= 0 && offset < clip.DurationFrames;
    }

    private static string? Normalize(string property)
    {
        var p = property.Trim().ToLowerInvariant();
        return p switch
        {
            "opacity" or "volume" or "rotation" or "scale" or "position" or "crop" => p,
            "scale.a" or "scale.b" => "scale",
            "position.a" or "position.b" => "position",
            _ => null,
        };
    }

    private static void UpsertDouble(Clip clip, string property, int offset, double value, Interpolation? interp = null)
    {
        var track = GetDoubleTrack(clip, property) ?? new KeyframeTrack<double>();
        var existing = track.Keyframes.FirstOrDefault(k => k.Frame == offset);
        track.Upsert(new Keyframe<double>
        {
            Frame = offset,
            Value = value,
            InterpolationOut = interp ?? existing?.InterpolationOut ?? Interpolation.smooth,
        });
        SetDoubleTrack(clip, property, track);
    }

    private static void UpsertPair(Clip clip, string property, int offset, AnimPair value, Interpolation? interp = null)
    {
        var track = GetPairTrack(clip, property) ?? new KeyframeTrack<AnimPair>();
        var existing = track.Keyframes.FirstOrDefault(k => k.Frame == offset);
        track.Upsert(new Keyframe<AnimPair>
        {
            Frame = offset,
            Value = value,
            InterpolationOut = interp ?? existing?.InterpolationOut ?? Interpolation.smooth,
        });
        SetPairTrack(clip, property, track);
    }

    private static void UpsertCrop(Clip clip, int offset, Crop value, Interpolation? interp = null)
    {
        var track = clip.CropTrack ?? new KeyframeTrack<Crop>();
        var existing = track.Keyframes.FirstOrDefault(k => k.Frame == offset);
        track.Upsert(new Keyframe<Crop>
        {
            Frame = offset,
            Value = value,
            InterpolationOut = interp ?? existing?.InterpolationOut ?? Interpolation.smooth,
        });
        clip.CropTrack = track;
    }

    private static int RemoveDoubleAt(Clip clip, string property, int offset)
    {
        var track = GetDoubleTrack(clip, property);
        if (track is null) return 0;
        var removed = track.Keyframes.RemoveAll(k => k.Frame == offset);
        if (removed == 0) return 0;
        SetDoubleTrack(clip, property, track.Keyframes.Count == 0 ? null : track);
        return removed;
    }

    private static int RemovePairAt(Clip clip, string property, int offset)
    {
        var track = GetPairTrack(clip, property);
        if (track is null) return 0;
        var removed = track.Keyframes.RemoveAll(k => k.Frame == offset);
        if (removed == 0) return 0;
        SetPairTrack(clip, property, track.Keyframes.Count == 0 ? null : track);
        return removed;
    }

    private static int RemoveCropAt(Clip clip, int offset)
    {
        var track = clip.CropTrack;
        if (track is null) return 0;
        var removed = track.Keyframes.RemoveAll(k => k.Frame == offset);
        if (removed == 0) return 0;
        clip.CropTrack = track.Keyframes.Count == 0 ? null : track;
        return removed;
    }

    private static KeyframeTrack<double>? GetDoubleTrack(Clip clip, string property) => property switch
    {
        "opacity" => clip.OpacityTrack,
        "volume" => clip.VolumeTrack,
        "rotation" => clip.RotationTrack,
        _ => null,
    };

    private static void SetDoubleTrack(Clip clip, string property, KeyframeTrack<double>? track)
    {
        switch (property)
        {
            case "opacity": clip.OpacityTrack = track; break;
            case "volume": clip.VolumeTrack = track; break;
            case "rotation": clip.RotationTrack = track; break;
        }
    }

    private static KeyframeTrack<AnimPair>? GetPairTrack(Clip clip, string property) => property switch
    {
        "scale" => clip.ScaleTrack,
        "position" => clip.PositionTrack,
        _ => null,
    };

    private static void SetPairTrack(Clip clip, string property, KeyframeTrack<AnimPair>? track)
    {
        switch (property)
        {
            case "scale": clip.ScaleTrack = track; break;
            case "position": clip.PositionTrack = track; break;
        }
    }

    private static Crop CloneCrop(Crop c) => new()
    {
        Left = c.Left,
        Top = c.Top,
        Right = c.Right,
        Bottom = c.Bottom,
    };
}
