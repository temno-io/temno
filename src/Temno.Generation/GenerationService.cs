using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Temno.Core.Project;
using Temno.Core.Session;

namespace Temno.Generation;

public class GenerationService
{
    public const long DefaultMaxDownloadBytes = 5L * 1024 * 1024 * 1024;

    private readonly IGenerationClient _client;
    private readonly HttpClient _httpClient;
    private readonly long _maxDownloadBytes;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>>? _hostResolver;

    public GenerationService(
        IGenerationClient client,
        HttpClient? httpClient = null,
        long maxDownloadBytes = DefaultMaxDownloadBytes,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostResolver = null)
    {
        _client = client;
        _httpClient = httpClient ?? RemoteMediaDownloader.CreateHttpClient(TimeSpan.FromMinutes(15));
        _maxDownloadBytes = maxDownloadBytes > 0
            ? maxDownloadBytes
            : throw new ArgumentOutOfRangeException(nameof(maxDownloadBytes));
        _hostResolver = hostResolver;
    }

    public IGenerationClient Client => _client;

    public async Task<BackendGenerationJob> PollJobAsync(
        string jobId,
        TimeSpan pollInterval,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var job = await _client.GetJobStatusAsync(jobId, cts.Token).ConfigureAwait(false);
                if (job == null)
                {
                    throw new InvalidOperationException($"Job {jobId} not found or failed to retrieve.");
                }

                if (job.Status == BackendGenerationStatus.Succeeded)
                {
                    return job;
                }
                if (job.Status == BackendGenerationStatus.Failed)
                {
                    if (GenerationHttpErrors.TryParsePayload(job.ErrorMessage, out var code, out var parsed)
                        && (!string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(parsed)))
                    {
                        throw new GenerationException(
                            GenerationHttpErrors.Describe(status: null, code, parsed),
                            code: code);
                    }

                    throw new InvalidOperationException($"Job {jobId} failed: {job.ErrorMessage ?? "Unknown error"}");
                }

                await Task.Delay(pollInterval, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Polling for job {jobId} timed out.");
        }
    }

    public async Task<IReadOnlyList<string>> DownloadAndLandAssetsAsync(
        IReadOnlyList<string> remoteUrls,
        string packageDirectory,
        string assetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteUrls);
        if (remoteUrls.Count == 0)
            throw new InvalidOperationException("Generation succeeded but returned no result URLs.");
        var landed = new List<string>(remoteUrls.Count);
        for (var index = 0; index < remoteUrls.Count; index++)
        {
            var suffix = index == 0 ? null : "-" + (index + 1);
            landed.Add(await DownloadAndLandAssetAsync(remoteUrls[index], packageDirectory, assetId, cancellationToken, suffix)
                .ConfigureAwait(false));
        }
        return landed;
    }

    public async Task<string> DownloadAndLandAssetAsync(
        string remoteUrl,
        string packageDirectory,
        string assetId,
        CancellationToken cancellationToken = default,
        string? fileSuffix = null)
    {
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Generated asset URL must be absolute.");
        if (string.IsNullOrWhiteSpace(assetId))
            throw new ArgumentException("Asset ID is required.", nameof(assetId));

        var mediaDir = Path.Combine(Path.GetFullPath(packageDirectory), ProjectConstants.MediaDirectoryName);
        Directory.CreateDirectory(mediaDir);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            assetId + "|" + fileSuffix + "|" + RemoteMediaDownloader.SourceIdentity(uri)))).ToLowerInvariant();
        var tempFilePath = Path.Combine(mediaDir, $".download-{identity}.partial");
        var downloaded = false;
        try
        {
            var download = await RemoteMediaDownloader.DownloadAsync(
                _httpClient,
                uri,
                tempFilePath,
                _maxDownloadBytes,
                cancellationToken,
                _hostResolver,
                resume: true).ConfigureAwait(false);
            downloaded = true;
            var extension = ResolveMediaExtension(download.FinalUri, download.ContentType);
            var safeAssetId = new string(assetId
                .Take(8)
                .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')
                .ToArray());
            if (string.IsNullOrWhiteSpace(safeAssetId))
                safeAssetId = Guid.NewGuid().ToString("N")[..8];
            var destinationPath = Path.Combine(mediaDir, $"gen-{safeAssetId}{fileSuffix}{extension}");
            destinationPath = ResolveNonCollidingPath(tempFilePath, destinationPath);
            File.Move(tempFilePath, destinationPath, overwrite: true);
            return destinationPath;
        }
        finally
        {
            if (downloaded && File.Exists(tempFilePath))
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch
                {
                    // Cleanup must preserve the original download or move error.
                }
            }
        }
    }

    // A retry downloads the same bytes to the same deterministic name and must keep
    // it; anything with different bytes (an original or another job's output) must
    // never be overwritten, so it gets a fresh -rN name instead.
    private static string ResolveNonCollidingPath(string tempFilePath, string destinationPath)
    {
        if (!File.Exists(destinationPath))
            return destinationPath;
        try
        {
            if (new FileInfo(destinationPath).Length == new FileInfo(tempFilePath).Length)
            {
                using var existing = File.OpenRead(destinationPath);
                using var incoming = File.OpenRead(tempFilePath);
                if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), SHA256.HashData(incoming)))
                    return destinationPath;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var dir = Path.GetDirectoryName(destinationPath)!;
        var stem = Path.GetFileNameWithoutExtension(destinationPath);
        var ext = Path.GetExtension(destinationPath);
        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(dir, $"{stem}-r{n}{ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
        return Path.Combine(dir, $"{stem}-r{Guid.NewGuid():N}{ext}");
    }

    // Polls an images-path job by id only. A local timeout or retry never
    // resubmits provider work and never spends again; recovery re-polls the
    // same job id or re-downloads its result URLs.
    public async Task<HostedImageJob> PollImageJobAsync(
        HostedImageClient client,
        string jobId,
        TimeSpan pollInterval,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var job = await client.GetJobAsync(jobId, cts.Token).ConfigureAwait(false);
                if (job == null)
                {
                    throw new InvalidOperationException($"Job {jobId} not found or failed to retrieve.");
                }

                if (job.Status == "succeeded")
                {
                    return job;
                }
                if (job.Status is "failed" or "canceled")
                {
                    var payload = job.ToBackendJob().ErrorMessage;
                    if (GenerationHttpErrors.TryParsePayload(payload, out var code, out var parsed)
                        && (!string.IsNullOrEmpty(code) || !string.IsNullOrEmpty(parsed)))
                    {
                        throw new GenerationException(
                            GenerationHttpErrors.Describe(status: null, code, parsed),
                            code: code);
                    }

                    throw new InvalidOperationException($"Job {jobId} {job.Status}: {job.Error ?? "Unknown error"}");
                }

                await Task.Delay(pollInterval, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"Polling for job {jobId} timed out.");
        }
    }

    private static string ResolveMediaExtension(Uri finalUri, string? contentType)
    {
        var extension = Path.GetExtension(finalUri.AbsolutePath).ToLowerInvariant();
        if (MediaLibraryCommands.SupportedFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return extension;
        if (MediaLibraryCommands.TryGetMimeType(contentType, out var mappedExtension, out _))
            return "." + mappedExtension;
        throw new InvalidDataException(
            $"Generated asset type is unsupported (extension '{extension}', Content-Type '{contentType ?? "unknown"}').");
    }
}
