using Temno.Core.Models;
using System.Text.Json;

namespace Temno.Core.Session;

public static partial class EditCommands
{
    public static EditResult SetKeyframes(
        ProjectSession session,
        string clipId,
        string property,
        IReadOnlyList<object> keyframeRows)
    {
        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            Clip? clip = null;
            Track? ownerTrack = null;
            foreach (var track in timeline.Tracks)
            {
                clip = track.Clips.FirstOrDefault(c => c.Id == clipId);
                if (clip != null)
                {
                    ownerTrack = track;
                    break;
                }
            }
            if (clip == null)
                return EditResult.Fail($"Clip not found: {clipId}");
            if (ownerTrack!.Locked)
                return EditResult.Fail(TrackLockedError);

            if (keyframeRows == null || keyframeRows.Count == 0)
            {
                switch (property.ToLowerInvariant())
                {
                    case "volume": clip.VolumeTrack = null; break;
                    case "opacity": clip.OpacityTrack = null; break;
                    case "rotation": clip.RotationTrack = null; break;
                    case "position": clip.PositionTrack = null; break;
                    case "scale": clip.ScaleTrack = null; break;
                    case "crop": clip.CropTrack = null; break;
                    default: return EditResult.Fail($"Unknown keyframe property: {property}");
                }
                session.MarkDirty();
                return EditResult.Success();
            }

            switch (property.ToLowerInvariant())
            {
                case "volume":
                    {
                        var track = ParseScalarTrack(keyframeRows);
                        clip.VolumeTrack = track.Keyframes.Count == 0 ? null : track;
                        break;
                    }
                case "opacity":
                    {
                        var track = ParseScalarTrack(keyframeRows);
                        clip.OpacityTrack = track.Keyframes.Count == 0 ? null : track;
                        break;
                    }
                case "rotation":
                    {
                        var track = ParseScalarTrack(keyframeRows);
                        clip.RotationTrack = track.Keyframes.Count == 0 ? null : track;
                        break;
                    }
                case "position":
                    {
                        var track = ParsePairTrack(keyframeRows);
                        clip.PositionTrack = track.Keyframes.Count == 0 ? null : track;
                        break;
                    }
                case "scale":
                    {
                        var track = ParsePairTrack(keyframeRows);
                        clip.ScaleTrack = track.Keyframes.Count == 0 ? null : track;
                        break;
                    }
                case "crop":
                    {
                        var track = ParseCropTrack(keyframeRows);
                        clip.CropTrack = track.Keyframes.Count == 0 ? null : track;
                        break;
                    }
                default:
                    return EditResult.Fail($"Unknown keyframe property: {property}");
            }

            session.MarkDirty();
            return EditResult.Success();
        });
    }

    private static int ParseInt(object raw)
    {
        if (raw is int i) return i;
        if (raw is long l) return (int)l;
        if (raw is double d) return (int)Math.Round(d);
        if (raw is float f) return (int)Math.Round(f);
        if (raw is string s && int.TryParse(s, out var parsed)) return parsed;
        if (raw is System.Text.Json.JsonElement element)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Number) return element.GetInt32();
            if (element.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(element.GetString(), out var ps)) return ps;
        }
        return Convert.ToInt32(raw);
    }

    private static double ParseDouble(object raw)
    {
        if (raw is double d) return d;
        if (raw is float f) return f;
        if (raw is int i) return i;
        if (raw is long l) return l;
        if (raw is string s && double.TryParse(s, out var parsed)) return parsed;
        if (raw is System.Text.Json.JsonElement element)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.Number) return element.GetDouble();
            if (element.ValueKind == System.Text.Json.JsonValueKind.String && double.TryParse(element.GetString(), out var ps)) return ps;
        }
        return Convert.ToDouble(raw);
    }

    private static Interpolation ParseInterpolation(object? raw)
    {
        if (raw == null) return Interpolation.smooth;
        var str = raw.ToString()?.ToLowerInvariant();
        if (str == "linear") return Interpolation.linear;
        if (str == "hold") return Interpolation.hold;
        return Interpolation.smooth;
    }

    private static IReadOnlyList<object> ConvertToReadOnlyList(object rawRow)
    {
        if (rawRow is IReadOnlyList<object> list) return list;
        if (rawRow is System.Text.Json.JsonElement element && element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var result = new List<object>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    if (item.TryGetInt64(out var l)) result.Add(l);
                    else result.Add(item.GetDouble());
                }
                else if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    result.Add(item.GetString() ?? "");
                }
                else if (item.ValueKind == System.Text.Json.JsonValueKind.True)
                {
                    result.Add(true);
                }
                else if (item.ValueKind == System.Text.Json.JsonValueKind.False)
                {
                    result.Add(false);
                }
                else
                {
                    result.Add(item);
                }
            }
            return result;
        }
        if (rawRow is System.Collections.IEnumerable enumerable)
        {
            var result = new List<object>();
            foreach (var item in enumerable)
            {
                result.Add(item);
            }
            return result;
        }
        throw new ArgumentException("Keyframe row must be an array/list");
    }

    private static KeyframeTrack<double> ParseScalarTrack(IReadOnlyList<object> rows)
    {
        var track = new KeyframeTrack<double>();
        foreach (var rawRow in rows)
        {
            var row = ConvertToReadOnlyList(rawRow);
            if (row.Count < 2) continue;
            var frame = ParseInt(row[0]);
            var val = ParseDouble(row[1]);
            var interp = ParseInterpolation(row.Count > 2 ? row[2] : null);
            track.Upsert(new Keyframe<double> { Frame = frame, Value = val, InterpolationOut = interp });
        }
        return track;
    }

    private static KeyframeTrack<AnimPair> ParsePairTrack(IReadOnlyList<object> rows)
    {
        var track = new KeyframeTrack<AnimPair>();
        foreach (var rawRow in rows)
        {
            var row = ConvertToReadOnlyList(rawRow);
            if (row.Count < 3) continue;
            var frame = ParseInt(row[0]);
            var a = ParseDouble(row[1]);
            var b = ParseDouble(row[2]);
            var interp = ParseInterpolation(row.Count > 3 ? row[3] : null);
            track.Upsert(new Keyframe<AnimPair> { Frame = frame, Value = new AnimPair(a, b), InterpolationOut = interp });
        }
        return track;
    }

    private static KeyframeTrack<Crop> ParseCropTrack(IReadOnlyList<object> rows)
    {
        var track = new KeyframeTrack<Crop>();
        foreach (var rawRow in rows)
        {
            var row = ConvertToReadOnlyList(rawRow);
            if (row.Count < 5) continue;
            var frame = ParseInt(row[0]);
            var top = ParseDouble(row[1]);
            var right = ParseDouble(row[2]);
            var bottom = ParseDouble(row[3]);
            var left = ParseDouble(row[4]);
            var interp = ParseInterpolation(row.Count > 5 ? row[5] : null);
            track.Upsert(new Keyframe<Crop> { Frame = frame, Value = new Crop { Top = top, Right = right, Bottom = bottom, Left = left }, InterpolationOut = interp });
        }
        return track;
    }
}
