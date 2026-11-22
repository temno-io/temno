namespace Temno.Core.Editing;

/// <summary>Mac: Editor/OverwriteEngine.swift</summary>
public static class OverwriteEngine
{
    public abstract record Action
    {
        public sealed record Remove(string ClipId) : Action;
        public sealed record TrimEnd(string ClipId, int NewDuration) : Action;
        public sealed record TrimStart(string ClipId, int NewStartFrame, int NewTrimStart, int NewDuration) : Action;
        public sealed record Split(
            string ClipId,
            int LeftDuration,
            string RightId,
            int RightStartFrame,
            int RightTrimStart,
            int RightDuration) : Action;
    }

    public readonly record struct ClipSpan(
        string Id,
        int StartFrame,
        int EndFrame,
        int TrimStartFrame,
        double Speed);

    /// <summary>
    /// Region is half-open [regionStart, regionEnd).
    /// </summary>
    public static IReadOnlyList<Action> ComputeOverwrite(
        IReadOnlyList<ClipSpan> clips,
        int regionStart,
        int regionEnd)
    {
        if (regionEnd <= regionStart) return [];

        var actions = new List<Action>();
        foreach (var clip in clips)
        {
            var cs = clip.StartFrame;
            var ce = clip.EndFrame;

            if (ce <= regionStart || cs >= regionEnd)
                continue;

            if (cs >= regionStart && ce <= regionEnd)
            {
                actions.Add(new Action.Remove(clip.Id));
            }
            else if (cs < regionStart && ce > regionEnd)
            {
                var leftDuration = regionStart - cs;
                var rightStartFrame = regionEnd;
                var rightTrimStart = clip.TrimStartFrame
                    + (int)Math.Round((regionEnd - cs) * clip.Speed);
                var rightDuration = ce - regionEnd;
                actions.Add(new Action.Split(
                    clip.Id,
                    leftDuration,
                    Guid.NewGuid().ToString().ToUpperInvariant(),
                    rightStartFrame,
                    rightTrimStart,
                    rightDuration));
            }
            else if (cs < regionStart)
            {
                var newDuration = regionStart - cs;
                actions.Add(new Action.TrimEnd(clip.Id, newDuration));
            }
            else
            {
                var trimAmount = regionEnd - cs;
                var newStartFrame = regionEnd;
                var newTrimStart = clip.TrimStartFrame
                    + (int)Math.Round(trimAmount * clip.Speed);
                var newDuration = ce - regionEnd;
                actions.Add(new Action.TrimStart(
                    clip.Id,
                    newStartFrame,
                    newTrimStart,
                    newDuration));
            }
        }

        return actions;
    }
}
