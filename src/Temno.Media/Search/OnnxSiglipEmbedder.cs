using Microsoft.ML.OnnxRuntime;

namespace Temno.Media.Search;

/// <summary>
/// Attempts to open a Temno SigLIP2 ONNX with OnnxRuntime. EncodeText/EncodeFrame
/// stay unimplemented until a traced image tower + tokenizer ship — this type never
/// invents 768-d vectors. A CoreML <c>.mlpackage</c> is rejected outright.
/// </summary>
public sealed class OnnxSiglipEmbedder : ISemanticVisualEmbedder, IDisposable
{
    public const string ExpectedFileName = SiglipModelStore.DefaultFileName;

    private InferenceSession? _session;

    private OnnxSiglipEmbedder(
        SemanticEmbedderStatus status,
        string message,
        InferenceSession? session = null)
    {
        Status = status;
        Message = message;
        _session = session;
        EncodesPixels = status == SemanticEmbedderStatus.Ready && session is not null;
        Mode = EncodesPixels ? SemanticSearchMode.Model : SemanticSearchMode.Unavailable;
    }

    public string ModelId => SearchIndexConfig.TargetModelId;
    public int EmbeddingDim => SearchIndexConfig.TargetEmbeddingDim;
    public SemanticEmbedderStatus Status { get; }
    public SemanticSearchMode Mode { get; }
    public string? Message { get; }
    public bool EncodesPixels { get; }

    public static OnnxSiglipEmbedder Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new OnnxSiglipEmbedder(
                SemanticEmbedderStatus.NotInstalled,
                "No SigLIP ONNX path. Set TEMNO_SIGLIP_MODEL to "
                + ExpectedFileName
                + " or copy it to %LocalAppData%\\Temno\\siglip\\.");
        }

        var trimmed = path.Trim();
        if (SiglipModelStore.IsCoreMlPackage(trimmed))
        {
            return new OnnxSiglipEmbedder(
                SemanticEmbedderStatus.NotInstalled,
                "HuggingFace palmier-io/siglip2-base-coreml is CoreML-only. "
                + "Do not load .mlpackage as ONNX. Export ImageEncoder + TextEncoder + tokenizer "
                + "and set TEMNO_SIGLIP_MODEL to "
                + ExpectedFileName
                + ".");
        }

        if (!File.Exists(trimmed))
        {
            return new OnnxSiglipEmbedder(
                SemanticEmbedderStatus.NotInstalled,
                $"SigLIP ONNX not found at '{trimmed}'. Expected {ExpectedFileName}.");
        }

        if (!SiglipModelStore.IsOnnxFile(trimmed))
        {
            return new OnnxSiglipEmbedder(
                SemanticEmbedderStatus.Failed,
                $"'{trimmed}' is not a .onnx file. Expected {ExpectedFileName}.");
        }

        InferenceSession? session = null;
        try
        {
            session = new InferenceSession(trimmed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new OnnxSiglipEmbedder(
                SemanticEmbedderStatus.Failed,
                $"Could not open '{trimmed}' as ONNX: {ex.Message}");
        }

        // Session opened, but a correct SigLIP encode needs tokenizer tokens and a
        // 256×256 squash-resize in [-1,1] on the image tower. No public Temno ONNX
        // contract exists yet — dispose and report Failed so the factory falls back.
        session.Dispose();
        return new OnnxSiglipEmbedder(
            SemanticEmbedderStatus.Failed,
            "Opened "
            + Path.GetFileName(trimmed)
            + " as ONNX, but EncodeText/EncodeFrame are unimplemented until Palmier ships "
            + "a tokenizer + 256×256 image tower. HuggingFace palmier-io/siglip2-base-coreml "
            + "is CoreML-only. Falling back to concept-space-v1.");
    }

    public float[]? EncodeText(string text) => null;

    public float[]? EncodeSignature(VisualSignature signature) => null;

    public float[]? EncodeFrame(VideoFrame frame) => null;

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
    }
}
