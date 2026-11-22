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

// WP-242 submit path: cached-quote verification (model/operation/category,
// prompt/settings/inputs echoes, maxCredits, account/project binding, source
// hashes), correct-type landing, and the quote-only refusal (WP-329). No duplicate
// charge: recovery reuses the persisted submit key (see Recovery tests).

[Collection("EnvRegistry")]
public sealed class HostedMediaMcpSubmitTests
{
    private const string CatalogJson = """
        {"version":"media-v1","developmentPrices":true,"configured":true,"acceptJobs":true,"qualificationOwner":true,"enabledOperations":[],
        "models":[
        {"id":"vid-1","displayName":"Vid One","family":"Vid","category":"video","route":"fal",
        "operations":[{"operation":"text_to_video","available":true,"inputs":[],"maxOutputs":2,"pricingUnit":"per_second"}],
        "settings":[{"name":"aspect_ratio","allowedValues":["16:9","9:16"],"defaultValue":"16:9","description":"Aspect."}],
        "outputKinds":["video"]},
        {"id":"aud-1","displayName":"Aud One","family":"Aud","category":"audio","route":"fal",
        "operations":[{"operation":"text_to_speech","available":true,"inputs":[],"maxOutputs":1,"pricingUnit":"per_1k_chars"}],
        "settings":[],"outputKinds":["audio"]},
        {"id":"up-1","displayName":"Up One","family":"Up","category":"upscale","route":"fal",
        "operations":[{"operation":"upscale_image","promptRequired":false,"promptMaxLength":0,"available":true,"inputs":[{"role":"reference_image","kinds":["image"],"min":1,"max":1}],"maxOutputs":1,"pricingUnit":"per_mp"}],
        "settings":[],"outputKinds":["image"]}
        ]}
        """;

    private sealed class MediaHandler : HttpMessageHandler
    {
        public string? LastQuoteId;
        public int Requests;
        public TimeSpan QuoteLifetime = TimeSpan.FromMinutes(2);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            var path = request.RequestUri!.AbsolutePath;
            string json;
            switch (path)
            {
                case "/v1/media/uploads/start":
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
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
                    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                    var id = Guid.NewGuid().ToString("D");
                    LastQuoteId = id;
                    json = JsonSerializer.Serialize(new
                    {
                        quoteId = id,
                        model = body["model"]!.GetValue<string>(),
                        operation = body["operation"]!.GetValue<string>(),
                        credits = 7,
                        priceVersion = "media-2026-09-18-v1",
                        expiresAt = DateTimeOffset.UtcNow.Add(QuoteLifetime).ToString("O"),
                        fingerprint = "fp",
                        outputCount = 1,
                        settings = body["settings"] ?? new JsonObject(),
                        inputs = body["inputs"] ?? new JsonArray(),
                    });
                    break;
                case "/v1/media/jobs":
                    json = """{"id":"job-1","model":"vid-1","operation":"text_to_video","status":"queued"}""";
                    break;
                case "/v1/media/jobs/job-1":
                    json = JobJson("https://cdn.example.test/out.mp4");
                    break;
                case "/v1/media/jobs/job-audio":
                    json = JobJson("https://cdn.example.test/out.mp3").Replace("job-1", "job-audio");
                    break;
                case "/v1/media/jobs/job-up":
                    json = JobJson("https://cdn.example.test/out.png").Replace("job-1", "job-up");
                    break;
                default: throw new InvalidOperationException("Unexpected media request: " + path);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }

        private static string JobJson(string url) =>
            $$"""{"id":"job-1","model":"vid-1","operation":"text_to_video","status":"succeeded","costCredits":7,"resultUrls":["{{url}}"]}""";
    }

