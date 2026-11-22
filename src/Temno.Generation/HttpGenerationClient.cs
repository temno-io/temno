using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Temno.Core.IO;

namespace Temno.Generation;

public class HttpGenerationClient : IGenerationClient
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <exception cref="ArgumentException">No endpoint URL was passed and TEMNO_API_BASE_URL is unset.
    /// There is no default host: generation goes only to an endpoint the user configured.</exception>
    public HttpGenerationClient(string? baseUrl = null, string? apiKey = null, HttpClient? httpClient = null)
    {
        var url = string.IsNullOrWhiteSpace(baseUrl) ? EnvCompat.Get("TEMNO_API_BASE_URL") : baseUrl;
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException(
                "A generation endpoint URL is required: pass one or set TEMNO_API_BASE_URL.",
                nameof(baseUrl));
        _baseUrl = url.Trim();
        _apiKey = apiKey ?? EnvCompat.Get("TEMNO_API_KEY");
        _httpClient = httpClient ?? new HttpClient();

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        if (!string.IsNullOrEmpty(_apiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }
    }

    private string NormalizeUrl(string path)
        => $"{_baseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    public async Task<List<CatalogEntry>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var url = NormalizeUrl("api/models");
        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        var responseString = await ReadSuccessStringAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<List<CatalogEntry>>(responseString, _jsonOptions) ?? new List<CatalogEntry>();
    }

    public async Task<string> SubmitGenerationAsync(
        string model,
        BackendGenerationParams parameters,
        string? projectId = null,
        CancellationToken cancellationToken = default)
    {
        var url = NormalizeUrl("api/generations/submit");
        var requestBody = new
        {
            model = model,
            @params = parameters,
            projectId = projectId
        };
        var content = new StringContent(JsonSerializer.Serialize(requestBody, _jsonOptions), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        var responseString = await ReadSuccessStringAsync(response, cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<SubmitResult>(responseString, _jsonOptions);
        if (result == null)
        {
            throw new InvalidOperationException("Failed to parse submit generation response");
        }
        return result.JobId;
    }

    public async Task<BackendGenerationJob?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var url = NormalizeUrl($"api/generations/status/{jobId}");
        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        var responseString = await ReadSuccessStringAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<BackendGenerationJob>(responseString, _jsonOptions);
    }

    public async Task<string> UploadReferenceAsync(
        string filePath,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var url = NormalizeUrl("api/uploads");
        using var fileStream = File.OpenRead(filePath);
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        content.Add(fileContent, "file", Path.GetFileName(filePath));

        using var response = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
        var responseString = await ReadSuccessStringAsync(response, cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<UrlResponse>(responseString, _jsonOptions);
        if (result == null)
        {
            throw new InvalidOperationException("Failed to upload reference");
        }
        return result.Url;
    }

    private static async Task<string> ReadSuccessStringAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await GenerationHttpErrors.ThrowIfUnsuccessfulAsync(response, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        GenerationHttpErrors.ThrowIfErrorPayload(body);
        return body;
    }

    private class SubmitResult
    {
        [JsonPropertyName("jobId")]
        public string JobId { get; set; } = "";
    }

    private class UrlResponse
    {
        [JsonPropertyName("url")]
        public string Url { get; set; } = "";
    }
}
