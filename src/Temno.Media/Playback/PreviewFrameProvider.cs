using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media.Compositing;
using Temno.Media.Effects;
using Temno.Media.Hardware;

namespace Temno.Media.Playback;

/// <summary>
/// Default <see cref="IPreviewFrameProvider"/>: resolves visible layers via
/// <see cref="TimelineFrameResolver"/> and decodes them through an <see cref="IFrameSource"/>.
/// Duplicate in-flight requests for the same (timeline id, frame) are coalesced onto one decode;
/// cancelling a caller abandons its wait but a shared decode runs to completion for other waiters.
/// <para>
/// WP-248: every caller owns the frames it is given, including a caller that joined a shared
/// decode — each gets its own handle (<see cref="VideoFrame.Share"/>), because composited and
/// decoded frames are pooled and one caller's dispose would otherwise recycle pixels another is
/// still reading. The decode's own handle is released once it and every caller have let go.
/// </para>
/// </summary>
public sealed class PreviewFrameProvider : IPreviewFrameProvider, IDisposable
{
    private readonly IFrameSource _frameSource;
    private readonly MediaResolver? _mediaResolver;
    private readonly TimelineFrameRenderer _timelineRenderer;
    private readonly object _gate = new();
    private readonly Dictionary<(string Kind, string TimelineId, int Frame, long Version), object> _inflight = [];
    private readonly FrameBufferPool _canvasPool = new();
    private CancellationTokenSource _versionCts = new();
    private int _maxDimension; // 0 means Full (uncapped canvas size)
    private long _renderVersion;
    private bool _disposed;

    public PreviewFrameProvider(IFrameSource frameSource, MediaResolver? mediaResolver = null)
        : this(frameSource, mediaResolver, timelineLookup: null)
    {
    }

    /// <param name="timelineLookup">WP-106: resolve nested sequence mediaRef → Timeline.</param>
    /// <param name="acceleration">WP-114: PreferHardware uses D3D11 color effects when available.</param>
    /// <param name="maxDimension">
    /// Optional longest-edge cap for composited preview frames, or <c>null</c> for full timeline canvas.
    /// Defaults to <see cref="PreviewRaster.PreviewMaxDimension"/> (1280 px).
    /// </param>
    public PreviewFrameProvider(
        IFrameSource frameSource,
        MediaResolver? mediaResolver,
        Func<string, Timeline?>? timelineLookup,
        HardwareAccelerationMode acceleration = HardwareAccelerationMode.PreferHardware,
        int? maxDimension = PreviewRaster.PreviewMaxDimension)
    {
        ArgumentNullException.ThrowIfNull(frameSource);
        if (maxDimension is <= 0) throw new ArgumentOutOfRangeException(nameof(maxDimension));
        _frameSource = frameSource;
        _mediaResolver = mediaResolver;
        _maxDimension = maxDimension ?? 0;
        _canvasPool.MaxRetainedBytes = CanvasPoolBudgetFor(maxDimension);
        var compositor = CompositorFactory.Create();
        if (compositor is GpuCompositor gpu)
            gpu.OutputPool = _canvasPool;
        _timelineRenderer = new TimelineFrameRenderer(
            frameSource,
            mediaResolver,
            compositor,
            EffectsRendererFactory.Create(acceleration),
            timelineLookup: timelineLookup);
    }

    /// <summary>
    /// Longest-edge cap for composited preview frames, or <c>null</c> for full timeline canvas resolution.
    /// </summary>
    public int? MaxDimension
    {
        get
        {
            var value = Volatile.Read(ref _maxDimension);
            return value <= 0 ? null : value;
        }
        set
        {
            if (value is <= 0) throw new ArgumentOutOfRangeException(nameof(value));
            var encoded = value ?? 0;
            if (Interlocked.Exchange(ref _maxDimension, encoded) == encoded) return;
            Invalidate();
            _canvasPool.MaxRetainedBytes = CanvasPoolBudgetFor(value);
            _canvasPool.Clear();
        }
    }

