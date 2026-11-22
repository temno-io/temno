using Temno.Core.Models;

namespace Temno.Core.Audio;

/// <summary>
/// Creates Mac-compatible volume-track keyframes that lower a background lane while the selected
/// voice lane has timeline coverage. This intentionally uses clip coverage, not speech analysis:
/// VAD and transcript-triggered ducking need their own evidence-backed packages.
/// </summary>
public static class AudioDuckEnvelope
{
    public const double DefaultAttackSeconds = 0.12;
    public const double DefaultReleaseSeconds = 0.30;

    public static AudioDuckPlan CreatePlan(
        Timeline timeline,
        int voiceTrackIndex,
        int backgroundTrackIndex,
        double reductionDb,
        double attackSeconds = DefaultAttackSeconds,
        double releaseSeconds = DefaultReleaseSeconds)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.Fps <= 0) throw new ArgumentOutOfRangeException(nameof(timeline), "Timeline fps must be positive.");
        if (voiceTrackIndex < 0 || voiceTrackIndex >= timeline.Tracks.Count)
            throw new ArgumentOutOfRangeException(nameof(voiceTrackIndex));
        if (backgroundTrackIndex < 0 || backgroundTrackIndex >= timeline.Tracks.Count)
            throw new ArgumentOutOfRangeException(nameof(backgroundTrackIndex));
        if (voiceTrackIndex == backgroundTrackIndex)
            throw new ArgumentException("Voice and background tracks must differ.", nameof(backgroundTrackIndex));
        if (!double.IsFinite(reductionDb) || reductionDb < 0 || reductionDb > 60)
            throw new ArgumentOutOfRangeException(nameof(reductionDb), "Reduction must be between 0 and 60 dB.");
        if (!double.IsFinite(attackSeconds) || attackSeconds < 0 || attackSeconds > 60)
            throw new ArgumentOutOfRangeException(nameof(attackSeconds), "Attack must be between 0 and 60 seconds.");
        if (!double.IsFinite(releaseSeconds) || releaseSeconds < 0 || releaseSeconds > 60)
            throw new ArgumentOutOfRangeException(nameof(releaseSeconds), "Release must be between 0 and 60 seconds.");

        var voiceTrack = timeline.Tracks[voiceTrackIndex];
        var backgroundTrack = timeline.Tracks[backgroundTrackIndex];
        if (voiceTrack.Type != ClipType.audio)
            throw new ArgumentException("Voice track must be an audio track.", nameof(voiceTrackIndex));
        if (backgroundTrack.Type != ClipType.audio)
            throw new ArgumentException("Background track must be an audio track.", nameof(backgroundTrackIndex));
        if (voiceTrack.Hidden || voiceTrack.Muted)
            throw new InvalidOperationException("Voice track is muted or hidden, so it has no audible coverage.");

        var attackFrames = SecondsToFrames(attackSeconds, timeline.Fps);
        var releaseFrames = SecondsToFrames(releaseSeconds, timeline.Fps);
        var intervals = MergeCoverage(
            voiceTrack.Clips
                .Where(clip => clip.DurationFrames > 0)
                .Select(clip => new AudioDuckInterval(clip.StartFrame, clip.EndFrame)),
            attackFrames,
            releaseFrames);
        if (intervals.Count == 0)
            throw new InvalidOperationException("Voice track has no positive-duration clips to drive ducking.");

        return new AudioDuckPlan(timeline.Fps, reductionDb, attackFrames, releaseFrames, intervals);
    }

    /// <summary>Overlays this plan on a clip's existing dB envelope and returns whether it changed.</summary>
    public static bool ApplyToClip(Clip backgroundClip, AudioDuckPlan plan)
    {
        ArgumentNullException.ThrowIfNull(backgroundClip);
        ArgumentNullException.ThrowIfNull(plan);
        if (backgroundClip.DurationFrames <= 0 || plan.ReductionDb <= 0)
            return false;

        var clipStart = backgroundClip.StartFrame;
        var clipEnd = backgroundClip.EndFrame;
        if (!plan.Intervals.Any(interval =>
                interval.EndFrame + plan.ReleaseFrames > clipStart
                && interval.StartFrame - plan.AttackFrames < clipEnd))
            return false;

        var existing = backgroundClip.VolumeTrack;
        var points = new SortedSet<int> { 0, backgroundClip.DurationFrames };
        if (existing is { IsActive: true })
        {
            foreach (var keyframe in existing.Keyframes)
                if (keyframe.Frame >= 0 && keyframe.Frame <= backgroundClip.DurationFrames)
                    points.Add(keyframe.Frame);
        }

        foreach (var interval in plan.Intervals)
        {
            AddLocalPoint(points, interval.StartFrame - plan.AttackFrames, backgroundClip);
            AddLocalPoint(points, interval.StartFrame, backgroundClip);
            AddLocalPoint(points, interval.EndFrame, backgroundClip);
            AddLocalPoint(points, interval.EndFrame + plan.ReleaseFrames, backgroundClip);
        }

        var output = new KeyframeTrack<double>();
        foreach (var localFrame in points)
        {
            var baseDb = KeyframeMath.Sample(existing, localFrame, 0);
            var duckDb = plan.DuckDbAt(clipStart + localFrame);
            output.Upsert(new Keyframe<double>
            {
                Frame = localFrame,
                Value = Math.Max(VolumeScale.FloorDb, baseDb + duckDb),
                InterpolationOut = Interpolation.linear,
            });
        }

        backgroundClip.VolumeTrack = output;
        return true;
    }

    private static void AddLocalPoint(SortedSet<int> points, int timelineFrame, Clip clip)
        => points.Add(Math.Clamp(timelineFrame - clip.StartFrame, 0, clip.DurationFrames));

    private static int SecondsToFrames(double seconds, int fps)
        => Math.Max(0, (int)Math.Round(seconds * fps, MidpointRounding.AwayFromZero));

    private static IReadOnlyList<AudioDuckInterval> MergeCoverage(
        IEnumerable<AudioDuckInterval> source,
        int attackFrames,
        int releaseFrames)
    {
        var sorted = source
            .Where(interval => interval.EndFrame > interval.StartFrame)
            .OrderBy(interval => interval.StartFrame)
            .ToList();
        if (sorted.Count == 0)
            return [];

        var merged = new List<AudioDuckInterval> { sorted[0] };
        foreach (var interval in sorted.Skip(1))
        {
            var previous = merged[^1];
            if (interval.StartFrame <= previous.EndFrame + attackFrames + releaseFrames)
            {
                merged[^1] = previous with { EndFrame = Math.Max(previous.EndFrame, interval.EndFrame) };
            }
            else
            {
                merged.Add(interval);
            }
        }

        return merged;
    }
}

