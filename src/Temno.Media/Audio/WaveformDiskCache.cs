using System.Security.Cryptography;
using System.Text;

namespace Temno.Media.Audio;

/// <summary>
/// Best-effort on-disk store for <see cref="WaveformEnvelope"/> data so reopening a project
/// reuses waveforms without re-decoding (WP-213). File identity (byte length + last-write
/// ticks) is validated on load; a mismatch or corrupt file is a cache miss, never an error.
/// Lives under <c>{cacheRoot}/waveforms</c> next to the thumbnail/proxy subdirectories.
/// </summary>
public sealed class WaveformDiskCache
{
    public const string SubdirectoryName = "waveforms";

    private const uint Magic = 0x574D4E54; // "TNMW" little-endian
    private const int Version = 1;
    private const int HeaderBytes = 4 + 4 + 4 + 4 + 8 + 8;

    private readonly string _directory;

    public WaveformDiskCache(string? cacheRootDirectory = null)
    {
        var root = cacheRootDirectory ?? Cache.MediaCachePaths.Default.Root;
        _directory = Path.Combine(root, SubdirectoryName);
    }

    public string CacheDirectory => _directory;

    public bool TryLoad(string mediaPath, int peaksPerSecond, out WaveformEnvelope? envelope)
    {
        envelope = null;
        try
        {
            var info = new FileInfo(mediaPath);
            if (!info.Exists) return false;
            var cachePath = CachePathFor(mediaPath);
            if (!File.Exists(cachePath)) return false;

            using var stream = File.OpenRead(cachePath);
            if (stream.Length < HeaderBytes) return false;
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != Magic) return false;
            if (reader.ReadInt32() != Version) return false;
            if (reader.ReadInt32() != peaksPerSecond) return false;
            var peakCount = reader.ReadInt32();
            var sourceLength = reader.ReadInt64();
            var sourceMtimeTicks = reader.ReadInt64();
            if (peakCount < 0 || peakCount > stream.Length) return false;
            if (sourceLength != info.Length || sourceMtimeTicks != info.LastWriteTimeUtc.Ticks) return false;
            if (stream.Length != HeaderBytes + (long)peakCount * sizeof(float)) return false;

            var peaks = new float[peakCount];
            var bytes = reader.ReadBytes(peakCount * sizeof(float));
            if (bytes.Length != peakCount * sizeof(float)) return false;
            Buffer.BlockCopy(bytes, 0, peaks, 0, bytes.Length);
            envelope = new WaveformEnvelope(peaks, peaksPerSecond);
            return true;
        }
        catch
        {
            envelope = null;
            return false;
        }
    }

    public void Save(string mediaPath, WaveformEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        try
        {
            var info = new FileInfo(mediaPath);
            if (!info.Exists) return;
            System.IO.Directory.CreateDirectory(_directory);
            var cachePath = CachePathFor(mediaPath);
            var tempPath = cachePath + ".tmp";
            using (var stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(envelope.PeaksPerSecond);
                writer.Write(envelope.Peaks.Length);
                writer.Write(info.Length);
                writer.Write(info.LastWriteTimeUtc.Ticks);
                var bytes = new byte[envelope.Peaks.Length * sizeof(float)];
                Buffer.BlockCopy(envelope.Peaks, 0, bytes, 0, bytes.Length);
                writer.Write(bytes);
            }
            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch
        {
            // Cache writes must never fail a waveform request.
        }
    }

    private string CachePathFor(string mediaPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(mediaPath));
        return Path.Combine(_directory, Convert.ToHexString(hash) + ".waveform");
    }
}
