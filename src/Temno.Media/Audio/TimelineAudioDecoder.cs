using Temno.Core.Models;
using Temno.Media.Playback;

namespace Temno.Media.Audio;

/// <summary>
/// Builds timeline-aligned PCM segments from the audio lanes of a project. Source media is decoded
/// in seconds; the resulting samples align to the timeline's frame clock for <see cref="AudioMixer"/>.
/// Mac reference: Preview/CompositionBuilder.swift insertAudioLane and insertClip.
/// Nested sequences (video-track carriers, mediaRef = nested timeline id) are mixed recursively.
/// </summary>
public sealed class TimelineAudioDecoder
{
    public const int DefaultSampleRate = 48_000;
    public const int DefaultChannelCount = 2;

    private readonly IAudioSource _source;

    public TimelineAudioDecoder(IAudioSource? source = null)
    {
        _source = source ?? new FfmpegAudioSource();
    }

    public async Task<TimelineAudioDecodeResult> DecodeAsync(
        Timeline timeline,
        Func<string, string?> resolvePath,
        int sampleRate = DefaultSampleRate,
        int channelCount = DefaultChannelCount,
        CancellationToken cancellationToken = default,
        Func<string, Timeline?>? timelineLookup = null,
        int nestingDepth = 0)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(resolvePath);
        if (timeline.Fps <= 0) throw new ArgumentOutOfRangeException(nameof(timeline), "Timeline fps must be positive.");
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channelCount <= 0) throw new ArgumentOutOfRangeException(nameof(channelCount));

        var segments = new List<AudioClipSegment>();
        var diagnostics = new List<TimelineAudioDiagnostic>();
        var effects = new List<TimelineAudioEffectStatus>();
        // WP-175: who speaks for a link group is decided over the whole timeline before the walk,
        // so a muted lane can still be the lane that decides, and so the answer cannot depend on
        // which track the walk happens to reach first.
        var linkedCarrierOwners = LinkedCarrierOwners(timeline);
        var nestedPrograms = new Dictionary<string, TimelineAudioProgram?>(StringComparer.Ordinal);

        foreach (var track in timeline.Tracks)
        {
            if (track.Muted || track.Hidden)
                continue;
            if (track.Type is not (ClipType.audio or ClipType.video))
                continue;

            var previousEndFrame = int.MinValue;
            foreach (var clip in track.Clips.OrderBy(clip => clip.StartFrame))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (clip.DurationFrames <= 0)
                {
                    if (track.Type == ClipType.audio || IsSequenceClip(clip))
                        diagnostics.Add(new TimelineAudioDiagnostic(clip.Id, "Skipped zero-duration clip."));
                    continue;
                }

                // One audio lane maps to one source track on Mac. A later overlap must not be
                // mixed twice, even when the preceding source was unavailable. Video tracks use
                // the same occupancy rule so a nested sequence does not mix under a prior clip.
                if (clip.StartFrame < previousEndFrame)
                {
                    if (track.Type == ClipType.audio)
                        diagnostics.Add(new TimelineAudioDiagnostic(clip.Id, "Skipped overlapping clip on the same audio track."));
                    continue;
                }

                previousEndFrame = clip.EndFrame;
                if (IsSequenceClip(clip))
                {
                    if (!SpeaksForItsLinkGroup(clip, linkedCarrierOwners))
                        continue;

                    await AppendNestedSequenceAudioAsync(
                            clip,
                            timeline,
                            resolvePath,
                            sampleRate,
                            channelCount,
                            cancellationToken,
                            timelineLookup,
                            nestingDepth,
                            segments,
                            diagnostics,
                            effects,
                            nestedPrograms)
                        .ConfigureAwait(false);
                    continue;
                }

                if (track.Type != ClipType.audio)
                    continue;

                if (string.IsNullOrWhiteSpace(clip.MediaRef))
                {
                    diagnostics.Add(new TimelineAudioDiagnostic(clip.Id, "Skipped clip without a media reference."));
                    continue;
                }

                if (!double.IsFinite(clip.Speed) || clip.Speed <= 0)
                {
                    diagnostics.Add(new TimelineAudioDiagnostic(clip.Id, "Skipped clip with an invalid playback speed."));
                    continue;
                }

                var path = resolvePath(clip.MediaRef);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    diagnostics.Add(new TimelineAudioDiagnostic(clip.Id, "Source media is offline.", path));
                    continue;
                }

                var sourceFrames = clip.Speed == 1.0
                    ? clip.DurationFrames
                    : Math.Max(1, (int)(clip.DurationFrames * clip.Speed));
                var sourceStartSeconds = Math.Max(0, clip.TrimStartFrame) / (double)timeline.Fps;
                var sourceDurationSeconds = sourceFrames / (double)timeline.Fps;

                try
                {
                    var decoded = await _source.ReadAudioAsync(
                        path,
                        sampleRate,
                        channelCount,
                        sourceStartSeconds,
                        sourceDurationSeconds,
                        cancellationToken).ConfigureAwait(false);
                    var targetSampleFrames = SampleFramesForClip(clip, timeline.Fps, sampleRate);
                    // WP-336: the nested-sequence rule. The source window is padded with silence, or
                    // trimmed, to the length the clip's trim and speed expect, and only then does the
                    // resample apply the speed. Stretching whatever the stream held across the clip
                    // slowed audio that ended early and slid it off its picture.
                    var sourceSampleFrames = clip.Speed == 1.0
                        ? targetSampleFrames
                        : checked((int)Math.Round(sourceDurationSeconds * sampleRate, MidpointRounding.AwayFromZero));
                    var window = FitToSampleFrames(decoded, sourceSampleFrames);
                    var samples = ResampleInterleaved(window, targetSampleFrames, channelCount);
                    samples = ApplyDenoise(clip, samples, sampleRate, channelCount, effects);
                    segments.Add(new AudioClipSegment(clip, samples));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    diagnostics.Add(new TimelineAudioDiagnostic(clip.Id, "Audio decode failed.", ex.Message));
                }
            }
        }

        return new TimelineAudioDecodeResult(segments, diagnostics, effects, sampleRate, channelCount);
    }

    private async Task AppendNestedSequenceAudioAsync(
        Clip clip,
        Timeline parentTimeline,
        Func<string, string?> resolvePath,
        int sampleRate,
        int channelCount,
        CancellationToken cancellationToken,
        Func<string, Timeline?>? timelineLookup,
        int nestingDepth,
        List<AudioClipSegment> segments,
        List<TimelineAudioDiagnostic> diagnostics,
        List<TimelineAudioEffectStatus> effects,
        Dictionary<string, TimelineAudioProgram?> nestedPrograms)
    {
        if (nestingDepth >= TimelineFrameRenderer.MaxSequenceNestingDepth)
        {
            diagnostics.Add(new TimelineAudioDiagnostic(
                clip.Id, "Nested-sequence audio omitted: nesting depth limit reached."));
            return;
        }

        var mediaRef = clip.MediaRef;
        if (timelineLookup is null
            || string.IsNullOrWhiteSpace(mediaRef)
            || timelineLookup(mediaRef) is not { } nestedTimeline)
        {
            diagnostics.Add(new TimelineAudioDiagnostic(
                clip.Id, "Nested-sequence audio omitted: nested timeline not found."));
            return;
        }

        if (!double.IsFinite(clip.Speed) || clip.Speed <= 0)
        {
            diagnostics.Add(new TimelineAudioDiagnostic(clip.Id, "Skipped clip with an invalid playback speed."));
            return;
        }

        // WP-175: stacked placements of one nest each contribute now, so the same child timeline
        // is reached more than once per parent walk. Its program is identical for every carrier of
        // it - only the trim/speed slice below differs - so it is decoded once, and the child's own
        // diagnostics are reported once rather than once per placement.
        if (!nestedPrograms.TryGetValue(mediaRef, out var program))
        {
            var nested = await DecodeAsync(
                    nestedTimeline,
                    resolvePath,
                    sampleRate,
                    channelCount,
                    cancellationToken,
                    timelineLookup,
                    nestingDepth + 1)
                .ConfigureAwait(false);
            diagnostics.AddRange(nested.Diagnostics);
            effects.AddRange(nested.Effects);
            // WP-165: a submix, not an output stage - see AudioMixer.Mix(clampOutput).
            program = nested.Segments.Count == 0 ? null : nested.MixAsSubmix(nestedTimeline);
            nestedPrograms[mediaRef] = program;
        }

        if (program is null)
            return;

        var samples = SliceProgramToClip(program, clip, parentTimeline.Fps, sampleRate, channelCount);
        ReportUnappliedCarrierEffects(clip, effects);
        segments.Add(new AudioClipSegment(CopyForNestedPlacement(clip), samples));
    }

    /// <summary>
    /// WP-165: an audio effect on the sequence clip itself is not applied - the nested program is
    /// already mixed by the time the carrier is placed, and per-clip denoise inside the nested
    /// timeline has run on its own clips. Saying so is the point: every other denoise outcome
    /// emits a status, and a silently dropped effect is the one failure this decoder's
    /// effect-truthfulness contract exists to prevent.
    /// </summary>
    private static void ReportUnappliedCarrierEffects(
        Clip clip, ICollection<TimelineAudioEffectStatus> statuses)
    {
        var effect = clip.Effects?.FirstOrDefault(candidate => candidate.Type == Clip.DenoiseEffectType);
        if (effect is null || !effect.Enabled)
            return;

        statuses.Add(new TimelineAudioEffectStatus(
            clip.Id,
            Clip.DenoiseEffectType,
            TimelineAudioEffectState.Unavailable,
            "Denoise on a nested-sequence clip is not applied; apply it to the clips inside the sequence."));
    }

    /// <summary>
    /// WP-175: the carrier that speaks for each link group, resolved over the whole timeline.
    ///
    /// A nested sequence reached through two linked carriers is one placement, not two, and the
    /// carrier on the AUDIO lane is the one whose volume, fades and track mute decide it. That is
    /// the Mac rule read straight off CompositionBuilder: build() sends audio tracks to
    /// insertAudioLane and everything else to insertVideoLane, and only insertAudioLane expands a
    /// carrier's audio (expandNestAudio) - insertVideoLane expands its picture alone. Mute follows
    /// from the same place: buildVisuals zeroes a nested submix from the top-level track the
    /// carrier sits on, which for nested audio is always that audio lane.
    ///
    /// It is scoped to groups, not applied globally, because EditCommands.TrackAccepts refuses a
    /// sequence clip on an audio track here, so every nest this app can author has its only carrier
    /// on a video lane. Silencing those would be the Mac rule applied to a shape the Mac cannot
    /// produce. Where no audio-lane carrier exists the video-lane carrier still speaks.
    ///
    /// Key is (link group, media ref): linking is a general grouping operation, so a group holding
    /// carriers of two different nests still mixes both. Zero-duration carriers are not candidates
    /// - the walk drops them - so one cannot win a group and silence it.
    /// </summary>
    private static Dictionary<(string Group, string MediaRef), string> LinkedCarrierOwners(Timeline timeline)
    {
        var held = new Dictionary<(string Group, string MediaRef), (string Id, bool OnAudioLane)>();
        foreach (var track in timeline.Tracks)
        {
            if (track.Type is not (ClipType.audio or ClipType.video))
                continue;

            var onAudioLane = track.Type == ClipType.audio;
            foreach (var clip in track.Clips)
            {
                if (clip.DurationFrames <= 0 || !IsSequenceClip(clip) || string.IsNullOrEmpty(clip.LinkGroupId))
                    continue;

                var key = (clip.LinkGroupId, clip.MediaRef ?? string.Empty);
                var candidate = (clip.Id, onAudioLane);
                if (!held.TryGetValue(key, out var incumbent) || Outranks(candidate, incumbent))
                    held[key] = candidate;
            }
        }

        return held.ToDictionary(entry => entry.Key, entry => entry.Value.Id);
    }

    /// <summary>
    /// A total order, so the winner is the same whatever order the tracks arrive in. The audio lane
    /// outranks the video lane; within one lane class the ordinal-lowest clip id wins. The tiebreak
    /// cannot be the track index - track order is the thing the result must not depend on - and clip
    /// ids are unique within a timeline and stable across save/reopen. It only ever fires on a group
    /// no authoring path produces (two carriers of one nest on two audio lanes); it exists so that
    /// shape has one defined answer rather than two.
    /// </summary>
    private static bool Outranks((string Id, bool OnAudioLane) candidate, (string Id, bool OnAudioLane) incumbent)
        => candidate.OnAudioLane != incumbent.OnAudioLane
            ? candidate.OnAudioLane
            : string.CompareOrdinal(candidate.Id, incumbent.Id) < 0;

    /// <summary>
    /// WP-175: an unlinked carrier always speaks for itself. Two deliberately stacked placements of
    /// one nest are two contributions, each at its own carrier's gain - which is already what
    /// TimelineFrameRenderer does with their picture. The placement key this replaced
    /// ((mediaRef, start, duration, trim)) called them a linked pair and dropped one.
    /// </summary>
    private static bool SpeaksForItsLinkGroup(
        Clip clip, IReadOnlyDictionary<(string Group, string MediaRef), string> owners)
    {
        if (string.IsNullOrEmpty(clip.LinkGroupId))
            return true;

        return !owners.TryGetValue((clip.LinkGroupId, clip.MediaRef ?? string.Empty), out var owner)
            || string.Equals(owner, clip.Id, StringComparison.Ordinal);
    }

    private static bool IsSequenceClip(Clip clip)
        => clip.SourceClipType == ClipType.sequence || clip.MediaType == ClipType.sequence;

    private static Clip CopyForNestedPlacement(Clip clip) => new()
    {
        Id = clip.Id,
        MediaRef = clip.MediaRef,
        MediaType = clip.MediaType,
        SourceClipType = clip.SourceClipType,
        StartFrame = clip.StartFrame,
        DurationFrames = clip.DurationFrames,
        Volume = clip.Volume,
        VolumeTrack = clip.VolumeTrack,
        FadeInFrames = clip.FadeInFrames,
        FadeOutFrames = clip.FadeOutFrames,
        FadeInInterpolation = clip.FadeInInterpolation,
        FadeOutInterpolation = clip.FadeOutInterpolation,
    };

    /// <summary>
    /// Maps the parent clip's trim/speed window onto nested-program PCM (seconds, like video
    /// source mapping), then resamples to the parent clip's timeline sample length.
    ///
    /// WP-165: the window is taken in samples straight from seconds. Rounding it to whole NESTED
    /// frames first — as this did — made the slice the wrong length whenever the nested timeline
    /// ran at a different fps from its parent, and the resample to the carrier's own length then
    /// turned that error into a rate warp: a 24 fps sequence on a 30 fps timeline played 7% fast,
    /// pitched up, and drifted against its own picture, which uses an unwarped per-frame mapping.
    /// Only <see cref="Clip.Speed"/> may retime nested audio.
    /// </summary>
    private static float[] SliceProgramToClip(
        TimelineAudioProgram program,
        Clip clip,
        int parentFps,
        int sampleRate,
        int channelCount)
    {
        var sourceFrames = clip.Speed == 1.0
            ? clip.DurationFrames
            : Math.Max(1, (int)(clip.DurationFrames * clip.Speed));
        var sourceStartSeconds = Math.Max(0, clip.TrimStartFrame) / (double)parentFps;
        var sourceDurationSeconds = sourceFrames / (double)parentFps;

        var startSample = (long)Math.Round(
            Math.Max(0, sourceStartSeconds) * sampleRate, MidpointRounding.AwayFromZero);
        var endSample = (long)Math.Round(
            (Math.Max(0, sourceStartSeconds) + Math.Max(0, sourceDurationSeconds)) * sampleRate,
            MidpointRounding.AwayFromZero);
        var durationSampleFrames = (int)Math.Max(0, endSample - startSample);
        var sliced = new float[checked(durationSampleFrames * channelCount)];

        var programFrames = program.ChannelCount == 0 ? 0 : program.Samples.Length / program.ChannelCount;
        if (durationSampleFrames > 0 && programFrames > 0 && channelCount > 0)
        {
            var srcStart = (int)Math.Max(0, startSample);
            var copyFrames = Math.Min(durationSampleFrames, Math.Max(0, programFrames - srcStart));
            if (copyFrames > 0)
            {
                Array.Copy(
                    program.Samples,
                    checked(srcStart * channelCount),
                    sliced,
                    0,
                    checked(copyFrames * channelCount));
            }
        }

        var targetSampleFrames = SampleFramesForClip(clip, parentFps, sampleRate);
        return ResampleInterleaved(new AudioBuffer(sliced, sampleRate, channelCount), targetSampleFrames, channelCount);
    }

    private static float[] ApplyDenoise(
        Clip clip,
        float[] samples,
        int sampleRate,
        int channelCount,
        ICollection<TimelineAudioEffectStatus> statuses)
    {
        var effect = clip.Effects?.FirstOrDefault(candidate => candidate.Type == Clip.DenoiseEffectType);
        if (effect is null)
            return samples;

        if (!effect.Enabled)
        {
            statuses.Add(new TimelineAudioEffectStatus(
                clip.Id, Clip.DenoiseEffectType, TimelineAudioEffectState.Bypassed,
                "Effect is disabled."));
            return samples;
        }

        var amount = effect.Params.TryGetValue("amount", out var parameter)
            ? parameter.Resolved(0, Clip.DefaultDenoiseAmount)
            : Clip.DefaultDenoiseAmount;
        if (!double.IsFinite(amount) || amount < 0 || amount > 1)
        {
            statuses.Add(new TimelineAudioEffectStatus(
                clip.Id, Clip.DenoiseEffectType, TimelineAudioEffectState.Unavailable,
                "Denoise amount must be a finite value in [0, 1]."));
            return samples;
        }

        if (amount == 0)
        {
            statuses.Add(new TimelineAudioEffectStatus(
                clip.Id, Clip.DenoiseEffectType, TimelineAudioEffectState.Bypassed,
                "Strength is 0."));
            return samples;
        }

        try
        {
            var result = DenoiseProcessor.Apply(samples, sampleRate, channelCount, amount);
            statuses.Add(new TimelineAudioEffectStatus(
                clip.Id, Clip.DenoiseEffectType,
                result.Applied ? TimelineAudioEffectState.Applied : TimelineAudioEffectState.Bypassed,
                result.Detail));
            return result.Samples;
        }
        catch (Exception ex)
        {
            statuses.Add(new TimelineAudioEffectStatus(
                clip.Id, Clip.DenoiseEffectType, TimelineAudioEffectState.Unavailable,
                $"Windows denoise could not be applied: {ex.Message}"));
            return samples;
        }
    }

    internal static int SampleFramesForClip(Clip clip, int fps, int sampleRate)
    {
        var frames = AudioMixer.SampleOffsetForFrame(clip.EndFrame, fps, sampleRate)
            - AudioMixer.SampleOffsetForFrame(clip.StartFrame, fps, sampleRate);
        if (frames < 0 || frames > int.MaxValue)
            throw new InvalidOperationException("Clip audio range is too large.");
        return (int)frames;
    }

    /// <summary>
    /// WP-336: <paramref name="source"/> cut to exactly <paramref name="sampleFrames"/>, or padded to it
    /// with silence. Samples keep their positions, so nothing before the cut or the pad moves.
    /// </summary>
    internal static AudioBuffer FitToSampleFrames(AudioBuffer source, int sampleFrames)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (sampleFrames < 0) throw new ArgumentOutOfRangeException(nameof(sampleFrames));

        var length = checked(sampleFrames * source.ChannelCount);
        if (source.Samples.Length == length)
            return source;

        var samples = new float[length];
        Array.Copy(source.Samples, samples, Math.Min(source.Samples.Length, length));
        return new AudioBuffer(samples, source.SampleRate, source.ChannelCount);
    }

    internal static float[] ResampleInterleaved(AudioBuffer source, int targetSampleFrames, int channelCount)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (targetSampleFrames < 0) throw new ArgumentOutOfRangeException(nameof(targetSampleFrames));
        if (channelCount <= 0) throw new ArgumentOutOfRangeException(nameof(channelCount));
        if (source.ChannelCount != channelCount)
            throw new InvalidOperationException("Decoded audio channel count does not match the requested mix format.");

        var output = new float[checked(targetSampleFrames * channelCount)];
        var sourceFrames = source.SampleFrameCount;
        if (sourceFrames == 0 || targetSampleFrames == 0)
            return output;

        if (sourceFrames == targetSampleFrames)
        {
            Array.Copy(source.Samples, output, output.Length);
            return output;
        }

        for (var outputFrame = 0; outputFrame < targetSampleFrames; outputFrame++)
        {
            var position = targetSampleFrames == 1
                ? 0.0
                : outputFrame * (sourceFrames - 1.0) / (targetSampleFrames - 1.0);
            var lower = (int)Math.Floor(position);
            var upper = Math.Min(lower + 1, sourceFrames - 1);
            var fraction = position - lower;
            for (var channel = 0; channel < channelCount; channel++)
            {
                var a = source.Samples[lower * channelCount + channel];
                var b = source.Samples[upper * channelCount + channel];
                output[outputFrame * channelCount + channel] = (float)(a + (b - a) * fraction);
            }
        }

        return output;
    }
}

