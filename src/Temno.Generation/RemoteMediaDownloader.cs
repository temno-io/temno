using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Temno.Generation;

public sealed record RemoteDownloadResult(
    Uri FinalUri,
    string? ContentType,
    long BytesWritten,
    string? Sha256 = null);

/// <summary>Streams public HTTPS media with bounded size and validated redirects.</summary>
public static class RemoteMediaDownloader
{
    public const int MaxRedirects = 5;
    private const int MaxAttempts = 4;
    private sealed record Checkpoint(string Identity, string ETag, long? Length, string? ContentType);

    public static string SourceIdentity(Uri uri) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)))).ToLowerInvariant();

    public static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectCallback = ConnectPublicHostAsync,
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    public static async Task<RemoteDownloadResult> DownloadAsync(
        HttpClient httpClient,
        Uri sourceUri,
        string destinationPath,
        long maxBytes,
        CancellationToken cancellationToken = default,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostResolver = null,
        bool resume = false,
        string? expectedSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(sourceUri);
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("Download destination is required.", nameof(destinationPath));
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        if (expectedSha256 is not null && !System.Text.RegularExpressions.Regex.IsMatch(expectedSha256, "^[a-f0-9]{64}$"))
            throw new ArgumentException("Expected SHA-256 must be lowercase hex.", nameof(expectedSha256));
        hostResolver ??= static (host, token) => Dns.GetHostAddressesAsync(host, token);
        await ValidatePublicHttpsUriAsync(sourceUri, hostResolver, cancellationToken).ConfigureAwait(false);
        var identity = SourceIdentity(sourceUri);
        var checkpointPath = destinationPath + ".resume.json";
        Checkpoint? checkpoint = null;
        var existed = File.Exists(destinationPath);
        if (existed)
        {
            if (!resume) throw new IOException("Download destination already exists.");
            if ((File.GetAttributes(destinationPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Download checkpoint must be a regular file.");
            checkpoint = ReadCheckpoint(checkpointPath);
            if (checkpoint is null || checkpoint.Identity != identity || new FileInfo(destinationPath).Length > maxBytes)
                throw new InvalidDataException("Download checkpoint does not match this object.");
        }
        var ownsDestination = false;
        var preserve = false;
        var completed = false;
        var current = sourceUri;
        try
        {
            using var output = new FileStream(destinationPath, existed ? FileMode.Open : FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            ownsDestination = true;
            output.Position = output.Length;
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    deadline.CancelAfter(httpClient.Timeout == Timeout.InfiniteTimeSpan ? TimeSpan.FromMinutes(15) : httpClient.Timeout);
                    var transferToken = deadline.Token;
                    if (checkpoint is null) { output.SetLength(0); output.Position = 0; }
                    if (checkpoint?.Length is > 0 && output.Length == checkpoint.Length)
                    {
                        output.SetLength(output.Length - 1);
                        output.Position = output.Length;
                    }
                    var offset = output.Length;
                    HttpResponseMessage? response = null;
                    try
                    {
                        current = sourceUri;
                        for (var redirect = 0; redirect <= MaxRedirects; redirect++)
                        {
                            await ValidatePublicHttpsUriAsync(current, hostResolver, transferToken).ConfigureAwait(false);
                            using var request = new HttpRequestMessage(HttpMethod.Get, current);
                            request.Headers.AcceptEncoding.ParseAdd("identity");
                            if (offset > 0 && checkpoint is not null)
                            {
                                request.Headers.Range = new RangeHeaderValue(offset, null);
                                request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(checkpoint.ETag));
                            }
                            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, transferToken).ConfigureAwait(false);
                            var applied = response.RequestMessage?.RequestUri ?? current;
                            await ValidatePublicHttpsUriAsync(applied, hostResolver, transferToken).ConfigureAwait(false);
                            current = applied;
                            if (!IsRedirect(response.StatusCode)) break;
                            if (redirect == MaxRedirects) throw new InvalidOperationException("Remote media redirect limit exceeded.");
                            var location = response.Headers.Location ?? throw new InvalidOperationException("Missing redirect location.");
                            current = location.IsAbsoluteUri ? location : new Uri(current, location);
                            response.Dispose(); response = null;
                        }
                        response!.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentEncoding.Count != 0)
                            throw new InvalidDataException("Encoded responses cannot be resumed safely.");
                        var etag = response.Headers.ETag is { IsWeak: false } tag && tag.ToString().Length <= 512 ? tag.ToString() : null;
                        var declared = response.Content.Headers.ContentLength;
                        long? total;
                        if (response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            var range = response.Content.Headers.ContentRange;
                            if (offset == 0 || checkpoint is null || etag != checkpoint.ETag || range is null || range.Unit != "bytes"
                                || range.From != offset || range.Length is not > 0 || range.To != range.Length - 1
                                || (declared.HasValue && declared != range.Length - offset)
                                || (checkpoint.Length.HasValue && checkpoint.Length != range.Length))
                                throw new InvalidDataException("Remote media returned an inconsistent range.");
                            total = range.Length;
                        }
                        else if (response.StatusCode == HttpStatusCode.OK)
                        {
                            // If-Range returned the new complete entity (or the server ignores Range).
                            output.SetLength(0); output.Position = 0; offset = 0;
                            total = declared;
                        }
                        else throw new InvalidDataException("Unexpected download response.");
                        if (total > maxBytes) throw new InvalidDataException("Remote media exceeds the download byte limit.");
                        var contentType = response.Content.Headers.ContentType?.MediaType;
                        checkpoint = etag is null ? null : new Checkpoint(identity, etag, total, contentType);
                        if (resume && checkpoint is not null)
                            await SaveCheckpoint(checkpointPath, checkpoint, transferToken).ConfigureAwait(false);
                        using var input = await response.Content.ReadAsStreamAsync(transferToken).ConfigureAwait(false);
                        var buffer = ArrayPool<byte>.Shared.Rent(65536);
                        try
                        {
                            int read;
                            while ((read = await input.ReadAsync(buffer.AsMemory(0, 65536), transferToken).ConfigureAwait(false)) > 0)
                            {
                                if (output.Length + read > maxBytes || (total.HasValue && output.Length + read > total))
                                    throw new InvalidDataException("Remote media exceeds its declared byte limit.");
                                await output.WriteAsync(buffer.AsMemory(0, read), transferToken).ConfigureAwait(false);
                            }
                            if (total.HasValue && output.Length != total) throw new IOException("Remote media connection ended early.");
                            await output.FlushAsync(transferToken).ConfigureAwait(false);
                        }
                        finally { ArrayPool<byte>.Shared.Return(buffer); }
                        output.Position = 0;
                        var hash = Convert.ToHexString(await SHA256.HashDataAsync(output, transferToken).ConfigureAwait(false)).ToLowerInvariant();
                        if (expectedSha256 is not null && hash != expectedSha256) throw new InvalidDataException("Downloaded media hash does not match.");
                        TryDelete(checkpointPath);
                        completed = true;
                        return new RemoteDownloadResult(current, contentType, output.Length, hash);
                    }
                    finally { response?.Dispose(); }
                }
                catch (Exception error) when (Retryable(error, cancellationToken) && attempt + 1 < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                }
            }
            throw new IOException("Download retry limit exceeded.");
        }
        catch (Exception error) when (resume && checkpoint is not null
            && (Retryable(error, cancellationToken) || error is OperationCanceledException
                || error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized }))
        {
            preserve = true;
            throw;
        }
        finally
        {
            if (ownsDestination && !completed && !preserve)
            {
                TryDelete(destinationPath); TryDelete(checkpointPath);
            }
        }
    }

    private static bool Retryable(Exception error, CancellationToken ct) => !ct.IsCancellationRequested
        && (error is IOException || error is OperationCanceledException
            || error is HttpRequestException http && (http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                || (int)http.StatusCode.GetValueOrDefault() >= 500));

    private static Checkpoint? ReadCheckpoint(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 4096 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return null;
            var value = JsonSerializer.Deserialize<Checkpoint>(File.ReadAllText(path));
            return value is not null && value.ETag is { Length: > 0 and <= 512 } && value.Length is null or >= 0
                && EntityTagHeaderValue.TryParse(value.ETag, out var etag) && !etag.IsWeak ? value : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private static async Task SaveCheckpoint(string path, Checkpoint state, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(state), ct).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    public static async Task ValidatePublicHttpsUriAsync(
        Uri uri,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostResolver = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Remote media URL must be absolute and use HTTPS.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Remote media URL must not embed credentials.");
        if (string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidOperationException("Remote media URL must include a host.");

        hostResolver ??= static (host, token) => Dns.GetHostAddressesAsync(host, token);
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host, out var literal)
                ? [literal]
                : await hostResolver(uri.IdnHost, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException)
        {
            throw new InvalidOperationException($"Remote media host could not be resolved: {uri.Host}", ex);
        }

        ValidateResolvedAddresses(addresses);
    }

    private static async ValueTask<Stream> ConnectPublicHostAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(
            context.DnsEndPoint.Host,
            cancellationToken).ConfigureAwait(false);
        ValidateResolvedAddresses(addresses);

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                if (ex is OperationCanceledException)
                    throw;
                lastError = ex;
            }
        }

        throw new IOException($"Could not connect to remote media host {context.DnsEndPoint.Host}.", lastError);
    }

    private static void ValidateResolvedAddresses(IPAddress[] addresses)
    {
        if (addresses.Length == 0 || addresses.Any(IsPrivateOrReserved))
            throw new InvalidOperationException("Remote media URL must resolve only to public network addresses.");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;

    private static bool IsPrivateOrReserved(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6None))
            return true;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 0
                || bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 169 && bytes[1] == 254)
                || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                || (bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99)
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                || (bytes[0] == 198 && bytes[1] is 18 or 19)
                || (bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                || (bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                || bytes[0] >= 224;
        }

        return address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast
            || (bytes[0] & 0xFE) == 0xFC
            || (bytes[0] & 0xE0) != 0x20
            || (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8);
    }
}
