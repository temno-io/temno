using Temno.Core.Audio;
using Temno.Core.Models;
using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

public sealed class AudioEffectsCorrectnessTests
{
    [Fact]
    public void DenoiseProcessor_UsesDryWetBlendAndPreservesStrongProgramContent()
    {
        var input = Enumerable.Repeat(0.01f, 20)
            .Concat(Enumerable.Repeat(0.5f, 80))
            .ToArray();

        var bypass = DenoiseProcessor.Apply(input, sampleRate: 100, channelCount: 1, strength: 0);
        Assert.False(bypass.Applied);
        Assert.Equal(input, bypass.Samples);

        var applied = DenoiseProcessor.Apply(input, sampleRate: 100, channelCount: 1, strength: 1);
        Assert.True(applied.Applied);
        Assert.All(applied.Samples.Take(20), sample => Assert.InRange(Math.Abs(sample), 0, 0.0001));
        Assert.All(applied.Samples.Skip(20), sample => Assert.InRange(sample, 0.48f, 0.50f));
    }

    [Fact]
    public async Task TimelineDecoder_ReportsAppliedAndBypassedDenoiseWithoutChangingDryBypass()
    {
        var path = Path.GetTempFileName();
        try
        {
            var samples = Enumerable.Repeat(0.01f, 20)
                .Concat(Enumerable.Repeat(0.5f, 80))
                .ToArray();
            var clip = AudioClip("denoise", 0, 10);
            clip.Effects =
            [
                new Effect
                {
                    Type = Clip.DenoiseEffectType,
                    Enabled = true,
                    Params = new Dictionary<string, EffectParam>
                    {
                        ["amount"] = new EffectParam { Value = 1 },
                    },
                },
            ];
            var timeline = new Timeline
            {
                Fps = 10,
                Tracks = [new Track { Type = ClipType.audio, Clips = [clip] }],
            };
            var decoder = new TimelineAudioDecoder(new FixedAudioSource(new AudioBuffer(samples, 100, 1)));

            var applied = await decoder.DecodeAsync(timeline, _ => path, sampleRate: 100, channelCount: 1);
            Assert.Equal(TimelineAudioEffectState.Applied, Assert.Single(applied.Effects).State);
            Assert.InRange(Math.Abs(Assert.Single(applied.Segments).Samples[0]), 0, 0.0001);

            clip.Effects[0].Enabled = false;
            var bypassed = await decoder.DecodeAsync(timeline, _ => path, sampleRate: 100, channelCount: 1);
            Assert.Equal(TimelineAudioEffectState.Bypassed, Assert.Single(bypassed.Effects).State);
            Assert.Equal(samples, Assert.Single(bypassed.Segments).Samples);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AudioDuckEnvelope_WritesAttackReleaseKeyframesOverBaseVolume()
    {
        var background = AudioClip("music", 0, 40);
        background.VolumeTrack = new KeyframeTrack<double>();
        background.VolumeTrack.Upsert(new Keyframe<double> { Frame = 0, Value = -4, InterpolationOut = Interpolation.linear });
        var timeline = new Timeline
        {
            Fps = 10,
            Tracks =
            [
                new Track { Type = ClipType.audio, Clips = [AudioClip("voice", 10, 10)] },
                new Track { Type = ClipType.audio, Clips = [background] },
            ],
        };

        var plan = AudioDuckEnvelope.CreatePlan(timeline, 0, 1, reductionDb: 6, attackSeconds: 0.2, releaseSeconds: 0.3);
        Assert.True(AudioDuckEnvelope.ApplyToClip(background, plan));

        Assert.Equal(-4, KeyframeMath.Sample(background.VolumeTrack, 8, 0), 5);   // attack begins
        Assert.Equal(-7, KeyframeMath.Sample(background.VolumeTrack, 9, 0), 5);   // half attack
        Assert.Equal(-10, KeyframeMath.Sample(background.VolumeTrack, 10, 0), 5); // full duck
        Assert.Equal(-10, KeyframeMath.Sample(background.VolumeTrack, 20, 0), 5); // release begins
        Assert.Equal(-8, KeyframeMath.Sample(background.VolumeTrack, 21, 0), 5);
        Assert.Equal(-4, KeyframeMath.Sample(background.VolumeTrack, 23, 0), 5);  // release ends
    }

    private static Clip AudioClip(string id, int startFrame, int durationFrames) => new()
    {
        Id = id,
        MediaRef = id,
        MediaType = ClipType.audio,
        SourceClipType = ClipType.audio,
        StartFrame = startFrame,
        DurationFrames = durationFrames,
    };

    private sealed class FixedAudioSource(AudioBuffer buffer) : IAudioSource
    {
        public Task<AudioBuffer> ReadAudioAsync(
            string path,
            int sampleRate,
            int channelCount,
            double startSeconds = 0,
            double? durationSeconds = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(buffer);
    }
}
