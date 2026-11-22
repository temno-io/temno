using Temno.Core.Models;
using Temno.Media.Playback;

namespace Temno.Media.Audio;

/// <summary>
/// Peak levels for the audio meter (WP-75). Samples source media covering a
/// timeline frame via <see cref="IAudioSource"/> — not decorative noise.
/// Returns silence when inactive, muted, or no audio covers the frame.
/// </summary>
public sealed class TimelineAudioLevelSampler
{
    private readonly IAudioSource _source;
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private (float Left, float Right) _last = (0, 0);
    private string? _lastKey;

    public TimelineAudioLevelSampler(IAudioSource? source = null)
    {
        _source = source ?? new FfmpegAudioSource();
    }

    public float LeftPeak => _last.Left;
    public float RightPeak => _last.Right;

    public void Reset()
    {
        _last = (0, 0);
        _lastKey = null;
        _lastSampleUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Updates peaks for <paramref name="frame"/>. Throttled to ~12 Hz unless
    /// <paramref name="force"/>. When <paramref name="active"/> is false, zeros out.
    /// When <paramref name="timelineLookup"/> is supplied, a timeline whose only audio
    /// arrives through nested-sequence carriers meters the nested program (WP-172) —
    /// mapped to the nested frame with the same trim/speed arithmetic the decoder's
    /// <c>SliceProgramToClip</c> uses, gains multiplied down the carrier chain the way
    /// the decoder's submix applies them.
    /// </summary>
    public async Task<(float Left, float Right)> SampleAsync(
        Timeline timeline,
        Func<string, string?> resolvePath,
        int frame,
        bool active,
        bool force = false,
        CancellationToken cancellationToken = default,
        Func<string, Timeline?>? timelineLookup = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(resolvePath);

        if (!active)
        {
            Reset();
            return (0, 0);
        }

        var now = DateTime.UtcNow;
        if (!force && (now - _lastSampleUtc).TotalMilliseconds < 80)
            return _last;

        var clips = CollectCoveringAudio(timeline, frame, resolvePath).ToList();
        if (clips.Count == 0)
        {
            if (timelineLookup is not null
                && TryCollectNestedCoveringAudio(timeline, frame, resolvePath, timelineLookup, 0) is { } nested)
            {
                return await SampleNestedAsync(nested, force, now, cancellationToken).ConfigureAwait(false);
            }

            _last = (0, 0);
            _lastKey = "silence";
            _lastSampleUtc = now;
            return _last;
        }

        var (clip, path) = clips[0];
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _last = (0, 0);
            _lastSampleUtc = now;
            return _last;
        }

        var fps = Math.Max(1, timeline.Fps);
        var speed = clip.Speed <= 0 ? 1.0 : clip.Speed;
        var sourceSec = clip.TrimStartFrame / (double)fps
            + (frame - clip.StartFrame) / (double)fps * speed;
        var duration = Math.Max(0.04, 1.0 / fps);
        var key = $"{path}|{sourceSec:F3}|{clip.Volume}";
        if (!force && key == _lastKey)
        {
            _lastSampleUtc = now;
            return _last;
        }

        try
        {
            var buffer = await _source.ReadAudioAsync(
                path,
                sampleRate: 22050,
                channelCount: 2,
                startSeconds: Math.Max(0, sourceSec),
                durationSeconds: duration,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var (l, r) = PeakStereo(buffer.Samples, buffer.ChannelCount);
            var gain = (float)Math.Clamp(clip.VolumeAt(frame), 0, 4);
            _last = (Math.Clamp(l * gain, 0, 1), Math.Clamp(r * gain, 0, 1));
            _lastKey = key;
            _lastSampleUtc = now;
            return _last;
        }
        catch
        {
            _last = (0, 0);
            _lastSampleUtc = now;
            return _last;
        }
    }

    private static IEnumerable<(Clip Clip, string? Path)> CollectCoveringAudio(
        Timeline timeline,
        int frame,
        Func<string, string?> resolvePath)
    {
        foreach (var track in timeline.Tracks)
        {
            if (track.Muted || track.Hidden) continue;
            if (track.Type != ClipType.audio) continue;
            var previousEndFrame = int.MinValue;
            foreach (var clip in track.Clips.OrderBy(c => c.StartFrame))
            {
                if (clip.DurationFrames <= 0 || clip.StartFrame < previousEndFrame) continue;
                previousEndFrame = clip.EndFrame;
                if (frame < clip.StartFrame || frame >= clip.EndFrame) continue;
                if (IsSequenceClip(clip) || !double.IsFinite(clip.Speed) || clip.Speed <= 0) continue;
                if (clip.MediaType is not (ClipType.audio or ClipType.video)) continue;
                if (string.IsNullOrEmpty(clip.MediaRef)) continue;
                if (resolvePath(clip.MediaRef) is { Length: > 0 } path && File.Exists(path))
                    yield return (clip, path);
            }
        }
    }

    /// <summary>
    /// WP-172: nested-sequence fallback for the meter. A carrier whose
    /// <c>MediaRef</c> is a timeline id carries no manifest entry, so the direct pass above
    /// never sees it even though the decoder mixes it audibly. Maps the parent frame into the
    /// nested timeline and recurses, mirroring the decoder's occupancy rules (muted/hidden
    /// tracks contribute nothing, invalid speed is skipped) and its depth cap.
    /// Returns the inner file path, the nested-frame source position and the accumulated gain.
    /// </summary>
    private static (string Path, double SourceSeconds, double Gain, int NestedFrame, int NestedFps)? TryCollectNestedCoveringAudio(
        Timeline timeline,
        int frame,
        Func<string, string?> resolvePath,
        Func<string, Timeline?> timelineLookup,
        int depth)
    {
        if (depth >= TimelineFrameRenderer.MaxSequenceNestingDepth)
            return null;

        var fps = Math.Max(1, timeline.Fps);
        foreach (var track in timeline.Tracks)
        {
            if (track.Muted || track.Hidden) continue;
            if (track.Type is not (ClipType.audio or ClipType.video)) continue;
            var previousEndFrame = int.MinValue;
            foreach (var clip in track.Clips.OrderBy(c => c.StartFrame))
            {
                if (clip.DurationFrames <= 0 || clip.StartFrame < previousEndFrame) continue;
                previousEndFrame = clip.EndFrame;
                if (frame < clip.StartFrame || frame >= clip.EndFrame) continue;
                if (!IsSequenceClip(clip)) continue;
                if (string.IsNullOrWhiteSpace(clip.MediaRef)) continue;
                if (!double.IsFinite(clip.Speed) || clip.Speed <= 0) continue;
                if (timelineLookup(clip.MediaRef) is not { } nested) continue;

                // Same window the decoder slices: trim (parent frames) to seconds, then the
                // carrier offset stretched by Speed back into nested seconds.
                var trimSeconds = Math.Max(0, clip.TrimStartFrame) / (double)fps;
                var nestedSeconds = trimSeconds + (frame - clip.StartFrame) / (double)fps * clip.Speed;
                var nestedFps = Math.Max(1, nested.Fps);
                var nestedFrame = (int)Math.Floor(nestedSeconds * nestedFps);
                var carrierGain = clip.VolumeAt(frame);

                foreach (var direct in CollectCoveringAudio(nested, nestedFrame, resolvePath))
                {
                    var innerSpeed = direct.Clip.Speed <= 0 ? 1.0 : direct.Clip.Speed;
                    var innerSourceSec = direct.Clip.TrimStartFrame / (double)nestedFps
                        + (nestedFrame - direct.Clip.StartFrame) / (double)nestedFps * innerSpeed;
                    return (direct.Path ?? "", innerSourceSec, carrierGain * direct.Clip.VolumeAt(nestedFrame), nestedFrame, nestedFps);
                }

                if (TryCollectNestedCoveringAudio(nested, nestedFrame, resolvePath, timelineLookup, depth + 1) is { } deeper)
                    return (deeper.Path, deeper.SourceSeconds, carrierGain * deeper.Gain, deeper.NestedFrame, deeper.NestedFps);
            }
        }

        return null;
    }

    private static bool IsSequenceClip(Clip clip)
        => clip.SourceClipType == ClipType.sequence || clip.MediaType == ClipType.sequence;

    private async Task<(float Left, float Right)> SampleNestedAsync(
        (string Path, double SourceSeconds, double Gain, int NestedFrame, int NestedFps) nested,
        bool force,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(nested.Path) || !File.Exists(nested.Path))
        {
            _last = (0, 0);
            _lastSampleUtc = now;
            return _last;
        }

        var duration = Math.Max(0.04, 1.0 / nested.NestedFps);
        var key = $"nested|{nested.Path}|{nested.SourceSeconds:F3}|{nested.Gain:F4}";
        if (!force && key == _lastKey)
        {
            _lastSampleUtc = now;
            return _last;
        }

        try
        {
            var buffer = await _source.ReadAudioAsync(
                nested.Path,
                sampleRate: 22050,
                channelCount: 2,
                startSeconds: Math.Max(0, nested.SourceSeconds),
                durationSeconds: duration,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var (l, r) = PeakStereo(buffer.Samples, buffer.ChannelCount);
            var gain = (float)Math.Clamp(nested.Gain, 0, 4);
            _last = (Math.Clamp(l * gain, 0, 1), Math.Clamp(r * gain, 0, 1));
            _lastKey = key;
            _lastSampleUtc = now;
            return _last;
        }
        catch
        {
            _last = (0, 0);
            _lastSampleUtc = now;
            return _last;
        }
    }

    public static (float Left, float Right) PeakStereo(float[] interleaved, int channels)
    {
        if (interleaved is null || interleaved.Length == 0 || channels <= 0) return (0, 0);
        float l = 0, r = 0;
        if (channels == 1)
        {
            for (var i = 0; i < interleaved.Length; i++)
                l = Math.Max(l, Math.Abs(interleaved[i]));
            return (l, l);
        }

        for (var i = 0; i + 1 < interleaved.Length; i += channels)
        {
            l = Math.Max(l, Math.Abs(interleaved[i]));
            r = Math.Max(r, Math.Abs(interleaved[i + 1]));
        }
        return (l, r);
    }

    /// <summary>Reads the exact mixed PCM sent to the output device at this timeline frame.</summary>
    public static (float Left, float Right) PeakAtFrame(TimelineAudioProgram program, int frame)
    {
        ArgumentNullException.ThrowIfNull(program);
        if (frame < 0 || frame >= program.DurationFrames) return (0, 0);
        var start = program.InterleavedSampleOffsetForFrame(frame);
        var end = program.InterleavedSampleOffsetForFrame(frame + 1);
        float left = 0, right = 0;
        for (var i = start; i < end; i += program.ChannelCount)
        {
            left = Math.Max(left, Math.Abs(program.Samples[i]));
            right = Math.Max(right, Math.Abs(program.Samples[i + (program.ChannelCount > 1 ? 1 : 0)]));
        }
        return (Math.Clamp(left, 0, 1), Math.Clamp(right, 0, 1));
    }
}
