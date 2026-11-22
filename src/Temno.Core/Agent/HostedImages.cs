using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Temno.Core.Agent;

/// <summary>
/// WP-436: the Gateway's rules for tool-result images (catalog <c>toolResultImages</c>). A turn freezes them when its
/// model takes images (<see cref="HostedChatMetadata.ToolImages"/>), so a resubmission composes the same request.
/// </summary>
public sealed class HostedImagePolicy
{
    public string[] MediaTypes { get; set; } = [];
    public int MaxPerResult { get; set; }
    public int MaxPerRequest { get; set; }
    public int MaxBytes { get; set; }
    public int MaxSide { get; set; }
    public int MaxPixels { get; set; }
    public int BaseTokens { get; set; }
    public int PixelsPerToken { get; set; }
}

public sealed class HostedToolImage
{
    public string MediaType { get; set; } = "";
    public string Data { get; set; } = "";
}

/// <summary>
/// WP-436: which of a tool result's images go to the hosted agent. Every choice is a pure function of the saved
/// tool-result blocks and the frozen policy, so a lost acknowledgment resubmits byte-identical results. An image left
/// out is named in a note on the result's text, so the model knows a picture existed.
/// </summary>
public static class HostedImages
{
    // Tool results share the Gateway's 4 MiB request with their text, measured as sent (JSON escapes included), with room
    // for the Gateway's width and height and Postgres's spacing; history shares it with the conversation, the
    // instructions and the tool schemas.
    public const int RequestBytes = 3 * 1024 * 1024 + 512 * 1024;
    public const int RequestImageChars = 3 * 1024 * 1024;
    public const int HistoryImageChars = 2 * 1024 * 1024;
    public const int HistoryRequestChars = 3 * 1024 * 1024;
    public const int HistoryImages = 8;
    // The label and image-part wrapping the Gateway adds around each forwarded image, for the composer's estimate.
    private const int PartOverheadBytes = 64;

    /// <summary>The image's size when it meets the policy, or null when the Gateway would refuse it.</summary>
    public static (int Width, int Height)? Accepted(HostedImagePolicy policy, string? mediaType, string? data)
    {
        if (mediaType is null || data is not { Length: > 0 } || !policy.MediaTypes.Contains(mediaType, StringComparer.Ordinal)
            || data.Length % 4 != 0 || data.Length > (policy.MaxBytes + 2L) / 3 * 4
            || data.Any(c => !(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=')))
            return null;
        var bytes = new byte[data.Length / 4 * 3];
        if (!Convert.TryFromBase64String(data, bytes, out var length) || length > policy.MaxBytes) return null;
        var size = mediaType == "image/png" ? Png(bytes.AsSpan(0, length))
            : mediaType == "image/jpeg" ? Jpeg(bytes.AsSpan(0, length)) : null;
        if (size is not { } s || s.Width < 1 || s.Height < 1 || s.Width > policy.MaxSide || s.Height > policy.MaxSide
            || (long)s.Width * s.Height > policy.MaxPixels)
            return null;
        return s;
    }

    private static (int Width, int Height)? Png(ReadOnlySpan<byte> b)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (b.Length < 24 || !b[..8].SequenceEqual(signature) || !b.Slice(12, 4).SequenceEqual("IHDR"u8)) return null;
        uint w = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(16, 4)), h = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(20, 4));
        return w > int.MaxValue || h > int.MaxValue ? null : ((int)w, (int)h);
    }

