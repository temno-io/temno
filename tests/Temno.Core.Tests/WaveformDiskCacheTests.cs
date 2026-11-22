using Temno.Media.Audio;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>WP-213: waveform disk cache — reuse across reopen, identity validation, safe misses.</summary>
public sealed class WaveformDiskCacheTests
{
    private static string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "wp213-wcache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string TouchMedia(string root, byte[]? content = null)
    {
        var path = Path.Combine(root, "media-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(path, content ?? [10, 20, 30, 40]);
        return path;
    }

    [Fact]
    public void SaveThenLoad_RoundTripsPeaks()
    {
        var root = NewTempRoot();
        try
        {
            var media = TouchMedia(root);
            var cache = new WaveformDiskCache(root);
            var peaks = new float[400];
            peaks[7] = 0.33f;
            peaks[399] = 1.0f;
            cache.Save(media, new WaveformEnvelope(peaks, 200));

            Assert.True(cache.TryLoad(media, 200, out var loaded));
            Assert.NotNull(loaded);
            Assert.Equal(200, loaded.PeaksPerSecond);
            Assert.Equal(peaks, loaded.Peaks);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ChangedFile_MissesInsteadOfReturningStalePeaks()
    {
        var root = NewTempRoot();
        try
        {
            var media = TouchMedia(root);
            var cache = new WaveformDiskCache(root);
            cache.Save(media, new WaveformEnvelope([0.5f, 0.5f], 200));

            File.WriteAllBytes(media, [10, 20, 30, 40, 50, 60]); // different length
            Assert.False(cache.TryLoad(media, 200, out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RewrittenSameLengthFile_MissesOnMtime()
    {
        var root = NewTempRoot();
        try
        {
            var media = TouchMedia(root);
            var cache = new WaveformDiskCache(root);
            cache.Save(media, new WaveformEnvelope([0.5f], 200));

            File.SetLastWriteTimeUtc(media, File.GetLastWriteTimeUtc(media).AddHours(1));
            Assert.False(cache.TryLoad(media, 200, out _));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CorruptOrMissing_MissesWithoutThrowing()
    {
        var root = NewTempRoot();
        try
        {
            var media = TouchMedia(root);
            var cache = new WaveformDiskCache(root);
            Assert.False(cache.TryLoad(media, 200, out _)); // nothing cached yet

            cache.Save(media, new WaveformEnvelope([0.5f], 200));
            Assert.False(cache.TryLoad(media, 100, out _)); // rate mismatch

            var cacheFile = Directory.GetFiles(Path.Combine(root, "waveforms"))[0];
            File.WriteAllBytes(cacheFile, [0xDE, 0xAD, 0xBE, 0xEF]); // truncated garbage
            Assert.False(cache.TryLoad(media, 200, out _));

            File.Delete(media);
            Assert.False(cache.TryLoad(media, 200, out _)); // media gone
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
