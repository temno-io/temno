using System.Net.Http;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Temno.Core.Account;

namespace Temno.Core.Transcription;

public sealed class HostedTranscriptionClient
{
    public const int PartBytes = 4 * 1024 * 1024;
    public const string PriceVersion = "stt-local-v1";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Func<string?> _accessToken;
    private readonly Func<string?> _accountId;
    private readonly HostedTranscriptionRequestStore _requests;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private static readonly ConcurrentDictionary<Guid, ActiveRequest> Active = new();

    private sealed class ActiveRequest(CancellationTokenSource cancel)
    {
        public CancellationTokenSource Cancel { get; } = cancel;
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public HostedTranscriptionClient(string baseUrl, Func<string?> accessToken, HttpClient? httpClient = null,
        HostedTranscriptionRequestStore? requests = null, Func<string?>? accountId = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        _baseUrl = baseUrl.TrimEnd('/');
        _accessToken = accessToken ?? throw new ArgumentNullException(nameof(accessToken));
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _requests = requests ?? new HostedTranscriptionRequestStore();
        _accountId = accountId ?? (() => HostedRuntime.Session?.UserId);
        _delay = delay ?? ((gap, ct) => Task.Delay(gap, ct));
    }

    public async Task<TranscriptionCapability> GetCapabilityAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "/v1/transcription/capability", null, null, null, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TranscriptionCapability>(body, Json) ?? new TranscriptionCapability();
    }

    public async Task<TranscriptionResult> TranscribeAsync(
        PreparedPcmWav wav,
        string language,
        IProgress<string>? progress,
        CancellationToken cancellationToken = default,
        string? sourcePath = null,
        Action<TranscriptionResult>? persistResult = null,
        string? sourceIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(wav);
        language = NormalizeLanguage(language);
        sourcePath = sourcePath is null ? null : Path.GetFullPath(sourcePath);
        sourceIdentity ??= sourcePath is null ? null : TranscriptDiskCache.KeyForPath(sourcePath);
        var account = Account();
        using var lease = await _requests.AcquireAsync(account, _baseUrl, wav.Sha256.ToUpperInvariant(), language,
            sourcePath, cancellationToken, sourceIdentity: sourceIdentity).ConfigureAwait(false);
        return await RunAsync(lease, wav, progress, persistResult, cancellationToken, sourcePath, sourceIdentity).ConfigureAwait(false);
    }

