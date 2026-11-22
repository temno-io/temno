using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Temno.Core.IO;
using Temno.Media;
using Temno.Media.Cache;
using Xunit;

namespace Temno.Core.Tests;

public class MediaCacheTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly MediaCachePaths _paths;

    public MediaCacheTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"TemnoMediaCacheTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        _paths = new MediaCachePaths(Path.Combine(_tempRoot, "cache"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Ignore cleanup exceptions
        }
    }

    private string WriteSourceFile(string name = "source.mp4", string content = "fake media bytes")
    {
        var path = Path.Combine(_tempRoot, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- MediaCachePaths ----

    [Fact]
    public void CachePaths_Subdirectories_LiveUnderRoot()
    {
        Assert.Equal(Path.Combine(_paths.Root, "thumbnails"), _paths.ThumbnailsDirectory);
        Assert.Equal(Path.Combine(_paths.Root, "proxies"), _paths.ProxiesDirectory);

        var thumbs = _paths.EnsureThumbnailsDirectory();
        var proxies = _paths.EnsureProxiesDirectory();
        Assert.True(Directory.Exists(thumbs));
        Assert.True(Directory.Exists(proxies));
    }

    [Fact]
    public void CachePaths_DefaultRoot_IsPlatformCorrect()
    {
        var root = MediaCachePaths.DefaultRoot();
        Assert.False(string.IsNullOrEmpty(root));
        Assert.True(Path.IsPathRooted(root));

        if (OperatingSystem.IsWindows())
        {
            // WP-334: a development build (every test run is one) keeps its cache apart from the production app's.
            Assert.Equal(Path.Combine(UserDataRoots.LocalAppDataRoot, "cache"), root);
            Assert.EndsWith(Path.Combine("Temno-Dev", "cache"), root);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.EndsWith(Path.Combine("Library", "Caches", "TemnoPro"), root);
        }
    }

    [Fact]
    public void CachePaths_EnvironmentOverride_Wins()
    {
        var custom = Path.Combine(_tempRoot, "override-root");
        Environment.SetEnvironmentVariable(MediaCachePaths.EnvironmentOverrideVariable, custom);
        try
        {
            Assert.Equal(custom, MediaCachePaths.DefaultRoot());
            Assert.Equal(custom, MediaCachePaths.Default.Root);
        }
        finally
        {
            Environment.SetEnvironmentVariable(MediaCachePaths.EnvironmentOverrideVariable, null);
        }
    }

    // ---- MediaCacheKey ----

    [Fact]
    public void CacheKey_SizeMtimeTag_MatchesMacFormat()
    {
        Assert.Equal("1234_99", MediaCacheKey.SizeMtimeTag(1234, 99));
    }

    [Fact]
    public void CacheKey_IsDeterministic_AndSensitiveToEveryInput()
    {
        var identity = new MediaIdentity("/a/b.mp4", 100, 200, "c0ffee");
        var key = MediaCacheKey.Compute(identity, "thumb|t=1.5|h=120");

        Assert.Equal(key, MediaCacheKey.Compute(identity, "thumb|t=1.5|h=120"));
        Assert.Equal(32, key.Length);
        Assert.All(key, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));

        Assert.NotEqual(key, MediaCacheKey.Compute(identity with { Path = "/a/other.mp4" }, "thumb|t=1.5|h=120"));
        Assert.NotEqual(key, MediaCacheKey.Compute(identity with { SizeBytes = 101 }, "thumb|t=1.5|h=120"));
        Assert.NotEqual(key, MediaCacheKey.Compute(identity with { MtimeTicksUtc = 201 }, "thumb|t=1.5|h=120"));
        Assert.NotEqual(key, MediaCacheKey.Compute(identity with { ContentTag = "decaf0" }, "thumb|t=1.5|h=120"));
        Assert.NotEqual(key, MediaCacheKey.Compute(identity, "thumb|t=2.5|h=120"));
        Assert.NotEqual(key, MediaCacheKey.Compute(identity, "thumb|t=1.5|h=240"));
    }

    [Fact]
    public void CacheKey_ChangedMtime_InvalidatesFileStatKey()
    {
        var source = WriteSourceFile();
        var before = ThumbnailCache.KeyFor(source, 1.0, 120);

        File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(5));
        var after = ThumbnailCache.KeyFor(source, 1.0, 120);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void CacheKey_MissingFile_Throws()
    {
        var missing = Path.Combine(_tempRoot, "missing.mp4");
        Assert.Throws<FileNotFoundException>(() => MediaCacheKey.Compute(missing, "p"));
        Assert.Throws<FileNotFoundException>(() => MediaCacheKey.SizeMtimeTag(missing));
        Assert.Throws<FileNotFoundException>(() => MediaIdentity.ForFile(missing));
    }

    // ---- WP-179: same-second, same-length rewrites ----

    [Fact]
    public void CacheKey_SameSecondSameLengthRewrite_ChangesKey_EvenWithTheMtimeRestored()
    {
        // The hardest form of the WP-164 finding 2 case, and the one the pre-WP-179 suite could not
        // see: two writes of the same byte length at a byte-identical modification time. Path, size
        // and mtime are indistinguishable afterwards - SizeMtimeTag proves it - so only the content
        // tag can tell the two states apart.
        var source = WriteSourceFile(content: "aaaaaaaaaaaaaaaa");
        var stamp = File.GetLastWriteTimeUtc(source);
        var before = ThumbnailCache.KeyFor(source, 1.0, 120);
        var beforeTag = MediaCacheKey.SizeMtimeTag(source);

        File.WriteAllText(source, "bbbbbbbbbbbbbbbb");
        File.SetLastWriteTimeUtc(source, stamp);

        Assert.Equal(beforeTag, MediaCacheKey.SizeMtimeTag(source));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(source));
        Assert.NotEqual(before, ThumbnailCache.KeyFor(source, 1.0, 120));
    }

    [Fact]
    public void CacheKey_SubSecondMtimeChange_ChangesKey_WithIdenticalContent()
    {
        // The other half: sub-second mtime is in the key, so a rewrite that happens to produce the
        // same bytes inside the same whole second is still a different entry.
        var source = WriteSourceFile();
        var stamp = File.GetLastWriteTimeUtc(source);
        var before = ThumbnailCache.KeyFor(source, 1.0, 120);
        var beforeTag = MediaCacheKey.SizeMtimeTag(source);

        File.SetLastWriteTimeUtc(source, stamp.AddTicks(1000));

        Assert.Equal(beforeTag, MediaCacheKey.SizeMtimeTag(source));
        Assert.NotEqual(before, ThumbnailCache.KeyFor(source, 1.0, 120));
    }

    [Fact]
    public void MediaIdentity_ForFile_CarriesSubSecondMtimeAndAContentTag()
    {
        var source = WriteSourceFile();
        var identity = MediaIdentity.ForFile(source);

        Assert.Equal(source, identity.Path);
        Assert.Equal(new FileInfo(source).Length, identity.SizeBytes);
        Assert.Equal(File.GetLastWriteTimeUtc(source).Ticks, identity.MtimeTicksUtc);
        Assert.Equal(16, identity.ContentTag.Length);
        Assert.NotEqual(MediaIdentity.UnreadableContentTag, identity.ContentTag);
    }

    [Fact]
    public void CacheKey_LargeFile_SeesAHeadOrTailEditAtTheSameLengthAndMtime()
    {
        var source = Path.Combine(_tempRoot, "large.mp4");
        var bytes = new byte[1024 * 1024];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i * 31);
        }

        File.WriteAllBytes(source, bytes);
        var stamp = File.GetLastWriteTimeUtc(source);
        var original = ThumbnailCache.KeyFor(source, 1.0, 120);

        bytes[10] ^= 0xFF; // head window
        File.WriteAllBytes(source, bytes);
        File.SetLastWriteTimeUtc(source, stamp);
        var headEdited = ThumbnailCache.KeyFor(source, 1.0, 120);
        Assert.NotEqual(original, headEdited);

        bytes[10] ^= 0xFF;
        bytes[^7] ^= 0xFF; // tail window
        File.WriteAllBytes(source, bytes);
        File.SetLastWriteTimeUtc(source, stamp);
        Assert.NotEqual(original, ThumbnailCache.KeyFor(source, 1.0, 120));
    }

    [Fact]
    public void CacheKey_LargeFile_CanMissAnEditOutsideTheSampledWindows()
    {
        // The stated bound, pinned so it cannot drift into a surprise. The tag samples 4 x 16 KiB at
        // the head, one third, two thirds and the tail; an edit that misses all four AND leaves the
        // byte length and the 100 ns mtime untouched is invisible. Documented in
        // docs/verification/wp-179/EVIDENCE.md rather than papered over.
        var source = Path.Combine(_tempRoot, "sampled.mp4");
        var bytes = new byte[1024 * 1024];
        File.WriteAllBytes(source, bytes);
        var stamp = File.GetLastWriteTimeUtc(source);
        var original = ThumbnailCache.KeyFor(source, 1.0, 120);

        bytes[500_000] ^= 0xFF; // between the head/third and third/two-thirds windows
        File.WriteAllBytes(source, bytes);
        File.SetLastWriteTimeUtc(source, stamp);

        Assert.Equal(original, ThumbnailCache.KeyFor(source, 1.0, 120));
    }

    [Fact]
    public async Task ThumbnailCache_SameSecondSameLengthRewrite_RegeneratesUnderANewKey()
    {
        var source = WriteSourceFile(content: "0123456789abcdef");
        var stamp = File.GetLastWriteTimeUtc(source);
        var cache = new ThumbnailCache(_paths);
        var calls = 0;

        Task Generator(string target, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return File.WriteAllBytesAsync(target, new byte[] { 1, 2, 3 }, ct);
        }

        var first = await cache.GetOrCreateAsync(source, 1.0, 120, Generator);

        File.WriteAllText(source, "fedcba9876543210");
        File.SetLastWriteTimeUtc(source, stamp);
        var second = await cache.GetOrCreateAsync(source, 1.0, 120, Generator);

        Assert.NotEqual(first, second);
        Assert.Equal(2, calls);
        Assert.True(cache.TryGet(source, 1.0, 120, out var hit));
        Assert.Equal(second, hit);
    }

    // ---- ThumbnailCache (fake generator; no ffmpeg needed) ----

    [Fact]
    public async Task ThumbnailCache_GetOrCreate_InvokesGeneratorOnce()
    {
        var source = WriteSourceFile();
        var cache = new ThumbnailCache(_paths);
        var calls = 0;

        Task Generator(string target, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return File.WriteAllBytesAsync(target, Encoding.UTF8.GetBytes("thumbnail"), ct);
        }

        var first = await cache.GetOrCreateAsync(source, 1.0, 120, Generator);
        var second = await cache.GetOrCreateAsync(source, 1.0, 120, Generator);

        Assert.Equal(first, second);
        Assert.Equal(1, calls);
        Assert.True(File.Exists(first));
        Assert.EndsWith(".jpg", first);
        Assert.StartsWith(_paths.ThumbnailsDirectory, first);
        Assert.True(cache.TryGet(source, 1.0, 120, out var hitPath));
        Assert.Equal(first, hitPath);
    }

    [Fact]
    public async Task ThumbnailCache_ChangedSource_RegeneratesUnderNewKey()
    {
        var source = WriteSourceFile();
        var cache = new ThumbnailCache(_paths);
        var calls = 0;

        Task Generator(string target, CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            return File.WriteAllBytesAsync(target, new byte[] { 1, 2, 3 }, ct);
        }

        var first = await cache.GetOrCreateAsync(source, 1.0, 120, Generator);

        File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(10));
        var second = await cache.GetOrCreateAsync(source, 1.0, 120, Generator);

        Assert.NotEqual(first, second);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task ThumbnailCache_ConcurrentSameKey_NoCrash_SinglePath()
    {
        var source = WriteSourceFile();
        var cache = new ThumbnailCache(_paths);

        var tasks = Enumerable.Range(0, 8).Select(i => cache.GetOrCreateAsync(
            source, 2.0, 120,
            async (target, ct) =>
            {
                await Task.Delay(10, ct);
                await File.WriteAllBytesAsync(target, Encoding.UTF8.GetBytes($"writer-{i}"), ct);
            }));

        var results = await Task.WhenAll(tasks);

        Assert.Single(results.Distinct());
        Assert.True(File.Exists(results[0]));
        // Exactly one final file; no temp leftovers. The key-format marker is bookkeeping, not an
        // entry, so it is excluded here exactly as it is from the byte budget.
        var files = Directory.GetFiles(_paths.ThumbnailsDirectory)
            .Where(f => Path.GetFileName(f) != DiskCache.FormatMarkerFileName)
            .ToArray();
        Assert.Single(files);
    }

    [Fact]
    public async Task ThumbnailCache_GeneratorProducesNothing_Throws()
    {
        var source = WriteSourceFile();
        var cache = new ThumbnailCache(_paths);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cache.GetOrCreateAsync(source, 1.0, 120, (_, _) => Task.CompletedTask));
    }

    // ---- DiskCache eviction ----

    [Fact]
    public void DiskCache_Evict_PrunesOldestUntilUnderBudget()
    {
        var cache = new DiskCache(Path.Combine(_tempRoot, "evict"), maxTotalBytes: 100);
        // WP-179: stamps the key format before entries are planted, so the first cache call does not
        // read them as leftovers from an older format and sweep them.
        cache.EnsureDirectory();

        var now = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
        {
            var path = cache.PathForKey($"file{i}", ".bin");
            File.WriteAllBytes(path, new byte[40]);
            // file0 oldest ... file4 newest
            File.SetLastWriteTimeUtc(path, now.AddMinutes(i - 10));
        }

        Assert.Equal(200, cache.CurrentSizeBytes());
        var removed = cache.Evict();

        Assert.Equal(3, removed);
        Assert.Equal(80, cache.CurrentSizeBytes());
        Assert.False(File.Exists(cache.PathForKey("file0", ".bin")));
        Assert.False(File.Exists(cache.PathForKey("file1", ".bin")));
        Assert.False(File.Exists(cache.PathForKey("file2", ".bin")));
        Assert.True(File.Exists(cache.PathForKey("file3", ".bin")));
        Assert.True(File.Exists(cache.PathForKey("file4", ".bin")));
    }

    [Fact]
    public void DiskCache_TryGetHit_BumpsLruRecency()
    {
        var cache = new DiskCache(Path.Combine(_tempRoot, "lru"), maxTotalBytes: 50);
        cache.EnsureDirectory();

        var old = DateTime.UtcNow.AddHours(-2);
        var pathA = cache.PathForKey("a", ".bin");
        var pathB = cache.PathForKey("b", ".bin");
        File.WriteAllBytes(pathA, new byte[40]);
        File.WriteAllBytes(pathB, new byte[40]);
        File.SetLastWriteTimeUtc(pathA, old);
        File.SetLastWriteTimeUtc(pathB, old.AddMinutes(30));

        // A is older, but a hit marks it most recently used.
        Assert.True(cache.TryGet("a", ".bin", out _));

        cache.Evict();

        Assert.True(File.Exists(pathA));
        Assert.False(File.Exists(pathB));
    }

    [Fact]
    public async Task DiskCache_AutoEvictAfterCreate_ProtectsNewFile()
    {
        var cache = new DiskCache(Path.Combine(_tempRoot, "auto"), maxTotalBytes: 100);
        cache.EnsureDirectory();

        var old = DateTime.UtcNow.AddHours(-1);
        foreach (var name in new[] { "old1", "old2" })
        {
            var path = cache.PathForKey(name, ".bin");
            File.WriteAllBytes(path, new byte[60]);
            File.SetLastWriteTimeUtc(path, old);
        }

        var created = await cache.GetOrCreateAsync(
            "fresh", ".bin",
            (target, ct) => File.WriteAllBytesAsync(target, new byte[60], ct));

        Assert.True(File.Exists(created));
        Assert.True(cache.CurrentSizeBytes() <= 100);
        Assert.Equal(60, cache.CurrentSizeBytes());
    }

    // ---- WP-179: a hit is pinned, not lucky ----

    /// <summary>
    /// Builds two 40-byte entries in a 50-byte cache: exactly one has to go. "a" is the older of the
    /// two both before and after the hit, so LRU alone would take it.
    /// </summary>
    private (DiskCache Cache, string A, string B) TwoEntriesOverBudget(string name, TimeSpan? grace)
    {
        var cache = new DiskCache(Path.Combine(_tempRoot, name), maxTotalBytes: 50, handoutGrace: grace);
        cache.EnsureDirectory();

        var old = DateTime.UtcNow.AddHours(-2);
        var a = cache.PathForKey("a", ".bin");
        var b = cache.PathForKey("b", ".bin");
        File.WriteAllBytes(a, new byte[40]);
        File.WriteAllBytes(b, new byte[40]);
        File.SetLastWriteTimeUtc(a, old);
        File.SetLastWriteTimeUtc(b, old.AddMinutes(30));
        return (cache, a, b);
    }

    [Fact]
    public void DiskCache_HandoutPin_ProtectsAHitThatLruWouldOtherwiseTake()
    {
        var (cache, a, b) = TwoEntriesOverBudget("pin-on", grace: null);

        Assert.True(cache.TryGet("a", ".bin", out _));
        // Undo the recency bump, so the hit is once again the file LRU wants first. Nothing but the
        // handout pin can save it now - in particular no file handle is held, so this is not the OS
        // refusing a delete.
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddHours(-2));

        cache.Evict();

        Assert.True(File.Exists(a));
        Assert.False(File.Exists(b));
    }

    [Fact]
    public void DiskCache_WithoutTheHandoutPin_TheSameHitIsEvicted()
    {
        // The discriminator for the test above: same sequence, grace disabled, and the hit is gone.
        // This is what every caller got before WP-179.
        var (cache, a, b) = TwoEntriesOverBudget("pin-off", TimeSpan.Zero);

        Assert.True(cache.TryGet("a", ".bin", out _));
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddHours(-2));

        cache.Evict();

        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void DiskCache_Lease_HoldsAcrossEviction_AndReleasesOnDispose()
    {
        var (cache, a, b) = TwoEntriesOverBudget("lease", TimeSpan.Zero);

        Assert.Null(cache.Rent("not-there", ".bin"));

        using (var lease = cache.Rent("a", ".bin"))
        {
            Assert.NotNull(lease);
            Assert.Equal(a, lease!.Path);
            File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddHours(-2));

            cache.Evict();

            Assert.True(File.Exists(a));
            Assert.False(File.Exists(b));
        }

        // Released: "a" is an ordinary LRU candidate again.
        File.WriteAllBytes(b, new byte[40]);
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow);
        cache.Evict();

        Assert.False(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    [Fact]
    public async Task DiskCache_AHitStaysOpenable_WhileEvictionRunsAgainstIt()
    {
        // The WP-164 finding 3 race, run for real. The cache is permanently over budget with one
        // entry, so every eviction pass wants that exact file, and the reader opens it with
        // FileShare.Delete - so the OS would happily let the delete through. Only the pin stops it.
        var cache = new DiskCache(Path.Combine(_tempRoot, "race"), maxTotalBytes: 16);
        cache.EnsureDirectory();
        var only = cache.PathForKey("only", ".bin");
        File.WriteAllBytes(only, new byte[64]);

        Assert.True(cache.TryGet("only", ".bin", out _));

        using var stop = new CancellationTokenSource();
        var evictor = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                cache.Evict();
            }
        });

        try
        {
            for (var i = 0; i < 500; i++)
            {
                Assert.True(cache.TryGet("only", ".bin", out var hit), $"the hit vanished on iteration {i}");
                using var open = new FileStream(
                    hit, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Assert.Equal(64, open.Length);
            }
        }
        finally
        {
            stop.Cancel();
            await evictor;
        }

        Assert.True(File.Exists(only));
    }

    // ---- WP-179: key-format migration ----

    [Fact]
    public void DiskCache_StaleKeyFormat_IsSweptOnFirstUse()
    {
        var directory = Path.Combine(_tempRoot, "format-stale");
        Directory.CreateDirectory(directory);
        var orphan = Path.Combine(directory, new string('a', 32) + ".jpg");
        File.WriteAllBytes(orphan, new byte[128]);

        var cache = new DiskCache(directory, keyFormatVersion: "test-2");

        Assert.False(cache.TryGet(new string('a', 32), ".jpg", out _));
        Assert.False(File.Exists(orphan));
        Assert.Equal("test-2", File.ReadAllText(Path.Combine(directory, DiskCache.FormatMarkerFileName)));
    }

    [Fact]
    public void DiskCache_MatchingKeyFormat_KeepsExistingEntries()
    {
        var directory = Path.Combine(_tempRoot, "format-current");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, DiskCache.FormatMarkerFileName), "test-2");
        var kept = Path.Combine(directory, "bbbb.bin");
        File.WriteAllBytes(kept, new byte[64]);

        var cache = new DiskCache(directory, keyFormatVersion: "test-2");

        Assert.True(cache.TryGet("bbbb", ".bin", out _));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void DiskCache_UnreadableFormatMarker_IsNotTreatedAsStale()
    {
        // Windows only: FileShare.None is what makes the stamp genuinely unreadable while still
        // existing. On POSIX the read succeeds through the lock, so there is nothing to observe.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(_tempRoot, "format-unreadable");
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, DiskCache.FormatMarkerFileName);
        File.WriteAllText(marker, "test-2");
        var kept = Path.Combine(directory, "dddd.bin");
        File.WriteAllBytes(kept, new byte[32]);

        using var held = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None);

        // A version the stamp does NOT match: were the stamp readable, this would sweep.
        var cache = new DiskCache(directory, keyFormatVersion: "test-3");

        Assert.True(cache.TryGet("dddd", ".bin", out _));
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public void DiskCache_FormatMarker_IsOutsideTheBudgetAndSurvivesClear()
    {
        var cache = new DiskCache(Path.Combine(_tempRoot, "format-marker"), maxTotalBytes: 100);

        Assert.False(cache.TryGet("missing", ".bin", out _));
        var marker = Path.Combine(cache.DirectoryPath, DiskCache.FormatMarkerFileName);
        Assert.Equal(MediaCacheKey.FormatVersion, File.ReadAllText(marker));
        Assert.Equal(0, cache.CurrentSizeBytes());

        File.WriteAllBytes(cache.PathForKey("x", ".bin"), new byte[10]);
        Assert.Equal(10, cache.CurrentSizeBytes());

        cache.Clear();

        Assert.Equal(0, cache.CurrentSizeBytes());
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public async Task ThumbnailCache_OldFormatEntries_AreSweptRatherThanLeftToAgeOut()
    {
        var directory = _paths.EnsureThumbnailsDirectory();
        var orphan = Path.Combine(directory, new string('c', 32) + ".jpg");
        File.WriteAllBytes(orphan, new byte[256]);

        var source = WriteSourceFile();
        var cache = new ThumbnailCache(_paths);
        var made = await cache.GetOrCreateAsync(
            source, 1.0, 120, (target, ct) => File.WriteAllBytesAsync(target, new byte[8], ct));

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(made));
        Assert.Equal(
            MediaCacheKey.FormatVersion,
            File.ReadAllText(Path.Combine(directory, DiskCache.FormatMarkerFileName)));
        Assert.Equal(8, cache.CurrentSizeBytes());
    }

    [Fact]
    public void DiskCache_Clear_RemovesEverything()
    {
        var cache = new DiskCache(Path.Combine(_tempRoot, "clear"));
        cache.EnsureDirectory();
        File.WriteAllBytes(cache.PathForKey("x", ".bin"), new byte[10]);
        File.WriteAllBytes(cache.PathForKey("y", ".bin"), new byte[10]);

        cache.Clear();

        Assert.Equal(0, cache.CurrentSizeBytes());
    }

    [Fact]
    public void ClearTree_RemovesNestedFilesAndEmptyDirectories()
    {
        var root = Path.Combine(_tempRoot, "tree-full");
        var proxies = Path.Combine(root, "proxies");
        var thumbs = Path.Combine(root, "thumbnails");
        Directory.CreateDirectory(proxies);
        Directory.CreateDirectory(thumbs);
        File.WriteAllBytes(Path.Combine(proxies, "a.bin"), new byte[2048]);
        File.WriteAllBytes(Path.Combine(thumbs, "b.bin"), new byte[1024]);

        var result = DiskCache.ClearTree(root);

        Assert.True(result.RootExisted);
        Assert.True(result.RootRemoved);
        Assert.Equal(2, result.FilesRemoved);
        Assert.Equal(0, result.FilesSkipped);
        Assert.Equal(3072, result.BytesFreed);
        Assert.False(Directory.Exists(root));
        Assert.Equal(
            "Freed 3 KB of thumbnails, proxies, and visual-search cache. They rebuild on next use.",
            result.Describe());
    }

    [Fact]
    public void ClearTree_MissingRoot_ReportsNoFolders()
    {
        var missing = Path.Combine(_tempRoot, "does-not-exist");
        var result = DiskCache.ClearTree(missing);
        Assert.False(result.RootExisted);
        Assert.Equal(0, result.FilesRemoved);
        Assert.Equal("No cache folders found.", result.Describe());
    }

    [Fact]
    public void ClearTree_SkipsLockedFile_AndRemovesTheRest()
    {
        var root = Path.Combine(_tempRoot, "tree-locked");
        var proxies = Path.Combine(root, "proxies");
        var thumbs = Path.Combine(root, "thumbnails");
        var waveforms = Path.Combine(root, "waveforms");
        Directory.CreateDirectory(proxies);
        Directory.CreateDirectory(thumbs);
        Directory.CreateDirectory(waveforms);
        var locked = Path.Combine(proxies, "held.bin");
        var freeProxy = Path.Combine(proxies, "free.bin");
        var thumb = Path.Combine(thumbs, "t.bin");
        var wave = Path.Combine(waveforms, "w.bin");
        File.WriteAllBytes(locked, new byte[100]);
        File.WriteAllBytes(freeProxy, new byte[50]);
        File.WriteAllBytes(thumb, new byte[25]);
        File.WriteAllBytes(wave, new byte[25]);

        using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = DiskCache.ClearTree(root);

            Assert.True(result.RootExisted);
            Assert.False(result.RootRemoved);
            Assert.Equal(100, result.BytesFreed);
            Assert.Equal(3, result.FilesRemoved);
            Assert.False(File.Exists(freeProxy));
            Assert.False(File.Exists(thumb));
            Assert.False(File.Exists(wave));
            Assert.False(Directory.Exists(thumbs));
            Assert.False(Directory.Exists(waveforms));
            Assert.True(held.CanRead);

            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(1, result.FilesSkipped);
                Assert.True(File.Exists(locked));
                Assert.True(Directory.Exists(proxies));
                Assert.Equal("Freed 100 B. 1 file still in use and was left.", result.Describe());
            }
        }
    }

    [Fact]
    public void ClearTree_LockedFileInOneRoot_DoesNotBlockTheOther()
    {
        var mediaRoot = Path.Combine(_tempRoot, "media-cache");
        var visualRoot = Path.Combine(_tempRoot, "visual-search");
        Directory.CreateDirectory(Path.Combine(mediaRoot, "proxies"));
        Directory.CreateDirectory(visualRoot);
        var locked = Path.Combine(mediaRoot, "proxies", "live.mp4");
        File.WriteAllBytes(locked, new byte[40]);
        File.WriteAllBytes(Path.Combine(mediaRoot, "proxies", "stale.mp4"), new byte[80]);
        File.WriteAllBytes(Path.Combine(visualRoot, "visual-index.json"), new byte[60]);

        using (var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var media = DiskCache.ClearTree(mediaRoot);
            var visual = DiskCache.ClearTree(visualRoot);
            var combined = CacheTreeClearResult.Combine(media, visual);

            Assert.True(visual.RootRemoved);
            Assert.False(Directory.Exists(visualRoot));
            Assert.Equal(60, visual.BytesFreed);
            Assert.Equal(0, visual.FilesSkipped);
            Assert.False(File.Exists(Path.Combine(mediaRoot, "proxies", "stale.mp4")));
            Assert.True(held.CanRead);

            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(1, media.FilesSkipped);
                Assert.True(File.Exists(locked));
                Assert.Equal(140, combined.BytesFreed);
                Assert.Equal(1, combined.FilesSkipped);
                Assert.Equal("Freed 140 B. 1 file still in use and was left.", combined.Describe());
            }
            else
            {
                Assert.True(visual.RootRemoved);
            }
        }
    }

    [Fact]
    public void ClearTree_Describe_NothingRemovedWhenOnlyLeftovers()
    {
        var result = new CacheTreeClearResult(0, 0, 2, RootExisted: true, RootRemoved: false);
        Assert.Equal("2 files still in use and were left. Nothing was removed.", result.Describe());
    }

    // ---- ProxyPolicy ----

    [Theory]
    [InlineData(3840, 2160, "h264", true)]   // 4K always proxies
    [InlineData(1920, 1080, "h264", false)]  // 1080p easy codec plays directly
    [InlineData(1920, 1080, "hevc", true)]   // expensive codec above proxy height
    [InlineData(1280, 720, "hevc", false)]   // proxy would not be smaller
    [InlineData(1080, 1920, "h264", false)]  // vertical video judged by short side
    [InlineData(2160, 3840, "h264", true)]
    [InlineData(null, null, "hevc", true)]   // unknown dims, expensive codec
    [InlineData(null, null, "h264", false)]
    [InlineData(null, null, null, false)]
    [InlineData(1920, 1080, "HEVC", true)]   // codec match is case-insensitive
    public void ProxyPolicy_Default_Heuristics(int? width, int? height, string? codec, bool expected)
    {
        Assert.Equal(expected, ProxyPolicy.Default.ShouldProxy(width, height, codec));
    }

    [Fact]
    public void ProxyPolicy_CustomThresholds_Respected()
    {
        var policy = new ProxyPolicy { MaxDirectPlaybackHeight = 720, ProxyHeight = 360 };
        Assert.True(policy.ShouldProxy(1920, 1080, "h264"));
        Assert.False(policy.ShouldProxy(1280, 720, "h264"));
        Assert.True(policy.ShouldProxy(1280, 720, "hevc"));
    }

    // ---- ProxyCache ----

    [Fact]
    public void ProxyCache_ReservePath_IsDeterministic_AndUnderProxiesDir()
    {
        var source = WriteSourceFile();
        var cache = new ProxyCache(_paths);

        var first = cache.ReservePath(source, 720);
        var second = cache.ReservePath(source, 720);

        Assert.Equal(first, second);
        Assert.StartsWith(_paths.ProxiesDirectory, first);
        Assert.EndsWith(".mp4", first);
        Assert.NotEqual(first, cache.ReservePath(source, 540));
        Assert.False(cache.TryGet(source, 720, out _));
    }

    [Fact]
    public async Task ProxyCache_GetOrCreate_UsesReservedPath()
    {
        var source = WriteSourceFile();
        var cache = new ProxyCache(_paths);
        var reserved = cache.ReservePath(source, 720);

        var created = await cache.GetOrCreateAsync(
            source, 720,
            (target, ct) => File.WriteAllBytesAsync(target, new byte[] { 9 }, ct));

        Assert.Equal(reserved, created);
        Assert.True(cache.TryGet(source, 720, out var hit));
        Assert.Equal(reserved, hit);
    }

    // ---- ffmpeg command builders (pure strings) ----

    [Fact]
    public void ThumbnailGenerator_BuildArguments_ExactString()
    {
        var args = FfmpegThumbnailGenerator.BuildArguments("/media/in put.mp4", 1.5, 120, "/cache/out.jpg");
        // WP-192: the poster is composited over a checkerboard so alpha-only media is not a black square.
        Assert.Equal(
            "-y -ss 1.500 -i \"/media/in put.mp4\" -vframes 1 -vf \"" + FfmpegThumbnailGenerator.PosterFilter(120)
            + "\" -q:v 5 -f image2 \"/cache/out.jpg\"",
            args);
        Assert.StartsWith("scale=trunc(dar*120/2)*2:120,setsar=1,format=rgba,split[bg][fg];[bg]format=rgb24,geq=", FfmpegThumbnailGenerator.PosterFilter(120));
        Assert.EndsWith("[bgc];[bgc][fg]overlay=format=auto", FfmpegThumbnailGenerator.PosterFilter(120));
    }

    [Fact]
    public void ThumbnailGenerator_BuildArguments_NoSeekAtZero()
    {
        // WP-420: a still's poster is at 0 s, and an input seek, even to 0, left FFmpeg with no frame of a JPEG.
        var args = FfmpegThumbnailGenerator.BuildArguments("/media/still.jpg", 0, 36, "/cache/out.jpg");
        Assert.Equal(
            "-y -i \"/media/still.jpg\" -vframes 1 -vf \"" + FfmpegThumbnailGenerator.PosterFilter(36)
            + "\" -q:v 5 -f image2 \"/cache/out.jpg\"",
            args);
        Assert.DoesNotContain("-ss", args);
        Assert.StartsWith("-y -ss 0.001 -i ", FfmpegThumbnailGenerator.BuildArguments("/media/clip.mp4", 0.001, 36, "/cache/out.jpg"));
    }

    [Fact]
    public void ProxyTranscoder_BuildArguments_ExactString()
    {
        var args = ProxyTranscoder.BuildArguments("/media/src.mov", "/cache/proxy.mp4", 720);
        Assert.Equal(
            "-y -i \"/media/src.mov\" -vf scale=-2:720 -c:v libx264 -preset veryfast -crf 23 " +
            "-g 30 -pix_fmt yuv420p -c:a aac -b:a 128k -movflags +faststart -f mp4 \"/cache/proxy.mp4\"",
            args);
    }

    // ---- ffmpeg integration (skip gracefully when ffmpeg is absent) ----

    [Fact]
    public async Task ThumbnailCache_FfmpegOverload_WritesScaledJpeg()
    {
        if (!IsFfmpegAvailable())
        {
            return;
        }

        string? video = null;
        try
        {
            video = await GenerateTestVideoAsync();
            var cache = new ThumbnailCache(_paths);

            var path = await cache.GetOrCreateAsync(video, 0.5, 120);

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);
            Assert.EndsWith(".jpg", path);

            var again = await cache.GetOrCreateAsync(video, 0.5, 120);
            Assert.Equal(path, again);
        }
        finally
        {
            TryDeleteFile(video);
        }
    }

    [Fact]
    public async Task ProxyCache_FfmpegOverload_TranscodesScrubProxy()
    {
        if (!IsFfmpegAvailable())
        {
            return;
        }

        string? video = null;
        try
        {
            video = await GenerateTestVideoAsync();
            var cache = new ProxyCache(_paths);

            var path = await cache.GetOrCreateAsync(video, 240);

            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);

            var info = await new FfmpegMediaProbe().ProbeAsync(path);
            Assert.True(info.HasVideo);
            Assert.Equal(240, info.Height);
        }
        finally
        {
            TryDeleteFile(video);
        }
    }

    // ---- helpers (same pattern as MediaProbeTests) ----

    private bool IsFfmpegAvailable()
    {
        try
        {
            var ffmpegPath = FFmpegResolver.GetFFmpegPath();
            var ffprobePath = FFmpegResolver.GetFFprobePath();

            if (File.Exists(ffmpegPath) && File.Exists(ffprobePath))
            {
                return true;
            }

            using var process = new Process();
            process.StartInfo.FileName = ffmpegPath;
            process.StartInfo.Arguments = "-version";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.Start();
            process.WaitForExit();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> GenerateTestVideoAsync()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"temno_test_{Guid.NewGuid():N}.mp4");
        var ffmpegPath = FFmpegResolver.GetFFmpegPath();
        var arguments = $"-y -f lavfi -i testsrc=duration=1:size=1280x720:rate=30 -c:v libx264 -pix_fmt yuv420p \"{tempPath}\"";

        using var process = new Process();
        process.StartInfo.FileName = ffmpegPath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.CreateNoWindow = true;

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start ffmpeg to generate test video.");
        }

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg failed to generate test video. Exit code: {process.ExitCode}");
        }

        return tempPath;
    }

    private static void TryDeleteFile(string? path)
    {
        if (path == null || !File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch
        {
            // Ignore cleanup exceptions
        }
    }
}
