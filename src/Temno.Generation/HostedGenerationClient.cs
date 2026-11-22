using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Temno.Core.Account;

namespace Temno.Generation;

/// <summary>
/// The hosted jobs route the desktop still needs: polling and cancelling a saved job. Hosted generation is
/// quote-only (WP-329), so the catalog is empty and a submit is refused without a request; new hosted work
/// goes through <see cref="HostedImageClient"/> and <see cref="HostedMediaClient"/> quotes.
/// </summary>
public sealed class HostedGenerationClient : IGenerationClient
{
    public const string QuoteRequiredMessage =
        "Hosted generation is quote-only: call list_models, then quote_image or quote_media, and submit with the returned quoteId.";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Func<string?> _accessToken;

    public HostedGenerationClient(string baseUrl, Func<string?> accessToken, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _baseUrl = baseUrl.TrimEnd('/');
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public Task<List<CatalogEntry>> ListModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new List<CatalogEntry>());

    public Task<string> SubmitGenerationAsync(
        string model,
        BackendGenerationParams parameters,
        string? projectId = null,
        CancellationToken cancellationToken = default)
        => Task.FromException<string>(new GenerationException(QuoteRequiredMessage, code: "quote_required"));

    public async Task<BackendGenerationJob?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/v1/jobs/" + jobId, null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return MapJob(JsonSerializer.Deserialize<HostedJob>(body, Json));
    }

    public Task<string> UploadReferenceAsync(string filePath, string contentType, CancellationToken cancellationToken = default)
        => Task.FromException<string>(new GenerationException(QuoteRequiredMessage, code: "quote_required"));

    public async Task CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return;
        using var response = await SendAsync(HttpMethod.Post, "/v1/jobs/" + jobId + "/cancel", "{}", cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;
        await ReadAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? json,
        CancellationToken cancellationToken)
    {
        var token = _accessToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new GenerationException("Sign in to use hosted credits.", 401, "authentication_required");

        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        HostedEndpoints.StampEnvironment(request);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
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

    private static BackendGenerationJob? MapJob(HostedJob? job)
    {
        if (job is null || string.IsNullOrWhiteSpace(job.Id))
            return null;
        var status = (job.Status ?? "").ToLowerInvariant() switch
        {
            "queued" => BackendGenerationStatus.Queued,
            "succeeded" => BackendGenerationStatus.Succeeded,
            "failed" or "canceled" or "cancelled" or "expired" => BackendGenerationStatus.Failed,
            _ => BackendGenerationStatus.Running,
        };
        return new BackendGenerationJob
        {
            Id = job.Id,
            Status = status,
            ResultUrls = job.ResultUrls,
            CostCredits = job.CostCredits is long credits ? (int)Math.Min(credits, int.MaxValue) : null,
            ErrorMessage = string.IsNullOrWhiteSpace(job.Error)
                ? null
                : JsonSerializer.Serialize(new { code = job.Error }),
        };
    }

    private sealed class HostedJob
    {
        public string Id { get; set; } = "";
        public string? Status { get; set; }
        public long? CostCredits { get; set; }
        public string? Error { get; set; }
        public List<string>? ResultUrls { get; set; }
    }
}
