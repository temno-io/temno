using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Temno.Core.Account;
using Temno.Core.Generation;

namespace Temno.Generation;

public sealed record HostedUploadPart(int Number, long Bytes, string? ETag, string? Url);
public sealed record HostedUploadSession(string Protocol, Guid Id, long Bytes, string Sha256, string ContentType,
    int PartSize, string ExpiresAt, HostedUploadPart[] Parts, JsonElement? Asset = null);

internal sealed class HostedFileUploader(
    Func<HttpMethod, string, HttpContent?, Guid?, CancellationToken, Task<HttpResponseMessage>> control,
    HttpClient transfer,
    Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxControlBytes = 128 * 1024;

    public async Task<string> Upload(Stream source, string contentType, Guid key, string prefix, long maximum,
        long legacyMaximum, CancellationToken ct, string? expectedSha256 = null)
    {
        if (!source.CanRead || !source.CanSeek || source.Length is < 12 || source.Length > maximum)
            throw new GenerationException("Reference media exceeds the upload limit.", code: "media_too_large");
        if (key == Guid.Empty) throw new GenerationException("An idempotency key is required.", code: "idempotency_key_required");
        source.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(source, ct).ConfigureAwait(false)).ToLowerInvariant();
        source.Position = 0;
        if (expectedSha256 is not null && hash != expectedSha256)
            throw new GenerationException("Reference media changed before upload. Quote it again.", code: "media_changed");
        if (transfer.DefaultRequestHeaders.Any()) throw new InvalidOperationException("The object-transfer client must not carry default credentials or headers.");
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var begin = await control(HttpMethod.Post, prefix + "/start",
                    Body(new { bytes = source.Length, sha256 = hash, contentType }), key, ct).ConfigureAwait(false);
                if (begin.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented)
                {
                    if (source.Length > legacyMaximum)
                        throw new GenerationException("This server does not support large direct uploads yet.", code: "direct_upload_unavailable");
                    source.Position = 0;
                    using var content = new StreamContent(new PartStream(source, source.Length));
                    content.Headers.ContentLength = source.Length;
                    content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                    using var legacy = await control(HttpMethod.Post, prefix, content, key, ct).ConfigureAwait(false);
                    return await ReadControl(legacy, ct).ConfigureAwait(false);
                }
                var session = JsonSerializer.Deserialize<HostedUploadSession>(await ReadControl(begin, ct).ConfigureAwait(false), Json)
                    ?? throw new GenerationException("Invalid upload session.", code: "invalid_response");
                if (session.Protocol != "r2-multipart-v1" || session.Id == Guid.Empty || session.Bytes != source.Length
                    || session.Sha256 != hash || session.ContentType != contentType
                    || session.PartSize is < 5 * 1024 * 1024 or > 32 * 1024 * 1024
                    || !DateTimeOffset.TryParse(session.ExpiresAt, out var expiry) || expiry <= DateTimeOffset.UtcNow)
                    throw new GenerationException("Invalid upload session.", code: "invalid_response");
                if (session.Asset is { ValueKind: JsonValueKind.Object } asset)
                    return JsonSerializer.Serialize(new { asset, expiresAt = session.ExpiresAt }, Json);
                var count = checked((int)((source.Length + session.PartSize - 1) / session.PartSize));
                if (session.Parts is null || session.Parts.Length != count || session.Parts.Select(p => p.Number).Distinct().Count() != count)
                    throw new GenerationException("Invalid upload part list.", code: "invalid_response");
                foreach (var part in session.Parts.OrderBy(p => p.Number))
                {
                    if (part.Number < 1 || part.Number > count) throw new GenerationException("Invalid part ordinal.", code: "invalid_response");
                    var offset = (part.Number - 1L) * session.PartSize;
                    var size = Math.Min(session.PartSize, source.Length - offset);
                    if (part.Bytes != size) throw new GenerationException("Invalid part size.", code: "invalid_response");
                    if (!string.IsNullOrEmpty(part.ETag)) continue;
                    if (!Uri.TryCreate(part.Url, UriKind.Absolute, out var url) || !url.IsDefaultPort || url.Fragment.Length != 0
                        || !System.Text.RegularExpressions.Regex.IsMatch(url.Host, "^[a-f0-9]{32}\\.r2\\.cloudflarestorage\\.com$")
                        || url.Query.Length == 0)
                        throw new GenerationException("Invalid direct upload destination.", code: "invalid_response");
                    await RemoteMediaDownloader.ValidatePublicHttpsUriAsync(url, resolver, ct).ConfigureAwait(false);
                    source.Position = offset;
                    using var request = new HttpRequestMessage(HttpMethod.Put, url)
                    {
                        Content = new StreamContent(new PartStream(source, size)),
                    };
                    request.Content.Headers.ContentLength = size;
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromMinutes(5));
                    using var uploaded = await transfer.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                    if (uploaded.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                        || (int)uploaded.StatusCode >= 500) throw new HttpRequestException("Direct upload must be resumed.");
                    if (!uploaded.IsSuccessStatusCode || uploaded.StatusCode == HttpStatusCode.PartialContent)
                        throw new GenerationException("Direct upload was refused.", code: "upload_failed");
                }
                using var complete = await control(HttpMethod.Post, prefix + "/" + session.Id.ToString("D") + "/complete", Body(new { }), null, ct).ConfigureAwait(false);
                return await ReadControl(complete, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (!ct.IsCancellationRequested && attempt < 3
                && (error is HttpRequestException or IOException or OperationCanceledException
                    || error is GenerationException { StatusCode: >= 500 } or GenerationException { Code: "upload_incomplete" or "upload_busy" }))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
        throw new GenerationException("Upload interrupted. Retry to resume its completed parts.", code: "upload_interrupted");
    }

    private static StringContent Body(object value) => new(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json");
    private static async Task<string> ReadControl(HttpResponseMessage response, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(6));
        ct = deadline.Token;
        using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var block = new byte[8192]; int count;
        while ((count = await input.ReadAsync(block, ct).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaxControlBytes) throw new GenerationException("Upload control response is too large.", code: "invalid_response");
            buffer.Write(block, 0, count);
        }
        var body = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        if (response.IsSuccessStatusCode) return body;
        HostedApiErrors.TryParse(body, out var code, out var message);
        throw new GenerationException(HostedApiErrors.Describe((int)response.StatusCode, code, message), (int)response.StatusCode, code);
    }

    private sealed class PartStream(Stream source, long length) : Stream
    {
        private readonly long _length = length;
        private long _remaining = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, (int)Math.Min(count, _remaining)); _remaining -= read; return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], ct).ConfigureAwait(false);
            _remaining -= read; return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        // StreamContent owns this slice, not the file reused by the next part.
    }
}
