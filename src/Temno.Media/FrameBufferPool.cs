namespace Temno.Media;

/// <summary>
/// WP-248. Recycles full-frame pixel buffers for the preview path, handed out as
/// <see cref="VideoFrame"/>s whose buffer comes back here when the last handle on it is disposed
/// (<see cref="VideoFrame.Share"/>).
///
/// Why it exists: a 1280×720 BGRA frame is 3.7 MB, far over the 85 KB large-object threshold, and
/// playback allocated two per frame — one for the decode, one for the composite. Every
/// large-object allocation spends the gen-2 budget, so at 24 fps the runtime ran a background
/// gen-2 collection roughly every 170 ms, and its pauses (34–50 ms each) held every third frame on
/// screen for 80–95 ms. 43 of the 43 UI stalls traced in the running app coincided with one.
///
/// Bounded by bytes, not count: frames here are at most preview size, but a source switch changes
/// the size, and buffers of a size nobody asks for any more must not be kept forever. The oldest
/// retained buffer is dropped first; a dropped buffer is just garbage.
/// </summary>
public sealed class FrameBufferPool
{
    /// <summary>About seventeen 1280×720 frames: every pipe's last frame plus the ones in flight.</summary>
    public const long DefaultMaxRetainedBytes = 64L * 1024 * 1024;

    private long _maxRetainedBytes;
    private readonly List<byte[]> _free = [];
    private long _retainedBytes;
    private long _allocated;

    public FrameBufferPool(long maxRetainedBytes = DefaultMaxRetainedBytes)
    {
        if (maxRetainedBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxRetainedBytes));
        _maxRetainedBytes = maxRetainedBytes;
    }

    /// <summary>Maximum idle bytes retained for reuse. Lowering trims the oldest idle buffers immediately.</summary>
    public long MaxRetainedBytes
    {
        get { lock (_free) return _maxRetainedBytes; }
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_free)
            {
                _maxRetainedBytes = value;
                while (_retainedBytes > _maxRetainedBytes && _free.Count > 0)
                {
                    _retainedBytes -= _free[0].Length;
                    _free.RemoveAt(0);
                }
            }
        }
    }

    /// <summary>Drops all idle retained buffers; active handles stay valid until disposed.</summary>
    public void Clear()
    {
        lock (_free)
        {
            _free.Clear();
            _retainedBytes = 0;
        }
    }

    /// <summary>Buffers this pool has had to allocate because none of the right size was free.</summary>
    public long AllocatedCount => Interlocked.Read(ref _allocated);

    /// <summary>Buffers waiting to be rented again.</summary>
    public int RetainedCount
    {
        get { lock (_free) return _free.Count; }
    }

    /// <summary>
    /// A frame of <paramref name="width"/> × <paramref name="height"/> four-byte pixels over a
    /// recycled buffer. The contents are whatever the last user left: the caller overwrites all of it.
    /// </summary>
    public VideoFrame Rent(int width, int height, string pixelFormat)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        var length = checked(width * height * 4);
        return new VideoFrame(width, height, new Lease(this, TakeOrAllocate(length)), pixelFormat);
    }

    private byte[] TakeOrAllocate(int length)
    {
        lock (_free)
        {
            // Newest first: the buffer most likely still in cache.
            for (var i = _free.Count - 1; i >= 0; i--)
            {
                var buffer = _free[i];
                if (buffer.Length != length) continue;
                _free.RemoveAt(i);
                _retainedBytes -= length;
                return buffer;
            }
        }

        Interlocked.Increment(ref _allocated);
        return new byte[length];
    }

    private void Return(byte[] buffer)
    {
        lock (_free)
        {
            if (buffer.Length > _maxRetainedBytes) return;
            while (_retainedBytes + buffer.Length > _maxRetainedBytes && _free.Count > 0)
            {
                _retainedBytes -= _free[0].Length;
                _free.RemoveAt(0);
            }

            _free.Add(buffer);
            _retainedBytes += buffer.Length;
        }
    }

    /// <summary>
    /// The handles on one rented buffer. It goes back to the pool when the count reaches zero, and
    /// can never be counted up again from there: a buffer back in the pool may already be someone
    /// else's frame.
    /// </summary>
    internal sealed class Lease(FrameBufferPool pool, byte[] buffer)
    {
        private int _handles = 1;

        public byte[] Buffer { get; } = buffer;

        public bool TryAddHandle()
        {
            while (true)
            {
                var handles = Volatile.Read(ref _handles);
                if (handles == 0) return false;
                if (Interlocked.CompareExchange(ref _handles, handles + 1, handles) == handles) return true;
            }
        }

        public void ReleaseHandle()
        {
            var handles = Interlocked.Decrement(ref _handles);
            if (handles == 0)
                pool.Return(Buffer);
            else if (handles < 0)
                throw new InvalidOperationException("A pooled frame buffer was released more times than it was shared.");
        }
    }
}
