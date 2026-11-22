using Temno.Core.Export;
using Temno.Core.Models;
using Temno.Core.Session;

namespace Temno.Media.Audio;

/// <summary>
/// Mac: EditorViewModel.syncClips — audio-envelope alignment of target clips to a reference,
/// with source-timecode alignment (WP-107).
/// </summary>
public sealed class ClipSyncEngine
{
    public const double DefaultSearchWindowSeconds = 30;
    public const double DefaultMinConfidence = 0.5;
    public const double MinOverlapSeconds = 3;
    public const double MinSpeed = 0.0001;

    public enum SyncMode
    {
        auto,
        audio,
        timecode,
    }

    public enum SyncMethod
    {
        audio,
        timecode,
    }

    public sealed class SyncHit
    {
        public required string ClipId { get; init; }
        public int OffsetFrames { get; init; }
        public double Confidence { get; init; }
        public required string Method { get; init; }
    }

    public sealed class SyncFailure
    {
        public required string ClipId { get; init; }
        public required string Reason { get; init; }
    }

    public sealed class SyncBatchReport
    {
        public List<SyncHit> Synced { get; } = [];
        public List<SyncFailure> Failures { get; } = [];
        public int ShiftedFrames { get; set; }
    }

    private readonly IAudioSource _audioSource;
    private readonly Func<string, string?> _resolvePath;
    private readonly Func<string, CancellationToken, Task<SourceTimecode?>> _readTimecode;

    public ClipSyncEngine(
        Func<string, string?> resolvePath,
        IAudioSource? audioSource = null,
        Func<string, CancellationToken, Task<SourceTimecode?>>? readTimecode = null)
    {
        _resolvePath = resolvePath ?? throw new ArgumentNullException(nameof(resolvePath));
        _audioSource = audioSource ?? new FfmpegAudioSource();
        _readTimecode = readTimecode ?? ((path, ct) => FfmpegSourceTimecodeReader.ReadAsync(path, ct));
    }

    /// <summary>Mac: EditorViewModel.timecodeAlignedStart.</summary>
    public static int TimecodeAlignedStart(
        int refStartFrame,
        int refTrimStartFrame,
        double refSpeed,
        SourceTimecode refTimecode,
        int targetTrimStartFrame,
        SourceTimecode targetTimecode,
        double fps)
    {
        fps = Math.Max(1e-9, fps);
        var refClock = refTimecode.Seconds + refTrimStartFrame / fps;
        var targetClock = targetTimecode.Seconds + targetTrimStartFrame / fps;
        var lagFrames = (targetClock - refClock) * fps / Math.Max(refSpeed, MinSpeed);
        return (int)Math.Round(refStartFrame + lagFrames);
    }

