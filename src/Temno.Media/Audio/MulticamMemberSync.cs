namespace Temno.Media.Audio;

using Temno.Core.Export;
using Temno.Core.Models;

/// <summary>
/// Correlate multicam members to a master mediaRef via audio envelopes (WP-93),
/// with source-timecode fallback when audio is unavailable (WP-107).
/// </summary>
public static class MulticamMemberSync
{
    public sealed class Outcome
    {
        public Dictionary<string, MulticamSyncMap> Maps { get; } = new(StringComparer.Ordinal);
        public List<(string MediaRef, string Reason)> Failures { get; } = [];
    }

    public static async Task<Outcome> SyncAsync(
        string masterMediaRef,
        IReadOnlyList<(string MediaRef, double? PinnedOffsetSeconds)> members,
        Func<string, string?> resolvePath,
        double searchWindowSeconds = 240,
        double minConfidence = ClipSyncEngine.DefaultMinConfidence,
        IAudioSource? audioSource = null,
        Func<string, CancellationToken, Task<SourceTimecode?>>? readTimecode = null,
        CancellationToken cancellationToken = default)
    {
        var outcome = new Outcome();
        var source = audioSource ?? new FfmpegAudioSource();
        readTimecode ??= (path, ct) => FfmpegSourceTimecodeReader.ReadAsync(path, ct);
        var pending = new List<string>();

        foreach (var (mediaRef, pinned) in members)
        {
            if (pinned is { } p)
            {
                outcome.Maps[mediaRef] = new MulticamSyncMap
                {
                    OffsetSeconds = p,
                    Confidence = 1,
                    Locked = true,
                };
            }
            else if (mediaRef == masterMediaRef)
            {
                outcome.Maps[mediaRef] = new MulticamSyncMap { OffsetSeconds = 0, Confidence = 1 };
            }
            else
            {
                pending.Add(mediaRef);
            }
        }

        if (pending.Count == 0)
            return Rebase(outcome);

        var masterPath = resolvePath(masterMediaRef);
        if (string.IsNullOrEmpty(masterPath) || !File.Exists(masterPath))
        {
            outcome.Failures.Add((masterMediaRef, "Master has no readable path."));
            foreach (var refId in pending)
                outcome.Maps[refId] = new MulticamSyncMap();
            return outcome;
        }

        AudioEnvelope? masterEnv = null;
        try
        {
            masterEnv = await AudioEnvelopeExtractor.ExtractFromFileAsync(
                masterPath,
                startSeconds: 0,
                durationSeconds: searchWindowSeconds + 300,
                source,
                cancellationToken).ConfigureAwait(false);
            if (masterEnv.Samples.Length == 0) masterEnv = null;
        }
        catch
        {
            masterEnv = null;
        }

        if (masterEnv is null)
        {
            // No master audio — try pure timecode for each pending member.
            await ResolvePendingViaTimecodeAsync(
                    outcome, masterMediaRef, masterPath, pending, resolvePath, readTimecode,
                    audioFailReason: "No audio to sync with and no shared timecode.",
                    cancellationToken)
                .ConfigureAwait(false);
            return Rebase(outcome);
        }

        var hop = AudioEnvelopeExtractor.HopSeconds;
        var masterOffset = outcome.Maps.TryGetValue(masterMediaRef, out var mm) ? mm.OffsetSeconds : 0;
        SourceTimecode? masterTcCache = null;
        async Task<SourceTimecode?> MasterTc()
        {
            masterTcCache ??= await readTimecode(masterPath, cancellationToken).ConfigureAwait(false);
            return masterTcCache;
        }

        foreach (var refId in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = resolvePath(refId);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                outcome.Maps[refId] = new MulticamSyncMap();
                outcome.Failures.Add((refId, "No readable path to sync with."));
                continue;
            }

            AudioEnvelope? env = null;
            try
            {
                env = await AudioEnvelopeExtractor.ExtractFromFileAsync(
                    path, 0, searchWindowSeconds + 300, source, cancellationToken).ConfigureAwait(false);
                if (env.Samples.Length == 0) env = null;
            }
            catch
            {
                env = null;
            }

            if (env is not null)
            {
                var maxLag = MulticamMaxLagHops(searchWindowSeconds, hop, masterEnv.Samples.Length, env.Samples.Length);
                var minOverlap = Math.Max(
                    AudioSyncCorrelator.MinOverlapHops,
                    (int)Math.Round(ClipSyncEngine.MinOverlapSeconds / hop));
                minOverlap = Math.Min(minOverlap, Math.Max(AudioSyncCorrelator.MinOverlapHops,
                    Math.Min(masterEnv.Samples.Length, env.Samples.Length) / 3));

                var corr = AudioSyncCorrelator.SeededCorrelate(
                    masterEnv.Samples,
                    env.Samples,
                    seedHops: null,
                    seedWindowHops: maxLag,
                    maxLagHops: maxLag,
                    minOverlapHops: minOverlap,
                    minConfidence: minConfidence);

                if (corr is not null)
                {
                    var offset = masterOffset + corr.Value.LagHops * hop;
                    outcome.Maps[refId] = new MulticamSyncMap
                    {
                        OffsetSeconds = Math.Round(offset, 3),
                        Confidence = Math.Round(corr.Value.Confidence, 3),
                    };
                    continue;
                }
            }

            // Audio missing or failed — try source timecode.
            var masterTc = await MasterTc().ConfigureAwait(false);
            var memberTc = await readTimecode(path, cancellationToken).ConfigureAwait(false);
            if (masterTc is { } mtc && memberTc is { } ttc)
            {
                outcome.Maps[refId] = new MulticamSyncMap
                {
                    OffsetSeconds = Math.Round(masterOffset + ttc.Seconds - mtc.Seconds, 3),
                    Confidence = 1,
                };
                continue;
            }

            outcome.Maps[refId] = new MulticamSyncMap();
            outcome.Failures.Add((refId,
                env is null
                    ? "No readable audio to sync with and no shared timecode."
                    : "No confident audio alignment and no shared timecode — pin an offset or re-sync."));
        }

