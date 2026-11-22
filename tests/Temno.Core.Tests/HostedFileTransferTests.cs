using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Generation;
using Xunit;

namespace Temno.Core.Tests;

[CollectionDefinition(nameof(HostedTransferCollection), DisableParallelization = true)]
public sealed class HostedTransferCollection;

[Collection(nameof(HostedTransferCollection))]
public sealed class HostedFileTransferTests
{
    private const int PartSize = 5 * 1024 * 1024;
    private const string Host = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.r2.cloudflarestorage.com";
    private static Task<IPAddress[]> Resolve(string _, CancellationToken ct) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private static HttpResponseMessage Reply(object value, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    { Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))) };
    private static async Task<string> FileOfSize(int length)
    {
        var path = Path.Combine(Path.GetTempPath(), "temno-upload-" + Guid.NewGuid().ToString("N"));
        using var file = File.Create(path); var block = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
        while (length > 0) { var count = Math.Min(length, block.Length); await file.WriteAsync(block.AsMemory(0, count)); length -= count; }
        return path;
    }

    [Theory]
    [InlineData("drop")]
    [InlineData("expired-link")]
    [InlineData("completion-reply-lost")]
    public async Task DirectUploadResumesAtHalfWithoutReuploadingFirstPartOrSendingBearerToR2(string fault)
    {
        var path = await FileOfSize(2 * PartSize); var id = Guid.NewGuid(); var requestKey = Guid.NewGuid();
        var accepted = new HashSet<int>(); var transfers = new List<int>(); var starts = 0; var completions = 0; var failed = false;
        var expiry = DateTimeOffset.UtcNow.AddHours(24).ToString("O"); var hashes = new Dictionary<int, string>();
        string hash; using (var file = File.OpenRead(path)) hash = Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
        var asset = new { id, sha256 = hash, bytes = 2 * PartSize, kind = "video", contentType = "video/mp4", width = 32, height = 32, durationSeconds = 2, fps = 30 };
        using var control = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("app-session-token", request.Headers.Authorization.Parameter);
            Assert.Equal("gateway.example.test", request.RequestUri!.Host);
            Assert.True((await request.Content!.ReadAsByteArrayAsync(ct)).Length < 1024);
            if (request.RequestUri.AbsolutePath.EndsWith("/start"))
            {
                starts++; Assert.Equal(requestKey.ToString(), request.Headers.GetValues("Idempotency-Key").Single());
                return Reply(new { protocol = "r2-multipart-v1", id, bytes = 2 * PartSize, sha256 = hash, contentType = "video/mp4", partSize = PartSize,
                    expiresAt = expiry, parts = Enumerable.Range(1, 2).Select(n => new { number = n, bytes = PartSize, eTag = accepted.Contains(n) ? "confirmed" : null,
                        url = accepted.Contains(n) ? null : $"https://{Host}/test-dev/key.mp4?partNumber={n}&signature=fake" }) });
            }
            Assert.EndsWith($"/{id}/complete", request.RequestUri.AbsolutePath); completions++;
            Assert.Equal(2, accepted.Count);
            if (fault == "completion-reply-lost" && completions == 1) throw new HttpRequestException("reply lost");
            return Reply(new { asset, expiresAt = expiry });
        }));
        using var transfer = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method); Assert.Equal(Host, request.RequestUri!.Host);
            Assert.Null(request.Headers.Authorization); Assert.False(request.Headers.Contains("X-Temno-Environment"));
            Assert.False(request.Headers.Contains("Idempotency-Key")); Assert.Equal(PartSize, request.Content!.Headers.ContentLength);
            var ordinal = request.RequestUri.Query.Contains("partNumber=1") ? 1 : 2; transfers.Add(ordinal);
            if (ordinal == 2 && !failed && fault != "completion-reply-lost")
            {
                failed = true;
                if (fault == "expired-link") return Reply(new { }, HttpStatusCode.Forbidden);
                throw new IOException("connection dropped at 50 percent");
            }
            using var body = await request.Content.ReadAsStreamAsync(ct);
            hashes[ordinal] = Convert.ToHexString(await SHA256.HashDataAsync(body, ct)); accepted.Add(ordinal);
            return Reply(new { });
        }));
        try
        {
            var client = new HostedMediaClient("https://gateway.example.test", () => "app-session-token", control, transfer, Resolve);
            var result = await client.UploadFileAsync(path, "video/mp4", requestKey, expectedSha256: hash);
            Assert.Equal(id, result.Asset.Id); Assert.Equal(2, starts); Assert.Equal(1, transfers.Count(p => p == 1));
            Assert.Equal(hashes[1], hashes[2]); Assert.Equal(hash, result.Asset.Sha256);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("http://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.r2.cloudflarestorage.com/b/key?x=1")]
    [InlineData("https://attacker.example.test/b/key?x=1")]
    [InlineData("https://user:secret@aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.r2.cloudflarestorage.com/b/key?x=1")]
    public async Task InvalidDestinationIsRefusedBeforeTransfer(string url)
    {
        var path = await FileOfSize(20); var id = Guid.NewGuid(); var transfers = 0;
        using var control = new HttpClient(new Handler(async (r, ct) =>
        {
            var start = JsonNode.Parse(await r.Content!.ReadAsStringAsync(ct))!;
            return Reply(new { protocol = "r2-multipart-v1", id, bytes = 20, sha256 = start["sha256"]!.GetValue<string>(), contentType = "image/png", partSize = PartSize,
                expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O"), parts = new[] { new { number = 1, bytes = 20, url } } });
        }));
        using var transfer = new HttpClient(new Handler((_, _) => { transfers++; return Task.FromResult(Reply(new { })); }));
        try
        {
            var client = new HostedImageClient("https://gateway.example.test", () => "token", control, transfer, Resolve);
            await Assert.ThrowsAnyAsync<Exception>(() => client.UploadFileAsync(path, "image/png", Guid.NewGuid()));
            Assert.Equal(0, transfers);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OldGatewayFallbackStreamsOnlyTheOriginalSmallUploadRoute()
    {
        var path = await FileOfSize(20); var seen = new List<string>(); var id = Guid.NewGuid();
        using var control = new HttpClient(new Handler(async (r, ct) =>
        {
            seen.Add(r.RequestUri!.AbsolutePath);
            if (seen.Count == 1) return Reply(new { error = "not_found" }, HttpStatusCode.NotFound);
            Assert.Equal(20, (await r.Content!.ReadAsByteArrayAsync(ct)).Length);
            Assert.Equal("image/png", r.Content.Headers.ContentType!.MediaType);
            return Reply(new { asset = new { id, sha256 = "abc", bytes = 20, width = 2, height = 2, contentType = "image/png" }, expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O") });
        }));
        try
        {
            var result = await new HostedImageClient("https://gateway.example.test", () => "token", control).UploadFileAsync(path, "image/png", Guid.NewGuid());
            Assert.Equal(id, result.Asset.Id); Assert.Equal(["/v1/images/uploads/start", "/v1/images/uploads"], seen);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ChangedSourceRefusedBeforeAnyNetworkRequest()
    {
        var path = await FileOfSize(20); var calls = 0;
        using var control = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Reply(new { })); }));
        try
        {
            var error = await Assert.ThrowsAsync<GenerationException>(() => new HostedMediaClient("https://gateway.example.test", () => "token", control)
                .UploadFileAsync(path, "video/mp4", Guid.NewGuid(), expectedSha256: new string('f', 64)));
            Assert.Equal("media_changed", error.Code); Assert.Equal(0, calls);
        }
        finally { File.Delete(path); }
    }
}
