using System.Text.Json;
using Temno.Core.Models;

namespace Temno.Core.Session;

/// <summary>
/// Multi-timeline operations (add / switch / rename). Mirrors MCP create_timeline /
/// set_active_timeline plus rename for the WP-75 tab bar.
/// </summary>
public static class TimelineCommands
{
    public static EditResult AddTimeline(
        ProjectSession session,
        string? name,
        string? duplicateFromId,
        out string createdId)
    {
        ArgumentNullException.ThrowIfNull(session);
        createdId = "";

        // WP-177: adding to the shared Timelines list is a structural change to a collection that
        // renders, exports and audio decodes read from their own threads. The MCP path already
        // takes this gate (ToolExecutor.Execute serializes create_timeline); the New Timeline
        // button did not, which left the readers nothing to synchronise against.
        var id = "";
        var result = ProjectHub.RunUnderGate(() => AddTimelineUnderGate(session, name, duplicateFromId, out id));
        createdId = id;
        return result;
    }

    private static EditResult AddTimelineUnderGate(
        ProjectSession session,
        string? name,
        string? duplicateFromId,
        out string createdId)
    {
        createdId = "";
        session.RecordBeforeEdit();
        try
        {
            Timeline created;
            if (!string.IsNullOrWhiteSpace(duplicateFromId))
            {
                var source = session.ProjectFile.Timelines.FirstOrDefault(t => t.Id == duplicateFromId);
                if (source is null)
                {
                    session.RollbackLastRecord();
                    return EditResult.Fail($"No timeline with id '{duplicateFromId}'.");
                }

                var json = JsonSerializer.Serialize(source, JsonOptions.ForProject);
                created = JsonSerializer.Deserialize<Timeline>(json, JsonOptions.ForProject)
                    ?? throw new InvalidOperationException("Failed to clone timeline");
                created.Id = Timeline.NewId();
                foreach (var track in created.Tracks)
                {
                    track.Id = Timeline.NewId();
                    foreach (var clip in track.Clips)
                        clip.Id = Timeline.NewId();
                }

                created.Name = string.IsNullOrWhiteSpace(name)
                    ? $"{source.Name} copy"
                    : name.Trim();
            }
            else
            {
                var active = session.ActiveTimeline;
                created = new Timeline
                {
                    Id = Timeline.NewId(),
                    Name = string.IsNullOrWhiteSpace(name)
                        ? $"Timeline {session.ProjectFile.Timelines.Count + 1}"
                        : name.Trim(),
                    Fps = active.Fps,
                    Width = active.Width,
                    Height = active.Height,
                    SettingsConfigured = active.SettingsConfigured,
                    Tracks =
                    [
                        new Track { Type = ClipType.video },
                        new Track { Type = ClipType.video },
                        new Track { Type = ClipType.audio },
                        new Track { Type = ClipType.audio },
                    ],
                };
            }

            session.ProjectFile.Timelines.Add(created);
            session.ProjectFile.ActiveTimelineId = created.Id;
            if (session.ProjectFile.OpenTimelineIds is { } open && !open.Contains(created.Id))
                open.Add(created.Id);
            createdId = created.Id;
            session.MarkDirty();
            return EditResult.Success();
        }
        catch
        {
            session.RollbackLastRecord();
            throw;
        }
    }

    public static EditResult SetActiveTimeline(ProjectSession session, string timelineId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(timelineId))
            return EditResult.Fail("timelineId is required");

        var target = session.ProjectFile.Timelines.FirstOrDefault(t => t.Id == timelineId);
        if (target is null)
            return EditResult.Fail($"No timeline with id '{timelineId}'.");

        if (session.ProjectFile.ActiveTimelineId == target.Id)
        {
            if (session.ProjectFile.OpenTimelineIds is { } openNow && !openNow.Contains(target.Id))
            {
                openNow.Add(target.Id);
                session.MarkDirty();
            }
            return EditResult.Success();
        }

