using Temno.Core.Transcription;
using Temno.Media.Audio;
using Whisper.net;

namespace Temno.Media.Transcription;

/// <summary>
/// On-device Whisper (whisper.cpp via Whisper.net). Mac analog: local SpeechAnalyzer.
/// </summary>
public sealed class WhisperNetTranscriptionProvider : ITranscriptionProvider
{
    public const int SampleRate = 16_000;
    public const double WindowSeconds = 8 * 60;
    public const double OverlapSeconds = 1.0;

    private static readonly SemaphoreSlim RunGate = new(1, 1);
    private static readonly object FactoryGate = new();
    private static WhisperFactory? _factory;
    private static string? _factoryPath;

    private readonly IAudioSource _audio;
    private readonly IMediaProbe _probe;
    private readonly Func<CancellationToken, Task<string>> _ensureModel;

    public WhisperNetTranscriptionProvider(
        IAudioSource? audio = null,
        IMediaProbe? probe = null,
        Func<CancellationToken, Task<string>>? ensureModel = null)
    {
        _audio = audio ?? new FfmpegAudioSource();
        _probe = probe ?? new FfmpegMediaProbe();
        _ensureModel = ensureModel ?? WhisperModelStore.EnsureDefaultAsync;
    }

    public TranscriptionProvider Provider => TranscriptionProvider.local;

    public async Task<TranscriptionResult> TranscribeAsync(
        string mediaPath,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mediaPath))
            throw new ArgumentException("Path cannot be empty.", nameof(mediaPath));
        if (!File.Exists(mediaPath))
            throw new FileNotFoundException("Media file not found.", mediaPath);

        MediaProbeResult info;
        try
        {
            info = await _probe.ProbeAsync(mediaPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Whisper transcription needs the pinned ffprobe beside the app (or TEMNO_FFPROBE_PATH / TEMNO_FFMPEG_DIR). "
                + ex.Message,
                ex);
        }

        if (!info.HasAudio)
            throw new InvalidOperationException("The media file does not contain an audio stream.");

        var duration = Math.Max(0.01, info.DurationSeconds);
        var whisperLang = ResolveLanguage(language);

        string modelPath;
        try
        {
            modelPath = await _ensureModel(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "Whisper English model is not available. " + ex.Message,
                ex);
        }

        await RunGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WhisperFactory factory;
            try
            {
                factory = GetFactory(modelPath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not load Whisper model at " + modelPath + ". " + ex.Message,
                    ex);
            }

            var mapped = new List<WhisperMappedSegment>();
            for (double start = 0; start < duration; start += WindowSeconds - OverlapSeconds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(WindowSeconds, duration - start);
                if (length < 0.08)
                    break;

                AudioBuffer buffer;
                try
                {
                    buffer = await _audio
                        .ReadAudioAsync(mediaPath, SampleRate, 1, start, length, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (InvalidOperationException) when (start > 0)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && start == 0)
                {
                    throw new InvalidOperationException(
                        "Whisper transcription needs the pinned ffmpeg beside the app (or TEMNO_FFMPEG_PATH). "
                        + ex.Message,
                        ex);
                }

                if (buffer.Samples.Length < SampleRate / 20)
                    continue;

                using var processor = factory.CreateBuilder()
                    .WithLanguage(whisperLang)
                    .WithTokenTimestamps()
                    .Build();

                var keepAfter = start <= 0 ? -1.0 : OverlapSeconds / 2.0;
                await foreach (var segment in processor.ProcessAsync(buffer.Samples, cancellationToken)
                    .ConfigureAwait(false))
                {
                    var relStart = segment.Start.TotalSeconds;
                    var relEnd = segment.End.TotalSeconds;
                    if (relEnd <= keepAfter)
                        continue;

                    var tokens = new List<WhisperMappedToken>();
                    foreach (var token in segment.Tokens)
                    {
                        var t0 = WhisperTranscriptMapper.CentisecondsToSeconds(token.Start);
                        var t1 = WhisperTranscriptMapper.CentisecondsToSeconds(token.End);
                        if (t1 <= keepAfter && t0 <= keepAfter)
                            continue;
                        tokens.Add(new WhisperMappedToken(token.Text ?? "", t0, t1));
                    }

                    mapped.Add(new WhisperMappedSegment
                    {
                        Text = segment.Text ?? "",
                        Start = relStart + start,
                        End = relEnd + start,
                        Tokens = OffsetTokens(tokens, start),
                    });
                }
            }

            var result = WhisperTranscriptMapper.FromSegments(mapped, language ?? whisperLang);
            return TrimToMedia(result, duration);
        }
        finally
        {
            RunGate.Release();
        }
    }

    private static List<WhisperMappedToken> OffsetTokens(List<WhisperMappedToken> tokens, double offset)
    {
        if (offset == 0)
            return tokens;
        return tokens.Select(t => new WhisperMappedToken(t.Text, t.Start + offset, t.End + offset)).ToList();
    }

    private static TranscriptionResult TrimToMedia(TranscriptionResult result, double duration)
    {
        result.Words.RemoveAll(w => (w.Start ?? 0) >= duration);
        result.Segments.RemoveAll(s => s.Start >= duration);
        return result;
    }

    public static string ResolveLanguage(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint) ||
            hint.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return "en";
        var t = hint.Trim();
        var dash = t.IndexOfAny(['-', '_']);
        if (dash > 0)
            t = t[..dash];
        return t.Length >= 2 ? t[..2].ToLowerInvariant() : "en";
    }

    private static WhisperFactory GetFactory(string modelPath)
    {
        lock (FactoryGate)
        {
            if (_factory is not null && string.Equals(_factoryPath, modelPath, StringComparison.OrdinalIgnoreCase))
                return _factory;
            _factory?.Dispose();
            _factory = null;
            _factoryPath = null;
            // The packaged model sits under WindowsApps, which the user cannot write; the user-data
            // fallback and TEMNO_WHISPER_MODEL are writable only by the user who launched Temno.
            WhisperModelStore.VerifyPinned(modelPath);
            _factory = WhisperFactory.FromPath(modelPath);
            _factoryPath = modelPath;
            return _factory;
        }
    }
}
