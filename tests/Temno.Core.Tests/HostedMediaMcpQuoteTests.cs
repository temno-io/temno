using System.Net;
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

// WP-242 quote path: validation against the media catalog, typed input roles,
// quote/request binding, routing predicates and stable ids. The fake gateway
// speaks the /v1/media/* shapes from .local/wp242/media-contract.md.

[Collection("EnvRegistry")]
public sealed class HostedMediaMcpQuoteTests
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
        {"id":"aud-clean","displayName":"Audio cleanup","category":"audio","route":"fal",
        "operations":[{"operation":"audio_isolation","promptRequired":false,"available":true,"inputs":[{"role":"source_audio","kinds":["audio"],"min":1,"max":1}],"maxOutputs":1,"pricingUnit":"per_second"}],
        "settings":[],"outputKinds":["audio"]},
        {"id":"vid-2","displayName":"Vid Two","family":"Vid","category":"video","route":"fal",
        "operations":[{"operation":"image_to_video","available":true,"inputs":[{"role":"first_frame","kinds":["image"],"min":1,"max":1},{"role":"last_frame","kinds":["image"],"min":0,"max":1}],"maxOutputs":1,"pricingUnit":"per_second"}],
        "settings":[{"name":"duration","allowedValues":[],"defaultValue":"5","description":"Integer 2..30."},
        {"name":"cfg","allowedValues":[],"defaultValue":"0.5","description":"Number 0..1."},
        {"name":"prompt_expansion","allowedValues":[],"defaultValue":"true","description":"Boolean."},
        {"name":"resolution","allowedValues":["720p","1080p"],"defaultValue":"720p","description":"Output."},
        {"name":"negative_prompt","allowedValues":[],"defaultValue":"(omitted)","description":"Optional negative prompt, 1..2000 chars."}],"outputKinds":["video"]},
        {"id":"up-1","displayName":"Up One","family":"Up","category":"upscale","route":"fal",
        "operations":[{"operation":"upscale_image","promptRequired":false,"promptMaxLength":0,"available":true,"inputs":[{"role":"reference_image","kinds":["image"],"min":1,"max":1}],"maxOutputs":1,"pricingUnit":"per_mp"}],
        "settings":[],"outputKinds":["image"]},
        {"id":"old-1","displayName":"Old","family":"Old","category":"video","route":"deferred",
        "operations":[{"operation":"text_to_video","available":false,"unavailableReason":"needs adapter","inputs":[],"maxOutputs":1}],
        "settings":[],"outputKinds":["video"]}
        ]}
        """;

    private sealed class MediaHandler : HttpMessageHandler
    {
        public string? LastQuoteId;
        public JsonNode? LastQuoteSettings;
        public int Uploads;
        public string? UploadContentType;
        public TimeSpan QuoteLifetime = TimeSpan.FromMinutes(2);
        public string? OutputNote;
        public string? QuoteError;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
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
                    Uploads++;
                    UploadContentType = request.Content!.Headers.ContentType!.MediaType;
                    json = JsonSerializer.Serialize(new
                    {
                        asset = new { id = Guid.NewGuid().ToString("D"), sha256 = "00", kind = HostedMediaClient.KindFor(UploadContentType!), bytes = 1, contentType = UploadContentType },
                        expiresAt = DateTimeOffset.UtcNow.AddHours(24).ToString("O"),
                    });
                    break;
                case "/v1/media/quotes":
                    if (QuoteError is not null)
                        return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(
                            JsonSerializer.Serialize(new { error = "invalid_request", message = QuoteError })) };
                    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                    var id = Guid.NewGuid().ToString("D");
                    LastQuoteId = id;
                    LastQuoteSettings = body["settings"]?.DeepClone();
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
                        outputNote = OutputNote,
                        settings = body["settings"] ?? new JsonObject(),
                        inputs = body["inputs"] ?? new JsonArray(),
                    });
                    break;
                default: throw new InvalidOperationException("Unexpected media request: " + path);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Folder;
        public ProjectSession Session;
        public MediaHandler Handler;
        public Fixture()
        {
            Folder = Path.Combine(Path.GetTempPath(), "temno-mediaq-" + Guid.NewGuid().ToString("N"));
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

    private static ToolCallResult Quote(Fixture f, string model, string operation, string prompt, JsonObject? settings = null, JsonArray? inputs = null)
    {
        var args = new JsonObject { ["model"] = model, ["operation"] = operation, ["prompt"] = prompt };
        if (settings is not null) args["settings"] = settings;
        if (inputs is not null) args["inputs"] = inputs;
        return ToolExecutor.Execute("quote_media", args);
    }

    private static JsonArray Inputs(params (string Role, string Ref)[] items)
    {
        var array = new JsonArray();
        foreach (var (role, mediaRef) in items)
            array.Add(new JsonObject { ["role"] = role, ["mediaRef"] = mediaRef });
        return array;
    }

    [Fact]
    public void Quote_preserves_the_Gateway_limit_sentence_for_the_composer_and_MCP()
    {
        using var f = new Fixture();
        f.Handler.QuoteError = "Kling O3 Edit requires a source video between 3 and 15 seconds long.";
        var result = Quote(f, "vid-1", "text_to_video", "A cat");
        Assert.True(result.IsError);
        Assert.Contains(f.Handler.QuoteError, result.Content[0].Text);
        Assert.Null(f.Handler.LastQuoteId);
    }

    [Fact]
    public void Quote_forwards_the_output_limitation_before_submission()
    {
        using var f = new Fixture();
        f.Handler.OutputNote = "Happy Horse Edit returns only the first 15 seconds of your source video.";
        var result = Quote(f, "vid-1", "text_to_video", "A cat");
        Assert.False(result.IsError, result.Content[0].Text);
        var quote = JsonNode.Parse(result.Content[0].Text!)!;
        Assert.Equal(f.Handler.OutputNote, quote["outputNote"]!.GetValue<string>());
    }

    [Fact]
    public void Quote_TextToVideo_Succeeds()
    {
        using var f = new Fixture();
        var result = Quote(f, "vid-1", "text_to_video", "A cat");
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.NotNull(f.Handler.LastQuoteId);
    }

    [Fact]
    public void Quote_SameImageForFirstAndLastFrame_UploadsPerRole()
    {
        using var f = new Fixture();
        var image = AddAsset(f, "frame.png", ClipType.image, [1, 2, 3]);
        var result = Quote(f, "vid-2", "image_to_video", "Move",
            inputs: Inputs(("first_frame", image.Id), ("last_frame", image.Id)));
        Assert.False(result.IsError, result.Content[0].Text);
        // One disk file, one entry per role: the gateway needs distinct asset
        // ids per input, so each role uploads separately.
        Assert.Equal(2, f.Handler.Uploads);
    }

    [Fact]
    public void Quote_UnknownModel_Rejected()
    {
        using var f = new Fixture();
        var result = Quote(f, "nope-1", "text_to_video", "A cat");
        Assert.True(result.IsError);
        Assert.Contains("Unknown hosted media model", result.Content[0].Text);
    }

    [Theory]
    [InlineData("source.m4a", "audio/mp4")]
    [InlineData("source.aac", "audio/aac")]
    public void Quote_AudioContainer_UploadsWithMatchingMimeType(string fileName, string contentType)
    {
        using var f = new Fixture();
        var audio = AddAsset(f, fileName, ClipType.audio, [1, 2, 3]);
        var result = Quote(f, "aud-clean", "audio_isolation", "",
            inputs: Inputs(("source_audio", audio.Id)));
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal(contentType, f.Handler.UploadContentType);
        Assert.Equal(1, f.Handler.Uploads);
    }

    [Fact]
    public void Quote_PromptChangeReusesUpload_ButContentChangeUploadsAgain()
    {
        using var f = new Fixture();
        var image = AddAsset(f, "frame.png", ClipType.image, [1, 2, 3]);
        Assert.False(Quote(f, "vid-2", "image_to_video", "Move left", inputs: Inputs(("first_frame", image.Id))).IsError);
        Assert.False(Quote(f, "vid-2", "image_to_video", "Move right", inputs: Inputs(("first_frame", image.Id))).IsError);
        Assert.Equal(1, f.Handler.Uploads);
        File.WriteAllBytes(Path.Combine(f.Folder, "media", "frame.png"), [3, 2, 1, .. new byte[9]]);
        Assert.False(Quote(f, "vid-2", "image_to_video", "Move right", inputs: Inputs(("first_frame", image.Id))).IsError);
        Assert.Equal(2, f.Handler.Uploads);
        ToolExecutor.ResetMediaQuotes();
        Assert.False(Quote(f, "vid-2", "image_to_video", "Move right", inputs: Inputs(("first_frame", image.Id))).IsError);
        Assert.Equal(3, f.Handler.Uploads);
    }

    [Fact]
    public void Quote_UnknownOperation_Rejected()
    {
        using var f = new Fixture();
        var result = Quote(f, "vid-1", "fly_to_moon", "A cat");
        Assert.True(result.IsError);
        Assert.Contains("Unknown hosted media operation", result.Content[0].Text);
    }

    [Fact]
    public void Quote_UnavailableOperation_SurfacesReason()
    {
        using var f = new Fixture();
        var result = Quote(f, "old-1", "text_to_video", "A cat");
        Assert.True(result.IsError);
        Assert.Contains("needs adapter", result.Content[0].Text);
    }

    [Fact]
    public void Quote_UnknownSetting_Rejected()
    {
        using var f = new Fixture();
        var result = Quote(f, "vid-1", "text_to_video", "A cat",
            new JsonObject { ["provider_temperature"] = "hot" });
        Assert.True(result.IsError);
        Assert.Contains("not supported", result.Content[0].Text);
    }

    [Fact]
    public void Quote_BadSettingValue_Rejected()
    {
        using var f = new Fixture();
        var result = Quote(f, "vid-1", "text_to_video", "A cat",
            new JsonObject { ["aspect_ratio"] = "4:3" });
        Assert.True(result.IsError);
        Assert.Contains("must be one of", result.Content[0].Text);
    }

    [Fact]
    public void Quote_EmptyPrompt_RejectedForTextOp()
    {
        using var f = new Fixture();
        var result = Quote(f, "vid-1", "text_to_video", "   ");
        Assert.True(result.IsError);
        Assert.Contains("prompt is required", result.Content[0].Text);
    }

    [Fact]
    public void Quote_EmptyPrompt_AcceptedForInputOnlyOp()
    {
        using var f = new Fixture();
        var image = AddAsset(f, "src.png", ClipType.image, [1, 2, 3]);
        var result = Quote(f, "up-1", "upscale_image", "", inputs: Inputs(("reference_image", image.Id)));
        Assert.False(result.IsError, result.Content[0].Text);
    }

    [Fact]
    public void Quote_WrongRole_Rejected()
    {
        using var f = new Fixture();
        var audio = AddAsset(f, "src.mp3", ClipType.audio, [4, 5, 6]);
        var result = Quote(f, "up-1", "upscale_image", "", inputs: Inputs(("source_audio", audio.Id)));
        Assert.True(result.IsError);
        Assert.Contains("does not accept 'source_audio'", result.Content[0].Text);
    }

    [Fact]
    public void Quote_KindMismatch_Rejected()
    {
        using var f = new Fixture();
        var audio = AddAsset(f, "src.mp3", ClipType.audio, [4, 5, 6]);
        var result = Quote(f, "up-1", "upscale_image", "", inputs: Inputs(("reference_image", audio.Id)));
        Assert.True(result.IsError);
        Assert.Contains("takes image", result.Content[0].Text);
    }

    [Fact]
    public void Quote_CountViolation_Rejected()
    {
        using var f = new Fixture();
        var a = AddAsset(f, "a.png", ClipType.image, [1]);
        var b = AddAsset(f, "b.png", ClipType.image, [2]);
        var result = Quote(f, "up-1", "upscale_image", "",
            inputs: Inputs(("reference_image", a.Id), ("reference_image", b.Id)));
        Assert.True(result.IsError);
        Assert.Contains("takes 1..1", result.Content[0].Text);
    }

    [Fact]
    public void Quote_MissingRequiredInput_Rejected()
    {
        using var f = new Fixture();
        var result = Quote(f, "up-1", "upscale_image", "");
        Assert.True(result.IsError);
        Assert.Contains("takes 1..1", result.Content[0].Text);
    }

    [Fact]
    public void Quote_UnknownMediaRef_Rejected()
    {
        using var f = new Fixture();
        var result = Quote(f, "up-1", "upscale_image", "",
            inputs: Inputs(("reference_image", "NOPE")));
        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content[0].Text);
    }

    [Fact]
    public void Quote_OfflineMedia_Rejected()
    {
        using var f = new Fixture();
        var entry = new MediaManifestEntry
        {
            Id = Guid.NewGuid().ToString().ToUpperInvariant(),
            Name = "gone.png",
            Type = ClipType.image,
            Source = MediaSource.Project("media/gone.png"),
        };
        f.Session.Manifest.Entries.Add(entry);
        var result = Quote(f, "up-1", "upscale_image", "", inputs: Inputs(("reference_image", entry.Id)));
        Assert.True(result.IsError);
        Assert.Contains("offline", result.Content[0].Text);
    }

    [Fact]
    public void Quote_UnsupportedExtension_Rejected()
    {
        using var f = new Fixture();
        var bmp = AddAsset(f, "src.bmp", ClipType.image, [7, 8]);
        var result = Quote(f, "up-1", "upscale_image", "", inputs: Inputs(("reference_image", bmp.Id)));
        Assert.True(result.IsError);
        Assert.Contains("unsupported", result.Content[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    private static HostedMediaQuote MakeQuote() => new()
    {
        QuoteId = Guid.NewGuid().ToString("D"),
        Model = "vid-1",
        Operation = "text_to_video",
        Credits = 7,
        PriceVersion = "media-2026-09-18-v1",
        ExpiresAt = "2026-09-18T01:00:00Z",
        Fingerprint = "fp",
        OutputCount = 1,
        Settings = new JsonObject { ["aspect_ratio"] = "16:9" },
        Inputs = [new HostedMediaInputRef { Role = "first_frame", AssetId = Guid.Parse("11111111-1111-1111-1111-111111111111") }],
    };

    private static HostedMediaQuoteRequest MakeRequest(HostedMediaQuote quote) => new()
    {
        Model = quote.Model,
        Operation = quote.Operation,
        Prompt = "a cat",
        Settings = (JsonObject)quote.Settings!.DeepClone(),
        Inputs = [.. quote.Inputs!],
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
        changed.Model = "other";
        Assert.False(quote.Matches(changed));

        changed = MakeRequest(quote);
        changed.Operation = "image_to_video";
        Assert.False(quote.Matches(changed));

        changed = MakeRequest(quote);
        changed.Inputs = [new HostedMediaInputRef { Role = "last_frame", AssetId = quote.Inputs![0].AssetId }];
        Assert.False(quote.Matches(changed));

        changed = MakeRequest(quote);
        changed.Inputs = [new HostedMediaInputRef { Role = "first_frame", AssetId = Guid.NewGuid() }];
        Assert.False(quote.Matches(changed));

        changed = MakeRequest(quote);
        changed.Settings = new JsonObject { ["aspect_ratio"] = "9:16" };
        Assert.False(quote.Matches(changed));

        Assert.False(quote.Matches(null!));
    }

    [Fact]
    public void JobStatusMapping_CoversMediaLifecycle()
    {
        Assert.Equal(BackendGenerationStatus.Queued, new HostedMediaJob { Id = "a", Status = "queued" }.ToBackendJob().Status);
        Assert.Equal(BackendGenerationStatus.Running, new HostedMediaJob { Id = "a", Status = "running" }.ToBackendJob().Status);
        Assert.Equal(BackendGenerationStatus.Succeeded, new HostedMediaJob { Id = "a", Status = "succeeded" }.ToBackendJob().Status);
        Assert.Equal(BackendGenerationStatus.Failed, new HostedMediaJob { Id = "a", Status = "failed", Error = "provider_error" }.ToBackendJob().Status);
        Assert.Equal(BackendGenerationStatus.Failed, new HostedMediaJob { Id = "a", Status = "canceled" }.ToBackendJob().Status);
        Assert.True(new HostedMediaJob { Id = "a", Status = "succeeded" }.IsTerminal);
        Assert.False(new HostedMediaJob { Id = "a", Status = "running" }.IsTerminal);
    }

    [Fact]
    public void IsMediaOperation_CoversRoster()
    {
        foreach (var op in HostedMediaOperations.All)
            Assert.True(ToolExecutor.IsMediaOperation(op), op);
        Assert.False(ToolExecutor.IsMediaOperation("generate_image"));
        Assert.False(ToolExecutor.IsMediaOperation("edit_image"));
        Assert.False(ToolExecutor.IsMediaOperation(null));
        Assert.False(ToolExecutor.IsMediaOperation(""));
        Assert.Equal("video", ToolExecutor.MediaCategoryOf("text_to_video"));
        Assert.Equal("audio", ToolExecutor.MediaCategoryOf("text_to_speech"));
        Assert.Equal("upscale", ToolExecutor.MediaCategoryOf("upscale_image"));
        Assert.Null(ToolExecutor.MediaCategoryOf("generate_image"));
    }

    [Fact]
    public void IsMediaJob_RoutesOnlyMediaOps()
    {
        static MediaManifestEntry Job(string? op) => new()
        {
            Id = "x", Name = "x", Type = ClipType.video,
            Source = MediaSource.Project("media/x.mp4"),
            GenerationInput = op is null ? null : new GenerationInput { Operation = op },
        };
        Assert.True(ToolExecutor.IsMediaJob(Job("text_to_video")));
        Assert.True(ToolExecutor.IsMediaJob(Job("upscale_image")));
        Assert.False(ToolExecutor.IsMediaJob(Job("generate_image")));
        Assert.False(ToolExecutor.IsMediaJob(Job(null)));
    }

    [Fact]
    public void StableIds_AreDeterministicAndKindScoped()
    {
        Assert.Equal(ToolExecutor.SubmitMediaKeyFor("p"), ToolExecutor.SubmitMediaKeyFor("p"));
        Assert.NotEqual(ToolExecutor.SubmitMediaKeyFor("a"), ToolExecutor.SubmitMediaKeyFor("b"));
        Assert.Equal(ToolExecutor.SiblingMediaOutputId("job:1", 1), ToolExecutor.SiblingMediaOutputId("job:1", 1));
        Assert.NotEqual(ToolExecutor.SiblingMediaOutputId("job:1", 1), ToolExecutor.SiblingMediaOutputId("job:1", 2));
        Assert.NotEqual(ToolExecutor.SiblingMediaOutputId("job:1", 1), ToolExecutor.SiblingOutputId("job:1", 1));
    }

    [Fact]
    public async Task GetMediaCatalogAsync_ReturnsModelsWithAvailability()
    {
        using var f = new Fixture();
        var catalog = await ToolExecutor.GetMediaCatalogAsync();
        Assert.Contains(catalog.Models, m => m.Id == "vid-1");
        var vid = catalog.Get("vid-1")!;
        Assert.True(catalog.IsAvailable(vid, "text_to_video"));
        var old = catalog.Get("old-1")!;
        Assert.False(catalog.IsAvailable(old, "text_to_video"));
        Assert.Contains("needs adapter", catalog.UnavailabilityReason(old, "text_to_video")!);
    }

    [Fact]
    public void ListModels_IncludesMediaRows()
    {
        using var f = new Fixture();
        ToolExecutor.GenerationServiceOverride = new GenerationService(new FakeGenerationClient());
        var result = ToolExecutor.Execute("list_models", new JsonObject());
        Assert.False(result.IsError, result.Content[0].Text);
        var text = result.Content[0].Text;
        Assert.Contains("vid-1", text);
        Assert.Contains("aud-1", text);
        Assert.Contains("up-1", text);
    }

    // WP-331: a value of the wrong JSON type is converted only when the conversion is exact, so the gateway's
    // readers (a choice is a string, a number a number) accept it.
    [Fact]
    public void Quote_CoercesExactSettingTypes()
    {
        using var f = new Fixture();
        var image = AddAsset(f, "frame.png", ClipType.image, [1, 2, 3]);
        var result = Quote(f, "vid-2", "image_to_video", "Move",
            new JsonObject { ["duration"] = "7", ["cfg"] = " 0.25 ", ["prompt_expansion"] = "false", ["negative_prompt"] = "blur" },
            Inputs(("first_frame", image.Id)));
        Assert.False(result.IsError, result.Content[0].Text);
        var sent = f.Handler.LastQuoteSettings!;
        Assert.Equal(System.Text.Json.JsonValueKind.Number, sent["duration"]!.GetValueKind());
        Assert.Equal(7, sent["duration"]!.GetValue<long>());
        Assert.Equal(0.25m, sent["cfg"]!.GetValue<decimal>());
        Assert.False(sent["prompt_expansion"]!.GetValue<bool>());
        Assert.Equal("blur", sent["negative_prompt"]!.GetValue<string>());

        Assert.False(Quote(f, "vid-1", "text_to_video", "A cat", JsonNode.Parse("""{"aspect_ratio":"9:16"}""")!.AsObject()).IsError);
        Assert.Equal("9:16", f.Handler.LastQuoteSettings!["aspect_ratio"]!.GetValue<string>());
        // A number for a choice becomes its text and must still be listed; an inexact value is left for the gateway.
        Assert.False(Quote(f, "vid-2", "image_to_video", "Move", JsonNode.Parse("""{"duration":"abc"}""")!.AsObject(),
            Inputs(("first_frame", image.Id))).IsError);
        Assert.Equal("abc", f.Handler.LastQuoteSettings!["duration"]!.GetValue<string>());
        var refused = Quote(f, "vid-2", "image_to_video", "Move", JsonNode.Parse("""{"resolution":720}""")!.AsObject(),
            Inputs(("first_frame", image.Id)));
        Assert.True(refused.IsError);
        Assert.Contains("must be one of [720p, 1080p]", refused.Content[0].Text);
    }

    // WP-331: the agent sees each model's settings and input roles before it quotes.
    [Fact]
    public void ListModels_ShowsMediaSettingsAndInputRoles()
    {
        using var f = new Fixture();
        ToolExecutor.GenerationServiceOverride = new GenerationService(new FakeGenerationClient());
        var result = ToolExecutor.Execute("list_models", new JsonObject());
        Assert.False(result.IsError, result.Content[0].Text);
        var models = JsonNode.Parse(result.Content[0].Text!)!["models"]!.AsArray();
        var vid = models.Single(m => m!["id"]!.GetValue<string>() == "vid-1")!;
        var setting = vid["settings"]!.AsArray().Single()!;
        Assert.Equal("aspect_ratio", setting["name"]!.GetValue<string>());
        Assert.Equal(["16:9", "9:16"], setting["values"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("16:9", setting["default"]!.GetValue<string>());
        Assert.Equal("choice", setting["type"]!.GetValue<string>());
        Assert.Null(vid["inputs"]);
        var vid2 = models.Single(m => m!["id"]!.GetValue<string>() == "vid-2")!;
        // The gateway's scalar rules (MediaDefaultsTests): free-form settings carry a typed default and their rule.
        var byName = vid2["settings"]!.AsArray().ToDictionary(s => s!["name"]!.GetValue<string>(), s => s!);
        Assert.Equal("number", byName["duration"]["type"]!.GetValue<string>());
        Assert.Equal(System.Text.Json.JsonValueKind.Number, byName["duration"]["default"]!.GetValueKind());
        Assert.Equal(5, byName["duration"]["default"]!.GetValue<long>());
        Assert.Equal("Integer 2..30.", byName["duration"]["description"]!.GetValue<string>());
        Assert.Equal(0.5m, byName["cfg"]["default"]!.GetValue<decimal>());
        Assert.Equal("boolean", byName["prompt_expansion"]["type"]!.GetValue<string>());
        Assert.True(byName["prompt_expansion"]["default"]!.GetValue<bool>());
        Assert.Equal("string", byName["negative_prompt"]["type"]!.GetValue<string>());
        Assert.True(byName["negative_prompt"]["optional"]!.GetValue<bool>());
        Assert.Null(byName["negative_prompt"]["default"]);
        Assert.Null(byName["duration"]["values"]);
        var roles = vid2["inputs"]!["image_to_video"]!.AsArray();
        Assert.Equal(["first_frame", "last_frame"], roles.Select(r => r!["role"]!.GetValue<string>()));
        Assert.Equal([1, 0], roles.Select(r => r!["min"]!.GetValue<int>()));
        Assert.Equal(["image"], roles[0]!["kinds"]!.AsArray().Select(k => k!.GetValue<string>()));
        // An unavailable model lists neither.
        var old = models.Single(m => m!["id"]!.GetValue<string>() == "old-1")!;
        Assert.Null(old["settings"]);
        Assert.Null(old["inputs"]);
    }
}
