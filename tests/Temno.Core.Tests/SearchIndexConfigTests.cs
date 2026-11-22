using Temno.Media;
using Temno.Media.Search;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class SearchIndexConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "temno-search-index-" + Guid.NewGuid().ToString("N"));
    private readonly string? _prevModel;

    public SearchIndexConfigTests()
    {
        Directory.CreateDirectory(_root);
        _prevModel = Environment.GetEnvironmentVariable(SiglipModelStore.EnvironmentVariableName);
        Environment.SetEnvironmentVariable(SiglipModelStore.EnvironmentVariableName, null);
        SiglipModelStore.SetPathsForTests(
            Path.Combine(_root, "bundled"),
            Path.Combine(_root, "user"));
        Directory.CreateDirectory(Path.Combine(_root, "bundled"));
        Directory.CreateDirectory(Path.Combine(_root, "user"));
    }

    public void Dispose()
    {
        SiglipModelStore.SetPathsForTests(null, null);
        Environment.SetEnvironmentVariable(SiglipModelStore.EnvironmentVariableName, _prevModel);
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    [Fact]
    public void CreateDefault_MissingOnnxFile_UsesConceptSpaceReady()
    {
        var embedder = SearchIndexConfig.CreateDefault(new SearchIndexCreateOptions
        {
            PreferOnnx = true,
            ModelPath = Path.Combine(_root, "missing", SiglipModelStore.DefaultFileName),
        });

        Assert.Equal(SemanticConceptEmbedder.ModelIdValue, embedder.ModelId);
        Assert.Equal(SemanticEmbedderStatus.Ready, embedder.Status);
        Assert.Equal(SemanticSearchMode.Fallback, embedder.Mode);
        Assert.False(embedder.EncodesPixels);
        Assert.Contains("concept-space-v1", embedder.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDefault_NoResolvedFile_UsesConceptSpaceReady()
    {
        var embedder = SearchIndexConfig.CreateDefault(new SearchIndexCreateOptions
        {
            PreferOnnx = true,
        });

        Assert.Equal(SemanticConceptEmbedder.ModelIdValue, embedder.ModelId);
        Assert.Equal(SemanticEmbedderStatus.Ready, embedder.Status);
        Assert.Equal(SemanticSearchMode.Fallback, embedder.Mode);
    }

    [Fact]
    public void CreateDefault_InjectedEmbedder_KeepsModelIdAndStatus()
    {
        var stub = new StubSemanticEmbedder
        {
            ModelId = SearchIndexConfig.TargetModelId,
            Status = SemanticEmbedderStatus.Ready,
            Mode = SemanticSearchMode.Model,
            EncodesPixels = true,
            Message = "injected",
        };

        var embedder = SearchIndexConfig.CreateDefault(new SearchIndexCreateOptions
        {
            PreferOnnx = true,
            InjectedEmbedder = stub,
        });

        Assert.Same(stub, embedder);
        Assert.Equal(SearchIndexConfig.TargetModelId, embedder.ModelId);
        Assert.Equal(SemanticEmbedderStatus.Ready, embedder.Status);
        Assert.Equal(SemanticSearchMode.Model, embedder.Mode);
        Assert.True(embedder.EncodesPixels);
    }

    [Fact]
    public void CreateDefault_StubOnnxPath_FallsBackAndIsDistinguishableFromInjected()
    {
        var stubPath = Path.Combine(_root, "stub.onnx");
        File.WriteAllBytes(stubPath, [1, 2, 3, 4]);

        var factory = SearchIndexConfig.CreateDefault(new SearchIndexCreateOptions
        {
            PreferOnnx = true,
            ModelPath = stubPath,
            OnnxFactory = path => new StubSemanticEmbedder
            {
                ModelId = SearchIndexConfig.TargetModelId,
                Status = SemanticEmbedderStatus.Failed,
                Mode = SemanticSearchMode.Unavailable,
                EncodesPixels = false,
                Message = $"could not encode {path}",
            },
        });

        Assert.Equal(SemanticConceptEmbedder.ModelIdValue, factory.ModelId);
        Assert.Equal(SemanticEmbedderStatus.Ready, factory.Status);
        Assert.Equal(SemanticSearchMode.Fallback, factory.Mode);
        Assert.Contains("could not encode", factory.Message, StringComparison.Ordinal);

        var injected = SearchIndexConfig.CreateDefault(new SearchIndexCreateOptions
        {
            InjectedEmbedder = new StubSemanticEmbedder
            {
                ModelId = SearchIndexConfig.TargetModelId,
                Status = SemanticEmbedderStatus.Ready,
                Mode = SemanticSearchMode.Model,
                EncodesPixels = true,
            },
        });

        Assert.Equal(SearchIndexConfig.TargetModelId, injected.ModelId);
        Assert.Equal(SemanticEmbedderStatus.Ready, injected.Status);
        Assert.NotEqual(factory.ModelId, injected.ModelId);
        Assert.NotEqual(factory.Mode, injected.Mode);
    }

    [Fact]
    public void CreateDefault_CoreMlPackagePath_DoesNotPretendToBeOnnx()
    {
        var embedder = SearchIndexConfig.CreateDefault(new SearchIndexCreateOptions
        {
            PreferOnnx = true,
            ModelPath = Path.Combine(_root, "ImageEncoder.mlpackage"),
        });

        Assert.Equal(SemanticConceptEmbedder.ModelIdValue, embedder.ModelId);
        Assert.Equal(SemanticEmbedderStatus.Ready, embedder.Status);
        Assert.Contains("CoreML-only", embedder.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateDefault_OnnxFactoryReadyPixelEmbedder_SelectsModel()
    {
        var stubPath = Path.Combine(_root, "siglip2-base-patch16-256.onnx");
        File.WriteAllBytes(stubPath, [1]);

        var embedder = SearchIndexConfig.CreateDefault(new SearchIndexCreateOptions
        {
            PreferOnnx = true,
            ModelPath = stubPath,
            OnnxFactory = _ => new StubSemanticEmbedder
            {
                ModelId = SearchIndexConfig.TargetModelId,
                Status = SemanticEmbedderStatus.Ready,
                Mode = SemanticSearchMode.Model,
                EncodesPixels = true,
                Message = "pixel onnx",
            },
        });

        Assert.Equal(SearchIndexConfig.TargetModelId, embedder.ModelId);
        Assert.Equal(SemanticEmbedderStatus.Ready, embedder.Status);
        Assert.True(embedder.EncodesPixels);
        Assert.Equal(SemanticSearchMode.Model, embedder.Mode);
    }
}

internal sealed class StubSemanticEmbedder : ISemanticVisualEmbedder
{
    public string ModelId { get; init; } = SemanticConceptEmbedder.ModelIdValue;
    public int EmbeddingDim { get; init; } = 2;
    public SemanticEmbedderStatus Status { get; init; } = SemanticEmbedderStatus.Ready;
    public SemanticSearchMode Mode { get; init; } = SemanticSearchMode.Fallback;
    public string? Message { get; init; }
    public bool EncodesPixels { get; init; }
    public Func<string, float[]?>? Text { get; init; }
    public Func<VisualSignature, float[]?>? Signature { get; init; }
    public Func<VideoFrame, float[]?>? Frame { get; init; }

    public float[]? EncodeText(string text) => Text?.Invoke(text);
    public float[]? EncodeSignature(VisualSignature signature) => Signature?.Invoke(signature);
    public float[]? EncodeFrame(VideoFrame frame) => Frame?.Invoke(frame);
}
