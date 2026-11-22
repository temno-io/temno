namespace Temno.Core.Editing;

public readonly record struct ClipShift(string ClipId, int NewStartFrame);

public readonly record struct FrameRange(int Start, int End)
{
    public int Length => End - Start;
}

/// <summary>Mac: Editor/RippleEngine.swift — pure functions.</summary>
public static class RippleEngine
{
    public static IReadOnlyList<ClipShift> ComputeRippleShifts(
        IReadOnlyList<(string Id, int StartFrame, int EndFrame)> clips,
        IReadOnlySet<string> removedIds)
    {
        var removedRanges = clips
            .Where(c => removedIds.Contains(c.Id))
            .Select(c => new FrameRange(c.StartFrame, c.EndFrame))
            .ToList();

        var remaining = clips.Where(c => !removedIds.Contains(c.Id)).ToList();
        return ComputeRippleShiftsForRanges(remaining, removedRanges);
    }

    public static IReadOnlyList<ClipShift> ComputeRippleShiftsForRanges(
        IReadOnlyList<(string Id, int StartFrame, int EndFrame)> clips,
        IReadOnlyList<FrameRange> removedRanges)
    {
        var merged = MergeRanges(removedRanges);
        if (merged.Count == 0)
            return [];

        var shifts = new List<ClipShift>();
        foreach (var clip in clips.OrderBy(c => c.StartFrame))
        {
            var shift = merged
                .Where(r => r.End <= clip.StartFrame)
                .Sum(r => r.Length);
            if (shift > 0)
                shifts.Add(new ClipShift(clip.Id, clip.StartFrame - shift));
        }
        return shifts;
    }

    public static IReadOnlyList<ClipShift> ComputeRipplePush(
        IReadOnlyList<(string Id, int StartFrame)> clips,
        int insertFrame,
        int pushAmount,
        IReadOnlySet<string>? excludeIds = null)
    {
        excludeIds ??= new HashSet<string>();
        return clips
            .Where(c => !excludeIds.Contains(c.Id) && c.StartFrame >= insertFrame)
            .Select(c => new ClipShift(c.Id, c.StartFrame + pushAmount))
            .ToList();
    }

    public static List<FrameRange> MergeRanges(IReadOnlyList<FrameRange> ranges)
    {
        var sorted = ranges.OrderBy(r => r.Start).ToList();
        var merged = new List<FrameRange>();
        foreach (var range in sorted)
        {
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = new FrameRange(last.Start, Math.Max(last.End, range.End));
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }
}