    private sealed class DownloadHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            byte[] bytes = path.EndsWith(".mp3") ? [10, 20, 30] : path.EndsWith(".png") ? [1, 2] : [5, 6, 7, 8];
            var contentType = path.EndsWith(".mp3") ? "audio/mpeg" : path.EndsWith(".png") ? "image/png" : "video/mp4";
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            return Task.FromResult(response);
        }
    }

    private static readonly Func<string, CancellationToken, Task<System.Net.IPAddress[]>> Resolver =
        (_, _) => Task.FromResult(new[] { System.Net.IPAddress.Parse("93.184.216.34") });

    private sealed class FakeSession : IAccountSessionSource
    {
        public string UserId { get; set; } = "user-1";
        public bool IsSignedIn => true;
        public string? Email => null;
        public string? AccessToken => "test-token";
        public CreditBalance? Balance => null;
        public string? BalanceError => null;
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class Fixture : IDisposable
    {
        public string Folder;
        public ProjectSession Session;
        public MediaHandler Handler;
        public Fixture()
        {
            Folder = Path.Combine(Path.GetTempPath(), "temno-medias-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Folder, "media"));
            Session = ProjectSession.CreateNew();
            Session.Save(Folder);
            ProjectHub.ActiveSession = Session;
            UserPreferences.SetPathForTests(Path.Combine(Folder, "prefs.json"));
            ToolExecutor.ResetGenerationService();
            ToolExecutor.ResetMediaCatalog();
            ToolExecutor.ResetMediaQuotes();
            Handler = new MediaHandler();
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

    private static MediaManifestEntry AddAsset(Fixture f, string fileName, ClipType type, byte[] bytes)
    {
        if (bytes.Length < 12) bytes = [.. bytes, .. new byte[12 - bytes.Length]];
        File.WriteAllBytes(Path.Combine(f.Folder, "media", fileName), bytes);
        var entry = new MediaManifestEntry
        {
            Id = Guid.NewGuid().ToString().ToUpperInvariant(),
            Name = fileName,
            Type = type,
            Source = MediaSource.Project("media/" + fileName),
        };
        f.Session.Manifest.Entries.Add(entry);
        return entry;
    }

    private static string QuoteVideo(Fixture f, string prompt = "A cat")
    {
        var result = ToolExecutor.Execute("quote_media", new JsonObject
        {
            ["model"] = "vid-1", ["operation"] = "text_to_video", ["prompt"] = prompt,
        });
        Assert.False(result.IsError, result.Content[0].Text);
        return f.Handler.LastQuoteId!;
    }

    private static MediaManifestEntry FindByQuote(Fixture f, string quoteId)
    {
        var entry = f.Session.Manifest.Entries.FirstOrDefault(e => e.GenerationInput?.QuoteId == quoteId);
        Assert.NotNull(entry);
        return entry!;
    }

    private static async Task WaitForLand(MediaManifestEntry entry)
    {
        for (var i = 0; i < 750 && entry.GenerationStatus is "preparing" or "generating" or "downloading"; i++)
            await Task.Delay(20);
        if (entry.GenerationStatus?.StartsWith("failed:", StringComparison.Ordinal) == true)
            Assert.Fail("Generation failed: " + entry.GenerationStatus);
    }

    [Fact]
    public async Task Submit_Video_LandsCorrectType()
    {
        using var f = new Fixture();
        var quoteId = QuoteVideo(f);
        var result = ToolExecutor.Execute("generate_video", new JsonObject { ["quoteId"] = quoteId });
        Assert.False(result.IsError, result.Content[0].Text);

        var placeholder = FindByQuote(f, quoteId);
        Assert.Equal(ClipType.video, placeholder.Type);
        Assert.Equal("text_to_video", placeholder.GenerationInput!.Operation);
        Assert.Equal("vid-1", placeholder.GenerationInput.Model);
        Assert.Equal(7, placeholder.GenerationInput.QuotedCredits);
        Assert.NotNull(placeholder.GenerationInput.SubmitKey);
        Assert.Null(placeholder.GenerationInput.ChargedCredits);

        await WaitForLand(placeholder);
        Assert.Null(placeholder.GenerationStatus);
        Assert.Equal("job-1", placeholder.GenerationInput.BackendJobId);
        Assert.Equal(7, placeholder.GenerationInput.ChargedCredits);
        Assert.EndsWith(".mp4", (placeholder.Source as MediaSource.ProjectSource)!.RelativePath);
        Assert.True(File.Exists(Path.Combine(f.Folder, (placeholder.Source as MediaSource.ProjectSource)!.RelativePath)));
    }

    [Fact]
    public void Submit_Audio_PlaceholderKindAndProvenance()
    {
        using var f = new Fixture();
        var quote = ToolExecutor.Execute("quote_media", new JsonObject
        {
            ["model"] = "aud-1", ["operation"] = "text_to_speech", ["prompt"] = "Hello",
        });
        Assert.False(quote.IsError, quote.Content[0].Text);
        // The fake job endpoint keys on job id; point audio at its own job via a second submit is
        // covered by recovery tests — here assert the placeholder kind/provenance synchronously.
        var result = ToolExecutor.Execute("generate_audio", new JsonObject { ["quoteId"] = f.Handler.LastQuoteId! });
        Assert.False(result.IsError, result.Content[0].Text);
        var placeholder = FindByQuote(f, f.Handler.LastQuoteId!);
        Assert.Equal(ClipType.audio, placeholder.Type);
        Assert.Equal("text_to_speech", placeholder.GenerationInput!.Operation);
        Assert.True(placeholder.GenerationInput.Model == "aud-1");
        Assert.True(placeholder.HasAudio == true);
    }

    [Fact]
    public async Task Submit_UpscaleImage_PreservesInputAndLandsImage()
    {
        using var f = new Fixture();
        var image = AddAsset(f, "src.png", ClipType.image, [1, 2, 3, 4]);
        var quote = ToolExecutor.Execute("quote_media", new JsonObject
        {
            ["model"] = "up-1",
            ["operation"] = "upscale_image",
            ["prompt"] = "",
            ["inputs"] = new JsonArray(new JsonObject { ["role"] = "reference_image", ["mediaRef"] = image.Id }),
        });
        Assert.False(quote.IsError, quote.Content[0].Text);
        var quoteId = f.Handler.LastQuoteId!;

        var result = ToolExecutor.Execute("upscale_media", new JsonObject
        {
            ["mediaRef"] = image.Id,
            ["quoteId"] = quoteId,
        });
        Assert.False(result.IsError, result.Content[0].Text);
        var placeholder = FindByQuote(f, quoteId);
        Assert.Equal(ClipType.image, placeholder.Type);
        Assert.Equal([image.Id], placeholder.GenerationInput!.SourceAssetIds);

        // Original input untouched by the submit.
        Assert.Equal([1, 2, 3, 4, .. new byte[8]], await File.ReadAllBytesAsync(Path.Combine(f.Folder, "media", "src.png")));
        Assert.Equal("media/src.png", (image.Source as MediaSource.ProjectSource)!.RelativePath);
    }

    [Fact]
    public void Submit_UpscaleImage_ViaComposerSubmissionPayload_SucceedsWithoutUnknownPromptError()
    {
        using var f = new Fixture();
        var image = AddAsset(f, "keroro.png", ClipType.image, [1, 2, 3, 4]);
        var quote = ToolExecutor.Execute("quote_media", new JsonObject
        {
            ["model"] = "up-1",
            ["operation"] = "upscale_image",
            ["prompt"] = "",
            ["inputs"] = new JsonArray(new JsonObject { ["role"] = "reference_image", ["mediaRef"] = image.Id }),
        });
        Assert.False(quote.IsError, quote.Content[0].Text);
        var quoteId = f.Handler.LastQuoteId!;

        var (tool, args) = GenerationComposerSubmission.BuildHostedMediaSubmit(
            GenerationKind.Upscale,
            quoteId,
            maxCredits: 24,
            modelId: "up-1",
            operation: "upscale_image",
            prompt: "",
            acceptsPrompt: false);

        var result = ToolExecutor.Execute(tool, args);
        Assert.False(result.IsError, result.Content[0].Text);
        var placeholder = FindByQuote(f, quoteId);
        Assert.Equal(ClipType.image, placeholder.Type);
    }

    [Fact]
    public void Submit_Upscale_RejectsMismatchedMediaRef()
    {
        using var f = new Fixture();
        var image = AddAsset(f, "src.png", ClipType.image, [1, 2, 3]);
        var other = AddAsset(f, "other.png", ClipType.image, [9]);
        var quote = ToolExecutor.Execute("quote_media", new JsonObject
        {
            ["model"] = "up-1",
            ["operation"] = "upscale_image",
            ["prompt"] = "",
            ["inputs"] = new JsonArray(new JsonObject { ["role"] = "reference_image", ["mediaRef"] = image.Id }),
        });
        Assert.False(quote.IsError, quote.Content[0].Text);
        var result = ToolExecutor.Execute("upscale_media", new JsonObject
        {
            ["mediaRef"] = other.Id,
            ["quoteId"] = f.Handler.LastQuoteId!,
        });
        Assert.True(result.IsError);
        Assert.Contains("not used with hosted quotes", result.Content[0].Text);
    }

    [Fact]
    public void Submit_UnknownQuote_Rejected()
    {
        using var f = new Fixture();
        var result = ToolExecutor.Execute("generate_video", new JsonObject { ["quoteId"] = Guid.NewGuid().ToString("D") });
        Assert.True(result.IsError);
        Assert.Contains("Unknown or expired quote", result.Content[0].Text);
    }

    [Fact]
    public void Submit_ExpiredQuote_Rejected()
    {
        using var f = new Fixture();
        f.Handler.QuoteLifetime = TimeSpan.FromMinutes(-5);
        var quoteId = QuoteVideo(f);
        var result = ToolExecutor.Execute("generate_video", new JsonObject { ["quoteId"] = quoteId });
        Assert.True(result.IsError);
        Assert.Contains("expired", result.Content[0].Text);
    }

    [Fact]
    public void Submit_ModelMismatch_Rejected()
    {
        using var f = new Fixture();
        var quoteId = QuoteVideo(f);
        var result = ToolExecutor.Execute("generate_video", new JsonObject { ["quoteId"] = quoteId, ["model"] = "vid-2" });
        Assert.True(result.IsError);
        Assert.Contains("not 'vid-2'", result.Content[0].Text);
    }

    [Fact]
    public void Submit_PromptMismatch_Rejected()
    {
        using var f = new Fixture();
        var quoteId = QuoteVideo(f);
        var result = ToolExecutor.Execute("generate_video", new JsonObject { ["quoteId"] = quoteId, ["prompt"] = "A dog" });
        Assert.True(result.IsError);
        Assert.Contains("prompt differs", result.Content[0].Text);
    }

    [Fact]
    public void Submit_SettingsMismatch_Rejected()
    {
        using var f = new Fixture();
        var quoted = ToolExecutor.Execute("quote_media", new JsonObject
        {
            ["model"] = "vid-1",
            ["operation"] = "text_to_video",
            ["prompt"] = "A cat",
            ["settings"] = new JsonObject { ["aspect_ratio"] = "16:9" },
        });
        Assert.False(quoted.IsError, quoted.Content[0].Text);
        var result = ToolExecutor.Execute("generate_video", new JsonObject
        {
            ["quoteId"] = f.Handler.LastQuoteId!,
            ["settings"] = new JsonObject { ["aspect_ratio"] = "9:16" },
        });
        Assert.True(result.IsError);
        Assert.Contains("settings differ", result.Content[0].Text);
    }

    [Fact]
    public void Submit_CategoryMismatch_Rejected()
    {
        using var f = new Fixture();
        var quote = ToolExecutor.Execute("quote_media", new JsonObject
        {
            ["model"] = "aud-1", ["operation"] = "text_to_speech", ["prompt"] = "Hi",
        });
        Assert.False(quote.IsError, quote.Content[0].Text);
        var result = ToolExecutor.Execute("generate_video", new JsonObject { ["quoteId"] = f.Handler.LastQuoteId! });
        Assert.True(result.IsError);
        Assert.Contains("not video", result.Content[0].Text);
    }

    [Fact]
    public void Submit_OperationEcho_ValidatedNotRejected()
    {
        using var f = new Fixture();
        var quoteId = QuoteVideo(f);
        var ok = ToolExecutor.Execute("generate_video", new JsonObject
        {
            ["quoteId"] = quoteId, ["operation"] = "text_to_video",
        });
        Assert.False(ok.IsError, ok.Content[0].Text);

        var bad = ToolExecutor.Execute("generate_video", new JsonObject
        {
            ["quoteId"] = quoteId, ["operation"] = "image_to_video",
        });
        Assert.True(bad.IsError);
        Assert.Contains("is for operation 'text_to_video'", bad.Content[0].Text);
    }

    [Fact]
    public void Submit_MaxCreditsCap_Rejected()
    {
        using var f = new Fixture();
        var quoteId = QuoteVideo(f);
        var result = ToolExecutor.Execute("generate_video", new JsonObject { ["quoteId"] = quoteId, ["maxCredits"] = 4 });
        Assert.True(result.IsError);
        Assert.Contains("above the accepted cap", result.Content[0].Text);
    }

    [Fact]
    public void Submit_LegacyDuration_Rejected()
    {
        using var f = new Fixture();
        var quoteId = QuoteVideo(f);
        var result = ToolExecutor.Execute("generate_video", new JsonObject { ["quoteId"] = quoteId, ["duration"] = 9 });
        Assert.True(result.IsError);
        Assert.Contains("'duration' is not used with hosted quotes", result.Content[0].Text);
    }

    [Fact]
    public void Submit_AccountChange_Rejected()
    {
        using var f = new Fixture();
        HostedRuntime.Session = new FakeSession { UserId = "user-1" };
        var quoteId = QuoteVideo(f);
        HostedRuntime.Session = new FakeSession { UserId = "user-2" };
        var result = ToolExecutor.Execute("generate_video", new JsonObject { ["quoteId"] = quoteId });
        Assert.True(result.IsError);
        Assert.Contains("different account", result.Content[0].Text);
    }

    [Fact]
    public void Submit_ProjectChange_Rejected()
    {
        using var f = new Fixture();
        var quoteId = QuoteVideo(f);
        var otherDir = Path.Combine(f.Folder, "other");
        Directory.CreateDirectory(otherDir);
        var other = ProjectSession.CreateNew();
        other.Save(otherDir);
        ProjectHub.ActiveSession = other;
        try
        {
            var result = ToolExecutor.Execute("generate_video", new JsonObject { ["quoteId"] = quoteId });
            Assert.True(result.IsError);
            Assert.Contains("different project", result.Content[0].Text);
        }
        finally
        {
            ProjectHub.ActiveSession = f.Session;
        }
    }

    [Fact]
    public void Submit_SourceChanged_Rejected()
    {
        using var f = new Fixture();
        var image = AddAsset(f, "src.png", ClipType.image, [1, 2, 3]);
        var quote = ToolExecutor.Execute("quote_media", new JsonObject
        {
            ["model"] = "up-1",
            ["operation"] = "upscale_image",
            ["prompt"] = "",
            ["inputs"] = new JsonArray(new JsonObject { ["role"] = "reference_image", ["mediaRef"] = image.Id }),
        });
        Assert.False(quote.IsError, quote.Content[0].Text);
        var quoteId = f.Handler.LastQuoteId!;
        File.WriteAllBytes(Path.Combine(f.Folder, "media", "src.png"), [9, 9, 9]);
        var result = ToolExecutor.Execute("upscale_media", new JsonObject { ["quoteId"] = quoteId });
        Assert.True(result.IsError);
        Assert.Contains("changed since the quote", result.Content[0].Text);
    }

    [Fact]
    public void Submit_SourceRemoved_Rejected()
    {
        using var f = new Fixture();
        var image = AddAsset(f, "src.png", ClipType.image, [1, 2, 3]);
        var quote = ToolExecutor.Execute("quote_media", new JsonObject
        {
            ["model"] = "up-1",
            ["operation"] = "upscale_image",
            ["prompt"] = "",
            ["inputs"] = new JsonArray(new JsonObject { ["role"] = "reference_image", ["mediaRef"] = image.Id }),
        });
        Assert.False(quote.IsError, quote.Content[0].Text);
        var quoteId = f.Handler.LastQuoteId!;
        f.Session.Manifest.Entries.Remove(image);
        var result = ToolExecutor.Execute("upscale_media", new JsonObject { ["quoteId"] = quoteId });
        Assert.True(result.IsError);
        Assert.Contains("no longer in the project", result.Content[0].Text);
    }

    [Fact]
    public void Submit_SourceOffline_Rejected()
    {
        using var f = new Fixture();
        var image = AddAsset(f, "src.png", ClipType.image, [1, 2, 3]);
        var quote = ToolExecutor.Execute("quote_media", new JsonObject
        {
            ["model"] = "up-1",
            ["operation"] = "upscale_image",
            ["prompt"] = "",
            ["inputs"] = new JsonArray(new JsonObject { ["role"] = "reference_image", ["mediaRef"] = image.Id }),
        });
        Assert.False(quote.IsError, quote.Content[0].Text);
        var quoteId = f.Handler.LastQuoteId!;
        File.Delete(Path.Combine(f.Folder, "media", "src.png"));
        var result = ToolExecutor.Execute("upscale_media", new JsonObject { ["quoteId"] = quoteId });
        Assert.True(result.IsError);
        Assert.Contains("offline", result.Content[0].Text);
    }

    // WP-329: hosted generation is quote-only. Without a quoteId every submit is refused, whatever
    // the model (catalog, uncached or none), before a placeholder exists or a request is sent. The
    // fixture's demo service would land a placeholder if the call fell through to the direct path.
    [Theory]
    [InlineData("generate_video", "vid-1", true)]
    [InlineData("generate_video", "seedance-2.5", false)]
    [InlineData("generate_video", null, false)]
    [InlineData("generate_audio", "aud-1", true)]
    [InlineData("generate_audio", "music-model", false)]
    [InlineData("generate_audio", null, false)]
    [InlineData("upscale_media", "up-1", true)]
    [InlineData("upscale_media", null, false)]
    public void Submit_WithoutQuote_RefusedBeforeAnyPlaceholderOrRequest(string tool, string? model, bool catalogCached)
    {
        using var f = new Fixture();
        var image = AddAsset(f, "src.png", ClipType.image, [1, 2, 3]);
        if (catalogCached)
            Assert.False(ToolExecutor.Execute("list_models", new JsonObject()).IsError);
        var before = f.Session.Manifest.Entries.Count;
        var requests = f.Handler.Requests;
        var args = new JsonObject { ["prompt"] = "A cat", ["duration"] = 5 };
        if (model is not null) args["model"] = model;
        if (tool == "upscale_media") args = new JsonObject { ["mediaRef"] = image.Id, ["model"] = model };

        var result = ToolExecutor.Execute(tool, args);

        Assert.True(result.IsError);
        Assert.Contains("quote-only", result.Content[0].Text);
        Assert.Contains("list_models, then quote_media, then " + tool, result.Content[0].Text);
        Assert.Equal(before, f.Session.Manifest.Entries.Count);
        Assert.Equal(requests, f.Handler.Requests);
    }

    [Fact]
    public void BoundProject_QuoteMedia_BindsProjectAndAccount()
    {
        using var f = new Fixture();
        HostedRuntime.Session = new FakeSession { UserId = "user-1" };
        var ok = ToolExecutor.ExecuteGenerationForProject(f.Session, "quote_media", new JsonObject
        {
            ["model"] = "vid-1", ["operation"] = "text_to_video", ["prompt"] = "A cat",
        }, expectedUserId: "user-1");
        Assert.False(ok.IsError, ok.Content[0].Text);

        var wrongUser = ToolExecutor.ExecuteGenerationForProject(f.Session, "quote_media", new JsonObject
        {
            ["model"] = "vid-1", ["operation"] = "text_to_video", ["prompt"] = "A cat",
        }, expectedUserId: "user-2");
        Assert.True(wrongUser.IsError);
        Assert.Contains("account changed", wrongUser.Content[0].Text, StringComparison.OrdinalIgnoreCase);

        var otherDir = Path.Combine(f.Folder, "other");
        Directory.CreateDirectory(otherDir);
        var other = ProjectSession.CreateNew();
        other.Save(otherDir);
        var previous = ProjectHub.ActiveSession;
        ProjectHub.ActiveSession = other;
        try
        {
            var wrongProject = ToolExecutor.ExecuteGenerationForProject(f.Session, "quote_media", new JsonObject
            {
                ["model"] = "vid-1", ["operation"] = "text_to_video", ["prompt"] = "A cat",
            }, expectedUserId: "user-1");
            Assert.True(wrongProject.IsError);
            Assert.Contains("project changed", wrongProject.Content[0].Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            ProjectHub.ActiveSession = previous;
        }
    }
}
