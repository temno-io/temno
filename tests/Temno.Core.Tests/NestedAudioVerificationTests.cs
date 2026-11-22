using Temno.Core.Models;
using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

public sealed class NestedAudioVerificationTests : IDisposable
{
    private readonly string _path = Path.GetTempFileName();
    public void Dispose() => File.Delete(_path);

    [Theory]
    [InlineData(ClipType.sequence, 2.0)]
    [InlineData(ClipType.audio, 1.0)]
    public async Task Meter_UsesNestedLeafSourceTimeAndBothCarrierEncodings(ClipType encoding, double speed)
    {
        var inner = Audio("a", speed: speed);
        var nested = Timeline("nested", inner);
        var parent = Parent(nested);
        parent.Tracks[0].Clips[0].MediaType = encoding;
        var reader = new Reader((_, _) => 0.5f);
        var result = await new TimelineAudioLevelSampler(reader).SampleAsync(
            parent, id => id == "a" ? _path : null, 30, true, true,
            timelineLookup: id => id == nested.Id ? nested : null);
        Assert.Equal(0.5f, result.Left);
        Assert.Equal(speed, Assert.Single(reader.Starts), 6);
    }

    [Fact]
    public async Task Silence_DoesNotCutWhenAnotherNestedTrackCoversThePause()
    {
        var nested = Timeline("nested", Audio("a"), Audio("b"));
        var reader = new Reader((path, seconds) => path == _path
            ? seconds < 1 || seconds >= 3 ? 0.5f : 0
            : seconds >= 1 && seconds < 3 ? 0.5f : 0);
        var second = Path.GetTempFileName();
        try
        {
            var plan = NestedSilenceScanner.Plan(Parent(nested), _ => nested, id => id == "a" ? _path : second);
            Assert.Empty(await new NestedSilenceScanner(reader).ScanAsync(plan));
        }
        finally { File.Delete(second); }
    }

    [Fact]
    public void Silence_ExcludesVideoLaneFileAudioLikePlayback()
    {
        var nested = Timeline("nested", Audio("a"));
        nested.Tracks[0].Type = ClipType.video;
        nested.Tracks[0].Clips[0].MediaType = ClipType.video;
        Assert.Empty(NestedSilenceScanner.Plan(Parent(nested), _ => nested, _ => _path));
    }

    [Fact]
    public void Silence_ExcludesCarrierBlockedByEarlierClipOnSameTrack()
    {
        var nested = Timeline("nested", Audio("a"));
        var parent = Parent(nested);
        parent.Tracks[0].Clips.Insert(0, Audio("occupant"));
        Assert.Empty(NestedSilenceScanner.Plan(parent, _ => nested, _ => _path));
    }

    [Fact]
    public async Task Silence_UsesSummedProgramLevelForThreshold()
    {
        var nested = Timeline("nested", Audio("a"), Audio("b"));
        // Each -32 dBFS source is below -28 dBFS; their coherent sum is above it.
        var plan = NestedSilenceScanner.Plan(Parent(nested), _ => nested, _ => _path);
        Assert.Empty(await new NestedSilenceScanner(new Reader((_, _) => 0.025f)).ScanAsync(plan));
    }

    [Fact]
    public async Task Silence_SharedPauseIsReportedOnceInTheParentClock()
    {
        var nested = Timeline("nested", Audio("a"), Audio("b"));
        var parent = Parent(nested);
        parent.Tracks[0].Clips[0].StartFrame = 30;
        var plan = NestedSilenceScanner.Plan(parent, _ => nested, _ => _path);
        var reader = new Reader((_, seconds) => seconds >= 1 && seconds < 3 ? 0 : 0.5f);
        var hit = Assert.Single(await new NestedSilenceScanner(reader).ScanAsync(plan));
        Assert.InRange(hit.ParentRange.Start, 64, 66);
        Assert.InRange(hit.ParentRange.End, 114, 116);
    }

