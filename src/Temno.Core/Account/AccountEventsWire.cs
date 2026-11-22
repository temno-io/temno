using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Temno.Core.Account;

// docs/contracts/account-events.md. This file is compiled into Temno.Core and linked into the Gateway and Billing,
// so it depends on the base class library only.

/// <summary>What changed. A tickle carries a kind and an opaque id, never the change itself.</summary>
public static class AccountEventKinds
{
    public const string Credits = "credits";
    public const string Subscription = "subscription";
    public const string Job = "job";
    public const string Chat = "chat";
}

/// <summary>Why the events Worker closed a socket.</summary>
public static class AccountEventCloseCodes
{
    public const int InvalidMessage = 4400;
    public const int AuthenticationRequired = 4401;
    public const int AccountSuspended = 4403;
    public const int Replaced = 4408;
}

public enum AccountEventsSend
{
    Sent,

    /// <summary>The Worker refused it (a 4xx other than 429): retrying the same request won't help.</summary>
    Refused,

    /// <summary>Timeout, network, 429 or 5xx.</summary>
    Unavailable,
}

/// <summary>
/// Billing and the Gateway tell an account's Durable Object that something changed, signed with
/// <c>temno-events-v1</c> and the environment's account-events key (never the edge identity key). Best effort: one
/// attempt with a short timeout, because the app's safety poll catches anything lost.
/// </summary>
public sealed class AccountEventsPublisher
{
    public const string PublishPath = "/internal/v1/publish";
    public const string ClosePath = "/internal/v1/close";
    public const string TimestampHeader = "X-Temno-Events-Timestamp";
    public const string NonceHeader = "X-Temno-Events-Nonce";
    public const string SignatureHeader = "X-Temno-Events-Signature";
    public const string EnvironmentHeader = "X-Temno-Environment";

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly Uri _baseUrl;
    private readonly byte[] _key;
    private readonly string _environment;
    private readonly TimeProvider _time;

    private AccountEventsPublisher(HttpClient http, Uri baseUrl, byte[] key, string environment, TimeProvider time)
    {
        _http = http;
        _baseUrl = baseUrl;
        _key = key;
        _environment = environment;
        _time = time;
    }

    /// <summary>
    /// Null when the URL and key are both empty: publishing is off. Throws when either is set and the pair is not
    /// usable, so a half-configured service fails at startup instead of publishing nothing.
    /// </summary>
    public static AccountEventsPublisher? Create(HttpClient http, string? url, string? key, string environment, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(key))
            return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme != Uri.UriSchemeHttps
            || baseUrl.UserInfo.Length > 0 || baseUrl.AbsolutePath != "/" || baseUrl.Query.Length > 0 || baseUrl.Fragment.Length > 0)
            throw new ArgumentException("The account events URL must be an https origin with no path.", nameof(url));
        if (DecodeKey(key) is not { } bytes)
            throw new ArgumentException("The account events key must be base64 for exactly 32 bytes.", nameof(key));
        if (environment is not ("development" or "production"))
            throw new ArgumentException("The environment must be development or production.", nameof(environment));
        return new AccountEventsPublisher(http, baseUrl, bytes, environment, time ?? TimeProvider.System);
    }

    public static byte[]? DecodeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length != 44)
            return null;
        try
        {
            var bytes = Convert.FromBase64String(key);
            return bytes.Length == 32 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public Task<AccountEventsSend> PublishAsync(Guid user, string kind, string id, CancellationToken cancellationToken)
        => SendAsync(PublishPath, JsonSerializer.SerializeToUtf8Bytes(new { user = user.ToString("D"), kind, id }), cancellationToken);

    /// <summary>WP-345's hook: every socket of the account closes with account_suspended.</summary>
    public Task<AccountEventsSend> CloseSuspendedAsync(Guid user, CancellationToken cancellationToken)
        => SendAsync(ClosePath, JsonSerializer.SerializeToUtf8Bytes(new { user = user.ToString("D"), reason = "account_suspended" }), cancellationToken);

    /// <summary>An event id for an identifier that must not leave the service, such as a Stripe event id.</summary>
    public static string OpaqueId(string source)
        => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..32];

    public static string Canonical(string environment, string timestamp, string nonce, string method, string path, string bodySha256)
        => string.Join('\n', "temno-events-v1", environment, timestamp, nonce, method, path, bodySha256);

    private async Task<AccountEventsSend> SendAsync(string path, byte[] body, CancellationToken cancellationToken)
    {
        var timestamp = _time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Hex(RandomNumberGenerator.GetBytes(16));
        var canonical = Canonical(_environment, timestamp, nonce, "POST", path, Hex(SHA256.HashData(body)));
        var signature = Hex(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(canonical)));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUrl, path)) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(EnvironmentHeader, _environment);
        request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp);
        request.Headers.TryAddWithoutValidation(NonceHeader, nonce);
        request.Headers.TryAddWithoutValidation(SignatureHeader, signature);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
                return AccountEventsSend.Sent;
            return status is 429 or >= 500 ? AccountEventsSend.Unavailable : AccountEventsSend.Refused;
        }
        catch (HttpRequestException)
        {
            return AccountEventsSend.Unavailable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return AccountEventsSend.Unavailable;
        }
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
