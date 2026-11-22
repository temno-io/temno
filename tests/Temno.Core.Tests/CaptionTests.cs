using Temno.Core.Captions;
using Xunit;

namespace Temno.Core.Tests;

public class CaptionTests
{
    [Fact]
    public void SrtParser_ParsesSimpleCue()
    {
        var srt = "1\n00:00:01,000 --> 00:00:03,500\nHello World\n\n";
        var result = SrtParser.Parse(srt);

        Assert.Single(result.Entries);
        Assert.Empty(result.Warnings);
        var entry = result.Entries[0];
        Assert.Equal(1, entry.Index);
        Assert.Equal(1.0, entry.StartSeconds, precision: 3);
        Assert.Equal(3.5, entry.EndSeconds, precision: 3);
        Assert.Equal("Hello World", entry.Text);
    }

    [Fact]
    public void SrtParser_ParsesMultipleCues()
    {
        var srt = "1\n00:00:00,000 --> 00:00:02,000\nFirst\n\n2\n00:00:03,000 --> 00:00:05,000\nSecond\n\n";
        var result = SrtParser.Parse(srt);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("First", result.Entries[0].Text);
        Assert.Equal("Second", result.Entries[1].Text);
    }

    [Fact]
    public void SrtParser_MultiLineText()
    {
        var srt = "1\n00:00:01,000 --> 00:00:03,000\nLine one\nLine two\n\n";
        var result = SrtParser.Parse(srt);

        Assert.Single(result.Entries);
        Assert.Equal("Line one\nLine two", result.Entries[0].Text);
    }

    [Fact]
    public void SrtParser_DotSeparatorAccepted()
    {
        var srt = "1\n00:00:01.500 --> 00:00:03.000\nDot separator\n\n";
        var result = SrtParser.Parse(srt);

        Assert.Single(result.Entries);
        Assert.Equal(1.5, result.Entries[0].StartSeconds, precision: 3);
    }

    [Fact]
    public void SrtParser_SkipsMalformedBlock()
    {
        var srt = "1\nno timing here\nBad block\n\n2\n00:00:01,000 --> 00:00:02,000\nGood\n\n";
        var result = SrtParser.Parse(srt);

        Assert.Single(result.Entries);
        Assert.Equal("Good", result.Entries[0].Text);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void SrtSerializer_RoundTrips()
    {
        var entries = new List<CaptionEntry>
        {
            new() { Index = 1, StartSeconds = 0, EndSeconds = 2, Text = "Hello" },
            new() { Index = 2, StartSeconds = 3, EndSeconds = 5, Text = "World" },
        };
        var serialized = SrtParser.Serialize(entries);
        var reparsed = SrtParser.Parse(serialized);

        Assert.Equal(2, reparsed.Entries.Count);
        Assert.Equal("Hello", reparsed.Entries[0].Text);
        Assert.Equal("World", reparsed.Entries[1].Text);
        Assert.Equal(0, reparsed.Entries[0].StartSeconds, precision: 3);
        Assert.Equal(5, reparsed.Entries[1].EndSeconds, precision: 3);
    }

    [Fact]
    public void VttParser_ParsesSimpleCue()
    {
        var vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:03.500\nHello VTT\n\n";
        var result = VttParser.Parse(vtt);

        Assert.Single(result.Entries);
        Assert.Empty(result.Warnings);
        Assert.Equal("Hello VTT", result.Entries[0].Text);
        Assert.Equal(1.0, result.Entries[0].StartSeconds, precision: 3);
    }

    [Fact]
    public void VttParser_IgnoresNotes()
    {
        var vtt = "WEBVTT\n\nNOTE This is a comment\n\n00:00:01.000 --> 00:00:02.000\nCue text\n\n";
        var result = VttParser.Parse(vtt);

        Assert.Single(result.Entries);
        Assert.Equal("Cue text", result.Entries[0].Text);
    }

    [Fact]
    public void CaptionEntry_DefaultsToEmptyText()
    {
        var entry = new CaptionEntry();
        Assert.Equal("", entry.Text);
        Assert.Equal(0, entry.Index);
        Assert.Equal(0, entry.StartSeconds);
        Assert.Equal(0, entry.EndSeconds);
    }
}
