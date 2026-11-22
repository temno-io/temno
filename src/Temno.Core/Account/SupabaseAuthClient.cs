using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Temno.Core.Account;

public sealed class SupabaseAuthClient
{
    private readonly HttpClient _http;
    private readonly string _supabaseUrl;
    private readonly string _anonKey;

    public SupabaseAuthClient(string supabaseUrl, string anonKey, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supabaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(anonKey);
        _supabaseUrl = supabaseUrl.TrimEnd('/');
        _anonKey = anonKey;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public string AuthorizeUrl(string codeChallenge, string redirectUri, string scopes = HostedEndpoints.DefaultScopes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(codeChallenge);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);
        var query =
            "?provider=google"
            + "&prompt=select_account"
            + "&redirect_to=" + Uri.EscapeDataString(redirectUri)
            + "&code_challenge=" + Uri.EscapeDataString(codeChallenge)
            + "&code_challenge_method=s256";
        if (!string.IsNullOrWhiteSpace(scopes))
            query += "&scopes=" + Uri.EscapeDataString(scopes);
        return _supabaseUrl + "/auth/v1/authorize" + query;
    }

    public async Task<SignInOutcome> ExchangeCodeAsync(string authCode, string codeVerifier, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeVerifier);
        using var request = new HttpRequestMessage(HttpMethod.Post, _supabaseUrl + "/auth/v1/token?grant_type=pkce")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { auth_code = authCode, code_verifier = codeVerifier }),
                Encoding.UTF8,
                "application/json"),
        };
        ApplyAnon(request);
        return await SendSessionAsync(request, SignInOutcomeKind.Rejected, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SignInOutcome> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, _supabaseUrl + "/auth/v1/token?grant_type=refresh_token")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { refresh_token = refreshToken }),
                Encoding.UTF8,
                "application/json"),
        };
        ApplyAnon(request);
        return await SendSessionAsync(request, SignInOutcomeKind.Rejected, cancellationToken).ConfigureAwait(false);
    }

    public async Task LogoutAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return;
        using var request = new HttpRequestMessage(HttpMethod.Post, _supabaseUrl + "/auth/v1/logout?scope=local");
        ApplyAnon(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Local sign-out still proceeds; the provider revoke is best-effort.
        }
    }

    public async Task<AccountProfile?> ReadProfileAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, _supabaseUrl + "/auth/v1/user");
        ApplyAnon(request);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try { return AccountProfile.FromUser(JsonNode.Parse(body) as JsonObject); }
        catch (JsonException) { return null; }
    }

    private void ApplyAnon(HttpRequestMessage request)
        => request.Headers.TryAddWithoutValidation("apikey", _anonKey);

    private async Task<SignInOutcome> SendSessionAsync(
        HttpRequestMessage request,
        SignInOutcomeKind rejectedKind,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new SignInOutcome(SignInOutcomeKind.Unknown, Detail: "auth_transport_lost");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var session = ReadSession(body);
                return session is null
                    ? new SignInOutcome(SignInOutcomeKind.Unknown, Detail: "no_session")
                    : new SignInOutcome(SignInOutcomeKind.Exchanged, session);
            }

            if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                return new SignInOutcome(SignInOutcomeKind.Unknown, Detail: "auth_unavailable");
            return new SignInOutcome(rejectedKind, Detail: "auth_code_rejected");
        }
    }

    private static AccountTokenBundle? ReadSession(string body)
    {
        try
        {
            var json = JsonNode.Parse(string.IsNullOrWhiteSpace(body) ? "null" : body) as JsonObject;
            var access = json?["access_token"]?.GetValue<string>();
            var user = json?["user"] as JsonObject;
            var userId = user?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(access) || string.IsNullOrWhiteSpace(userId))
                return null;

            DateTimeOffset? expiresAt = null;
            if (json?["expires_at"] is JsonValue at && at.TryGetValue(out long epoch))
                expiresAt = DateTimeOffset.FromUnixTimeSeconds(epoch);
            else if (json?["expires_in"] is JsonValue seconds && seconds.TryGetValue(out int lifetime))
                expiresAt = DateTimeOffset.UtcNow.AddSeconds(lifetime);

            var profile = AccountProfile.FromUser(user);
            return new AccountTokenBundle
            {
                AccessToken = access!,
                RefreshToken = json?["refresh_token"]?.GetValue<string>() ?? "",
                UserId = userId!,
                Email = user?["email"]?.GetValue<string>(),
                DisplayName = profile?.DisplayName,
                AvatarUrl = profile?.AvatarUrl,
                AccessExpiresAt = expiresAt,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
