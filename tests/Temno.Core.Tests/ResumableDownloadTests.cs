using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Temno.Generation;
using Xunit;

namespace Temno.Core.Tests;

[Collection(nameof(HostedTransferCollection))]
public sealed class ResumableDownloadTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly Uri Source = new("https://objects.example.test/result.mp4?signature=temporary");
    private static Task<IPAddress[]> Resolve(string _, CancellationToken ct) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(handle(request));
    }
    private sealed class DroppedStream(byte[] data, int stop) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (Position >= stop) throw new IOException("simulated connection loss");
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, stop - (int)Position)], ct);
        }
    }
    private static HttpResponseMessage Reply(byte[] bytes, long offset = 0, string tag = "\"version-1\"", Stream? stream = null)
    {
        var r = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            Content = stream is null ? new ByteArrayContent(bytes[(int)offset..]) : new StreamContent(stream),
        };
        r.Headers.ETag = new EntityTagHeaderValue(tag);
        r.Content.Headers.ContentLength = bytes.Length - offset;
        r.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        if (offset > 0) r.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, bytes.Length - 1, bytes.Length);
        return r;
    }
    private static string Temp() => Path.Combine(Path.GetTempPath(), "temno-resume-" + Guid.NewGuid().ToString("N") + ".partial");
    private static void Clean(string path) { File.Delete(path); File.Delete(path + ".resume.json"); }

    private sealed class PatternStream(long offset, long length, long? dropAt) : Stream
    {
        private long _position = offset;
        private static readonly byte[] Pattern = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int start, int count) => Read(buffer.AsSpan(start, count));
        public override int Read(Span<byte> buffer)
        {
            if (dropAt is { } stop && _position >= stop) throw new IOException("1 GiB stream dropped at 512 MiB");
            var index = (int)(_position % Pattern.Length);
            var count = (int)Math.Min(Math.Min(buffer.Length, Pattern.Length - index), Math.Min(length, dropAt ?? length) - _position);
            Pattern.AsSpan(index, count).CopyTo(buffer); _position += count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task OneGiBDownloadDroppedAtHalfResumesAndMatchesIndependentSha256()
    {
        const long length = 1024L * 1024 * 1024, half = length / 2;
        // Python hashlib: sha256 of bytes(range(256))*4096, updated 1024 times.
        const string expected = "2c06ade942ee3f17a048dd1064b2fab046a4bb95386d8bb41b68dc6711ac2af3";
        var calls = 0; var path = Temp();
        using var http = new HttpClient(new Handler(request =>
        {
            var second = ++calls == 2;
            if (second) Assert.Equal(half, request.Headers.Range!.Ranges.Single().From);
            var response = new HttpResponseMessage(second ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            { Content = new StreamContent(new PatternStream(second ? half : 0, length, second ? null : half)) };
            response.Headers.ETag = new EntityTagHeaderValue("\"immutable-1gib\"");
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            response.Content.Headers.ContentLength = second ? half : length;
            if (second) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(half, length - 1, length);
            return response;
        }));
        try
        {
            var result = await RemoteMediaDownloader.DownloadAsync(http, Source, path, length, hostResolver: Resolve, resume: true, expectedSha256: expected);
            Assert.Equal(2, calls); Assert.Equal(length, result.BytesWritten); Assert.Equal(expected, result.Sha256);
            output.WriteLine($"bytes={result.BytesWritten}; drop={half}; resumed Range=bytes={half}-; requests={calls}; sha256={result.Sha256}");
        }
        finally { Clean(path); }
    }

    [Fact]
    public async Task DroppedBodyUsesRangeAndIfRangeAndVerifiesHash()
    {
        var bytes = Enumerable.Range(0, 200000).Select(i => (byte)(i % 251)).ToArray();
        var path = Temp(); var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            if (calls == 1) return Reply(bytes, stream: new DroppedStream(bytes, 100000));
            Assert.Equal(100000, request.Headers.Range!.Ranges.Single().From);
            Assert.Equal("\"version-1\"", request.Headers.IfRange!.EntityTag!.Tag);
            return Reply(bytes, 100000);
        }));
        try
        {
            var expected = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var result = await RemoteMediaDownloader.DownloadAsync(http, Source, path, bytes.Length, hostResolver: Resolve, expectedSha256: expected);
            Assert.Equal(2, calls); Assert.Equal(expected, result.Sha256);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { Clean(path); }
    }

    [Fact]
    public async Task ExhaustedRetryRetainsCheckpointWithoutSignedUrlAndNextCallResumes()
    {
        var bytes = new byte[100000]; Random.Shared.NextBytes(bytes); var path = Temp(); var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            if (calls == 1) return Reply(bytes, stream: new DroppedStream(bytes, 50000));
            if (calls <= 4) throw new HttpRequestException("offline");
            Assert.Equal(50000, request.Headers.Range!.Ranges.Single().From);
            return Reply(bytes, 50000);
        }));
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => RemoteMediaDownloader.DownloadAsync(http, Source, path, bytes.Length, hostResolver: Resolve, resume: true));
            Assert.Equal(50000, new FileInfo(path).Length);
            Assert.DoesNotContain("signature", await File.ReadAllTextAsync(path + ".resume.json"));
            var refreshed = new Uri("https://objects.example.test/result.mp4?signature=refreshed");
            var result = await RemoteMediaDownloader.DownloadAsync(http, refreshed, path, bytes.Length, hostResolver: Resolve, resume: true);
            Assert.Equal(bytes.Length, result.BytesWritten);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.False(File.Exists(path + ".resume.json"));
        }
        finally { Clean(path); }
    }

    [Fact]
    public async Task ChangedEntityReturning200ReplacesPrefixInsteadOfAppending()
    {
        byte[] old = [1, 2, 3, 4, 5, 6], fresh = [7, 8, 9]; var calls = 0; var path = Temp();
        using var http = new HttpClient(new Handler(_ => ++calls == 1 ? Reply(old, stream: new DroppedStream(old, 3)) : Reply(fresh, tag: "\"version-2\"")));
        try
        {
            var r = await RemoteMediaDownloader.DownloadAsync(http, Source, path, 20, hostResolver: Resolve);
            Assert.Equal(3, r.BytesWritten); Assert.Equal(fresh, await File.ReadAllBytesAsync(path));
        }
        finally { Clean(path); }
    }

    [Theory]
    [InlineData("etag")]
    [InlineData("offset")]
    [InlineData("total")]
    public async Task InconsistentRangeIsRefusedAndPartialRemoved(string fault)
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6]; var calls = 0; var path = Temp();
        using var http = new HttpClient(new Handler(_ =>
        {
            if (++calls == 1) return Reply(bytes, stream: new DroppedStream(bytes, 3));
            var r = Reply(bytes, 3, fault == "etag" ? "\"changed\"" : "\"version-1\"");
            if (fault == "offset") r.Content.Headers.ContentRange = new ContentRangeHeaderValue(2, 5, 6);
            if (fault == "total") r.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 6, 7);
            return r;
        }));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => RemoteMediaDownloader.DownloadAsync(http, Source, path, 20, hostResolver: Resolve, resume: true));
            Assert.False(File.Exists(path)); Assert.False(File.Exists(path + ".resume.json"));
        }
        finally { Clean(path); }
    }

    [Fact]
    public async Task ChecksumMismatchAndOversizeArePermanentAndDoNotKeepPartial()
    {
        var path = Temp(); var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return Reply([1, 2, 3]); }));
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => RemoteMediaDownloader.DownloadAsync(http, Source, path, 20, hostResolver: Resolve, resume: true, expectedSha256: new string('a', 64)));
            Assert.False(File.Exists(path)); Assert.Equal(1, calls);
            await Assert.ThrowsAsync<InvalidDataException>(() => RemoteMediaDownloader.DownloadAsync(http, Source, path, 2, hostResolver: Resolve, resume: true));
            Assert.False(File.Exists(path)); Assert.Equal(2, calls);
        }
        finally { Clean(path); }
    }

    [Fact]
    public async Task WeakValidatorRestartsInsteadOfJoiningUnverifiedBytes()
    {
        byte[] bytes = [1, 2, 3, 4, 5, 6]; var calls = 0; var path = Temp();
        using var http = new HttpClient(new Handler(request =>
        {
            if (++calls == 1)
            {
                var first = Reply(bytes, stream: new DroppedStream(bytes, 3));
                first.Headers.ETag = new EntityTagHeaderValue("\"weak\"", true); return first;
            }
            Assert.Null(request.Headers.Range); return Reply(bytes);
        }));
        try { await RemoteMediaDownloader.DownloadAsync(http, Source, path, 20, hostResolver: Resolve); Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); }
        finally { Clean(path); }
    }
}