        return Rebase(outcome);
    }

    private static async Task ResolvePendingViaTimecodeAsync(
        Outcome outcome,
        string masterMediaRef,
        string masterPath,
        List<string> pending,
        Func<string, string?> resolvePath,
        Func<string, CancellationToken, Task<SourceTimecode?>> readTimecode,
        string audioFailReason,
        CancellationToken cancellationToken)
    {
        var masterOffset = outcome.Maps.TryGetValue(masterMediaRef, out var mm) ? mm.OffsetSeconds : 0;
        var masterTc = await readTimecode(masterPath, cancellationToken).ConfigureAwait(false);
        if (masterTc is null)
        {
            outcome.Failures.Add((masterMediaRef, "Master has no readable audio and no source timecode."));
            foreach (var refId in pending)
            {
                outcome.Maps[refId] = new MulticamSyncMap();
                outcome.Failures.Add((refId, audioFailReason));
            }
            return;
        }

        foreach (var refId in pending)
        {
            var path = resolvePath(refId);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                outcome.Maps[refId] = new MulticamSyncMap();
                outcome.Failures.Add((refId, audioFailReason));
                continue;
            }
            var memberTc = await readTimecode(path, cancellationToken).ConfigureAwait(false);
            if (memberTc is { } ttc)
            {
                outcome.Maps[refId] = new MulticamSyncMap
                {
                    OffsetSeconds = Math.Round(masterOffset + ttc.Seconds - masterTc.Value.Seconds, 3),
                    Confidence = 1,
                };
            }
            else
            {
                outcome.Maps[refId] = new MulticamSyncMap();
                outcome.Failures.Add((refId, audioFailReason));
            }
        }
    }

    public static int MulticamMaxLagHops(double windowSeconds, double hopSeconds, int referenceCount, int targetCount)
    {
        var windowHops = (int)Math.Round(windowSeconds / hopSeconds);
        return Math.Max(1, Math.Min(windowHops, Math.Min(referenceCount, targetCount) / 2));
    }

    private static Outcome Rebase(Outcome outcome)
    {
        var usable = outcome.Maps.Values.Where(m => m.Confidence > 0 || m.Locked).Select(m => m.OffsetSeconds).ToList();
        if (usable.Count == 0) return outcome;
        var baseOff = usable.Min();
        if (Math.Abs(baseOff) < 1e-9) return outcome;
        foreach (var key in outcome.Maps.Keys.ToList())
        {
            var map = outcome.Maps[key];
            if (map.Confidence <= 0 && !map.Locked) continue;
            map.OffsetSeconds = Math.Round(map.OffsetSeconds - baseOff, 3);
            outcome.Maps[key] = map;
        }
        return outcome;
    }
}