    public async Task<SyncBatchReport> SyncAsync(
        ProjectSession session,
        string referenceClipId,
        IReadOnlyList<string> targetClipIds,
        SyncMode mode = SyncMode.auto,
        double searchWindowSeconds = DefaultSearchWindowSeconds,
        double minConfidence = DefaultMinConfidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(targetClipIds);

        var report = new SyncBatchReport();
        var timeline = session.ActiveTimeline;
        var fps = Math.Max(1, timeline.Fps);

        if (!TryFind(session, referenceClipId, out var refClip, out var refTrackIndex) || refClip is null)
        {
            foreach (var id in targetClipIds.Where(t => t != referenceClipId).Distinct())
                report.Failures.Add(new SyncFailure { ClipId = id, Reason = "Reference clip unavailable." });
            return report;
        }

        if (mode == SyncMode.timecode)
        {
            return await SyncByTimecodeAsync(session, referenceClipId, refClip, targetClipIds, report, fps, cancellationToken)
                .ConfigureAwait(false);
        }

        var refUnitKey = refClip.LinkGroupId ?? refClip.Id;
        var hop = AudioEnvelopeExtractor.HopSeconds;
        var maxLag = Math.Max(1, (int)Math.Round(searchWindowSeconds / hop));

        AudioEnvelope? refEnv;
        try
        {
            refEnv = await EnvelopeForClipAsync(refClip, fps, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            foreach (var id in targetClipIds.Where(t => t != referenceClipId).Distinct())
                report.Failures.Add(new SyncFailure
                {
                    ClipId = id,
                    Reason = "Reference clip has no audio: " + ex.Message,
                });
            return report;
        }

        if (refEnv.Samples.Length == 0)
        {
            foreach (var id in targetClipIds.Where(t => t != referenceClipId).Distinct())
                report.Failures.Add(new SyncFailure { ClipId = id, Reason = "Reference clip has no audio." });
            return report;
        }

        var seenUnits = new HashSet<string>(StringComparer.Ordinal);
        var placements = new List<(string ClipId, int NewStart, double Confidence, int CurrentStart, int TrackIndex)>();

        foreach (var targetId in targetClipIds.Distinct(StringComparer.Ordinal))
        {
            if (targetId == referenceClipId) continue;
            if (!TryFind(session, targetId, out var targetClip, out var targetTrack) || targetClip is null)
            {
                report.Failures.Add(new SyncFailure { ClipId = targetId, Reason = "Clip not found." });
                continue;
            }

            var unitKey = targetClip.LinkGroupId ?? targetClip.Id;
            if (unitKey == refUnitKey)
            {
                report.Failures.Add(new SyncFailure
                {
                    ClipId = targetId,
                    Reason = "Clip is linked to the reference — they already move together.",
                });
                continue;
            }
            if (!seenUnits.Add(unitKey))
                continue;

            // Prefer audio clip in link group for correlation bearer.
            var bearer = ResolveAudioBearer(session, targetClip);
            if (bearer is null)
            {
                report.Failures.Add(new SyncFailure { ClipId = targetId, Reason = "Clip has no audio." });
                continue;
            }

            AudioEnvelope tgtEnv;
            try
            {
                tgtEnv = await EnvelopeForClipAsync(bearer, fps, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                report.Failures.Add(new SyncFailure
                {
                    ClipId = bearer.Id,
                    Reason = "Clip has no audio: " + ex.Message,
                });
                continue;
            }

            if (tgtEnv.Samples.Length == 0)
            {
                report.Failures.Add(new SyncFailure { ClipId = bearer.Id, Reason = "Clip has no audio." });
                continue;
            }

            // Prefer Mac 3s min-overlap, but shorten for brief clips so short dual-system still works.
            var minOverlap = Math.Max(
                AudioSyncCorrelator.MinOverlapHops,
                (int)Math.Round(MinOverlapSeconds / hop));
            var available = Math.Min(refEnv.Samples.Length, tgtEnv.Samples.Length);
            minOverlap = Math.Min(minOverlap, Math.Max(AudioSyncCorrelator.MinOverlapHops, available / 3));

            var corr = AudioSyncCorrelator.SeededCorrelate(
                refEnv.Samples,
                tgtEnv.Samples,
                seedHops: null,
                seedWindowHops: maxLag,
                maxLagHops: maxLag,
                minOverlapHops: minOverlap,
                minConfidence: minConfidence);

            if (corr is null)
            {
                report.Failures.Add(new SyncFailure
                {
                    ClipId = bearer.Id,
                    Reason = "No confident alignment — clips may not overlap.",
                });
                continue;
            }

            var lagFrames = corr.Value.LagHops * hop * fps / Math.Max(refClip.Speed, MinSpeed);
            var newStart = (int)Math.Round(refClip.StartFrame + lagFrames);
            TryFind(session, bearer.Id, out _, out var bearerTrack);
            placements.Add((bearer.Id, newStart, corr.Value.Confidence, bearer.StartFrame, bearerTrack));
        }

        if (placements.Count == 0)
            return report;

        // Desired absolute starts for each bearer (before global shift).
        var desired = new Dictionary<string, (int Track, int Start, double Confidence, int OriginalStart)>(
            StringComparer.Ordinal);
        foreach (var p in placements)
        {
            desired[p.ClipId] = (p.TrackIndex, p.NewStart, p.Confidence, p.CurrentStart);
            // Linked partners keep relative offset to bearer.
            if (!TryFind(session, p.ClipId, out var bearer, out _) || bearer is null) continue;
            var delta = p.NewStart - p.CurrentStart;
            foreach (var partner in LinkedPartners(session, bearer))
            {
                if (partner.Id == p.ClipId) continue;
                if (!TryFind(session, partner.Id, out var pc, out var pt) || pc is null) continue;
                desired[partner.Id] = (pt, pc.StartFrame + delta, p.Confidence, pc.StartFrame);
            }
        }

        var minStart = desired.Values.Min(v => v.Start);
        var shift = Math.Max(0, -minStart);
        report.ShiftedFrames = shift;

        if (shift > 0)
        {
            // Shift reference unit too so relative sync is preserved.
            if (TryFind(session, referenceClipId, out var rc, out var rt) && rc is not null)
            {
                foreach (var partner in LinkedPartners(session, rc))
                {
                    if (!TryFind(session, partner.Id, out var pc, out var pt) || pc is null) continue;
                    desired[partner.Id] = (pt, pc.StartFrame + shift, 1.0, pc.StartFrame);
                }
            }

            foreach (var key in desired.Keys.ToList())
            {
                var v = desired[key];
                desired[key] = (v.Track, v.Start + shift, v.Confidence, v.OriginalStart);
            }
        }

        var moves = desired
            .Where(kv =>
            {
                if (!TryFind(session, kv.Key, out var c, out _) || c is null) return false;
                return c.StartFrame != kv.Value.Start;
            })
            .Select(kv => new ClipMove(kv.Key, kv.Value.Track, kv.Value.Start))
            .ToList();

        foreach (var p in placements)
        {
            var finalStart = desired.TryGetValue(p.ClipId, out var d) ? d.Start : p.NewStart;
            report.Synced.Add(new SyncHit
            {
                ClipId = p.ClipId,
                OffsetFrames = finalStart - p.CurrentStart,
                Confidence = Math.Round(p.Confidence, 3),
                Method = nameof(SyncMethod.audio),
            });
        }

        if (moves.Count > 0)
        {
            var result = EditCommands.MoveClips(session, moves, overwrite: false);
            if (!result.Ok)
            {
                report.Failures.Add(new SyncFailure
                {
                    ClipId = referenceClipId,
                    Reason = result.Error ?? "Move clips failed.",
                });
                report.Synced.Clear();
            }
        }

        return report;
    }

    private async Task<SyncBatchReport> SyncByTimecodeAsync(
        ProjectSession session,
        string referenceClipId,
        Clip refClip,
        IReadOnlyList<string> targetClipIds,
        SyncBatchReport report,
        int fps,
        CancellationToken cancellationToken)
    {
        var refPath = _resolvePath(refClip.MediaRef);
        if (string.IsNullOrEmpty(refPath))
        {
            foreach (var id in targetClipIds.Where(t => t != referenceClipId).Distinct())
                report.Failures.Add(new SyncFailure { ClipId = id, Reason = "Reference media has no readable path." });
            return report;
        }

        var refTc = await _readTimecode(refPath, cancellationToken).ConfigureAwait(false);
        if (refTc is null)
        {
            foreach (var id in targetClipIds.Where(t => t != referenceClipId).Distinct())
                report.Failures.Add(new SyncFailure
                {
                    ClipId = id,
                    Reason = "Reference has no source timecode; use mode audio or auto.",
                });
            return report;
        }

        var refUnitKey = refClip.LinkGroupId ?? refClip.Id;
        var seenUnits = new HashSet<string>(StringComparer.Ordinal);
        var placements = new List<(string ClipId, int NewStart, double Confidence, int CurrentStart, int TrackIndex)>();

        foreach (var targetId in targetClipIds.Distinct(StringComparer.Ordinal))
        {
            if (targetId == referenceClipId) continue;
            if (!TryFind(session, targetId, out var targetClip, out var targetTrack) || targetClip is null)
            {
                report.Failures.Add(new SyncFailure { ClipId = targetId, Reason = "Clip not found." });
                continue;
            }

            var unitKey = targetClip.LinkGroupId ?? targetClip.Id;
            if (unitKey == refUnitKey)
            {
                report.Failures.Add(new SyncFailure
                {
                    ClipId = targetId,
                    Reason = "Clip is linked to the reference — they already move together.",
                });
                continue;
            }
            if (!seenUnits.Add(unitKey)) continue;

            var path = _resolvePath(targetClip.MediaRef);
            if (string.IsNullOrEmpty(path))
            {
                report.Failures.Add(new SyncFailure { ClipId = targetId, Reason = "Media offline or unresolved." });
                continue;
            }

            var targetTc = await _readTimecode(path, cancellationToken).ConfigureAwait(false);
            if (targetTc is null)
            {
                report.Failures.Add(new SyncFailure
                {
                    ClipId = targetId,
                    Reason = "Source timecode unavailable for this media.",
                });
                continue;
            }

            var newStart = TimecodeAlignedStart(
                refClip.StartFrame,
                refClip.TrimStartFrame,
                refClip.Speed,
                refTc.Value,
                targetClip.TrimStartFrame,
                targetTc.Value,
                fps);
            placements.Add((targetClip.Id, newStart, 1.0, targetClip.StartFrame, targetTrack));
        }

        if (placements.Count == 0)
            return report;

        var desired = new Dictionary<string, (int Track, int Start, double Confidence, int OriginalStart)>(
            StringComparer.Ordinal);
        foreach (var p in placements)
        {
            desired[p.ClipId] = (p.TrackIndex, p.NewStart, p.Confidence, p.CurrentStart);
            if (!TryFind(session, p.ClipId, out var bearer, out _) || bearer is null) continue;
            var delta = p.NewStart - p.CurrentStart;
            foreach (var partner in LinkedPartners(session, bearer))
            {
                if (partner.Id == p.ClipId) continue;
                if (!TryFind(session, partner.Id, out var pc, out var pt) || pc is null) continue;
                desired[partner.Id] = (pt, pc.StartFrame + delta, p.Confidence, pc.StartFrame);
            }
        }

        var minStart = desired.Values.Min(v => v.Start);
        var shift = Math.Max(0, -minStart);
        report.ShiftedFrames = shift;
        if (shift > 0)
        {
            if (TryFind(session, referenceClipId, out var rc, out _) && rc is not null)
            {
                foreach (var partner in LinkedPartners(session, rc))
                {
                    if (!TryFind(session, partner.Id, out var pc, out var pt) || pc is null) continue;
                    desired[partner.Id] = (pt, pc.StartFrame + shift, 1.0, pc.StartFrame);
                }
            }
            foreach (var key in desired.Keys.ToList())
            {
                var v = desired[key];
                desired[key] = (v.Track, v.Start + shift, v.Confidence, v.OriginalStart);
            }
        }

        var moves = desired
            .Where(kv =>
            {
                if (!TryFind(session, kv.Key, out var c, out _) || c is null) return false;
                return c.StartFrame != kv.Value.Start;
            })
            .Select(kv => new ClipMove(kv.Key, kv.Value.Track, kv.Value.Start))
            .ToList();

        foreach (var p in placements)
        {
            var finalStart = desired.TryGetValue(p.ClipId, out var d) ? d.Start : p.NewStart;
            report.Synced.Add(new SyncHit
            {
                ClipId = p.ClipId,
                OffsetFrames = finalStart - p.CurrentStart,
                Confidence = 1,
                Method = nameof(SyncMethod.timecode),
            });
        }

        if (moves.Count > 0)
        {
            var result = EditCommands.MoveClips(session, moves, overwrite: false);
            if (!result.Ok)
            {
                report.Failures.Add(new SyncFailure
                {
                    ClipId = referenceClipId,
                    Reason = result.Error ?? "Move clips failed.",
                });
                report.Synced.Clear();
            }
        }

        return report;
    }

    private async Task<AudioEnvelope> EnvelopeForClipAsync(Clip clip, int fps, CancellationToken ct)
    {
        var path = _resolvePath(clip.MediaRef)
            ?? throw new InvalidOperationException("Media offline or unresolved.");
        var startSec = clip.TrimStartFrame / (double)fps;
        var durationSec = clip.DurationFrames * Math.Max(clip.Speed, MinSpeed) / fps;
        if (durationSec <= 0) durationSec = 0.1;
        return await AudioEnvelopeExtractor.ExtractFromFileAsync(
            path, startSec, durationSec, _audioSource, ct).ConfigureAwait(false);
    }

    private static Clip? ResolveAudioBearer(ProjectSession session, Clip target)
    {
        var unit = LinkedPartners(session, target);
        return unit.FirstOrDefault(c => c.MediaType == ClipType.audio)
            ?? unit.FirstOrDefault(c => c.MediaType == ClipType.video)
            ?? unit.FirstOrDefault();
    }

    private static List<Clip> LinkedPartners(ProjectSession session, Clip clip)
    {
        if (string.IsNullOrEmpty(clip.LinkGroupId))
            return [clip];
        return session.ActiveTimeline.Tracks
            .SelectMany(t => t.Clips)
            .Where(c => c.LinkGroupId == clip.LinkGroupId)
            .ToList();
    }

    private static bool TryFind(ProjectSession session, string clipId, out Clip? clip, out int trackIndex)
    {
        clip = null;
        trackIndex = -1;
        var tracks = session.ActiveTimeline.Tracks;
        for (var i = 0; i < tracks.Count; i++)
        {
            var c = tracks[i].Clips.FirstOrDefault(x => x.Id == clipId);
            if (c is null) continue;
            clip = c;
            trackIndex = i;
            return true;
        }
        return false;
    }
}
