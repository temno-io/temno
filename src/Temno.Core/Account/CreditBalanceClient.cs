using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Temno.Core.Account;

public sealed class CreditBalanceClient
{
    private readonly HttpClient _http;
    private readonly string _supabaseUrl;
    private readonly string _anonKey;

    public CreditBalanceClient(string supabaseUrl, string anonKey, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(supabaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(anonKey);
        _supabaseUrl = supabaseUrl.TrimEnd('/');
        _anonKey = anonKey;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<CreditBalance?> ReadAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, _supabaseUrl + "/rest/v1/rpc/my_credit_balance")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("apikey", _anonKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return await ReadResponseAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CreditBalance?> BootstrapAsync(string gatewayUrl, string accessToken, CancellationToken cancellationToken = default)
        => (await BootstrapSignInAsync(gatewayUrl, accessToken, null, cancellationToken).ConfigureAwait(false)).Balance;

    /// <summary>
    /// The Gateway's bootstrap. After a sign-in through the sign-in page the desktop sends that sign-in's PKCE
    /// verifier, once, so the server can grant the signup credits (WP-354); <see cref="AccountBootstrap.SignupGrant"/>
    /// says whether it did and why not. Null from a Gateway older than WP-354.
    /// </summary>
    public async Task<AccountBootstrap> BootstrapSignInAsync(
        string gatewayUrl, string accessToken, string? signInVerifier, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var gateway)
            || !(gateway.Scheme == "https" || gateway.Scheme == "http" && gateway.IsLoopback)
            || gateway.UserInfo.Length != 0 || gateway.Query.Length != 0 || gateway.Fragment.Length != 0)
            throw new ArgumentException("The account gateway must use HTTPS or a local test endpoint.", nameof(gatewayUrl));
        var body = signInVerifier is null
            ? "{}"
            : JsonSerializer.Serialize(new { signInVerifier });
        using var request = new HttpRequestMessage(HttpMethod.Post, gatewayUrl.TrimEnd('/') + "/v1/account/bootstrap")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        HostedEndpoints.StampEnvironment(request);
        var row = await ReadRowAsync(request, cancellationToken).ConfigureAwait(false);
        return new AccountBootstrap(
            row is null ? null : Balance(row),
            row?["signupGrant"] is JsonValue grant && grant.TryGetValue(out string? status) ? status : null);
    }

    private async Task<CreditBalance?> ReadResponseAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => await ReadRowAsync(request, cancellationToken).ConfigureAwait(false) is { } row ? Balance(row) : null;

    private async Task<JsonObject?> ReadRowAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new HostedApiException("The credit balance could not be read.", code: "balance_unavailable");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw HostedApiErrors.FromHttp((int)response.StatusCode, body);

            JsonNode? json;
            try
            {
                json = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
            }
            catch (JsonException)
            {
                throw new HostedApiException("The credit balance read returned malformed JSON.", code: "balance_unavailable");
            }

            if (json is null || json is JsonValue value && value.TryGetValue<object?>(out var raw) && raw is null)
                return null;

            var row = json switch
            {
                JsonArray array => array.Count > 0 ? array[0] as JsonObject : null,
                JsonObject obj => obj,
                _ => null,
            };
            if (row is null)
                throw new HostedApiException("The credit balance read returned an unexpected shape.", code: "balance_unavailable");

            return row;
        }
    }

    private static CreditBalance Balance(JsonObject row)
        => new CreditBalance(
            Number(row, "total") ?? Number(row, "total_credits") ?? 0,
            Number(row, "reserved") ?? Number(row, "reserved_credits") ?? 0,
            Number(row, "available") ?? Number(row, "available_credits") ?? 0)
        {
            OwnerTesting = row["ownerTesting"] is JsonValue mode
                && mode.TryGetValue<bool>(out var enabled) && enabled,
            NextExpiry = Expiry(row["nextExpiry"]),
        };

    // Absent on the bootstrap's balance and on servers before WP-272; either means none shown.
    private static CreditExpiry? Expiry(JsonNode? node)
    {
        if (node is not JsonObject expiry || Number(expiry, "credits") is not long credits || credits <= 0)
            return null;
        return expiry["at"] is JsonValue at && at.TryGetValue(out string? text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when)
            ? new CreditExpiry(credits, when)
            : null;
    }

    private static long? Number(JsonObject row, string name)
    {
        if (row[name] is not JsonValue value)
            return null;
        if (value.TryGetValue(out long number))
            return number;
        if (value.TryGetValue(out int smaller))
            return smaller;
        return null;
    }
}

/// <param name="Balance">Null when the server has no credit account for this user.</param>
/// <param name="SignupGrant">
/// What the bootstrap did about the signup credits: applied, duplicate, unconfirmed, no_ticket, ticket_used,
/// ticket_expired or velocity_limited (docs/contracts/hosted-credits.md § Provisioning). Null from an older Gateway.
/// </param>
public readonly record struct AccountBootstrap(CreditBalance? Balance, string? SignupGrant);
