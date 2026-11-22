using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Temno.Core.Account;

namespace Temno.Core.Agent;

public sealed class HostedAgentClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Func<string?> _accessToken;
    private readonly TimeProvider _time;
    public static readonly TimeSpan StreamReconnectWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(45);

    public HostedAgentClient(string baseUrl, Func<string?> accessToken, HttpClient? httpClient = null, string? accountId = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _baseUrl = baseUrl.TrimEnd('/');
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        AccountId = accountId;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string? AccountId { get; }

    public async Task<HostedAgentCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/v1/agent/catalog", null, null, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        var catalog = JsonSerializer.Deserialize<HostedAgentCatalog>(body, Json)
               ?? throw new HostedApiException("The hosted chat catalog was empty.");
        // WP-436: a model that takes images carries the Gateway's image rules, which a turn freezes.
        foreach (var model in catalog.Models)
            model.ImagePolicy = model.ImageInput ? catalog.ToolResultImages : null;
        return catalog;
    }

    public async Task<HostedQuote> CreateQuoteAsync(HostedQuoteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var response = await SendAsync(
            HttpMethod.Post,
            "/v1/agent/quotes",
            JsonSerializer.Serialize(request, Json),
            null,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HostedQuote>(body, Json)
               ?? throw new HostedApiException("The quote response was empty.");
    }

    public async Task<HostedTurn> CreateTurnAsync(Guid quoteId, Guid idempotencyKey, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/v1/agent/turns",
            JsonSerializer.Serialize(new { quoteId }, Json),
            idempotencyKey,
            cancellationToken).ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HostedTurn>(body, Json)
               ?? throw new HostedApiException("The hosted turn response was empty.");
    }

    public async Task<HostedTurn> GetTurnAsync(Guid turnId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/v1/agent/turns/" + turnId.ToString("D"), null, null, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HostedTurn>(body, Json)
               ?? throw new HostedApiException("The hosted turn was empty.");
    }

    public async Task<HostedTurn> CancelTurnAsync(Guid turnId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Post, "/v1/agent/turns/" + turnId.ToString("D") + "/cancel", "{}", null, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HostedTurn>(body, Json)
               ?? throw new HostedApiException("The cancel response was empty.");
    }

    /// <summary>WP-436: the UTF-8 size of the tool-results body <see cref="SubmitToolResultsAsync"/> sends, escapes included.</summary>
    public static long ToolResultsBytes(IReadOnlyList<HostedToolResult> results) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new { results }, Json));

    public Task SubmitToolResultsAsync(
        Guid turnId,
        IReadOnlyList<HostedToolResult> results,
        CancellationToken cancellationToken = default) =>
        SubmitToolResultsWithRecoveryAsync(turnId, results, cancellationToken);

    /// <returns>True if the submission needed recovery and the caller should reopen its event stream.</returns>
    internal async Task<bool> SubmitToolResultsWithRecoveryAsync(
        Guid turnId, IReadOnlyList<HostedToolResult> results, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(results);
        // The Gateway deduplicates this turn's tool IDs and payload hash even after settlement.
        var path = "/v1/agent/turns/" + turnId.ToString("D") + "/tool-results";
        var body = JsonSerializer.Serialize(new { results }, Json);
        long? disconnectedAt = null;
        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var window = OpenRecoveryWindow(disconnectedAt);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, window.Token);
            try
            {
                using var response = await SendAsync(HttpMethod.Post, path, body, null, attempt.Token).ConfigureAwait(false);
                await ReadAsync(response, attempt.Token).ConfigureAwait(false);
                return disconnectedAt.HasValue;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && RetryTransport(ex))
            {
                disconnectedAt ??= _time.GetTimestamp();
            }
            await WaitToReconnectAsync(disconnectedAt.Value, attempts++, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>WP-431: the optional subscription header the relay reads. It grants nothing; the bearer token still does.</summary>
    public const string StreamReaderHeader = "X-Temno-Stream-Reader";

    /// <summary>A new subscription key: 32 lowercase hex characters from 16 random bytes.</summary>
    public static string NewStreamReader() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static bool ValidStreamReader(string? key) =>
        key is { Length: 32 } && key.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// What a subscription key is bound to: a digest of the account, the auth session and the turn, so the chat's saved
    /// state names none of them in the clear and a key is never carried into another account, session or turn.
    /// </summary>
    public static string StreamReaderScope(string? account, string? session, Guid turnId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', "temno-stream-reader-v1", account ?? "", session ?? "", turnId.ToString("D")))))[..32].ToLowerInvariant();

    /// <summary>The current access token's auth session id, read without verification (the server verifies), or null.</summary>
    public string? SessionId()
    {
        try
        {
            var parts = _accessToken()?.Split('.');
            if (parts is not { Length: 3 } || parts[1].Length is 0 or > 12000) return null;
            var body = parts[1].Replace('-', '+').Replace('_', '/');
            body = body.PadRight((body.Length + 3) / 4 * 4, '=');
            return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(body)))?["session_id"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException) { return null; }
    }

    public IAsyncEnumerable<HostedSseEvent> StreamEventsAsync(Guid turnId, int after, CancellationToken cancellationToken = default)
        => StreamEventsAsync(turnId, after, null, cancellationToken);

    /// <summary>
    /// The turn's events from <paramref name="after"/>, reconnecting within the recovery window. Every reconnect sends the
    /// same <paramref name="streamReader"/> key (WP-431) beside the integer cursor; null sends none, as older desktops did.
    /// </summary>
    public async IAsyncEnumerable<HostedSseEvent> StreamEventsAsync(
        Guid turnId,
        int after,
        string? streamReader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (streamReader is not null && !ValidStreamReader(streamReader))
            throw new ArgumentException("A stream reader key is 32 lowercase hex characters.", nameof(streamReader));
        long? disconnectedAt = null;
        var attempts = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var window = OpenRecoveryWindow(disconnectedAt);
            using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, window.Token);
            var stream = ReadStreamAsync(turnId, after, streamReader, connection.Token).GetAsyncEnumerator();
            await using (stream.ConfigureAwait(false))
            {
                while (true)
                {
                    bool next;
                    // Catch only transport reads. Tool execution and persistence happen in the consumer and
                    // must retain their own recovery checkpoints rather than being retried as a stream failure.
                    try { next = await stream.MoveNextAsync().ConfigureAwait(false); }
                    catch (Exception ex) when (!cancellationToken.IsCancellationRequested && RetryTransport(ex)) { break; }
                    if (!next) break;
                    var evt = stream.Current;
                    if (evt.Sequence <= after) continue;
                    after = evt.Sequence;
                    disconnectedAt = null;
                    attempts = 0;
                    window.CancelAfter(Timeout.InfiniteTimeSpan);
                    yield return evt;
                    if (evt.Kind == "terminal") yield break;
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            disconnectedAt ??= _time.GetTimestamp();
            await WaitToReconnectAsync(disconnectedAt.Value, attempts++, cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan RecoveryRemaining(long since)
    {
        var remaining = StreamReconnectWindow - _time.GetElapsedTime(since);
        return remaining > TimeSpan.Zero ? remaining : throw StreamRecoveryExpired();
    }

    private CancellationTokenSource OpenRecoveryWindow(long? since) =>
        new(since.HasValue ? RecoveryRemaining(since.Value) : Timeout.InfiniteTimeSpan, _time);

    private Task WaitToReconnectAsync(long since, int attempt, CancellationToken cancellationToken)
    {
        var remaining = RecoveryRemaining(since);
        var delay = TimeSpan.FromSeconds(1 << Math.Min(attempt, 3));
        return Task.Delay(delay < remaining ? delay : remaining, _time, cancellationToken);
    }

    private static bool RetryTransport(Exception ex) => ex is HttpRequestException or IOException or OperationCanceledException
        || ex is HostedApiException { StatusCode: 408 or 429 or 500 or 502 or 503 or 504 };

    private static HostedApiException StreamRecoveryExpired() => new(
        "The connection could not recover within five minutes. This turn is still pending; reconnect to recover it without another reservation.");

    private async IAsyncEnumerable<HostedSseEvent> ReadStreamAsync(Guid turnId, int after, string? streamReader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var token = RequireToken();
        using var request = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/v1/agent/turns/" + turnId.ToString("D") + "/events");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HostedEndpoints.StampEnvironment(request);
        if (after > 0)
            request.Headers.TryAddWithoutValidation("Last-Event-ID", after.ToString());
        if (streamReader is not null)
            request.Headers.TryAddWithoutValidation(StreamReaderHeader, streamReader);
        using var opening = new CancellationTokenSource(StreamIdleTimeout, _time);
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, opening.Token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var fail = await response.Content.ReadAsStringAsync(headers.Token).ConfigureAwait(false);
            throw HostedApiErrors.FromHttp((int)response.StatusCode, fail);
        }
        opening.CancelAfter(Timeout.InfiniteTimeSpan);

        // Synchronous dispose: the stream is fully read (or the read failed and the
        // response is dropped below), so there is nothing left to flush asynchronously.
        // An `await using` here would capture the caller's context in its DisposeAsync.
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        string? eventName = null;
        string? id = null;
        var data = new StringBuilder();
        while (!cancellationToken.IsCancellationRequested)
        {
            using var idle = new CancellationTokenSource(StreamIdleTimeout, _time);
            using var read = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);
            var line = (await reader.ReadLineAsync(read.Token).ConfigureAwait(false))?.TrimStart();
            if (line is null)
                yield break;
            if (line.Length == 0)
            {
                if (eventName is not null && data.Length > 0)
                {
                    JsonNode? parsed;
                    try { parsed = JsonNode.Parse(data.ToString()); }
                    catch (JsonException) { throw new HostedApiException("The hosted event was invalid. Reconnect to recover this turn."); }
                    if (parsed is null || !int.TryParse(id, out var sequence) || sequence <= 0)
                        throw new HostedApiException("The hosted event cursor was invalid. Reconnect to recover this turn.");
                    yield return new HostedSseEvent(sequence, eventName, parsed);
                }

                eventName = null;
                id = null;
                data.Clear();
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventName = line[6..].Trim();
            else if (line.StartsWith("id:", StringComparison.Ordinal))
                id = line[3..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? json,
        Guid? idempotency,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", RequireToken());
        HostedEndpoints.StampEnvironment(request);
        if (idempotency is { } key)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return body;
        throw HostedApiErrors.FromHttp((int)response.StatusCode, body);
    }

    private string RequireToken()
    {
        var token = _accessToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new HostedApiException("Sign in to use hosted credits.", 401, "authentication_required");
        return token;
    }
}

public sealed class HostedAgentCatalog
{
    public bool Configured { get; set; }
    public bool AcceptTurns { get; set; }
    public HostedBillingPolicy? BillingPolicy { get; set; }
    public List<HostedAgentProfile> Models { get; set; } = [];
    public string[] Tools { get; set; } = [];
    // WP-254: UTF-8 size of each approved tool's schema, for the composer's estimate.
    public Dictionary<string, int>? ToolSchemaBytes { get; set; }
    // WP-436: what a tool result's images may be. Absent from gateways that predate it.
    public HostedImagePolicy? ToolResultImages { get; set; }

    public string[] SelectTools(IEnumerable<string> localToolNames)
    {
        var approved = new HashSet<string>(Tools ?? [], StringComparer.Ordinal);
        return localToolNames.Where(approved.Contains).Distinct(StringComparer.Ordinal).ToArray();
    }
}

public sealed class HostedBillingPolicy
{
    public long MinimumBillableTurnCredits { get; set; } = 5;
    public long CreditsPerUpstreamUsd { get; set; } = 300;
    // WP-254: "estimated" when the gateway sizes each hold itself from spendingCapCredits 0.
    public string? Hold { get; set; }
    public long AutoHoldLimitCredits { get; set; }
    public int HoldSteps { get; set; }
    public int LikelySteps { get; set; } = 3;
    public int StepGrowthBytes { get; set; } = 16384;
    // Credits a balance-limited hold leaves free for other jobs (owner decision 2026-09-25).
    public long BalanceHeadroomCredits { get; set; }
    public bool EstimatedHolds => string.Equals(Hold, "estimated", StringComparison.Ordinal);
}

public sealed class HostedAgentProfile
{
    public string Id { get; set; } = "";
    public string Model { get; set; } = "";
    public string[] Efforts { get; set; } = [];
    public string[] OffEfforts { get; set; } = [];
    public string DefaultEffort { get; set; } = "";
    public int MaxOutputTokens { get; set; }
    public bool Qualified { get; set; }
    public decimal InputUsdPerMillion { get; set; }
    public decimal OutputUsdPerMillion { get; set; }
    // WP-254: server-authoritative completion tokens per effort. Absent from gateways that predate it.
    public Dictionary<string, HostedCompletionCapacity>? Completion { get; set; }
    // WP-254: the capacity when reasoning is off, for models that allow it.
    public HostedCompletionCapacity? CompletionOff { get; set; }
    // WP-331: the gateway's prompt cache policy and prices, for a likely range that matches its own.
    public HostedPromptCache? PromptCache { get; set; }
    public decimal CacheReadUsdPerMillion { get; set; }
    public decimal CacheWrite5mUsdPerMillion { get; set; }
    public decimal CacheWrite1hUsdPerMillion { get; set; }
    // Long-context rates: a request whose prompt reaches a tier's threshold bills entirely at it. Absent from older gateways.
    public List<HostedPriceTier>? PriceTiers { get; set; }
    // WP-436: the model takes tool-result images, under the catalog's rules (set when the catalog is read).
    public bool ImageInput { get; set; }
    [JsonIgnore]
    public HostedImagePolicy? ImagePolicy { get; set; }

    /// <summary>The rates a request with <paramref name="promptTokens"/> prompt tokens bills at.</summary>
    public HostedPriceTier RatesAt(decimal promptTokens)
    {
        var rates = new HostedPriceTier
        {
            InputUsdPerMillion = InputUsdPerMillion, OutputUsdPerMillion = OutputUsdPerMillion,
            CacheReadUsdPerMillion = CacheReadUsdPerMillion, CacheWrite5mUsdPerMillion = CacheWrite5mUsdPerMillion,
            CacheWrite1hUsdPerMillion = CacheWrite1hUsdPerMillion,
        };
        foreach (var tier in (PriceTiers ?? []).OrderBy(t => t.MinPromptTokens))
            if (promptTokens >= tier.MinPromptTokens)
                rates = tier;
        return rates;
    }
}

public sealed class HostedPriceTier
{
    public int MinPromptTokens { get; set; }
    public decimal InputUsdPerMillion { get; set; }
    public decimal OutputUsdPerMillion { get; set; }
    public decimal CacheReadUsdPerMillion { get; set; }
    public decimal CacheWrite5mUsdPerMillion { get; set; }
    public decimal CacheWrite1hUsdPerMillion { get; set; }
}

public sealed class HostedPromptCache
{
    public string Type { get; set; } = "";
    public string? Ttl { get; set; }
}

public sealed class HostedCompletionCapacity
{
    public int Target { get; set; }
    public int Floor { get; set; }
    public int Typical { get; set; }
}

public sealed class HostedQuoteRequest
{
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    public bool ReasoningEnabled { get; set; } = true;
    public int MaxOutputTokens { get; set; }
    public long SpendingCapCredits { get; set; }
    public JsonArray Messages { get; set; } = [];
    public string[] Tools { get; set; } = [];
}

public sealed class HostedQuote
{
    public Guid Id { get; set; }
    public long Cap { get; set; }
    public long CallBound { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public HostedQuoteEstimate? Estimate { get; set; }
}

// A realistic figure for display, beside the conservative per-call bound the gateway enforces.
public sealed class HostedQuoteEstimate
{
    public int WorkTokens { get; set; }
    public int FloorTokens { get; set; }
    public int FirstCallTokens { get; set; }
    public long TypicalCallCredits { get; set; }
    public long ReportReserveCredits { get; set; }
    public long MinimumCapCredits { get; set; }
    public long RequiredCapCredits { get; set; }
    public string? ReportEffort { get; set; }
    public int MaxCalls { get; set; }
    public int MaxTools { get; set; }
    // WP-254 estimated hold: the likely charge, what is held, and what limited the hold (estimate, policy, balance or cap).
    public long? LikelyLowCredits { get; set; }
    public long? LikelyHighCredits { get; set; }
    public long? HoldCredits { get; set; }
    public string? HoldLimitedBy { get; set; }
    public long? AvailableCredits { get; set; }
    public long? MinimumHoldCredits { get; set; }
}

public sealed class HostedTurn
{
    public Guid Id { get; set; }
    public string? Status { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public bool? ReasoningEnabled { get; set; }
    public long? Cap { get; set; }
    public long? Charge { get; set; }
    public string? Error { get; set; }
    public bool UsageUnknown { get; set; }
    public bool CancelRequested { get; set; }
    public string? StopReason { get; set; }
}

public sealed class HostedToolResult
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
    // WP-436: omitted when null, so a text-only result is the request it always was.
    public List<HostedToolImage>? Images { get; set; }
}

public readonly record struct HostedSseEvent(int Sequence, string Kind, JsonNode? Data);

public sealed class HostedTurnProgress
{
    public string? VisibleText { get; init; }
    public string? Status { get; init; }
    public long? Charge { get; init; }
    public bool Pending { get; init; }
}
