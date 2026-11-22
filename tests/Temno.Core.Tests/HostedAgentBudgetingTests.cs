using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.Account;
using Temno.Core.Agent;
using Xunit;

namespace Temno.Core.Tests;

// WP-254. The gateway here is a scripted local fixture: these check the desktop's handling, not live providers.
public sealed partial class HostedAgentBudgetingTests
{
    private static HostedAgentProfile Profile(bool completion = true, int tokens = 128000) => new()
    {
        Id = "fixture", DefaultEffort = "high", Efforts = ["high"], MaxOutputTokens = tokens, Qualified = true,
        Completion = completion ? new() { ["high"] = new() { Target = 32768, Floor = 16384, Typical = 6144 } } : null,
    };

    private static Task Send(HostedAgentConversation c, Gateway g, IToolRunner? runner = null, HostedAgentProfile? profile = null,
        IProgress<HostedTurnProgress>? progress = null, string text = "trim the intro", long cap = 200) =>
        c.SendUserAsync(text, g.Client, profile ?? Profile(), ["add_marker", "get_timeline", "move_clips"], runner ?? new Runner(), cap, progress);

    private static string ToolRequest(params (string Id, string Name)[] calls) =>
        "[" + string.Join(",", calls.Select(c => $"{{\"id\":\"{c.Id}\",\"function\":{{\"name\":\"{c.Name}\",\"arguments\":\"{{\\\"frame\\\":12}}\"}}}}")) + "]";

    [Fact]
    public async Task A_published_completion_policy_leaves_sizing_to_the_gateway()
    {
        using var g = new Gateway();
        await Send(new HostedAgentConversation(), g);
        Assert.Equal(0, JsonNode.Parse(g.Quotes.Single())!["maxOutputTokens"]!.GetValue<int>());
    }

