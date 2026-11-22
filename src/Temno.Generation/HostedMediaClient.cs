using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Temno.Core.Account;
using Temno.Core.Generation;

namespace Temno.Generation;

// Transport-only HTTP client for the public /v1/media/* endpoints. Quote and
// job orchestration (uploads, placeholders, landing) lives in the MCP partial
// so MCP tools and native UI share one implementation.

public sealed class HostedMediaClient
{
    public const long MaxImageBytes = 12L * 1024 * 1024;
    public const long MaxVideoBytes = 512L * 1024 * 1024;
    public const long LegacyMaxVideoBytes = 256L * 1024 * 1024;
    public const long MaxAudioBytes = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Func<string?> _accessToken;
    private readonly HostedFileUploader _uploader;
    private static readonly HttpClient TransferHttp = RemoteMediaDownloader.CreateHttpClient(TimeSpan.FromMinutes(5));

    public HostedMediaClient(string baseUrl, Func<string?> accessToken, HttpClient? httpClient = null,
        HttpClient? transferClient = null, Func<string, CancellationToken, Task<System.Net.IPAddress[]>>? hostResolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _baseUrl = baseUrl.TrimEnd('/');
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
        _uploader = new HostedFileUploader(SendAsync, transferClient ?? TransferHttp, hostResolver);
    }

    public string BaseUrl => _baseUrl;

    public async Task<HostedMediaUploadResult> UploadFileAsync(string path, string contentType, Guid key,
        CancellationToken cancellationToken = default, string? expectedSha256 = null)
    {
        var kind = KindFor(contentType);
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var body = await _uploader.Upload(source, contentType, key, "/v1/media/uploads", MaxBytesFor(kind),
            kind == "video" ? LegacyMaxVideoBytes : MaxBytesFor(kind), cancellationToken, expectedSha256).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HostedMediaUploadResult>(body, Json)
            ?? throw new GenerationException("The media upload response was empty.");
    }

    public static string KindFor(string contentType) => contentType switch
    {
        "image/png" or "image/jpeg" or "image/webp" => "image",
        "video/mp4" or "video/webm" or "video/quicktime" => "video",
        "audio/mpeg" or "audio/wav" or "audio/ogg" or "audio/flac" or "audio/mp4" or "audio/aac" => "audio",
        _ => throw new GenerationException(
            "Reference media must be PNG/JPEG/WebP, MP4/WebM/MOV, or MP3/WAV/OGG/FLAC/M4A/AAC.",
            code: "unsupported_media_type"),
    };

    public static long MaxBytesFor(string kind) => kind switch
    {
        "image" => MaxImageBytes,
        "video" => MaxVideoBytes,
        "audio" => MaxAudioBytes,
        _ => throw new GenerationException("Unknown media kind.", code: "invalid_request"),
    };

