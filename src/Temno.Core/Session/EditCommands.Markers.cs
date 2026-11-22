using Temno.Core.Models;

namespace Temno.Core.Session;

public static partial class EditCommands
{
    public static EditResult AddMarker(ProjectSession session, int frame, string? name = null, string? color = null, string? comment = null)
    {
        return ExecuteWithUndo(session, () =>
        {
            session.ActiveTimeline.Markers.Add(new Marker
            {
                Frame = frame,
                Name = name ?? "Marker",
                Color = color ?? "blue",
                Note = comment
            });
            session.MarkDirty();
            return EditResult.Success();
        });
    }

    /// <summary>
    /// Replaces every marker with <paramref name="name"/> in one undo step.
    /// Pass an empty frame list to clear them (Mark Silence off).
    /// </summary>
    public static EditResult ReplaceMarkersNamed(
        ProjectSession session,
        string name,
        IReadOnlyList<int> frames,
        string color = "gray",
        string? comment = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(frames);

        return ExecuteWithUndo(session, () =>
        {
            var markers = session.ActiveTimeline.Markers;
            markers.RemoveAll(m => string.Equals(m.Name, name, StringComparison.Ordinal));
            foreach (var frame in frames.Distinct().OrderBy(f => f))
            {
                markers.Add(new Marker
                {
                    Frame = Math.Max(0, frame),
                    Name = name,
                    Color = color,
                    Note = comment,
                });
            }

            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static EditResult UpdateMarker(
        ProjectSession session,
        string markerId,
        int? frame,
        string? name,
        string? color,
        string? comment)
    {
        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            var marker = timeline.Markers.FirstOrDefault(m => m.Id == markerId);
            if (marker == null)
                return EditResult.Fail($"Marker not found: {markerId}");

            if (frame != null) marker.Frame = frame.Value;
            if (name != null) marker.Name = name;
            if (color != null) marker.Color = color;
            if (comment != null) marker.Note = comment;

            session.MarkDirty();
            return EditResult.Success();
        });
    }

    public static EditResult RemoveMarker(ProjectSession session, string markerId)
    {
        return ExecuteWithUndo(session, () =>
        {
            var timeline = session.ActiveTimeline;
            var marker = timeline.Markers.FirstOrDefault(m => m.Id == markerId);
            if (marker == null)
                return EditResult.Fail($"Marker not found: {markerId}");

            timeline.Markers.Remove(marker);
            session.MarkDirty();
            return EditResult.Success();
        });
    }
}