    [Theory][InlineData(1024, 1024)][InlineData(128000, 4096)]
    public async Task An_older_gateway_keeps_the_legacy_clamp(int ceiling, int expected)
    {
        using var g = new Gateway();
        await Send(new HostedAgentConversation(), g, profile: Profile(completion: false, tokens: ceiling));
        Assert.Equal(expected, JsonNode.Parse(g.Quotes.Single())!["maxOutputTokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task An_unaffordable_quote_names_the_cap_it_needs_and_keeps_the_draft_uncharged()
    {
        using var g = new Gateway
        {
            QuoteStatus = HttpStatusCode.BadRequest,
            QuoteBody = "{\"error\":\"spending_cap_too_small\",\"details\":{\"minimumCapCredits\":276,\"requiredCapCredits\":473,\"typicalCallCredits\":98}}",
        };
        var c = new HostedAgentConversation();
        var e = await Assert.ThrowsAsync<HostedApiException>(() => Send(c, g));
        Assert.Equal("spending_cap_too_small", e.Code);
        Assert.Contains("at least 276 credits", e.Message);Assert.Contains("473", e.Message);Assert.Contains("about 98", e.Message);
        Assert.Contains("Nothing was charged", e.Message);Assert.DoesNotContain("Raise the cap", e.Message);
        // The desktop puts the draft back when the user message is withdrawn; nothing was reserved.
        Assert.Empty(c.Session.Messages);Assert.Equal(0, g.Reservations);Assert.Equal(0, c.Session.Hosted!.LastChargeCredits);
        Assert.Equal("trim the intro", e.WithdrawnText);
    }

    // The gateway also sends "message" for desktops that cannot read details; a desktop that can keeps its own text.
    [Fact]
    public async Task The_details_text_wins_over_the_message_meant_for_older_desktops()
    {
        using var g = new Gateway
        {
            QuoteStatus = HttpStatusCode.BadRequest,
            QuoteBody = "{\"error\":\"spending_cap_too_small\",\"message\":\"This model and effort need a spending cap of at least 276 credits. Raise the spending cap or choose a lower effort. Nothing was charged.\","
                + "\"details\":{\"minimumCapCredits\":276}}",
        };
        var e = await Assert.ThrowsAsync<HostedApiException>(() => Send(new HostedAgentConversation(), g));
        Assert.StartsWith("This model and effort need a spending cap of at least 276 credits for one useful step", e.Message);
        Assert.DoesNotContain("Raise", e.Message);
    }

    // A rolled-back gateway reads the automatic hold as an invalid cap. Nothing is held; the text comes back.
    [Fact]
    public async Task An_older_gateway_refusing_the_automatic_hold_withdraws_the_draft_with_an_update_message()
    {
        using var g = new Gateway { QuoteStatus = HttpStatusCode.BadRequest, QuoteBody = "{\"error\":\"invalid_spending_bounds\"}" };
        var c = new HostedAgentConversation();
        var e = await Assert.ThrowsAsync<HostedApiException>(() => Send(c, g, cap: HostedAgentConversation.AutomaticHold));
        Assert.Equal(("Hosted chat is updating. Nothing was sent.", "trim the intro"), (e.Message, e.WithdrawnText));
        Assert.Empty(c.Session.Messages);Assert.False(c.HasPendingTurn);Assert.Equal(0, g.Reservations);
    }

    [Fact]
    public async Task A_context_rejection_is_not_described_as_a_spending_problem()
    {
        using var g = new Gateway { QuoteStatus = HttpStatusCode.RequestEntityTooLarge, QuoteBody = "{\"error\":\"context_too_large\",\"details\":{\"floorTokens\":16384}}" };
        var e = await Assert.ThrowsAsync<HostedApiException>(() => Send(new HostedAgentConversation(), g));
        Assert.Equal("context_too_large", e.Code);Assert.Contains("too long", e.Message);Assert.DoesNotContain("cap", e.Message);
    }

    [Fact]
    public async Task The_quote_status_shows_the_typical_cost_beside_the_enforced_bound()
    {
        using var g = new Gateway();var statuses = new List<string>();
        await Send(new HostedAgentConversation(), g, progress: new Sync(p => { if (p.Status is { } s) statuses.Add(s); }));
        Assert.Contains(statuses, s => s.Contains("First step up to 203 credits, typically about 41") && s.Contains("73 kept back"));
    }

    [Fact]
    public async Task The_models_closing_report_is_shown_as_its_own_message_and_flagged_as_early()
    {
        using var g = new Gateway();
        g.Events.Add(("tool_request", ToolRequest(("t1", "add_marker"))));
        g.Events.Add(("report_started", "{\"reason\":\"spending_cap_exhausted\",\"effort\":\"low\"}"));
        g.Events.Add(("delta", "{\"text\":\"Added one marker. Trimming the intro is not done.\"}"));
        g.Events.Add(("terminal", "{\"status\":\"succeeded\",\"charge\":120,\"stopReason\":\"spending_cap_exhausted\"}"));
        var c = new HostedAgentConversation();var statuses = new List<string>();
        await Send(c, g, progress: new Sync(p => { if (p.Status is { } s) statuses.Add(s); }));
        var last = c.Session.Messages[^1];
        Assert.Equal("Added one marker. Trimming the intro is not done.", last.Blocks[0].Text);Assert.Null(last.Origin);
        Assert.DoesNotContain(c.Session.Messages, m => m.Origin == HostedTurnReport.Origin);
        Assert.Contains(statuses, s => s.StartsWith("Stopping early: The credits held for this request ran out") && s.Contains("tools off (low effort)"));
        Assert.Contains(statuses, s => s.StartsWith("Stopped early:") && s.Contains("120 credits"));
        Assert.Equal("spending_cap_exhausted", c.Session.Hosted!.StopReason);
    }

    [Fact]
    public async Task Output_exhaustion_after_an_edit_gets_a_verified_partial_report_that_is_not_model_history()
    {
        using var g = new Gateway();
        g.Events.Add(("tool_request", ToolRequest(("t1", "add_marker"), ("t2", "get_timeline"))));
        g.Events.Add(("terminal", "{\"status\":\"failed\",\"charge\":71,\"error\":\"output_exhausted\"}"));
        var c = new HostedAgentConversation();
        await Send(c, g);
        var report = Assert.Single(c.Session.Messages, m => m.Origin == HostedTurnReport.Origin);
        var text = report.Blocks[0].Text!;
        Assert.Contains("stopped before the task was finished", text);Assert.Contains("reasoning before it answered", text);
        Assert.Contains("Partly done: 1 editing step completed", text);Assert.Contains("Completed: add_marker", text);
        Assert.Contains("Checked: get_timeline.", text);Assert.Contains("Charges: 71 credits (held 200).", text);
        Assert.DoesNotContain("No project changes", text);
        Assert.DoesNotContain(HostedHistory.ToQuoteMessages(c.Session.Messages), m => m?["content"]?.GetValue<string>()?.Contains("Temno report") == true);
        // The next turn quotes the tool record, never Temno's own report.
        await Send(c, g, text: "continue");
        Assert.DoesNotContain("Temno report", g.Quotes[^1]);Assert.Contains("add_marker", g.Quotes[^1]);
    }

    [Fact]
    public async Task An_unconfirmed_edit_is_reported_as_uncertain_never_as_no_changes()
    {
        using var g = new Gateway();
        g.Events.Add(("tool_request", ToolRequest(("t1", "move_clips"))));
        g.Events.Add(("terminal", "{\"status\":\"succeeded\",\"charge\":9}"));
        var c = new HostedAgentConversation();
        await Assert.ThrowsAsync<HostedApiException>(() => Send(c, g, new Runner { Throw = true }));
        var text = Assert.Single(c.Session.Messages, m => m.Origin == HostedTurnReport.Origin).Blocks[0].Text!;
        Assert.Contains("Outcome uncertain: 1 editing step may have changed the project", text);
        Assert.Contains("Not confirmed, check the project: move_clips", text);Assert.DoesNotContain("No project changes", text);
        Assert.Contains("could not be confirmed", text);
    }

    [Fact]
    public async Task No_tools_and_no_answer_says_no_changes_and_the_exact_charge()
    {
        using var g = new Gateway();
        g.Events.Add(("terminal", "{\"status\":\"failed\",\"charge\":48,\"error\":\"output_exhausted\"}"));
        var c = new HostedAgentConversation();
        await Send(c, g);
        var text = Assert.Single(c.Session.Messages, m => m.Origin == HostedTurnReport.Origin).Blocks[0].Text!;
        Assert.Contains("No project changes: no editing tool ran.", text);Assert.Contains("Charges: 48 credits (held 200).", text);
        Assert.Single(c.Session.Messages, m => m.Role == AgentMessageRole.user);
    }

    [Fact]
    public async Task A_failed_edit_is_no_confirmed_change_not_no_change()
    {
        using var g = new Gateway();
        g.Events.Add(("tool_request", ToolRequest(("t1", "move_clips"))));
        g.Events.Add(("terminal", "{\"status\":\"failed\",\"charge\":12,\"error\":\"truncated_tool_call\"}"));
        var c = new HostedAgentConversation();
        await Send(c, g, new Runner { Fail = true });
        var text = Assert.Single(c.Session.Messages, m => m.Origin == HostedTurnReport.Origin).Blocks[0].Text!;
        Assert.Contains("No confirmed changes: 1 editing step reported an error.", text);Assert.Contains("was cut off at the output limit, so it was not run", text);
    }

    [Fact]
    public async Task Unresolved_usage_and_pending_charges_are_reported_as_such()
    {
        using var g = new Gateway();
        g.Events.Add(("terminal", "{\"status\":\"failed\",\"error\":\"worker_lost\",\"usageUnknown\":true}"));
        var c = new HostedAgentConversation();
        await Send(c, g);
        var text = Assert.Single(c.Session.Messages, m => m.Origin == HostedTurnReport.Origin).Blocks[0].Text!;
        Assert.Contains("final charge is still pending", text);Assert.Contains("Some usage could not be confirmed", text);
    }

    [Fact]
    public async Task A_clean_finish_and_an_untouched_cancel_add_no_report()
    {
        using var g = new Gateway();
        g.Events.Add(("delta", "{\"text\":\"Done.\"}"));
        g.Events.Add(("terminal", "{\"status\":\"succeeded\",\"charge\":6}"));
        var c = new HostedAgentConversation();
        await Send(c, g);
        Assert.DoesNotContain(c.Session.Messages, m => m.Origin is not null);
        g.Events.Clear();g.Events.Add(("terminal", "{\"status\":\"canceled\",\"charge\":0,\"error\":\"user_cancel\"}"));
        await Send(c, g, text: "next");
        Assert.DoesNotContain(c.Session.Messages, m => m.Origin is not null);
    }

    [Fact]
    public async Task A_replayed_terminal_event_after_restart_does_not_duplicate_the_report()
    {
        using var g = new Gateway();
        g.Events.Add(("tool_request", ToolRequest(("t1", "add_marker"))));
        g.Events.Add(("terminal", "{\"status\":\"failed\",\"charge\":30,\"error\":\"spending_cap_exhausted\"}"));
        var c = new HostedAgentConversation();
        await Send(c, g);
        // Simulate a crash after the report was saved but before the terminal cursor was: the event replays.
        var restored = new HostedAgentConversation(ChatSessionStore.Snapshot(c.Session));
        var hosted = restored.Session.Hosted!;
        Assert.NotNull(hosted.ReportMessageId);
        hosted.Terminal = false;hosted.LastEventId = 1;
        // WP-400: a settled turn's record is dropped after its terminal state is saved; a crash before that leaves it.
        HostedTurnOrigins.Record(hosted.IdempotencyKey!, hosted.AccountId!, HostedTurnOrigins.Digest(hosted.Submission));
        HostedTurnOrigins.RecordTurn(hosted.IdempotencyKey, Guid.Parse(hosted.TurnId!));
        await restored.ResumeAsync(g.Client, new Runner(), null);
        Assert.True(hosted.Terminal);Assert.Equal("spending_cap_exhausted", hosted.LastError);
        Assert.Single(restored.Session.Messages, m => m.Origin == HostedTurnReport.Origin);
    }

    [Fact]
    public void A_session_saved_before_this_change_decodes_with_no_report_state()
    {
        var old = "{\"id\":\"" + Guid.NewGuid() + "\",\"messages\":[{\"id\":\"" + Guid.NewGuid() + "\",\"role\":\"user\",\"blocks\":[{\"kind\":\"text\",\"text\":\"hi\"}]}],"
                  + "\"hosted\":{\"model\":\"fixture\",\"turnId\":\"" + Guid.NewGuid() + "\",\"terminal\":false,\"lastEventId\":2}}";
        var session = ChatSessionStore.Decode(old);
        Assert.Null(session.Hosted!.StopReason);Assert.False(session.Hosted.ReportStarted);Assert.Null(session.Hosted.ReportMessageId);
        Assert.Null(session.Messages[0].Origin);Assert.Single(HostedHistory.ToQuoteMessages(session.Messages));
    }

    [Fact]
    public void Outcome_classes_are_distinct()
    {
        static HostedTurnReport.Step S(string name, HostedTurnReport.StepState state) => new("x" + name + state, name, "{}", HostedTurnReport.IsReadOnly(name), state);
        Assert.Equal(HostedTurnReport.Outcome.None, HostedTurnReport.Classify([S("get_timeline", HostedTurnReport.StepState.Done)], false));
        Assert.Equal(HostedTurnReport.Outcome.Partial, HostedTurnReport.Classify([S("add_marker", HostedTurnReport.StepState.Done)], false));
        Assert.Equal(HostedTurnReport.Outcome.Completed, HostedTurnReport.Classify([S("add_marker", HostedTurnReport.StepState.Done)], true));
        Assert.Equal(HostedTurnReport.Outcome.Uncertain, HostedTurnReport.Classify(
            [S("add_marker", HostedTurnReport.StepState.Done), S("move_clips", HostedTurnReport.StepState.Unconfirmed)], true));
        // A read that was not confirmed cannot have changed the project.
        Assert.Equal(HostedTurnReport.Outcome.None, HostedTurnReport.Classify([S("inspect_media", HostedTurnReport.StepState.Unconfirmed)], false));
        Assert.False(HostedTurnReport.IsReadOnly("detect_beats"));Assert.True(HostedTurnReport.IsReadOnly("validate_timeline"));
    }

    [Fact]
    public void Output_context_spending_step_provider_auth_and_policy_outcomes_read_differently()
    {
        string[] codes = ["output_exhausted", "output_truncated", "truncated_tool_call", "empty_output", "content_filtered", "context_exhausted",
            "provider_context_rejected", "spending_cap_exhausted", "call_limit", "tool_limit", "no_progress", "deadline",
            "openrouter_unauthorized", "openrouter_unfunded", "provider_policy_rejected", "provider_route_unavailable", "provider_rejected"];
        var messages = codes.Select(code => HostedApiErrors.Describe(null, code, null)).ToArray();
        Assert.Equal(messages.Length, messages.Distinct().Count());
        Assert.All(messages, m => Assert.DoesNotContain("Hosted request failed", m));
    }

    private sealed class Sync(Action<HostedTurnProgress> report) : IProgress<HostedTurnProgress>
    {
        public void Report(HostedTurnProgress value) => report(value);
    }

    private sealed class Runner : IToolRunner
    {
        public bool Throw, Fail;
        public Task<AgentToolExecutionResult> ExecuteAsync(string name, string inputJson, CancellationToken cancellationToken)
        {
            if (Throw) throw new InvalidOperationException("fixture failed after possible mutation");
            return Task.FromResult(Fail ? AgentToolExecutionResult.Error("No clip at frame 12.") : AgentToolExecutionResult.Ok(name + " ok"));
        }
    }

    // WP-254 estimated hold: the desktop sends 0 and states the gateway's likely range, hold and what returned.
    private const string AutomaticQuote = "{\"id\":\"QUOTE_ID\",\"cap\":1465,\"callBound\":430,\"estimate\":{\"typicalCallCredits\":61,"
        + "\"reportReserveCredits\":98,\"minimumCapCredits\":430,\"requiredCapCredits\":528,\"likelyLowCredits\":61,\"likelyHighCredits\":203,"
        + "\"holdCredits\":1465,\"holdLimitedBy\":\"estimate\",\"availableCredits\":5000,\"minimumHoldCredits\":430}}";

    [Fact]
    public async Task An_automatic_send_asks_for_an_estimate_and_states_the_hold_and_what_returned()
    {
        using var g = new Gateway { QuoteJson = AutomaticQuote };
        g.Events.Add(("terminal", "{\"status\":\"succeeded\",\"charge\":156}"));
        var statuses = new List<string>();var c = new HostedAgentConversation();
        await Send(c, g, cap: HostedAgentConversation.AutomaticHold, progress: new Sync(p => { if (p.Status is { } s) statuses.Add(s); }));
        Assert.Equal(0, JsonNode.Parse(g.Quotes.Single())!["spendingCapCredits"]!.GetValue<long>());
        Assert.Null(c.Session.Hosted!.SpendingCapCredits);Assert.Equal(1465, c.Session.Hosted.HoldCredits);
        Assert.Contains("Estimated 60–205 credits. Up to 1,465 held while it works; unused credits return when it finishes.", statuses);
        Assert.Contains(statuses, s => s.Contains("Final charge 156 credits. 1,309 held credits returned."));
        Assert.Equal("Charges: 156 credits (held 1465).", HostedTurnReport.Charges(c.Session.Hosted));
    }

    [Fact]
    public void The_quote_states_what_limited_the_hold()
    {
        HostedQuote Quote(long cap, string? limitedBy, bool likely = true) => new()
        {
            Cap = cap, CallBound = 430, Estimate = new()
            {
                TypicalCallCredits = 61, ReportReserveCredits = 98, HoldLimitedBy = limitedBy,
                LikelyLowCredits = likely ? 61 : null, LikelyHighCredits = likely ? 203 : null,
            },
        };
        // A balance-limited hold keeps headroom for other jobs; only a hold at the minimum can take it all.
        var roomy = Quote(600, "balance");roomy.Estimate!.AvailableCredits = 700;
        Assert.Equal("Estimated 60–205 credits. Holding 600 of your 700 credits, so 100 stay free for other jobs; it may stop early.",
            HostedAgentConversation.QuoteStatus(roomy));
        var whole = Quote(600, "balance");whole.Estimate!.AvailableCredits = 600;
        Assert.Equal("Estimated 60–205 credits. Holding 600, your whole available balance; it may stop early. Other hosted jobs need more credits until it finishes.",
            HostedAgentConversation.QuoteStatus(whole));
        Assert.StartsWith("Estimated 60–205 credits. Up to 3,000 held, the most Temno holds without asking",
            HostedAgentConversation.QuoteStatus(Quote(3000, "policy")));
        Assert.StartsWith("Estimated 60–205 credits. Up to 8,400 held while it works",
            HostedAgentConversation.QuoteStatus(Quote(8400, "cap")));
        // A gateway from before estimated holds still gets the step-bound wording.
        Assert.StartsWith("Quoted. First step up to 430 credits, typically about 61. Cap 200",
            HostedAgentConversation.QuoteStatus(Quote(200, null, likely: false)));
    }

    [Fact]
    public async Task A_long_conversation_asks_first_and_a_yes_resends_with_the_named_hold()
    {
        using var g = new Gateway
        {
            QuoteStatus = HttpStatusCode.BadRequest,
            QuoteBody = "{\"error\":\"hold_confirmation_required\",\"details\":{\"suggestedHoldCredits\":8400,\"minimumHoldCredits\":3120,"
                + "\"likelyLowCredits\":440,\"likelyHighCredits\":1350}}",
        };
        var c = new HostedAgentConversation();var statuses = new List<string>();
        var e = await Assert.ThrowsAsync<HostedApiException>(() => Send(c, g, cap: HostedAgentConversation.AutomaticHold,
            progress: new Sync(p => { if (p.Status is { } s) statuses.Add(s); })));
        Assert.Equal(("hold_confirmation_required", 8400L), (e.Code, e.SuggestedHoldCredits));
        // Waiting on the user's yes is not a rejection; the line stays true after "Don't send" too.
        Assert.Equal("Not sent yet: this needs a larger hold. No credits charged.", statuses[^1]);
        Assert.Contains("needs up to 8,400 credits held (likely 440–1,350)", e.Message);Assert.Contains("Nothing is held unless you continue", e.Message);
        Assert.Empty(c.Session.Messages);Assert.Equal(0, g.Reservations);Assert.Equal(0, c.Session.Hosted!.LastChargeCredits);
        Assert.Equal("trim the intro", e.WithdrawnText);
        // The user's yes: the same text, with the hold the gateway named as an explicit hold.
        g.QuoteStatus = HttpStatusCode.OK;
        await Send(c, g, cap: e.SuggestedHoldCredits!.Value);
        Assert.Equal(8400, JsonNode.Parse(g.Quotes[^1])!["spendingCapCredits"]!.GetValue<long>());
        Assert.Equal(8400, c.Session.Hosted!.SpendingCapCredits);Assert.Equal(1, g.Reservations);
    }

    [Fact]
    public async Task Too_few_credits_names_the_minimum_and_the_balance_and_charges_nothing()
    {
        using var g = new Gateway
        {
            QuoteStatus = HttpStatusCode.PaymentRequired,
            QuoteBody = "{\"error\":\"insufficient_credits\",\"details\":{\"minimumHoldCredits\":430,\"availableCredits\":300,\"likelyHighCredits\":203}}",
        };
        var c = new HostedAgentConversation();
        var e = await Assert.ThrowsAsync<HostedApiException>(() => Send(c, g, cap: HostedAgentConversation.AutomaticHold));
        Assert.Equal("insufficient_credits", e.Code);
        Assert.Equal("This model and effort need 430 credits available to start. You have 300. Open Account and choose a plan, or choose another model. Nothing was charged.", e.Message);
        Assert.Empty(c.Session.Messages);Assert.Equal(0, g.Reservations);
    }

    [Theory][InlineData(-1)][InlineData(1)][InlineData(4)]
    public async Task A_hold_below_five_is_refused_before_anything_is_sent(long cap)
    {
        using var g = new Gateway();var c = new HostedAgentConversation();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Send(c, g, cap: cap));
        Assert.Empty(g.Quotes);Assert.Empty(c.Session.Messages);
    }

    [Fact]
    public async Task A_gateway_without_estimated_holds_is_refused_before_anything_is_sent()
    {
        using var g = new Gateway { CatalogJson = "{\"configured\":true,\"acceptTurns\":true,\"billingPolicy\":{\"minimumBillableTurnCredits\":5}}" };
        var old = await g.Client.GetCatalogAsync();
        var e = Assert.Throws<HostedApiException>(() => HostedAgentConversation.RequireEstimatedHolds(old));
        Assert.Equal("hold_unsupported", e.Code);Assert.Equal("Hosted chat is updating. Nothing was sent.", e.Message);
        Assert.Throws<HostedApiException>(() => HostedAgentConversation.RequireEstimatedHolds(null));
        g.CatalogJson = "{\"configured\":true,\"acceptTurns\":true,\"billingPolicy\":{\"hold\":\"estimated\",\"autoHoldLimitCredits\":3000,\"holdSteps\":6,"
            + "\"likelySteps\":3,\"stepGrowthBytes\":16384,\"creditsPerUpstreamUsd\":300},\"toolSchemaBytes\":{\"add_marker\":180},"
            + "\"models\":[{\"id\":\"claude-opus-5.5\",\"inputUsdPerMillion\":4,\"outputUsdPerMillion\":20}]}";
        var current = await g.Client.GetCatalogAsync();
        HostedAgentConversation.RequireEstimatedHolds(current);
        Assert.Equal((3000L, 6, 180), (current.BillingPolicy!.AutoHoldLimitCredits, current.BillingPolicy.HoldSteps, current.ToolSchemaBytes!["add_marker"]));
        Assert.Equal((4m, 20m), (current.Models[0].InputUsdPerMillion, current.Models[0].OutputUsdPerMillion));
        Assert.Empty(g.Quotes);
    }

    // Pinned to the same inputs and figures as the gateway's AgentBudgetTests (Opus 5.5 High, 53,000 bytes).
    [Fact]
    public void The_composer_estimate_matches_the_gateway_figure()
    {
        var opus = new HostedAgentProfile { Id = "claude-opus-5.5", InputUsdPerMillion = 4, OutputUsdPerMillion = 20 };
        Assert.Equal((61L, 203L), HostedEstimate.Likely(opus, 6144, 53000, tools: true));
        Assert.Equal((61L, 61L), HostedEstimate.Likely(opus, 6144, 53000, tools: false));
        Assert.Equal("60–205", HostedEstimate.Range(61, 203));Assert.Equal("5", HostedEstimate.Range(5, 5));
        Assert.Equal("1,200–3,405", HostedEstimate.Range(1203, 3401));
    }

    // WP-331: the gateway's catalog row for Opus with its prompt cache; the desktop's range must equal the gateway's
    // cached figure (AgentBudgetTests: 66–164 with tools, 66 without).
    [Fact]
    public void The_composer_estimate_uses_the_published_cache_prices()
    {
        var opus = JsonSerializer.Deserialize<HostedAgentProfile>(
            """{"id":"claude-opus-5.5","inputUsdPerMillion":4,"outputUsdPerMillion":20,"cacheReadUsdPerMillion":0.2,"cacheWrite5mUsdPerMillion":5,"cacheWrite1hUsdPerMillion":8,"promptCache":{"type":"ephemeral","ttl":null}}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("ephemeral", opus.PromptCache!.Type);
        Assert.Equal((66L, 164L), HostedEstimate.Likely(opus, 6144, 53000, tools: true));
        Assert.Equal((66L, 66L), HostedEstimate.Likely(opus, 6144, 53000, tools: false));
        // The one-hour cache writes at its own price; no cache policy keeps the uncached figure.
        opus.PromptCache.Ttl = "1h";
        Assert.True(HostedEstimate.Likely(opus, 6144, 53000, tools: true).Low > 66);
        opus.PromptCache = null;
        Assert.Equal((61L, 203L), HostedEstimate.Likely(opus, 6144, 53000, tools: true));
    }

    [Fact]
    public void The_composer_estimate_grows_with_the_draft_and_needs_estimated_holds()
    {
        var catalog = new HostedAgentCatalog
        {
            BillingPolicy = new() { Hold = "estimated", LikelySteps = 3, StepGrowthBytes = 16384 },
            ToolSchemaBytes = new() { ["add_marker"] = 180 },
        };
        var opus = new HostedAgentProfile
        {
            Id = "claude-opus-5.5", InputUsdPerMillion = 4, OutputUsdPerMillion = 20,
            Completion = new() { ["high"] = new() { Target = 32768, Floor = 16384, Typical = 6144 } },
        };
        var small = HostedEstimate.ForDraft(catalog, opus, "high", true, 50000, "trim the intro", ["add_marker"])!.Value;
        var large = HostedEstimate.ForDraft(catalog, opus, "high", true, 50000, new string('x', 600_000), ["add_marker"])!.Value;
        Assert.True(small.Low < small.High && large.Low > small.Low * 3, $"{small} {large}");
        // Three steps, the later two on a context grown by the planned allowance.
        Assert.InRange(small.High, 3 * small.Low, 3 * small.Low + 30);
        var noTools = HostedEstimate.ForDraft(catalog, opus, "high", true, 50000, "trim the intro", [])!.Value;
        Assert.Equal(noTools.Low, noTools.High);
        Assert.Null(HostedEstimate.ForDraft(new HostedAgentCatalog { BillingPolicy = new() }, opus, "high", true, 50000, "x", []));
        Assert.Null(HostedEstimate.ForDraft(catalog, new HostedAgentProfile { Completion = opus.Completion }, "high", true, 50000, "x", []));
        Assert.Null(HostedEstimate.ForDraft(catalog, opus, "max", true, 50000, "x", []));
    }

    // Reasoning off prices the capacity the gateway publishes for it, not a desktop constant.
    [Fact]
    public void The_composer_estimate_with_reasoning_off_uses_the_published_off_capacity()
    {
        var catalog = new HostedAgentCatalog { BillingPolicy = new() { Hold = "estimated", LikelySteps = 3, StepGrowthBytes = 16384 } };
        var profile = new HostedAgentProfile
        {
            Id = "gemini-3.8-flash", InputUsdPerMillion = 4, OutputUsdPerMillion = 20,
            Completion = new() { ["low"] = new() { Target = 16384, Floor = 8192, Typical = 2048 } },
        };
        // WP-308: every quote also carries the hosted instructions, one more message in the array.
        var sent = 50000 + HostedEstimate.EnvelopeBytes + 30 + HostedEstimate.InstructionsBytes([]) + 1;
        var legacy = HostedEstimate.ForDraft(catalog, profile, "low", false, 50000, "x", [])!.Value;
        Assert.Equal(HostedEstimate.StepCall(profile, 1024, sent), legacy.Low);
        profile.CompletionOff = new() { Target = 8192, Floor = 4096, Typical = 3000 };
        var published = HostedEstimate.ForDraft(catalog, profile, "low", false, 50000, "x", [])!.Value;
        Assert.Equal(HostedEstimate.StepCall(profile, 3000, sent), published.Low);
        Assert.True(published.Low > legacy.Low);
    }

    [Theory]
    [InlineData("hold_exhausted", "The credits held for this request ran out before the work finished.")]
    [InlineData("spending_cap_exhausted", "The credits held for this request ran out before the work finished.")]
    [InlineData("balance_exhausted", "The credits your balance allowed for this request ran out before the work finished. Add credits, then send a follow-up.")]
    public void What_ran_out_is_named_plainly(string reason, string text) => Assert.Equal(text, HostedApiErrors.Describe(null, reason, null));

    private sealed class Gateway : HttpMessageHandler
    {
        public readonly List<string> Quotes = [];
        public readonly List<(string Kind, string Data)> Events = [];
        public HttpStatusCode QuoteStatus = HttpStatusCode.OK;
        public string? QuoteBody;
        public string? QuoteJson;
        public string CatalogJson = "{}";
        public int Reservations;
        public HostedAgentClient Client { get; }
        public Gateway() => Client = new HostedAgentClient("https://fixture.test", () => "local-fixture", new HttpClient(this), accountId: "fixture-owner");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/quotes"))
            {
                Quotes.Add(await request.Content!.ReadAsStringAsync(ct));
                if (QuoteStatus != HttpStatusCode.OK)
                    return new(QuoteStatus) { Content = new StringContent(QuoteBody ?? "{}") };
                if (QuoteJson is not null) return Json(QuoteJson.Replace("QUOTE_ID", Guid.NewGuid().ToString()));
                return Json("{\"id\":\"" + Guid.NewGuid() + "\",\"cap\":200,\"callBound\":203,\"estimate\":{\"typicalCallCredits\":41,\"reportReserveCredits\":73,"
                            + "\"minimumCapCredits\":178,\"requiredCapCredits\":276,\"firstCallTokens\":20107,\"workTokens\":32768}}");
            }
            if (path == "/v1/agent/turns") { Reservations++; return Json("{\"id\":\"" + Guid.NewGuid() + "\",\"status\":\"queued\"}"); }
            if (path.EndsWith("/events"))
            {
                var after = request.Headers.TryGetValues("Last-Event-ID", out var values) ? int.Parse(values.Single()) : 0;
                var script = Events.Count > 0 ? Events : [("terminal", "{\"status\":\"succeeded\",\"charge\":5}")];
                var body = new StringBuilder();
                for (var i = 0; i < script.Count; i++)
                    if (i + 1 > after) body.Append($"id: {i + 1}\nevent: {script[i].Kind}\ndata: {script[i].Data}\n\n");
                return new(HttpStatusCode.OK) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream") };
            }
            if (path.EndsWith("/catalog")) return Json(CatalogJson);
            if (path.EndsWith("/tool-results")) return Json("{}");
            if (path.EndsWith("/cancel")) return Json("{\"status\":\"canceled\",\"charge\":9}");
            throw new InvalidOperationException(path);
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
