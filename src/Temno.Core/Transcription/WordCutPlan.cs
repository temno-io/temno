using Temno.Core.Editing;
using Temno.Core.Session;

namespace Temno.Core.Transcription;

/// <summary>
/// Executable word-cut edit plan for one track: split clips at SplitFrames, then ripple-delete
/// Ranges. All values are absolute timeline frames; ranges are merged and half-open [start,end).
/// Mac equivalent applies via EditorViewModel.rippleDeleteRangesOnTrack (ToolExecutor+Words.swift).
/// </summary>
public sealed class WordCutPlan
{
    public WordCutPlan(int trackIndex, IReadOnlyList<FrameRange> ranges, IReadOnlyList<int> splitFrames)
    {
        TrackIndex = trackIndex;
        Ranges = ranges;
        SplitFrames = splitFrames;
    }

    public int TrackIndex { get; }

    public IReadOnlyList<FrameRange> Ranges { get; }

    /// <summary>Range boundaries strictly inside the source clip; split here before deleting.</summary>
    public IReadOnlyList<int> SplitFrames { get; }

    public bool IsEmpty => Ranges.Count == 0;

    public int RemovedFrames => Ranges.Sum(r => r.Length);

    /// <summary>
    /// Applies mid-clip holes as one undo step. <see cref="EditCommands.RippleDeleteRanges"/>
    /// splits interior boundaries before deleting so source trim stays aligned.
    /// </summary>
    public EditResult ApplyTo(ProjectSession session)
    {
        if (IsEmpty)
            return EditResult.Success();

        var timeline = session.ActiveTimeline;
        if (TrackIndex < 0 || TrackIndex >= timeline.Tracks.Count)
            return EditResult.Fail($"trackIndex {TrackIndex} out of range");

        return EditCommands.RippleDeleteRanges(
            session, TrackIndex, Ranges.Select(r => (r.Start, r.End)).ToList());
    }
}