    [Fact]
    public async Task Silence_UsesCarrierGainAndAnImmutableSnapshot()
    {
        var nested = Timeline("nested", Audio("a"));
        var parent = Parent(nested);
        parent.Tracks[0].Clips[0].Volume = 0.02;
        var plan = NestedSilenceScanner.Plan(parent, _ => nested, _ => _path);
        parent.Tracks[0].Clips[0].Volume = 1;
        var hit = Assert.Single(await new NestedSilenceScanner(new Reader((_, _) => 0.5f)).ScanAsync(plan));
        Assert.Equal(0, hit.ParentRange.Start);
        Assert.Equal(120, hit.ParentRange.End);
    }

    [Fact]
    public async Task Silence_UnreadableContributorCannotMakeTheCarrierDeletable()
    {
        var nested = Timeline("nested", Audio("a"), Audio("missing"));
        var plan = NestedSilenceScanner.Plan(Parent(nested), _ => nested,
            id => id == "a" ? _path : _path + ".missing");
        Assert.Empty(await new NestedSilenceScanner(new Reader((_, _) => 0)).ScanAsync(plan));
    }

    [Fact]
    public async Task Silence_CancellationPropagates()
    {
        var nested = Timeline("nested", Audio("a"));
        var plan = NestedSilenceScanner.Plan(Parent(nested), _ => nested, _ => _path);
        using var cancellation = new CancellationTokenSource();
        var reader = new Reader((_, _) =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return 0;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new NestedSilenceScanner(reader).ScanAsync(plan, cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task PlaybackMeter_ReadsTheSummedNestedProgramAndRespectsFrameBoundaries()
    {
        var nested = Timeline("nested", Audio("a"), Audio("b"));
        var parent = Parent(nested);
        parent.Tracks[0].Clips[0].Volume = 0.5;
        var reader = new Reader((_, seconds) => seconds >= 1 && seconds < 3 ? 0 : 0.5f);
        var decoded = await new TimelineAudioDecoder(reader).DecodeAsync(parent, _ => _path,
            timelineLookup: _ => nested);
        var program = decoded.Mix(parent);
        Assert.Equal((0.5f, 0.5f), TimelineAudioLevelSampler.PeakAtFrame(program, 15));
        Assert.Equal((0f, 0f), TimelineAudioLevelSampler.PeakAtFrame(program, 60));
        Assert.Equal((0f, 0f), TimelineAudioLevelSampler.PeakAtFrame(program, 120));
        Assert.Equal((0f, 0f), TimelineAudioLevelSampler.PeakAtFrame(program, -1));
    }

    private static Clip Audio(string id, double speed = 1) => new()
    {
        Id = id, MediaRef = id, MediaType = ClipType.audio, SourceClipType = ClipType.audio,
        StartFrame = 0, DurationFrames = 120, Speed = speed,
    };

    private static Timeline Timeline(string id, params Clip[] clips) => new()
    {
        Id = id, Fps = 30,
        Tracks = clips.Select(c => new Track { Type = ClipType.audio, Clips = [c] }).ToList(),
    };

    private static Timeline Parent(Timeline nested) => new()
    {
        Id = "parent", Fps = 30,
        Tracks = [new Track { Type = ClipType.video, Clips = [new Clip
        {
            Id = "carrier", MediaRef = nested.Id, MediaType = ClipType.sequence,
            SourceClipType = ClipType.sequence, StartFrame = 0, DurationFrames = 120,
        }] }],
    };

    private sealed class Reader(Func<string, double, float> sample) : IAudioSource
    {
        public List<double> Starts { get; } = [];
        public Task<AudioBuffer> ReadAudioAsync(string path, int sampleRate, int channelCount,
            double startSeconds = 0, double? durationSeconds = null, CancellationToken cancellationToken = default)
        {
            Starts.Add(startSeconds);
            var count = (int)Math.Round((durationSeconds ?? 4) * sampleRate);
            var data = new float[count * channelCount];
            for (var i = 0; i < count; i++)
                for (var ch = 0; ch < channelCount; ch++)
                    data[i * channelCount + ch] = sample(path, startSeconds + i / (double)sampleRate);
            return Task.FromResult(new AudioBuffer(data, sampleRate, channelCount));
        }
    }
}
