using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Temno.Core.Account;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-354: the desktop opens Temno's sign-in page before Google, carries the sign-in's PKCE verifier to the bootstrap
/// once, and says why a sign-in brought no signup credits.
/// </summary>
public sealed class SignInStartTests
{
    // RFC 7636 appendix B.
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    public void The_challenge_the_page_carries_is_the_s256_of_the_verifier_the_bootstrap_sends() =>
        Assert.Equal(Challenge, Pkce.ChallengeS256(Verifier));

    [Fact]
    public void The_start_page_url_carries_the_challenge_and_the_loopback_and_nothing_else()
    {
        Assert.Equal(
            "https://accounts.temno.io/auth/start?code_challenge=" + Challenge
            + "&redirect_uri=http%3A%2F%2F127.0.0.1%3A17321%2Fcallback",
            GoogleSignInFlow.StartPageUrl("https://accounts.temno.io/", Challenge, HostedEndpoints.DefaultRedirectUri));
        Assert.StartsWith("http://127.0.0.1:5241/auth/start?",
            GoogleSignInFlow.StartPageUrl("http://127.0.0.1:5241", Challenge, HostedEndpoints.DefaultRedirectUri));
    }

    [Theory]
    [InlineData("http://accounts.temno.io")]
    [InlineData("https://user@accounts.temno.io")]
    [InlineData("https://accounts.temno.io/?x=1")]
    [InlineData("https://accounts.temno.io/#x")]
    [InlineData("accounts.temno.io")]
    [InlineData("")]
    public void An_accounts_host_that_is_not_https_gives_no_start_page(string accounts) =>
        Assert.Null(GoogleSignInFlow.StartPageUrl(accounts, Challenge, HostedEndpoints.DefaultRedirectUri));

    [Fact]
    public void A_sign_in_waits_ten_minutes_for_the_page_and_google() =>
        Assert.Equal(TimeSpan.FromMinutes(10), GoogleSignInFlow.Lifetime);

    [Theory]
    [InlineData("applied", "Your free credits are in your balance.")]
    [InlineData("no_ticket", "skipped Temno's sign-in check")]
    [InlineData("ticket_used", "expired before Temno could use it")]
    [InlineData("ticket_expired", "expired before Temno could use it")]
    [InlineData("velocity_limited", "today's limit for this network")]
    [InlineData("unconfirmed", "email is confirmed")]
    public void Every_grant_outcome_the_server_reports_has_a_plain_sentence(string grant, string expected)
    {
        var note = GoogleSignInFlow.GrantNote(grant);
        Assert.NotNull(note);
        Assert.Contains(expected, note);
        Assert.DoesNotContain("—", note);
        Assert.DoesNotContain("Wright", note);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData(null)]
    [InlineData("something_newer")]
    public void An_account_that_had_its_credits_or_an_older_server_says_nothing(string? grant) =>
        Assert.Null(GoogleSignInFlow.GrantNote(grant));

    private sealed class Gateway(string answer) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("https://gateway.example.test/v1/account/bootstrap", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer own-session", request.Headers.Authorization!.ToString());
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer) };
        }
    }

    [Fact]
    public async Task The_bootstrap_after_a_sign_in_carries_the_verifier_and_reads_what_the_grant_did()
    {
        var gateway = new Gateway("""{"total":250,"reserved":0,"available":250,"signupGrant":"applied"}""");
        var client = new CreditBalanceClient("https://project.supabase.co", "publishable", new HttpClient(gateway));

        var result = await client.BootstrapSignInAsync("https://gateway.example.test", "own-session", Verifier);

        Assert.Equal(new CreditBalance(250, 0, 250), result.Balance);
        Assert.Equal("applied", result.SignupGrant);
        var body = JsonNode.Parse(Assert.Single(gateway.Bodies))!.AsObject();
        Assert.Equal(Verifier, body["signInVerifier"]!.GetValue<string>());
        Assert.Single(body);
    }

    [Fact]
    public async Task A_plain_bootstrap_sends_no_verifier_and_an_older_gateway_reports_no_grant()
    {
        var gateway = new Gateway("""{"total":0,"reserved":0,"available":0}""");
        var client = new CreditBalanceClient("https://project.supabase.co", "publishable", new HttpClient(gateway));

        Assert.Equal(new CreditBalance(0, 0, 0), await client.BootstrapAsync("https://gateway.example.test", "own-session"));
        var result = await client.BootstrapSignInAsync("https://gateway.example.test", "own-session", null);

        Assert.Equal(new[] { "{}", "{}" }, gateway.Bodies);
        Assert.Null(result.SignupGrant);
    }

    private static async Task<string> Callback(string target)
    {
        using var server = new LoopbackCallbackServer(0);
        var wait = server.WaitAsync("/callback", TimeSpan.FromSeconds(5));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, server.Port);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"));
        await wait;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task The_returning_page_hands_the_code_bearing_address_to_nobody_and_loads_nothing()
    {
        var response = await Callback("/callback?code=from-google");

        Assert.StartsWith("HTTP/1.1 200 OK", response);
        Assert.Contains("\r\nReferrer-Policy: no-referrer\r\n", response);
        Assert.Contains("\r\nContent-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; img-src data:;", response);
        Assert.Contains("\r\nCache-Control: no-store\r\n", response);
        Assert.Contains("You’re signed in", response);
        Assert.DoesNotContain("from-google", response);
    }

    [Fact]
    public async Task A_cancelled_google_step_shows_that_sign_in_did_not_finish()
    {
        var response = await Callback("/callback?error=access_denied");

        Assert.StartsWith("HTTP/1.1 400", response);
        Assert.Contains("\r\nReferrer-Policy: no-referrer\r\n", response);
        Assert.Contains("Sign-in didn’t finish", response);
    }

    [Theory]
    [InlineData(LoopbackCallbackServer.SuccessHtml)]
    [InlineData(LoopbackCallbackServer.FailureHtml)]
    public void The_loopback_pages_are_self_contained(string html)
    {
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"http", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Wright", html);
    }
}
