using System.Net;
using System.Text;
using Temno.Core.Account;
using Xunit;

namespace Temno.Core.Tests;

public sealed class OwnerCreditTestingTests
{
    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("null", false)]
    [InlineData("\"true\"", false)]
    [InlineData("1", false)]
    [InlineData("{}", false)]
    public async Task BalanceRequiresExplicitServerBoolean(string mode, bool expected)
    {
        using var http = new HttpClient(new ReplyHandler(
            "{\"total\":0,\"reserved\":0,\"available\":0,\"ownerTesting\":" + mode + "}"));
        var client = new CreditBalanceClient("https://example.test", "test-key", http);
        var balance = await client.ReadAsync("test-token");
        Assert.NotNull(balance);
        Assert.Equal(expected, balance.Value.OwnerTesting);
        Assert.Equal(expected, balance.Value.CanCover(100));
        Assert.Equal(0, balance.Value.Available);
    }

    [Fact]
    public async Task OlderServerRemainsMetered()
    {
        using var http = new HttpClient(new ReplyHandler("{\"total\":50,\"reserved\":30,\"available\":20}"));
        var client = new CreditBalanceClient("https://example.test", "test-key", http);
        var balance = (await client.ReadAsync("test-token"))!.Value;
        Assert.False(balance.OwnerTesting);
        Assert.True(balance.CanCover(20));
        Assert.False(balance.CanCover(21));
    }

    [Fact]
    public async Task FreshModeReadRestoresOrdinaryCreditCheck()
    {
        using var http = new HttpClient(new ReplyHandler(
            "{\"available\":0,\"ownerTesting\":true}", "{\"available\":0,\"ownerTesting\":false}"));
        var client = new CreditBalanceClient("https://example.test", "test-key", http);
        Assert.True((await client.ReadAsync("test-token"))!.Value.CanCover(4));
        Assert.False((await client.ReadAsync("test-token"))!.Value.CanCover(4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeQuoteIsNeverAccepted(bool ownerTesting)
        => Assert.False(new CreditBalance(0, 0, 0) { OwnerTesting = ownerTesting }.CanCover(-1));

    private sealed class ReplyHandler(params string[] replies) : HttpMessageHandler
    {
        private readonly Queue<string> _replies = new(replies);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/rest/v1/rpc/my_credit_balance", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_replies.Dequeue(), Encoding.UTF8, "application/json"),
            });
        }
    }
}
