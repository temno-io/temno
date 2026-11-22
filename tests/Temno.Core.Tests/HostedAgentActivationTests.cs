using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.Account;
using Temno.Core.Agent;
using Xunit;

namespace Temno.Core.Tests;

public sealed class HostedAgentActivationTests
{
    [Fact]
    public void CatalogSelectsOnlyServerApprovedLocallyImplementedTools()
    {
        var catalog = JsonSerializer.Deserialize<HostedAgentCatalog>(
            """{"tools":["add_marker","get_timeline","server_only"]}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(["get_timeline", "add_marker"], catalog.SelectTools(
            ["get_timeline", "quote_image", "add_marker", "edit_image", "quote_media", "get_timeline"]));
        Assert.Empty(new HostedAgentCatalog().SelectTools(["get_timeline"]));
    }

    [Theory]
    [InlineData(943718, 4096)]
    [InlineData(128000, 4096)]
    [InlineData(1024, 1024)]
    [InlineData(0, 4096)]
    public async Task ProviderCeilingDoesNotBecomeTheReplyBudget(int ceiling, int expected)
    {
        using var transport = new QuoteTransport(HttpStatusCode.BadRequest);
        var conversation = new HostedAgentConversation();
        await Assert.ThrowsAsync<HostedApiException>(() => Send(conversation, transport, ceiling));
        Assert.Equal(expected, transport.Quote!["maxOutputTokens"]!.GetValue<int>());
        Assert.Equal(25, transport.Quote["spendingCapCredits"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(402)]
    [InlineData(413)]
    public async Task DefiniteQuoteRejectionUnlocksChoicesAndDoesNotDuplicateDraft(int status)
    {
        using var transport = new QuoteTransport((HttpStatusCode)status);
        var conversation = new HostedAgentConversation();
        var earlier = AgentMessage.UserText("earlier settled message");
        conversation.Session.Messages.Add(earlier);
        byte[] saved = [];
        await Assert.ThrowsAsync<HostedApiException>(() => Send(conversation, transport, 1024,
            () => saved = ChatSessionStore.Encode(conversation.Session)));
        Assert.False(conversation.HasPendingTurn);
        Assert.Equal(0, conversation.Session.Hosted!.LastChargeCredits);
        Assert.False(conversation.Session.Hosted.SubmissionAttempted);
        Assert.Equal(earlier.Id, Assert.Single(conversation.Session.Messages).Id);
        Assert.Equal(0, transport.CreateCalls);
        Assert.False(new HostedAgentConversation(ChatSessionStore.Decode(saved)).HasPendingTurn);
        var oldKey = conversation.Session.Hosted.IdempotencyKey;
        await Assert.ThrowsAsync<HostedApiException>(() => Send(conversation, transport, 2048));
        Assert.NotEqual(oldKey, conversation.Session.Hosted.IdempotencyKey);
        Assert.Equal(2048, transport.Quote!["maxOutputTokens"]!.GetValue<int>());
    }

    [Fact]
    public async Task LostQuoteResponseRetainsFrozenRecoveryState()
    {
        using var transport = new QuoteTransport(null);
        var conversation = new HostedAgentConversation();
        await Assert.ThrowsAsync<HttpRequestException>(() => Send(conversation, transport, 1024));
        Assert.True(conversation.HasPendingTurn);
        Assert.Single(conversation.Session.Messages);
        Assert.Null(conversation.Session.Hosted!.LastChargeCredits);
        Assert.Equal(0, transport.CreateCalls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(408)]
    [InlineData(409)]
    [InlineData(429)]
    public async Task TransientQuoteRejectionRetainsTheSameIntentForResume(int status)
    {
        using var transport = new QuoteTransport((HttpStatusCode)status);
        var conversation = new HostedAgentConversation();
        await Assert.ThrowsAsync<HostedApiException>(() => Send(conversation, transport, 1024));
        var key = conversation.Session.Hosted!.IdempotencyKey;
        var request = transport.Quote!.ToJsonString();
        var client = new HostedAgentClient("https://fixture.test", () => "fixture", new HttpClient(transport), accountId: "fixture-owner");
        await Assert.ThrowsAsync<HostedApiException>(() => conversation.ResumeAsync(client, new NoTools(), null));
        Assert.True(conversation.HasPendingTurn);
        Assert.Single(conversation.Session.Messages);
        Assert.Equal(key, conversation.Session.Hosted.IdempotencyKey);
        Assert.Equal(request, transport.Quote!.ToJsonString());
        Assert.Null(conversation.Session.Hosted.LastChargeCredits);
        Assert.Equal(0, transport.CreateCalls);
    }

    private static Task Send(HostedAgentConversation c, QuoteTransport transport, int ceiling, Action? persist = null)
        => c.SendUserAsync("draft", new HostedAgentClient("https://fixture.test", () => "fixture", new HttpClient(transport), accountId: "fixture-owner"),
            new HostedAgentProfile { Id = "fixture", DefaultEffort = "medium", MaxOutputTokens = ceiling },
            [], new NoTools(), 25, null, persist);

    private sealed class NoTools : IToolRunner
    {
        public Task<AgentToolExecutionResult> ExecuteAsync(string name, string inputJson, CancellationToken ct)
            => throw new InvalidOperationException("No tool should execute for a rejected quote");
    }

    private sealed class QuoteTransport(HttpStatusCode? status) : HttpMessageHandler
    {
        public JsonNode? Quote;
        public int CreateCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("/quotes"))
            {
                CreateCalls++;
                throw new InvalidOperationException("Quote rejection must not create a turn");
            }
            Quote = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct));
            if (status is null) throw new HttpRequestException("Lost quote response");
            return new HttpResponseMessage(status.Value)
            { Content = new StringContent("""{"error":"spending_cap_too_small"}""", Encoding.UTF8, "application/json") };
        }
    }
}
