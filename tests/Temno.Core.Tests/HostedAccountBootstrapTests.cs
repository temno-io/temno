using System.Net;
using Temno.Core.Account;
using Xunit;

namespace Temno.Core.Tests;

public sealed class HostedAccountBootstrapTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Assert.Equal("https://gateway.example.test/v1/account/bootstrap", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer own-session", request.Headers.Authorization!.ToString());
            Assert.False(request.Headers.Contains("apikey"));
            Assert.Equal("{}", await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"total\":0,\"reserved\":0,\"available\":0}") };
        }
    }
    [Fact]
    public async Task Bootstrap_uses_only_the_signed_in_session_and_returns_the_provisioned_zero_balance()
    {
        var h = new Handler();
        var client = new CreditBalanceClient("https://project.supabase.co", "publishable", new HttpClient(h));
        Assert.Equal(new CreditBalance(0, 0, 0), await client.BootstrapAsync("https://gateway.example.test", "own-session"));
        Assert.Equal(1, h.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => client.BootstrapAsync("http://untrusted.example.test", "own-session"));
        Assert.Equal(1, h.Calls);
    }
}
