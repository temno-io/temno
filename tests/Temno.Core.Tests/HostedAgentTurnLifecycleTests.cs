using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Temno.Core.Agent;
using Temno.Core.Account;
using Xunit;

namespace Temno.Core.Tests;

public sealed class HostedAgentTurnLifecycleTests
{
    private static HostedAgentProfile Profile(int tokens = 1024) => new()
    {
        Id = "fixture", DefaultEffort = "medium", Efforts = ["medium"],
        MaxOutputTokens = tokens, Qualified = true,
    };

    private static Task Send(HostedAgentConversation conversation, Fixture fixture,
        IToolRunner? runner = null, Action? persist = null, string text = "hello",
        CancellationToken ct = default, int tokens = 1024) =>
        conversation.SendUserAsync(text, fixture.Client, Profile(tokens), ["edit_clip"],
            runner ?? new Runner(), 25, null, persist, ct);

    [Fact]
    public async Task ConsecutiveTurns_HaveDistinctKeys_AndOneReservationEach()
    {
        using var fixture = new Fixture();
        var conversation = new HostedAgentConversation();
        await Send(conversation, fixture);
        await Send(conversation, fixture, text: "next");
        Assert.Equal(2, fixture.Reservations.Count);
        Assert.Equal(2, fixture.CreateKeys.Distinct().Count());
        Assert.Equal(2, conversation.Session.Messages.Count(m => m.Role == AgentMessageRole.user));
    }

    [Fact]
    public async Task LostCreateResponse_RestartReusesPersistedQuoteAndKey()
    {
        using var fixture = new Fixture { LoseCreateResponse = true };
        var conversation = new HostedAgentConversation();
        byte[] saved = [];
        await Assert.ThrowsAsync<HttpRequestException>(() => Send(conversation, fixture,
            persist: () => saved = ChatSessionStore.Encode(conversation.Session)));
        var restarted = new HostedAgentConversation(ChatSessionStore.Decode(saved));
        await restarted.ResumeAsync(fixture.Client, new Runner(), null);
        Assert.True(restarted.Session.Hosted!.Terminal);
        Assert.Single(fixture.Reservations);
        Assert.Equal(2, fixture.CreateKeys.Count);
        Assert.Single(fixture.CreateKeys.Distinct());
        Assert.Single(restarted.Session.Messages, m => m.Role == AgentMessageRole.user);
    }

    [Fact]
    public async Task ThrowingTool_IsNotExecutedAgain_AfterRestart()
    {
        using var fixture = new Fixture { Tool = true };
        var conversation = new HostedAgentConversation();
        var runner = new Runner { Throw = true };
        byte[] saved = [];
        try { await Send(conversation, fixture, runner, () => saved = ChatSessionStore.Encode(conversation.Session)); }
        catch (HostedApiException) { }
        var restarted = new HostedAgentConversation(ChatSessionStore.Decode(saved));
        try { await restarted.ResumeAsync(fixture.Client, runner, null); }
        catch (HostedApiException) { }
        Assert.Equal(1, runner.Calls);
        Assert.Contains(restarted.Session.Messages.SelectMany(m => m.Blocks),
            b => b.Kind == AgentContentBlock.KindToolResult && b.IsError == true);
    }

    [Fact]
    public async Task LostToolSubmit_RecoversInOpenConversationWithoutRepeatingToolOrAdmission()
    {
        using var fixture = new Fixture { Tool = true, FailSubmit = true };
        var conversation = new HostedAgentConversation();
        var runner = new Runner();
        await Send(conversation, fixture, runner);
        Assert.Equal(1, fixture.QuoteCount);
        Assert.Single(fixture.CreateKeys);
        Assert.Single(fixture.Reservations);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(2, fixture.Submissions.Count);
        Assert.Equal(fixture.Submissions[0], fixture.Submissions[1]);
        Assert.Equal(new[] { 0, 1 }, fixture.StreamCursors);
        Assert.Single(fixture.StreamReaders.Distinct());
        Assert.True(HostedAgentClient.ValidStreamReader(fixture.StreamReaders[0]));
        Assert.True(conversation.Session.Hosted!.Terminal);
        Assert.Equal(4, conversation.Session.Hosted.LastEventId);
    }

    [Fact]
    public async Task CancelingToolSubmitRecovery_LeavesIdenticalSavedResultForLaterResume()
    {
        using var fixture = new Fixture { Tool = true, FailSubmit = true };
        using var stop = new CancellationTokenSource();
        var conversation = new HostedAgentConversation();
        var runner = new Runner();
        byte[] saved = [];
        var send = Send(conversation, fixture, runner,
            () => saved = ChatSessionStore.Encode(conversation.Session), ct: stop.Token);
        await fixture.SubmitAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Single(fixture.Submissions);
        var restarted = new HostedAgentConversation(ChatSessionStore.Decode(saved));
        Assert.Equal(0, restarted.Session.Hosted!.LastEventId);
        await restarted.ResumeAsync(fixture.Client, runner, null);
        Assert.True(restarted.Session.Hosted.Terminal);
        Assert.Equal(1, runner.Calls);
        Assert.Single(fixture.CreateKeys);
        Assert.Equal(2, fixture.Submissions.Count);
        Assert.Equal(fixture.Submissions[0], fixture.Submissions[1]);
    }

    [Fact]
    public async Task OverlappingResume_HasOneStreamConsumer()
    {
        using var fixture = new Fixture { HoldStream = true };
        var conversation = new HostedAgentConversation();
        var send = Send(conversation, fixture);
        await fixture.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var resume = conversation.ResumeAsync(fixture.Client, new Runner(), null);
        await Task.Delay(50);
        fixture.ReleaseStream.TrySetResult();
        await Task.WhenAll(send, resume);
        Assert.Equal(1, fixture.MaxStreams);
    }

