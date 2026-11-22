using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Temno.Generation;
using Xunit;

namespace Temno.Core.Tests;

public class GenerationTests
{
    private static readonly Func<string, CancellationToken, Task<IPAddress[]>> PublicResolver =
        (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    [Fact]
    public void GenerationHttpErrors_Maps401402AndConvexCodes()
    {
        var unauthorized = GenerationHttpErrors.FromHttp(401, """{"error":{"code":"unauthenticated","message":"Sign in"}}""");
        Assert.Equal("Sign in", unauthorized.Message);
        Assert.Equal(401, unauthorized.StatusCode);

        var payment = GenerationHttpErrors.FromHttp(402, "");
        Assert.Contains("402", payment.Message);

        Assert.Equal("Insufficient credits. Open Account and choose a plan.", GenerationHttpErrors.Describe(null, "insufficient_credits", null));
        Assert.Equal("Subscription required.", GenerationHttpErrors.Describe(null, "subscription_required", null));
        Assert.Equal("Plan required.", GenerationHttpErrors.Describe(null, "plan_required", null));
        Assert.Equal("Rate limited. Try again later.", GenerationHttpErrors.Describe(null, "rate_limited", null));

        var ex = Assert.Throws<GenerationException>(() =>
            GenerationHttpErrors.ThrowIfErrorPayload("""{"code":"insufficient_credits","message":"Out of credits"}"""));
        Assert.Equal("Out of credits", ex.Message);
        Assert.Equal("insufficient_credits", ex.Code);
    }

    [Fact]
    public async Task HttpGenerationClient_Submit401_ThrowsUserVisibleError()
    {
        var client = new HttpGenerationClient(
            "https://api.example.test",
            "bad-key",
            new HttpClient(new MockHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"error":{"code":"unauthenticated","message":"Bad key"}}"""),
                }))));

        var error = await Assert.ThrowsAsync<GenerationException>(() =>
            client.SubmitGenerationAsync("video-gen-v1", new VideoGenerationParams { Prompt = "x" }));
        Assert.Equal("Bad key", error.Message);
        Assert.Equal(401, error.StatusCode);
    }

    [Fact]
    public async Task PollJobAsync_CancelToken_StopsPolling()
    {
        var client = new FakeGenerationClient();
        var service = new GenerationService(client);
        var jobId = await client.SubmitGenerationAsync("video-gen-v1", new VideoGenerationParams { Prompt = "x" });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PollJobAsync(jobId, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), cts.Token));
    }

    [Fact]
    public async Task FakeGenerationClient_ShouldSimulateStateTransitions()
    {
        var client = new FakeGenerationClient();
        
        // 1. List models
        var models = await client.ListModelsAsync();
        Assert.NotEmpty(models);
        Assert.Equal("video-gen-v1", models[0].Id);

        // 2. Submit job
        var jobId = await client.SubmitGenerationAsync("video-gen-v1", new VideoGenerationParams
        {
            Prompt = "A futuristic city",
            Duration = 5,
            AspectRatio = "16:9"
        });
        Assert.StartsWith("job-", jobId);

        // 3. First status query -> Queued
        var status1 = await client.GetJobStatusAsync(jobId);
        Assert.NotNull(status1);
        Assert.Equal(BackendGenerationStatus.Queued, status1.Status);

        // 4. Second status query -> Running
        var status2 = await client.GetJobStatusAsync(jobId);
        Assert.NotNull(status2);
        Assert.Equal(BackendGenerationStatus.Running, status2.Status);

        // 5. Third status query -> Succeeded
        var status3 = await client.GetJobStatusAsync(jobId);
        Assert.NotNull(status3);
        Assert.Equal(BackendGenerationStatus.Succeeded, status3.Status);
        Assert.NotNull(status3.ResultUrls);
        Assert.NotEmpty(status3.ResultUrls);
        Assert.Equal("https://fake.temno.io/assets/output.mp4", status3.ResultUrls[0]);
    }

    [Fact]
    public async Task GenerationService_PollJobAsync_ShouldSucceed()
    {
        var client = new FakeGenerationClient();
        var service = new GenerationService(client);

        var jobId = await client.SubmitGenerationAsync("video-gen-v1", new VideoGenerationParams
        {
            Prompt = "Golden sunset",
            Duration = 3,
            AspectRatio = "9:16"
        });

        // The service should poll until Succeeded
        var completedJob = await service.PollJobAsync(jobId, TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(5));
        
        Assert.Equal(BackendGenerationStatus.Succeeded, completedJob.Status);
        Assert.NotNull(completedJob.ResultUrls);
        Assert.Equal("https://fake.temno.io/assets/output.mp4", completedJob.ResultUrls[0]);
    }

    [Fact]
    public async Task HttpGenerationClient_WithMockHandler_ShouldParseRestResponses()
    {
        var mockHandler = new MockHttpMessageHandler(async (request, cancellationToken) =>
        {
            await Task.CompletedTask;
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.EndsWith("/api/models"))
            {
                var models = new List<CatalogEntry>
                {
                    new CatalogEntry { Id = "test-model", DisplayName = "Test Model", Kind = GenerationKind.Video }
                };
                var json = JsonSerializer.Serialize(models);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/api/generations/submit"))
            {
                var response = new { jobId = "mocked-job-id" };
                var json = JsonSerializer.Serialize(response);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath.Contains("/api/generations/status/"))
            {
                var job = new BackendGenerationJob
                {
                    Id = "mocked-job-id",
                    Status = BackendGenerationStatus.Succeeded,
                    ResultUrls = new List<string> { "https://example.com/output.mp4" }
                };
                var json = JsonSerializer.Serialize(job);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var httpClient = new HttpClient(mockHandler);
        var client = new HttpGenerationClient("https://api.palmier-rest.io", null, httpClient);

        // 1. Test ListModels
        var models = await client.ListModelsAsync();
        Assert.Single(models);
        Assert.Equal("test-model", models[0].Id);

        // 2. Test Submit
        var jobId = await client.SubmitGenerationAsync("test-model", new VideoGenerationParams { Prompt = "Test REST" });
        Assert.Equal("mocked-job-id", jobId);

        // 3. Test Status
        var jobStatus = await client.GetJobStatusAsync(jobId);
        Assert.NotNull(jobStatus);
        Assert.Equal(BackendGenerationStatus.Succeeded, jobStatus.Status);
        Assert.Equal("https://example.com/output.mp4", jobStatus.ResultUrls?[0]);
    }

    [Fact]
    public async Task HttpGenerationClient_ConvexLookingUrl_GetsPlainRestPaths()
    {
        var requests = new List<string>();
        var client = new HttpGenerationClient(
            "https://test.convex.site/",
            null,
            new HttpClient(new MockHttpMessageHandler((request, _) =>
            {
                requests.Add($"{request.Method} {request.RequestUri}");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        request.Method == HttpMethod.Get ? "[]" : """{"jobId":"rest-job"}""",
                        System.Text.Encoding.UTF8,
                        "application/json"),
                });
            })));

        Assert.Empty(await client.ListModelsAsync());
        Assert.Equal("rest-job", await client.SubmitGenerationAsync("m", new ImageGenerationParams { Prompt = "x" }));
        Assert.Equal(
            new[] { "GET https://test.convex.site/api/models", "POST https://test.convex.site/api/generations/submit" },
            requests);
    }

    [Fact]
    public async Task RemoteMediaDownloader_RedirectToPrivateAddress_IsRejectedBeforeRequest()
    {
        var requests = 0;
        using var client = new HttpClient(new MockHttpMessageHandler((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers = { Location = new Uri("https://127.0.0.1/internal.mp4") },
            });
        }));
        var destination = Path.Combine(Path.GetTempPath(), $"temno-redirect-{Guid.NewGuid():N}.partial");

        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                RemoteMediaDownloader.DownloadAsync(
                    client,
                    new Uri("https://example.com/output.mp4"),
                    destination,
                    1024,
                    hostResolver: PublicResolver));

            Assert.Contains("public network", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, requests);
            Assert.False(File.Exists(destination));
        }
        finally
        {
            if (File.Exists(destination)) File.Delete(destination);
        }
    }

    [Fact]
    public async Task GenerationService_OversizeAsset_PreservesExistingFileAndCleansPartial()
    {
        var packageDirectory = CreateTemporaryDirectory();
        try
        {
            var mediaDirectory = Path.Combine(packageDirectory, "media");
            Directory.CreateDirectory(mediaDirectory);
            var destination = Path.Combine(mediaDirectory, "gen-asset123.mp4");
            await File.WriteAllBytesAsync(destination, [9, 9, 9]);
            using var client = new HttpClient(new MockHttpMessageHandler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3, 4, 5]),
                })));
            var service = new GenerationService(
                new FakeGenerationClient(),
                client,
                maxDownloadBytes: 4,
                hostResolver: PublicResolver);

            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadAndLandAssetAsync(
                "https://example.com/output.mp4",
                packageDirectory,
                "asset12345"));

            Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(destination));
            Assert.Empty(Directory.GetFiles(mediaDirectory, "*.partial"));
        }
        finally
        {
            Directory.Delete(packageDirectory, recursive: true);
        }
    }

    [Fact]
    // WP-232 changed this contract: landing must never overwrite different bytes at a
    // deterministic name (that could be an original). Identical-byte retries keep identity.
    public async Task GenerationService_ValidAsset_PreservesDifferentBytesWithUniqueName()
    {
        var packageDirectory = CreateTemporaryDirectory();
        try
        {
            var mediaDirectory = Path.Combine(packageDirectory, "media");
            Directory.CreateDirectory(mediaDirectory);
            var destination = Path.Combine(mediaDirectory, "gen-asset123.mp4");
            await File.WriteAllBytesAsync(destination, [9, 9, 9]);
            using var client = new HttpClient(new MockHttpMessageHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3, 4]),
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
                return Task.FromResult(response);
            }));
            var service = new GenerationService(
                new FakeGenerationClient(),
                client,
                maxDownloadBytes: 1024,
                hostResolver: PublicResolver);

            var landed = await service.DownloadAndLandAssetAsync(
                "https://example.com/output.mp4",
                packageDirectory,
                "asset12345");

            Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(destination));
            Assert.NotEqual(destination, landed);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(landed));
            Assert.Empty(Directory.GetFiles(mediaDirectory, "*.partial"));
        }
        finally
        {
            Directory.Delete(packageDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RemoteMediaDownloader_ExistingDestination_IsNeverDeleted()
    {
        var destination = Path.Combine(Path.GetTempPath(), $"temno-existing-{Guid.NewGuid():N}.partial");
        await File.WriteAllBytesAsync(destination, [9, 9, 9]);
        using var client = new HttpClient(new MockHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]),
            })));

        try
        {
            await Assert.ThrowsAsync<IOException>(() => RemoteMediaDownloader.DownloadAsync(
                client,
                new Uri("https://example.com/output.mp4"),
                destination,
                1024,
                hostResolver: PublicResolver));
            Assert.Equal(new byte[] { 9, 9, 9 }, await File.ReadAllBytesAsync(destination));
        }
        finally
        {
            File.Delete(destination);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "temno-generation-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
