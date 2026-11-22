using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Temno.Media.Cache;

/// <summary>
/// What a cached derivation was made from: the source path, its byte length, its full-precision
/// modification time, and a bounded digest of its bytes.
///
/// <para><b>Why the content tag exists.</b> Until WP-179 identity was path + size + <i>whole-second</i>
/// mtime, so two writes to one path inside the same second at the same byte length produced the same
/// key and a stale proxy or thumbnail was served with no invalidation - reachable whenever a render
/// pipeline rewrites a file in place. <see cref="MtimeTicksUtc"/> fixes that wherever the filesystem
/// records sub-second write times (NTFS, APFS, ext4); <see cref="ContentTag"/> closes the rest,
/// including a filesystem that quantises timestamps (FAT32 to 2 s, exFAT to 10 ms) and a file whose
/// timestamp was restored verbatim by a copy or a backup tool.</para>
///
/// <para><b>The tag is a sample, not a whole-file hash.</b> Files at or below
/// <see cref="FullDigestMaxBytes"/> are hashed entirely. Larger ones are hashed over four
/// <see cref="SampleWindowBytes"/> windows at the head, two interior offsets and the tail, so the
/// read is bounded at 64 KiB no matter how large the media is and the call stays cheap enough for the
/// decode path. The residual is stated in docs/verification/wp-179/EVIDENCE.md: a same-length
/// in-place rewrite that leaves the head, both interior windows and the tail byte-identical - and
/// also lands on the same 100 ns mtime - is still indistinguishable.</para>
/// </summary>
public readonly record struct MediaIdentity(string Path, long SizeBytes, long MtimeTicksUtc, string ContentTag)
{
    /// <summary>Files this size or smaller are digested whole; larger ones are sampled.</summary>
    public const int FullDigestMaxBytes = 64 * 1024;

    /// <summary>Bytes read per sample window; four windows are taken.</summary>
    public const int SampleWindowBytes = 16 * 1024;

    /// <summary>Content tag used when the bytes cannot be read at all (locked, or a permission denial).</summary>
    public const string UnreadableContentTag = "unread";

    /// <summary>Stats the file and samples its bytes. Throws when the file is not there.</summary>
    public static MediaIdentity ForFile(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Media file not found.", path);
        }

        var length = info.Length;
        return new MediaIdentity(path, length, info.LastWriteTimeUtc.Ticks, ContentTagFor(path, length));
    }

    /// <summary>
    /// Bounded digest of the file's bytes. Opens shared for read, write and delete so sampling can
    /// never block the render pipeline that is writing the file, and never fails the caller: an
    /// unreadable file yields <see cref="UnreadableContentTag"/> and identity falls back to size and
    /// mtime, which is what it was before this package.
    /// </summary>
    private static string ContentTagFor(string path, long length)
    {
        byte[]? buffer = null;
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1,
                FileOptions.None);

            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Span<byte> lengthBytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, length);
            digest.AppendData(lengthBytes);

            buffer = ArrayPool<byte>.Shared.Rent(SampleWindowBytes);
            if (length <= FullDigestMaxBytes)
            {
                AppendWindow(stream, digest, 0, length, buffer);
            }
            else
            {
                // Head, two interior offsets, tail. Windows can overlap on files just over the
                // threshold; the offset is folded in so an overlap is not a silent duplicate.
                AppendWindow(stream, digest, 0, SampleWindowBytes, buffer);
                AppendWindow(stream, digest, length / 3, SampleWindowBytes, buffer);
                AppendWindow(stream, digest, length / 3 * 2, SampleWindowBytes, buffer);
                AppendWindow(stream, digest, length - SampleWindowBytes, SampleWindowBytes, buffer);
            }

            return Convert.ToHexString(digest.GetHashAndReset().AsSpan(0, 8)).ToLowerInvariant();
        }
        catch (IOException)
        {
            return UnreadableContentTag;
        }
        catch (UnauthorizedAccessException)
        {
            return UnreadableContentTag;
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static void AppendWindow(
        FileStream stream,
        IncrementalHash digest,
        long offset,
        long count,
        byte[] buffer)
    {
        Span<byte> offsetBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(offsetBytes, offset);
        digest.AppendData(offsetBytes);

        if (count <= 0)
        {
            return;
        }

        stream.Position = offset < 0 ? 0 : offset;
        var remaining = count;
        while (remaining > 0)
        {
            var want = (int)Math.Min(remaining, buffer.Length);
            var read = stream.Read(buffer, 0, want);
            if (read <= 0)
            {
                return;
            }

            digest.AppendData(buffer.AsSpan(0, read));
            remaining -= read;
        }
    }
}

/// <summary>
/// Deterministic cache keys derived from source identity (<see cref="MediaIdentity"/>) plus
/// derivation parameters.
/// Mac: Utilities/DiskCache.swift (sizeMtimeTag) and Timeline/MediaVisualCache.swift (diskCacheKey).
/// </summary>
public static class MediaCacheKey
{
    /// <summary>
    /// Bumped whenever the seed changes shape, so a stale on-disk entry can never be mistaken for a
    /// current one. Version 1 was path + size + whole-second mtime; version 2 adds sub-second mtime
    /// and the content tag. <see cref="DiskCache"/> sweeps a directory whose recorded version does
    /// not match this one, because old and new keys are both opaque 32-hex names and cannot be told
    /// apart by inspection.
    /// </summary>
    public const string FormatVersion = "2";

    /// <summary>
    /// Cheap invalidation tag "{sizeBytes}_{mtimeUnixSeconds}", identical to Mac DiskCache.sizeMtimeTag.
    /// This is the <b>Mac interchange</b> tag and deliberately keeps its whole-second shape; it is not
    /// the cache key. Use <see cref="Compute(string,string)"/> for anything that gates a cache hit.
    /// </summary>
    public static string SizeMtimeTag(string path)
    {
        var info = StatFile(path);
        return SizeMtimeTag(info.Length, new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds());
    }

    public static string SizeMtimeTag(long sizeBytes, long mtimeUnixSeconds)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{sizeBytes}_{mtimeUnixSeconds}");
    }

    /// <summary>Stats and samples the source, then hashes identity + parameters into a stable filename-safe key.</summary>
    public static string Compute(string sourcePath, string parameters)
    {
        return Compute(MediaIdentity.ForFile(sourcePath), parameters);
    }

    /// <summary>
    /// Pure form: first 16 bytes of
    /// SHA256("v{FormatVersion}|{path}|{size}|{mtimeTicks}|{contentTag}|{parameters}") as 32
    /// lowercase hex chars. Mac MediaVisualCache hashes "{path}|{size}|{mtime}"; parameters extend
    /// the seed for per-derivation keys.
    /// </summary>
    public static string Compute(in MediaIdentity identity, string parameters)
    {
        if (string.IsNullOrEmpty(identity.Path))
        {
            throw new ArgumentException("Source path cannot be null or empty.", nameof(identity));
        }

        ArgumentNullException.ThrowIfNull(parameters);

        var seed = string.Create(
            CultureInfo.InvariantCulture,
            $"v{FormatVersion}|{identity.Path}|{identity.SizeBytes}|{identity.MtimeTicksUtc}|{identity.ContentTag}|{parameters}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(digest.AsSpan(0, 16)).ToLowerInvariant();
    }

    private static FileInfo StatFile(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new ArgumentException("Path cannot be null or empty.", nameof(path));
        }

        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("Media file not found.", path);
        }

        return info;
    }
}
