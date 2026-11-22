using System;
using System.Threading;

namespace Temno.Media;

/// <summary>
/// Represents a decoded video frame.
/// <para>
/// WP-248: a frame may be one handle on a pooled buffer (<see cref="FrameBufferPool"/>). Such a
/// frame must be disposed exactly by whoever holds it, and <see cref="Data"/> must not be read
/// after that — the buffer may by then be someone else's frame. Anyone who hands the same pixels
/// to a second holder gives that holder its own handle with <see cref="Share"/>. A frame that is
/// not pooled behaves exactly as before: disposing it does nothing and sharing it returns itself.
/// </para>
/// </summary>
public sealed class VideoFrame : IDisposable
{
    private readonly FrameBufferPool.Lease? _lease;
    private int _disposed;

    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }
    public string PixelFormat { get; }

    public VideoFrame(int width, int height, byte[] data, string pixelFormat)
    {
        Width = width;
        Height = height;
        Data = data ?? throw new ArgumentNullException(nameof(data));
        PixelFormat = pixelFormat ?? throw new ArgumentNullException(nameof(pixelFormat));
    }

    internal VideoFrame(int width, int height, FrameBufferPool.Lease lease, string pixelFormat)
        : this(width, height, lease.Buffer, pixelFormat)
    {
        _lease = lease;
    }

    /// <summary>True when <see cref="Data"/> goes back to a pool once every handle is disposed.</summary>
    public bool IsPooled => _lease is not null;

    /// <summary>
    /// A second handle on the same pixels, for a second holder to dispose on its own schedule. The
    /// buffer is returned to its pool only when every handle has been disposed. Returns this frame
    /// itself when it is not pooled.
    /// </summary>
    /// <exception cref="ObjectDisposedException">This handle has already been disposed.</exception>
    public VideoFrame Share()
    {
        if (_lease is null) return this;
        if (Volatile.Read(ref _disposed) != 0 || !_lease.TryAddHandle())
            throw new ObjectDisposedException(nameof(VideoFrame), "A disposed pooled frame cannot be shared.");
        return new VideoFrame(Width, Height, _lease, PixelFormat);
    }

    public void Dispose()
    {
        // Idempotent per handle: a second Dispose must not release a handle someone else holds.
        if (_lease is not null && Interlocked.Exchange(ref _disposed, 1) == 0)
            _lease.ReleaseHandle();
    }
}
