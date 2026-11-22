using Temno.Media;
using Temno.Media.Search;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class SiglipModelStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "temno-siglip-store-" + Guid.NewGuid().ToString("N"));
    private readonly string? _prevModel;

    public SiglipModelStoreTests()
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
    public void TryResolvePath_PrefersEnvThenPrefsThenUserThenBundled()
    {
        var envFile = Path.Combine(_root, "env.onnx");
        var prefsFile = Path.Combine(_root, "prefs.onnx");
        var userFile = Path.Combine(_root, "user", SiglipModelStore.DefaultFileName);
        var bundledFile = Path.Combine(_root, "bundled", SiglipModelStore.DefaultFileName);
        File.WriteAllBytes(envFile, [1]);
        File.WriteAllBytes(prefsFile, [2]);
        File.WriteAllBytes(userFile, [3]);
        File.WriteAllBytes(bundledFile, [4]);

        Environment.SetEnvironmentVariable(SiglipModelStore.EnvironmentVariableName, envFile);
        Assert.Equal(envFile, SiglipModelStore.TryResolvePath(prefsFile));

        Environment.SetEnvironmentVariable(SiglipModelStore.EnvironmentVariableName, null);
        Assert.Equal(prefsFile, SiglipModelStore.TryResolvePath(prefsFile));
        Assert.Equal(userFile, SiglipModelStore.TryResolvePath());

        File.Delete(userFile);
        Assert.Equal(bundledFile, SiglipModelStore.TryResolvePath());

        File.Delete(bundledFile);
        Assert.Null(SiglipModelStore.TryResolvePath());
        Assert.False(SiglipModelStore.IsReady);
    }

    [Fact]
    public void TryResolvePath_IgnoresCoreMlPackages()
    {
        var mlpackage = Path.Combine(_root, "ImageEncoder.mlpackage");
        Directory.CreateDirectory(mlpackage);
        Environment.SetEnvironmentVariable(SiglipModelStore.EnvironmentVariableName, mlpackage);
        Assert.Null(SiglipModelStore.TryResolvePath());
        Assert.True(SiglipModelStore.IsCoreMlPackage(mlpackage));
        Assert.True(SiglipModelStore.IsCoreMlPackage("tokenizer.zip"));
        Assert.False(SiglipModelStore.IsOnnxFile(mlpackage));
    }

    [Fact]
    public void OnnxSiglipEmbedder_Load_JunkFileIsFailedNotReady()
    {
        var junk = Path.Combine(_root, "junk.onnx");
        File.WriteAllBytes(junk, "not-an-onnx-graph"u8.ToArray());

        using var embedder = OnnxSiglipEmbedder.Load(junk);

        Assert.Equal(SearchIndexConfig.TargetModelId, embedder.ModelId);
        Assert.Equal(SemanticEmbedderStatus.Failed, embedder.Status);
        Assert.Equal(SemanticSearchMode.Unavailable, embedder.Mode);
        Assert.False(embedder.EncodesPixels);
        Assert.Null(embedder.EncodeText("red car"));
        Assert.Null(embedder.EncodeSignature(new VisualSignature()));
        Assert.Null(embedder.EncodeFrame(new VideoFrame(1, 1, [255, 0, 0, 255], "rgba")));
    }

    [Fact]
    public void OnnxSiglipEmbedder_Load_CoreMlPathIsNotInstalled()
    {
        using var embedder = OnnxSiglipEmbedder.Load(Path.Combine(_root, "TextEncoder.mlpackage"));

        Assert.Equal(SemanticEmbedderStatus.NotInstalled, embedder.Status);
        Assert.Contains("CoreML-only", embedder.Message, StringComparison.Ordinal);
        Assert.False(embedder.EncodesPixels);
    }

    [Fact]
    public void VisualSearchCache_SeparatesConceptAndSiglipFiles()
    {
        var cache = new VisualSearchCache(Path.Combine(_root, "cache"));
        var conceptPath = cache.GetCachePath("scope-a", SemanticConceptEmbedder.ModelIdValue);
        var siglipPath = cache.GetCachePath("scope-a", SearchIndexConfig.TargetModelId);
        Assert.NotEqual(conceptPath, siglipPath);
        Assert.Contains("concept-space-v1", conceptPath, StringComparison.Ordinal);
        Assert.Contains("siglip2-base-patch16-256", siglipPath, StringComparison.Ordinal);

        cache.Save("scope-a", new VisualSearchIndex
        {
            ModelId = SemanticConceptEmbedder.ModelIdValue,
            Entries = { ["a"] = new VisualMediaIndexEntry { MediaRef = "a" } },
        });
        var loadedSiglip = cache.Load("scope-a", SearchIndexConfig.TargetModelId);
        Assert.Empty(loadedSiglip.Entries);
        var loadedConcept = cache.Load("scope-a", SemanticConceptEmbedder.ModelIdValue);
        Assert.Contains("a", loadedConcept.Entries.Keys);
    }
}