public sealed record TimelineAudioDiagnostic(string ClipId, string Reason, string? Detail = null);

public enum TimelineAudioEffectState
{
    Applied,
    Bypassed,
    Unavailable,
}

/// <summary>Truthful effect state for callers that share decoded segments between preview and export.</summary>
public sealed record TimelineAudioEffectStatus(
    string ClipId,
    string EffectType,
    TimelineAudioEffectState State,
    string Detail);

public sealed class TimelineAudioDecodeResult
{
    public TimelineAudioDecodeResult(
        IReadOnlyList<AudioClipSegment> segments,
        IReadOnlyList<TimelineAudioDiagnostic> diagnostics,
        IReadOnlyList<TimelineAudioEffectStatus>? effects,
        int sampleRate,
        int channelCount)
    {
        Segments = segments;
        Diagnostics = diagnostics;
        Effects = effects ?? [];
        SampleRate = sampleRate;
        ChannelCount = channelCount;
    }

    public IReadOnlyList<AudioClipSegment> Segments { get; }
    public IReadOnlyList<TimelineAudioDiagnostic> Diagnostics { get; }
    public IReadOnlyList<TimelineAudioEffectStatus> Effects { get; }
    public int SampleRate { get; }
    public int ChannelCount { get; }

    /// <summary>The audible program: summed and clamped, ready for the device or the encoder.</summary>
    public TimelineAudioProgram Mix(Timeline timeline)
        => TimelineAudioProgram.Create(timeline, Segments, SampleRate, ChannelCount);

