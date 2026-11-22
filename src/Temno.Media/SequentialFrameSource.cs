using Temno.Media.Cache;

namespace Temno.Media;

/// <summary>
/// Preview <see cref="IFrameSource"/>: sequential ffmpeg pipes while requests walk forward in time,
/// process-per-frame seek only on reverse / large jumps (scrub).
///
/// One pipe is kept per media path (bounded by <see cref="MaxLivePipes"/>, least-recently-used
/// evicted). A composited timeline frame asks for one path per visible layer, so a single-pipe
/// source restarted ffmpeg — probe, spawn, kill — once per layer on every clock tick.
///
/// Returned frames are read-only: a pipe keeps the last decoded frame to answer duplicate
/// timestamps and end-of-stream. Callers must not write into <see cref="VideoFrame.Data"/>.
///
/// WP-248: decoded frames are pooled (<see cref="FrameBufferPool"/>). Every frame returned is the
/// caller's own handle — the pipe keeps a separate one as its last frame — so the caller disposes
/// it when done, and the buffer is reused once both have let go.
///
/// WP-163: pipes are per-path concurrent. A composited frame decodes one path per visible layer,
/// so the old single global gate serialized the layers back-to-back (~17 ms each on the owner
/// project). Each pipe now has its own gate; only same-path requests serialize. The pipe table
/// itself is still a short lock, and eviction never takes a pipe that is in use.
///
/// WP-248: a pipe is chosen by position, not only by path, so one file can have more than one
/// pipe — the one playing now and one <see cref="PrerollAsync"/> started at the next cut. A cold
/// start (probe, spawn, D3D11 init, seek) at every clip boundary held the picture for 377–685 ms
/// in the running app while audio played on.
/// </summary>
public sealed class SequentialFrameSource : IFrameSource, IDisposable
{
    public const int CatchUpRestartFrames = 45;

    /// <summary>Live ffmpeg pipes retained across calls. Covers a multi-layer composite plus scrub.</summary>
    public const int MaxLivePipes = 3;

    /// <summary>
    /// WP-248. A pipe read within this window belongs to a layer that is playing now. A preroll may
    /// only evict a pipe colder than this, or it would trade the next cut's stall for one mid-clip.
    /// </summary>
    internal const long HotPipeMilliseconds = 500;

    /// <summary>
    /// WP-248. How long a prerolled pipe that nobody has read yet is protected from another
    /// preroll. Past it the preroll was for a cut that never came (pause, seek, edit).
    /// </summary>
    internal const long PrerollReserveMilliseconds = 5000;

    /// <summary>
    /// WP-248. A pipe this close to a preroll's position already covers it. Further than this and
    /// the cut would read forward through the gap on demand, which is the stall a preroll is for.
    /// </summary>
    internal const int PrerollToleranceFrames = 2;

    private readonly FfmpegFrameSource _seek;
    private readonly IMediaProbe _probe;
    private readonly ProxySourceSelector? _proxies;
    private int _maxDimension;
    private readonly bool _preferHardware;
    private readonly List<Pipe> _pipes = [];
    private readonly FrameBufferPool _framePool = new();
    private readonly Dictionary<string, MediaProbeResult> _probeCache = new(StringComparer.OrdinalIgnoreCase);
    private long _useCounter;
    private int _outputFps = 30;
    private int _restartCount;
    private int _prerollCount;
    private bool _disposed;

    /// <param name="proxies">
    /// WP-164. When supplied, a heavy source decodes through its cached scrub proxy instead of the
    /// original. Null keeps the pre-WP-164 behaviour exactly. Only the preview frame source takes
    /// one: <c>ExportFrameSource</c> has no such parameter, which is what keeps proxies out of
    /// export.
    /// </param>
    public SequentialFrameSource(
        FfmpegFrameSource? seek = null,
        IMediaProbe? probe = null,
        int maxDimension = PreviewRaster.DefaultMaxDimension,
        ProxySourceSelector? proxies = null,
        bool preferHardware = false)
    {
        _seek = seek ?? new FfmpegFrameSource(probe);
        _probe = probe ?? new FfmpegMediaProbe();
        if (maxDimension <= 0) throw new ArgumentOutOfRangeException(nameof(maxDimension));
        _maxDimension = maxDimension;
        _framePool.MaxRetainedBytes = PoolBudgetFor(maxDimension);
        _proxies = proxies;
        _preferHardware = preferHardware;
    }

    /// <summary>The proxy selector this source decodes through, or null when proxies are off.</summary>
    public ProxySourceSelector? Proxies => _proxies;

