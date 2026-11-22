using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.Agent;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public sealed class ChatSessionStoreTests
{
    [Fact]
    public void JsonRoundTrip_PreservesMacFieldNamesAndMentions()
    {
        var session = new ChatSession
        {
            Id = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            Title = "Tighten the VO",
            UpdatedAt = new DateTimeOffset(2026, 8, 24, 18, 0, 0, TimeSpan.Zero),
            IsOpen = true,
            Messages =
            [
                new AgentMessage
                {
                    Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                    Role = AgentMessageRole.user,
                    Blocks = [AgentContentBlock.FromText("Cut ums on V1")],
                    Mentions =
                    [
                        new AgentMention
                        {
                            Id = Guid.Parse("99999999-8888-7777-6666-555555555555"),
                            DisplayName = "A-roll",
                            MediaRef = "M1",
                            Type = ClipType.video,
                            ClipId = "C1",
                            TimelineRange = new AgentTimelineRangeMention
                            {
                                StartFrame = 0,
                                EndFrame = 30,
                                DurationFrames = 30,
                                Fps = 30,
                                StartTimecode = "00:00:00:00",
                                EndTimecode = "00:00:01:00",
                                DurationTimecode = "00:00:01:00",
                                RangeSemantics = "startInclusiveEndExclusive",
                            },
                        },
                    ],
                    ContextHint = "clip C1 on V1",
                },
                AgentMessage.Assistant(
                    AgentContentBlock.ToolUse("toolu_1", "get_timeline", "{}"),
                    AgentContentBlock.FromText("Reading the timeline.")),
                new AgentMessage
                {
                    Role = AgentMessageRole.user,
                    Blocks =
                    [
                        AgentContentBlock.ToolResult(
                            "toolu_1",
                            [AgentToolResultBlock.FromText("{\"durationFrames\":0}")],
                            isError: false),
                    ],
                },
            ],
        };

        var json = Encoding.UTF8.GetString(ChatSessionStore.Encode(session));
        Assert.Contains("\"kind\": \"toolUse\"", json);
        Assert.Contains("\"kind\": \"toolResult\"", json);
        Assert.Contains("\"toolUseId\": \"toolu_1\"", json);
        Assert.Contains("\"updatedAt\": \"2026-08-24T18:00:00Z\"", json);
        Assert.Contains("\"displayName\": \"A-roll\"", json);
        Assert.Contains("\"rangeSemantics\": \"startInclusiveEndExclusive\"", json);
        Assert.Contains("\"type\": \"video\"", json);
        Assert.DoesNotContain("\"kind\": \"tool_use\"", json);

        var loaded = ChatSessionStore.Decode(json);
        Assert.Equal(session.Id, loaded.Id);
        Assert.Equal("Tighten the VO", loaded.Title);
        Assert.True(loaded.IsOpen);
        Assert.Equal(3, loaded.Messages.Count);
        Assert.Equal(AgentMessageRole.user, loaded.Messages[0].Role);
        var mention = Assert.Single(loaded.Messages[0].Mentions);
        Assert.Equal("A-roll", mention.DisplayName);
        Assert.Equal("C1", mention.ClipId);
        Assert.Equal(ClipType.video, mention.Type);
        Assert.Equal(30, mention.TimelineRange!.EndFrame);
        Assert.Equal(AgentContentBlock.KindToolUse, loaded.Messages[1].Blocks[0].Kind);
        Assert.Equal("get_timeline", loaded.Messages[1].Blocks[0].Name);
        Assert.Equal("toolu_1", loaded.Messages[2].Blocks[0].ToolUseId);
        Assert.False(loaded.Messages[2].Blocks[0].IsError ?? true);
        Assert.Equal("{\"durationFrames\":0}", loaded.Messages[2].Blocks[0].Content![0].Text);
    }

    [Fact]
    public void MissingIsOpen_DefaultsTrue()
    {
        const string json = """
            {
              "id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
              "title": "New chat",
              "updatedAt": "2026-08-24T18:00:00Z",
              "messages": []
            }
            """;
        var loaded = ChatSessionStore.Decode(json);
        Assert.True(loaded.IsOpen);
        Assert.Empty(loaded.Messages);
    }

    [Fact]
    public void Store_WritesPackageChatFolderAndReloads()
    {
        var package = Path.Combine(Path.GetTempPath(), "temno-chat-" + Guid.NewGuid().ToString("N") + ".palmier");
        try
        {
            var session = new ChatSession
            {
                Title = "Stored",
                Messages = [AgentMessage.UserText("hello"), AgentMessage.Assistant(AgentContentBlock.FromText("hi"))],
            };
            ChatSessionStore.Save(package, session);

            var path = Path.Combine(package, ProjectConstants.ChatDirectoryName, ChatSessionStore.FileName(session));
            Assert.True(File.Exists(path));

            var loaded = ChatSessionStore.Load(package);
            var round = Assert.Single(loaded);
            Assert.Equal(session.Id, round.Id);
            Assert.Equal("Stored", round.Title);
            Assert.Equal(2, round.Messages.Count);
            Assert.Equal("hello", round.Messages[0].Blocks[0].Text);
        }
        finally
        {
            if (Directory.Exists(package))
                Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public void ProjectPackage_NullChatSessions_KeepsClonedChat()
    {
        var package = Path.Combine(Path.GetTempPath(), "temno-chat-keep-" + Guid.NewGuid().ToString("N") + ".palmier");
        try
        {
            var session = ProjectSession.CreateNew("Keep chat");
            session.Save(package);
            var opaque = """{"messages":[],"title":"opaque-keep"}"""u8.ToArray();
            var chatFile = Path.Combine(package, ProjectConstants.ChatDirectoryName, "session-fixture.json");
            Directory.CreateDirectory(Path.GetDirectoryName(chatFile)!);
            File.WriteAllBytes(chatFile, opaque);

            ProjectPackage.Write(
                package,
                session.ProjectFile,
                session.Manifest,
                sourcePackageDirectory: package,
                chatSessions: null);

            Assert.Equal(opaque, File.ReadAllBytes(chatFile));
        }
        finally
        {
            if (Directory.Exists(package))
                Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public void ProjectPackage_NonNullChatSessions_ReplacesChatDirectory()
    {
        var package = Path.Combine(Path.GetTempPath(), "temno-chat-replace-" + Guid.NewGuid().ToString("N") + ".palmier");
        try
        {
            var project = ProjectSession.CreateNew("Replace chat");
            project.Save(package);
            var stale = Path.Combine(package, ProjectConstants.ChatDirectoryName, "stale.json");
            Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
            File.WriteAllText(stale, """{"title":"stale"}""");

            var live = new ChatSession
            {
                Title = "Live",
                Messages = [AgentMessage.UserText("keep me")],
            };
            ProjectPackage.Write(
                package,
                project.ProjectFile,
                project.Manifest,
                sourcePackageDirectory: package,
                chatSessions: [live]);

            Assert.False(File.Exists(stale));
            var reloaded = Assert.Single(ChatSessionStore.Load(package));
            Assert.Equal(live.Id, reloaded.Id);
            Assert.Equal("keep me", reloaded.Messages[0].Blocks[0].Text);
        }
        finally
        {
            if (Directory.Exists(package))
                Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public void HostedMetadata_RoundTripsAndPreservesUnknownKeys()
    {
        var session = new ChatSession
        {
            Title = "Hosted",
            Messages = [AgentMessage.UserText("hello")],
            Hosted = new HostedChatMetadata
            {
                Model = "muse-spark-1.3",
                Effort = "xhigh",
                ReasoningEnabled = true,
                TurnId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                LastEventId = 7,
                ExecutedToolIds = ["call_1"],
                ToolResults = { ["call_1"] = "ok" },
                SpendingCapCredits = 25,
            },
        };
        var json = Encoding.UTF8.GetString(ChatSessionStore.Encode(session));
        Assert.Contains("\"hosted\"", json);
        Assert.Contains("\"executedToolIds\"", json);

        var loaded = ChatSessionStore.Decode(json);
        Assert.Equal("muse-spark-1.3", loaded.Hosted!.Model);
        Assert.Equal("xhigh", loaded.Hosted.Effort);
        Assert.Equal(7, loaded.Hosted.LastEventId);
        Assert.Contains("call_1", loaded.Hosted.ExecutedToolIds);
        Assert.Equal("ok", loaded.Hosted.ToolResults["call_1"]);

        const string withOpaque = """
            {
              "id": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
              "title": "Old",
              "updatedAt": "2026-08-24T18:00:00Z",
              "messages": [],
              "legacyField": "keep-me",
              "hosted": {
                "model": "claude-opus-5",
                "effort": "max",
                "signature": "opaque-signature",
                "executedToolIds": ["a"]
              }
            }
            """;
        var opaque = ChatSessionStore.Decode(withOpaque);
        Assert.Equal("claude-opus-5", opaque.Hosted!.Model);
        Assert.Equal("a", Assert.Single(opaque.Hosted.ExecutedToolIds));
        Assert.NotNull(opaque.Extra);
        Assert.True(opaque.Extra!.ContainsKey("legacyField"));
        var again = Encoding.UTF8.GetString(ChatSessionStore.Encode(opaque));
        Assert.Contains("\"legacyField\"", again);
        Assert.Contains("opaque-signature", again);
    }
}

public sealed class AnthropicClientTests
{
    [Fact]
    public async Task CompleteAsync_MissingKey_ThrowsTypedErrorWithoutHttp()
    {
        var handler = new ScriptedHandler();
        using var http = new HttpClient(handler);
        var client = new AnthropicClient("", "https://example.test", httpClient: http);

        var ex = await Assert.ThrowsAsync<AnthropicClientException>(() =>
            client.CompleteAsync("sys", [], [], CancellationToken.None));

        Assert.Equal(AnthropicClientErrorKind.MissingApiKey, ex.Kind);
        Assert.Equal(AgentSetup.MissingKeyError, ex.Message);
        Assert.Empty(handler.Bodies);
        Assert.DoesNotContain("sure", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("happy to help", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompleteAsync_TextReply_ParsesAssistantText()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson("""
            {"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"Hello from the model."}],"stop_reason":"end_turn"}
            """);
        using var http = new HttpClient(handler);
        var client = new AnthropicClient(
            "sk-ant-test",
            "https://proxy.example.test/anthropic",
            "claude-sonnet-5",
            http);

        Assert.Equal("https://proxy.example.test/anthropic/v1/messages", client.MessagesUrl);

        var turn = await client.CompleteAsync(
            "sys",
            [new AnthropicToolSchema { Name = "get_timeline", Description = "Read timeline", InputSchema = new JsonObject { ["type"] = "object" } }],
            [AgentMessage.UserText("hi")]);

        Assert.Equal("end_turn", turn.StopReason);
        var block = Assert.Single(turn.Blocks);
        Assert.Equal(AgentContentBlock.KindText, block.Kind);
        Assert.Equal("Hello from the model.", block.Text);

        var body = Assert.Single(handler.Bodies);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("claude-sonnet-5", doc.RootElement.GetProperty("model").GetString());
        Assert.False(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal("2023-06-01", handler.Versions.Single());
        Assert.Equal("sk-ant-test", handler.ApiKeys.Single());
        Assert.Contains("/v1/messages", handler.Urls.Single(), StringComparison.Ordinal);
        Assert.True(doc.RootElement.TryGetProperty("output_config", out var outputConfig));
        Assert.Equal("low", outputConfig.GetProperty("effort").GetString());
    }

    [Fact]
    public async Task Conversation_ToolUseThenToolResultThenText_RecordsReceipts()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson("""
            {"content":[{"type":"tool_use","id":"toolu_1","name":"get_timeline","input":{}}],"stop_reason":"tool_use"}
            """);
        handler.EnqueueJson("""
            {"content":[{"type":"text","text":"The timeline is empty."}],"stop_reason":"end_turn"}
            """);
        using var http = new HttpClient(handler);
        var client = new AnthropicClient("sk-ant-test", "https://api.anthropic.com", httpClient: http);
        var conversation = new AgentConversation();
        var runner = new ScriptedToolRunner();

        await conversation.SendUserAsync(
            "What's on the timeline?",
            client,
            [new AnthropicToolSchema { Name = "get_timeline", Description = "Read", InputSchema = new JsonObject { ["type"] = "object" } }],
            runner);

        Assert.Equal(4, conversation.Session.Messages.Count);
        Assert.Equal(AgentMessageRole.user, conversation.Session.Messages[0].Role);
        Assert.Equal("What's on the timeline?", conversation.Session.Messages[0].Blocks[0].Text);

        var assistantTool = conversation.Session.Messages[1];
        Assert.Equal(AgentMessageRole.assistant, assistantTool.Role);
        Assert.Equal(AgentContentBlock.KindToolUse, assistantTool.Blocks[0].Kind);
        Assert.Equal("get_timeline", assistantTool.Blocks[0].Name);
        Assert.Equal("toolu_1", assistantTool.Blocks[0].Id);

        var toolResult = conversation.Session.Messages[2];
        Assert.Equal(AgentMessageRole.user, toolResult.Role);
        Assert.Equal(AgentContentBlock.KindToolResult, toolResult.Blocks[0].Kind);
        Assert.Equal("toolu_1", toolResult.Blocks[0].ToolUseId);
        Assert.False(toolResult.Blocks[0].IsError ?? true);
        Assert.Equal("{\"tracks\":[]}", toolResult.Blocks[0].Content![0].Text);

        var final = conversation.Session.Messages[3];
        Assert.Equal(AgentMessageRole.assistant, final.Role);
        Assert.Equal("The timeline is empty.", final.Blocks[0].Text);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Contains("tool_result", handler.Bodies[1], StringComparison.Ordinal);
        Assert.Contains("toolu_1", handler.Bodies[1], StringComparison.Ordinal);
        Assert.Equal("get_timeline", runner.LastName);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Conversation_Unconfigured_DoesNotInventAssistantContent()
    {
        using var http = new HttpClient(new ScriptedHandler());
        var client = new AnthropicClient(apiKey: "", httpClient: http);
        var conversation = new AgentConversation();

        var ex = await Assert.ThrowsAsync<AnthropicClientException>(() =>
            conversation.SendUserAsync("hello", client, [], new ScriptedToolRunner()));

        Assert.Equal(AnthropicClientErrorKind.MissingApiKey, ex.Kind);
        Assert.Empty(conversation.Session.Messages);
    }

    [Fact]
    public async Task Conversation_CancelDuringTool_WritesOrphanToolResult()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson("""
            {"content":[{"type":"tool_use","id":"toolu_cancel","name":"get_timeline","input":{}}],"stop_reason":"tool_use"}
            """);
        using var http = new HttpClient(handler);
        var client = new AnthropicClient("sk-ant-test", "https://api.anthropic.com", httpClient: http);
        var conversation = new AgentConversation();
        using var cts = new CancellationTokenSource();
        var runner = new BlockingToolRunner();

        var send = conversation.SendUserAsync(
            "What's on the timeline?",
            client,
            [new AnthropicToolSchema { Name = "get_timeline", Description = "Read", InputSchema = new JsonObject { ["type"] = "object" } }],
            runner,
            cancellationToken: cts.Token);

        await runner.Started.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

        Assert.Equal(3, conversation.Session.Messages.Count);
        var result = conversation.Session.Messages[2];
        Assert.Equal(AgentMessageRole.user, result.Role);
        Assert.Equal(AgentContentBlock.KindToolResult, result.Blocks[0].Kind);
        Assert.Equal("toolu_cancel", result.Blocks[0].ToolUseId);
        Assert.True(result.Blocks[0].IsError);
        Assert.Equal("Cancelled", result.Blocks[0].Content![0].Text);
    }

    [Fact]
    public async Task Conversation_FollowUpAfterCancel_CoalescesAdjacentUserRoles()
    {
        var handler = new ScriptedHandler();
        handler.EnqueueJson("""
            {"content":[{"type":"tool_use","id":"toolu_cancel","name":"get_timeline","input":{}}],"stop_reason":"tool_use"}
            """);
        handler.EnqueueJson("""
            {"content":[{"type":"text","text":"Stopped."}],"stop_reason":"end_turn"}
            """);
        using var http = new HttpClient(handler);
        var client = new AnthropicClient("sk-ant-test", httpClient: http);
        var conversation = new AgentConversation();
        using var cts = new CancellationTokenSource();
        var runner = new BlockingToolRunner();

        var send = conversation.SendUserAsync(
            "What's on the timeline?",
            client,
            [new AnthropicToolSchema { Name = "get_timeline", Description = "Read", InputSchema = new JsonObject { ["type"] = "object" } }],
            runner,
            cancellationToken: cts.Token);
        await runner.Started.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

        await conversation.SendUserAsync(
            "Try again without tools.",
            client,
            [],
            new ScriptedToolRunner());

        Assert.Equal(2, handler.Bodies.Count);
        using var doc = JsonDocument.Parse(handler.Bodies[1]);
        var roles = doc.RootElement.GetProperty("messages").EnumerateArray()
            .Select(m => m.GetProperty("role").GetString())
            .ToList();
        for (var i = 1; i < roles.Count; i++)
            Assert.NotEqual(roles[i - 1], roles[i]);
        Assert.Contains(roles, r => r == "user");
        Assert.Contains(roles, r => r == "assistant");
    }

    private sealed class BlockingToolRunner : IToolRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentToolExecutionResult> ExecuteAsync(
            string name,
            string inputJson,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return AgentToolExecutionResult.Ok("{}");
        }
    }

    private sealed class ScriptedToolRunner : IToolRunner
    {
        public List<string> Calls { get; } = [];
        public string? LastName { get; private set; }

        public Task<AgentToolExecutionResult> ExecuteAsync(string name, string inputJson, CancellationToken cancellationToken)
        {
            LastName = name;
            Calls.Add(name);
            return Task.FromResult(AgentToolExecutionResult.Ok("{\"tracks\":[]}"));
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _queue = new();
        public List<string> Bodies { get; } = [];
        public List<string> Urls { get; } = [];
        public List<string> ApiKeys { get; } = [];
        public List<string> Versions { get; } = [];

        public void EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _queue.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri?.ToString() ?? "");
            if (request.Headers.TryGetValues("x-api-key", out var keys))
                ApiKeys.AddRange(keys);
            if (request.Headers.TryGetValues("anthropic-version", out var versions))
                Versions.AddRange(versions);
            if (request.Content is not null)
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            if (_queue.Count == 0)
                throw new InvalidOperationException("No scripted HTTP response remaining.");
            return _queue.Dequeue();
        }
    }
}
