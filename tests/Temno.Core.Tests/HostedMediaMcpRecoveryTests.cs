using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.Account;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Generation;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

// WP-242 recovery: cancel/refund routing (submission vs job cancel), retry and
// reopen recovery (by job id, by result URLs, ambiguous submit key reuse),
// stable sibling ids across duplicates, and image-path isolation.

[Collection("EnvRegistry")]
public sealed class HostedMediaMcpRecoveryTests
{
    private const string CatalogJson = """
        {"version":"media-v1","developmentPrices":true,"configured":true,"acceptJobs":true,"qualificationOwner":true,"enabledOperations":[],
        "models":[
        {"id":"vid-1","displayName":"Vid One","family":"Vid","category":"video","route":"fal",
        "operations":[{"operation":"text_to_video","available":true,"inputs":[],"maxOutputs":2,"pricingUnit":"per_second"}],
        "settings":[],"outputKinds":["video"]}
        ]}
        """;

    private sealed class MediaHandler : HttpMessageHandler
    {
        public string Quote = Guid.NewGuid().ToString("D");
        public int Creates;
        public string? SubmitKey;
        public string? CanceledSubmission;
        public string? CanceledJob;
        public readonly TaskCompletionSource Submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HangJobs;
        public Action? CancellationObserved;
        public Func<string> ResultUrls = () => """["https://cdn.example.test/out.mp4"]""";
        public int JobPolls;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            switch (path)
            {
                case "/v1/media/catalog":
                    json = CatalogJson;
                    break;
                case "/v1/media/uploads":
                    json = JsonSerializer.Serialize(new
                    {
                        asset = new { id = Guid.NewGuid().ToString("D"), sha256 = "00", kind = "image", bytes = 1, contentType = "image/png" },
                        expiresAt = DateTimeOffset.UtcNow.AddHours(24).ToString("O"),
                    });
                    break;
                case "/v1/media/quotes":
                    json = JsonSerializer.Serialize(new
                    {
                        quoteId = Quote,
                        model = "vid-1",
                        operation = "text_to_video",
                        credits = 4,
                        expiresAt = DateTimeOffset.UtcNow.AddMinutes(2).ToString("O"),
                        fingerprint = "fp",
                        outputCount = 1,
                        settings = new { },
                        inputs = Array.Empty<object>(),
                    });
                    break;
                case "/v1/media/jobs":
                    Interlocked.Increment(ref Creates);
                    SubmitKey = Assert.Single(request.Headers.GetValues("Idempotency-Key"));
                    Submitted.TrySetResult();
                    if (HangJobs)
                    {
                        using var observer = cancellationToken.Register(() => CancellationObserved?.Invoke());
                        await Task.Delay(Timeout.Infinite, cancellationToken);
                        throw new InvalidOperationException("The simulated lost-response request should be canceled.");
                    }
                    json = """{"id":"job-1","model":"vid-1","operation":"text_to_video","status":"queued"}""";
                    break;
                case "/v1/media/jobs/job-1":
                    Interlocked.Increment(ref JobPolls);
                    json = $$"""{"id":"job-1","model":"vid-1","operation":"text_to_video","status":"succeeded","costCredits":4,"resultUrls":{{ResultUrls()}}}""";
                    break;
                case "/v1/media/jobs/job-9":
                    Interlocked.Increment(ref JobPolls);
                    json = """{"id":"job-9","model":"vid-1","operation":"text_to_video","status":"succeeded","costCredits":4,"resultUrls":["https://cdn.example.test/out.mp4"]}""";
                    break;
                case "/v1/media/submissions/cancel":
                    Assert.Equal(SubmitKey, Assert.Single(request.Headers.GetValues("Idempotency-Key")));
                    Assert.Equal(Quote, JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["quoteId"]!.GetValue<string>());
                    CanceledSubmission = Quote;
                    Canceled.TrySetResult();
                    json = """{"status":"canceled"}""";
                    break;
                case "/v1/media/jobs/job-9/cancel":
                    CanceledJob = "job-9";
                    Canceled.TrySetResult();
                    json = """{"status":"canceled"}""";
                    break;
                default: throw new InvalidOperationException("Unexpected media request: " + path);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    private sealed class DownloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([5, 6, 7, 8]),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            return Task.FromResult(response);
        }
    }