    private static (int Width, int Height)? Jpeg(ReadOnlySpan<byte> b)
    {
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return null;
        var i = 2;
        while (i + 3 < b.Length)
        {
            if (b[i] != 0xFF) return null;
            var marker = b[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0x01 or >= 0xD0 and <= 0xD7) { i += 2; continue; }
            if (marker is 0xD9 or 0xDA) return null;
            int length = BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 2, 2));
            if (length < 2 || i + 2 + length > b.Length) return null;
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                return length < 7 ? null
                    : (BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 7, 2)), BinaryPrimitives.ReadUInt16BigEndian(b.Slice(i + 5, 2)));
            i += 2 + length;
        }
        return null;
    }

    /// <summary>The Gateway's token allowance for one image: what holds count instead of its base64.</summary>
    public static long Tokens(HostedImagePolicy policy, int width, int height) =>
        policy.BaseTokens + ((long)width * height + policy.PixelsPerToken - 1) / Math.Max(1, policy.PixelsPerToken);

    private static IEnumerable<AgentToolResultBlock> ImageBlocks(IEnumerable<AgentToolResultBlock>? content) =>
        (content ?? []).Where(c => c.Kind == AgentToolResultBlock.KindImage);

    /// <summary>"image 3 of 5", "images 4–5 of 5" or "images 2, 4 and 5 of 5": the tool's own 1-based order.</summary>
    public static string Positions(IReadOnlyList<int> positions, int total)
    {
        if (positions.Count == 1) return $"image {positions[0]} of {total}";
        var run = positions.Zip(positions.Skip(1)).All(p => p.Second == p.First + 1);
        var list = run ? $"{positions[0]}–{positions[^1]}"
            : string.Join(", ", positions.Take(positions.Count - 1)) + " and " + positions[^1];
        return $"images {list} of {total}";
    }

    private static string Note(IReadOnlyList<int> positions, int total, string state) =>
        $"\n[Temno: {Positions(positions, total)} from this tool result {(positions.Count == 1 ? "was" : "were")} not sent: {state}.]";

    private sealed class Draft(HostedToolResult result, int total)
    {
        public HostedToolResult Result { get; } = result;
        public int Total { get; } = total;
        public List<(int Position, HostedToolImage Image)> Attached { get; } = [];
        public List<int> Over { get; } = [];
        public List<int> Refused { get; } = [];

        public HostedToolResult Render()
        {
            var content = Result.Content;
            if (Over.Count > 0) content += Note([.. Over.Order()], Total, "over the hosted size limit");
            if (Refused.Count > 0) content += Note(Refused, Total, "not a PNG or JPEG the hosted service accepts");
            return new HostedToolResult
            {
                Id = Result.Id, Content = content,
                Images = Attached.Count > 0 ? [.. Attached.Select(a => a.Image)] : null,
            };
        }
    }

    /// <summary>
    /// The results to submit, in call order, with each one's images from its saved tool-result block, in the tool's
    /// order. Without a policy (a model or Gateway that takes no images) they are text only, exactly as before WP-436.
    /// The serialized body stays within <see cref="RequestBytes"/>: the last attached images give way first.
    /// </summary>
    public static List<HostedToolResult> Compose(IReadOnlyList<HostedToolResult> results,
        Func<string, IReadOnlyList<AgentToolResultBlock>?> blocks, HostedImagePolicy? policy)
    {
        if (policy is null) return [.. results.Select(r => new HostedToolResult { Id = r.Id, Content = r.Content })];
        var drafts = new List<Draft>();
        int count = 0;
        long chars = 0;
        foreach (var result in results)
        {
            var images = ImageBlocks(blocks(result.Id)).ToList();
            var draft = new Draft(result, images.Count);
            for (var i = 0; i < images.Count; i++)
            {
                var block = images[i];
                if (Accepted(policy, block.MediaType, block.Base64) is null) { draft.Refused.Add(i + 1); continue; }
                if (draft.Attached.Count >= policy.MaxPerResult || count >= policy.MaxPerRequest
                    || chars + block.Base64!.Length > RequestImageChars) { draft.Over.Add(i + 1); continue; }
                draft.Attached.Add((i + 1, new HostedToolImage { MediaType = block.MediaType!, Data = block.Base64! }));
                count++;
                chars += block.Base64!.Length;
            }
            drafts.Add(draft);
        }
        var composed = drafts.Select(d => d.Render()).ToList();
        // Text, JSON escapes (a base64 '+' is sent as +) and notes count too: drop from the end until it fits.
        while (HostedAgentClient.ToolResultsBytes(composed) > RequestBytes && drafts.LastOrDefault(d => d.Attached.Count > 0) is { } last)
        {
            last.Over.Add(last.Attached[^1].Position);
            last.Attached.RemoveAt(last.Attached.Count - 1);
            composed = [.. drafts.Select(d => d.Render())];
        }
        return composed;
    }

    /// <summary>
    /// The history's tool messages with images: the newest first, up to <see cref="HistoryImages"/> and
    /// <see cref="HistoryImageChars"/>, and only while the whole history stays under <see cref="HistoryRequestChars"/>.
    /// An older picture is named in a note.
    /// </summary>
    public static void AttachToHistory(JsonArray messages, IReadOnlyDictionary<JsonObject, IReadOnlyList<AgentToolResultBlock>> blocks,
        HostedImagePolicy policy)
    {
        long room = Math.Min(HistoryImageChars, HistoryRequestChars - (long)Encoding.UTF8.GetByteCount(messages.ToJsonString()));
        int count = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject message || !blocks.TryGetValue(message, out var content)) continue;
            var all = ImageBlocks(content).ToList();
            var images = new JsonArray();
            var omitted = new List<int>();
            for (var p = 0; p < all.Count; p++)
            {
                var block = all[p];
                if (Accepted(policy, block.MediaType, block.Base64) is null) { omitted.Add(p + 1); continue; }
                var cost = block.Base64!.Length + PartOverheadBytes;
                if (images.Count >= policy.MaxPerResult || count >= Math.Min(HistoryImages, policy.MaxPerRequest) || cost > room)
                {
                    omitted.Add(p + 1);
                    continue;
                }
                images.Add(new JsonObject { ["mediaType"] = block.MediaType, ["data"] = block.Base64 });
                count++;
                room -= cost;
            }
            // Named by the tool's own order, so the attached pictures pair with the frames the text lists.
            if (omitted.Count > 0)
                message["content"] = (message["content"]?.GetValue<string>() ?? "")
                    + $"\n[Temno: {Positions(omitted, all.Count)} from this tool result {(omitted.Count == 1 ? "is" : "are")} not attached.]";
            if (images.Count > 0) message["images"] = images;
        }
    }

    /// <summary>
    /// Quote messages as the Gateway measures them: the JSON without the images, plus each image's token allowance and
    /// its part wrapping. Display only; the quote's figures win.
    /// </summary>
    public static long MeasuredBytes(JsonArray messages, HostedImagePolicy? policy)
    {
        if (policy is null || !messages.Any(m => m is JsonObject o && o.ContainsKey("images")))
            return Encoding.UTF8.GetByteCount(messages.ToJsonString());
        var copy = (JsonArray)messages.DeepClone();
        long allowance = 0;
        foreach (var message in copy.OfType<JsonObject>())
        {
            if (message["images"] is not JsonArray images) continue;
            foreach (var image in images.OfType<JsonObject>())
                if (Accepted(policy, image["mediaType"]?.GetValue<string>(), image["data"]?.GetValue<string>()) is { } size)
                    allowance += Tokens(policy, size.Width, size.Height) + PartOverheadBytes;
            message.Remove("images");
        }
        return Encoding.UTF8.GetByteCount(copy.ToJsonString()) + allowance;
    }
}