        // Switching active timeline is not an undoable edit on Mac; mark dirty only.
        session.ProjectFile.ActiveTimelineId = target.Id;
        if (session.ProjectFile.OpenTimelineIds is { } open && !open.Contains(target.Id))
            open.Add(target.Id);
        session.MarkDirty();
        return EditResult.Success();
    }

    public static EditResult RenameTimeline(ProjectSession session, string timelineId, string newName)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(timelineId))
            return EditResult.Fail("timelineId is required");
        if (string.IsNullOrWhiteSpace(newName))
            return EditResult.Fail("name is required");

        var target = session.ProjectFile.Timelines.FirstOrDefault(t => t.Id == timelineId);
        if (target is null)
            return EditResult.Fail($"No timeline with id '{timelineId}'.");

        var trimmed = newName.Trim();
        if (target.Name == trimmed)
            return EditResult.Success();

        session.RecordBeforeEdit();
        try
        {
            target.Name = trimmed;
            session.MarkDirty();
            return EditResult.Success();
        }
        catch
        {
            session.RollbackLastRecord();
            throw;
        }
    }

    /// <summary>
    /// Ensures OpenTimelineIds is non-null and contains at least the active timeline.
    /// Windows historically showed all timelines; Mac strips only open tabs.
    /// </summary>
    public static void EnsureOpenTimelineIds(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var file = session.ProjectFile;
        file.OpenTimelineIds ??= [];
        if (file.OpenTimelineIds.Count == 0)
        {
            foreach (var t in file.Timelines)
                file.OpenTimelineIds.Add(t.Id);
        }
        // Drop stale ids and ensure the active timeline is always represented in the open tab strip.
        file.OpenTimelineIds.RemoveAll(id => file.Timelines.All(t => t.Id != id));
        if (!string.IsNullOrEmpty(file.ActiveTimelineId)
            && file.Timelines.Any(t => t.Id == file.ActiveTimelineId)
            && !file.OpenTimelineIds.Contains(file.ActiveTimelineId))
            file.OpenTimelineIds.Add(file.ActiveTimelineId);
        if (file.OpenTimelineIds.Count == 0 && file.Timelines.Count > 0)
            file.OpenTimelineIds.Add(file.Timelines[0].Id);
    }

    /// <summary>Mac closeTimelineTab — remove from open strip only (does not delete timeline data).</summary>
    public static EditResult CloseTimelineTab(ProjectSession session, string timelineId)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureOpenTimelineIds(session);
        var open = session.ProjectFile.OpenTimelineIds!;
        if (open.Count <= 1)
            return EditResult.Fail("Cannot close the last open timeline tab.");
        var index = open.IndexOf(timelineId);
        if (index < 0)
            return EditResult.Fail("Timeline tab is not open.");

        if (session.ProjectFile.ActiveTimelineId == timelineId)
        {
            var next = open[index == 0 ? 1 : index - 1];
            session.ProjectFile.ActiveTimelineId = next;
        }
        open.RemoveAt(index);
        session.MarkDirty();
        return EditResult.Success();
    }

    /// <summary>Mac closeOtherTimelineTabs — leave a single open tab.</summary>
    public static EditResult CloseOtherTimelineTabs(ProjectSession session, string keepId)
    {
        ArgumentNullException.ThrowIfNull(session);
        EnsureOpenTimelineIds(session);
        if (session.ProjectFile.Timelines.All(t => t.Id != keepId))
            return EditResult.Fail($"No timeline with id '{keepId}'.");
        session.ProjectFile.ActiveTimelineId = keepId;
        session.ProjectFile.OpenTimelineIds = [keepId];
        session.MarkDirty();
        return EditResult.Success();
    }

    /// <summary>Mac duplicateTimeline — deep copy with new ids.</summary>
    public static EditResult DuplicateTimeline(ProjectSession session, string timelineId, out string createdId)
    {
        createdId = "";
        return AddTimeline(session, null, timelineId, out createdId);
    }

    /// <summary>
    /// Deletes a timeline. Refuses if it is the only remaining timeline (Mac: keep at least one).
    /// Switches active selection to a neighbor when the active timeline is removed.
    /// </summary>
    public static EditResult DeleteTimeline(ProjectSession session, string timelineId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(timelineId))
            return EditResult.Fail("timelineId is required");

        // WP-177: see AddTimeline. Removal is the same structural change to the same shared list.
        return ProjectHub.RunUnderGate(() => DeleteTimelineUnderGate(session, timelineId));
    }

    private static EditResult DeleteTimelineUnderGate(ProjectSession session, string timelineId)
    {
        var timelines = session.ProjectFile.Timelines;
        var target = timelines.FirstOrDefault(t => t.Id == timelineId);
        if (target is null)
            return EditResult.Fail($"No timeline with id '{timelineId}'.");
        if (timelines.Count <= 1)
            return EditResult.Fail("Cannot delete the only timeline in the project.");

        session.RecordBeforeEdit();
        try
        {
            var index = timelines.IndexOf(target);
            timelines.RemoveAt(index);

            if (session.ProjectFile.OpenTimelineIds is { } open)
                open.RemoveAll(id => id == timelineId);

            if (session.ProjectFile.ViewStates is { } vs)
                vs.Remove(timelineId);

            if (session.ProjectFile.ActiveTimelineId == timelineId)
            {
                var next = timelines[Math.Clamp(index, 0, timelines.Count - 1)];
                session.ProjectFile.ActiveTimelineId = next.Id;
            }

            session.MarkDirty();
            return EditResult.Success();
        }
        catch
        {
            session.RollbackLastRecord();
            throw;
        }
    }
}
