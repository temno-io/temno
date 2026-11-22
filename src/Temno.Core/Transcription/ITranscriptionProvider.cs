namespace Temno.Core.Transcription;

/// <summary>
/// Pluggable speech-to-text backend. Mac equivalents: local Apple SpeechAnalyzer
/// (Transcription/Transcription.swift) and cloud Convex (Transcription/CloudTranscription.swift).
/// Timestamps in the returned result are SECONDS in the source media file.
/// </summary>
public interface ITranscriptionProvider
{
    TranscriptionProvider Provider { get; }

    /// <param name="mediaPath">Absolute path to the media file to transcribe.</param>
    /// <param name="language">Optional BCP-47 language hint (e.g. "en-US"); null = auto-detect.</param>
    Task<TranscriptionResult> TranscribeAsync(
        string mediaPath,
        string? language = null,
        CancellationToken cancellationToken = default);
}
