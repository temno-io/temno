using Temno.Core.Models;
using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

public class TimelineAudioLevelSamplerTests
{
    [Fact]
    public void PeakStereo_SilenceIsZero()
    {
        var (l, r) = TimelineAudioLevelSampler.PeakStereo(new float[100], 2);
        Assert.Equal(0, l);
        Assert.Equal(0, r);
    }

    [Fact]
    public void PeakStereo_FindsChannelPeaks()
    {
        var samples = new float[] { 0.1f, -0.8f, 0.5f, 0.2f };
        var (l, r) = TimelineAudioLevelSampler.PeakStereo(samples, 2);
        Assert.Equal(0.5f, l, 3);
        Assert.Equal(0.8f, r, 3);
    }

    [Fact]
    public void PeakStereo_MonoDuplicates()
    {
        var (l, r) = TimelineAudioLevelSampler.PeakStereo(new float[] { 0.25f, -0.4f }, 1);
        Assert.Equal(0.4f, l, 3);
        Assert.Equal(0.4f, r, 3);
    }

    // ---- WP-172: nested-sequence carriers --------------------------------------

    [Fact]
    public async Task SampleAsync_NestedOnlyTimeline_MetersTheNestedProgram()
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = new ConstantAudioSource(0.5f);
            var sampler = new TimelineAudioLevelSampler(source);
            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = 10,
                Tracks = [new Track
                {
                    Type = ClipType.audio,
                    Clips = [NestedAudioClip("inner", 0, 20)],
                }],
            };
            var parent = new Timeline
            {
                Id = "T-MAIN",
                Fps = 10,
                Tracks = [new Track
                {
                    Type = ClipType.video,
                    Clips = [SequenceCarrier("carrier", nested.Id, 0, 20, volume: 0.5)],
                }],
            };

            var (l, r) = await sampler.SampleAsync(
                parent, _ => path, frame: 5, active: true, force: true,
                timelineLookup: id => id == nested.Id ? nested : null);

            // 0.5 source peak through the 0.5 carrier gain.
            Assert.Equal(0.25f, l, 3);
            Assert.Equal(0.25f, r, 3);
            var request = Assert.Single(source.Requests);
            Assert.Equal(0.5, request.StartSeconds, 6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SampleAsync_NestedOnlyTimeline_WithoutLookup_RemainsSilent()
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = new ConstantAudioSource(0.5f);
            var sampler = new TimelineAudioLevelSampler(source);
            var parent = new Timeline
            {
                Id = "T-MAIN",
                Fps = 10,
                Tracks = [new Track
                {
                    Type = ClipType.video,
                    Clips = [SequenceCarrier("carrier", "T-NEST", 0, 20)],
                }],
            };

            // No lookup: the carrier is a timeline id, not a file, so the meter reads silence
            // and issues no source read. Callers that cannot resolve timelines keep old behaviour.
            var (l, r) = await sampler.SampleAsync(
                parent, _ => path, frame: 5, active: true, force: true);

            Assert.Equal(0, l);
            Assert.Equal(0, r);
            Assert.Empty(source.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SampleAsync_DirectClip_StillWinsOverNestedCarrier()
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = new ConstantAudioSource(0.5f);
            var sampler = new TimelineAudioLevelSampler(source);
            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = 10,
                Tracks = [new Track
                {
                    Type = ClipType.audio,
                    Clips = [NestedAudioClip("inner", 0, 20)],
                }],
            };
            var parent = new Timeline
            {
                Id = "T-MAIN",
                Fps = 10,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.audio,
                        Clips = [NestedAudioClip("direct", 0, 20)],
                    },
                    new Track
                    {
                        Type = ClipType.video,
                        Clips = [SequenceCarrier("carrier", nested.Id, 0, 20)],
                    },
                ],
            };

            // Direct level at unity vs the carrier path (0.5 source x 0.5 carrier = 0.125):
            // the direct pass must win, proving the nested branch is fallback-only.
            var direct = parent.Tracks[0].Clips[0];
            direct.Volume = 1.0;
            var (l, _) = await sampler.SampleAsync(
                parent, _ => path, frame: 5, active: true, force: true,
                timelineLookup: id => id == nested.Id ? nested : null);

            Assert.Equal(0.5f, l, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SampleAsync_NestedCarrierGain_PropagatesToNestedLevel()
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = new ConstantAudioSource(1.0f);
            var sampler = new TimelineAudioLevelSampler(source);
            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = 10,
                Tracks = [new Track
                {
                    Type = ClipType.audio,
                    Clips = [NestedAudioClip("inner", 0, 20)],
                }],
            };
            nested.Tracks[0].Clips[0].Volume = 0.5;
            var parent = new Timeline
            {
                Id = "T-MAIN",
                Fps = 10,
                Tracks = [new Track
                {
                    Type = ClipType.video,
                    Clips = [SequenceCarrier("carrier", nested.Id, 0, 20, volume: 0.5)],
                }],
            };

            // 1.0 source x 0.5 inner volume x 0.5 carrier volume, clamped to [0, 1].
            var (l, _) = await sampler.SampleAsync(
                parent, _ => path, frame: 5, active: true, force: true,
                timelineLookup: id => id == nested.Id ? nested : null);

            Assert.Equal(0.25f, l, 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SampleAsync_MutedNestedTrack_MetersSilence()
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = new ConstantAudioSource(0.5f);
            var sampler = new TimelineAudioLevelSampler(source);
            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = 10,
                Tracks = [new Track
                {
                    Type = ClipType.audio,
                    Muted = true,
                    Clips = [NestedAudioClip("inner", 0, 20)],
                }],
            };
            var parent = new Timeline
            {
                Id = "T-MAIN",
                Fps = 10,
                Tracks = [new Track
                {
                    Type = ClipType.video,
                    Clips = [SequenceCarrier("carrier", nested.Id, 0, 20)],
                }],
            };

            var (l, r) = await sampler.SampleAsync(
                parent, _ => path, frame: 5, active: true, force: true,
                timelineLookup: id => id == nested.Id ? nested : null);

            Assert.Equal(0, l);
            Assert.Equal(0, r);
            Assert.Empty(source.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Clip NestedAudioClip(string id, int startFrame, int durationFrames) => new()
    {
        Id = id,
        MediaRef = id,
        MediaType = ClipType.audio,
        SourceClipType = ClipType.audio,
        StartFrame = startFrame,
        DurationFrames = durationFrames,
    };

    private static Clip SequenceCarrier(
        string id, string nestedId, int startFrame, int durationFrames, double volume = 1.0) => new()
    {
        Id = id,
        MediaRef = nestedId,
        MediaType = ClipType.sequence,
        SourceClipType = ClipType.sequence,
        StartFrame = startFrame,
        DurationFrames = durationFrames,
        Volume = volume,
    };

    private sealed class ConstantAudioSource(float peak) : IAudioSource
    {
        public List<(double StartSeconds, double? DurationSeconds)> Requests { get; } = [];

        public Task<AudioBuffer> ReadAudioAsync(
            string path,
            int sampleRate,
            int channelCount,
            double startSeconds = 0,
            double? durationSeconds = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add((startSeconds, durationSeconds));
            var frames = Math.Max(1, (int)Math.Round((durationSeconds ?? 0.1) * sampleRate));
            var samples = new float[frames * channelCount];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = peak;
            return Task.FromResult(new AudioBuffer(samples, sampleRate, channelCount));
        }
    }
}
