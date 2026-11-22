using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.Agent;
using Xunit;

namespace Temno.Core.Tests;

// WP-436: the hosted agent sends the frames its tools render. The gateway here is a scripted local fixture.
public sealed class HostedToolImageTests
{
    private static readonly HostedImagePolicy Policy = new()
    {
        MediaTypes = ["image/png", "image/jpeg"], MaxPerResult = 10, MaxPerRequest = 20, MaxBytes = 1_048_576,
        MaxSide = 4096, MaxPixels = 4_194_304, BaseTokens = 2560, PixelsPerToken = 196,
    };

    internal static string Png(int width, int height, int padding = 0)
    {
        var b = new byte[33 + padding];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(20), height);
        for (var i = 33; i < b.Length; i++) b[i] = (byte)(i * 31);
        return Convert.ToBase64String(b);
    }

    private static HostedAgentProfile Profile(bool images = true) => new()
    {
        Id = "fixture", DefaultEffort = "high", Efforts = ["high"], MaxOutputTokens = 128000, Qualified = true,
        Completion = new() { ["high"] = new() { Target = 32768, Floor = 16384, Typical = 6144 } },
        ImageInput = images, ImagePolicy = images ? Policy : null,
    };

    private sealed class Frames(params AgentToolResultBlock[] blocks) : IToolRunner
    {
        public Task<AgentToolExecutionResult> ExecuteAsync(string name, string inputJson, CancellationToken cancellationToken) =>
            Task.FromResult(new AgentToolExecutionResult(blocks, false));
    }

    private static Frames Inspect(params string[] pngs) => new(
        [AgentToolResultBlock.FromText("{\"status\":\"success\",\"frames\":[42]}"), .. pngs.Select(p => AgentToolResultBlock.Image(p, "image/png"))]);

    private static async Task<(HostedAgentConversation Conversation, Gateway Gateway)> Ask(HostedAgentProfile profile, IToolRunner runner)
    {
        var g = new Gateway();
        g.Events.Add(("tool_request", "[{\"id\":\"call_1\",\"function\":{\"name\":\"inspect_timeline\",\"arguments\":\"{}\"}}]"));
        g.Events.Add(("terminal", "{\"status\":\"succeeded\",\"charge\":5}"));
        var c = new HostedAgentConversation();
        await c.SendUserAsync("what do you see on preview?", g.Client, profile, ["inspect_timeline"], runner, 200, null);
        return (c, g);
    }

    [Fact]
    public async Task The_catalog_gives_the_image_rules_only_to_a_model_that_takes_images()
    {
        using var g = new Gateway
        {
            CatalogJson = "{\"toolResultImages\":{\"mediaTypes\":[\"image/png\"],\"maxPerResult\":10,\"maxPerRequest\":20,\"maxBytes\":1048576,"
                + "\"maxSide\":4096,\"maxPixels\":4194304,\"baseTokens\":2560,\"pixelsPerToken\":196},"
                + "\"models\":[{\"id\":\"sees\",\"imageInput\":true},{\"id\":\"blind\",\"imageInput\":false}]}",
        };
        var catalog = await g.Client.GetCatalogAsync();
        Assert.Equal(196, catalog.Models.Single(m => m.Id == "sees").ImagePolicy!.PixelsPerToken);
        Assert.Null(catalog.Models.Single(m => m.Id == "blind").ImagePolicy);
        // A gateway that predates WP-436 publishes neither.
        g.CatalogJson = "{\"models\":[{\"id\":\"old\"}]}";
        Assert.Null((await g.Client.GetCatalogAsync()).Models.Single().ImagePolicy);
    }

    [Fact]
    public async Task A_rendered_frame_reaches_the_gateway_beside_its_text()
    {
        var png = Png(640, 360, 4000);
        var (c, g) = await Ask(Profile(), Inspect(png));
        using var _ = g;
        var result = JsonNode.Parse(g.ToolResults.Single())!["results"]![0]!;
        Assert.Equal("{\"status\":\"success\",\"frames\":[42]}", result["content"]!.GetValue<string>());
        Assert.Equal("image/png", result["images"]![0]!["mediaType"]!.GetValue<string>());
        Assert.Equal(png, result["images"]![0]!["data"]!.GetValue<string>());
        Assert.Equal(20, c.Session.Hosted!.ToolImages!.MaxPerRequest);
    }

    [Fact]
    public async Task A_model_without_image_input_sends_the_text_alone()
    {
        var (c, g) = await Ask(Profile(images: false), Inspect(Png(640, 360)));
        using var _ = g;
        Assert.Equal("{\"results\":[{\"id\":\"call_1\",\"content\":\"{\\u0022status\\u0022:\\u0022success\\u0022,\\u0022frames\\u0022:[42]}\"}]}",
            g.ToolResults.Single());
        Assert.Null(c.Session.Hosted!.ToolImages);
    }

    [Fact]
    public async Task A_lost_acknowledgment_resubmits_the_same_bytes()
    {
        var png = Png(640, 360, 4000);
        using var g = new Gateway { FailToolResultsOnce = true };
        g.Events.Add(("tool_request", "[{\"id\":\"call_1\",\"function\":{\"name\":\"inspect_timeline\",\"arguments\":\"{}\"}}]"));
        g.Events.Add(("terminal", "{\"status\":\"succeeded\",\"charge\":5}"));
        var c = new HostedAgentConversation();
        await Assert.ThrowsAnyAsync<Exception>(() => c.SendUserAsync("what do you see on preview?", g.Client, Profile(), ["inspect_timeline"],
            Inspect(png), 200, null));
        // The tool already ran; recovery must not run it again, and must send what it sent before.
        await c.ResumeAsync(g.Client, new Frames(AgentToolResultBlock.FromText("must not run")), null);
        Assert.Equal(2, g.ToolResults.Count);
        Assert.Equal(g.ToolResults[0], g.ToolResults[1]);
        Assert.Equal(png, JsonNode.Parse(g.ToolResults[1])!["results"]![0]!["images"]![0]!["data"]!.GetValue<string>());
    }

    [Fact]
    public void Images_over_the_limits_or_refused_by_the_gateway_become_a_note()
    {
        var blocks = new List<AgentToolResultBlock> { AgentToolResultBlock.FromText("frames") };
        for (var i = 0; i < 12; i++) blocks.Add(AgentToolResultBlock.Image(Png(64, 36 + i), "image/png"));
        blocks.Add(AgentToolResultBlock.Image(Png(64, 36), "image/gif"));
        blocks.Add(AgentToolResultBlock.Image(Png(5000, 10), "image/png"));
        var composed = HostedImages.Compose([new HostedToolResult { Id = "a", Content = "frames" }], _ => blocks, Policy);
        var r = composed.Single();
        Assert.Equal(10, r.Images!.Count);
        Assert.Equal(36, BinaryPrimitives.ReadInt32BigEndian(Convert.FromBase64String(r.Images[0].Data).AsSpan(20)));
        Assert.Contains("images 11–12 of 14 from this tool result were not sent: over the hosted size limit", r.Content);
        Assert.Contains("images 13–14 of 14 from this tool result were not sent: not a PNG or JPEG", r.Content);
        // The request budget spans the batch: twenty in all, and no more than its byte budget.
        var many = HostedImages.Compose([.. Enumerable.Range(0, 3).Select(i => new HostedToolResult { Id = "r" + i, Content = "x" })], _ => blocks, Policy);
        Assert.Equal([10, 10, 0], many.Select(x => x.Images?.Count ?? 0).ToArray());
        var large = Enumerable.Range(0, 4).Select(_ => AgentToolResultBlock.Image(Png(640, 360, 1_000_000), "image/png")).ToList();
        var sized = HostedImages.Compose([new HostedToolResult { Id = "a", Content = "x" }], _ => large, Policy).Single();
        Assert.Equal(2, sized.Images!.Count);
        Assert.True(sized.Images.Sum(i => i.Data.Length) <= HostedImages.RequestImageChars);
        // Without a policy: text only, no note.
        Assert.Equal("frames", HostedImages.Compose([new HostedToolResult { Id = "a", Content = "frames" }], _ => blocks, null).Single().Content);
    }

    // A PNG header and random bytes: base64 with '+' at its natural rate, which JSON escapes as +.
    private static string NoisyPng(int width, int height, int bytes, int seed)
    {
        var b = Convert.FromBase64String(Png(width, height, bytes - 33));
        new Random(seed).NextBytes(b.AsSpan(33));
        return Convert.ToBase64String(b);
    }

    [Fact]
    public void The_measured_request_stays_inside_the_gateway_intake()
    {
        // T2 round 1: 700,000 bytes of JSON text with 38,000 quotes beside four 580,000-byte frames used to compose a
        // body over 4 MiB, refused identically on every reconnect.
        var text = string.Concat(Enumerable.Repeat("{\"a\":1}", 38_000 / 2)).PadRight(700_000, 'x');
        var frames = Enumerable.Range(0, 4).Select(i => AgentToolResultBlock.Image(NoisyPng(640, 360, 580_000, i), "image/png")).ToList();
        var results = new List<HostedToolResult> { new() { Id = "get", Content = text }, new() { Id = "inspect", Content = "frames" } };
        var composed = HostedImages.Compose(results, id => id == "inspect" ? frames : null, Policy);
        Assert.True(HostedAgentClient.ToolResultsBytes(composed) <= HostedImages.RequestBytes);
        var inspect = composed.Single(r => r.Id == "inspect");
        Assert.InRange(inspect.Images!.Count, 1, 3);
        Assert.Contains($"from this tool result {(inspect.Images.Count == 3 ? "was" : "were")} not sent: over the hosted size limit", inspect.Content);
        Assert.Contains("of 4", inspect.Content);
        Assert.Equal(frames[0].Base64, inspect.Images[0].Data);
        // Deterministic: the same inputs compose the same bytes.
        Assert.Equal(HostedAgentClient.ToolResultsBytes(composed),
            HostedAgentClient.ToolResultsBytes(HostedImages.Compose(results, id => id == "inspect" ? frames : null, Policy)));
    }

    [Theory]
    [InlineData(new[] { 3 }, 5, "image 3 of 5")]
    [InlineData(new[] { 9, 10 }, 10, "images 9–10 of 10")]
    [InlineData(new[] { 2, 4, 5 }, 5, "images 2, 4 and 5 of 5")]
    public void Left_out_images_are_named_in_the_tools_order(int[] positions, int total, string expected) =>
        Assert.Equal(expected, HostedImages.Positions(positions, total));

    [Fact]
    public void History_keeps_a_results_first_frames_and_names_the_rest()
    {
        var history = History(1, 10);
        var tool = HostedHistory.ToQuoteMessages(history, Policy).Single(m => m!["role"]!.GetValue<string>() == "tool")!;
        Assert.Equal(HostedImages.HistoryImages, tool["images"]!.AsArray().Count);
        Assert.Equal(360, BinaryPrimitives.ReadInt32BigEndian(Convert.FromBase64String(tool["images"]![0]!["data"]!.GetValue<string>()).AsSpan(20)));
        Assert.EndsWith("[Temno: images 9–10 of 10 from this tool result are not attached.]", tool["content"]!.GetValue<string>());
    }

    private static List<AgentMessage> History(int results, int imagesEach, int padding = 2000)
    {
        var history = new List<AgentMessage> { AgentMessage.UserText("what do you see?") };
        for (var r = 0; r < results; r++)
        {
            history.Add(AgentMessage.Assistant(AgentContentBlock.ToolUse("c" + r, "inspect_timeline", "{}")));
            List<AgentToolResultBlock> content = [AgentToolResultBlock.FromText("frame " + r),
                .. Enumerable.Range(0, imagesEach).Select(i => AgentToolResultBlock.Image(Png(640, 360 + r * 10 + i, padding), "image/png"))];
            history.Add(new AgentMessage { Role = AgentMessageRole.user, Blocks = [AgentContentBlock.ToolResult("c" + r, content, false)] });
        }
        history.Add(AgentMessage.Assistant(AgentContentBlock.FromText("It fades in from black.")));
        return history;
    }

    [Fact]
    public void Replayed_history_carries_the_newest_images_and_names_older_ones()
    {
        var history = History(3, 4);
        var messages = HostedHistory.ToQuoteMessages(history, Policy);
        var tools = messages.Where(m => m!["role"]!.GetValue<string>() == "tool").ToArray();
        Assert.Equal([0, 4, 4], tools.Select(t => (t!["images"] as JsonArray)?.Count ?? 0).ToArray());
        Assert.Contains("images 1–4 of 4 from this tool result are not attached", tools[0]!["content"]!.GetValue<string>());
        Assert.Equal("frame 2", tools[2]!["content"]!.GetValue<string>());
        // Without a policy the history is the text it always was.
        Assert.Equal(HostedHistory.ToQuoteMessages(history).ToJsonString(), HostedHistory.ToQuoteMessages(history, null).ToJsonString());
        Assert.DoesNotContain("images", HostedHistory.ToQuoteMessages(history).ToJsonString());
    }

    [Fact]
    public void History_images_stay_inside_the_request_budget()
    {
        var messages = HostedHistory.ToQuoteMessages(History(2, 3, padding: 600_000), Policy);
        var chars = messages.SelectMany(m => (m!["images"] as JsonArray)?.Select(i => i!["data"]!.GetValue<string>().Length) ?? []).Sum();
        Assert.InRange(chars, 1, HostedImages.HistoryImageChars);
        Assert.True(Encoding.UTF8.GetByteCount(messages.ToJsonString()) < 4 * 1024 * 1024);
    }

    [Fact]
    public void The_estimate_counts_images_by_allowance_not_base64()
    {
        var history = History(1, 2, padding: 300_000);
        var profile = Profile();
        var measured = HostedEstimate.HistoryBytes(history, profile);
        var text = HostedEstimate.HistoryBytes(history);
        Assert.Equal(text, HostedEstimate.HistoryBytes(history, Profile(images: false)));
        var tokens = 2 * HostedImages.Tokens(Policy, 640, 360);
        Assert.InRange(measured, text + tokens, text + tokens + 256);
        var raw = Encoding.UTF8.GetByteCount(HostedHistory.ToQuoteMessages(history, Policy).ToJsonString());
        Assert.True(measured < raw / 50);
    }

    [Fact]
    public void The_frozen_rules_survive_the_chat_file()
    {
        var session = new ChatSession { Hosted = new HostedChatMetadata { ToolImages = Policy } };
        var loaded = ChatSessionStore.Decode(ChatSessionStore.Encode(session));
        Assert.Equal(1_048_576, loaded.Hosted!.ToolImages!.MaxBytes);
        Assert.Equal(["image/png", "image/jpeg"], loaded.Hosted.ToolImages.MediaTypes);
        var old = ChatSessionStore.Decode(ChatSessionStore.Encode(new ChatSession { Hosted = new HostedChatMetadata() }));
        Assert.Null(old.Hosted!.ToolImages);
    }

    private sealed class Gateway : HttpMessageHandler
    {
        public readonly List<(string Kind, string Data)> Events = [];
        public readonly List<string> ToolResults = [];
        public string CatalogJson = "{}";
        public bool FailToolResultsOnce;
        public HostedAgentClient Client { get; }
        public Gateway() => Client = new HostedAgentClient("https://fixture.test", () => "local-fixture", new HttpClient(this), accountId: "fixture-owner");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/quotes"))
                return Json("{\"id\":\"" + Guid.NewGuid() + "\",\"cap\":200,\"callBound\":203,\"estimate\":{\"typicalCallCredits\":41,\"reportReserveCredits\":73,"
                            + "\"minimumCapCredits\":178,\"requiredCapCredits\":276,\"firstCallTokens\":20107,\"workTokens\":32768}}");
            if (path == "/v1/agent/turns") return Json("{\"id\":\"" + Guid.NewGuid() + "\",\"status\":\"queued\"}");
            if (path.EndsWith("/events"))
            {
                var after = request.Headers.TryGetValues("Last-Event-ID", out var values) ? int.Parse(values.Single()) : 0;
                var body = new StringBuilder();
                for (var i = 0; i < Events.Count; i++)
                    if (i + 1 > after) body.Append($"id: {i + 1}\nevent: {Events[i].Kind}\ndata: {Events[i].Data}\n\n");
                return new(HttpStatusCode.OK) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream") };
            }
            if (path.EndsWith("/catalog")) return Json(CatalogJson);
            if (path.EndsWith("/tool-results"))
            {
                ToolResults.Add(await request.Content!.ReadAsStringAsync(ct));
                if (FailToolResultsOnce) { FailToolResultsOnce = false; return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"fixture_lost\"}") }; }
                return Json("{}");
            }
            if (path.EndsWith("/cancel")) return Json("{\"status\":\"canceled\",\"charge\":0}");
            if (path.StartsWith("/v1/agent/turns/")) return Json("{\"status\":\"waiting_tools\"}");
            throw new InvalidOperationException(path);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
