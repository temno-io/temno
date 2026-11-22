using Temno.Core.Models;

namespace Temno.Core.Editing;

/// <summary>
/// Pure helpers for A/V link groups (Mac: EditorViewModel+Linking.swift).
/// Clips that share a <see cref="Clip.LinkGroupId"/> move/trim/delete as one unit.
/// </summary>
public static class LinkGroup
{
    /// <summary>Reverse index: groupId → member clip ids (order of discovery).</summary>
    public static Dictionary<string, List<string>> BuildIndex(Timeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var track in timeline.Tracks)
        {
            foreach (var clip in track.Clips)
            {
                if (string.IsNullOrEmpty(clip.LinkGroupId)) continue;
                if (!map.TryGetValue(clip.LinkGroupId, out var list))
                {
                    list = [];
                    map[clip.LinkGroupId] = list;
                }
                list.Add(clip.Id);
            }
        }
        return map;
    }

    /// <summary>
    /// Returns every clip id that shares a link group with any id in <paramref name="ids"/>,
    /// including the inputs themselves (even when unlinked).
    /// </summary>
    public static HashSet<string> Expand(Timeline timeline, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(ids);
        var seed = ids.Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        if (seed.Count == 0) return seed;

        var index = BuildIndex(timeline);
        if (index.Count == 0) return seed;

        var clipToGroup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (gid, members) in index)
        {
            foreach (var id in members)
                clipToGroup[id] = gid;
        }

        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in seed)
        {
            if (clipToGroup.TryGetValue(id, out var g))
                groups.Add(g);
        }
        if (groups.Count == 0) return seed;

        var result = new HashSet<string>(seed, StringComparer.Ordinal);
        foreach (var g in groups)
        {
            if (index.TryGetValue(g, out var members))
                result.UnionWith(members);
        }
        return result;
    }

    /// <summary>Partner ids that share a group with <paramref name="clipId"/>, excluding itself.</summary>
    public static IReadOnlyList<string> PartnerIds(Timeline timeline, string clipId)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (string.IsNullOrWhiteSpace(clipId)) return [];
        foreach (var members in BuildIndex(timeline).Values)
        {
            if (!members.Contains(clipId, StringComparer.Ordinal)) continue;
            return members.Where(id => !string.Equals(id, clipId, StringComparison.Ordinal)).ToList();
        }
        return [];
    }

    /// <summary>
    /// True when <paramref name="clipId"/> is selected and has at least one link-group partner that
    /// is NOT in <paramref name="selectedClipIds"/> (i.e. the user Alt+clicked to isolate this half).
    /// </summary>
    public static bool IsIsolatedInSelection(Timeline timeline, IReadOnlySet<string> selectedClipIds, string clipId)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(selectedClipIds);
        if (string.IsNullOrWhiteSpace(clipId) || !selectedClipIds.Contains(clipId))
            return false;
        var partners = PartnerIds(timeline, clipId);
        if (partners.Count == 0)
            return false;
        return partners.Any(id => !selectedClipIds.Contains(id));
    }

    /// <summary>True when any clip in <paramref name="selectedClipIds"/> is isolated from its link partner.</summary>
    public static bool HasIsolatedMember(Timeline timeline, IReadOnlySet<string> selectedClipIds)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(selectedClipIds);
        foreach (var id in selectedClipIds)
        {
            if (IsIsolatedInSelection(timeline, selectedClipIds, id))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether an edit targeting <paramref name="clipId"/> should expand to its link group.
    /// False if Alt is held now OR the clip was already Alt-isolated in the current selection.
    /// </summary>
    public static bool ShouldExpandLinks(
        Timeline timeline,
        IReadOnlySet<string> selectedClipIds,
        string clipId,
        bool altHeld)
        => !altHeld && !IsIsolatedInSelection(timeline, selectedClipIds, clipId);

    /// <summary>
    /// Whether a selection-wide command (trim, split, delete) should expand link groups.
    /// False if Alt is held now OR any selected clip is already isolated from its partner.
    /// </summary>
    public static bool ShouldExpandSelection(
        Timeline timeline,
        IReadOnlySet<string> selectedClipIds,
        bool altHeld)
        => !altHeld && !HasIsolatedMember(timeline, selectedClipIds);
}
