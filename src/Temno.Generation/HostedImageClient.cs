using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Temno.Core.Account;
using Temno.Core.Generation;

namespace Temno.Generation;

// Transport-only HTTP client for the public /v1/images/* endpoints. Quote and
// job orchestration (uploads, placeholders, landing) lives in the MCP partial
// so MCP tools and native UI share one implementation.

public sealed class HostedImageClient
{
    public const long MaxUploadBytes = 12L * 1024 * 1024;

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

    public HostedImageClient(string baseUrl, Func<string?> accessToken, HttpClient? httpClient = null,
        HttpClient? transferClient = null, Func<string, CancellationToken, Task<System.Net.IPAddress[]>>? hostResolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _baseUrl = baseUrl.TrimEnd('/');
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
        _uploader = new HostedFileUploader(SendAsync, transferClient ?? TransferHttp, hostResolver);
    }

    public string BaseUrl => _baseUrl;

    public async Task<HostedImageUploadResult> UploadFileAsync(string path, string contentType, Guid key,
        CancellationToken cancellationToken = default, string? expectedSha256 = null)
    {
        if (contentType is not ("image/png" or "image/jpeg" or "image/webp"))
            throw new GenerationException("Reference images must be PNG, JPEG, or WebP.", code: "invalid_image_type");
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var body = await _uploader.Upload(source, contentType, key, "/v1/images/uploads", MaxUploadBytes, MaxUploadBytes,
            cancellationToken, expectedSha256).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HostedImageUploadResult>(body, Json)
            ?? throw new GenerationException("The image upload response was empty.");
    }

    public async Task<HostedImageCatalog> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/v1/images/catalog", null, null, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HostedImageCatalog>(body, Json)
            ?? throw new GenerationException("The image catalog response was empty.");
    }

    public async Task<HostedImageUploadResult> UploadAsync(
        byte[] bytes,
        string contentType,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length == 0 || bytes.Length > MaxUploadBytes)
            throw new GenerationException(
                "Reference images must be non-empty and under 12 MB.",
                code: "image_too_large");
        if (contentType is not ("image/png" or "image/jpeg" or "image/webp"))
            throw new GenerationException(
                "Reference images must be PNG, JPEG, or WebP.",
                code: "invalid_image_type");
        if (idempotencyKey == Guid.Empty)
            throw new GenerationException("An idempotency key is required.", code: "idempotency_key_required");

        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        using var response = await SendAsync(HttpMethod.Post, "/v1/images/uploads", content, idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HostedImageUploadResult>(body, Json)
            ?? throw new GenerationException("The image upload response was empty.");
    }

    public async Task<HostedImageQuote> QuoteAsync(
        HostedImageQuoteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new GenerationException("model is required.", code: "invalid_request");
        if (request.Operation is not (HostedImageOperations.Generate or HostedImageOperations.Edit))
            throw new GenerationException("operation must be generate_image or edit_image.", code: "invalid_request");
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new GenerationException("prompt is required.", code: "invalid_request");

        var json = JsonSerializer.Serialize(request, Json);
        using var response = await SendAsync(HttpMethod.Post, "/v1/images/quotes", JsonContent(json), null, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        var quote = JsonSerializer.Deserialize<HostedImageQuote>(body, Json);
        if (quote is null || string.IsNullOrWhiteSpace(quote.QuoteId))
            throw new GenerationException("The image quote response did not include a quote id.");
        return quote;
    }

    public async Task<HostedImageJob> SubmitAsync(
        string quoteId,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(quoteId))
            throw new GenerationException("quoteId is required.", code: "invalid_request");
        if (idempotencyKey == Guid.Empty)
            throw new GenerationException("An idempotency key is required.", code: "idempotency_key_required");

        var json = JsonSerializer.Serialize(new { quoteId }, Json);
        using var response = await SendAsync(HttpMethod.Post, "/v1/images/jobs", JsonContent(json), idempotencyKey, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<HostedImageJob>(body, Json)
            ?? throw new GenerationException("The image job response was empty.");
    }

    public async Task<HostedImageJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return null;
        using var response = await SendAsync(HttpMethod.Get, "/v1/images/jobs/" + jobId, null, null, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        var job = JsonSerializer.Deserialize<HostedImageJob>(body, Json);
        return string.IsNullOrWhiteSpace(job?.Id) ? null : job;
    }

    public Task<HostedJobObservation> ReadStatusAsync(string jobId, CancellationToken cancellationToken = default)
        => ReadObservedAsync(HttpMethod.Get, "/v1/images/jobs/" + jobId, null, null, submit: false, cancellationToken);

    public Task<HostedJobObservation> SubmitObservedAsync(string quoteId, Guid idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(quoteId))
            throw new GenerationException("quoteId is required.", code: "invalid_request");
        if (idempotencyKey == Guid.Empty)
            throw new GenerationException("An idempotency key is required.", code: "idempotency_key_required");
        var json = JsonSerializer.Serialize(new { quoteId }, Json);
        return ReadObservedAsync(HttpMethod.Post, "/v1/images/jobs", JsonContent(json), idempotencyKey, submit: true, cancellationToken);
    }

    public Task<HostedCancelResult> CancelSubmissionAsync(string quoteId, Guid idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(quoteId, out _) || idempotencyKey == Guid.Empty)
            throw new GenerationException("The saved image submission is invalid.", code: "invalid_request");
        return CancelObservedAsync("/v1/images/submissions/cancel",
            JsonContent(JsonSerializer.Serialize(new { quoteId }, Json)), idempotencyKey, cancellationToken);
    }

    public Task<HostedCancelResult> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return Task.FromResult(HostedCancelResult.TransportStillWorking());
        return CancelObservedAsync("/v1/images/jobs/" + jobId + "/cancel", JsonContent("{}"), null, cancellationToken);
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
