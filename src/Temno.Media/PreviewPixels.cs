using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Temno.Media;

/// <summary>Copies the compositor's encoded SDR pixels without a viewer-only grade.</summary>
public static class PreviewPixels
{
    private static readonly Vector256<byte> RgbaToBgraMask256 = Vector256.Create(
        (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15,
        2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);

    private static readonly Vector128<byte> RgbaToBgraMask128 = Vector128.Create(
        (byte)2, 1, 0, 3, 6, 5, 4, 7, 10, 9, 8, 11, 14, 13, 12, 15);

    public static void CopyToBgra(VideoFrame frame, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var length = checked(frame.Width * frame.Height * 4);
        if (frame.Data.Length < length || destination.Length < length)
            throw new ArgumentException("A complete four-channel frame is required.");
        var source = frame.Data.AsSpan(0, length);
        if (string.Equals(frame.PixelFormat, "bgra", StringComparison.OrdinalIgnoreCase))
        {
            source.CopyTo(destination);
            return;
        }
        if (!string.Equals(frame.PixelFormat, "rgba", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"Unsupported preview pixel format: {frame.PixelFormat}");

        var i = 0;
        ref var srcRef = ref MemoryMarshal.GetReference(source);
        ref var dstRef = ref MemoryMarshal.GetReference(destination);

        if (Avx2.IsSupported && length >= 32)
        {
            var mask = RgbaToBgraMask256;
            for (; i <= length - 32; i += 32)
            {
                var v = Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref srcRef, i));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, i), Avx2.Shuffle(v, mask));
            }
        }

        if (Vector128.IsHardwareAccelerated && i <= length - 16)
        {
            var mask = RgbaToBgraMask128;
            for (; i <= length - 16; i += 16)
            {
                var v = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.Add(ref srcRef, i));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dstRef, i), Vector128.Shuffle(v, mask));
            }
        }

        for (; i < length; i += 4)
        {
            var red = source[i];
            destination[i] = source[i + 2];
            destination[i + 1] = source[i + 1];
            destination[i + 2] = red;
            destination[i + 3] = source[i + 3];
        }
    }
}
