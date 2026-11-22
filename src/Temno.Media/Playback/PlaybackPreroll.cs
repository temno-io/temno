using Temno.Core.IO;
using Temno.Core.Models;

namespace Temno.Media.Playback;

/// <summary>
/// One decode that playback will need soon: <see cref="SourceSeconds"/> of <see cref="MediaPath"/>,
/// first asked for at timeline frame <see cref="NeededAtFrame"/>.
/// </summary>
public readonly record struct PrerollTarget(string MediaPath, double SourceSeconds, int NeededAtFrame);

/// <summary>
/// WP-248. The decodes that playback is about to need and is not making yet: the first source
/// position of every video clip that becomes visible inside the lookahead window. The preview starts
/// a decoder at each one ahead of time, because a decoder started at the cut (probe, spawn, D3D11
/// init, seek) held the picture for 377–685 ms in the running app while audio played on.
///
/// The position is the one <see cref="TimelineFrameRenderer"/> will request, from the same
/// arithmetic: <see cref="TimelineFrameResolver.SourceSecondsAt"/> at the clip's start frame. A clip
/// entering through a transition is first decoded at the transition's start, still at its own start
/// frame (the renderer clamps), so that is when it is needed. Clips are taken with the resolver's
/// own visibility rules: hidden tracks and clips that start inside their predecessor never show.
/// Nested sequences and stills are not decoded through a playback pipe and are not planned.
/// </summary>
public static class PlaybackPreroll
{
    /// <summary>
    /// How far ahead to start. More than twice the worst cold start measured, so a start that runs
    /// slow under decode load still lands before its cut.
    /// </summary>
    public const double LeadSeconds = 1.5;

    public static int LeadFrames(int fps) => (int)Math.Ceiling(LeadSeconds * Math.Max(1, fps));

    /// <summary>
    /// Targets needed after <paramref name="frame"/> and no later than
    /// <paramref name="frame"/> + <paramref name="lookaheadFrames"/>, soonest first.
    /// </summary>
    public static IReadOnlyList<PrerollTarget> Plan(
        Timeline timeline, int frame, int lookaheadFrames, MediaResolver? mediaResolver)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (timeline.Fps <= 0 || lookaheadFrames <= 0)
            return [];

        var last = frame + lookaheadFrames;
        Dictionary<string, int>? transitionLead = null;
        foreach (var rt in timeline.ResolvedTransitions)
        {
            transitionLead ??= new Dictionary<string, int>(StringComparer.Ordinal);
            transitionLead[rt.ClipB.Id] = rt.Frames / 2;
        }

        var targets = new List<PrerollTarget>();
        foreach (var track in timeline.Tracks)
        {
            if (track.Type != ClipType.video || track.Hidden) continue;

            Clip? previous = null;
            foreach (var clip in track.Clips.OrderBy(c => c.StartFrame))
            {
                if (previous is not null && clip.StartFrame < previous.EndFrame) continue;
                var before = previous;
                previous = clip;
                if (clip.MediaType != ClipType.video || clip.DurationFrames <= 0) continue;

                var lead = transitionLead is not null && transitionLead.TryGetValue(clip.Id, out var frames) ? frames : 0;
                var needed = clip.StartFrame - lead;
                if (needed <= frame || needed > last) continue;

                var seconds = TimelineFrameResolver.SourceSecondsAt(clip, clip.StartFrame, timeline.Fps);
                if (lead == 0 && ContinuesSource(before, clip, seconds, timeline.Fps)) continue;

                var path = mediaResolver?.ResolvePath(clip.MediaRef);
                if (string.IsNullOrEmpty(path)) continue;

                targets.Add(new PrerollTarget(path, seconds, needed));
            }
        }

        targets.Sort(static (a, b) => a.NeededAtFrame.CompareTo(b.NeededAtFrame));
        return targets;
    }

    /// <summary>
    /// A split with nothing removed: the clip before it plays the same media, at the same speed,
    /// and ends exactly where this one starts in the source. The decoder playing that clip reads
    /// straight on into this one, so starting a second one would only cost a process.
    /// </summary>
    private static bool ContinuesSource(Clip? before, Clip clip, double startSeconds, int fps)
        => before is not null
           && before.EndFrame == clip.StartFrame
           && before.MediaType == ClipType.video
           && string.Equals(before.MediaRef, clip.MediaRef, StringComparison.Ordinal)
           && before.Speed.Equals(clip.Speed)
           && Math.Abs(TimelineFrameResolver.SourceSecondsAt(before, clip.StartFrame, fps) - startSeconds) < 0.5 / fps;
}
