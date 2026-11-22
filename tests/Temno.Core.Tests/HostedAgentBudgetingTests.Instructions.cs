using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Temno.Core.Account;
using Temno.Core.Agent;
using Xunit;

namespace Temno.Core.Tests;

// WP-308. The hosted quote starts with Temno's instructions: one system message, added at each send and never saved.
public sealed partial class HostedAgentBudgetingTests
{
    private static readonly string[] SentTools = ["add_marker", "get_timeline", "move_clips"];

    private static JsonArray QuoteMessages(string quote) => JsonNode.Parse(quote)!["messages"]!.AsArray();

    private static string[] Roles(JsonArray messages) => messages.Select(m => m!["role"]!.GetValue<string>()).ToArray();

    private static void AssertLeadsWithInstructions(string quote)
    {
        var messages = QuoteMessages(quote);
        Assert.Equal("system", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal(AgentInstructions.Hosted(SentTools), messages[0]!["content"]!.GetValue<string>());
        Assert.Single(Roles(messages), r => r == "system");
    }

    private static void AssertSavedChatHasNoInstructions(HostedAgentConversation c)
    {
        Assert.DoesNotContain(c.Session.Messages, m => m.Role == AgentMessageRole.system);
        Assert.DoesNotContain("system", Roles(c.Session.Hosted!.Submission!.Messages));
        var saved = Encoding.UTF8.GetString(ChatSessionStore.Encode(c.Session));
        Assert.DoesNotContain("# Hosted credits", saved);
        Assert.DoesNotContain("creative AI assistant connected to Temno", saved);
    }

    [Fact]
    public async Task Every_quote_leads_with_one_system_message_and_the_saved_chat_holds_none()
    {
        using var g = new Gateway();
        var c = new HostedAgentConversation();
        await Send(c, g);
        await Send(c, g, text: "now the outro");
        Assert.Equal(2, g.Quotes.Count);
        foreach (var quote in g.Quotes)
            AssertLeadsWithInstructions(quote);
        // The second turn's history follows the instructions; the instructions are not repeated as history.
        Assert.Equal(["system", "user", "user"], Roles(QuoteMessages(g.Quotes[1])));
        AssertSavedChatHasNoInstructions(c);
    }

    [Fact]
    public async Task A_submission_recovered_after_a_restart_is_quoted_with_the_current_instructions()
    {
        using var g = new Gateway { QuoteStatus = HttpStatusCode.ServiceUnavailable };
        var c = new HostedAgentConversation();
        await Assert.ThrowsAsync<HostedApiException>(() => Send(c, g));
        Assert.True(c.HasPendingTurn);
        var restored = new HostedAgentConversation(ChatSessionStore.Decode(ChatSessionStore.Encode(c.Session)));
        g.QuoteStatus = HttpStatusCode.OK;
        await restored.ResumeAsync(g.Client, new Runner(), null);
        Assert.Equal(2, g.Quotes.Count);
        Assert.Equal(g.Quotes[0], g.Quotes[1]);
        AssertLeadsWithInstructions(g.Quotes[1]);
        Assert.Equal(1, g.Reservations);
        AssertSavedChatHasNoInstructions(restored);
    }

    [Fact]
    public async Task The_yes_to_a_larger_hold_is_quoted_with_the_instructions_too()
    {
        using var g = new Gateway
        {
            QuoteStatus = HttpStatusCode.BadRequest,
            QuoteBody = "{\"error\":\"hold_confirmation_required\",\"details\":{\"suggestedHoldCredits\":8400}}",
        };
        var c = new HostedAgentConversation();
        var e = await Assert.ThrowsAsync<HostedApiException>(() => Send(c, g, cap: HostedAgentConversation.AutomaticHold));
        g.QuoteStatus = HttpStatusCode.OK;
        await Send(c, g, cap: e.SuggestedHoldCredits!.Value);
        Assert.Equal(2, g.Quotes.Count);
        foreach (var quote in g.Quotes)
            AssertLeadsWithInstructions(quote);
        Assert.Equal(["system", "user"], Roles(QuoteMessages(g.Quotes[1])));
    }

    [Fact]
    public async Task The_composer_estimate_counts_the_system_message_the_quote_sends()
    {
        using var g = new Gateway();
        await Send(new HostedAgentConversation(), g);
        // The gateway measures the parsed payload re-serialized with the default encoder, as this does.
        var sent = Encoding.UTF8.GetByteCount(QuoteMessages(g.Quotes.Single())[0]!.ToJsonString());
        Assert.Equal(sent, HostedEstimate.InstructionsBytes(SentTools));

        var catalog = new HostedAgentCatalog
        {
            BillingPolicy = new() { Hold = "estimated", LikelySteps = 3, StepGrowthBytes = 16384 },
            ToolSchemaBytes = new() { ["add_marker"] = 180, ["get_timeline"] = 90, ["move_clips"] = 400 },
        };
        var opus = new HostedAgentProfile
        {
            Id = "claude-opus-5.5", InputUsdPerMillion = 4, OutputUsdPerMillion = 20,
            Completion = new() { ["high"] = new() { Target = 32768, Floor = 16384, Typical = 6144 } },
        };
        var draft = Encoding.UTF8.GetByteCount(new JsonObject { ["role"] = "user", ["content"] = "trim the intro" }.ToJsonString()) + 1;
        long without = 50000 + HostedEstimate.EnvelopeBytes + draft + (180 + 1) + (90 + 1) + (400 + 1);
        Assert.Equal(HostedEstimate.Likely(opus, 6144, without + sent + 1, tools: true),
            HostedEstimate.ForDraft(catalog, opus, "high", true, 50000, "trim the intro", SentTools));
        Assert.True(HostedEstimate.ForDraft(catalog, opus, "high", true, 50000, "trim the intro", SentTools)!.Value.High
            > HostedEstimate.Likely(opus, 6144, without, tools: true).High);
    }

    [Fact]
    public void The_sent_quote_copies_the_frozen_request_and_carries_exactly_one_system_message()
    {
        var frozen = new HostedQuoteRequest
        {
            Model = "fixture", Effort = "high", ReasoningEnabled = false, MaxOutputTokens = 0, SpendingCapCredits = 400,
            Messages = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = "stale" },
                new JsonObject { ["role"] = "user", ["content"] = "hi" }),
            Tools = ["get_timeline"],
        };
        var sent = HostedHistory.WithInstructions(frozen);
        foreach (var property in typeof(HostedQuoteRequest).GetProperties().Where(p => p.Name != nameof(HostedQuoteRequest.Messages)))
            Assert.Equal(property.GetValue(frozen), property.GetValue(sent));
        Assert.Equal(["system", "user"], Roles(sent.Messages));
        Assert.Equal(AgentInstructions.Hosted(["get_timeline"]), sent.Messages[0]!["content"]!.GetValue<string>());
        // The frozen submission is never changed: it is what a retry resends.
        Assert.Equal(["system", "user"], Roles(frozen.Messages));
        Assert.Equal("stale", frozen.Messages[0]!["content"]!.GetValue<string>());
    }
}
