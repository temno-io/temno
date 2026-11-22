namespace Temno.Generation;

// Pure-C# still-image dimension reader (PNG, JPEG, WebP). Landed generation
// bytes are never re-encoded, so this only inspects headers to record actual
// dimensions instead of tagging every generated image as timeline size.

public static class ImageDimensions
{
    public static bool TryRead(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        try
        {
            using var stream = File.OpenRead(path);
            return TryRead(stream, out width, out height);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryRead(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;
        Span<byte> header = stackalloc byte[32];
        int read = 0;
        while (read < header.Length)
        {
            int n = stream.Read(header.Slice(read));
            if (n == 0)
                break;
            read += n;
        }
        if (read < 12)
            return false;

        if (IsPng(header))
            return TryReadPng(stream, header, read, out width, out height);
        if (IsJpeg(header))
            return TryReadJpeg(stream, out width, out height);
        if (IsRiffWebp(header))
            return TryReadWebp(stream, header, read, out width, out height);
        return false;
    }

    private static bool IsPng(ReadOnlySpan<byte> h)
        => h.Length >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47
            && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A;

    private static bool TryReadPng(Stream stream, ReadOnlySpan<byte> header, int headerBytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        Span<byte> ihdr = stackalloc byte[24];
        int have = Math.Min(headerBytes, ihdr.Length);
        header.Slice(0, have).CopyTo(ihdr);
        while (have < ihdr.Length)
        {
            int n = stream.Read(ihdr.Slice(have));
            if (n == 0)
                return false;
            have += n;
        }
        // Width/height are the first 8 bytes of the IHDR chunk data.
        if (ihdr[12] != (byte)'I' || ihdr[13] != (byte)'H' || ihdr[14] != (byte)'D' || ihdr[15] != (byte)'R')
            return false;
        width = (ihdr[16] << 24) | (ihdr[17] << 16) | (ihdr[18] << 8) | ihdr[19];
        height = (ihdr[20] << 24) | (ihdr[21] << 16) | (ihdr[22] << 8) | ihdr[23];
        return width > 0 && height > 0;
    }

    private static bool IsJpeg(ReadOnlySpan<byte> h)
        => h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF;

    private static bool TryReadJpeg(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (!stream.CanSeek) return false;
        stream.Seek(2, SeekOrigin.Begin);
        Span<byte> seg = stackalloc byte[4];
        for (int guard = 0; guard < 256; guard++)
        {
            if (!Fill(stream, seg.Slice(0, 2)) || seg[0] != 0xFF)
                return false;
            byte marker = seg[1];
            if (marker == 0xD8 || marker == 0xD9)
                continue;
            if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                continue;
            if (!Fill(stream, seg.Slice(0, 2)))
                return false;
            int length = (seg[0] << 8) | seg[1];
            if (length < 2)
                return false;
            bool isSof = marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);
            if (isSof)
            {
                Span<byte> sof = stackalloc byte[5];
                if (!Fill(stream, sof))
                    return false;
                height = (sof[1] << 8) | sof[2];
                width = (sof[3] << 8) | sof[4];
                return width > 0 && height > 0;
            }
            stream.Seek(length - 2, SeekOrigin.Current);
        }
        return false;
    }

    private static bool IsRiffWebp(ReadOnlySpan<byte> h)
        => h.Length >= 12 && h[0] == (byte)'R' && h[1] == (byte)'I' && h[2] == (byte)'F' && h[3] == (byte)'F'
            && h[8] == (byte)'W' && h[9] == (byte)'E' && h[10] == (byte)'B' && h[11] == (byte)'P';

    private static bool TryReadWebp(Stream stream, ReadOnlySpan<byte> header, int headerBytes, out int width, out int height)
    {
        width = 0;
        height = 0;
        Span<byte> chunk = stackalloc byte[30];
        int have = Math.Min(headerBytes, chunk.Length);
        header.Slice(0, have).CopyTo(chunk);
        while (have < chunk.Length)
        {
            int n = stream.Read(chunk.Slice(have));
            if (n == 0)
                break;
            have += n;
        }
        if (have < 21)
            return false;
        string fourCc = ((char)chunk[12]).ToString() + (char)chunk[13] + (char)chunk[14] + (char)chunk[15];
        switch (fourCc)
        {
            case "VP8 ":
                if (have < 30)
                    return false;
                width = chunk[26] | (chunk[27] << 8);
                height = chunk[28] | (chunk[29] << 8);
                width &= 0x3FFF;
                height &= 0x3FFF;
                return width > 0 && height > 0;
            case "VP8L":
                if (have < 25)
                    return false;
                int bits = chunk[21] | (chunk[22] << 8) | (chunk[23] << 16) | (chunk[24] << 24);
                width = (bits & 0x3FFF) + 1;
                height = ((bits >> 14) & 0x3FFF) + 1;
                return true;
            case "VP8X":
                if (have < 30)
                    return false;
                width = (chunk[24] | (chunk[25] << 8) | (chunk[26] << 16)) + 1;
                height = (chunk[27] | (chunk[28] << 8) | (chunk[29] << 16)) + 1;
                return true;
            default:
                return false;
        }
    }

    private static bool Fill(Stream stream, Span<byte> buffer)
    {
        int have = 0;
        while (have < buffer.Length)
        {
            int n = stream.Read(buffer.Slice(have));
            if (n == 0)
                return false;
            have += n;
        }
        return true;
    }
}