    [Fact]
    public async Task SmallPositiveTokenLimit_IsRejectedBeforeAppendingUserOrDispatch()
    {
        using var fixture = new Fixture();
        var conversation = new HostedAgentConversation();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Send(conversation, fixture, tokens: 64));
        Assert.Empty(conversation.Session.Messages);
        Assert.Equal(0, fixture.QuoteCount);
    }

    [Fact]
    public async Task StreamLoss_ReconnectsAutomatically_ToSameAssistant_WithoutRepeatingUser()
    {
        using var fixture = new Fixture { DropStreamOnce = true };
        var conversation = new HostedAgentConversation();
        byte[] saved = [];
        await Send(conversation, fixture,
            persist: () => saved = ChatSessionStore.Encode(conversation.Session));
        var restarted = new HostedAgentConversation(ChatSessionStore.Decode(saved));
        Assert.True(restarted.Session.Hosted!.Terminal);
        Assert.Equal(new[] { 0, 2 }, fixture.StreamCursors);
        Assert.Single(fixture.CreateKeys);
        Assert.Single(fixture.Reservations);
        Assert.Single(restarted.Session.Messages, m => m.Role == AgentMessageRole.user);
        Assert.Equal("hello back continued", Assert.Single(restarted.Session.Messages,
            m => m.Role == AgentMessageRole.assistant).Blocks[0].Text);
    }

    // WP-431: the stream subscription key is saved before the first open, sent on every reconnect and after a relaunch,
    // and made anew for another account, auth session or turn. Cursors stay integers; the key grants nothing.
    [Fact]
    public async Task StreamReaderKey_IsSavedBeforeTheFirstOpen_AndKeptAcrossReconnectAndRelaunch()
    {
        using var fixture = new Fixture { HoldStream = true };
        var conversation = new HostedAgentConversation();
        byte[] saved = [];
        using var hold = new CancellationTokenSource();
        var send = Send(conversation, fixture, persist: () => saved = ChatSessionStore.Encode(conversation.Session), ct: hold.Token);
        await fixture.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var key = ChatSessionStore.Decode(saved).Hosted!.StreamReader;
        Assert.True(HostedAgentClient.ValidStreamReader(key));
        Assert.Equal(key, Assert.Single(fixture.StreamReaders));
        hold.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);

        // A relaunch reads the saved chat and reconnects with the same key and an integer cursor, and drops once more.
        fixture.HoldStream = false;
        fixture.DropStreamOnce = true;
        var restarted = new HostedAgentConversation(ChatSessionStore.Decode(saved));
        await restarted.ResumeAsync(fixture.Client, new Runner(), null, () => saved = ChatSessionStore.Encode(restarted.Session));
        Assert.True(restarted.Session.Hosted!.Terminal);
        Assert.All(fixture.StreamReaders, reader => Assert.Equal(key, reader));
        Assert.Equal(3, fixture.StreamReaders.Count);
        Assert.Equal(key, ChatSessionStore.Decode(saved).Hosted!.StreamReader);
        Assert.Contains("\"streamReader\"", System.Text.Encoding.UTF8.GetString(saved));
    }

    [Fact]
    public async Task StreamReaderKey_IsNewForEachTurn_AndForAnotherAccountOrSession()
    {
        using var fixture = new Fixture();
        var conversation = new HostedAgentConversation();
        await Send(conversation, fixture);
        var first = conversation.Session.Hosted!.StreamReader;
        await Send(conversation, fixture, text: "again");
        var second = conversation.Session.Hosted!.StreamReader;
        Assert.NotEqual(first, second);
        Assert.Equal(new[] { first, second }, fixture.StreamReaders);

        var turn = Guid.NewGuid();
        var scope = HostedAgentClient.StreamReaderScope("account", "session", turn);
        Assert.Matches("^[0-9a-f]{32}$", scope);
        Assert.NotEqual(scope, HostedAgentClient.StreamReaderScope("other", "session", turn));
        Assert.NotEqual(scope, HostedAgentClient.StreamReaderScope("account", "other", turn));
        Assert.NotEqual(scope, HostedAgentClient.StreamReaderScope("account", "session", Guid.NewGuid()));
        Assert.DoesNotContain("account", scope);
    }

    [Fact]
    public async Task A_saved_key_from_another_session_is_replaced_before_the_stream_opens()
    {
        using var fixture = new Fixture { HoldStream = true };
        var conversation = new HostedAgentConversation();
        byte[] saved = [];
        using var hold = new CancellationTokenSource();
        var send = Send(conversation, fixture, persist: () => saved = ChatSessionStore.Encode(conversation.Session), ct: hold.Token);
        await fixture.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hold.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        var session = ChatSessionStore.Decode(saved);
        var old = session.Hosted!.StreamReader;
        session.Hosted.StreamReaderScope = HostedAgentClient.StreamReaderScope(Fixture.Owner, "another-session", Guid.Parse(session.Hosted.TurnId!));
        fixture.HoldStream = false;
        var restarted = new HostedAgentConversation(session);
        await restarted.ResumeAsync(fixture.Client, new Runner(), null);
        Assert.NotEqual(old, restarted.Session.Hosted!.StreamReader);
        Assert.Equal(restarted.Session.Hosted.StreamReader, fixture.StreamReaders[^1]);
    }

    [Fact]
    public void A_key_is_32_lowercase_hex_and_the_session_id_is_read_from_the_token()
    {
        var key = HostedAgentClient.NewStreamReader();
        Assert.True(HostedAgentClient.ValidStreamReader(key));
        Assert.NotEqual(key, HostedAgentClient.NewStreamReader());
        foreach (var bad in new[] { null, "", key.ToUpperInvariant(), key[..31], key + "0", "anon-" + key[..27] })
            Assert.False(HostedAgentClient.ValidStreamReader(bad));
        static string Part(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var token = "e30." + Part("{\"sub\":\"u\",\"session_id\":\"43100000-0000-4000-8000-000000000002\"}") + ".sig";
        Assert.Equal("43100000-0000-4000-8000-000000000002", new HostedAgentClient("https://fixture.test", () => token).SessionId());
        Assert.Null(new HostedAgentClient("https://fixture.test", () => "not-a-jwt").SessionId());
        Assert.Throws<ArgumentException>(() => new HostedAgentClient("https://fixture.test", () => token)
            .StreamEventsAsync(Guid.NewGuid(), 0, "BAD", default).GetAsyncEnumerator().MoveNextAsync().AsTask().GetAwaiter().GetResult());
    }

    [Fact]
    public async Task Interrupted_free_retry_reports_final_zero_charge_even_when_operator_usage_is_unknown()
    {
        using var fixture = new Fixture { TerminalJson = "{\"status\":\"failed\",\"charge\":0,\"error\":\"worker_lost\",\"usageUnknown\":true}" };
        var conversation = new HostedAgentConversation();
        var progress = new ProgressRecorder();
        await conversation.SendUserAsync("hello", fixture.Client, Profile(), [], new Runner(), 25, progress);
        Assert.False(conversation.HasPendingTurn);
        Assert.Contains("No credits charged", progress.Last!.Status);
        Assert.Contains("only the new reply", progress.Last.Status);
        Assert.DoesNotContain("still reconciling", progress.Last.Status);
        Assert.Single(fixture.Reservations);
    }

    private sealed class ProgressRecorder : IProgress<HostedTurnProgress>
    {
        public HostedTurnProgress? Last;
        public void Report(HostedTurnProgress value) => Last = value;
    }

    [Fact]
    public async Task CrashAfterExecution_BeforeResultCheckpoint_NeverRepeatsMutation()
    {
        using var fixture = new Fixture { Tool = true };
        var conversation = new HostedAgentConversation();
        var runner = new Runner();
        byte[] saved = [];
        await Assert.ThrowsAsync<IOException>(() => Send(conversation, fixture, runner, () =>
        {
            if (conversation.Session.Hosted!.ToolResults.Count > 0) throw new IOException("crash before result flush");
            saved = ChatSessionStore.Encode(conversation.Session);
        }));
        var restarted = new HostedAgentConversation(ChatSessionStore.Decode(saved));
        await restarted.ResumeAsync(fixture.Client, runner, null);
        Assert.Equal(1, runner.Calls);
        Assert.True(restarted.Session.Hosted!.CancelRequested);
        Assert.Contains("may have changed", restarted.Session.Hosted.RecoveryError);
        Assert.Empty(fixture.Submissions);
    }

    [Fact]
    public async Task SendResumeCancelOverlap_CancelsOneConsumer_WithoutNewReservation()
    {
        using var fixture = new Fixture { HoldStream = true };
        var conversation = new HostedAgentConversation();
        var send = Send(conversation, fixture);
        await fixture.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HostedApiException>(() => Send(conversation, fixture, text: "duplicate"));
        var resume = conversation.ResumeAsync(fixture.Client, new Runner(), null);
        var cancel = conversation.CancelAsync(fixture.Client);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        await Task.WhenAll(resume, cancel);
        Assert.Single(fixture.Reservations);
        Assert.Equal(1, fixture.MaxStreams);
        Assert.True(conversation.Session.Hosted!.Terminal);
    }

    [Fact]
    public async Task CancelAfterLostCreate_RestartResolvesOriginalThenCancels()
    {
        using var fixture = new Fixture { LoseCreateResponse = true };
        var conversation = new HostedAgentConversation();
        await Assert.ThrowsAsync<HttpRequestException>(() => Send(conversation, fixture));
        await conversation.CancelAsync(fixture.Client);
        Assert.False(conversation.Session.Hosted!.Terminal);
        var restarted = new HostedAgentConversation(ChatSessionStore.Snapshot(conversation.Session));
        await restarted.ResumeAsync(fixture.Client, new Runner(), null);
        Assert.Single(fixture.Reservations);
        Assert.Single(fixture.CreateKeys.Distinct());
        Assert.True(restarted.Session.Hosted!.Terminal);
        Assert.Equal(0, fixture.MaxStreams);
    }

    [Fact]
    public async Task QuoteFailure_RetryKeepsFrozenIntent_AndSingleUserMessage()
    {
        using var fixture = new Fixture { FailQuote = true };
        var conversation = new HostedAgentConversation();
        await Assert.ThrowsAsync<HttpRequestException>(() => Send(conversation, fixture));
        var restarted = new HostedAgentConversation(ChatSessionStore.Snapshot(conversation.Session));
        await Send(restarted, fixture, text: "do not append this draft", tokens: 4096);
        Assert.Equal(fixture.Quotes[0], fixture.Quotes[1]);
        Assert.Single(restarted.Session.Messages, m => m.Role == AgentMessageRole.user);
        Assert.Single(fixture.Reservations);
    }

    [Fact]
    public async Task RepeatedToolIdInNewTurn_ExecutesOnceInEachTurn()
    {
        using var fixture = new Fixture { Tool = true };
        var conversation = new HostedAgentConversation();
        var runner = new Runner();
        await Send(conversation, fixture, runner);
        await Send(conversation, fixture, runner, text: "next");
        Assert.Equal(2, runner.Calls);
        Assert.Equal(2, fixture.Reservations.Count);
    }

    [Fact]
    public async Task SnapshotAndDiskSave_AreCoherentDuringStreaming()
    {
        using var fixture = new Fixture { DeltaCount = 200 };
        var conversation = new HostedAgentConversation();
        var folder = Path.Combine(Path.GetTempPath(), "wp237-" + Guid.NewGuid());
        try
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var send = Task.Run(() => Send(conversation, fixture, persist: () =>
            {
                ChatSessionStore.Save(folder, conversation.Session);
                started.TrySetResult();
                Thread.Yield();
            }));
            await started.Task;
            for (var i = 0; i < 250; i++)
            {
                var snapshot = ChatSessionStore.Snapshot(conversation.Session);
                var strings = snapshot.Messages.SelectMany(m => m.Blocks).Select(b => b.Text).ToArray();
                await Task.Yield();
                Assert.Equal(strings, snapshot.Messages.SelectMany(m => m.Blocks).Select(b => b.Text));
                Assert.All(snapshot.Messages, m => Assert.NotEmpty(m.Blocks));
            }
            await send;
            var saved = Assert.Single(ChatSessionStore.Load(folder));
            Assert.True(saved.Hosted!.Terminal);
            Assert.Equal(conversation.Session.Hosted!.LastEventId, saved.Hosted.LastEventId);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task InvalidEvent_DoesNotAcknowledgeCursor()
    {
        using var fixture = new Fixture { InvalidEvent = true };
        var conversation = new HostedAgentConversation();
        await Assert.ThrowsAsync<HostedApiException>(() => Send(conversation, fixture));
        Assert.Equal(0, conversation.Session.Hosted!.LastEventId);
        Assert.False(conversation.Session.Hosted.Terminal);
    }

    [Fact]
    public async Task LegacyAcknowledgedToolWithoutResult_CancelsInsteadOfWaitingOrReexecuting()
    {
        using var fixture = new Fixture();
        var conversation = new HostedAgentConversation();
        var metadata = conversation.Session.Hosted!;
        metadata.TurnId = Guid.NewGuid().ToString("D");
        metadata.LastEventId = 1;
        // WP-400: only a turn this install recorded resumes on its own.
        metadata.AccountId = Fixture.Owner;
        metadata.IdempotencyKey = Guid.NewGuid().ToString("D");
        HostedTurnOrigins.Record(metadata.IdempotencyKey, Fixture.Owner, HostedTurnOrigins.Digest(metadata.Submission));
        HostedTurnOrigins.RecordTurn(metadata.IdempotencyKey, Guid.Parse(metadata.TurnId));
        conversation.Session.Messages.Add(AgentMessage.UserText("legacy"));
        conversation.Session.Messages.Add(AgentMessage.Assistant(AgentContentBlock.ToolUse("old", "edit_clip", "{}")));
        var runner = new Runner();
        await conversation.ResumeAsync(fixture.Client, runner, null);
        Assert.Equal(0, runner.Calls);
        Assert.True(metadata.CancelRequested);
        Assert.True(metadata.Terminal);
        Assert.NotNull(metadata.RecoveryError);
        Assert.Equal(0, fixture.MaxStreams);
    }

    [Fact]
    public async Task AuthoritativeExpiredQuote_ResolvesWithoutReservation_AndAllowsNewTurn()
    {
        using var fixture = new Fixture { CreateFailure = (HttpStatusCode.Gone, "{\"error\":\"expired\"}") };
        var conversation = new HostedAgentConversation();
        await Assert.ThrowsAsync<HostedApiException>(() => Send(conversation, fixture));
        Assert.True(conversation.Session.Hosted!.Terminal);
        Assert.Empty(fixture.Reservations);
        var oldKey = conversation.Session.Hosted.IdempotencyKey;
        await Send(conversation, fixture, text: "new authorized turn");
        Assert.Single(fixture.Reservations);
        Assert.NotEqual(oldKey, conversation.Session.Hosted!.IdempotencyKey);
    }

    // WP-254 review: the agent gateway sends 410 "expired" (TA410), which the desktop used to miss, leaving the turn
    // pending forever. A refused reservation (402) rolls agent_create back whole, so it is equally definite.
    [Theory]
    [InlineData(410, "{\"error\":\"expired\"}", "quote_expired")]
    [InlineData(410, "{\"error\":\"quote_expired\"}", "quote_expired")]
    [InlineData(402, "{\"error\":\"insufficient_credits\"}", "insufficient_credits")]
    public async Task RefusedAdmission_WithdrawsTheMessage_ChargesNothing_AndLeavesNothingPending(int status, string body, string code)
    {
        using var fixture = new Fixture { CreateFailure = ((HttpStatusCode)status, body) };
        var conversation = new HostedAgentConversation();
        var error = await Assert.ThrowsAsync<HostedApiException>(() => Send(conversation, fixture, text: "refused turn"));
        Assert.Equal((code, "refused turn"), (error.Code, error.WithdrawnText));
        Assert.False(conversation.HasPendingTurn);
        Assert.Equal(0, conversation.Session.Hosted!.LastChargeCredits);
        Assert.DoesNotContain(conversation.Session.Messages, m => m.Role == AgentMessageRole.user);
        Assert.Empty(fixture.Reservations);
        if (status == 410)
            Assert.Equal(HostedApiErrors.Describe(null, "quote_expired", null), error.Message);
    }

    [Fact]
    public async Task LegacyLostKey_CancelDoesNotClaimNoChargeOrResolvedOutcome()
    {
        using var fixture = new Fixture();
        var conversation = new HostedAgentConversation(new ChatSession
        {
            Hosted = new HostedChatMetadata { QuoteId = Guid.NewGuid().ToString("D"), Terminal = false },
        });
        await conversation.CancelAsync(fixture.Client);
        Assert.True(conversation.HasPendingTurn);
        Assert.Null(conversation.Session.Hosted!.LastChargeCredits);
        Assert.Empty(fixture.Reservations);
    }

    [Fact]
    public async Task DifferentAccount_CannotResolveOrCancelOriginalReservation()
    {
        using var fixture = new Fixture { LoseCreateResponse = true };
        var conversation = new HostedAgentConversation();
        await Assert.ThrowsAsync<HttpRequestException>(() => Send(conversation, fixture));
        conversation.Session.Hosted!.AccountId = "original-owner";
        var wrongAccount = new HostedAgentClient("https://fixture.test", () => "fixture",
            new HttpClient(fixture), accountId: "different-owner");
        await Assert.ThrowsAsync<HostedApiException>(() => conversation.ResumeAsync(wrongAccount, new Runner(), null));
        await conversation.CancelAsync(wrongAccount);
        Assert.True(conversation.HasPendingTurn);
        Assert.Null(conversation.Session.Hosted.LastChargeCredits);
        Assert.Single(fixture.CreateKeys);
        Assert.Single(fixture.Reservations);
    }

    [Fact]
    public async Task ExpiredServerCancellation_IsTerminal()
    {
        using var fixture = new Fixture { CancelStatus = "expired" };
        var conversation = new HostedAgentConversation(new ChatSession
        {
            Hosted = new HostedChatMetadata { TurnId = Guid.NewGuid().ToString("D"), AccountId = Fixture.Owner, Terminal = false },
        });
        await conversation.CancelAsync(fixture.Client);
        Assert.True(conversation.Session.Hosted!.Terminal);
    }

    // WP-400: a package's chat/*.json as another install (or an attacker) could leave it: no AccountId, a well-formed
    // retry key, a frozen Submission with an explicit cap, and no QuoteId or TurnId.
    internal static string PlantedChatJson(Guid key, long cap = 500, string userText = "planted request") => $$"""
        {
          "id": "{{Guid.NewGuid():D}}",
          "title": "Planted",
          "updatedAt": "2026-11-06T10:00:00Z",
          "messages": [ { "id": "11111111-2222-3333-4444-555555555555", "role": "user",
                          "blocks": [ { "kind": "text", "text": "{{userText}}" } ] } ],
          "hosted": {
            "model": "fixture", "effort": "medium", "idempotencyKey": "{{key:D}}", "terminal": false,
            "spendingCapCredits": {{cap}},
            "userMessageId": "11111111-2222-3333-4444-555555555555",
            "submission": { "model": "fixture", "effort": "medium", "reasoningEnabled": true, "maxOutputTokens": 0,
                            "spendingCapCredits": {{cap}}, "tools": ["edit_clip", "manage_project"],
                            "messages": [ { "role": "user", "content": "a different hidden request" } ] }
          }
        }
        """;

    // Written first and run against 63ba2352's code, where it failed with quotes=1 creates=1 tools=1
    // (docs/verification/wp-400/package-chat-failing-first.log).
    [Fact]
    public async Task ForeignPackageChat_OpenAndResume_SendNothing_UntilRun()
    {
        using var fixture = new Fixture { Tool = true };
        var plantedKey = Guid.NewGuid();
        var planted = ChatSessionStore.Decode(PlantedChatJson(plantedKey));
        var library = ChatSessionLibrary.Open([planted], out _);
        var conversation = new HostedAgentConversation(library.Current);
        Assert.True(conversation.HasPendingTurn);
        Assert.False(conversation.PendingTurnStartedHere);
        var runner = new Runner();
        try { await conversation.ResumeAsync(fixture.Client, runner, null); }
        catch (HostedApiException) { }
        var send = await Assert.ThrowsAsync<HostedApiException>(() => Send(conversation, fixture, runner, text: "typed meanwhile"));
        Assert.Equal(HostedAgentConversation.NotStartedHereCode, send.Code);
        Assert.True(fixture.QuoteCount == 0 && fixture.CreateKeys.Count == 0 && runner.Calls == 0,
            $"quotes={fixture.QuoteCount} creates={fixture.CreateKeys.Count} tools={runner.Calls}");

        // The explicit run: the visible message, this chat's tools, an automatic hold, a key recorded here.
        await conversation.RunPendingAsync(fixture.Client, Profile(), ["edit_clip"], runner,
            HostedAgentConversation.AutomaticHold, null);
        Assert.Equal(1, fixture.QuoteCount);
        Assert.Single(fixture.CreateKeys);
        Assert.Equal(1, runner.Calls);
        var quote = JsonNode.Parse(fixture.Quotes[0])!;
        Assert.Equal(0, quote["spendingCapCredits"]!.GetValue<long>());
        Assert.Equal(["edit_clip"], quote["tools"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Contains("planted request", fixture.Quotes[0], StringComparison.Ordinal);
        Assert.DoesNotContain("a different hidden request", fixture.Quotes[0], StringComparison.Ordinal);
        var hosted = conversation.Session.Hosted!;
        Assert.Equal(Fixture.Owner, hosted.AccountId);
        Assert.NotEqual(plantedKey.ToString("D"), hosted.IdempotencyKey);
        Assert.Equal(hosted.IdempotencyKey, fixture.CreateKeys[0]);
        // Recorded before the quote (TurnSentHere_IsRecordedBeforeItsFirstRequest), and forgotten once it settled.
        Assert.False(HostedTurnOrigins.Matches(hosted.IdempotencyKey, Fixture.Owner, HostedTurnOrigins.Digest(hosted.Submission), hosted.TurnId));
        Assert.Null(hosted.SpendingCapCredits);
        Assert.True(hosted.Terminal);
        Assert.Single(conversation.Session.Messages, m => m.Role == AgentMessageRole.user
            && m.Blocks.Exists(b => b.Kind == AgentContentBlock.KindText));
    }

    [Fact]
    public async Task ForeignTurn_RunWithAConfirmedHold_QuotesThatHold_NeverTheFilesCap()
    {
        using var fixture = new Fixture();
        var conversation = new HostedAgentConversation(ChatSessionStore.Decode(PlantedChatJson(Guid.NewGuid(), cap: 900_000)));
        await conversation.RunPendingAsync(fixture.Client, Profile(), ["edit_clip"], new Runner(), 1500, null);
        Assert.Equal(1500, JsonNode.Parse(Assert.Single(fixture.Quotes))!["spendingCapCredits"]!.GetValue<long>());
        Assert.Equal(1500, conversation.Session.Hosted!.SpendingCapCredits);
    }

    // A file claiming a create was attempted, with no quote to replay, is not a state a send leaves: Run sends it as new,
    // never the file's frozen submission or cap, even when it names the signed-in account.
    [Fact]
    public async Task ForeignTurn_ClaimingAnAttemptWithNoQuote_RunsAsANewSend()
    {
        using var fixture = new Fixture();
        var session = ChatSessionStore.Decode(PlantedChatJson(Guid.NewGuid(), cap: 900_000));
        session.Hosted!.SubmissionAttempted = true;
        session.Hosted.AccountId = Fixture.Owner;
        var conversation = new HostedAgentConversation(session);
        Assert.False(HostedAgentConversation.MayBeAdmitted(session.Hosted));
        await conversation.RunPendingAsync(fixture.Client, Profile(), ["edit_clip"], new Runner(), HostedAgentConversation.AutomaticHold, null);
        var quote = Assert.Single(fixture.Quotes);
        Assert.Equal(0, JsonNode.Parse(quote)!["spendingCapCredits"]!.GetValue<long>());
        Assert.DoesNotContain("a different hidden request", quote, StringComparison.Ordinal);
    }

    // T2 round 1, finding 1: a key this install recorded stops vouching once its turn settles or is withdrawn, so a
    // chat edited to look unfinished again, with its own key and account, needs a click. The same goes for a turn
    // still pending here whose frozen submission or turn id was changed.
    [Theory]
    [InlineData("settled")]
    [InlineData("withdrawn")]
    public async Task ARecordedKey_StopsVouching_OnceItsTurnSettlesOrIsWithdrawn(string ending)
    {
        using var fixture = new Fixture { CreateFailure = ending == "withdrawn" ? (HttpStatusCode.PaymentRequired, "{\"error\":\"insufficient_credits\"}") : null };
        var conversation = new HostedAgentConversation();
        byte[] saved = [];
        try { await Send(conversation, fixture, persist: () => saved = ChatSessionStore.Encode(conversation.Session)); }
        catch (HostedApiException) when (ending == "withdrawn") { }
        Assert.False(conversation.HasPendingTurn);
        var returned = ChatSessionStore.Decode(saved);
        returned.Hosted!.Terminal = false;
        returned.Hosted.LastChargeCredits = null;
        returned.Hosted.LastEventId = 0;
        returned.Hosted.ExecutedToolIds.Clear();
        returned.Hosted.ToolResults.Clear();
        if (ending == "withdrawn") returned.Messages.Add(AgentMessage.UserText("hello"));
        var reopened = new HostedAgentConversation(returned);
        Assert.True(reopened.HasPendingTurn);
        Assert.False(reopened.PendingTurnStartedHere);
        var requests = (fixture.QuoteCount, fixture.CreateKeys.Count, fixture.StreamCursors.Count);
        await Assert.ThrowsAsync<HostedApiException>(() => reopened.ResumeAsync(fixture.Client, new Runner(), null));
        Assert.Equal(requests, (fixture.QuoteCount, fixture.CreateKeys.Count, fixture.StreamCursors.Count));
    }

    [Fact]
    public async Task APendingTurnSentHere_WhoseSubmissionOrTurnIdWasEdited_IsNoLongerOneSentHere()
    {
        using var fixture = new Fixture { LoseCreateResponse = true };
        var conversation = new HostedAgentConversation();
        byte[] saved = [];
        await Assert.ThrowsAsync<HttpRequestException>(() => Send(conversation, fixture,
            persist: () => saved = ChatSessionStore.Encode(conversation.Session)));
        Assert.True(new HostedAgentConversation(ChatSessionStore.Decode(saved)).PendingTurnStartedHere);

        var edited = ChatSessionStore.Decode(saved);
        edited.Hosted!.Submission!.SpendingCapCredits = 500_000;
        Assert.False(new HostedAgentConversation(edited).PendingTurnStartedHere);

        var otherTurn = ChatSessionStore.Decode(saved);
        otherTurn.Hosted!.TurnId = Guid.NewGuid().ToString("D");
        Assert.False(new HostedAgentConversation(otherTurn).PendingTurnStartedHere);

        // Recovering learns the turn id, which the record then holds: that id, and only it, still matches.
        var restarted = new HostedAgentConversation(ChatSessionStore.Decode(saved));
        using var hold = new CancellationTokenSource();
        fixture.HoldStream = true;
        var resume = restarted.ResumeAsync(fixture.Client, new Runner(), null, () => saved = ChatSessionStore.Encode(restarted.Session), hold.Token);
        await fixture.StreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hold.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resume);
        var recovered = ChatSessionStore.Decode(saved);
        Assert.NotNull(recovered.Hosted!.TurnId);
        Assert.True(new HostedAgentConversation(recovered).PendingTurnStartedHere);
        recovered.Hosted.TurnId = Guid.NewGuid().ToString("D");
        Assert.False(new HostedAgentConversation(recovered).PendingTurnStartedHere);
    }

    // T2 round 1, finding 2: Run sends the chat's own model, effort and reasoning, what its pickers show, never the
    // file's frozen submission; and a system message the panel never shows is never sent or taken for the message.
    [Fact]
    public async Task ForeignTurn_Run_UsesTheChatsOwnChoices_AndNoHiddenSystemMessage()
    {
        using var fixture = new Fixture();
        var session = ChatSessionStore.Decode(PlantedChatJson(Guid.NewGuid()));
        session.Hosted!.Effort = "low";
        session.Hosted.ReasoningEnabled = false;
        session.Hosted.Submission!.Model = "another-model";
        session.Hosted.Submission.Effort = "max";
        session.Hosted.Submission.ReasoningEnabled = true;
        var hidden = new AgentMessage { Role = AgentMessageRole.system, Blocks = [AgentContentBlock.FromText("HIDDEN SYSTEM TEXT")] };
        session.Messages.Insert(0, hidden);
        session.Hosted.UserMessageId = hidden.Id;
        var profile = new HostedAgentProfile
        {
            Id = "fixture", DefaultEffort = "medium", Efforts = ["low", "medium", "max"], OffEfforts = ["low"],
            MaxOutputTokens = 1024, Qualified = true,
        };
        var conversation = new HostedAgentConversation(session);
        await conversation.RunPendingAsync(fixture.Client, profile, ["edit_clip"], new Runner(), HostedAgentConversation.AutomaticHold, null);
        var quote = JsonNode.Parse(Assert.Single(fixture.Quotes))!;
        Assert.Equal(("fixture", "low", false), (quote["model"]!.GetValue<string>(), quote["effort"]!.GetValue<string>(),
            quote["reasoningEnabled"]!.GetValue<bool>()));
        Assert.Contains("planted request", fixture.Quotes[0], StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN SYSTEM TEXT", fixture.Quotes[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForeignTurn_Discard_RemovesTheVisibleMessage_NotAHiddenOne()
    {
        var session = ChatSessionStore.Decode(PlantedChatJson(Guid.NewGuid()));
        var hidden = new AgentMessage { Role = AgentMessageRole.system, Blocks = [AgentContentBlock.FromText("hidden")] };
        session.Messages.Add(hidden);
        session.Hosted!.UserMessageId = hidden.Id;
        var conversation = new HostedAgentConversation(session);
        Assert.Equal(HostedAgentConversation.DiscardOutcome.Withdrawn, await conversation.DiscardPendingAsync(null));
        Assert.DoesNotContain(conversation.Session.Messages, m => m.Role == AgentMessageRole.user);
        Assert.Contains(conversation.Session.Messages, m => m.Id == hidden.Id);
    }

    // T2 round 1, finding 3: a turn with no record of what it offered offers nothing, so no tool runs.
    [Fact]
    public async Task ATurnWithNoRecordOfItsOffer_RunsNoTool()
    {
        using var fixture = new Fixture { Tool = true };
        var conversation = new HostedAgentConversation(new ChatSession
        {
            Messages = [AgentMessage.UserText("legacy")],
            Hosted = new HostedChatMetadata
            {
                TurnId = Guid.NewGuid().ToString("D"), AccountId = Fixture.Owner, IdempotencyKey = Guid.NewGuid().ToString("D"),
                Terminal = false,
            },
        });
        var runner = new Runner();
        await conversation.RunPendingAsync(fixture.Client, Profile(), ["edit_clip"], runner, HostedAgentConversation.AutomaticHold, null,
            gate: new AgentToolGate(["edit_clip"], new AgentToolGateTests.Consent(_ => true)));
        Assert.Equal(0, runner.Calls);
        Assert.Contains("is not available in this chat", Assert.Single(fixture.Submissions), StringComparison.Ordinal);
    }

    // Run asks for a larger hold as a new send does: the automatic quote is refused with nothing held, the message comes
    // back to the composer, and the yes is a new send with that hold named as an explicit cap.
    [Fact]
    public async Task ForeignTurn_RunNeedingALargerHold_AsksAsANewSendDoes()
    {
        using var fixture = new Fixture
        {
            QuoteFailure = (HttpStatusCode.BadRequest,
                "{\"error\":\"hold_confirmation_required\",\"details\":{\"suggestedHoldCredits\":4000}}"),
        };
        var conversation = new HostedAgentConversation(ChatSessionStore.Decode(PlantedChatJson(Guid.NewGuid(), cap: 500)));
        var asked = await Assert.ThrowsAsync<HostedApiException>(() => conversation.RunPendingAsync(fixture.Client, Profile(),
            ["edit_clip"], new Runner(), HostedAgentConversation.AutomaticHold, null));
        Assert.Equal(("hold_confirmation_required", 4000L, "planted request"), (asked.Code, asked.SuggestedHoldCredits, asked.WithdrawnText));
        Assert.Equal(0, JsonNode.Parse(Assert.Single(fixture.Quotes))!["spendingCapCredits"]!.GetValue<long>());
        Assert.Empty(fixture.CreateKeys);
        Assert.False(conversation.HasPendingTurn);
        await conversation.SendUserAsync(asked.WithdrawnText!, fixture.Client, Profile(), ["edit_clip"], new Runner(),
            asked.SuggestedHoldCredits!.Value, null);
        Assert.Equal(4000, JsonNode.Parse(fixture.Quotes[1])!["spendingCapCredits"]!.GetValue<long>());
        Assert.Single(fixture.CreateKeys);
    }

    [Fact]
    public async Task ForeignTurn_Discard_WithNoTurnOrAttempt_ResolvesHereAtZero_WithNoRequest()
    {
        using var fixture = new Fixture();
        var conversation = new HostedAgentConversation(ChatSessionStore.Decode(PlantedChatJson(Guid.NewGuid())));
        Assert.Equal(HostedAgentConversation.DiscardOutcome.Withdrawn, await conversation.DiscardPendingAsync(fixture.Client));
        Assert.False(conversation.HasPendingTurn);
        Assert.Equal(0, conversation.Session.Hosted!.LastChargeCredits);
        Assert.DoesNotContain(conversation.Session.Messages, m => m.Role == AgentMessageRole.user);
        Assert.Equal((0, 0, 0), (fixture.QuoteCount, fixture.CreateKeys.Count, fixture.CancelCount));
    }

    [Fact]
    public async Task ForeignTurn_ThatNamesNoAccount_CannotRun_AndDiscardDropsItHere_WithChargeUnknown()
    {
        using var fixture = new Fixture { Tool = true };
        var session = ChatSessionStore.Decode(PlantedChatJson(Guid.NewGuid()));
        session.Hosted!.TurnId = Guid.NewGuid().ToString("D");
        var conversation = new HostedAgentConversation(session);
        var runner = new Runner();
        await Assert.ThrowsAsync<HostedApiException>(() => conversation.RunPendingAsync(fixture.Client, Profile(), ["edit_clip"],
            runner, HostedAgentConversation.AutomaticHold, null));
        await Assert.ThrowsAsync<HostedApiException>(() => conversation.CancelAsync(fixture.Client));
        Assert.Equal(HostedAgentConversation.DiscardOutcome.Forgotten, await conversation.DiscardPendingAsync(fixture.Client));
        Assert.False(conversation.HasPendingTurn);
        Assert.Null(conversation.Session.Hosted!.LastChargeCredits);
        Assert.Equal((0, 0, 0, 0, 0), (fixture.QuoteCount, fixture.CreateKeys.Count, fixture.MaxStreams, fixture.CancelCount, runner.Calls));
    }

    [Fact]
    public async Task ForeignTurn_ForTheSignedInAccount_DiscardCancelsItAtTheGateway()
    {
        using var fixture = new Fixture();
        var session = ChatSessionStore.Decode(PlantedChatJson(Guid.NewGuid()));
        session.Hosted!.TurnId = Guid.NewGuid().ToString("D");
        session.Hosted.AccountId = Fixture.Owner;
        var conversation = new HostedAgentConversation(session);
        Assert.False(conversation.PendingTurnStartedHere);
        Assert.Equal(HostedAgentConversation.DiscardOutcome.Cancelled, await conversation.DiscardPendingAsync(fixture.Client));
        Assert.Equal(1, fixture.CancelCount);
        Assert.True(conversation.Session.Hosted!.Terminal);
    }

    // A record names a key and an account; a chat that names no account, or another one, is not that turn.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("someone-else")]
    public async Task RecordedKey_DoesNotVouchForAChatNamingAnotherOrNoAccount(string? fileAccount)
    {
        using var fixture = new Fixture();
        var key = Guid.NewGuid();
        var session = ChatSessionStore.Decode(PlantedChatJson(key));
        HostedTurnOrigins.Record(key.ToString("D"), Fixture.Owner, HostedTurnOrigins.Digest(session.Hosted!.Submission));
        session.Hosted.AccountId = fileAccount;
        var conversation = new HostedAgentConversation(session);
        Assert.False(conversation.PendingTurnStartedHere);
        var error = await Assert.ThrowsAsync<HostedApiException>(() => conversation.ResumeAsync(fixture.Client, new Runner(), null));
        Assert.Equal(HostedAgentConversation.NotStartedHereCode, error.Code);
        Assert.Equal(0, fixture.QuoteCount);
    }

    [Fact]
    public async Task TurnSentHere_IsRecordedBeforeItsFirstRequest_AndResumesWithoutAClick()
    {
        using var fixture = new Fixture { LoseCreateResponse = true };
        var conversation = new HostedAgentConversation();
        bool? recordedAtQuote = null;
        fixture.OnQuote = () => recordedAtQuote = conversation.PendingTurnStartedHere;
        byte[] saved = [];
        await Assert.ThrowsAsync<HttpRequestException>(() => Send(conversation, fixture,
            persist: () => saved = ChatSessionStore.Encode(conversation.Session)));
        Assert.True(recordedAtQuote);
        var reopened = new HostedAgentConversation(ChatSessionLibrary.Open([ChatSessionStore.Decode(saved)], out _).Current);
        Assert.True(reopened.PendingTurnStartedHere);
        await reopened.ResumeAsync(fixture.Client, new Runner(), null);
        Assert.True(reopened.Session.Hosted!.Terminal);
        Assert.Single(fixture.Reservations);
    }

    [Fact]
    public async Task SendWithoutAnAccount_OrWhenTheRecordCannotBeWritten_SendsNothing()
    {
        using var fixture = new Fixture();
        var anonymous = new HostedAgentClient("https://fixture.test", () => "fixture", new HttpClient(fixture));
        var conversation = new HostedAgentConversation();
        Assert.Equal("account_required", (await Assert.ThrowsAsync<HostedApiException>(() =>
            conversation.SendUserAsync("hello", anonymous, Profile(), ["edit_clip"], new Runner(), 25, null))).Code);

        var blocker = Path.Combine(Path.GetTempPath(), "wp400-origin-blocker-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where the record's folder would be");
        HostedTurnOrigins.SetPathForTests(Path.Combine(blocker, "nested", HostedTurnOrigins.FileName));
        try
        {
            Assert.Equal("origin_unrecorded", (await Assert.ThrowsAsync<HostedApiException>(() => Send(conversation, fixture))).Code);
        }
        finally
        {
            HostedTurnOrigins.SetPathForTests(null);
            File.Delete(blocker);
        }
        Assert.Empty(conversation.Session.Messages);
        Assert.False(conversation.HasPendingTurn);
        Assert.Equal(0, fixture.QuoteCount);
    }

    // WP-400 acceptance 1, hosted path: a tool_request for a project tool or an unoffered tool is refused at dispatch.
    [Theory]
    [InlineData("manage_project")]
    [InlineData("open_project")]
    [InlineData("export_project")]
    public async Task HostedToolRequest_ForAnUnofferedOrProjectTool_IsRefused_AndNothingRuns(string tool)
    {
        using var fixture = new Fixture { Tool = true, ToolName = tool };
        var conversation = new HostedAgentConversation();
        var runner = new Runner();
        await conversation.SendUserAsync("hello", fixture.Client, Profile(), ["edit_clip", "manage_project", "open_project"],
            runner, 25, null, gate: new AgentToolGate(["edit_clip", "manage_project", "open_project"], new AgentToolGateTests.Consent(_ => true)));
        Assert.Equal(0, runner.Calls);
        Assert.Contains("is not available in this chat", Assert.Single(fixture.Submissions), StringComparison.Ordinal);
        Assert.Empty(conversation.Session.Hosted!.StartedToolIds);
        Assert.Equal(HostedTurnReport.StepState.Failed, Assert.Single(HostedTurnReport.Steps(conversation.Session)).State);
    }

    [Theory]
    [InlineData(true, 1, "saved result")]
    [InlineData(false, 0, "declined")]
    public async Task HostedExport_RunsOnlyAfterTheUsersYes(bool allow, int runs, string submitted)
    {
        using var fixture = new Fixture { Tool = true, ToolName = "export_project", ToolArguments = "{ \"mode\": \"xml\", \"outputPath\": \"C:/x/out.xml\" }" };
        var conversation = new HostedAgentConversation();
        var runner = new Runner();
        var consent = new AgentToolGateTests.Consent(_ => allow);
        await conversation.SendUserAsync("hello", fixture.Client, Profile(), ["export_project"], runner, 25, null,
            gate: new AgentToolGate(["export_project"], consent));
        Assert.Equal(runs, runner.Calls);
        Assert.Contains(submitted, Assert.Single(fixture.Submissions), StringComparison.Ordinal);
        var asked = Assert.Single(consent.Asked);
        Assert.Contains(asked.Details, d => d.Key == "File" && d.Value == "out.xml");
        if (allow) Assert.Equal("{\"mode\":\"xml\",\"outputPath\":\"C:/x/out.xml\"}", Assert.Single(runner.Inputs));
    }

    [Fact]
    public async Task HostedExport_UnansweredPrompt_TimesOutAsARefusal()
    {
        using var fixture = new Fixture { Tool = true, ToolName = "export_project" };
        var conversation = new HostedAgentConversation();
        var runner = new Runner();
        await conversation.SendUserAsync("hello", fixture.Client, Profile(), ["export_project"], runner, 25, null,
            gate: new AgentToolGate(["export_project"], new AgentToolGateTests.Consent(null), TimeSpan.FromMilliseconds(50)));
        Assert.Equal(0, runner.Calls);
        Assert.Contains("did not answer", Assert.Single(fixture.Submissions), StringComparison.Ordinal);
    }

    // Cancel or a dropped app while the prompt is open: nothing ran and nothing is "uncertain"; the reconnect asks again.
    [Fact]
    public async Task HostedExport_CancelledWhileAsking_RecordsNothing_AndTheReconnectAsksAgain()
    {
        using var fixture = new Fixture { Tool = true, ToolName = "export_project" };
        var conversation = new HostedAgentConversation();
        var runner = new Runner();
        using var cts = new CancellationTokenSource();
        var waiting = new AgentToolGateTests.Consent(null) { OnAsk = cts.Cancel };
        byte[] saved = [];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conversation.SendUserAsync("hello", fixture.Client, Profile(),
            ["export_project"], runner, 25, null, () => saved = ChatSessionStore.Encode(conversation.Session), cts.Token,
            new AgentToolGate(["export_project"], waiting)));
        var restarted = new HostedAgentConversation(ChatSessionStore.Decode(saved));
        Assert.Empty(restarted.Session.Hosted!.StartedToolIds);
        Assert.Empty(restarted.Session.Hosted.ToolResults);
        Assert.Null(restarted.Session.Hosted.RecoveryError);
        var deny = new AgentToolGateTests.Consent(_ => false);
        await restarted.ResumeAsync(fixture.Client, runner, null, gate: new AgentToolGate(["export_project"], deny));
        Assert.Single(waiting.Asked);
        Assert.Single(deny.Asked);
        Assert.Equal(0, runner.Calls);
        Assert.Contains("declined", Assert.Single(fixture.Submissions), StringComparison.Ordinal);
        Assert.True(restarted.Session.Hosted.Terminal);
    }

    private sealed class Runner : IToolRunner
    {
        public int Calls;
        public bool Throw;
        public readonly List<string> Inputs = [];
        public Task<AgentToolExecutionResult> ExecuteAsync(string name, string inputJson, CancellationToken cancellationToken)
        {
            Calls++;
            Inputs.Add(inputJson);
            if (Throw) throw new InvalidOperationException("fixture failed after possible mutation");
            return Task.FromResult(AgentToolExecutionResult.Ok("saved result"));
        }
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public readonly Dictionary<string, (Guid Quote, Guid Turn)> Reservations = [];
        public readonly List<string> CreateKeys = [];
        public readonly List<string> Submissions = [];
        public readonly List<string> Quotes = [];
        public readonly List<int> StreamCursors = [];
        public readonly List<string?> StreamReaders = [];
        public readonly TaskCompletionSource StreamEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource SubmitAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseStream = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool LoseCreateResponse, Tool, FailSubmit, HoldStream, DropStreamOnce, FailQuote, InvalidEvent;
        // One refused admission, with the body the agent gateway actually sends.
        public (HttpStatusCode Status, string Body)? CreateFailure;
        // One refused quote, likewise.
        public (HttpStatusCode Status, string Body)? QuoteFailure;
        public int DeltaCount;
        public string CancelStatus = "cancelled";
        public string TerminalJson = "{\"status\":\"succeeded\",\"charge\":5}";
        public int QuoteCount, MaxStreams, CancelCount;
        public Action? OnQuote;
        private int _streams;
        private readonly HttpClient _http;
        public HostedAgentClient Client { get; }
        public const string Owner = "fixture-owner";
        // The tool_request the stream issues when Tool is set.
        public string ToolName = "edit_clip", ToolArguments = "{}";
        public Fixture()
        {
            _http = new HttpClient(this);
            Client = new HostedAgentClient("https://fixture.test", () => "local-fixture", _http, accountId: Owner);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/quotes"))
            {
                QuoteCount++;
                OnQuote?.Invoke();
                Quotes.Add(await request.Content!.ReadAsStringAsync(ct));
                if (FailQuote) { FailQuote = false; throw new HttpRequestException("quote response lost"); }
                if (QuoteFailure is { } refused)
                {
                    QuoteFailure = null;
                    return new HttpResponseMessage(refused.Status) { Content = new StringContent(refused.Body) };
                }
                return Json(new JsonObject { ["id"] = Guid.NewGuid(), ["cap"] = 25, ["callBound"] = 8 });
            }
            if (path == "/v1/agent/turns")
            {
                if (CreateFailure is { } failure)
                {
                    CreateFailure = null;
                    return new HttpResponseMessage(failure.Status) { Content = new StringContent(failure.Body) };
                }
                var key = request.Headers.GetValues("Idempotency-Key").Single();
                CreateKeys.Add(key);
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                var quote = Guid.Parse(body["quoteId"]!.GetValue<string>());
                if (Reservations.TryGetValue(key, out var prior) && prior.Quote != quote)
                    return new HttpResponseMessage(HttpStatusCode.Conflict)
                    { Content = new StringContent("{\"error\":\"idempotency_conflict\"}") };
                if (!Reservations.ContainsKey(key)) Reservations[key] = (quote, Guid.NewGuid());
                if (LoseCreateResponse)
                {
                    LoseCreateResponse = false;
                    throw new HttpRequestException("accepted but response lost");
                }
                return Json(new JsonObject { ["id"] = Reservations[key].Turn, ["status"] = "running" });
            }
            if (path.EndsWith("/events"))
            {
                var concurrent = Interlocked.Increment(ref _streams);
                MaxStreams = Math.Max(MaxStreams, concurrent);
                StreamReaders.Add(request.Headers.TryGetValues(HostedAgentClient.StreamReaderHeader, out var readers) ? readers.Single() : null);
                StreamEntered.TrySetResult();
                try
                {
                    if (HoldStream) await ReleaseStream.Task.WaitAsync(ct);
                }
                finally { Interlocked.Decrement(ref _streams); }
                var after = request.Headers.TryGetValues("Last-Event-ID", out var values) ? int.Parse(values.Single()) : 0;
                StreamCursors.Add(after);
                var events = "";
                if (InvalidEvent) events += "id: 1\nevent: tool_request\ndata: INVALID\n\n";
                if (Tool && after < 1)
                    events += "id: 1\nevent: tool_request\ndata: " + new JsonArray(new JsonObject
                    {
                        ["id"] = "call_1",
                        ["function"] = new JsonObject { ["name"] = ToolName, ["arguments"] = ToolArguments },
                    }).ToJsonString() + "\n\n";
                if (after < 2) events += "id: 2\nevent: delta\ndata: {\"text\":\"hello back\"}\n\n";
                if (!DropStreamOnce)
                {
                    if (after < 3) events += "id: 3\nevent: delta\ndata: {\"text\":\" continued\"}\n\n";
                    for (var i = 0; i < DeltaCount; i++)
                        events += $"id: {4 + i}\nevent: delta\ndata: {{\"text\":\" chunk\"}}\n\n";
                    events += $"id: {4 + DeltaCount}\nevent: terminal\ndata: {TerminalJson}\n\n";
                }
                DropStreamOnce = false;
                return new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(events, Encoding.UTF8, "text/event-stream") };
            }
            if (path.EndsWith("/tool-results"))
            {
                Submissions.Add(await request.Content!.ReadAsStringAsync(ct));
                SubmitAttempted.TrySetResult();
                if (FailSubmit) { FailSubmit = false; throw new HttpRequestException("submit response lost"); }
                return Json(new JsonObject());
            }
            if (path.EndsWith("/cancel"))
            {
                CancelCount++;
                return Json(new JsonObject { ["status"] = CancelStatus });
            }
            throw new InvalidOperationException(path);
        }

        private static HttpResponseMessage Json(JsonObject body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