    /// <summary>
    /// Invalidates in-flight coalescing keys and cancels stale renders/decodes after an edit,
    /// preview resolution change, or timeline switch so outgoing work stops immediately while
    /// preserving the warm D3D11 compositor and pools.
    /// </summary>
    public void Invalidate()
    {
        CancellationTokenSource? previous = null;
        lock (_gate)
        {
            Interlocked.Increment(ref _renderVersion);
            if (!_disposed)
            {
                previous = _versionCts;
                _versionCts = new CancellationTokenSource();
            }
        }

        // Cancel without disposing: in-flight flights may still be observing this token.
        if (previous is not null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private static long CanvasPoolBudgetFor(int? maxDimension)
        => maxDimension is null or > 2560 ? 96L * 1024 * 1024 : FrameBufferPool.DefaultMaxRetainedBytes;

    public Task<VideoFrame?> GetTopMostFrameAsync(Timeline timeline, int frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return CoalesceAsync(
            "top", timeline.Id, frame, ct => DecodeTopMostAsync(timeline, frame, ct), ShareFrame, ReleaseFrame, cancellationToken);
    }

    public Task<VideoFrame?> GetCompositedFrameAsync(Timeline timeline, int frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        var (width, height) = PreviewSize(timeline);
        return CoalesceAsync(
            $"composited:{width}x{height}",
            timeline.Id,
            frame,
            ct => _timelineRenderer.RenderAsync(timeline, frame, width, height, ct),
            ShareFrame,
            ReleaseFrame,
            cancellationToken);
    }

    public Task<IReadOnlyList<PreviewLayerFrame>> GetLayerFramesAsync(Timeline timeline, int frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return CoalesceAsync(
            "layers", timeline.Id, frame, ct => DecodeLayersAsync(timeline, frame, ct), ShareLayers, ReleaseLayers, cancellationToken);
    }

    private async Task<VideoFrame?> DecodeTopMostAsync(Timeline timeline, int frame, CancellationToken cancellationToken)
    {
        var layers = TimelineFrameResolver.Resolve(timeline, frame, _mediaResolver);
        for (var i = layers.Count - 1; i >= 0; i--) // list is bottom→top; walk top→bottom
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layer = layers[i];
            if (!IsDecodable(layer)) continue;
            try
            {
                return await _frameSource.GetFrameAsync(layer.MediaPath!, layer.SourceSeconds, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // Offline media: fall through to the layer beneath, matching Mac offline-clip handling.
            }
        }

        return null;
    }

    private async Task<IReadOnlyList<PreviewLayerFrame>> DecodeLayersAsync(Timeline timeline, int frame, CancellationToken cancellationToken)
    {
        var layers = TimelineFrameResolver.Resolve(timeline, frame, _mediaResolver);
        var result = new List<PreviewLayerFrame>(layers.Count);
        try
        {
            foreach (var layer in layers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsDecodable(layer)) continue;
                try
                {
                    var decoded = await _frameSource.GetFrameAsync(layer.MediaPath!, layer.SourceSeconds, cancellationToken).ConfigureAwait(false);
                    result.Add(new PreviewLayerFrame(layer, decoded));
                }
                catch (FileNotFoundException)
                {
                    // Offline media: skip this layer.
                }
            }
        }
        catch
        {
            ReleaseLayers(result);
            throw;
        }

        return result;
    }

    // Text is filtered by the resolver; sequence (nested timeline) and lottie need bake/flatten
    // pipelines (WP-22/WP-25) and cannot be decoded directly from a file path.
    private static bool IsDecodable(in ResolvedFrameClip layer)
        => layer.MediaPath is not null && layer.MediaType is ClipType.video or ClipType.image;

    internal (int Width, int Height) PreviewSize(Timeline timeline)
    {
        var width = Math.Max(1, timeline.Width);
        var height = Math.Max(1, timeline.Height);
        // WP-163 + user-adjustable frame preview: composite at the configured preview cap, or
        // full canvas when MaxDimension is null. Export renders through TimelineFrameRenderer directly.
        var max = MaxDimension;
        if (max is null || (width <= max.Value && height <= max.Value))
            return (width, height);

        return PreviewRaster.Fit(width, height, max.Value);
    }

    private static VideoFrame? ShareFrame(VideoFrame? frame) => frame?.Share();

    private static void ReleaseFrame(VideoFrame? frame) => frame?.Dispose();

    private static IReadOnlyList<PreviewLayerFrame> ShareLayers(IReadOnlyList<PreviewLayerFrame> layers)
    {
        if (!layers.Any(l => l.Frame.IsPooled)) return layers;
        return layers.Select(l => l with { Frame = l.Frame.Share() }).ToList();
    }

    private static void ReleaseLayers(IReadOnlyList<PreviewLayerFrame> layers)
    {
        foreach (var layer in layers)
            layer.Frame.Dispose();
    }

    /// <summary>
    /// One decode and everyone holding on to it: the decode itself until it finishes, and each
    /// caller until it has taken its own share of the result or given up waiting.
    /// </summary>
    private sealed class Flight<T>
    {
        public Task<T> Task = null!;
        public T? Result;
        public int Holders = 2;   // the decode, and the caller that started it
    }

    private async Task<T> CoalesceAsync<T>(
        string kind, string timelineId, int frame, Func<CancellationToken, Task<T>> factory,
        Func<T, T> share, Action<T> release, CancellationToken cancellationToken)
    {
        Flight<T> flight;
        lock (_gate)
        {
            var key = (kind, timelineId, frame, Volatile.Read(ref _renderVersion));
            if (_inflight.TryGetValue(key, out var existing))
            {
                flight = (Flight<T>)existing;
                // Still in the table, so the decode has not let go: the count cannot be zero here.
                Interlocked.Increment(ref flight.Holders);
            }
            else
            {
                var versionToken = _versionCts.Token;
                flight = new Flight<T>();
                flight.Task = RunAsync(flight, () => factory(versionToken), key, release);
                // Observe faults on the shared decode task so cancelling all waiting callers before a
                // transient decode exception finishes never surfaces TaskScheduler.UnobservedTaskException;
                // active waiters still receive the original exception via WaitAsync below.
                _ = flight.Task.ContinueWith(
                    static t => _ = t.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                // A synchronously completed task has already run its cleanup; caching it would pin a
                // stale result under this key forever.
                if (!flight.Task.IsCompleted) _inflight[key] = flight;
            }
        }

        try
        {
            var result = await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return share(result);
        }
        finally
        {
            Leave(flight, release);
        }
    }

    private async Task<T> RunAsync<T>(
        Flight<T> flight, Func<Task<T>> factory, (string Kind, string TimelineId, int Frame, long Version) key, Action<T> release)
    {
        try
        {
            var result = await factory().ConfigureAwait(false);
            flight.Result = result;
            return result;
        }
        finally
        {
            // Out of the table first, so no caller can join a decode that has already let go.
            lock (_gate)
            {
                _inflight.Remove(key);
            }

            Leave(flight, release);
        }
    }

    private static void Leave<T>(Flight<T> flight, Action<T> release)
    {
        if (Interlocked.Decrement(ref flight.Holders) == 0 && flight.Result is { } result)
            release(result);
    }

    /// <summary>
    /// Releases the timeline renderer, and with it the compositor and effects renderer that can
    /// each own an <c>ID3D11Device</c>. The provider is rebuilt on every rebind, so without this
    /// a project switch or an edit refresh leaked a device per call.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Increment(ref _renderVersion);
            cts = _versionCts;
        }
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        _timelineRenderer.Dispose();
    }
}