    /// <summary>
    /// Longest-edge limit for preview decode. Updating it resets live pipes and clears idle
    /// frame buffers so subsequent frames decode immediately at the new resolution.
    /// </summary>
    public int MaxDimension
    {
        get => Volatile.Read(ref _maxDimension);
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (Interlocked.Exchange(ref _maxDimension, value) == value) return;
            _framePool.MaxRetainedBytes = PoolBudgetFor(value);
            Reset();
            _framePool.Clear();
        }
    }

    private static long PoolBudgetFor(int maxDimension)
        => maxDimension > 1920 ? 192L * 1024 * 1024 : FrameBufferPool.DefaultMaxRetainedBytes;

    public int OutputFps
    {
        get => _outputFps;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            if (_outputFps == value) return;
            _outputFps = value;
            Reset();
        }
    }

    /// <summary>Live decoder pipes right now. Diagnostic hook for the restart-thrash regression.</summary>
    public int LivePipeCount
    {
        get
        {
            lock (_pipes)
            {
                var count = 0;
                foreach (var pipe in _pipes)
                {
                    if (pipe.Decoder is not null) count++;
                }

                return count;
            }
        }
    }

    /// <summary>WP-248: the pool decoded frames are read into. Diagnostic hook for the allocation tests.</summary>
    public FrameBufferPool FramePool => _framePool;

    /// <summary>WP-248: prerolls that started a decoder, since construction or the last <see cref="Reset"/>.</summary>
    public int PrerollCount => _prerollCount;

    /// <summary>
    /// Demand decoder restarts (probe + ffmpeg spawn) since construction or the last
    /// <see cref="Reset"/>. Prerolls are counted in <see cref="PrerollCount"/> instead.
    /// </summary>
    public int RestartCount => _restartCount;

    /// <summary>
    /// WP-163. Invoked each time a pipe restarts (probe + ffmpeg spawn), on the caller's thread
    /// while that path's pipe gate is held — the handler must not block or call back into this instance.
    /// Diagnostic hook for the restart-thrash measurement; null by default.
    /// </summary>
    public Action? DecoderRestarted;

    /// <summary>WP-248. Same contract as <see cref="DecoderRestarted"/>, for a start made by <see cref="PrerollAsync"/>.</summary>
    public Action? DecoderPrerolled;

    public async Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var dt = 1.0 / _outputFps;
        var t = Math.Max(0, sourceSeconds);
        var pipe = RentPipe(path, t, dt);
        try
        {
            // A pipe still being prerolled holds its gate, so a request that arrives at the cut
            // waits for that start instead of spawning a second ffmpeg for the same position.
            await pipe.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (TryReuseLast(pipe, t, dt) is { } reused)
                    return reused;

                return await ReadForwardAsync(pipe, path, t, dt, allowRestartRetry: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                pipe.Gate.Release();
            }
        }
        finally
        {
            ReleasePipe(pipe);
        }
    }

    /// <summary>
    /// WP-248. Starts a decoder positioned at <paramref name="sourceSeconds"/> in the background, so
    /// the <see cref="GetFrameAsync"/> that reaches that position finds a pipe with its first frame
    /// already decoded. Advisory: it does nothing when a pipe can already serve the position, when
    /// room could only be made by evicting a pipe that is playing, or after disposal; and a start
    /// that fails is dropped here and left to the demand path, which reports it.
    /// </summary>
    public async Task PrerollAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path)) return;

        var dt = 1.0 / _outputFps;
        var t = Math.Max(0, sourceSeconds);
        Pipe pipe;
        lock (_pipes)
        {
            // Stricter than a demand read: a pipe that could reach t only by discarding up to
            // CatchUpRestartFrames would still stall the cut for that many decodes.
            foreach (var existing in _pipes)
            {
                if (IsPath(existing, path) && CanServe(existing, t, dt, out var lead)
                    && lead <= PrerollToleranceFrames * dt)
                    return;
            }

            while (_pipes.Count >= MaxLivePipes)
            {
                if (ColdestEvictablePipe() is not { } victim)
                    return;
                _pipes.Remove(victim);
                Retire(victim);
            }

            pipe = new Pipe(path) { PendingSeconds = t, UseCount = 1, LastUsed = ++_useCounter };
            _pipes.Add(pipe);
        }

        try
        {
            await pipe.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!pipe.Retired)
                {
                    await RestartAsync(pipe, path, t, cancellationToken, preroll: true).ConfigureAwait(false);
                    pipe.PrerolledAtTicks = Environment.TickCount64;
                }
            }
            catch (Exception)
            {
                pipe.Dispose();
            }
            finally
            {
                pipe.PendingSeconds = null;
                pipe.Gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled while waiting for the gate; nothing was started.
        }
        finally
        {
            ReleasePipe(pipe);
        }
    }

    public void Reset()
    {
        lock (_pipes)
        {
            foreach (var pipe in _pipes)
                Retire(pipe);
            _pipes.Clear();
            _useCounter = 0;
            _probeCache.Clear();
        }

        Interlocked.Exchange(ref _restartCount, 0);
        Interlocked.Exchange(ref _prerollCount, 0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
    }

    /// <summary>
    /// Caller does NOT hold the pipe-table lock. Takes a use on the pipe for this path that is
    /// positioned to serve <paramref name="t"/> — the nearest one when several are — so eviction
    /// cannot take it mid-decode; pair every return with <see cref="ReleasePipe"/>. With no such
    /// pipe, the least recently used idle pipe for the path is taken (it will restart), and only
    /// when every pipe for the path is busy is a new one added.
    /// </summary>
    private Pipe RentPipe(string path, double t, double dt)
    {
        lock (_pipes)
        {
            Pipe? best = null;
            var bestLead = double.MaxValue;
            Pipe? idle = null;
            foreach (var candidate in _pipes)
            {
                if (!IsPath(candidate, path)) continue;
                if (CanServe(candidate, t, dt, out var lead) && lead < bestLead)
                {
                    best = candidate;
                    bestLead = lead;
                }

                if (candidate.UseCount == 0 && (idle is null || candidate.LastUsed < idle.LastUsed))
                    idle = candidate;
            }

            var pipe = best ?? idle;
            if (pipe is null)
            {
                pipe = new Pipe(path);
                _pipes.Add(pipe);
            }

            pipe.UseCount++;
            pipe.LastUsed = ++_useCounter;
            pipe.LastUsedTicks = Environment.TickCount64;
            pipe.PrerolledAtTicks = null;
            EvictLeastRecentlyUsed();
            return pipe;
        }
    }

    private void ReleasePipe(Pipe pipe)
    {
        lock (_pipes)
        {
            pipe.UseCount--;
            // Retired while in use (Reset, eviction race): whatever a restart put back in it is
            // owned by nobody now, and would otherwise outlive the table as an orphan ffmpeg.
            if (pipe.UseCount == 0 && pipe.Retired)
                pipe.Dispose();
        }
    }

    /// <summary>Caller holds the pipe-table lock.</summary>
    private void EvictLeastRecentlyUsed()
    {
        while (_pipes.Count > MaxLivePipes)
        {
            Pipe? oldest = null;
            foreach (var candidate in _pipes)
            {
                // In-use pipes are never taken: a concurrent decode owns their decoder fields
                // under the pipe gate. The table can transiently exceed the budget instead —
                // bounded by concurrent callers, drained on release.
                if (candidate.UseCount > 0) continue;
                if (oldest is not null && candidate.LastUsed >= oldest.LastUsed) continue;
                oldest = candidate;
            }

            if (oldest is null) return;
            _pipes.Remove(oldest);
            Retire(oldest);
        }
    }

    /// <summary>
    /// Caller holds the pipe-table lock. A pipe in use (only <see cref="Reset"/> retires one) has
    /// its ffmpeg stopped now, as before, but keeps its last frame: the request holding the pipe
    /// may be sharing it this moment. <see cref="ReleasePipe"/> disposes the rest when it leaves.
    /// </summary>
    private static void Retire(Pipe pipe)
    {
        pipe.Retired = true;
        if (pipe.UseCount == 0)
            pipe.Dispose();
        else
            pipe.StopDecoder();
    }

    /// <summary>
    /// Caller holds the pipe-table lock. The least recently used pipe a preroll may displace: idle,
    /// not read within <see cref="HotPipeMilliseconds"/> (a layer playing now), and not itself a
    /// preroll still waiting for its cut. Null when there is none, and the preroll is skipped.
    /// </summary>
    private Pipe? ColdestEvictablePipe()
    {
        var now = Environment.TickCount64;
        Pipe? coldest = null;
        foreach (var pipe in _pipes)
        {
            if (pipe.UseCount > 0) continue;
            if (now - pipe.LastUsedTicks <= HotPipeMilliseconds) continue;
            if (pipe.PrerolledAtTicks is { } prerolled && now - prerolled <= PrerollReserveMilliseconds) continue;
            if (coldest is not null && pipe.LastUsed >= coldest.LastUsed) continue;
            coldest = pipe;
        }

        return coldest;
    }

    private static bool IsPath(Pipe pipe, string path)
        => string.Equals(pipe.Path, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Caller holds the pipe-table lock. True when <paramref name="pipe"/> would answer
    /// <paramref name="t"/> without a restart: by reusing its last frame, or by reading forward at
    /// most <see cref="CatchUpRestartFrames"/>. A pipe still being prerolled is judged by the
    /// position it is starting at. <paramref name="lead"/> is how far it would read forward.
    /// Read under the table lock while a reader may be advancing the pipe, so this is a choice,
    /// not a guarantee: <see cref="ReadForwardAsync"/> re-checks and restarts if it was wrong.
    /// </summary>
    private static bool CanServe(Pipe pipe, double t, double dt, out double lead)
    {
        lead = double.MaxValue;
        if (pipe.Retired) return false;

        var next = pipe.PendingSeconds ?? pipe.NextSeconds;
        if (pipe.PendingSeconds is null)
        {
            if (pipe.Decoder is null) return false;
            if (pipe.LastFrame is not null && t + dt * 0.5 < next && t + dt * 1.5 >= next)
            {
                lead = 0;
                return true;
            }
        }

        if (t + dt * 0.75 < next) return false;
        var ahead = t - next;
        if (ahead > CatchUpRestartFrames * dt) return false;
        lead = Math.Max(0, ahead);
        return true;
    }

    private async Task<VideoFrame> ReadForwardAsync(
        Pipe pipe,
        string path,
        double t,
        double dt,
        bool allowRestartRetry,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!CanContinue(pipe, t, dt))
                await RestartAsync(pipe, path, t, cancellationToken).ConfigureAwait(false);

            var skipped = 0;
            while (pipe.NextSeconds + dt * 0.5 < t)
            {
                if (skipped >= CatchUpRestartFrames)
                {
                    await RestartAsync(pipe, path, t, cancellationToken).ConfigureAwait(false);
                    break;
                }

                if (pipe.Decoder is null)
                    await RestartAsync(pipe, path, t, cancellationToken).ConfigureAwait(false);

                await pipe.Decoder!.DiscardFrameAsync(cancellationToken).ConfigureAwait(false);
                pipe.NextSeconds += dt;
                skipped++;
            }

            var frame = await pipe.Decoder!.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            pipe.LastFrame?.Dispose();
            pipe.LastFrame = frame;
            pipe.NextSeconds += dt;
            return frame.Share();
        }
        catch (EndOfStreamException)
        {
            if (pipe.LastFrame is { } last)
                return last.Share();

            pipe.Dispose();
            return await _seek.GetPreviewFrameAsync(path, t, Volatile.Read(ref _maxDimension), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            pipe.Dispose();
            throw;
        }
        catch (Exception ex) when (allowRestartRetry)
        {
            // WP-164: when this pipe was reading a proxy, the proxy is what just failed - evicted,
            // deleted, or unreadable. Retire it before retrying, or the retry opens the same broken
            // file and the second failure escapes as an offline-media error.
            if (pipe.ProxyPathInUse is not null)
                _proxies?.ReportProxyUnusable(path, ex.Message);
            pipe.Dispose();
            await RestartAsync(pipe, path, t, cancellationToken).ConfigureAwait(false);
            return await ReadForwardAsync(pipe, path, t, dt, allowRestartRetry: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static VideoFrame? TryReuseLast(Pipe pipe, double t, double dt)
    {
        if (pipe.LastFrame is not { } last)
            return null;
        // Same clock tick / inspector invalidate / 1-frame-behind float: do not kill the pipe.
        if (t + dt * 0.5 < pipe.NextSeconds && t + dt * 1.5 >= pipe.NextSeconds)
            return last.Share();
        return null;
    }

    private static bool CanContinue(Pipe pipe, double t, double dt)
    {
        if (pipe.Decoder is null)
            return false;
        if (t + dt * 0.75 < pipe.NextSeconds)
            return false;
        return true;
    }

    /// <param name="preroll">
    /// WP-248: counted and reported apart from demand restarts. A demand restart is a stall the
    /// viewer sees; a preroll is the same work done before anyone is waiting for it.
    /// </param>
    private async Task RestartAsync(
        Pipe pipe, string path, double t, CancellationToken cancellationToken, bool preroll = false)
    {
        pipe.Dispose();
        var info = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        if (!info.HasVideo || info.Width is not int sw || info.Height is not int sh)
            throw new InvalidOperationException("The media file does not contain a valid video stream.");

        // WP-405: the raster is fitted to the shown size; ffmpeg emits the decoded (turned) size, so that
        // is what the scale filter is compared against.
        var (dw, dh) = info.DecodedSize ?? (sw, sh);
        var (decodePath, proxyInfo) = await SelectDecodeSourceAsync(path, info, cancellationToken)
            .ConfigureAwait(false);
        if (proxyInfo is { Width: int pw, Height: int ph })
        {
            (sw, sh) = (pw, ph);
            (dw, dh) = proxyInfo.DecodedSize ?? (pw, ph);
        }

        var maxDimension = Volatile.Read(ref _maxDimension);
        var (w, h) = PreviewRaster.Fit(sw, sh, maxDimension);
        // WP-216: named-pipe 1 MiB transport cuts 4K frame read overhead from ~85 ms to ~16 ms
        // on Windows while falling back cleanly to stdout if ffmpeg cannot open the pipe.
        var useSizedTransport = OperatingSystem.IsWindows() && (long)w * h >= 1920L * 1080L;
        pipe.Decoder = await FfmpegPlaybackDecoder.StartAsync(
                decodePath, t, _outputFps, w, h, dw, dh, cancellationToken, preferHardware: _preferHardware,
                sizedTransport: useSizedTransport,
                framePool: _framePool)
            .ConfigureAwait(false);
        pipe.ProxyPathInUse = proxyInfo is null ? null : decodePath;
        pipe.NextSeconds = t;
        if (preroll)
        {
            Interlocked.Increment(ref _prerollCount);
            DecoderPrerolled?.Invoke();
            return;
        }

        Interlocked.Increment(ref _restartCount);
        DecoderRestarted?.Invoke();
    }

    /// <summary>
    /// WP-164. Resolves the file this restart should actually open. A proxy is adopted only once
    /// its own dimensions are known: they replace the source dimensions for the raster fit, which
    /// is what lets <see cref="FfmpegVideoFilter.Convert"/> drop the scale filter entirely - the
    /// proxy is already the size preview wants. A proxy that cannot be probed is retired.
    /// </summary>
    private async Task<(string DecodePath, MediaProbeResult? ProxyInfo)> SelectDecodeSourceAsync(
        string path, MediaProbeResult info, CancellationToken cancellationToken)
    {
        if (_proxies is null)
            return (path, null);

        var selected = _proxies.SelectDecodePath(path, info);
        if (string.Equals(selected, path, StringComparison.OrdinalIgnoreCase))
            return (path, null);

        try
        {
            var proxyInfo = await ProbeAsync(selected, cancellationToken).ConfigureAwait(false);
            if (proxyInfo.HasVideo && proxyInfo.Width is int pw && proxyInfo.Height is int ph
                && pw > 0 && ph > 0)
                return (selected, proxyInfo);

            _proxies.ReportProxyUnusable(path, "the proxy has no readable video stream");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _proxies.ReportProxyUnusable(path, ex.Message);
        }

        return (path, null);
    }

    private async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        // Concurrent pipes probe different paths at once; the cache is shared.
        lock (_pipes)
        {
            if (_probeCache.TryGetValue(path, out var cached))
                return cached;
        }
        var info = await _probe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        lock (_pipes)
        {
            _probeCache[path] = info;
        }
        return info;
    }

    /// <summary>One media path live ffmpeg pipe plus its forward-read position.</summary>
    private sealed class Pipe(string path) : IDisposable
    {
        public readonly string Path = path;
        /// <summary>Serializes requests on this pipe; other pipes decode concurrently (WP-163).</summary>
        public readonly SemaphoreSlim Gate = new(1, 1);
        /// <summary>Rents outstanding. Eviction only takes pipes at zero.</summary>
        public int UseCount;
        public FfmpegPlaybackDecoder? Decoder;
        public VideoFrame? LastFrame;
        public double NextSeconds;
        public long LastUsed;
        /// <summary>WP-248: <see cref="Environment.TickCount64"/> of the last demand read.</summary>
        public long LastUsedTicks = Environment.TickCount64;
        /// <summary>WP-248: the position a preroll is starting this pipe at; null once started.</summary>
        public double? PendingSeconds;
        /// <summary>WP-248: when a preroll finished starting this pipe; null once a demand read it.</summary>
        public long? PrerolledAtTicks;
        /// <summary>WP-248: removed from the table; disposed by its last user.</summary>
        public bool Retired;

        /// <summary>WP-164: the proxy this pipe is reading, or null when it is on the original.</summary>
        public string? ProxyPathInUse;

        public void StopDecoder()
        {
            Decoder?.Dispose();
            Decoder = null;
        }

        /// <summary>Caller holds this pipe's gate, or the pipe has no users.</summary>
        public void Dispose()
        {
            StopDecoder();
            LastFrame?.Dispose();
            LastFrame = null;
            NextSeconds = 0;
            ProxyPathInUse = null;
        }
    }
}
