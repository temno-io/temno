using System.Net;
using System.Text;
using System.Text.Json;
using Temno.Core.Account;
using Xunit;

namespace Temno.Core.Tests;

public sealed class AccountProfileTests
{
    [Fact]
    public void Authorize_AlwaysOffersAccountChoiceWithoutChangingPkceOrRedirect()
    {
        var client = new SupabaseAuthClient("https://auth.example.test/", "anon");
        var uri = new Uri(client.AuthorizeUrl("challenge_-", "http://127.0.0.1:17321/callback", "openid email profile"));
        var query = uri.Query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
            .ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
        Assert.Equal("/auth/v1/authorize", uri.AbsolutePath);
        Assert.Equal("google", query["provider"]);
        Assert.Equal("select_account", query["prompt"]);
        Assert.Equal("challenge_-", query["code_challenge"]);
        Assert.Equal("s256", query["code_challenge_method"]);
        Assert.Equal("http://127.0.0.1:17321/callback", query["redirect_to"]);
        Assert.Equal("openid email profile", query["scopes"]);
        Assert.False(query.ContainsKey("login_hint"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExchangeAndRefresh_PreserveGoogleProfileAndTokenIdentity(bool refresh)
    {
        using var handler = new ResponseHandler("""
            {"access_token":"test-access","refresh_token":"test-refresh","expires_at":2000000000,
             "user":{"id":"user-1","email":"person@example.test","user_metadata":{
               "full_name":"Ada Lovelace","avatar_url":"https://lh3.googleusercontent.com/a/photo=s96-c"}}}
            """);
        using var http = new HttpClient(handler);
        var client = new SupabaseAuthClient("https://auth.example.test", "anon", http);
        var result = refresh ? await client.RefreshAsync("old-refresh") : await client.ExchangeCodeAsync("code", "verifier");
        Assert.Equal(SignInOutcomeKind.Exchanged, result.Kind);
        var session = Assert.IsType<AccountTokenBundle>(result.Session);
        Assert.Equal("user-1", session.UserId);
        Assert.Equal("person@example.test", session.Email);
        Assert.Equal("Ada Lovelace", session.DisplayName);
        Assert.Equal("https://lh3.googleusercontent.com/a/photo=s96-c", session.AvatarUrl);
        Assert.Equal("test-access", session.AccessToken);
        Assert.Equal("test-refresh", session.RefreshToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2000000000), session.AccessExpiresAt);
        Assert.Contains(refresh ? "grant_type=refresh_token" : "grant_type=pkce", handler.Url);
        var stored = JsonSerializer.Deserialize<AccountTokenBundle>(JsonSerializer.Serialize(session))!;
        Assert.Equal(session.DisplayName, stored.DisplayName);
        Assert.Equal(session.AvatarUrl, stored.AvatarUrl);
    }

    [Fact]
    public async Task GoogleIdentity_WinsOverUnrelatedProviderAndUserEditableDisplayMetadata()
    {
        using var handler = new ResponseHandler("""
            {"id":"user-1","user_metadata":{"full_name":"Other name","avatar_url":"https://evil.test/avatar"},
             "identities":[{"provider":"github","identity_data":{"name":"Other provider"}},
               {"provider":"google","identity_data":{"name":" Ada\n  Lovelace ","picture":"https://lh3.ggpht.com/photo"}}]}
            """);
        using var http = new HttpClient(handler);
        var profile = await new SupabaseAuthClient("https://auth.example.test", "anon", http).ReadProfileAsync("test-access");
        Assert.Equal(new AccountProfile("user-1", "Ada Lovelace", "https://lh3.ggpht.com/photo"), profile);
        Assert.Equal("https://auth.example.test/auth/v1/user", handler.Url);
        Assert.Equal("Bearer test-access", handler.Authorization);
        Assert.Equal("anon", handler.ApiKey);
        Assert.Equal(HttpMethod.Get, handler.Method);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"full_name\":123,\"avatar_url\":{},\"picture\":false,\"name\":[]}")]
    public async Task MalformedOptionalMetadata_DoesNotRejectValidSession(string metadata)
    {
        using var handler = new ResponseHandler($$$"""
            {"access_token":"test-access","user":{"id":"user-1","user_metadata":{{{metadata}}},"identities":[null,7,{"provider":false}]}}
            """);
        using var http = new HttpClient(handler);
        var result = await new SupabaseAuthClient("https://auth.example.test", "anon", http).ExchangeCodeAsync("code", "verifier");
        Assert.Equal(SignInOutcomeKind.Exchanged, result.Kind);
        Assert.Null(result.Session!.DisplayName);
        Assert.Null(result.Session.AvatarUrl);
    }

    [Fact]
    public async Task AlternateProfileKeys_WorkWhenPrimaryFieldsAreMissingOrInvalid()
    {
        using var handler = new ResponseHandler("""
            {"access_token":"test-access","user":{"id":"user-1","user_metadata":{
              "full_name":" ","name":"Ada","avatar_url":"file:///C:/private.png","picture":"https://lh3.googleusercontent.com/photo"}}}
            """);
        using var http = new HttpClient(handler);
        var result = await new SupabaseAuthClient("https://auth.example.test", "anon", http).RefreshAsync("refresh");
        Assert.Equal("Ada", result.Session!.DisplayName);
        Assert.Equal("https://lh3.googleusercontent.com/photo", result.Session.AvatarUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///C:/private.png")]
    [InlineData("http://lh3.googleusercontent.com/photo")]
    [InlineData("https://127.0.0.1/photo")]
    [InlineData("https://googleusercontent.com.evil.test/photo")]
    [InlineData("https://evilgoogleusercontent.com/photo")]
    [InlineData("https://lh3.googleusercontent.com:8443/photo")]
    [InlineData("https://user:password@lh3.googleusercontent.com/photo")]
    [InlineData("data:image/png;base64,AA")]
    [InlineData("https://\u200D.googleusercontent.com/photo")]
    public void UntrustedAvatarLocations_UseFallback(string? value)
        => Assert.Null(AccountProfile.GoogleAvatarUrl(value));

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/a/photo=s96-c")]
    [InlineData("https://lh5.ggpht.com/photo")]
    public void GoogleImageHosts_AreAccepted(string value)
        => Assert.Equal(value, AccountProfile.GoogleAvatarUrl(value));

    [Theory]
    [InlineData("{}", 200)]
    [InlineData("not-json", 200)]
    [InlineData("{\"error\":\"expired\"}", 401)]
    [InlineData("{}", 503)]
    public async Task ProfileFailure_ReturnsNoDecoration(string body, int status)
    {
        using var handler = new ResponseHandler(body, (HttpStatusCode)status);
        using var http = new HttpClient(handler);
        Assert.Null(await new SupabaseAuthClient("https://auth.example.test", "anon", http).ReadProfileAsync("test-access"));
    }

    [Fact]
    public void LegacyStoredSessions_StillDeserializeWithoutProfile()
    {
        var session = JsonSerializer.Deserialize<AccountTokenBundle>("""{"AccessToken":"a","RefreshToken":"r","UserId":"u","Email":"p@example.test"}""")!;
        Assert.Equal("u", session.UserId);
        Assert.Null(session.DisplayName);
        Assert.Null(session.AvatarUrl);
    }

    private sealed class ResponseHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Url { get; private set; }
        public string? Authorization { get; private set; }
        public string? ApiKey { get; private set; }
        public HttpMethod? Method { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.AbsoluteUri;
            Authorization = request.Headers.Authorization?.ToString();
            ApiKey = request.Headers.GetValues("apikey").Single();
            Method = request.Method;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
