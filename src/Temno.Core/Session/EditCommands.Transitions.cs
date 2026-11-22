using Temno.Core.Models;

namespace Temno.Core.Session;

public static partial class EditCommands
{
    public static EditResult AddTransition(
        ProjectSession session,
        string clipIdA,
        string clipIdB,
        string? type,
        int durationFrames)
    {
        return ExecuteWithUndo(session, () =>
        {
            if (durationFrames <= 0)
                return EditResult.Fail("add_transition: durationFrames must be > 0");

            var timeline = session.ActiveTimeline;
            var pair = timeline.AdjacentVideoClipPair(clipIdA, clipIdB);
            if (pair == null)
            {
                return EditResult.Fail($"add_transition: clipIdA and clipIdB must be video/image clips on the same video track, with clipIdB starting exactly at clipIdA's end frame (clipIdA={clipIdA}, clipIdB={clipIdB}).");
            }
            if (IsClipOnLockedTrack(timeline, clipIdA) || IsClipOnLockedTrack(timeline, clipIdB))
                return EditResult.Fail(TrackLockedError);

            var tType = ParseTransitionType(type);
            if (tType is null)
            {
                var allowed = string.Join(", ", Enum.GetNames<TransitionType>()
                    .Select(n => char.ToLowerInvariant(n[0]) + n[1..]));
                return EditResult.Fail($"add_transition: unknown type '{type}'. Use one of: {allowed}");
            }

            timeline.Transitions.RemoveAll(t => t.ClipIdA == clipIdA && t.ClipIdB == clipIdB);

            var transition = new Transition
            {
                Id = Timeline.NewId(),
                ClipIdA = clipIdA,
                ClipIdB = clipIdB,
                Type = tType.Value,
                DurationFrames = durationFrames
            };

            timeline.Transitions.Add(transition);
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static EditResult RemoveTransition(ProjectSession session, string transitionId)
    {
        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            var idx = timeline.Transitions.FindIndex(t => t.Id == transitionId);
            if (idx < 0)
            {
                return EditResult.Fail($"remove_transition: transition not found: {transitionId}");
            }
            var transition = timeline.Transitions[idx];
            if (IsClipOnLockedTrack(timeline, transition.ClipIdA) || IsClipOnLockedTrack(timeline, transition.ClipIdB))
                return EditResult.Fail(TrackLockedError);

            timeline.Transitions.RemoveAt(idx);
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static IReadOnlyList<Transition> ListTransitions(ProjectSession session)
    {
        return session.ActiveTimeline.Transitions;
    }

    /// <summary>
    /// Parses a camelCase or PascalCase transition type string. Null/empty defaults to crossDissolve.
    /// </summary>
    private static TransitionType? ParseTransitionType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return TransitionType.CrossDissolve;

        return Enum.TryParse<TransitionType>(type, ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }
}
