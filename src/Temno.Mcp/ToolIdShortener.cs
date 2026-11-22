using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Temno.Mcp;

/// <summary>
/// Mac <c>ToolExecutor+ShortId.swift</c>: UUID ids shorten to unique prefixes on output;
/// prefixes (≥8) expand back to full ids on input.
/// </summary>
public static class ToolIdShortener
{
    public const int PrefixFloor = 8;

    private static readonly Regex UuidRegex = new(
        @"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ScalarIdKeys = new(StringComparer.Ordinal)
    {
        "clipId", "sourceClipId", "referenceClipId", "targetClipId",
        "mediaRef", "startFrameMediaRef", "endFrameMediaRef",
        "sourceVideoMediaRef", "videoSourceMediaRef", "sourceMediaRef",
        "captionGroupId", "timelineId", "trackId", "item", "from", "reference",
        "groupId", "memberId",
        // Windows extras (Mac shortens these via the UUID regex on output).
        "id", "markerId", "transitionId", "multicamGroupId", "folderId",
        // WP-337: add_transition, the image mask, add_captions' subtitle asset and manage_multicam's master. The
        // Mac checkout has no add_transition, so its key list has none of these.
        "clipIdA", "clipIdB", "maskMediaRef", "subtitleMediaRef", "master",
    };

    private static readonly HashSet<string> ArrayIdKeys = new(StringComparer.Ordinal)
    {
        "clipIds", "targetClipIds", "items", "ids", "deletes",
        "referenceMediaRefs", "referenceImageMediaRefs",
        "referenceVideoMediaRefs", "referenceAudioMediaRefs",
    };

    public static JsonObject Expand(JsonObject args, IReadOnlySet<string> universe)
    {
        var clone = args.DeepClone().AsObject()
            ?? throw new InvalidOperationException("Tool arguments must be a JSON object.");
        ExpandNode(clone, universe);
        return clone;
    }

    public static ToolCallResult Shorten(ToolCallResult result, IReadOnlySet<string> universe)
    {
        if (result.IsError || universe.Count == 0)
            return result;
        var map = ShortIdMap(universe);
        if (map.Count == 0)
            return result;

        foreach (var block in result.Content)
        {
            if (!string.Equals(block.Type, "text", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(block.Text))
                continue;
            block.Text = UuidRegex.Replace(block.Text, match =>
            {
                var raw = match.Value;
                return map.TryGetValue(raw, out var shortId) ? shortId : raw;
            });
        }

        return result;
    }

    /// <summary>
    /// Maps each id to its shortest prefix (≥ <see cref="PrefixFloor"/>) that no other id shares.
    /// </summary>
    public static Dictionary<string, string> ShortIdMap(IReadOnlySet<string> ids)
    {
        var sorted = ids.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var map = new Dictionary<string, string>(sorted.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < sorted.Count; i++)
        {
            var id = sorted[i];
            var sharedLen = 0;
            if (i > 0)
                sharedLen = Math.Max(sharedLen, CommonPrefixLength(id, sorted[i - 1]));
            if (i < sorted.Count - 1)
                sharedLen = Math.Max(sharedLen, CommonPrefixLength(id, sorted[i + 1]));
            var len = Math.Min(id.Length, Math.Max(PrefixFloor, sharedLen + 1));
            map[id] = id[..len];
        }

        return map;
    }

    public static string ExpandOne(string reference, IReadOnlySet<string> universe)
    {
        if (universe.Contains(reference))
            return reference;
        if (reference.Length < PrefixFloor)
            return reference;

        string? match = null;
        var count = 0;
        foreach (var id in universe)
        {
            if (!id.StartsWith(reference, StringComparison.OrdinalIgnoreCase))
                continue;
            count++;
            match = id;
            if (count > 1)
                break;
        }

        if (count == 1)
            return match!;
        if (count > 1)
        {
            throw new InvalidOperationException(
                $"Ambiguous id '{reference}' matches {count} items; re-read with get_timeline or get_media for current ids.");
        }

        return reference;
    }

    private static void ExpandNode(JsonNode? node, IReadOnlySet<string> universe)
    {
        switch (node)
        {
            case JsonObject obj:
                // Snapshot keys so we can replace values while iterating.
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var value = obj[key];
                    if (ScalarIdKeys.Contains(key) && value is JsonValue scalar
                        && scalar.TryGetValue<string>(out var s) && s is not null)
                    {
                        obj[key] = ExpandOne(s, universe);
                    }
                    else if (ArrayIdKeys.Contains(key) && value is JsonArray arr)
                    {
                        for (var i = 0; i < arr.Count; i++)
                        {
                            if (arr[i] is JsonValue item && item.TryGetValue<string>(out var id) && id is not null)
                                arr[i] = ExpandOne(id, universe);
                            else
                                ExpandNode(arr[i], universe);
                        }
                    }
                    else
                    {
                        ExpandNode(value, universe);
                    }
                }
                break;
            case JsonArray array:
                foreach (var child in array)
                    ExpandNode(child, universe);
                break;
        }
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i])
            i++;
        return i;
    }
}
