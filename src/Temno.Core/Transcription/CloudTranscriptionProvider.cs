using Temno.Core.Account;

namespace Temno.Core.Transcription;

public sealed class CloudTranscriptionProvider : ITranscriptionProvider
{
    private readonly IPcmWavPreparer _audio;
    private readonly Func<HostedTranscriptionClient> _client;
    private readonly IProgress<string>? _progress;
    private readonly CancellationToken _lifetime;

    public CloudTranscriptionProvider(
        IPcmWavPreparer audio,
        Func<HostedTranscriptionClient> client,
        IProgress<string>? progress = null,
        CancellationToken lifetime = default)
    {
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _progress = progress;
        _lifetime = lifetime;
    }

    public TranscriptionProvider Provider => TranscriptionProvider.cloud;

    public async Task<TranscriptionResult> TranscribeAsync(
        string mediaPath,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
            throw new ArgumentException("Media path is required.", nameof(mediaPath));
        if (!HostedEndpoints.GatewayConfigured())
            throw new HostedApiException("Cloud transcription is not configured.", code: "transcription_not_ready");
        if (!HostedRuntime.IsSignedIn)
            throw new HostedApiException("Sign in to use cloud transcription.", 401, "authentication_required");

        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime);
        var sourcePath = Path.GetFullPath(mediaPath);
        var sourceIdentity = TranscriptDiskCache.KeyForPath(sourcePath);
        var wav = await _audio.PrepareAsync(sourcePath, run.Token).ConfigureAwait(false);
        if (TranscriptDiskCache.KeyForPath(sourcePath) != sourceIdentity)
            throw new IOException("The transcription source changed while preparing audio.");
        var client = _client();
        // Existing jobs must remain recoverable when new admission is temporarily disabled.
        return await client.TranscribeAsync(wav, language ?? "en", _progress, run.Token, sourcePath, result =>
        {
            if (!File.Exists(sourcePath) || TranscriptDiskCache.KeyForPath(sourcePath) != sourceIdentity)
                throw new IOException("The transcription source changed before its result arrived.");
            TranscriptDiskCache.Store(sourcePath, result);
        }, sourceIdentity).ConfigureAwait(false);
    }
}
