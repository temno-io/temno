using Temno.Core.Models;
using Temno.Media.Audio;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

public sealed class TimelineAudioDecoderTests
{
    [Fact]
    public async Task DecodeAsync_UsesAudioLanesSkipsMutedAndOverlappingClips()
    {
        var path = Path.GetTempFileName();
        try
        {
        var source = new RecordingAudioSource(new AudioBuffer(Enumerable.Repeat(0.25f, 10).ToArray(), 10, 1));
        var decoder = new TimelineAudioDecoder(source);
        var timeline = new Timeline
        {
            Fps = 10,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.audio,
                    Clips =
                    [
                        AudioClip("first", 0, 10),
                        AudioClip("overlap", 5, 10),
                        AudioClip("second", 10, 10),
                    ],
                },
                new Track { Type = ClipType.audio, Muted = true, Clips = [AudioClip("muted", 0, 10)] },
                new Track { Type = ClipType.video, Clips = [AudioClip("video", 0, 10)] },
            ],
        };

        var result = await decoder.DecodeAsync(timeline, _ => path, sampleRate: 10, channelCount: 1);

        Assert.Equal(["first", "second"], result.Segments.Select(segment => segment.Clip.Id));
        Assert.Equal(2, source.Requests.Count);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.ClipId == "overlap" && diagnostic.Reason.Contains("overlapping"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeAsync_AppliesTrimSpeedAndTimelineSampleLength()
    {
        var path = Path.GetTempFileName();
        try
        {
        var samples = Enumerable.Range(0, 20).Select(value => (float)value).ToArray();
        var source = new RecordingAudioSource(new AudioBuffer(samples, 10, 1));
        var clip = AudioClip("speed", startFrame: 10, durationFrames: 10);
        clip.TrimStartFrame = 2;
        clip.Speed = 2;
        var timeline = new Timeline { Fps = 10, Tracks = [new Track { Type = ClipType.audio, Clips = [clip] }] };

        var result = await new TimelineAudioDecoder(source).DecodeAsync(
            timeline, _ => path, sampleRate: 10, channelCount: 1);

        var request = Assert.Single(source.Requests);
        Assert.Equal(0.2, request.StartSeconds, 6);
        Assert.Equal(2.0, request.DurationSeconds!.Value, 6);
        var segment = Assert.Single(result.Segments);
        Assert.Equal(10, segment.Samples.Length);
        Assert.Equal(0f, segment.Samples[0]);
        Assert.Equal(19f, segment.Samples[^1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeAsync_NestedSequenceOnVideoTrack_ProducesSegmentAtParentPlacement()
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = new RecordingAudioSource(new AudioBuffer(Enumerable.Repeat(0.5f, 20).ToArray(), 10, 1));
            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = 10,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.audio,
                        Clips = [AudioClip("nested-audio", 0, 20)],
                    },
                ],
            };
            var parent = new Timeline
            {
                Id = "T-MAIN",
                Fps = 10,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "seq",
                                MediaRef = nested.Id,
                                MediaType = ClipType.sequence,
                                SourceClipType = ClipType.sequence,
                                StartFrame = 5,
                                DurationFrames = 10,
                                Volume = 0.8,
                            },
                        ],
                    },
                ],
            };

            Timeline? Lookup(string id) => id == nested.Id ? nested : null;
            var result = await new TimelineAudioDecoder(source).DecodeAsync(
                parent, _ => path, sampleRate: 10, channelCount: 1, timelineLookup: Lookup);

            var segment = Assert.Single(result.Segments);
            Assert.Equal("seq", segment.Clip.Id);
            Assert.Equal(5, segment.Clip.StartFrame);
            Assert.Equal(10, segment.Clip.DurationFrames);
            Assert.Equal(0.8, segment.Clip.Volume);
            Assert.Equal(10, segment.Samples.Length);
            Assert.Single(source.Requests);
            Assert.Contains(segment.Samples, sample => sample != 0f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeAsync_MissingNestedLookup_AddsDiagnosticWithoutThrowing()
    {
        var parent = new Timeline
        {
            Fps = 10,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            Id = "seq",
                            MediaRef = "missing-timeline",
                            MediaType = ClipType.sequence,
                            SourceClipType = ClipType.sequence,
                            StartFrame = 0,
                            DurationFrames = 10,
                        },
                    ],
                },
            ],
        };

        var result = await new TimelineAudioDecoder(new RecordingAudioSource(new AudioBuffer([0.1f], 10, 1)))
            .DecodeAsync(parent, _ => null, sampleRate: 10, channelCount: 1, timelineLookup: _ => null);

        Assert.Empty(result.Segments);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.ClipId == "seq"
                && diagnostic.Reason == "Nested-sequence audio omitted: nested timeline not found.");
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Reason.Contains("WP-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DecodeAsync_DepthCap_DoesNotRecurseForever()
    {
        var path = Path.GetTempFileName();
        try
        {
            var loop = new Timeline
            {
                Id = "T-LOOP",
                Fps = 10,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "loop",
                                MediaRef = "T-LOOP",
                                MediaType = ClipType.sequence,
                                SourceClipType = ClipType.sequence,
                                StartFrame = 0,
                                DurationFrames = 10,
                            },
                        ],
                    },
                    new Track
                    {
                        Type = ClipType.audio,
                        Clips = [AudioClip("leaf", 0, 10)],
                    },
                ],
            };

            var source = new RecordingAudioSource(new AudioBuffer(Enumerable.Repeat(0.2f, 10).ToArray(), 10, 1));
            Timeline? Lookup(string id) => id == loop.Id ? loop : null;
            var result = await new TimelineAudioDecoder(source).DecodeAsync(
                loop, _ => path, sampleRate: 10, channelCount: 1, timelineLookup: Lookup);

            Assert.Equal(TimelineFrameRenderer.MaxSequenceNestingDepth + 1, source.Requests.Count);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Reason == "Nested-sequence audio omitted: nesting depth limit reached.");
            Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Reason.Contains("WP-", StringComparison.OrdinalIgnoreCase));
            Assert.NotEmpty(result.Segments);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeAsync_LinkedVideoAndAudioSequenceCarriers_MixOnce()
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = new RecordingAudioSource(new AudioBuffer(Enumerable.Repeat(0.4f, 20).ToArray(), 10, 1));
            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = 10,
                Tracks = [new Track { Type = ClipType.audio, Clips = [AudioClip("nested-audio", 0, 20)] }],
            };
            var parent = new Timeline
            {
                Fps = 10,
                Tracks =
                [
                    new Track { Type = ClipType.video, Clips = [LinkedSeq("v-seq", nested.Id, "lg-1", 1.0)] },
                    new Track { Type = ClipType.audio, Clips = [LinkedSeq("a-seq", nested.Id, "lg-1", 0.25)] },
                ],
            };

            var result = await new TimelineAudioDecoder(source).DecodeAsync(
                parent, _ => path, sampleRate: 10, channelCount: 1, timelineLookup: id => id == nested.Id ? nested : null);

            Assert.Single(result.Segments);
            Assert.Single(source.Requests);
            // WP-175 strengthens, does not amend: the pair shares a link group so it still mixes
            // once, which is all this test ever asserted. What it never pinned is WHICH half
            // survives, and that was a track-order coin flip that discarded the audio lane's
            // fader. The audio lane is the one the owner mixes and the only lane the Mac expands
            // audio from - see docs/verification/wp-175/DECISION.md.
            Assert.Equal("a-seq", result.Segments[0].Clip.Id);
            Assert.Equal(0.25, result.Segments[0].Clip.Volume);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- WP-165: nested-sequence audio parity ----
    //
    // The nested mix path (AppendNestedSequenceAudioAsync -> SliceProgramToClip ->
    // CopyForNestedPlacement) shipped alongside WP-106's picture work; what it never had was a
    // proof that a nested carrier is gained, faded and summed exactly like a top-level audio clip.
    // AudioMixer.MixSegment reads only StartFrame/EndFrame/DurationFrames and Clip.VolumeAt, and
    // VolumeAt = Volume x volumeTrack dB gain x FadeMultiplier — so those are the whole parity
    // surface, and each test below pins one part of it.

    [Fact]
    public async Task DecodeAsync_NestedCarrier_MatchesTopLevelClipGainAndFades()
    {
        var path = Path.GetTempFileName();
        try
        {
            const int fps = 10;
            const int rate = 10;
            var pcm = new AudioBuffer(Enumerable.Repeat(0.25f, 10).ToArray(), rate, 1);

            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = fps,
                Tracks = [new Track { Type = ClipType.audio, Clips = [AudioClip("inner", 0, 10)] }],
            };

            // Same placement, same gain, same fades — one reached through a nested sequence, the
            // other a plain audio clip. The mixed programs must come out identical.
            static Clip Shaped(Clip clip)
            {
                clip.Volume = 0.5;
                clip.FadeInFrames = 4;
                clip.FadeOutFrames = 3;
                clip.FadeOutInterpolation = Interpolation.smooth;
                return clip;
            }

            var nestedParent = new Timeline
            {
                Id = "T-NESTED-PARENT",
                Fps = fps,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            Shaped(new Clip
                            {
                                Id = "carrier",
                                MediaRef = nested.Id,
                                MediaType = ClipType.sequence,
                                SourceClipType = ClipType.sequence,
                                StartFrame = 0,
                                DurationFrames = 10,
                            }),
                        ],
                    },
                ],
            };

            var flatParent = new Timeline
            {
                Id = "T-FLAT-PARENT",
                Fps = fps,
                Tracks = [new Track { Type = ClipType.audio, Clips = [Shaped(AudioClip("flat", 0, 10))] }],
            };

            var nestedMix = (await new TimelineAudioDecoder(new RecordingAudioSource(pcm)).DecodeAsync(
                    nestedParent, _ => path, rate, channelCount: 1,
                    timelineLookup: id => id == nested.Id ? nested : null))
                .Mix(nestedParent);
            var flatMix = (await new TimelineAudioDecoder(new RecordingAudioSource(pcm)).DecodeAsync(
                    flatParent, _ => path, rate, channelCount: 1))
                .Mix(flatParent);

            Assert.Equal(flatMix.Samples.Length, nestedMix.Samples.Length);
            Assert.Equal(flatMix.Samples, nestedMix.Samples);
            // Guard against the pair agreeing on silence: the fade shape must actually be there.
            Assert.Contains(nestedMix.Samples, sample => sample != 0f);
            Assert.True(nestedMix.Samples[0] < nestedMix.Samples[5], "fade-in must ramp up");
            // The final sample sits at the START of the last frame block, so it carries
            // VolumeAt(9) = 0.5 x smoothstep(1/3), not the envelope's zero endpoint at frame 10.
            Assert.True(nestedMix.Samples[^1] < nestedMix.Samples[5], "fade-out must ramp down");
            Assert.True(nestedMix.Samples[^1] > 0f, "the fade-out tail is sampled before the zero endpoint");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeAsync_MutedNestedTrack_StaysSilentWhileSiblingLaneIsAudible()
    {
        var path = Path.GetTempFileName();
        try
        {
            const int fps = 10;
            const int rate = 10;
            var pcm = new AudioBuffer(Enumerable.Repeat(0.25f, 10).ToArray(), rate, 1);

            // Two lanes inside the nested sequence, one muted. Only the audible lane may reach the
            // parent: exactly one source read, and the level stays single-lane.
            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = fps,
                Tracks =
                [
                    new Track { Type = ClipType.audio, Muted = true, Clips = [AudioClip("muted-lane", 0, 10)] },
                    new Track { Type = ClipType.audio, Clips = [AudioClip("audible-lane", 0, 10)] },
                ],
            };
            var parent = new Timeline
            {
                Id = "T-MAIN",
                Fps = fps,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "carrier",
                                MediaRef = nested.Id,
                                MediaType = ClipType.sequence,
                                SourceClipType = ClipType.sequence,
                                StartFrame = 0,
                                DurationFrames = 10,
                            },
                        ],
                    },
                ],
            };

            var source = new RecordingAudioSource(pcm);
            var mixed = (await new TimelineAudioDecoder(source).DecodeAsync(
                    parent, _ => path, rate, channelCount: 1,
                    timelineLookup: id => id == nested.Id ? nested : null))
                .Mix(parent);

            Assert.Single(source.Requests);
            Assert.All(mixed.Samples, sample => Assert.Equal(0.25f, sample, 5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeAsync_HiddenNestedTrack_ContributesNothing()
    {
        var path = Path.GetTempFileName();
        try
        {
            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = 10,
                Tracks =
                [
                    new Track { Type = ClipType.audio, Hidden = true, Clips = [AudioClip("hidden-lane", 0, 10)] },
                ],
            };
            var parent = new Timeline
            {
                Id = "T-MAIN",
                Fps = 10,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "carrier",
                                MediaRef = nested.Id,
                                MediaType = ClipType.sequence,
                                SourceClipType = ClipType.sequence,
                                StartFrame = 0,
                                DurationFrames = 10,
                            },
                        ],
                    },
                ],
            };

            var source = new RecordingAudioSource(new AudioBuffer(Enumerable.Repeat(0.25f, 10).ToArray(), 10, 1));
            var result = await new TimelineAudioDecoder(source).DecodeAsync(
                parent, _ => path, sampleRate: 10, channelCount: 1,
                timelineLookup: id => id == nested.Id ? nested : null);

            Assert.Empty(result.Segments);
            Assert.Empty(source.Requests);
            Assert.All(result.Mix(parent).Samples, sample => Assert.Equal(0f, sample));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeAsync_NestedAndTopLevelAudio_SumInTheMix()
    {
        var path = Path.GetTempFileName();
        try
        {
            const int fps = 10;
            const int rate = 10;
            var pcm = new AudioBuffer(Enumerable.Repeat(0.25f, 10).ToArray(), rate, 1);

            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = fps,
                Tracks = [new Track { Type = ClipType.audio, Clips = [AudioClip("inner", 0, 10)] }],
            };
            var parent = new Timeline
            {
                Id = "T-MAIN",
                Fps = fps,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "carrier",
                                MediaRef = nested.Id,
                                MediaType = ClipType.sequence,
                                SourceClipType = ClipType.sequence,
                                StartFrame = 0,
                                DurationFrames = 10,
                            },
                        ],
                    },
                    // A separate lane, so the per-track occupancy rule does not drop it.
                    new Track { Type = ClipType.audio, Clips = [AudioClip("top-level", 0, 10)] },
                ],
            };

            var result = await new TimelineAudioDecoder(new RecordingAudioSource(pcm)).DecodeAsync(
                parent, _ => path, rate, channelCount: 1,
                timelineLookup: id => id == nested.Id ? nested : null);

            Assert.Equal(2, result.Segments.Count);
            // 0.25 nested + 0.25 top-level: linear sum, no ducking and no double-count.
            Assert.All(result.Mix(parent).Samples, sample => Assert.Equal(0.5f, sample, 5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- WP-165: divergences a same-fps, unity-level fixture cannot see ----

    [Fact]
    public async Task NestedSubmix_KeepsHeadroom_SoTheCarrierFaderStillHasSomethingToPull()
    {
        // A submix is not an output stage. Two nested lanes at 0.8 sum to 1.6; if that is clamped
        // to 1.0 before the carrier's 0.5 fader runs, the result is 0.5 and the material is
        // permanently flat-topped. Placing the same two lanes directly on the parent at 0.5 each
        // gives 0.8 - that flat control is what parity means here.
        var path = Path.GetTempFileName();
        try
        {
            const int fps = 10;
            const int rate = 10;
            var pcm = new AudioBuffer(Enumerable.Repeat(0.8f, 10).ToArray(), rate, 1);

            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = fps,
                Tracks =
                [
                    new Track { Type = ClipType.audio, Clips = [AudioClip("lane-a", 0, 10)] },
                    new Track { Type = ClipType.audio, Clips = [AudioClip("lane-b", 0, 10)] },
                ],
            };
            var nestedParent = new Timeline
            {
                Id = "T-NESTED-PARENT",
                Fps = fps,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "carrier",
                                MediaRef = nested.Id,
                                MediaType = ClipType.sequence,
                                SourceClipType = ClipType.sequence,
                                StartFrame = 0,
                                DurationFrames = 10,
                                Volume = 0.5,
                            },
                        ],
                    },
                ],
            };

            var flatParent = new Timeline
            {
                Id = "T-FLAT-PARENT",
                Fps = fps,
                Tracks =
                [
                    new Track { Type = ClipType.audio, Clips = [Gained(AudioClip("flat-a", 0, 10), 0.5)] },
                    new Track { Type = ClipType.audio, Clips = [Gained(AudioClip("flat-b", 0, 10), 0.5)] },
                ],
            };

            var nestedMix = (await new TimelineAudioDecoder(new RecordingAudioSource(pcm)).DecodeAsync(
                    nestedParent, _ => path, rate, channelCount: 1,
                    timelineLookup: id => id == nested.Id ? nested : null))
                .Mix(nestedParent);
            var flatMix = (await new TimelineAudioDecoder(new RecordingAudioSource(pcm)).DecodeAsync(
                    flatParent, _ => path, rate, channelCount: 1))
                .Mix(flatParent);

            Assert.All(flatMix.Samples, sample => Assert.Equal(0.8f, sample, 5));
            Assert.All(nestedMix.Samples, sample => Assert.Equal(0.8f, sample, 5));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NestedTimelineAtADifferentFps_PlaysAtTheRightRate()
    {
        // Rounding the carrier's source window to whole NESTED frames and then resampling it onto
        // the carrier's own length is a rate warp: 7 parent frames at 30 fps is 0.2333 s, which
        // rounds to 6 nested frames at 24 fps = 0.2500 s, so 7% too much audio was crammed into
        // the clip - pitched up, and drifting against the nested picture, which does not warp.
        var path = Path.GetTempFileName();
        try
        {
            const int rate = 48_000;
            // A ramp makes a rate warp visible: the value at the clip's end reports how far into
            // the nested program playback actually got.
            var ramp = Enumerable.Range(0, rate).Select(i => i / (float)rate).ToArray();
            var pcm = new AudioBuffer(ramp, rate, 1);

            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = 24,
                Tracks = [new Track { Type = ClipType.audio, Clips = [AudioClip("inner", 0, 24)] }],
            };
            var parent = new Timeline
            {
                Id = "T-MAIN",
                Fps = 30,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "carrier",
                                MediaRef = nested.Id,
                                MediaType = ClipType.sequence,
                                SourceClipType = ClipType.sequence,
                                StartFrame = 0,
                                DurationFrames = 7,
                            },
                        ],
                    },
                ],
            };

            var result = await new TimelineAudioDecoder(new RecordingAudioSource(pcm)).DecodeAsync(
                parent, _ => path, rate, channelCount: 1,
                timelineLookup: id => id == nested.Id ? nested : null);

            var segment = Assert.Single(result.Segments);
            // 7 frames at 30 fps = 0.23333 s, so the carrier must consume exactly that much of the
            // nested program - no more (fast/pitched up), no less (slow).
            var expectedSampleFrames = (int)Math.Round(7 * rate / 30.0, MidpointRounding.AwayFromZero);
            Assert.Equal(expectedSampleFrames, segment.Samples.Length);
            Assert.Equal(0.23333f, segment.Samples[^1], 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NestedCarrier_HonoursTrimAndSpeed()
    {
        var path = Path.GetTempFileName();
        try
        {
            const int fps = 10;
            const int rate = 10;
            var ramp = Enumerable.Range(0, 40).Select(i => (float)i).ToArray();
            var pcm = new AudioBuffer(ramp, rate, 1);

            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = fps,
                Tracks = [new Track { Type = ClipType.audio, Clips = [AudioClip("inner", 0, 40)] }],
            };
            var parent = new Timeline
            {
                Id = "T-MAIN",
                Fps = fps,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "carrier",
                                MediaRef = nested.Id,
                                MediaType = ClipType.sequence,
                                SourceClipType = ClipType.sequence,
                                StartFrame = 0,
                                DurationFrames = 10,
                                TrimStartFrame = 5,
                                Speed = 2,
                            },
                        ],
                    },
                ],
            };

            var result = await new TimelineAudioDecoder(new RecordingAudioSource(pcm)).DecodeAsync(
                parent, _ => path, rate, channelCount: 1,
                timelineLookup: id => id == nested.Id ? nested : null);

            var segment = Assert.Single(result.Segments);
            // Trim 5 frames in at 10 fps = 0.5 s = nested sample 5; speed 2 over a 10-frame clip
            // consumes 20 nested samples (5..25) resampled down to the clip's 10.
            Assert.Equal(10, segment.Samples.Length);
            Assert.Equal(5f, segment.Samples[0], 3);
            Assert.Equal(24f, segment.Samples[^1], 3);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NestedCarrier_AppliesAKeyframedVolumeTrack()
    {
        var path = Path.GetTempFileName();
        try
        {
            const int fps = 10;
            const int rate = 10;
            var pcm = new AudioBuffer(Enumerable.Repeat(1.0f, 10).ToArray(), rate, 1);

            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = fps,
                Tracks = [new Track { Type = ClipType.audio, Clips = [AudioClip("inner", 0, 10)] }],
            };
            // -6 dB held across the clip: a flat automation curve, so every sample lands on it.
            var carrier = new Clip
            {
                Id = "carrier",
                MediaRef = nested.Id,
                MediaType = ClipType.sequence,
                SourceClipType = ClipType.sequence,
                StartFrame = 0,
                DurationFrames = 10,
                VolumeTrack = new KeyframeTrack<double>
                {
                    Keyframes = [new Keyframe<double> { Frame = 0, Value = -6 }, new Keyframe<double> { Frame = 10, Value = -6 }],
                },
            };
            var parent = new Timeline
            {
                Id = "T-MAIN",
                Fps = fps,
                Tracks = [new Track { Type = ClipType.video, Clips = [carrier] }],
            };

            var mixed = (await new TimelineAudioDecoder(new RecordingAudioSource(pcm)).DecodeAsync(
                    parent, _ => path, rate, channelCount: 1,
                    timelineLookup: id => id == nested.Id ? nested : null))
                .Mix(parent);

            var expected = (float)VolumeScale.LinearFromDb(-6);
            Assert.All(mixed.Samples, sample => Assert.Equal(expected, sample, 4));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task NestedCarrier_MixesStereoWithoutCrossingChannels()
    {
        var path = Path.GetTempFileName();
        try
        {
            const int fps = 10;
            const int rate = 10;
            // Left 0.25, right 0.75: any stride error swaps or smears them.
            var stereo = new float[20];
            for (var i = 0; i < 10; i++)
            {
                stereo[i * 2] = 0.25f;
                stereo[i * 2 + 1] = 0.75f;
            }

            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = fps,
                Tracks = [new Track { Type = ClipType.audio, Clips = [AudioClip("inner", 0, 10)] }],
            };
            var parent = new Timeline
            {
                Id = "T-MAIN",
                Fps = fps,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "carrier",
                                MediaRef = nested.Id,
                                MediaType = ClipType.sequence,
                                SourceClipType = ClipType.sequence,
                                StartFrame = 0,
                                DurationFrames = 10,
                            },
                        ],
                    },
                ],
            };

            var mixed = (await new TimelineAudioDecoder(
                    new RecordingAudioSource(new AudioBuffer(stereo, rate, 2))).DecodeAsync(
                    parent, _ => path, rate, channelCount: 2,
                    timelineLookup: id => id == nested.Id ? nested : null))
                .Mix(parent);

            Assert.Equal(20, mixed.Samples.Length);
            for (var i = 0; i < 10; i++)
            {
                Assert.Equal(0.25f, mixed.Samples[i * 2], 4);
                Assert.Equal(0.75f, mixed.Samples[i * 2 + 1], 4);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DenoiseOnTheCarrierItself_IsReportedRatherThanSilentlyDropped()
    {
        var path = Path.GetTempFileName();
        try
        {
            const int fps = 10;
            const int rate = 10;
            var pcm = new AudioBuffer(Enumerable.Repeat(0.25f, 10).ToArray(), rate, 1);

            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = fps,
                Tracks = [new Track { Type = ClipType.audio, Clips = [AudioClip("inner", 0, 10)] }],
            };
            var parent = new Timeline
            {
                Id = "T-MAIN",
                Fps = fps,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "carrier",
                                MediaRef = nested.Id,
                                MediaType = ClipType.sequence,
                                SourceClipType = ClipType.sequence,
                                StartFrame = 0,
                                DurationFrames = 10,
                                Effects =
                                [
                                    new Effect
                                    {
                                        Id = "E-DN",
                                        Type = Clip.DenoiseEffectType,
                                        Enabled = true,
                                        Params = { ["amount"] = new EffectParam { Value = 0.6 } },
                                    },
                                ],
                            },
                        ],
                    },
                ],
            };

            var result = await new TimelineAudioDecoder(new RecordingAudioSource(pcm)).DecodeAsync(
                parent, _ => path, rate, channelCount: 1,
                timelineLookup: id => id == nested.Id ? nested : null);

            // The effect is not applied - a nested program is already mixed by the time the carrier
            // is placed - but the decoder must say so rather than drop it in silence.
            var status = Assert.Single(result.Effects);
            Assert.Equal("carrier", status.ClipId);
            Assert.Equal(Clip.DenoiseEffectType, status.EffectType);
            Assert.Equal(TimelineAudioEffectState.Unavailable, status.State);
            Assert.Contains("inside the sequence", status.Detail);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- WP-175: the link decides the nested contribution, not the placement ----
    //
    // The dedup this replaced keyed on (mediaRef, start, duration, trim), which is a placement and
    // not a link. It was wrong in both directions: it called two deliberately stacked carriers a
    // linked pair and dropped one, and on a real linked pair it kept whichever half the track walk
    // reached first - so muting the audio lane, the lane the owner actually mixes, silenced
    // nothing. The rule is now (link group, media ref), and the audio lane speaks for its group.
    // Reasoning and the Mac citations: docs/verification/wp-175/DECISION.md.

    [Fact]
    public async Task DecodeAsync_LinkedPairWithMutedAudioLane_SilencesTheNest()
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = new RecordingAudioSource(new AudioBuffer(Enumerable.Repeat(0.4f, 10).ToArray(), 10, 1));
            var nested = NestWithFlatTone();
            var parent = new Timeline
            {
                Fps = 10,
                Tracks =
                [
                    new Track { Type = ClipType.video, Clips = [LinkedSeq("v-seq", nested.Id, "lg-1", 1.0)] },
                    new Track { Type = ClipType.audio, Muted = true, Clips = [LinkedSeq("a-seq", nested.Id, "lg-1", 1.0)] },
                ],
            };

            var mix = (await new TimelineAudioDecoder(source).DecodeAsync(
                    parent, _ => path, sampleRate: 10, channelCount: 1,
                    timelineLookup: id => id == nested.Id ? nested : null))
                .Mix(parent);

            Assert.All(mix.Samples, sample => Assert.Equal(0f, sample));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeAsync_LinkedPairWithMutedVideoLane_StaysAudibleAtTheAudioLanesGain()
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = new RecordingAudioSource(new AudioBuffer(Enumerable.Repeat(0.4f, 10).ToArray(), 10, 1));
            var nested = NestWithFlatTone();
            var parent = new Timeline
            {
                Fps = 10,
                Tracks =
                [
                    new Track { Type = ClipType.video, Muted = true, Clips = [LinkedSeq("v-seq", nested.Id, "lg-1", 1.0)] },
                    new Track { Type = ClipType.audio, Clips = [LinkedSeq("a-seq", nested.Id, "lg-1", 0.5)] },
                ],
            };

            var result = await new TimelineAudioDecoder(source).DecodeAsync(
                parent, _ => path, sampleRate: 10, channelCount: 1,
                timelineLookup: id => id == nested.Id ? nested : null);

            var segment = Assert.Single(result.Segments);
            Assert.Equal("a-seq", segment.Clip.Id);
            Assert.Contains(result.Mix(parent).Samples, sample => sample != 0f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodeAsync_LinkedPair_DoesNotDependOnTrackOrder(bool audioLaneFirst)
    {
        var path = Path.GetTempFileName();
        try
        {
            var source = new RecordingAudioSource(new AudioBuffer(Enumerable.Repeat(0.4f, 10).ToArray(), 10, 1));
            var nested = NestWithFlatTone();
            var video = new Track { Type = ClipType.video, Clips = [LinkedSeq("v-seq", nested.Id, "lg-1", 1.0)] };
            var audio = new Track { Type = ClipType.audio, Clips = [LinkedSeq("a-seq", nested.Id, "lg-1", 0.25)] };
            var parent = new Timeline
            {
                Fps = 10,
                Tracks = audioLaneFirst ? [audio, video] : [video, audio],
            };

            var result = await new TimelineAudioDecoder(source).DecodeAsync(
                parent, _ => path, sampleRate: 10, channelCount: 1,
                timelineLookup: id => id == nested.Id ? nested : null);

            // Same surviving carrier and same gain whichever lane the walk reaches first.
            var segment = Assert.Single(result.Segments);
            Assert.Equal("a-seq", segment.Clip.Id);
            Assert.Equal(0.25, segment.Clip.Volume);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeAsync_StackedUnlinkedCarriers_EachContributeAtTheirOwnGain()
    {
        var path = Path.GetTempFileName();
        try
        {
            var nested = NestWithFlatTone();
            Timeline Parent(params Clip[] carriers) => new()
            {
                Fps = 10,
                Tracks = [.. carriers.Select(carrier => new Track { Type = ClipType.video, Clips = [carrier] })],
            };
            Clip Pip(string id, double volume) => Gained(Seq(id, nested.Id), volume);

            async Task<(float[] Samples, TimelineAudioDecodeResult Result, RecordingAudioSource Source)> Decode(
                Timeline parent)
            {
                var source = new RecordingAudioSource(
                    new AudioBuffer(Enumerable.Repeat(0.4f, 10).ToArray(), 10, 1));
                var result = await new TimelineAudioDecoder(source).DecodeAsync(
                    parent, _ => path, sampleRate: 10, channelCount: 1,
                    timelineLookup: id => id == nested.Id ? nested : null);
                return (result.Mix(parent).Samples, result, source);
            }

            // Identical placement, identical media ref, no link between them: a picture-in-picture
            // of one nest over itself. The renderer composites both, so the mix must carry both.
            var stacked = await Decode(Parent(Pip("pip-a", 0.5), Pip("pip-b", 0.25)));
            var onlyA = await Decode(Parent(Pip("pip-a", 0.5)));
            var onlyB = await Decode(Parent(Pip("pip-b", 0.25)));

            Assert.Equal(2, stacked.Result.Segments.Count);
            Assert.Equal(["pip-a", "pip-b"], stacked.Result.Segments.Select(segment => segment.Clip.Id).Order());
            Assert.Equal(onlyA.Samples.Length, stacked.Samples.Length);
            for (var i = 0; i < stacked.Samples.Length; i++)
                Assert.Equal(onlyA.Samples[i] + onlyB.Samples[i], stacked.Samples[i], 6);
            // Guard against the pair agreeing on silence, and against the old collapse.
            Assert.Contains(stacked.Samples, sample => sample != 0f);
            Assert.True(
                stacked.Samples.Max() > onlyA.Samples.Max(),
                "two stacked placements must be louder than one");
            // One child program serves every carrier of it: stacking must not double the decode.
            Assert.Single(stacked.Source.Requests);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeAsync_LinkGroupHoldingTwoDifferentNests_MixesBoth()
    {
        var path = Path.GetTempFileName();
        try
        {
            // Linking is a general grouping operation, so a group can hold carriers of two
            // different sequences. Only carriers of the SAME nest are one placement.
            var source = new RecordingAudioSource(new AudioBuffer(Enumerable.Repeat(0.4f, 10).ToArray(), 10, 1));
            var first = NestWithFlatTone("T-NEST-1", "inner-1");
            var second = NestWithFlatTone("T-NEST-2", "inner-2");
            var parent = new Timeline
            {
                Fps = 10,
                Tracks =
                [
                    new Track { Type = ClipType.video, Clips = [LinkedSeq("seq-1", first.Id, "lg-1", 1.0)] },
                    new Track { Type = ClipType.video, Clips = [LinkedSeq("seq-2", second.Id, "lg-1", 1.0)] },
                ],
            };

            var result = await new TimelineAudioDecoder(source).DecodeAsync(
                parent, _ => path, sampleRate: 10, channelCount: 1,
                timelineLookup: id => id == first.Id ? first : id == second.Id ? second : null);

            Assert.Equal(2, result.Segments.Count);
            Assert.Equal(["seq-1", "seq-2"], result.Segments.Select(segment => segment.Clip.Id).Order());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeAsync_ZeroDurationAudioLaneCarrier_DoesNotSilenceItsGroup()
    {
        var path = Path.GetTempFileName();
        try
        {
            // A carrier the walk drops must not win its group on the way past - it would take the
            // nest down with it.
            var source = new RecordingAudioSource(new AudioBuffer(Enumerable.Repeat(0.4f, 10).ToArray(), 10, 1));
            var nested = NestWithFlatTone();
            var empty = LinkedSeq("a-seq", nested.Id, "lg-1", 1.0);
            empty.DurationFrames = 0;
            var parent = new Timeline
            {
                Fps = 10,
                Tracks =
                [
                    new Track { Type = ClipType.video, Clips = [LinkedSeq("v-seq", nested.Id, "lg-1", 1.0)] },
                    new Track { Type = ClipType.audio, Clips = [empty] },
                ],
            };

            var result = await new TimelineAudioDecoder(source).DecodeAsync(
                parent, _ => path, sampleRate: 10, channelCount: 1,
                timelineLookup: id => id == nested.Id ? nested : null);

            var segment = Assert.Single(result.Segments);
            Assert.Equal("v-seq", segment.Clip.Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static Timeline NestWithFlatTone(string id = "T-NEST", string innerClipId = "inner") => new()
    {
        Id = id,
        Fps = 10,
        Tracks = [new Track { Type = ClipType.audio, Clips = [AudioClip(innerClipId, 0, 10)] }],
    };

    private static Clip Seq(string id, string nestedTimelineId) => new()
    {
        Id = id,
        MediaRef = nestedTimelineId,
        MediaType = ClipType.sequence,
        SourceClipType = ClipType.sequence,
        StartFrame = 0,
        DurationFrames = 10,
    };

    private static Clip LinkedSeq(string id, string nestedTimelineId, string linkGroupId, double volume)
    {
        var clip = Gained(Seq(id, nestedTimelineId), volume);
        clip.LinkGroupId = linkGroupId;
        return clip;
    }

    private static Clip Gained(Clip clip, double volume)
    {
        clip.Volume = volume;
        return clip;
    }

    [Fact]
    public void Program_UsesAbsoluteFrameOffsetsForSeekPositions()
    {
        var timeline = new Timeline { Fps = 24 };
        var program = TimelineAudioProgram.Create(timeline, [], sampleRate: 44_100, channelCount: 2);

        Assert.Equal(0, program.InterleavedSampleOffsetForFrame(0));
        Assert.Equal(3_676, program.InterleavedSampleOffsetForFrame(1));
        Assert.Equal(7_350, program.InterleavedSampleOffsetForFrame(2));
        Assert.Equal(program.Samples.Length, program.InterleavedSampleOffsetForFrame(24));
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

    private sealed class RecordingAudioSource(AudioBuffer buffer) : IAudioSource
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
            return Task.FromResult(buffer);
        }
    }
}
