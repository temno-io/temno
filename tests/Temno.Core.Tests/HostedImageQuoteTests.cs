using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Temno.Generation;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

public class HostedImageQuoteTests
{
    private static HostedImageQuote MakeQuote() => new()
    {
        QuoteId = Guid.NewGuid().ToString("D"),
        Model = "muse-image",
        Operation = "edit_image",
        Credits = 8,
        PriceVersion = "image-2026-09-12-v1",
        ExpiresAt = "2026-09-12T01:00:00Z",
        Fingerprint = "fp",
        OutputCount = 2,
        Settings = new JsonObject { ["count"] = 2 },
        InputIds = [Guid.Parse("11111111-1111-1111-1111-111111111111")],
        MaskId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
    };

    private static HostedImageQuoteRequest MakeRequest(HostedImageQuote quote) => new()
    {
        Model = quote.Model,
        Operation = quote.Operation,
        Prompt = "a cat",
        Settings = (JsonObject)quote.Settings!.DeepClone(),
        InputIds = [.. quote.InputIds!],
        MaskId = quote.MaskId,
    };

    [Fact]
    public void QuoteMatches_AcceptsIdenticalRequest()
    {
        var quote = MakeQuote();
        Assert.True(quote.Matches(MakeRequest(quote)));
    }

    [Fact]
    public void QuoteMatches_RejectsAnyChange()
    {
        var quote = MakeQuote();
        var changed = MakeRequest(quote);
        changed.Model = "other-model";
        Assert.False(quote.Matches(changed));

        changed = MakeRequest(quote);
        changed.Operation = "generate_image";
        Assert.False(quote.Matches(changed));

        changed = MakeRequest(quote);
        changed.MaskId = Guid.NewGuid();
        Assert.False(quote.Matches(changed));

        changed = MakeRequest(quote);
        changed.InputIds = [Guid.NewGuid()];
        Assert.False(quote.Matches(changed));

        changed = MakeRequest(quote);
        changed.InputIds = [.. changed.InputIds!, Guid.NewGuid()];
        Assert.False(quote.Matches(changed));

        changed = MakeRequest(quote);
        changed.Settings = new JsonObject { ["count"] = 3 };
        Assert.False(quote.Matches(changed));

        Assert.False(quote.Matches(null!));
    }

    [Fact]
    public void JobStatusMapping_CoversImagesLifecycle()
    {
        Assert.Equal(BackendGenerationStatus.Queued, new HostedImageJob { Id = "a", Status = "queued" }.ToBackendJob().Status);
        Assert.Equal(BackendGenerationStatus.Running, new HostedImageJob { Id = "a", Status = "submitting" }.ToBackendJob().Status);
        Assert.Equal(BackendGenerationStatus.Running, new HostedImageJob { Id = "a", Status = "running" }.ToBackendJob().Status);
        Assert.Equal(BackendGenerationStatus.Succeeded, new HostedImageJob { Id = "a", Status = "succeeded" }.ToBackendJob().Status);
        Assert.Equal(BackendGenerationStatus.Failed, new HostedImageJob { Id = "a", Status = "failed", Error = "provider_error" }.ToBackendJob().Status);
        Assert.Equal(BackendGenerationStatus.Failed, new HostedImageJob { Id = "a", Status = "canceled" }.ToBackendJob().Status);
        Assert.True(new HostedImageJob { Id = "a", Status = "succeeded" }.IsTerminal);
        Assert.False(new HostedImageJob { Id = "a", Status = "running" }.IsTerminal);
    }

    // WP-329: hosted generation is quote-only. The jobs client lists nothing and refuses every submit,
    // whatever the model, before any request leaves the desktop.
    [Fact]
    public async Task HostedGenerationClient_RefusesEverySubmitWithoutARequest()
    {
        var calls = 0;
        var client = new HostedGenerationClient(
            "https://gateway.example.test",
            () => "token",
            new HttpClient(new StubHandler(_ =>
            {
                calls++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"id":"job-1"}""") };
            })));

        Assert.Empty(await client.ListModelsAsync());
        foreach (var (model, parameters) in new (string, BackendGenerationParams)[]
                 {
                     ("flux-schnell", new ImageGenerationParams { Prompt = "x" }),
                     ("muse-image", new ImageGenerationParams { Prompt = "x" }),
                     ("seedance-2.5", new VideoGenerationParams { Prompt = "x" }),
                     ("music-model", new AudioGenerationParams { Prompt = "x" }),
                 })
        {
            var error = await Assert.ThrowsAsync<GenerationException>(() => client.SubmitGenerationAsync(model, parameters));
            Assert.Equal("quote_required", error.Code);
            Assert.Contains("quote_media", error.Message);
        }
        var upload = await Assert.ThrowsAsync<GenerationException>(() => client.UploadReferenceAsync("a.png", "image/png"));
        Assert.Equal("quote_required", upload.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task HostedGenerationClient_StillPollsAndCancelsSavedJobs()
    {
        var requests = new List<string>();
        var client = new HostedGenerationClient(
            "https://gateway.example.test",
            () => "token",
            new HttpClient(new StubHandler(request =>
            {
                requests.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"job-1","status":"succeeded","costCredits":25,"resultUrls":["https://cdn.example.test/a.mp4"]}"""),
                };
            })));

        var job = await client.GetJobStatusAsync("job-1");
        Assert.Equal(BackendGenerationStatus.Succeeded, job!.Status);
        Assert.Equal(25, job.CostCredits);
        await client.CancelJobAsync("job-1");
        Assert.Equal(["GET /v1/jobs/job-1", "POST /v1/jobs/job-1/cancel"], requests);
    }

    [Fact]
    public void StableIds_AreDeterministicPerPurposeAndKey()
    {
        Assert.Equal(ToolExecutor.StableGuid("p", "k"), ToolExecutor.StableGuid("p", "k"));
        Assert.NotEqual(ToolExecutor.StableGuid("p", "k"), ToolExecutor.StableGuid("p", "other"));
        Assert.NotEqual(ToolExecutor.StableGuid("p", "k"), ToolExecutor.StableGuid("q", "k"));

        Assert.Equal(
            ToolExecutor.SiblingOutputId("job:1", 2),
            ToolExecutor.SiblingOutputId("job:1", 2));
        Assert.NotEqual(
            ToolExecutor.SiblingOutputId("job:1", 1),
            ToolExecutor.SiblingOutputId("job:1", 2));
        Assert.NotEqual(
            ToolExecutor.SiblingOutputId("job:1", 1),
            ToolExecutor.SiblingOutputId("job:2", 1));

        Assert.Equal(ToolExecutor.SubmitKeyFor("placeholder"), ToolExecutor.SubmitKeyFor("placeholder"));
        Assert.NotEqual(ToolExecutor.SubmitKeyFor("a"), ToolExecutor.SubmitKeyFor("b"));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }
}
