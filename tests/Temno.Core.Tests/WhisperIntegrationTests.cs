using Temno.Media.Transcription;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// Opt-in Windows smoke. Set TEMNO_WHISPER_IT=true and TEMNO_WHISPER_IT_MEDIA to a
/// short spoken English file (ffmpeg + ggml-base.en.bin required). Default runs are a no-op.
/// </summary>
public sealed class WhisperIntegrationTests
{
    [Fact]
    public async Task WhisperProvider_TranscribesSpokenEnglish_WhenMediaProvided()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("TEMNO_WHISPER_IT"),
                "true",
                StringComparison.OrdinalIgnoreCase))
            return;
        var media = Environment.GetEnvironmentVariable("TEMNO_WHISPER_IT_MEDIA");
        if (string.IsNullOrWhiteSpace(media) || !File.Exists(media))
            return;

        var provider = new WhisperNetTranscriptionProvider();
        var result = await provider.TranscribeAsync(media, "en");
        Assert.False(string.IsNullOrWhiteSpace(result.Text), "expected spoken text from Whisper");
        Assert.NotEmpty(result.Words);
        Assert.Contains(result.Words, w => w.Start is >= 0);
        var blob = result.Text.ToLowerInvariant();
        Assert.True(
            blob.Contains("hello") || blob.Contains("palmier") || blob.Contains("transcription") || blob.Contains("test"),
            "unexpected transcript: " + result.Text);
    }
}