    public async Task ResumePendingAsync(IPcmWavPreparer audio, CancellationToken ct = default)
    {
        var account = Account();
        foreach (var pending in _requests.Pending(account, _baseUrl))
        {
            try
            {
                using var lease = await _requests.AcquireAsync(account, _baseUrl, pending.Sha256, pending.Language,
                    pending.SourcePath, ct, existingOnly: true).ConfigureAwait(false);
                var request = lease.Request;
                if (request.ResultAcknowledged) continue;
                PreparedPcmWav? wav = null;
                if (!request.SubmitStarted && !request.CancelRequested)
                {
                    if (!SourceUnchanged(request)) continue;
                    wav = await audio.PrepareAsync(request.SourcePath!, ct).ConfigureAwait(false);
                    if (!string.Equals(wav.Sha256, request.Sha256, StringComparison.OrdinalIgnoreCase)) continue;
                }
                await RunAsync(lease, wav, null, result =>
                {
                    if (!SourceUnchanged(request)) throw new IOException("The transcription source changed.");
                    TranscriptDiskCache.Store(request.SourcePath!, result);
                }, ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (HostedApiException) when (!ct.IsCancellationRequested) { }
            catch (IOException) when (!ct.IsCancellationRequested) { }
        }
    }

    public static async Task CancelActiveAsync()
    {
        var pending = Active.Values.ToArray();
        foreach (var entry in pending)
            try { entry.Cancel.Cancel(); } catch (ObjectDisposedException) { }
        try { await Task.WhenAll(pending.Select(p => p.Finished.Task)).WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
        catch (TimeoutException) { } // Durable cancellation intent is retried after sign-in.
    }

    private static bool SourceUnchanged(HostedTranscriptionRequest request)
        => request.SourcePath is not null && File.Exists(request.SourcePath)
           && TranscriptDiskCache.KeyForPath(request.SourcePath) == request.SourceIdentity;

    private string Account() => _accountId() is { Length: > 0 } account ? account
        : throw new HostedApiException("Sign in to resume cloud transcription.", 401, "authentication_required");

    private void SameAccount(HostedTranscriptionRequest request)
    {
        if (_accountId() != request.AccountId)
            throw new HostedApiException("Sign in to the original account to resume this transcription.", 403, "transcription_account_changed");
    }

    private async Task<TranscriptionResult> RunAsync(HostedTranscriptionRequestStore.Lease lease, PreparedPcmWav? wav,
        IProgress<string>? progress, Action<TranscriptionResult>? persistResult, CancellationToken ct,
        string? sourcePath = null, string? sourceIdentity = null)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var id = Guid.NewGuid();
        var active = new ActiveRequest(run);
        Active[id] = active;
        try
        {
            var request = lease.Request;
            SameAccount(request);
            if (request.CancelRequested)
            {
                await RetryAsync(() => CancelRequestAsync(lease, run.Token), request, progress, run.Token).ConfigureAwait(false);
                throw new OperationCanceledException("The saved transcription was canceled.");
            }
            var result = await ContinueAsync(lease, wav, progress, run.Token, sourcePath, sourceIdentity).ConfigureAwait(false);
            if (persistResult is not null)
            {
                persistResult(result);
                request.ResultAcknowledged = true;
                lease.Save();
            }
            return result;
        }
        catch (Exception) when (run.IsCancellationRequested)
        {
            lease.Request.CancelRequested = true;
            lease.Save();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await RetryAsync(() => CancelRequestAsync(lease, cleanup.Token), lease.Request, null, cleanup.Token).ConfigureAwait(false); }
            catch (Exception) { } // Keep the request and cancellation intent if acknowledgement is unavailable.
            throw new OperationCanceledException(run.Token);
        }
        finally
        {
            Active.TryRemove(id, out _);
            active.Finished.TrySetResult();
        }
    }

    private async Task<TranscriptionResult> ContinueAsync(HostedTranscriptionRequestStore.Lease lease, PreparedPcmWav? wav,
        IProgress<string>? progress, CancellationToken ct, string? sourcePath, string? sourceIdentity)
    {
        var request = lease.Request;
        if (request.ResultAcknowledged && SourceUnchanged(request)
            && TranscriptDiskCache.TryLoad(request.SourcePath!, out var cached) && cached is not null) return cached;
        if (request.ResultAcknowledged && wav is not null)
        {
            // Only a new foreground request after successful local delivery can replace these keys.
            request.UploadKey = Guid.NewGuid(); request.JobKey = Guid.NewGuid();
            request.UploadId = null; request.JobId = null; request.QuotedCredits = null;
            request.SubmitStarted = false; request.ResultAcknowledged = false;
            if (sourcePath is not null) { request.SourcePath = sourcePath; request.SourceIdentity = sourceIdentity; }
            lease.Save();
        }
        if (request.JobId is null && !request.SubmitStarted)
        {
            if (wav is null) throw new IOException("The original audio is required to resume its upload.");
            progress?.Report("Uploading audio…");
            if (request.UploadId is null)
            {
                var upload = await RetryAsync(() => CreateUploadAsync(wav, request.UploadKey, ct), request, progress, ct).ConfigureAwait(false);
                request.UploadId = ValidId(upload.Id);
                lease.Save();
            }
            var stored = await RetryAsync(() => GetUploadAsync(request.UploadId, ct), request, progress, ct).ConfigureAwait(false);
            if (stored.State != "finalized")
            {
                for (var ordinal = 0; ordinal < (int)Math.Ceiling(wav.Bytes.Length / (double)PartBytes); ordinal++)
                {
                    if (stored.StoredParts.Contains(ordinal)) continue;
                    ct.ThrowIfCancellationRequested();
                    var offset = ordinal * PartBytes;
                    var length = Math.Min(PartBytes, wav.Bytes.Length - offset);
                    await RetryAsync(async () =>
                    {
                        await PutPartAsync(request.UploadId, ordinal, wav.Bytes.AsMemory(offset, length), ct).ConfigureAwait(false);
                        return true;
                    }, request, progress, ct).ConfigureAwait(false);
                }
            }

            progress?.Report("Finalizing upload…");
            var finalized = stored.State == "finalized" ? stored
                : await RetryAsync(() => FinalizeAsync(request.UploadId, ct), request, progress, ct, finalize: true).ConfigureAwait(false);
            if (finalized.QuotedCredits is not (>= 5 and <= 30)) throw new HostedApiException("The transcription quote was invalid.");
            request.QuotedCredits = finalized.QuotedCredits;
            request.SubmitStarted = true;
            lease.Save();
        }
        if (request.JobId is null)
        {
            progress?.Report("Starting cloud transcription…");
            var job = await RetryAsync(() => CreateJobAsync(request.UploadId!, request.Language, request.QuotedCredits!.Value,
                request.JobKey, ct), request, progress, ct).ConfigureAwait(false);
            request.JobId = ValidId(job.Id);
            lease.Save();
        }
        return await PollResultAsync(request.JobId, lease, progress, ct).ConfigureAwait(false);
    }

    private static string ValidId(string id) => Guid.TryParseExact(id, "D", out var parsed) && parsed != Guid.Empty
        ? id : throw new HostedApiException("The transcription response contained an invalid identifier.");

    private async Task<bool> CancelRequestAsync(HostedTranscriptionRequestStore.Lease lease, CancellationToken ct)
    {
        var request = lease.Request;
        SameAccount(request);
        if (!request.SubmitStarted) { lease.Remove(); return true; }
        var path = request.JobId is not null ? "/v1/transcription/jobs/" + request.JobId + "/cancel"
            : "/v1/transcription/submissions/cancel";
        using var response = await SendAsync(HttpMethod.Post, path, request.JobId is null ? JobPayload(request.UploadId!,
            request.Language, request.QuotedCredits!.Value) : "{}", request.JobKey, null, ct).ConfigureAwait(false);
        var job = JsonSerializer.Deserialize<TranscriptionJob>(await ReadAsync(response, ct).ConfigureAwait(false), Json)
            ?? throw new JsonException("Empty cancellation response");
        if (job.Status is "canceled" or "cancelled" or "failed" or "expired") lease.Remove();
        else if (job.Status == "succeeded")
        {
            request.JobId = ValidId(job.Id);
            request.CancelRequested = false;
            lease.Save(); // Settlement already won; preserve its result for recovery.
        }
        else throw new HttpRequestException("Cancellation has not settled.");
        return true;
    }

    private async Task<T> RetryAsync<T>(Func<Task<T>> action, HostedTranscriptionRequest request,
        IProgress<string>? progress, CancellationToken ct, bool finalize = false)
    {
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            SameAccount(request);
            try { return await action().ConfigureAwait(false); }
            catch (Exception error) when (!ct.IsCancellationRequested && (error is HttpRequestException or IOException or JsonException
                || error is OperationCanceledException
                || error is HostedApiException api && (api.StatusCode is 408 or 429 or >= 500
                    || finalize && api.Code == "transcription_conflict")))
            {
                progress?.Report("Connection interrupted. Your cloud transcription is saved; waiting to reconnect…");
                await _delay(TimeSpan.FromSeconds(Math.Min(10, 2 + attempt++ * 2)), ct).ConfigureAwait(false);
            }
        }
    }

