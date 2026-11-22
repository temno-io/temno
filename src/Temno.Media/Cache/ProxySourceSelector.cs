using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Temno.Media.Cache;

/// <summary>State of the scrub proxy for one media file, as the preview surface should show it.</summary>
public enum ProxyState
{
    /// <summary>The source is already cheap enough to decode; no proxy will be made.</summary>
    NotWarranted,

    /// <summary>A transcode is queued or running. Preview decodes the original meanwhile.</summary>
    Generating,

    /// <summary>A proxy exists on disk and preview is decoding it.</summary>
    Ready,

    /// <summary>
    /// The transcode failed, or the proxy went missing after being handed out (an eviction or an
    /// external delete). Preview stays on the original; not retried for the rest of the session.
    /// </summary>
    Failed,
}

/// <summary>One media file's proxy status. <paramref name="ProxyPath"/> is set only when Ready.</summary>
public readonly record struct ProxyStatus(
    string MediaPath,
    ProxyState State,
    string? ProxyPath,
    int TargetHeight,
    string? Detail);

/// <summary>
/// WP-164. Decides, per media file, whether <b>preview</b> decodes the original or a cached scrub
/// proxy, and generates the proxy in the background the first time preview asks for that file.
///
/// <para><b>Export never sees this.</b> Export decodes through <c>ExportFrameSource</c>, which takes
/// an <see cref="IMediaProbe"/> and nothing else, and <c>TimelineExporter</c> hands it the path the
/// media resolver produced. A proxy can only enter a decode through
/// <see cref="SequentialFrameSource"/>, which is the preview frame source. That is the
/// export-uses-original guarantee, and it is structural rather than a runtime check.</para>
///
/// <para><b>Trigger.</b> First preview demand, not import. Proxying at import would transcode
/// footage the owner may never scrub — on the 25.9 GB source that motivated this package, a
/// full-file transcode is minutes of disk and CPU spent before the first frame is drawn. Instead the
/// first restart on a heavy file returns the original and queues the transcode; when it lands, the
/// next restart picks the proxy up. Preview is never blocked on a transcode.</para>
///
/// <para><b>Invalidation.</b> Free, and it comes from the key:
/// <see cref="MediaCacheKey"/> hashes path + size + mtime + target height, so replacing or
/// re-rendering the source yields a different key and the old proxy is simply never looked up again
/// (it ages out under the LRU budget). Trim range and clip speed deliberately do <i>not</i> enter
/// the key: a proxy is a whole-file substitute addressed by source-seconds exactly like the
/// original, so re-trimming or retiming a clip cannot make one stale.</para>
/// </summary>
public sealed class ProxySourceSelector : IDisposable
{
    private readonly ProxyCache _cache;
    private int _previewMaxDimension;
    private int _generation;
    private readonly Func<string, string, int, CancellationToken, Task>? _transcoder;
    private readonly CancellationTokenSource _shutdown = new();
    private CancellationTokenSource _generationCts;

    /// <summary>One transcode at a time: ffmpeg re-encoding a 4K source will starve preview decode.</summary>
    private readonly SemaphoreSlim _transcodeGate = new(1, 1);

    private readonly object _gate = new();
    private readonly Dictionary<string, ProxyStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Keys (path + size + mtime + height) whose transcode failed, so a broken source is not
    /// retried on every restart. Keyed by <see cref="MediaCacheKey"/> identity rather than by path:
    /// keying by path alone would mean that once a file failed, replacing it with different content
    /// at the same path could never be proxied again for the life of the session - the opposite of
    /// the invalidation this class documents. WP-164 T1 finding.
    /// </summary>
    private readonly HashSet<string> _failedKeys = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <param name="transcoder">
    /// Overrides the ffmpeg transcode: (sourcePath, targetPath, targetHeight, ct). Tests inject one
    /// so proxy selection can be verified without ffmpeg on the machine.
    /// </param>
    public ProxySourceSelector(
        ProxyCache cache,
        int previewMaxDimension = PreviewRaster.PreviewMaxDimension,
        Func<string, string, int, CancellationToken, Task>? transcoder = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (previewMaxDimension <= 0) throw new ArgumentOutOfRangeException(nameof(previewMaxDimension));
        _cache = cache;
        _previewMaxDimension = previewMaxDimension;
        _transcoder = transcoder;
        _generationCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
    }

    /// <summary>
    /// Raised when a file's proxy state changes. Fires on a background thread; the preview surface
    /// must marshal to the UI thread itself.
    /// </summary>
    public event Action<ProxyStatus>? StateChanged;