    public async Task<HostedMediaCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/v1/media/catalog", null, null, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HostedMediaCatalog>(body, Json)
            ?? throw new GenerationException("The media catalog response was empty.");
    }

    public async Task<HostedMediaUploadResult> UploadAsync(
        byte[] bytes,
        string contentType,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var kind = KindFor(contentType);
        if (bytes.Length == 0 || bytes.Length > (kind == "video" ? LegacyMaxVideoBytes : MaxBytesFor(kind)))
            throw new GenerationException(
                $"Reference {kind} is over the upload limit.",
                code: "media_too_large");
        if (idempotencyKey == Guid.Empty)
            throw new GenerationException("An idempotency key is required.", code: "idempotency_key_required");

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await SendAsync(HttpMethod.Post, "/v1/media/uploads", content, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HostedMediaUploadResult>(body, Json)
            ?? throw new GenerationException("The media upload response was empty.");
    }

    public async Task<HostedMediaQuote> QuoteAsync(
        HostedMediaQuoteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new GenerationException("model is required.", code: "invalid_request");
        if (Array.IndexOf(HostedMediaOperations.All, request.Operation) < 0)
            throw new GenerationException("Unknown media operation.", code: "invalid_request");
        if (request.Inputs is { } inputs)
        {
            var seen = new HashSet<Guid>();
            foreach (var input in inputs)
            {
                if (string.IsNullOrWhiteSpace(input.Role) || input.AssetId == Guid.Empty || !seen.Add(input.AssetId))
                    throw new GenerationException("Media inputs must carry a role and distinct asset ids.", code: "invalid_request");
            }
        }

        var json = JsonSerializer.Serialize(request, Json);
        using var response = await SendAsync(HttpMethod.Post, "/v1/media/quotes", JsonContent(json), null, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        var quote = JsonSerializer.Deserialize<HostedMediaQuote>(body, Json);
        if (quote is null || string.IsNullOrWhiteSpace(quote.QuoteId))
            throw new GenerationException("The media quote response did not include a quote id.");
        return quote;
    }

    public async Task<HostedMediaJob> SubmitAsync(
        string quoteId,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(quoteId))
            throw new GenerationException("quoteId is required.", code: "invalid_request");
        if (idempotencyKey == Guid.Empty)
            throw new GenerationException("An idempotency key is required.", code: "idempotency_key_required");

        var json = JsonSerializer.Serialize(new { quoteId }, Json);
        using var response = await SendAsync(HttpMethod.Post, "/v1/media/jobs", JsonContent(json), idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HostedMediaJob>(body, Json)
            ?? throw new GenerationException("The media job response was empty.");
    }

    public async Task<HostedMediaJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return null;
        using var response = await SendAsync(HttpMethod.Get, "/v1/media/jobs/" + jobId, null, null, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        var job = JsonSerializer.Deserialize<HostedMediaJob>(body, Json);
        return string.IsNullOrWhiteSpace(job?.Id) ? null : job;
    }

    public Task<HostedJobObservation> ReadStatusAsync(string jobId, CancellationToken cancellationToken = default)
        => ReadObservedAsync(HttpMethod.Get, "/v1/media/jobs/" + jobId, null, null, submit: false, cancellationToken);

    public Task<HostedJobObservation> SubmitObservedAsync(string quoteId, Guid idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(quoteId))
            throw new GenerationException("quoteId is required.", code: "invalid_request");
        if (idempotencyKey == Guid.Empty)
            throw new GenerationException("An idempotency key is required.", code: "idempotency_key_required");
        var json = JsonSerializer.Serialize(new { quoteId }, Json);
        return ReadObservedAsync(HttpMethod.Post, "/v1/media/jobs", JsonContent(json), idempotencyKey, submit: true, cancellationToken);
    }

    public Task<HostedCancelResult> CancelSubmissionAsync(string quoteId, Guid idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(quoteId, out _) || idempotencyKey == Guid.Empty)
            throw new GenerationException("The saved media submission is invalid.", code: "invalid_request");
        return CancelObservedAsync("/v1/media/submissions/cancel",
            JsonContent(JsonSerializer.Serialize(new { quoteId }, Json)), idempotencyKey, cancellationToken);
    }

    public Task<HostedCancelResult> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return Task.FromResult(HostedCancelResult.TransportStillWorking());
        return CancelObservedAsync("/v1/media/jobs/" + jobId + "/cancel", JsonContent("{}"), null, cancellationToken);
    }

    private async Task<HostedJobObservation> ReadObservedAsync(
        HttpMethod method, string path, HttpContent? content, Guid? idempotency, bool submit, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await SendAsync(method, path, content, idempotency, timeout.Token).ConfigureAwait(false);
            var retry = RetryAfter(response);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return submit
                ? HostedStatusReads.FromSubmitResponse((int)response.StatusCode, body, retry)
                : HostedStatusReads.FromStatusResponse((int)response.StatusCode, body, retry);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (HostedStatusReads.IsSuspended(ex))
        {
            return HostedJobObservation.Suspended();
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException || HostedStatusReads.IsStillWorking(ex))
        {
            return HostedJobObservation.Working();
        }
    }

    private async Task<HostedCancelResult> CancelObservedAsync(
        string path, HttpContent content, Guid? idempotency, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await SendAsync(HttpMethod.Post, path, content, idempotency, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return HostedStatusReads.FromCancelResponse((int)response.StatusCode, body, RetryAfter(response));
        }
        catch (Exception ex) when (HostedStatusReads.IsSuspended(ex))
        {
            return new HostedCancelResult(HostedCancelResult.Suspended, HostedJobObservation.Suspended());
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or OperationCanceledException || HostedStatusReads.IsStillWorking(ex))
        {
            return HostedCancelResult.TransportStillWorking();
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
            return date - DateTimeOffset.UtcNow;
        return null;
    }

    private static StringContent JsonContent(string json)
        => new(json, Encoding.UTF8, "application/json");

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        Guid? idempotency,
        CancellationToken cancellationToken)
    {
        var token = _accessToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new GenerationException("Sign in to use hosted credits.", 401, "authentication_required");

        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        HostedEndpoints.StampEnvironment(request);
        if (idempotency is { } key)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        if (content is not null)
            request.Content = content;
        return await _http.SendAsync(request, path.EndsWith("/uploads/start", StringComparison.Ordinal) || path.EndsWith("/complete", StringComparison.Ordinal)
            ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return body;
        HostedApiErrors.TryParse(body, out var code, out var message);
        throw new GenerationException(
            HostedApiErrors.Describe((int)response.StatusCode, code, message),
            (int)response.StatusCode,
            code);
    }
}
