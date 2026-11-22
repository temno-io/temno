using Temno.Core.Transcription;
using Temno.Media.Transcription;
using Xunit;

namespace Temno.Core.Tests;

public sealed class WhisperTranscriptMapperTests
{
    [Fact]
    public void FromSegments_MergesTokensIntoWords_AndSkipsSpecials()
    {
        var segment = new WhisperMappedSegment
        {
            Text = "Hello world",
            Start = 1.0,
            End = 2.2,
            Tokens =
            [
                new WhisperMappedToken("<|0.00|>", 0, 0),
                new WhisperMappedToken(" Hello", 1.0, 1.4),
                new WhisperMappedToken(" world", 1.4, 2.2),
                new WhisperMappedToken("[BLANK_AUDIO]", 2.2, 2.2),
            ],
        };

        var result = WhisperTranscriptMapper.FromSegments([segment], "en");

        Assert.Equal("Hello world", result.Text);
        Assert.Equal("en", result.Language);
        Assert.Equal(2, result.Words.Count);
        Assert.Equal("Hello", result.Words[0].Text);
        Assert.Equal(1.0, result.Words[0].Start);
        Assert.Equal(1.4, result.Words[0].End);
        Assert.Equal("world", result.Words[1].Text);
        Assert.Equal(1.4, result.Words[1].Start);
        Assert.Equal(2.2, result.Words[1].End);
        Assert.Single(result.Segments);
        Assert.Equal("Hello world", result.Segments[0].Text);
    }

    [Fact]
    public void FromSegments_JoinsSubwordTokensWithoutLeadingSpace()
    {
        var segment = new WhisperMappedSegment
        {
            Text = "playing",
            Start = 0,
            End = 0.6,
            Tokens =
            [
                new WhisperMappedToken(" play", 0.0, 0.3),
                new WhisperMappedToken("ing", 0.3, 0.6),
            ],
        };

        var result = WhisperTranscriptMapper.FromSegments([segment], "en");
        Assert.Single(result.Words);
        Assert.Equal("playing", result.Words[0].Text);
        Assert.Equal(0.0, result.Words[0].Start);
        Assert.Equal(0.6, result.Words[0].End);
    }

    [Fact]
    public void FromSegments_TreatsGPrefixAsNewWord()
    {
        var segment = new WhisperMappedSegment
        {
            Text = "Hello world",
            Start = 0,
            End = 1,
            Tokens =
            [
                new WhisperMappedToken("Hello", 0.0, 0.4),
                new WhisperMappedToken("Ġworld", 0.4, 1.0),
            ],
        };

        var result = WhisperTranscriptMapper.FromSegments([segment], "en");
        Assert.Equal(2, result.Words.Count);
        Assert.Equal("Hello", result.Words[0].Text);
        Assert.Equal("world", result.Words[1].Text);
    }

    [Fact]
    public void FromSegments_FallsBackToInterpolatedWordsWhenTokensEmpty()
    {
        var segment = new WhisperMappedSegment
        {
            Text = "one two three",
            Start = 0,
            End = 3,
        };

        var result = WhisperTranscriptMapper.FromSegments([segment], null);
        Assert.Equal(3, result.Words.Count);
        Assert.Equal("one", result.Words[0].Text);
        Assert.Equal(0, result.Words[0].Start);
        Assert.Equal(1, result.Words[0].End);
        Assert.Equal("three", result.Words[2].Text);
        Assert.Equal(2, result.Words[2].Start);
        Assert.Equal(3, result.Words[2].End);
    }

    [Fact]
    public void CentisecondsToSeconds_DividesBy100()
    {
        Assert.Equal(1.25, WhisperTranscriptMapper.CentisecondsToSeconds(125));
        Assert.Equal(0, WhisperTranscriptMapper.CentisecondsToSeconds(0));
    }

    [Fact]
    public void IsSpecialToken_RecognizesWhisperMarkers()
    {
        Assert.True(WhisperTranscriptMapper.IsSpecialToken("<|startoftranscript|>"));
        Assert.True(WhisperTranscriptMapper.IsSpecialToken("[_BEG_]"));
        Assert.True(WhisperTranscriptMapper.IsSpecialToken("[BLANK_AUDIO]"));
        Assert.True(WhisperTranscriptMapper.IsSpecialToken(""));
        Assert.False(WhisperTranscriptMapper.IsSpecialToken("hello"));
    }

    [Fact]
    public void FromSegments_SkipsBlankAudioOnlySegment()
    {
        var segment = new WhisperMappedSegment
        {
            Text = "[BLANK_AUDIO]",
            Start = 0,
            End = 1.2,
        };

        var result = WhisperTranscriptMapper.FromSegments([segment], "en");
        Assert.Empty(result.Words);
        Assert.Empty(result.Segments);
        Assert.Equal("", result.Text);
    }

    [Fact]
    public void FromSegments_StripsEmbeddedBlankAudioFromSegmentText()
    {
        var segment = new WhisperMappedSegment
        {
            Text = "[BLANK_AUDIO] hello there",
            Start = 0,
            End = 2,
        };

        var result = WhisperTranscriptMapper.FromSegments([segment], "en");
        Assert.Equal("hello there", result.Text);
        Assert.Equal(2, result.Words.Count);
        Assert.Equal("hello", result.Words[0].Text);
        Assert.DoesNotContain(result.Words, w => w.Text.Contains("BLANK", StringComparison.OrdinalIgnoreCase));
    }
}
