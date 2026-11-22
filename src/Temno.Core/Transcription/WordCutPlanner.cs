using Temno.Core.Editing;
using Temno.Core.Models;

namespace Temno.Core.Transcription;

/// <summary>Mac: Transcription/WordCutPlanner.swift — raw values are the serialized strings.</summary>
public enum CutAggressiveness
{
    tight,
    balanced,
    loose,
}

/// <summary>A transcript word mapped onto the timeline (absolute frames, half-open [start,end)).</summary>
public readonly record struct MappedWord(
    string Text,
    int StartFrame,
    int EndFrame,
    double SourceStart,
    double SourceEnd,
    string? Speaker);

/// <summary>
/// Mac: Transcription/WordCutPlanner.swift (cut ranges) +
/// Agent/Tools/ToolExecutor+Transcription.swift (source-seconds -> timeline-frame mapping).
/// </summary>
public static class WordCutPlanner
{
    public readonly record struct Word(int StartFrame, int EndFrame, bool Selected);

    public static double KeptGapMs(CutAggressiveness aggressiveness) => aggressiveness switch
    {
        CutAggressiveness.tight => 60,
        CutAggressiveness.loose => 320,
        _ => 150,
    };

    public static int KeepGapFrames(CutAggressiveness aggressiveness, int fps)
    {
        // Mac msToFrames uses Swift .rounded() = midpoint away from zero.
        return (int)Math.Round(KeptGapMs(aggressiveness) / 1000.0 * fps, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// For each contiguous run of selected words, cut from the previous word's end to the next
    /// word's start (bounded by the clip span), keeping keepGapFrames/2 of pause on each side.
    /// All frames are absolute timeline frames; returned ranges are merged and half-open.
    /// </summary>
    public static List<FrameRange> CutRanges(
        IReadOnlyList<Word> words,
        int clipStart,
        int clipEnd,
        int keepGapFrames)
    {
        var valid = words.Where(w => w.EndFrame > w.StartFrame).ToList();
        if (clipEnd <= clipStart || valid.Count == 0)
            return [];

        var half = Math.Max(0, keepGapFrames / 2);
        var ranges = new List<FrameRange>();
        var k = 0;
        while (k < valid.Count)
        {
            if (!valid[k].Selected)
            {
                k++;
                continue;
            }

            var l = k;
            while (l + 1 < valid.Count && valid[l + 1].Selected)
                l++;

            var left = k > 0 ? valid[k - 1].EndFrame : clipStart;
            var right = l + 1 < valid.Count ? valid[l + 1].StartFrame : clipEnd;
            var runStart = valid[k].StartFrame;
            var runEnd = valid[l].EndFrame;
            var keepBefore = Math.Min(Math.Max(0, runStart - left), half);
            var keepAfter = Math.Min(Math.Max(0, right - runEnd), half);
            var start = Math.Max(clipStart, Math.Min(left + keepBefore, runStart));
            var end = Math.Min(clipEnd, Math.Max(runEnd, right - keepAfter));
            if (end > start)
                ranges.Add(new FrameRange(start, end));
            k = l + 1;
        }

        return RippleEngine.MergeRanges(ranges);
    }

    /// <summary>
    /// Maps transcript words (source SECONDS) onto absolute timeline FRAMES through the clip's
    /// placement, trim and speed. Words without timestamps or whose mid-time falls outside the
    /// clip's visible source span are dropped. Sorted by (StartFrame, EndFrame).
    /// </summary>
    public static List<MappedWord> MapToTimeline(
        Clip clip,
        int fps,
        IReadOnlyList<TranscriptionWord> words)
    {
        var rate = (double)fps;
        var speed = Math.Max(clip.Speed, 0.0001);
        // Visible source span in source frames: Mac CaptionTranscriptMapper.sourceSpan.
        var visibleStart = (double)clip.TrimStartFrame;
        var visibleEnd = visibleStart + clip.DurationFrames * speed;

        var mapped = new List<MappedWord>();
        foreach (var word in words)
        {
            if (word.Start is not double start || word.End is not double end)
                continue;

            var midFrame = (start + end) / 2.0 * rate;
            if (midFrame < visibleStart || midFrame >= visibleEnd)
                continue;

            var startFrame = Math.Max(start * rate, visibleStart);
            var endFrame = Math.Min(end * rate, visibleEnd);
            if (endFrame <= startFrame)
                continue;

            // Mac spanFrames uses Swift .rounded() = midpoint away from zero.
            int ToTimeline(double sourceFrame) => (int)Math.Round(
                clip.StartFrame + (sourceFrame - visibleStart) / speed,
                MidpointRounding.AwayFromZero);

            var mappedStart = ToTimeline(startFrame);
            var mappedEnd = Math.Max(mappedStart, ToTimeline(endFrame));
            mapped.Add(new MappedWord(word.Text, mappedStart, mappedEnd, start, end, word.Speaker));
        }

        mapped.Sort((a, b) => a.StartFrame != b.StartFrame
            ? a.StartFrame.CompareTo(b.StartFrame)
            : a.EndFrame.CompareTo(b.EndFrame));
        return mapped;
    }

    /// <summary>
    /// Plans the removal of the transcript words whose mid-time (source seconds) falls inside any
    /// of removeRanges. Returns cut ranges in absolute timeline frames plus the split frames
    /// needed so every cut range lands on clip boundaries before ripple-deleting.
    /// </summary>
    public static WordCutPlan PlanRemoval(
        Clip clip,
        int trackIndex,
        int fps,
        IReadOnlyList<TranscriptionWord> transcriptWords,
        IReadOnlyList<(double Start, double End)> removeRanges,
        CutAggressiveness aggressiveness = CutAggressiveness.balanced)
    {
        var mapped = MapToTimeline(clip, fps, transcriptWords);
        var planWords = mapped.Select(m =>
        {
            var mid = (m.SourceStart + m.SourceEnd) / 2.0;
            var selected = removeRanges.Any(r => mid >= r.Start && mid <= r.End);
            return new Word(m.StartFrame, m.EndFrame, selected);
        }).ToList();

        var ranges = CutRanges(planWords, clip.StartFrame, clip.EndFrame,
            KeepGapFrames(aggressiveness, fps));

        // SplitFrames stay on the plan for callers that inspect the cut; RippleDeleteRanges
        // now splits interior boundaries itself in one undo step.
        var splits = new SortedSet<int>();
        foreach (var range in ranges)
        {
            if (range.Start > clip.StartFrame)
                splits.Add(range.Start);
            if (range.End < clip.EndFrame)
                splits.Add(range.End);
        }

        return new WordCutPlan(trackIndex, ranges, splits.ToList());
    }
}
