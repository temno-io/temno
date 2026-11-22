using Temno.Core.Models;
using Temno.Core.Preview;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-173. Pins the contract answer: <c>sequence</c> appears on a clip only
/// (<c>docs/contracts/project-package.md</c> ClipType table), so the source monitor
/// — which opens from media-library entries — must not treat it as a playable asset.
/// </summary>
public sealed class SourceMonitorPlaybackTests
{
    [Theory]
    [InlineData(ClipType.video, true)]
    [InlineData(ClipType.audio, true)]
    [InlineData(ClipType.image, false)]
    [InlineData(ClipType.text, false)]
    [InlineData(ClipType.lottie, false)]
    [InlineData(ClipType.sequence, false)]
    [InlineData(ClipType.subtitle, false)]
    public void CanPlay_SequenceAndStillsAreNotPlayable(ClipType type, bool expected)
        => Assert.Equal(expected, SourceMonitorPlayback.CanPlay(type));

    [Fact]
    public void CanPlay_NullType_IsNotPlayable()
        => Assert.False(SourceMonitorPlayback.CanPlay(null));

    [Theory]
    [InlineData(ClipType.video, true)]
    [InlineData(ClipType.audio, true)]
    [InlineData(ClipType.sequence, false)]
    [InlineData(ClipType.image, false)]
    [InlineData(ClipType.lottie, false)]
    public void BuildsSourceAudioTimeline_OnlyFileBackedAv(ClipType type, bool expected)
        => Assert.Equal(expected, SourceMonitorPlayback.BuildsSourceAudioTimeline(type));

    [Fact]
    public void Sequence_IsIdentifiedAndExplained()
    {
        Assert.True(SourceMonitorPlayback.IsSequence(ClipType.sequence));
        Assert.False(SourceMonitorPlayback.IsSequence(ClipType.video));
        Assert.False(string.IsNullOrWhiteSpace(SourceMonitorPlayback.SequencePlaceholder));
        Assert.False(string.IsNullOrWhiteSpace(SourceMonitorPlayback.SequenceAudioStatus));
        Assert.Contains("Timeline", SourceMonitorPlayback.SequencePlaceholder, StringComparison.Ordinal);
        Assert.Contains("Timeline", SourceMonitorPlayback.SequenceAudioStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_WillDecodeASequenceTypedEntry_ButTheHostMustNotPlayIt()
    {
        // The shared ClipType enum means a hand-edited media.json can carry type:sequence
        // even though the contract forbids it as a media entry. Decoding must not become
        // silent source-monitor playback.
        var json = """
            {
              "version": 2,
              "entries": [
                {
                  "id": "not-a-contract-asset",
                  "name": "Hand-edited sequence entry",
                  "type": "sequence",
                  "source": { "external": { "absolutePath": "/tmp/unused.mp4" } },
                  "duration": 4
                }
              ]
            }
            """;
        var manifest = MediaManifest.Decode(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.Equal(ClipType.sequence, manifest.Entries[0].Type);
        Assert.False(SourceMonitorPlayback.CanPlay(manifest.Entries[0].Type));
        Assert.False(SourceMonitorPlayback.BuildsSourceAudioTimeline(manifest.Entries[0].Type));
    }
}
