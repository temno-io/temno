using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Temno.Generation;
using Xunit;

namespace Temno.Core.Tests;

public class HostedImageClientTests
{
    private const string CatalogJson = """
        {
          "version": "images-v1", "developmentPrices": true, "configured": true,
          "acceptJobs": true, "qualificationOwner": false,
          "enabledOperations": ["muse-image:generate_image"],
          "maxUploadBytes": 12582912,
          "models": [{
            "id": "muse-image", "displayName": "Muse Image", "family": "Muse", "variant": null,
            "operations": [
              {"operation": "generate_image", "endpoint": "meta/muse-image/text-to-image",
               "available": true, "unavailableReason": null, "minInputs": 0, "maxInputs": 0,
               "supportsMask": false, "maxOutputs": 4, "priceNote": "draft"},
              {"operation": "edit_image", "endpoint": "meta/muse-image/edit",
               "available": true, "unavailableReason": null, "minInputs": 1, "maxInputs": 10,
               "supportsMask": false, "maxOutputs": 4, "priceNote": "draft"}
            ],
            "settings": [
              {"name": "count", "allowedValues": [], "defaultValue": "1", "description": "Integer 1..4"},
              {"name": "quality", "allowedValues": ["low", "high"], "defaultValue": "low", "description": "Quality."}
            ],
            "outputFormats": ["png"], "defaultOutputFormat": "png",
            "maxReferences": 10, "supportsMask": false, "unavailableReason": null, "priceNote": "draft"
          }]
        }
        """;

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public readonly List<HttpRequestMessage> Requests = [];
        public readonly List<byte[]> Bodies = [];

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken));
            return _respond(request);
        }
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json) };

    private static HostedImageClient Client(RecordingHandler handler)
        => new("https://gateway.example.test", () => "token", new HttpClient(handler));

    [Fact]
    public async Task GetCatalogAsync_ParsesProfilesAndSettings()
    {
        var handler = new RecordingHandler(_ => JsonResponse(CatalogJson));
        var catalog = await Client(handler).GetCatalogAsync();

        Assert.Equal("images-v1", catalog.Version);
        Assert.True(catalog.DevelopmentPrices);
        Assert.True(catalog.Configured);
        Assert.True(catalog.AcceptJobs);
        Assert.False(catalog.QualificationOwner);
        var model = Assert.Single(catalog.Models);
        Assert.Equal("muse-image", model.Id);
        Assert.Equal("Muse Image", model.DisplayName);
        Assert.Equal(2, model.Operations.Count);
        Assert.Equal(2, model.Settings.Count);
        Assert.Equal("count", model.Settings[0].Name);
        Assert.Empty(model.Settings[0].AllowedValues);
        Assert.Equal(["low", "high"], model.Settings[1].AllowedValues);
        Assert.Same(model, catalog.Get("MUSE-IMAGE"));
        Assert.Null(catalog.Get("nope"));
    }

    [Fact]
    public async Task CatalogAvailability_RequiresEnabledOperationOrQualification()
    {
        var handler = new RecordingHandler(_ => JsonResponse(CatalogJson));
        var catalog = await Client(handler).GetCatalogAsync();
        var model = catalog.Get("muse-image")!;

        Assert.True(catalog.IsAvailable(model, "generate_image"));
        Assert.False(catalog.IsAvailable(model, "edit_image"));
        Assert.Contains("not enabled", catalog.UnavailabilityReason(model, "edit_image"));

        catalog.QualificationOwner = true;
        Assert.True(catalog.IsAvailable(model, "edit_image"));

        model.Operations.First(o => o.Operation == "edit_image").Available = false;
        model.Operations.First(o => o.Operation == "edit_image").UnavailableReason = "Unpriced.";
        Assert.False(catalog.IsAvailable(model, "edit_image"));
        Assert.Equal("Unpriced.", catalog.UnavailabilityReason(model, "edit_image"));
    }

    [Fact]
    public async Task UploadAsync_SendsRawBytesWithContentTypeAndKey()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var key = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var handler = new RecordingHandler(_ => JsonResponse(
            $$"""{"asset":{"id":"{{assetId:D}}","sha256":"abc","width":8,"height":8,"bytes":3,"contentType":"image/png"},"expiresAt":"2026-09-13T00:00:00Z"}"""));
        var result = await Client(handler).UploadAsync(bytes, "image/png", key);

        Assert.Equal(assetId, result.Asset.Id);
        Assert.Equal(8, result.Asset.Width);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/images/uploads", request.RequestUri!.AbsolutePath);
        Assert.Equal("image/png", request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(bytes, Assert.Single(handler.Bodies));
        Assert.Equal(key.ToString("D"), string.Join(",", request.Headers.GetValues("Idempotency-Key")));
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
    }

    [Fact]
    public async Task UploadAsync_RejectsOversizeBadTypeAndEmptyKeyLocally()
    {
        var handler = new RecordingHandler(_ => JsonResponse("{}"));
        var client = Client(handler);
        var big = new byte[HostedImageClient.MaxUploadBytes + 1];

        var tooLarge = await Assert.ThrowsAsync<GenerationException>(() =>
            client.UploadAsync(big, "image/png", Guid.NewGuid()));
        Assert.Equal("image_too_large", tooLarge.Code);

        var badType = await Assert.ThrowsAsync<GenerationException>(() =>
            client.UploadAsync([1], "image/gif", Guid.NewGuid()));
        Assert.Equal("invalid_image_type", badType.Code);

        await Assert.ThrowsAsync<GenerationException>(() =>
            client.UploadAsync([1], "image/png", Guid.Empty));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task QuoteAsync_SendsFullRequestAndParsesQuote()
    {
        var input = Guid.NewGuid();
        var mask = Guid.NewGuid();
        var quoteId = Guid.NewGuid().ToString("D");
        string? seenBody = null;
        var handler = new RecordingHandler(request =>
        {
            seenBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse($$"""
                {"quoteId":"{{quoteId}}","model":"muse-image","operation":"edit_image","credits":8,
                 "priceVersion":"image-2026-09-12-v1","expiresAt":"2026-09-12T01:00:00Z",
                 "fingerprint":"fp","outputCount":2,"settings":{"count":2},
                 "inputIds":["{{input:D}}"],"maskId":"{{mask:D}}"}
                """);
        });
        var quote = await Client(handler).QuoteAsync(new HostedImageQuoteRequest
        {
            Model = "muse-image",
            Operation = "edit_image",
            Prompt = "a cat",
            Settings = new JsonObject { ["count"] = 2 },
            InputIds = [input],
            MaskId = mask,
        });

        var body = JsonNode.Parse(seenBody!)!;
        Assert.Equal("muse-image", body["model"]!.GetValue<string>());
        Assert.Equal("edit_image", body["operation"]!.GetValue<string>());
        Assert.Equal("a cat", body["prompt"]!.GetValue<string>());
        Assert.Equal(2, body["settings"]!["count"]!.GetValue<int>());
        Assert.Equal(input.ToString("D"), body["inputIds"]![0]!.GetValue<string>());
        Assert.Equal(mask.ToString("D"), body["maskId"]!.GetValue<string>());

        Assert.Equal(quoteId, quote.QuoteId);
        Assert.Equal(8, quote.Credits);
        Assert.Equal(2, quote.OutputCount);
        Assert.Equal([input], quote.InputIds);
        Assert.Equal(mask, quote.MaskId);
    }

    [Fact]
    public async Task SubmitAndPoll_UseStableKeyAndMapStatuses()
    {
        var quoteId = Guid.NewGuid().ToString("D");
        var jobId = Guid.NewGuid().ToString("D");
        var key = Guid.NewGuid();
        var calls = 0;
        var handler = new RecordingHandler(request =>
        {
            calls++;
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/v1/images/jobs")
                return JsonResponse($$"""{"id":"{{jobId}}","model":"muse-image","operation":"edit_image","status":"queued","quoteId":"{{quoteId}}","resultUrls":[]}""");
            if (request.Method == HttpMethod.Get)
                return JsonResponse($$"""{"id":"{{jobId}}","model":"muse-image","operation":"edit_image","status":"succeeded","costCredits":8,"quoteId":"{{quoteId}}","resultUrls":["https://cdn.example.test/a.png","https://cdn.example.test/b.png"]}""");
            return JsonResponse("""{"id":"x","status":"canceled"}""");
        });
        var client = Client(handler);
        var submitted = await client.SubmitAsync(quoteId, key);
        Assert.Equal(jobId, submitted.Id);
        Assert.False(submitted.IsTerminal);

        var submit = handler.Requests[0];
        Assert.Equal(key.ToString("D"), string.Join(",", submit.Headers.GetValues("Idempotency-Key")));
        Assert.Equal(quoteId, JsonNode.Parse(handler.Bodies[0])!["quoteId"]!.GetValue<string>());

        var job = (await client.GetJobAsync(jobId))!;
        Assert.True(job.IsTerminal);
        Assert.Equal(2, job.ResultUrls!.Count);
        var backend = job.ToBackendJob();
        Assert.Equal(BackendGenerationStatus.Succeeded, backend.Status);
        Assert.Equal(8, backend.CostCredits);

        await client.CancelJobAsync(jobId);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task GetJobAsync_NotFound_ReturnsNull()
    {
        var handler = new RecordingHandler(_ => JsonResponse("", HttpStatusCode.NotFound));
        Assert.Null(await Client(handler).GetJobAsync("missing"));
        await Client(handler).CancelJobAsync("missing");
    }

    [Fact]
    public async Task CancelSubmission_UsesSavedQuoteAndKeyWithoutCreatingAJob()
    {
        var quote = Guid.NewGuid().ToString("D"); var key = Guid.NewGuid();
        var handler = new RecordingHandler(_ => JsonResponse("{\"status\":\"canceled\"}"));
        await Client(handler).CancelSubmissionAsync(quote, key);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/v1/images/submissions/cancel", request.RequestUri!.AbsolutePath);
        Assert.Equal(key.ToString("D"), Assert.Single(request.Headers.GetValues("Idempotency-Key")));
        Assert.Equal(quote, JsonNode.Parse(Assert.Single(handler.Bodies))!["quoteId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Errors_PreserveServerCodes()
    {
        var handler = new RecordingHandler(request =>
            JsonResponse("""{"code":"insufficient_credits","message":"Out of credits"}""",
                HttpStatusCode.PaymentRequired));
        var payment = await Assert.ThrowsAsync<GenerationException>(() => Client(handler).GetCatalogAsync());
        Assert.Equal(402, payment.StatusCode);
        Assert.Equal("insufficient_credits", payment.Code);

        var conflict = new RecordingHandler(_ =>
            JsonResponse("""{"code":"quote_key_conflict"}""", HttpStatusCode.Conflict));
        var error409 = await Assert.ThrowsAsync<GenerationException>(() =>
            Client(conflict).SubmitAsync(Guid.NewGuid().ToString("D"), Guid.NewGuid()));
        Assert.Equal(409, error409.StatusCode);
        Assert.Equal("quote_key_conflict", error409.Code);
    }

    [Fact]
    public async Task MissingAccessToken_ThrowsAuthenticationRequired()
    {
        var handler = new RecordingHandler(_ => JsonResponse(CatalogJson));
        var client = new HostedImageClient("https://gateway.example.test", () => null, new HttpClient(handler));
        var error = await Assert.ThrowsAsync<GenerationException>(() => client.GetCatalogAsync());
        Assert.Equal(401, error.StatusCode);
        Assert.Equal("authentication_required", error.Code);
        Assert.Empty(handler.Requests);
    }
}
