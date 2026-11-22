using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Hardware;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-248: preview frame buffers are reused, and never while anyone still holds them.</summary>
public sealed class FrameBufferPoolTests
{
    [Fact]
    public void ADisposedFrame_GivesItsBufferToTheNextRent()
    {
        var pool = new FrameBufferPool();

        var first = pool.Rent(4, 2, "bgra");
        var buffer = first.Data;
        first.Dispose();
        using var second = pool.Rent(4, 2, "rgba");

        Assert.Same(buffer, second.Data);
        Assert.Equal(1, pool.AllocatedCount);
        Assert.Equal("rgba", second.PixelFormat);
        Assert.Equal(4 * 2 * 4, second.Data.Length);
    }

    [Fact]
    public void ASharedBuffer_IsNotReused_UntilEveryHandleIsDisposed()
    {
        var pool = new FrameBufferPool();
        var original = pool.Rent(4, 2, "bgra");
        var shared = original.Share();
        Assert.NotSame(original, shared);
        Assert.Same(original.Data, shared.Data);

        original.Dispose();
        using (var whileShared = pool.Rent(4, 2, "bgra"))
            Assert.NotSame(shared.Data, whileShared.Data);

        var buffer = shared.Data;
        shared.Dispose();
        // The buffer rented while the share was alive went back too; either may come out first.
        using var a = pool.Rent(4, 2, "bgra");
        using var b = pool.Rent(4, 2, "bgra");
        Assert.True(ReferenceEquals(a.Data, buffer) || ReferenceEquals(b.Data, buffer));
        Assert.Equal(2, pool.AllocatedCount);
    }

    [Fact]
    public void DisposingOneHandleTwice_DoesNotReleaseAnotherHoldersHandle()
    {
        var pool = new FrameBufferPool();
        var original = pool.Rent(4, 2, "bgra");
        using var shared = original.Share();

        original.Dispose();
        original.Dispose();

        using var next = pool.Rent(4, 2, "bgra");
        Assert.NotSame(shared.Data, next.Data);
    }

    [Fact]
    public void ADisposedHandle_CannotBeShared()
    {
        var pool = new FrameBufferPool();
        var frame = pool.Rent(4, 2, "bgra");
        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => frame.Share());
    }

    [Fact]
    public void AnOrdinaryFrame_SharesAsItself_AndDisposingItChangesNothing()
    {
        var frame = new VideoFrame(1, 1, new byte[4], "rgba");

        Assert.False(frame.IsPooled);
        Assert.Same(frame, frame.Share());
        frame.Dispose();
        Assert.Same(frame, frame.Share());
    }

    [Fact]
    public void RetainedBuffers_AreCappedByBytes_OldestDroppedFirst()
    {
        var frameBytes = 4 * 2 * 4;
        var pool = new FrameBufferPool(maxRetainedBytes: 2 * frameBytes);
        var frames = Enumerable.Range(0, 3).Select(_ => pool.Rent(4, 2, "bgra")).ToList();
        var oldest = frames[0].Data;

        foreach (var frame in frames)
            frame.Dispose();

        Assert.Equal(2, pool.RetainedCount);
        using var again = pool.Rent(4, 2, "bgra");
        using var andAgain = pool.Rent(4, 2, "bgra");
        Assert.NotSame(oldest, again.Data);
        Assert.NotSame(oldest, andAgain.Data);
    }

    [Fact]
    public void BuffersOfADifferentSize_AreNotHandedOut()
    {
        var pool = new FrameBufferPool();
        pool.Rent(4, 2, "bgra").Dispose();

        using var larger = pool.Rent(8, 2, "bgra");

        Assert.Equal(8 * 2 * 4, larger.Data.Length);
        Assert.Equal(2, pool.AllocatedCount);
    }

    [Fact]
    public void GpuComposite_IntoAPooledBuffer_IsByteIdentical_AndReusesItOnceDisposed()
    {
        using var plain = new GpuCompositor(new CpuCompositor(), HardwareCapabilities.Probe());
        using var pooled = new GpuCompositor(new CpuCompositor(), HardwareCapabilities.Probe())
        {
            OutputPool = new FrameBufferPool(),
        };
        if (!plain.IsGpuReady || !pooled.IsGpuReady) return;   // no D3D11 here; the parity suite says so

        var pixels = new byte[64 * 36 * 4];
        new Random(7).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        using var source = new VideoFrame(64, 36, pixels, "rgba");
        var layers = new[] { new CompositorLayer(source) };

        using var expected = plain.Render(layers, 48, 27);
        var first = pooled.Render(layers, 48, 27);
        Assert.True(first.IsPooled);
        Assert.False(expected.IsPooled);
        Assert.Equal(expected.Data, first.Data);

        var buffer = first.Data;
        first.Dispose();
        using var second = pooled.Render(layers, 48, 27);
        Assert.Same(buffer, second.Data);
        Assert.Equal(expected.Data, second.Data);
        Assert.Equal(1, pooled.OutputPool!.AllocatedCount);
    }
}
