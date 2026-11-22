using Temno.Core.IO;

namespace Temno.Media.Search;

/// <summary>
/// Mac-aligned search index config plus the Windows embedder factory.
/// HuggingFace <c>palmier-io/siglip2-base-coreml</c> is CoreML-only; production
/// search stays on labelled <c>concept-space-v1</c> until a Palmier-exported
/// ONNX + tokenizer exists and <see cref="OnnxSiglipEmbedder"/> can encode
/// 256×256 [-1,1] pixels (not <see cref="VisualSignature"/>).
/// </summary>
public static class SearchIndexConfig
{
    public const string TargetModelId = "siglip2-base-patch16-256";
    public const int TargetEmbeddingDim = 768;
    public const int TargetImageSize = 256;
    public const string HostedModelBaseUrl = "https://huggingface.co/palmier-io/siglip2-base-coreml/resolve/main";
    public const string ExpectedOnnxFileName = SiglipModelStore.DefaultFileName;

    public static ISemanticVisualEmbedder CreateDefault()
        => CreateDefault(SearchIndexCreateOptions.FromEnvironment());

    public static ISemanticVisualEmbedder CreateDefault(SearchIndexCreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.InjectedEmbedder is not null)
            return options.InjectedEmbedder;

        if (!options.PreferOnnx)
        {
            return new SemanticConceptEmbedder(
                "Using concept-space-v1 (ONNX SigLIP disabled in preferences).");
        }

        var explicitPath = options.ModelPath;
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (SiglipModelStore.IsCoreMlPackage(explicitPath))
            {
                return new SemanticConceptEmbedder(
                    "HuggingFace palmier-io/siglip2-base-coreml is CoreML-only. "
                    + "Point TEMNO_SIGLIP_MODEL at a Palmier-exported "
                    + ExpectedOnnxFileName
                    + ", not a .mlpackage.");
            }

            if (!File.Exists(explicitPath))
                return new SemanticConceptEmbedder();

            return TryOnnxOrFallback(explicitPath, options);
        }

        var resolved = SiglipModelStore.TryResolvePath(options.PreferencesModelPath);
        if (resolved is null)
            return new SemanticConceptEmbedder();

        return TryOnnxOrFallback(resolved, options);
    }

    private static ISemanticVisualEmbedder TryOnnxOrFallback(string path, SearchIndexCreateOptions options)
    {
        ISemanticVisualEmbedder onnx;
        try
        {
            onnx = options.OnnxFactory is not null
                ? options.OnnxFactory(path)
                : OnnxSiglipEmbedder.Load(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SemanticConceptEmbedder(
                $"SigLIP ONNX failed to load from '{path}': {ex.Message}");
        }

        if (onnx.Status == SemanticEmbedderStatus.Ready && onnx.EncodesPixels)
            return onnx;

        var reason = string.IsNullOrWhiteSpace(onnx.Message)
            ? $"SigLIP ONNX at '{path}' cannot encode pixels yet. Using concept-space-v1."
            : onnx.Message;
        return new SemanticConceptEmbedder(reason);
    }
}

public sealed class SearchIndexCreateOptions
{
    public bool PreferOnnx { get; init; } = true;

    /// <summary>Explicit file path (tests / injected stub). Skips store search when set.</summary>
    public string? ModelPath { get; init; }

    /// <summary>From <see cref="UserPreferences.ResolveSiglipModelPath"/> when using the store.</summary>
    public string? PreferencesModelPath { get; init; }

    public ISemanticVisualEmbedder? InjectedEmbedder { get; init; }

    /// <summary>Test seam so a stub path can be distinguished without a real SigLIP graph.</summary>
    public Func<string, ISemanticVisualEmbedder>? OnnxFactory { get; init; }

    public static SearchIndexCreateOptions FromEnvironment()
    {
        var prefs = UserPreferences.Load();
        return new SearchIndexCreateOptions
        {
            PreferOnnx = prefs.PreferSiglip,
            PreferencesModelPath = prefs.ResolveSiglipModelPath(),
        };
    }
}
