using System.Diagnostics;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Audio;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

public sealed class ExportAudioTests
{
    [Fact]
    public void MixAudioChunk_PreservesTimelineOffsetAndFades()
    {
        var clip = new Clip
        {
            Id = "tone",
            MediaRef = "tone",
            MediaType = ClipType.audio,
            SourceClipType = ClipType.audio,
            StartFrame = 4,
            DurationFrames = 8,
            Volume = 0.5,
            FadeInFrames = 2,
            FadeOutFrames = 2,
        };
        var segment = new AudioClipSegment(clip, Enumerable.Repeat(1f, 8).ToArray());

        var mixed = TimelineExporter.MixAudioChunk([segment], 0, 14, fps: 10, sampleRate: 10, channelCount: 1);

        Assert.Equal(14, mixed.Length);
        Assert.Equal(0f, mixed[3]);
        Assert.Equal(0f, mixed[4]);
        Assert.Equal(0.25f, mixed[5]);
        Assert.Equal(0.5f, mixed[6]);
        Assert.Equal(0.5f, mixed[10]);
        Assert.Equal(0.25f, mixed[11]);
        Assert.Equal(0f, mixed[12]);
    }

    [Fact]
    public async Task ExportAsync_MuxesNonSilentDurationAlignedSourceAudio()
    {
        if (!IsFfmpegAvailable()) return;

        var tempDirectory = CreateTempDirectory();
        try
        {
            var tonePath = Path.Combine(tempDirectory, "tone.wav");
            var outputPath = Path.Combine(tempDirectory, "export.mp4");
            await RunFfmpegAsync($"-y -f lavfi -i sine=frequency=880:sample_rate=48000:duration=1 -ac 2 -c:a pcm_f32le \"{tonePath}\"");

            var timeline = AudioTimeline(startFrame: 10, durationFrames: 10);
            timeline.Tracks[1].Clips[0].Effects =
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
            // The video clip's mediaRef must resolve too, or the layer is dropped and this test
            // silently measures a black export instead of the SolidFrameSource picture it intends.
            var manifest = new MediaManifest
            {
                Entries =
                [
                    new MediaManifestEntry { Id = "tone", Type = ClipType.audio, Source = MediaSource.External(tonePath) },
                    new MediaManifestEntry { Id = "black", Type = ClipType.video, Source = MediaSource.External(tonePath) },
                ],
            };
            var resolver = new MediaResolver(() => manifest, () => null);
            var exporter = new TimelineExporter(new CpuCompositor(), new SolidFrameSource(), resolver);

            await exporter.ExportAsync(timeline, new ExportPreset("test"), outputPath);

            var probe = await new FfmpegMediaProbe().ProbeAsync(outputPath);
            Assert.True(probe.HasVideo);
            Assert.True(probe.HasAudio);
            Assert.InRange(probe.DurationSeconds, 1.90, 2.10);

            var decoded = await new FfmpegAudioSource().ReadAudioAsync(outputPath, 48_000, 2);
            var earlyPeak = Peak(decoded.Samples, 0, 0.35, decoded.ChannelCount, decoded.SampleRate);
            var tonePeak = Peak(decoded.Samples, 0.60, 0.85, decoded.ChannelCount, decoded.SampleRate);
            Assert.True(tonePeak > 0.01f, "The exported AAC stream should contain the source tone.");
            Assert.True(earlyPeak < tonePeak * 0.15f, "Audio must remain silent before the timeline clip offset.");
            Assert.Empty(exporter.LastAudioDiagnostics);
            Assert.Contains(exporter.LastAudioEffects, effect => effect.State == TimelineAudioEffectState.Applied);
        }
        finally
        {
            TryDelete(tempDirectory);
        }
    }

