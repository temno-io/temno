namespace Temno.Media.Search;

/// <summary>
/// Text/image embedding for free-text visual search (Moments).
/// Mac uses SigLIP2 CoreML; Windows ships a labelled concept-space fallback and
/// an ONNX SigLIP seam that only reports Ready when it can encode 256×256 pixels.
/// </summary>
public interface ISemanticVisualEmbedder
{
    string ModelId { get; }
    int EmbeddingDim { get; }
    SemanticEmbedderStatus Status { get; }
    SemanticSearchMode Mode { get; }
    string? Message { get; }

    /// <summary>
    /// True only when this embedder encodes squash-resized 256×256 pixels in [-1,1].
    /// Heuristic concept-space stays false and uses <see cref="EncodeSignature"/>.
    /// </summary>
    bool EncodesPixels { get; }

    /// <summary>Unit-normalized text embedding, or null if not ready.</summary>
    float[]? EncodeText(string text);

    /// <summary>Unit-normalized frame embedding from a local visual signature (heuristic only).</summary>
    float[]? EncodeSignature(VisualSignature signature);

    /// <summary>Unit-normalized embedding from decoded pixels (SigLIP only).</summary>
    float[]? EncodeFrame(VideoFrame frame);
}

public enum SemanticEmbedderStatus
{
    Ready,
    NotInstalled,
    Preparing,
    Failed,
}

/// <summary>Distinguishes installed SigLIP, labelled heuristic fallback, and cannot-search.</summary>
public enum SemanticSearchMode
{
    Model,
    Fallback,
    Unavailable,
}
