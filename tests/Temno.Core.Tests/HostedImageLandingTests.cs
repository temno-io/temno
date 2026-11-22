using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Temno.Generation;
using Xunit;

namespace Temno.Core.Tests;

public class HostedImageLandingTests
{
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public readonly List<HttpRequestMessage> Requests = [];
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request));
        }
    }

    private static readonly Func<string, CancellationToken, Task<System.Net.IPAddress[]>> Resolver =
        (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("93.184.216.34") });

    private static HttpResponseMessage PngResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return response;
    }

    private static string CreatePackage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "temno-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "media"));
        return dir;
    }

    [Fact]
    public async Task DownloadAndLandAssets_LandsEveryOutputWithUniqueNames()
    {
        var package = CreatePackage();
        try
        {
            var handler = new StubHandler(request => PngResponse(
                request.RequestUri!.AbsolutePath.Contains('1') ? [1, 1] : [2, 2, 2]));
            var service = new GenerationService(new FakeGenerationClient(), new HttpClient(handler), hostResolver: Resolver);
            var landed = await service.DownloadAndLandAssetsAsync(
                ["https://cdn.example.test/out1.png", "https://cdn.example.test/out2.png"],
                package,
                "ABCDEF12");

            Assert.Equal(2, landed.Count);
            Assert.NotEqual(landed[0], landed[1]);
            Assert.Equal("gen-ABCDEF12.png", Path.GetFileName(landed[0]));
            Assert.Equal("gen-ABCDEF12-2.png", Path.GetFileName(landed[1]));
            Assert.Equal([1, 1], await File.ReadAllBytesAsync(landed[0]));
            Assert.Equal([2, 2, 2], await File.ReadAllBytesAsync(landed[1]));
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAndLandAssets_RetryKeepsIdentity()
    {
        var package = CreatePackage();
        try
        {
            var handler = new StubHandler(_ => PngResponse([7, 7, 7]));
            var service = new GenerationService(new FakeGenerationClient(), new HttpClient(handler), hostResolver: Resolver);
            var first = await service.DownloadAndLandAssetsAsync(
                ["https://cdn.example.test/a.png", "https://cdn.example.test/b.png"], package, "ABCDEF12");
            var second = await service.DownloadAndLandAssetsAsync(
                ["https://cdn.example.test/a.png", "https://cdn.example.test/b.png"], package, "ABCDEF12");

            Assert.Equal(first, second);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(package, "media")).Length);
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAndLandAssets_NeverOverwritesDifferentBytes()
    {
        var package = CreatePackage();
        try
        {
            var original = Path.Combine(package, "media", "gen-ABCDEF12.png");
            await File.WriteAllBytesAsync(original, [9, 9, 9]);
            var handler = new StubHandler(_ => PngResponse([1, 2, 3, 4]));
            var service = new GenerationService(new FakeGenerationClient(), new HttpClient(handler), hostResolver: Resolver);

            var landed = await service.DownloadAndLandAssetsAsync(
                ["https://cdn.example.test/out.png"], package, "ABCDEF12");

            Assert.Equal([9, 9, 9], await File.ReadAllBytesAsync(original));
            Assert.NotEqual(original, landed[0]);
            Assert.StartsWith("gen-ABCDEF12-r", Path.GetFileNameWithoutExtension(landed[0]));
            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(landed[0]));
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task PollImageJob_PollsToSuccessWithoutResubmitting()
    {
        var jobId = Guid.NewGuid().ToString("D");
        var polls = 0;
        var handler = new StubHandler(request =>
        {
            polls++;
            var status = polls < 3 ? "running" : "succeeded";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"id":"{{jobId}}","model":"muse-image","status":"{{status}}","costCredits":8,"resultUrls":["https://cdn.example.test/a.png"]}"""),
            };
        });
        var service = new GenerationService(new FakeGenerationClient(), new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound))), hostResolver: Resolver);
        var client = new HostedImageClient("https://gateway.example.test", () => "t", new HttpClient(handler));

        var job = await service.PollImageJobAsync(client, jobId, TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(10));

        Assert.Equal("succeeded", job.Status);
        Assert.Equal(3, polls);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal($"/v1/images/jobs/{jobId}", r.RequestUri!.AbsolutePath);
        });
    }

    [Fact]
    public async Task PollImageJob_FailedMapsCodeAndTimeoutNeverResubmits()
    {
        var failHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"j","model":"m","status":"failed","error":"provider_error"}"""),
        });
        var service = new GenerationService(new FakeGenerationClient(), new HttpClient(failHandler), hostResolver: Resolver);
        var failClient = new HostedImageClient("https://gateway.example.test", () => "t", new HttpClient(failHandler));
        var failed = await Assert.ThrowsAsync<GenerationException>(() =>
            service.PollImageJobAsync(failClient, "j", TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(5)));
        Assert.Equal("provider_error", failed.Code);

        var slowHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"j","model":"m","status":"running","resultUrls":[]}"""),
        });
        var slowClient = new HostedImageClient("https://gateway.example.test", () => "t", new HttpClient(slowHandler));
        await Assert.ThrowsAsync<TimeoutException>(() =>
            service.PollImageJobAsync(slowClient, "j", TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(50)));
        Assert.NotEmpty(slowHandler.Requests);
        Assert.All(slowHandler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }
}