public sealed class AudioDuckPlan
{
    internal AudioDuckPlan(
        int fps,
        double reductionDb,
        int attackFrames,
        int releaseFrames,
        IReadOnlyList<AudioDuckInterval> intervals)
    {
        Fps = fps;
        ReductionDb = reductionDb;
        AttackFrames = attackFrames;
        ReleaseFrames = releaseFrames;
        Intervals = intervals;
    }

    public int Fps { get; }
    public double ReductionDb { get; }
    public int AttackFrames { get; }
    public int ReleaseFrames { get; }
    public IReadOnlyList<AudioDuckInterval> Intervals { get; }

    /// <summary>Returns an additive dB reduction at a timeline frame.</summary>
    public double DuckDbAt(int frame)
    {
        foreach (var interval in Intervals)
        {
            var attackStart = interval.StartFrame - AttackFrames;
            if (frame < attackStart || frame > interval.EndFrame + ReleaseFrames)
                continue;

            if (frame < interval.StartFrame)
            {
                if (AttackFrames == 0) return -ReductionDb;
                return -ReductionDb * (frame - attackStart) / AttackFrames;
            }

            if (frame <= interval.EndFrame)
                return -ReductionDb;

            if (ReleaseFrames == 0) return 0;
            return -ReductionDb * (1 - (frame - interval.EndFrame) / (double)ReleaseFrames);
        }

        return 0;
    }
}

public readonly record struct AudioDuckInterval(int StartFrame, int EndFrame);