    /// <summary>
    /// WP-165: an intermediate bus for a nested sequence. Identical to <see cref="Mix"/> except
    /// that it keeps headroom above full scale, because the carrier clip's volume and fades are
    /// still to be applied. Clamping here would bake in distortion the carrier fader cannot undo.
    /// </summary>
    public TimelineAudioProgram MixAsSubmix(Timeline timeline)
        => TimelineAudioProgram.Create(timeline, Segments, SampleRate, ChannelCount, clampOutput: false);
}

/// <summary>One fully mixed PCM program whose positions are always derived from timeline frames.</summary>
public sealed class TimelineAudioProgram
{
    private TimelineAudioProgram(float[] samples, int sampleRate, int channelCount, int fps, int durationFrames)
    {
        Samples = samples;
        SampleRate = sampleRate;
        ChannelCount = channelCount;
        Fps = fps;
        DurationFrames = durationFrames;
    }

    public float[] Samples { get; }
    public int SampleRate { get; }
    public int ChannelCount { get; }
    public int Fps { get; }
    public int DurationFrames { get; }

    public int InterleavedSampleOffsetForFrame(int frame)
    {
        var sampleFrames = AudioMixer.SampleOffsetForFrame(Math.Clamp(frame, 0, DurationFrames), Fps, SampleRate);
        return checked((int)(sampleFrames * ChannelCount));
    }

    public static TimelineAudioProgram Create(
        Timeline timeline,
        IReadOnlyList<AudioClipSegment> segments,
        int sampleRate,
        int channelCount,
        bool clampOutput = true)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(segments);
        var durationFrames = Math.Max(timeline.TotalFrames, timeline.Fps);
        var samples = AudioMixer.Mix(
            segments, 0, durationFrames, timeline.Fps, sampleRate, channelCount, clampOutput);
        return new TimelineAudioProgram(samples, sampleRate, channelCount, timeline.Fps, durationFrames);
    }
}
