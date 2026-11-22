using System.Text.Json.Nodes;
using Temno.Core.Project;
using Temno.Core.Session;

namespace Temno.Mcp;

public static partial class ToolExecutor
{
    /// <summary>
    /// Ids visible to the agent (Mac <c>currentIdUniverse</c>). Project-registry ids are
    /// omitted so <c>manage_project</c> list/open keep full project UUIDs, matching Mac.
    /// </summary>
    internal static HashSet<string> CollectIdUniverse()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var session = ProjectHub.ActiveSession;
        if (session is null)
            return ids;

        foreach (var timeline in session.ProjectFile.Timelines)
        {
            Add(ids, timeline.Id);
            foreach (var track in timeline.Tracks)
            {
                Add(ids, track.Id);
                foreach (var clip in track.Clips)
                {
                    Add(ids, clip.Id);
                    Add(ids, clip.CaptionGroupId);
                    Add(ids, clip.LinkGroupId);
                    Add(ids, clip.MediaRef);
                    Add(ids, clip.MulticamGroupId);
                }
            }

            foreach (var marker in timeline.Markers)
                Add(ids, marker.Id);
            foreach (var transition in timeline.Transitions)
                Add(ids, transition.Id);
        }

        foreach (var entry in session.Manifest.Entries)
            Add(ids, entry.Id);
        foreach (var folder in session.Manifest.Folders)
            Add(ids, folder.Id);

        if (session.ProjectFile.MulticamGroups is { } groups)
        {
            foreach (var group in groups)
            {
                Add(ids, group.Id);
                foreach (var member in group.Members)
                    Add(ids, member.Id);
            }
        }

        return ids;
    }

    private static void Add(HashSet<string> ids, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            ids.Add(value);
    }

    private static object ManageProject(JsonObject args)
    {
        var action = args["action"]?.GetValue<string>()
            ?? throw new InvalidOperationException("manage_project requires an 'action'.");
        var rest = new JsonObject();
        foreach (var kv in args)
        {
            if (kv.Key == "action")
                continue;
            rest[kv.Key] = kv.Value?.DeepClone();
        }

        return action switch
        {
            "list" => GetProjects(),
            "open" => OpenProject(rest),
            "create" => NewProject(rest),
            "close" => CloseProject(rest),
            _ => throw new InvalidOperationException(
                $"Unknown project action '{action}'. Use one of: list, open, create, close."),
        };
    }
}