    public async Task CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return;
        using var response = await SendAsync(HttpMethod.Post, "/v1/transcription/jobs/" + jobId + "/cancel", "{}", null, null, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;
        await ReadAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
            throw new HostedApiException("Cloud transcription needs English or Spanish.", code: "invalid_transcription_request");
        var trimmed = language.Trim();
        if (trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return "en";
        if (trimmed.StartsWith("es", StringComparison.OrdinalIgnoreCase))
            return "es";
        throw new HostedApiException("Cloud transcription currently admits English and Spanish.", code: "invalid_transcription_request");
    }

    public static long QuoteCredits(double durationSeconds)
        => Math.Clamp(5 * (long)Math.Ceiling(Math.Max(durationSeconds, 0.001) / 600d), 5, 30);

    private async Task<TranscriptionUpload> CreateUploadAsync(PreparedPcmWav wav, Guid key, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { bytes = wav.Bytes.Length, sha256 = wav.Sha256 }, Json);
        using var response = await SendAsync(HttpMethod.Post, "/v1/transcription/uploads", payload, key, null, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TranscriptionUpload>(body, Json)
               ?? throw new HostedApiException("The upload response was empty.");
    }

    private async Task<TranscriptionUpload> GetUploadAsync(string id, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "/v1/transcription/uploads/" + id, null, null, null, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TranscriptionUpload>(await ReadAsync(response, ct).ConfigureAwait(false), Json)
            ?? throw new JsonException("Empty upload response");
    }

    private async Task PutPartAsync(string uploadId, int ordinal, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(bytes.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(
            HttpMethod.Put,
            $"/v1/transcription/uploads/{uploadId}/parts/{ordinal}",
            null,
            null,
            content,
            cancellationToken).ConfigureAwait(false);
        await ReadAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TranscriptionUpload> FinalizeAsync(string uploadId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "/v1/transcription/uploads/" + uploadId + "/finalize", "{}", null, null, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TranscriptionUpload>(body, Json)
               ?? throw new HostedApiException("The finalize response was empty.");
    }

    private static string JobPayload(string uploadId, string language, long quotedCredits)
    {
        return JsonSerializer.Serialize(new
        {
            uploadId,
            language,
            keywords = Array.Empty<string>(),
            quotedCredits,
            priceVersion = PriceVersion,
        }, Json);
    }

    private async Task<TranscriptionJob> CreateJobAsync(string uploadId, string language, long quotedCredits, Guid key, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, "/v1/transcription/jobs", JobPayload(uploadId, language, quotedCredits), key, null, cancellationToken)
            .ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TranscriptionJob>(body, Json)
               ?? throw new HostedApiException("The transcription job response was empty.");
    }

    private async Task<TranscriptionResult> PollResultAsync(
        string jobId,
        HostedTranscriptionRequestStore.Lease lease,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = await RetryAsync(async () =>
            {
                using var response = await SendAsync(HttpMethod.Get, "/v1/transcription/jobs/" + jobId, null, null, null, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.Deserialize<TranscriptionJob>(await ReadAsync(response, cancellationToken).ConfigureAwait(false), Json)
                    ?? throw new JsonException("Empty status response");
            }, lease.Request, progress, cancellationToken).ConfigureAwait(false);
            var status = (job.Status ?? "").ToLowerInvariant();
            if (status is "succeeded" or "complete" or "completed")
                break;
            if (status is "failed" or "canceled" or "cancelled" or "expired")
            {
                lease.Remove(); // The server has reported a terminal, uncharged/released job.
                throw new HostedApiException(
                    HostedApiErrors.Describe(null, job.Error, null),
                    code: job.Error ?? "transcription_processing_failed");
            }

            var chunks = job.ChunkCount is > 0
                ? $" ({job.CompletedChunks ?? 0}/{job.ChunkCount})"
                : "";
            progress?.Report("Transcribing in the cloud" + chunks + "…");
            await _delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        return await RetryAsync(async () =>
        {
        using var resultResponse = await SendAsync(HttpMethod.Get, "/v1/transcription/jobs/" + jobId + "/result", null, null, null, cancellationToken)
            .ConfigureAwait(false);
        var resultBody = await ReadAsync(resultResponse, cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<TranscriptionResult>(resultBody, Json);
        if (result is null)
            throw new JsonException("The transcription result was empty.");
        return result;
        }, lease.Request, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? json,
        Guid? idempotency,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var token = _accessToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new HostedApiException("Sign in to use hosted credits.", 401, "authentication_required");
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HostedEndpoints.StampEnvironment(request);
        if (idempotency is { } key)
            request.Headers.TryAddWithoutValidation("Idempotency-Key", key.ToString("D"));
        if (content is not null)
            request.Content = content;
        else if (json is not null)
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
}

public sealed class TranscriptionCapability
{
    public bool Qualified { get; set; }
    public bool Configured { get; set; }
    public bool AcceptJobs { get; set; }
    public string[] Languages { get; set; } = [];
}

public sealed class TranscriptionUpload
{
    public string Id { get; set; } = "";
    public int? PartCount { get; set; }
    public long? QuotedCredits { get; set; }
    public string? State { get; set; }
    public int[] StoredParts { get; set; } = [];
}

public sealed class TranscriptionJob
{
    public string Id { get; set; } = "";
    public string? Status { get; set; }
    public string? Error { get; set; }
    public int? ChunkCount { get; set; }
    public int? CompletedChunks { get; set; }
}