    [Fact]
    public async Task ExportAsync_WithoutEnabledAudioTrack_WritesVideoOnly()
    {
        if (!IsFfmpegAvailable()) return;

        var tempDirectory = CreateTempDirectory();
        try
        {
            var outputPath = Path.Combine(tempDirectory, "video-only.mp4");
            var timeline = new Timeline
            {
                Fps = 20,
                Width = 64,
                Height = 64,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                MediaRef = "solid",
                                MediaType = ClipType.video,
                                SourceClipType = ClipType.video,
                                DurationFrames = 20,
                            },
                        ],
                    },
                ],
            };

            await new TimelineExporter(new CpuCompositor(), new SolidFrameSource())
                .ExportAsync(timeline, new ExportPreset("test"), outputPath);

            var probe = await new FfmpegMediaProbe().ProbeAsync(outputPath);
            Assert.True(probe.HasVideo);
            Assert.False(probe.HasAudio);
            Assert.InRange(probe.DurationSeconds, 0.90, 1.10);
        }
        finally
        {
            TryDelete(tempDirectory);
        }
    }

    [Fact]
    public async Task ExportAsync_PreferHardware_UsesNvencAtRealResolution()
    {
        if (!IsFfmpegAvailable()) return;
        if (!FfmpegEncoderCapabilities.IsEncoderAvailable("hevc_nvenc")) return;

        var tempDirectory = CreateTempDirectory();
        try
        {
            // Real 720p source so the persistent decode pipe and NVENC both engage.
            var clipPath = Path.Combine(tempDirectory, "clip.mp4");
            await RunFfmpegAsync(
                $"-y -f lavfi -i testsrc=duration=1:size=1280x720:rate=30 -pix_fmt yuv420p \"{clipPath}\"");

            var outputPath = Path.Combine(tempDirectory, "out.mp4");
            var timeline = new Timeline
            {
                Fps = 30,
                Width = 1280,
                Height = 720,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                MediaRef = clipPath,
                                MediaType = ClipType.video,
                                SourceClipType = ClipType.video,
                                StartFrame = 0,
                                DurationFrames = 30,
                            },
                        ],
                    },
                ],
            };

            using var frames = new ExportFrameSource();
            var exporter = new TimelineExporter(new CpuCompositor(), frames, null);
            await exporter.ExportAsync(
                timeline, new ExportPreset("t", VideoCodec.H265, ExportResolution.R720p), outputPath);

            Assert.Equal("hevc_nvenc", exporter.LastEncoderName);
            var probe = await new FfmpegMediaProbe().ProbeAsync(outputPath);
            Assert.True(probe.HasVideo);
            Assert.InRange(probe.DurationSeconds, 0.80, 1.20);
        }
        finally
        {
            TryDelete(tempDirectory);
        }
    }

    private static Timeline AudioTimeline(int startFrame, int durationFrames) => new()
    {
        Fps = 20,
        Width = 64,
        Height = 64,
        Tracks =
        [
            new Track
            {
                Type = ClipType.video,
                Clips =
                [
                    new Clip
                    {
                        MediaRef = "black",
                        MediaType = ClipType.video,
                        SourceClipType = ClipType.video,
                        DurationFrames = 40,
                    },
                ],
            },
            new Track
            {
                Type = ClipType.audio,
                Clips =
                [
                    new Clip
                    {
                        Id = "tone",
                        MediaRef = "tone",
                        MediaType = ClipType.audio,
                        SourceClipType = ClipType.audio,
                        StartFrame = startFrame,
                        DurationFrames = durationFrames,
                    },
                ],
            },
        ],
    };

    // ---- WP-165: nested-sequence audio through the real exporter ----
    //
    // Every other nested-audio test stops at TimelineAudioDecoder. This one runs the whole export
    // -- TimelineExporter, its timelineLookup, the mixer, ffmpeg's AAC muxer -- and reads the tone
    // back out of the encoded file, so removing `timelineLookup:` from the export call site fails
    // here instead of shipping a silent render.

    [Fact]
    public async Task ExportAsync_NestedSequenceAudio_ReachesTheEncodedFile()
    {
        if (!IsFfmpegAvailable()) return;

        var tempDirectory = CreateTempDirectory();
        try
        {
            var tonePath = Path.Combine(tempDirectory, "tone.wav");
            await RunFfmpegAsync(
                $"-y -f lavfi -i sine=frequency=880:sample_rate=48000:duration=2 -ac 2 -c:a pcm_f32le \"{tonePath}\"");

            var manifest = new MediaManifest
            {
                Entries =
                [
                    new MediaManifestEntry { Id = "tone", Type = ClipType.audio, Source = MediaSource.External(tonePath) },
                    new MediaManifestEntry { Id = "pic", Type = ClipType.video, Source = MediaSource.External(tonePath) },
                ],
            };
            var resolver = new MediaResolver(() => manifest, () => null);

            static Timeline Nested(bool muteTheAudioLane) => new()
            {
                Id = "T-NEST",
                Fps = 30,
                Width = 64,
                Height = 64,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "nested-pic", MediaRef = "pic",
                                MediaType = ClipType.video, SourceClipType = ClipType.video,
                                StartFrame = 0, DurationFrames = 60,
                            },
                        ],
                    },
                    new Track
                    {
                        Type = ClipType.audio,
                        Muted = muteTheAudioLane,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "nested-tone", MediaRef = "tone",
                                MediaType = ClipType.audio, SourceClipType = ClipType.audio,
                                StartFrame = 0, DurationFrames = 60,
                            },
                        ],
                    },
                ],
            };

            // The parent has no audio lane of its own: anything audible in the render arrived
            // through the sequence carrier.
            static Timeline Parent() => new()
            {
                Id = "T-MAIN",
                Fps = 30,
                Width = 64,
                Height = 64,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "carrier", MediaRef = "T-NEST",
                                MediaType = ClipType.sequence, SourceClipType = ClipType.sequence,
                                StartFrame = 0, DurationFrames = 60,
                            },
                        ],
                    },
                ],
            };

            async Task<float> ExportAndPeakAsync(bool muteTheAudioLane, string fileName)
            {
                var nested = Nested(muteTheAudioLane);
                var output = Path.Combine(tempDirectory, fileName);
                using var exporter = new TimelineExporter(
                    new CpuCompositor(),
                    new SolidFrameSource(),
                    resolver,
                    timelineLookup: id => id == nested.Id ? nested : null);
                await exporter.ExportAsync(Parent(), new ExportPreset("nested"), output);

                var probe = await new FfmpegMediaProbe().ProbeAsync(output);
                Assert.True(probe.HasVideo);
                if (!probe.HasAudio) return 0f;
                var decoded = await new FfmpegAudioSource().ReadAudioAsync(output, 48_000, 2);
                return Peak(decoded.Samples, 0.2, 1.8, decoded.ChannelCount, decoded.SampleRate);
            }

            var audible = await ExportAndPeakAsync(muteTheAudioLane: false, "nested-audible.mp4");
            Assert.True(audible > 0.01f, $"Nested-sequence audio must reach the encoded file (peak {audible}).");

            var muted = await ExportAndPeakAsync(muteTheAudioLane: true, "nested-muted.mp4");
            Assert.True(
                muted < audible * 0.05f,
                $"Muting the lane inside the nested sequence must silence the render (peak {muted} vs {audible}).");
        }
        finally
        {
            TryDelete(tempDirectory);
        }
    }

    // ---- WP-175: the linked pair and the stacked pair, through the real exporter ----
    //
    // The decoder tests prove the mix; this proves the render. Both shapes here changed audibly in
    // WP-175, and an encoded peak is the only receipt for an audio change that does not depend on
    // anyone listening. Decision and Mac citations: docs/verification/wp-175/DECISION.md.

    [Fact]
    public async Task ExportAsync_LinkedCarrierPair_TakesTheAudioLanesMuteAndGain()
    {
        if (!IsFfmpegAvailable()) return;

        var tempDirectory = CreateTempDirectory();
        try
        {
            var tonePath = Path.Combine(tempDirectory, "tone.wav");
            await RunFfmpegAsync(
                $"-y -f lavfi -i sine=frequency=880:sample_rate=48000:duration=2 -ac 2 -c:a pcm_f32le \"{tonePath}\"");

            var manifest = new MediaManifest
            {
                Entries =
                [
                    new MediaManifestEntry { Id = "tone", Type = ClipType.audio, Source = MediaSource.External(tonePath) },
                    new MediaManifestEntry { Id = "pic", Type = ClipType.video, Source = MediaSource.External(tonePath) },
                ],
            };
            var resolver = new MediaResolver(() => manifest, () => null);

            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = 30,
                Width = 64,
                Height = 64,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "nested-pic", MediaRef = "pic",
                                MediaType = ClipType.video, SourceClipType = ClipType.video,
                                StartFrame = 0, DurationFrames = 60,
                            },
                        ],
                    },
                    new Track
                    {
                        Type = ClipType.audio,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "nested-tone", MediaRef = "tone",
                                MediaType = ClipType.audio, SourceClipType = ClipType.audio,
                                StartFrame = 0, DurationFrames = 60,
                            },
                        ],
                    },
                ],
            };

            static Clip Carrier(string id, string? linkGroupId, double volume) => new()
            {
                Id = id,
                MediaRef = "T-NEST",
                MediaType = ClipType.sequence,
                SourceClipType = ClipType.sequence,
                StartFrame = 0,
                DurationFrames = 60,
                LinkGroupId = linkGroupId,
                Volume = volume,
            };

            // A Mac-authored shape: one nest carried by a linked pair of carriers, one per lane.
            // EditCommands.TrackAccepts will not build this here, which is exactly why it was
            // never noticed that the audio lane's fader and mute were being discarded.
            Timeline Parent(bool muteTheAudioLane) => new()
            {
                Id = "T-MAIN",
                Fps = 30,
                Width = 64,
                Height = 64,
                Tracks =
                [
                    new Track { Type = ClipType.video, Clips = [Carrier("v-seq", "lg-1", 1.0)] },
                    new Track
                    {
                        Type = ClipType.audio,
                        Muted = muteTheAudioLane,
                        Clips = [Carrier("a-seq", "lg-1", 0.25)],
                    },
                ],
            };

            async Task<float> ExportAndPeakAsync(Timeline parent, string fileName)
            {
                var output = Path.Combine(tempDirectory, fileName);
                using var exporter = new TimelineExporter(
                    new CpuCompositor(),
                    new SolidFrameSource(),
                    resolver,
                    timelineLookup: id => id == nested.Id ? nested : null);
                await exporter.ExportAsync(parent, new ExportPreset("linked"), output);

                var probe = await new FfmpegMediaProbe().ProbeAsync(output);
                Assert.True(probe.HasVideo);
                if (!probe.HasAudio) return 0f;
                var decoded = await new FfmpegAudioSource().ReadAudioAsync(output, 48_000, 2);
                return Peak(decoded.Samples, 0.2, 1.8, decoded.ChannelCount, decoded.SampleRate);
            }

            // The control: one unlinked carrier at the VIDEO lane's gain. Peaks are measured
            // against it rather than in absolute terms, because the encode chain has its own
            // scale factor - a full-level nested tone reads about 0.09 here, not 1.0.
            var full = await ExportAndPeakAsync(
                new Timeline
                {
                    Id = "T-MAIN", Fps = 30, Width = 64, Height = 64,
                    Tracks = [new Track { Type = ClipType.video, Clips = [Carrier("solo", null, 1.0)] }],
                },
                "linked-control.mp4");
            var audible = await ExportAndPeakAsync(Parent(muteTheAudioLane: false), "linked-audible.mp4");
            var muted = await ExportAndPeakAsync(Parent(muteTheAudioLane: true), "linked-muted.mp4");

            Assert.True(full > 0.01f, $"The control carrier must render (peak {full}).");
            Assert.True(
                audible > full * 0.1f,
                $"The linked pair must still render (peak {audible} against control {full}).");
            Assert.True(
                muted < audible * 0.05f,
                $"Muting the audio lane must silence the render (peak {muted} vs {audible}).");
            // The surviving gain is the audio lane's 0.25, not the video lane's 1.0. Falling back
            // to the video carrier would put this at the control's own level.
            Assert.InRange(audible / full, 0.15f, 0.45f);
        }
        finally
        {
            TryDelete(tempDirectory);
        }
    }

    [Fact]
    public async Task ExportAsync_StackedNestCarriers_RenderLouderThanOne()
    {
        if (!IsFfmpegAvailable()) return;

        var tempDirectory = CreateTempDirectory();
        try
        {
            var tonePath = Path.Combine(tempDirectory, "tone.wav");
            await RunFfmpegAsync(
                $"-y -f lavfi -i sine=frequency=880:sample_rate=48000:duration=2 -ac 2 -c:a pcm_f32le \"{tonePath}\"");

            var manifest = new MediaManifest
            {
                Entries =
                [
                    new MediaManifestEntry { Id = "tone", Type = ClipType.audio, Source = MediaSource.External(tonePath) },
                    new MediaManifestEntry { Id = "pic", Type = ClipType.video, Source = MediaSource.External(tonePath) },
                ],
            };
            var resolver = new MediaResolver(() => manifest, () => null);

            var nested = new Timeline
            {
                Id = "T-NEST",
                Fps = 30,
                Width = 64,
                Height = 64,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "nested-pic", MediaRef = "pic",
                                MediaType = ClipType.video, SourceClipType = ClipType.video,
                                StartFrame = 0, DurationFrames = 60,
                            },
                        ],
                    },
                    new Track
                    {
                        Type = ClipType.audio,
                        Clips =
                        [
                            new Clip
                            {
                                Id = "nested-tone", MediaRef = "tone",
                                MediaType = ClipType.audio, SourceClipType = ClipType.audio,
                                StartFrame = 0, DurationFrames = 60,
                            },
                        ],
                    },
                ],
            };

            // Two unlinked placements of one nest on two video lanes - the shape this app CAN
            // author. The renderer composites both; before WP-175 the mix carried one of them.
            static Clip Pip(string id, double volume) => new()
            {
                Id = id,
                MediaRef = "T-NEST",
                MediaType = ClipType.sequence,
                SourceClipType = ClipType.sequence,
                StartFrame = 0,
                DurationFrames = 60,
                Volume = volume,
            };

            Timeline Parent(params Clip[] carriers) => new()
            {
                Id = "T-MAIN",
                Fps = 30,
                Width = 64,
                Height = 64,
                Tracks = [.. carriers.Select(carrier => new Track { Type = ClipType.video, Clips = [carrier] })],
            };

            async Task<float> ExportAndPeakAsync(Timeline parent, string fileName)
            {
                var output = Path.Combine(tempDirectory, fileName);
                using var exporter = new TimelineExporter(
                    new CpuCompositor(),
                    new SolidFrameSource(),
                    resolver,
                    timelineLookup: id => id == nested.Id ? nested : null);
                await exporter.ExportAsync(parent, new ExportPreset("stacked"), output);

                var probe = await new FfmpegMediaProbe().ProbeAsync(output);
                Assert.True(probe.HasVideo);
                if (!probe.HasAudio) return 0f;
                var decoded = await new FfmpegAudioSource().ReadAudioAsync(output, 48_000, 2);
                return Peak(decoded.Samples, 0.2, 1.8, decoded.ChannelCount, decoded.SampleRate);
            }

            var one = await ExportAndPeakAsync(Parent(Pip("pip-a", 0.25)), "stacked-one.mp4");
            var two = await ExportAndPeakAsync(
                Parent(Pip("pip-a", 0.25), Pip("pip-b", 0.25)), "stacked-two.mp4");

            Assert.True(one > 0.01f, $"A single carrier must render (peak {one}).");
            Assert.True(
                two > one * 1.5f,
                $"Two stacked placements must render louder than one (peak {two} vs {one}).");
        }
        finally
        {
            TryDelete(tempDirectory);
        }
    }

    private static float Peak(float[] samples, double startSeconds, double endSeconds, int channels, int sampleRate)
    {
        var start = (int)Math.Floor(startSeconds * sampleRate) * channels;
        var end = Math.Min(samples.Length, (int)Math.Ceiling(endSeconds * sampleRate) * channels);
        var peak = 0f;
        for (var index = Math.Max(0, start); index < end; index++)
            peak = Math.Max(peak, Math.Abs(samples[index]));
        return peak;
    }

    private static bool IsFfmpegAvailable()
    {
        try
        {
            foreach (var tool in new[] { FFmpegResolver.GetFFmpegPath(), FFmpegResolver.GetFFprobePath() })
            {
                using var process = Process.Start(new ProcessStartInfo(tool, "-version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                process?.WaitForExit();
                if (process?.ExitCode != 0) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task RunFfmpegAsync(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(FFmpegResolver.GetFFmpegPath(), arguments)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Failed to start FFmpeg.");
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"FFmpeg fixture generation failed: {stderr}");
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"temno_export_audio_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }

    private sealed class SolidFrameSource : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(string mediaPath, double seconds, CancellationToken ct = default)
        {
            var pixels = new byte[64 * 64 * 4];
            for (var index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = 32;
                pixels[index + 1] = 64;
                pixels[index + 2] = 96;
                pixels[index + 3] = 255;
            }
            return Task.FromResult(new VideoFrame(64, 64, pixels, "rgba"));
        }
    }
}
