using System.Text.Json.Nodes;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Mcp;
using Temno.Media;
using Temno.Media.Search;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class VisualSearchTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "TemnoVisualSearch_" + Guid.NewGuid().ToString("N"));

    public VisualSearchTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        ToolExecutor.VisualSearchServiceOverride = null;
        ProjectHub.ActiveSession = null;
        try
        {
            if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // A test runner can still have a cache handle open on Windows.
        }
    }

    [Fact]
    public void Cache_MeasureBytes_CountsTheIndexOnDiskAndIsZeroWhenMissingOrCleared()
    {
        // WP-397: Settings → Search shows this size and disables Clear index at zero.
        var cache = new VisualSearchCache(Path.Combine(_tempRoot, "measure"));
        Assert.Equal(0, cache.MeasureBytes());

        cache.Save("scope-a", new VisualSearchIndex());
        cache.Save("scope-b", new VisualSearchIndex());
        var expected = new DirectoryInfo(cache.RootDirectory).EnumerateFiles().Sum(f => f.Length);
        Assert.True(expected > 0);
        Assert.Equal(expected, cache.MeasureBytes());

        Temno.Media.Cache.DiskCache.ClearTree(cache.RootDirectory);
        Assert.Equal(0, cache.MeasureBytes());
    }

    [Fact]
    public void Signature_IsDeterministicAndCapturesColorLuminanceContrastAndDistribution()
    {
        using var frame = new VideoFrame(2, 1, new byte[]
        {
            255, 0, 0, 255,
            0, 0, 255, 255,
        }, "rgba");

        var first = VisualSignature.FromFrame(frame);
        var second = VisualSignature.FromFrame(frame);

        Assert.Equal(0.5, first.MeanRed, 6);
        Assert.Equal(0.0, first.MeanGreen, 6);
        Assert.Equal(0.5, first.MeanBlue, 6);
        Assert.True(first.MeanLuminance > 0);
        Assert.True(first.LuminanceContrast > 0);
        Assert.Equal(1.0, first.ColorDistribution.Sum(), 6);
        Assert.Equal(first.MeanRed, second.MeanRed, 12);
        Assert.Equal(first.MeanLuminance, second.MeanLuminance, 12);
        Assert.Equal(first.LuminanceContrast, second.LuminanceContrast, 12);
        Assert.Equal(first.ColorDistribution, second.ColorDistribution);
    }

    [Fact]
    public async Task LocalSearch_RanksHexNamedBrightnessAndSimilarQueriesWithSampleTimestamps()
    {
        var bluePath = CreateSource("blue.mp4", "blue");
        var cyanPath = CreateSource("cyan.png", "cyan");
        var whitePath = CreateSource("white.png", "white");
        var blackPath = CreateSource("black.png", "black");
        var source = new FakeFrameSource((path, _) => path switch
        {
            var value when value == bluePath => Frame(0, 0, 255),
            var value when value == cyanPath => Frame(0, 72, 255),
            var value when value == whitePath => Frame(255, 255, 255),
            var value when value == blackPath => Frame(0, 0, 0),
            _ => throw new FileNotFoundException(),
        });
        var service = new VisualSearchService(source, new VisualSearchCache(Path.Combine(_tempRoot, "cache")));
        var assets = new[]
        {
            Asset("blue-ref", bluePath, ClipType.video, 9),
            Asset("cyan-near", cyanPath, ClipType.image),
            Asset("bright", whitePath, ClipType.image),
            Asset("dark", blackPath, ClipType.image),
        };

        var named = await service.SearchAsync(new VisualSearchRequest("ranked", assets, "blue", SampleFrameCount: 3));
        var hex = await service.SearchAsync(new VisualSearchRequest("ranked", assets, "#ffffff", SampleFrameCount: 3));
        var bright = await service.SearchAsync(new VisualSearchRequest("ranked", assets, "bright", SampleFrameCount: 3));
        var similar = await service.SearchAsync(new VisualSearchRequest("ranked", assets, "similar:blue-ref", SampleFrameCount: 3));

        Assert.Equal("ok", named.Status);
        Assert.Equal("blue-ref", named.Results[0].MediaRef);
        Assert.Equal(1.5, named.Results[0].TimestampSeconds, 6);
        Assert.Equal("bright", hex.Results[0].MediaRef);
        Assert.Equal("bright", bright.Results[0].MediaRef);
        Assert.Equal("cyan-near", similar.Results[0].MediaRef);
        Assert.DoesNotContain(similar.Results, hit => hit.MediaRef == "blue-ref");
    }

    [Fact]
    public async Task Search_UsesPerUserCacheAndInvalidatesWhenSourceFingerprintChanges()
    {
        var package = Path.Combine(_tempRoot, "Project.palmier");
        var mediaDirectory = Path.Combine(package, "media");
        Directory.CreateDirectory(mediaDirectory);
        var sourcePath = Path.Combine(mediaDirectory, "asset.png");
        File.WriteAllText(sourcePath, "first bytes");
        var cacheDirectory = Path.Combine(_tempRoot, "per-user-cache");
        var source = new FakeFrameSource((_, _) => Frame(255, 0, 0));
        var assets = new[] { Asset("red", sourcePath, ClipType.image) };
        var request = new VisualSearchRequest("project:" + package, assets, "red");

        var first = await new VisualSearchService(source, new VisualSearchCache(cacheDirectory)).SearchAsync(request);
        Assert.Equal(1, first.IndexedAssets);
        Assert.Single(source.Calls);
        Assert.True(File.Exists(new VisualSearchCache(cacheDirectory).GetCachePath(request.CacheScope)));
        Assert.Empty(Directory.GetFiles(package, "visual-index-*.json", SearchOption.AllDirectories));

        var second = await new VisualSearchService(source, new VisualSearchCache(cacheDirectory)).SearchAsync(request);
        Assert.Equal(1, second.CachedAssets);
        Assert.Single(source.Calls);

        File.WriteAllText(sourcePath, "second bytes with different content");
        var third = await new VisualSearchService(source, new VisualSearchCache(cacheDirectory)).SearchAsync(request);
        Assert.Equal(1, third.InvalidatedAssets);
        Assert.Equal(2, source.Calls.Count);
    }

    [Fact]
    public async Task Search_SkipsOfflineAndNonVisualMediaWithoutDecoding()
    {
        var source = new FakeFrameSource((_, _) => Frame(255, 0, 0));
        var service = new VisualSearchService(source, new VisualSearchCache(Path.Combine(_tempRoot, "cache")));
        var assets = new[]
        {
            Asset("offline", Path.Combine(_tempRoot, "missing.png"), ClipType.image),
            Asset("audio", CreateSource("sound.wav", "audio"), ClipType.audio),
        };

        var response = await service.SearchAsync(new VisualSearchRequest("offline", assets, "red"));

        Assert.Equal("no_results", response.Status);
        Assert.Equal(1, response.SkippedOffline);
        Assert.Equal(1, response.SkippedNonVisual);
        Assert.Empty(source.Calls);
    }

    [Fact]
    public void Parser_AcceptsCompactVisualGrammarAndFreeTextSemantic()
    {
        Assert.True(VisualSearchQueryParser.TryParse("dark #0f0", out var compact, out var compactError));
        Assert.Null(compactError);
        Assert.NotNull(compact);
        Assert.Equal(VisualQueryKind.Visual, compact.Kind);
        Assert.NotNull(compact.TargetColor);
        Assert.NotNull(compact.TargetLuminance);

        Assert.True(VisualSearchQueryParser.TryParse("red car on a road", out var semantic, out var semanticError));
        Assert.Null(semanticError);
        Assert.Equal(VisualQueryKind.Semantic, semantic!.Kind);
        Assert.Equal("red car on a road", semantic.TextQuery);
    }

    [Fact]
    public async Task SemanticSearch_RanksBlueSceneCueAboveRed()
    {
        var bluePath = CreateSource("sem-blue.png", "blue");
        var redPath = CreateSource("sem-red.png", "red");
        var source = new FakeFrameSource((path, _) => path == bluePath ? Frame(0, 0, 255) : Frame(255, 0, 0));
        var service = new VisualSearchService(source, new VisualSearchCache(Path.Combine(_tempRoot, "sem-cache")));
        var assets = new[]
        {
            Asset("blue-sky", bluePath, ClipType.image),
            Asset("red-fire", redPath, ClipType.image),
        };

        var response = await service.SearchAsync(new VisualSearchRequest("sem", assets, "blue sky"));
        Assert.Equal("ok", response.Status);
        Assert.Equal("semantic", response.QueryKind);
        Assert.Equal("blue-sky", response.Results[0].MediaRef);
        Assert.Equal("concept-space-v1", response.EmbedderModelId);
        Assert.Equal("fallback", response.IndexMode);
        Assert.Equal("fallback", response.IndexStatus);
        Assert.Contains("SigLIP2 ONNX not installed", response.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticSearch_UnavailableEmbedder_DoesNotSearch()
    {
        var path = CreateSource("unavail.png", "x");
        var source = new FakeFrameSource((_, _) => Frame(255, 0, 0));
        var service = new VisualSearchService(
            source,
            new VisualSearchCache(Path.Combine(_tempRoot, "unavail-cache")),
            new StubSemanticEmbedder
            {
                ModelId = SearchIndexConfig.TargetModelId,
                Status = SemanticEmbedderStatus.Failed,
                Mode = SemanticSearchMode.Unavailable,
                Message = "onnx load failed",
            });

        var response = await service.SearchAsync(new VisualSearchRequest(
            "unavail",
            [Asset("clip", path, ClipType.image)],
            "red car"));

        Assert.Equal("model_not_installed", response.Status);
        Assert.Equal("unavailable", response.IndexMode);
        Assert.Equal(SearchIndexConfig.TargetModelId, response.EmbedderModelId);
        Assert.Empty(response.Results);
    }

    [Fact]
    public void OnnxSiglipEmbedder_ConvWithTooFewInputDimensions_FailsInShapeInference()
    {
        // CVE-2026-14647: ONNX's Conv shape inference before onnx 1.22 accepted a 2-D input and then
        // read the input shape past its end. ONNX Runtime runs that inference while it opens a model,
        // so a hostile .onnx at the SigLIP path reached it. ORT 1.30 vendors onnx 1.22, which refuses.
        var path = Path.Combine(_tempRoot, "hostile-conv.onnx");
        File.WriteAllBytes(path, HostileConvModel());

        using var embedder = OnnxSiglipEmbedder.Load(path);

        Assert.Equal(SemanticEmbedderStatus.Failed, embedder.Status);
        Assert.Contains("Input tensor must have at least 3 dimensions", embedder.Message, StringComparison.Ordinal);
    }

    // A ModelProto in protobuf wire format: Conv (opset 11) on x:[1,1] with weights w:[1,1,3].
    private static byte[] HostileConvModel()
    {
        static byte[] Varint(ulong value)
        {
            var bytes = new List<byte>();
            do
            {
                var b = (byte)(value & 0x7F);
                value >>= 7;
                bytes.Add(value == 0 ? b : (byte)(b | 0x80));
            }
            while (value != 0);
            return [.. bytes];
        }

        static byte[] Field(int number, byte[] payload) =>
            [.. Varint((ulong)(number << 3 | 2)), .. Varint((ulong)payload.Length), .. payload];
        static byte[] Text(int number, string value) => Field(number, System.Text.Encoding.UTF8.GetBytes(value));
        static byte[] Integer(int number, long value) => [.. Varint((ulong)(number << 3)), .. Varint((ulong)value)];
        static byte[] Value(int number, string name, params long[] dims)
        {
            var shape = dims.SelectMany(d => Field(1, Integer(1, d))).ToArray();
            byte[] tensor = dims.Length == 0 ? Integer(1, 1) : [.. Integer(1, 1), .. Field(2, shape)];
            return Field(number, [.. Text(1, name), .. Field(2, Field(1, tensor))]);
        }

        var node = Field(1, [.. Text(1, "x"), .. Text(1, "w"), .. Text(2, "y"), .. Text(4, "Conv")]);
        var graph = Field(7, [.. node, .. Text(2, "g"), .. Value(11, "x", 1, 1), .. Value(11, "w", 1, 1, 3), .. Value(12, "y")]);
        return [.. Integer(1, 7), .. graph, .. Field(8, [.. Text(1, ""), .. Integer(2, 11)])];
    }

    [Fact]
    public async Task SemanticSearch_OnnxPixelEmbedder_UsesMacCutoffAndModelId()
    {
        var strongPath = CreateSource("strong.png", "strong");
        var weakPath = CreateSource("weak.png", "weak");
        var source = new FakeFrameSource((path, _) => path == strongPath ? Frame(0, 0, 255) : Frame(255, 0, 0));
        var service = new VisualSearchService(
            source,
            new VisualSearchCache(Path.Combine(_tempRoot, "onnx-cache")),
            new StubSemanticEmbedder
            {
                ModelId = SearchIndexConfig.TargetModelId,
                Status = SemanticEmbedderStatus.Ready,
                Mode = SemanticSearchMode.Model,
                EncodesPixels = true,
                Text = _ => [1f, 0f],
                Frame = frame => frame.Data[2] == 255 ? [1f, 0f] : [0.6f, 0.8f],
            });

        var response = await service.SearchAsync(new VisualSearchRequest(
            "onnx",
            [Asset("strong", strongPath, ClipType.image), Asset("weak", weakPath, ClipType.image)],
            "blue sky"));

        Assert.Equal("ok", response.Status);
        Assert.Equal(SearchIndexConfig.TargetModelId, response.EmbedderModelId);
        Assert.Equal("model", response.IndexMode);
        Assert.Equal("model", response.IndexStatus);
        Assert.Equal("strong", response.Results[0].MediaRef);
        Assert.DoesNotContain(response.Results, hit => hit.MediaRef == "weak");
    }

    [Fact]
    public async Task Search_CancelsBeforeItSamplesFrames()
    {
        var sourcePath = CreateSource("cancel.png", "cancel");
        var source = new FakeFrameSource((_, _) => Frame(255, 0, 0));
        var service = new VisualSearchService(source, new VisualSearchCache(Path.Combine(_tempRoot, "cache")));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SearchAsync(
            new VisualSearchRequest("cancel", new[] { Asset("cancel", sourcePath, ClipType.image) }, "red"),
            cancellation.Token));
        Assert.Empty(source.Calls);
    }

    [Fact]
    public void McpSearchMedia_ReturnsStructuredLocalVisualAndSpokenStatus()
    {
        var sourcePath = CreateSource("mcp.png", "mcp");
        var source = new FakeFrameSource((_, _) => Frame(255, 0, 0));
        ToolExecutor.VisualSearchServiceOverride = new VisualSearchService(
            source,
            new VisualSearchCache(Path.Combine(_tempRoot, "cache")));

        var session = ProjectSession.CreateNew();
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "red-image",
            Name = "Red image",
            Type = ClipType.image,
            Source = MediaSource.External(sourcePath),
        });
        ProjectHub.ActiveSession = session;

        var visual = Read(ToolExecutor.Execute("search_media", new JsonObject
        {
            ["query"] = "#ff0000",
            ["scope"] = "visual",
        }));
        var both = Read(ToolExecutor.Execute("search_media", new JsonObject
        {
            ["query"] = "red",
            ["scope"] = "both",
        }));
        var spoken = Read(ToolExecutor.Execute("search_media", new JsonObject
        {
            ["query"] = "anything said",
            ["scope"] = "spoken",
        }));
        var semantic = Read(ToolExecutor.Execute("search_media", new JsonObject
        {
            ["query"] = "red car",
            ["scope"] = "visual",
        }));

        Assert.Equal("ok", visual["status"]?.GetValue<string>());
        Assert.Equal("ok", visual["visualStatus"]?.GetValue<string>());
        Assert.Equal("not_requested", visual["spokenStatus"]?.GetValue<string>());
        Assert.True(visual["localOnly"]?.GetValue<bool>());
        Assert.Equal("red-image", visual["results"]?[0]?["mediaRef"]?.GetValue<string>());
        Assert.NotNull(visual["results"]?[0]?["timestampSeconds"]);

        // both: visual ok, spoken empty cache → partial
        Assert.Equal("partial", both["status"]?.GetValue<string>());
        Assert.Equal("ok", both["spokenStatus"]?.GetValue<string>());
        Assert.Equal("no_results", spoken["status"]?.GetValue<string>());
        // free-text Moments ranks the red image
        Assert.Equal("ok", semantic["status"]?.GetValue<string>());
        Assert.Equal("semantic", semantic["queryKind"]?.GetValue<string>());
        Assert.Equal("red-image", semantic["results"]?[0]?["mediaRef"]?.GetValue<string>());
        Assert.Equal("concept-space-v1", visual["index"]?["model"]?.GetValue<string>());
        Assert.Equal("fallback", visual["index"]?["mode"]?.GetValue<string>());
        Assert.Equal("concept-space-v1", semantic["index"]?["model"]?.GetValue<string>());
        Assert.Equal("fallback", semantic["index"]?["mode"]?.GetValue<string>());
        Assert.NotEqual("model_not_installed", semantic["status"]?.GetValue<string>());
    }

    private string CreateSource(string name, string content)
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static VisualSearchAsset Asset(string id, string path, ClipType type, double duration = 0)
        => new(id, id, type, path, duration);

    private static VideoFrame Frame(byte red, byte green, byte blue)
        => new(1, 1, new byte[] { red, green, blue, 255 }, "rgba");

    private static JsonObject Read(ToolCallResult result)
    {
        Assert.False(result.IsError, result.Content[0].Text);
        return JsonNode.Parse(result.Content[0].Text!)!.AsObject();
    }

    private sealed class FakeFrameSource : IFrameSource
    {
        private readonly Func<string, double, VideoFrame> _getFrame;

        public FakeFrameSource(Func<string, double, VideoFrame> getFrame)
        {
            _getFrame = getFrame;
        }

        public List<(string Path, double Timestamp)> Calls { get; } = [];

        public Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((path, sourceSeconds));
            return Task.FromResult(_getFrame(path, sourceSeconds));
        }
    }
}