    public ProxyCache Cache => _cache;

    /// <summary>
    /// Target long-edge limit that preview decodes at. Changing it cancels any queued or running
    /// transcodes for the previous resolution and clears per-file statuses so the next decode
    /// re-evaluates policy and target height cleanly.
    /// </summary>
    public int PreviewMaxDimension
    {
        get => Volatile.Read(ref _previewMaxDimension);
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (Interlocked.Exchange(ref _previewMaxDimension, value) == value) return;
            CancelPendingTranscodes();
        }
    }

    /// <summary>
    /// Cancels any queued or running proxy transcodes (for example on timeline switch or preview
    /// resolution change) so outgoing timelines cannot keep burning CPU/disk behind the active one.
    /// </summary>
    public void CancelPendingTranscodes()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_disposed) return;
            Interlocked.Increment(ref _generation);
            previous = _generationCts;
            _generationCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _statuses.Clear();
            _inFlight.Clear();
        }

        // Deliberately cancel without disposing (see Dispose): an in-flight background worker may
        // still be unwinding its wait/registration on this CTS.
        try { previous.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Proxies generated by this instance. Diagnostic hook for the WP-164 receipt.</summary>
    public int GeneratedCount => _generatedCount;
    private int _generatedCount;

    /// <summary>
    /// The path preview should decode for <paramref name="mediaPath"/>: a ready proxy, or the
    /// original. Cheap — a policy test plus one <c>File.Exists</c> — so it is safe on the decode
    /// path. Queues a background transcode the first time a heavy file is asked for.
    /// </summary>
    public string SelectDecodePath(string mediaPath, MediaProbeResult probe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);
        ArgumentNullException.ThrowIfNull(probe);
        if (_disposed) return mediaPath;

        var previewMaxDimension = Volatile.Read(ref _previewMaxDimension);
        var generation = Volatile.Read(ref _generation);

        if (ProxyPolicy.IsStillImagePath(mediaPath)
            || !_cache.Policy.ShouldProxyForPreview(
                probe.Width, probe.Height, probe.VideoCodecName, previewMaxDimension))
        {
            Publish(new ProxyStatus(mediaPath, ProxyState.NotWarranted, null, 0, null), generation);
            return mediaPath;
        }

        // Dimensions are needed to size the proxy; without them there is nothing to target.
        if (probe.Width is not int sourceWidth || probe.Height is not int sourceHeight
            || sourceWidth <= 0 || sourceHeight <= 0)
        {
            Publish(new ProxyStatus(
                mediaPath, ProxyState.NotWarranted, null, 0, "source dimensions unknown"), generation);
            return mediaPath;
        }

        var targetHeight = ProxyPolicy.TargetHeightFor(sourceWidth, sourceHeight, previewMaxDimension);

        string cacheKey;
        try
        {
            // Stats the source, so it also carries this content's size and mtime.
            cacheKey = ProxyCache.KeyFor(mediaPath, targetHeight);
            if (_cache.TryGet(mediaPath, targetHeight, out var proxyPath))
            {
                Publish(new ProxyStatus(mediaPath, ProxyState.Ready, proxyPath, targetHeight, null), generation);
                return proxyPath;
            }
        }
        catch (FileNotFoundException)
        {
            // The source itself vanished between resolve and here; let the decoder report it.
            return mediaPath;
        }

        // A previous failure retires that exact content, not the path. Re-render the source and the
        // key changes, so the next demand tries again instead of being stuck on the original.
        lock (_gate)
        {
            if (_failedKeys.Contains(cacheKey))
                return mediaPath;
        }

        QueueGeneration(mediaPath, targetHeight, cacheKey, generation);
        return mediaPath;
    }

    /// <summary>Last known status for a media file, or a NotWarranted default.</summary>
    public ProxyStatus StatusFor(string mediaPath)
    {
        lock (_gate)
        {
            return _statuses.TryGetValue(mediaPath, out var status)
                ? status
                : new ProxyStatus(mediaPath, ProxyState.NotWarranted, null, 0, null);
        }
    }

    /// <summary>Every media file this selector has seen, newest state.</summary>
    public IReadOnlyList<ProxyStatus> Statuses
    {
        get
        {
            lock (_gate)
            {
                return [.. _statuses.Values];
            }
        }
    }

    /// <summary>
    /// A proxy handed out by <see cref="SelectDecodePath"/> could not be opened — it was evicted or
    /// deleted between the lookup and the ffmpeg spawn. Drops back to the original and lets the
    /// next lookup regenerate.
    /// </summary>
    public void ReportProxyUnusable(string mediaPath, string reason)
    {
        if (string.IsNullOrWhiteSpace(mediaPath)) return;
        var previous = StatusFor(mediaPath);
        if (previous.TargetHeight > 0)
            RetireKey(mediaPath, previous.TargetHeight);
        Publish(new ProxyStatus(
            mediaPath, ProxyState.Failed, null, previous.TargetHeight, reason));
    }

    /// <summary>Marks this content (not this path) as not worth another transcode attempt.</summary>
    private void RetireKey(string mediaPath, int targetHeight)
    {
        try
        {
            var key = ProxyCache.KeyFor(mediaPath, targetHeight);
            lock (_gate) { _failedKeys.Add(key); }
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException)
        {
            // The source is gone; there is no content to retire and nothing to remember.
        }
    }

    /// <summary>
    /// Cancels any queued or running transcode. The <see cref="CancellationTokenSource"/> and the
    /// transcode gate are deliberately <b>not</b> disposed: a generation already running on the
    /// thread pool still has to read the token and release the gate on its way out, and disposing
    /// either from here would fault it with an <see cref="ObjectDisposedException"/> nobody is
    /// awaiting — an unobserved exception at app shutdown, which is exactly the kind of unexplained
    /// process exit this repo has chased before. Both are small, and the selector lives as long as
    /// the preview surface does.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? genCts;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            genCts = _generationCts;
        }
        try { _shutdown.Cancel(); } catch (ObjectDisposedException) { }
        try { genCts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void QueueGeneration(string mediaPath, int targetHeight, string cacheKey, int generation)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || generation != Volatile.Read(ref _generation))
                return;
            if (_inFlight.TryGetValue(cacheKey, out var activeGeneration) && activeGeneration == generation)
                return;
            _inFlight[cacheKey] = generation;
            token = _generationCts.Token;
        }

        Publish(new ProxyStatus(mediaPath, ProxyState.Generating, null, targetHeight, null), generation);
        _ = Task.Run(() => GenerateAsync(mediaPath, targetHeight, cacheKey, generation, token));
    }

    private async Task GenerateAsync(
        string mediaPath, int targetHeight, string cacheKey, int generation, CancellationToken token)
    {
        try
        {
            await _transcodeGate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            ClearInFlight(cacheKey, generation);
            return;
        }

        try
        {
            token.ThrowIfCancellationRequested();
            var proxyPath = _transcoder is null
                ? await _cache.GetOrCreateAsync(mediaPath, targetHeight, token).ConfigureAwait(false)
                : await _cache.GetOrCreateAsync(
                        mediaPath,
                        targetHeight,
                        (target, ct) => _transcoder(mediaPath, target, targetHeight, ct),
                        token)
                    .ConfigureAwait(false);

            Interlocked.Increment(ref _generatedCount);
            Publish(new ProxyStatus(mediaPath, ProxyState.Ready, proxyPath, targetHeight, null), generation);
        }
        catch (OperationCanceledException)
        {
            // Shutting down or superseded by a timeline switch / resolution change: leave no status change behind.
        }
        catch (Exception ex)
        {
            // A failed transcode must not take preview with it, and must not be retried every
            // restart - the original still decodes, just slower. Retiring the KEY rather than the
            // path means re-rendering the source gets a fresh attempt.
            lock (_gate) { _failedKeys.Add(cacheKey); }
            Publish(new ProxyStatus(mediaPath, ProxyState.Failed, null, targetHeight, ex.Message), generation);
        }
        finally
        {
            ClearInFlight(cacheKey, generation);
            try { _transcodeGate.Release(); } catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }
    }

    private void ClearInFlight(string cacheKey, int generation)
    {
        lock (_gate)
        {
            if (_inFlight.TryGetValue(cacheKey, out var active) && active == generation)
                _inFlight.Remove(cacheKey);
        }
    }

    private void Publish(ProxyStatus status, int? expectedGeneration = null)
    {
        bool changed;
        lock (_gate)
        {
            if (expectedGeneration is int gen && gen != Volatile.Read(ref _generation))
                return;
            changed = !_statuses.TryGetValue(status.MediaPath, out var previous) || previous != status;
            _statuses[status.MediaPath] = status;
        }

        if (changed)
            StateChanged?.Invoke(status);
    }
}