    private static readonly Func<string, CancellationToken, Task<System.Net.IPAddress[]>> Resolver =
        (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("93.184.216.34") });

    private sealed class Fixture : IDisposable
    {
        public string Folder;
        public ProjectSession Session;
        public MediaHandler Handler;
        public Fixture(bool hangJobs = false)
        {
            Folder = Path.Combine(Path.GetTempPath(), "temno-mediar-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Folder, "media"));
            Session = ProjectSession.CreateNew();
            Session.Save(Folder);
            ProjectHub.ActiveSession = Session;
            UserPreferences.SetPathForTests(Path.Combine(Folder, "prefs.json"));
            ToolExecutor.ResetGenerationService();
            ToolExecutor.ResetMediaCatalog();
            ToolExecutor.ResetMediaQuotes();
            Handler = new MediaHandler { HangJobs = hangJobs };
            ToolExecutor.MediaClientOverride = new HostedMediaClient("https://fixture.invalid", () => "test-token", new HttpClient(Handler));
            ToolExecutor.GenerationServiceOverride = new GenerationService(
                new FakeGenerationClient(), new HttpClient(new DownloadHandler()), hostResolver: Resolver);
        }
        public void Dispose()
        {
            ToolExecutor.GenerationServiceOverride = null;
            ToolExecutor.ResetGenerationService();
            ToolExecutor.ResetMediaCatalog();
            ToolExecutor.ResetMediaQuotes();
            ProjectHub.ActiveSession = null;
            UserPreferences.SetPathForTests(null);
            HostedRuntime.Session = null;
            try { Directory.Delete(Folder, true); } catch { }
        }
    }

    private static async Task WaitForSettled(MediaManifestEntry entry)
    {
        for (var i = 0; i < 750 && entry.GenerationStatus is "preparing" or "generating" or "downloading"; i++)
            await Task.Delay(20);
    }

    [Fact]
    public async Task Cancel_BeforeJobResponse_UsesSubmissionCancelWithoutSecondCharge()
    {
        using var f = new Fixture(hangJobs: true);
        var quote = ToolExecutor.Execute("quote_media", new JsonObject
        {
            ["model"] = "vid-1", ["operation"] = "text_to_video", ["prompt"] = "Blue square",
        });
        Assert.False(quote.IsError, quote.Content[0].Text);
        var result = ToolExecutor.Execute("generate_video", new JsonObject
        {
            ["quoteId"] = f.Handler.Quote, ["maxCredits"] = 4,
        });
        Assert.False(result.IsError, result.Content[0].Text);
        await f.Handler.Submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var entry = Assert.Single(f.Session.Manifest.Entries);
        Assert.Null(entry.GenerationInput!.BackendJobId);
        bool? persistedAtCancellation = null;
        f.Handler.CancellationObserved = () =>
        {
            var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Folder, "media.json")));
            persistedAtCancellation = saved?["entries"]?[0]?["generationInput"]?["cancelRequested"]?.GetValue<bool>() == true;
        };
        Assert.True(ToolExecutor.CancelInFlightGeneration(entry.Id));
        Assert.True(persistedAtCancellation, "Cancellation must not wake a worker before its durable intent is saved.");
        await f.Handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitForSettled(entry);
        Assert.Equal("failed: Cancellation requested", entry.GenerationStatus);
        Assert.True(entry.GenerationInput.CancelRequested);
        Assert.Equal(f.Handler.Quote, f.Handler.CanceledSubmission);
        Assert.Null(f.Handler.CanceledJob);
        Assert.Equal(1, f.Handler.Creates);
        Assert.Equal(f.Handler.SubmitKey, entry.GenerationInput.SubmitKey);
    }

    [Fact]
    public async Task Cancel_AfterJobId_UsesJobCancel()
    {
        using var f = new Fixture();
        var entry = new MediaManifestEntry
        {
            Id = Guid.NewGuid().ToString().ToUpperInvariant(),
            Name = "stuck",
            Type = ClipType.video,
            Source = MediaSource.Project("media/gen-stuck.mp4"),
            GenerationStatus = "generating",
            GenerationInput = new GenerationInput
            {
                Prompt = "x",
                Model = "vid-1",
                Operation = "text_to_video",
                BackendJobId = "job-9",
                CancelRequested = true,
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };
        f.Session.Manifest.Entries.Add(entry);
        Assert.True(ToolExecutor.RetryGeneration(entry.Id));
        await f.Handler.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitForSettled(entry);
        Assert.Equal("job-9", f.Handler.CanceledJob);
        Assert.Null(f.Handler.CanceledSubmission);
        Assert.Equal("failed: Cancellation requested", entry.GenerationStatus);
    }

    [Fact]
    public async Task Retry_ByResultUrls_LandsWithoutPolling()
    {
        using var f = new Fixture();
        var entry = new MediaManifestEntry
        {
            Id = Guid.NewGuid().ToString().ToUpperInvariant(),
            Name = "orphan",
            Type = ClipType.video,
            Source = MediaSource.Project("media/gen-orphan.mp4"),
            GenerationStatus = "failed: network lost",
            GenerationInput = new GenerationInput
            {
                Prompt = "x",
                Model = "vid-1",
                Operation = "text_to_video",
                ResultUrls = ["https://cdn.example.test/out.mp4"],
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };
        f.Session.Manifest.Entries.Add(entry);
        Assert.True(ToolExecutor.RetryGeneration(entry.Id));
        // The entry starts failed, so first wait for the background recovery to
        // pick it up before waiting for it to settle.
        for (var i = 0; i < 750 && entry.GenerationStatus == "failed: network lost"; i++)
            await Task.Delay(20);
        await WaitForSettled(entry);
        Assert.Null(entry.GenerationStatus);
        Assert.Equal(0, f.Handler.JobPolls);
        Assert.Equal(0, f.Handler.Creates);
        Assert.True(File.Exists(Path.Combine(f.Folder, (entry.Source as MediaSource.ProjectSource)!.RelativePath)));
    }

    [Fact]
    public async Task Retry_ByJobId_PollsSameJobAndLands()
    {
        using var f = new Fixture();
        var entry = new MediaManifestEntry
        {
            Id = Guid.NewGuid().ToString().ToUpperInvariant(),
            Name = "reopen",
            Type = ClipType.video,
            Source = MediaSource.Project("media/gen-reopen.mp4"),
            GenerationStatus = "generating",
            GenerationInput = new GenerationInput
            {
                Prompt = "x",
                Model = "vid-1",
                Operation = "text_to_video",
                BackendJobId = "job-1",
                QuoteId = Guid.NewGuid().ToString("D"),
                SubmitKey = Guid.NewGuid().ToString("D"),
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };
        f.Session.Manifest.Entries.Add(entry);
        Assert.True(ToolExecutor.RetryGeneration(entry.Id));
        await WaitForSettled(entry);
        Assert.Null(entry.GenerationStatus);
        Assert.True(f.Handler.JobPolls >= 1);
        Assert.Equal(0, f.Handler.Creates);
        Assert.Equal(4, entry.GenerationInput!.ChargedCredits);
    }

    [Fact]
    public async Task AmbiguousSubmit_ReusesKeyInsteadOfChargingTwice()
    {
        using var f = new Fixture();
        var key = Guid.NewGuid().ToString("D");
        var entry = new MediaManifestEntry
        {
            Id = Guid.NewGuid().ToString().ToUpperInvariant(),
            Name = "ambiguous",
            Type = ClipType.video,
            Source = MediaSource.Project("media/gen-ambiguous.mp4"),
            GenerationStatus = "preparing",
            GenerationInput = new GenerationInput
            {
                Prompt = "x",
                Model = "vid-1",
                Operation = "text_to_video",
                QuoteId = f.Handler.Quote,
                SubmitKey = key,
                CreatedAt = DateTimeOffset.UtcNow,
            },
        };
        f.Session.Manifest.Entries.Add(entry);
        Assert.True(ToolExecutor.RetryGeneration(entry.Id));
        await WaitForSettled(entry);
        Assert.Null(entry.GenerationStatus);
        Assert.Equal(key, f.Handler.SubmitKey);
        Assert.Equal("job-1", entry.GenerationInput!.BackendJobId);

        // A second ambiguous recovery of the same entry reuses the same key again.
        entry.GenerationStatus = "preparing";
        entry.GenerationInput.BackendJobId = null;
        entry.GenerationInput.ResultUrls = null;
        Assert.True(ToolExecutor.RetryGeneration(entry.Id));
        await WaitForSettled(entry);
        Assert.Null(entry.GenerationStatus);
        Assert.Equal(key, f.Handler.SubmitKey);
        Assert.Equal(2, f.Handler.Creates);
    }

    [Fact]
    public async Task DuplicateLanding_KeepsStableSiblingIds()
    {
        using var f = new Fixture(hangJobs: false);
        f.Handler.ResultUrls = () => """["https://cdn.example.test/a.mp4","https://cdn.example.test/b.mp4"]""";
        var quote = ToolExecutor.Execute("quote_media", new JsonObject
        {
            ["model"] = "vid-1", ["operation"] = "text_to_video", ["prompt"] = "Two outputs",
        });
        Assert.False(quote.IsError, quote.Content[0].Text);
        var submit = ToolExecutor.Execute("generate_video", new JsonObject { ["quoteId"] = f.Handler.Quote });
        Assert.False(submit.IsError, submit.Content[0].Text);

        var placeholder = Assert.Single(f.Session.Manifest.Entries, e => e.GenerationInput?.QuoteId == f.Handler.Quote);
        await WaitForSettled(placeholder);
        Assert.Null(placeholder.GenerationStatus);
        Assert.Equal(2, f.Session.Manifest.Entries.Count);
        var sibling = Assert.Single(f.Session.Manifest.Entries, e => e.Id != placeholder.Id);
        Assert.Equal(ToolExecutor.SiblingMediaOutputId("job:job-1", 1), sibling.Id);
        Assert.Equal(1, sibling.GenerationInput!.OutputIndex);

        // Recovery of the landed placeholder upserts the same sibling, no dupes.
        Assert.True(ToolExecutor.RetryGeneration(placeholder.Id));
        await WaitForSettled(placeholder);
        Assert.Equal(2, f.Session.Manifest.Entries.Count);
        Assert.Contains(f.Session.Manifest.Entries, e => e.Id == sibling.Id);
    }

    [Fact]
    public void MediaRouting_DoesNotAffectImages()
    {
        static MediaManifestEntry Job(string? op) => new()
        {
            Id = "x", Name = "x", Type = ClipType.image,
            Source = MediaSource.Project("media/x.png"),
            GenerationInput = op is null ? null : new GenerationInput { Operation = op },
        };
        Assert.False(ToolExecutor.IsMediaJob(Job("generate_image")));
        Assert.False(ToolExecutor.IsMediaJob(Job("edit_image")));
        Assert.False(ToolExecutor.IsImagesJob(Job("text_to_video")));
        Assert.False(ToolExecutor.IsImagesJob(Job("upscale_image")));
        Assert.True(ToolExecutor.IsImagesJob(Job("edit_image")));
    }
}
