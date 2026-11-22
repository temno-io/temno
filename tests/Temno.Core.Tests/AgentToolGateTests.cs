using System.Net;
using System.Text.Json.Nodes;
using Temno.Core.Agent;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-400: the in-app agent's dispatch allowlist and per-action consent.</summary>
public sealed class AgentToolGateTests
{
    /// <summary>A user at the prompt: <c>decide</c> answers, or with none the prompt stays open until cancelled.</summary>
    internal sealed class Consent(Func<AgentToolConsentRequest, bool>? decide) : IAgentToolConsent
    {
        public readonly List<AgentToolConsentRequest> Asked = [];
        public Action? OnAsk;
        public Func<AgentToolConsentRequest, AgentToolApproval?>? Answer;
        public Exception? Throw;

        public async Task<AgentToolApproval?> AskAsync(AgentToolConsentRequest request, CancellationToken cancellationToken)
        {
            Asked.Add(request);
            OnAsk?.Invoke();
            if (Throw is not null) throw Throw;
            if (Answer is not null) return Answer(request);
            if (decide is null)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return null;
            }
            return decide(request) ? new AgentToolApproval(request.Id) : null;
        }
    }

    private sealed class Runner : IToolRunner
    {
        public readonly List<(string Name, string Input)> Calls = [];
        public Task<AgentToolExecutionResult> ExecuteAsync(string name, string inputJson, CancellationToken cancellationToken)
        {
            Calls.Add((name, inputJson));
            return Task.FromResult(AgentToolExecutionResult.Ok("ran"));
        }
    }

    private sealed class Model(params string[] bodies) : HttpMessageHandler
    {
        private readonly Queue<string> _bodies = new(bodies);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_bodies.Dequeue()) });
    }

    private static string ToolUse(string name, JsonObject input) => new JsonObject
    {
        ["stop_reason"] = "tool_use",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = "call-1", ["name"] = name, ["input"] = input }),
    }.ToJsonString();

    private const string Done = "{\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"Done\"}]}";

    private static AnthropicToolSchema Tool(string name) => new() { Name = name, Description = name };

    private static async Task<(AgentConversation Conversation, Runner Runner)> Byok(string name, JsonObject input,
        IReadOnlyList<AnthropicToolSchema> offered, AgentToolGate? gate = null, CancellationToken ct = default)
    {
        using var http = new HttpClient(new Model(ToolUse(name, input), Done));
        var conversation = new AgentConversation();
        var runner = new Runner();
        await conversation.SendUserAsync("Summarize this caption; do not export files.", new AnthropicClient("fixture-key", httpClient: http),
            offered, runner, maxIterations: 2, cancellationToken: ct, gate: gate);
        return (conversation, runner);
    }

    private static AgentContentBlock Result(AgentConversation conversation) =>
        conversation.Session.Messages.SelectMany(m => m.Blocks).Single(b => b.Kind == AgentContentBlock.KindToolResult);

    private static readonly JsonObject Export = new() { ["mode"] = "xml", ["outputPath"] = "C:/fixture/agent-target.xml" };

    // The WP-321 F2 reproduction's shape: the model asks for an export the user forbade. With no way to ask the
    // user, the call is refused before the runner, so the file it named is never written.
    [Fact]
    public async Task F2_SyntheticExport_WithNoConsent_NeverReachesTheRunner()
    {
        var (conversation, runner) = await Byok("export_project", Export.DeepClone().AsObject(), [Tool("export_project")]);
        Assert.Empty(runner.Calls);
        var result = Result(conversation);
        Assert.True(result.IsError);
        Assert.Contains("needs the user's approval", result.Content![0].Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("open_project")]
    [InlineData("manage_project")]
    [InlineData("new_project")]
    [InlineData("close_project")]
    [InlineData("get_projects")]
    [InlineData("export_project")]
    public async Task Byok_ToolTheTurnDidNotOffer_IsRefusedAtDispatch(string name)
    {
        var gate = new AgentToolGate(["get_timeline", "open_project", "manage_project", "new_project", "close_project", "get_projects"],
            new Consent(_ => true));
        var (conversation, runner) = await Byok(name, [], [Tool("get_timeline"), Tool("open_project"), Tool("manage_project")], gate);
        Assert.Empty(runner.Calls);
        Assert.Contains("is not available in this chat", Result(conversation).Content![0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Byok_OfferedTool_OutsideTheLocalSet_IsRefused()
    {
        var (_, runner) = await Byok("import_media", [], [Tool("import_media")], new AgentToolGate(["get_timeline"], new Consent(_ => true)));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Byok_Approval_RunsExactlyTheArgumentsTheUserSaw_Once()
    {
        var consent = new Consent(_ => true);
        var (_, runner) = await Byok("export_project", Export.DeepClone().AsObject(), [Tool("export_project")],
            new AgentToolGate(["export_project"], consent));
        var asked = Assert.Single(consent.Asked);
        Assert.Equal(("export_project", asked.InputJson), Assert.Single(runner.Calls));
        Assert.Equal("{\"mode\":\"xml\",\"outputPath\":\"C:/fixture/agent-target.xml\"}", asked.InputJson);
        Assert.Equal(AgentToolConsentKind.Export, asked.Kind);
        Assert.Contains(asked.Details, d => d.Key == "File" && d.Value == "agent-target.xml");
        Assert.Contains("C:/fixture/agent-target.xml", asked.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Byok_Denial_LeavesTheTargetAlone()
    {
        var (conversation, runner) = await Byok("export_project", Export.DeepClone().AsObject(), [Tool("export_project")],
            new AgentToolGate(["export_project"], new Consent(_ => false)));
        Assert.Empty(runner.Calls);
        Assert.Contains("declined", Result(conversation).Content![0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Byok_UnansweredPrompt_TimesOut_AsARefusal()
    {
        var (conversation, runner) = await Byok("export_project", Export.DeepClone().AsObject(), [Tool("export_project")],
            new AgentToolGate(["export_project"], new Consent(null), TimeSpan.FromMilliseconds(50)));
        Assert.Empty(runner.Calls);
        Assert.Contains("did not answer", Result(conversation).Content![0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Byok_CancelWhileAsking_RunsNothing_AndResolvesTheCall()
    {
        using var cts = new CancellationTokenSource();
        using var http = new HttpClient(new Model(ToolUse("export_project", Export.DeepClone().AsObject()), Done));
        var conversation = new AgentConversation();
        var runner = new Runner();
        var consent = new Consent(null) { OnAsk = cts.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversation.SendUserAsync("hello",
            new AnthropicClient("fixture-key", httpClient: http), [Tool("export_project")], runner, maxIterations: 2,
            cancellationToken: cts.Token, gate: new AgentToolGate(["export_project"], consent)));
        Assert.Empty(runner.Calls);
        Assert.Equal("Cancelled", Result(conversation).Content![0].Text);
    }

    // Approval is bound to its request: an approval naming another request, or one that came late, runs nothing.
    [Fact]
    public async Task ApprovalForAnotherRequest_OrReplayed_IsRefused()
    {
        var first = new Consent(_ => true);
        var gate = new AgentToolGate(["export_project"], first);
        Assert.True((await gate.AdmitAsync("export_project", "{}", null, default)).Allowed);
        var earlier = new AgentToolApproval(first.Asked[0].Id);
        var replay = new AgentToolGate(["export_project"], new Consent(null) { Answer = _ => earlier });
        var admission = await replay.AdmitAsync("export_project", "{}", null, default);
        Assert.False(admission.Allowed);
        Assert.Contains("declined", admission.Refusal, StringComparison.Ordinal);
        var forged = new AgentToolGate(["export_project"], new Consent(null) { Answer = _ => new AgentToolApproval(Guid.NewGuid()) });
        Assert.False((await forged.AdmitAsync("export_project", "{}", null, default)).Allowed);
    }

    [Fact]
    public async Task APromptThatFailsToOpen_Refuses()
    {
        var gate = new AgentToolGate(["generate_video"], new Consent(_ => true) { Throw = new InvalidOperationException("another dialog is open") });
        var admission = await gate.AdmitAsync("generate_video", "{\"model\":\"m\"}", null, default);
        Assert.False(admission.Allowed);
        Assert.Contains("could not ask", admission.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateKeys_AreRefused_BeforeAsking()
    {
        var consent = new Consent(_ => true);
        var admission = await new AgentToolGate(["export_project"], consent)
            .AdmitAsync("export_project", "{\"outputPath\":\"C:/a.xml\",\"outputPath\":\"C:/b.xml\"}", null, default);
        Assert.False(admission.Allowed);
        Assert.Empty(consent.Asked);
    }

    [Fact]
    public async Task AToolThatNeedsNoConsent_RunsWithItsArgumentsUnchanged()
    {
        var consent = new Consent(_ => false);
        var admission = await new AgentToolGate(["get_timeline"], consent).AdmitAsync("get_timeline", "{ \"a\" : 1 }", ["get_timeline"], default);
        Assert.True(admission.Allowed);
        Assert.Equal(("get_timeline", "{ \"a\" : 1 }"), (admission.Name, admission.InputJson));
        Assert.Empty(consent.Asked);
    }

    [Theory]
    [InlineData("get_timeline", "{}")]
    [InlineData("add_clips", "{}")]
    [InlineData("send_feedback", "{\"category\":\"failure\",\"summary\":\"s\"}")]
    [InlineData("import_media", "{\"source\":{\"bytes\":\"AAAA\",\"mimeType\":\"image/png\"}}")]
    [InlineData("import_media", "{\"source\":{\"matte\":{\"hex\":\"#000000\"}}}")]
    [InlineData("apply_color", "{\"clipIds\":[\"a\"],\"exposure\":0.5}")]
    [InlineData("quote_image", "{\"model\":\"m\"}")]
    [InlineData("quote_media", "{\"model\":\"m\"}")]
    [InlineData("resume_generation", "{}")]
    public void CallsThatStayInsideTheProjectAndSpendNothing_NeedNoConsent(string name, string input) =>
        Assert.Null(AgentToolGate.ConsentFor(name, input));

    [Theory]
    [InlineData("export_project", "{}", AgentToolConsentKind.Export, "Downloads")]
    [InlineData("export_project", "not json", AgentToolConsentKind.Export, "Downloads")]
    [InlineData("export_project", "{\"mode\":\"palmier\",\"outputPath\":\"D:/out/Reel\",\"overwrite\":false}", AgentToolConsentKind.Export, "Reel")]
    [InlineData("import_media", "{\"source\":{\"path\":\"C:/Users/me/secret.mp4\"}}", AgentToolConsentKind.OutsideFile, "secret.mp4")]
    [InlineData("import_media", "{\"path\":\"C:/Users/me/legacy.mp4\"}", AgentToolConsentKind.OutsideFile, "legacy.mp4")]
    [InlineData("import_media", "{\"source\":{\"path\":\"C:/big.mov\",\"reference\":true}}", AgentToolConsentKind.OutsideFile, "big.mov")]
    [InlineData("import_media", "{\"source\":{\"url\":\"https://example.test/a.png?x=secret\"}}", AgentToolConsentKind.Download, "https://example.test/a.png?x=secret")]
    [InlineData("apply_color", "{\"clipIds\":[\"a\"],\"lut\":{\"path\":\"\\\\\\\\host\\\\share\\\\look.cube\"}}", AgentToolConsentKind.OutsideFile, "look.cube")]
    [InlineData("apply_effect", "{\"clipIds\":[\"a\"],\"effects\":[{\"type\":\"color.lut\",\"params\":{\"path\":\"C:/l.cube\"}}]}", AgentToolConsentKind.OutsideFile, "l.cube")]
    [InlineData("generate_video", "{\"model\":\"veo\",\"prompt\":\"a cat\",\"quoteId\":\"q\"}", AgentToolConsentKind.Spend, "veo")]
    [InlineData("generate_image", "{\"model\":\"flux\",\"maxCredits\":40}", AgentToolConsentKind.Spend, "40 credits")]
    [InlineData("edit_image", "{}", AgentToolConsentKind.Spend, "image edit")]
    [InlineData("generate_audio", "{}", AgentToolConsentKind.Spend, "Audio")]
    [InlineData("upscale_media", "{}", AgentToolConsentKind.Spend, "upscale")]
    [InlineData("generate_google", "{}", AgentToolConsentKind.Spend, "image or video")]
    public void ExportsOutsideFilesDownloadsAndPaidGenerations_NameExactlyWhatRuns(string name, string input,
        AgentToolConsentKind kind, string named)
    {
        var request = AgentToolGate.ConsentFor(name, input)!;
        Assert.Equal(kind, request.Kind);
        Assert.Equal(name, request.ToolName);
        Assert.Contains(request.Details, d => d.Value.Contains(named, StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(request.Title));
        Assert.False(string.IsNullOrWhiteSpace(request.Action));
        // What runs on approval is the normalized object the prompt describes.
        Assert.Equal(JsonNode.Parse(request.InputJson)!.ToJsonString(), request.InputJson);
    }

    [Fact]
    public void AnExportThatReplaces_SaysSo_AndOneThatRefusesDoesNot()
    {
        Assert.Contains(AgentToolGate.ConsentFor("export_project", "{\"outputPath\":\"C:/a.xml\"}")!.Details, d => d.Value == "Replaced");
        Assert.DoesNotContain(AgentToolGate.ConsentFor("export_project", "{\"outputPath\":\"C:/a.xml\",\"overwrite\":false}")!.Details,
            d => d.Value == "Replaced");
    }

    [Fact]
    public void TheProjectToolsAndSpendTools_AreTheDocumentedSets()
    {
        Assert.Equal(["close_project", "get_projects", "manage_project", "new_project", "open_project"], AgentToolGate.ProjectTools.Order());
        Assert.Equal(["edit_image", "generate_audio", "generate_google", "generate_image", "generate_video", "upscale_media"],
            AgentToolGate.SpendTools.Order());
    }
}
