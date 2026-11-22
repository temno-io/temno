using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Temno.Media.Cache;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-239. Pins must survive every path form the cache root can take, and a format sweep may
/// delete only recognized obsolete cache entries. Fixtures stay under a disposable temp root or a
/// uniquely named relative directory that this class deletes.
/// </summary>
public class CacheMigrationOwnershipTests : IDisposable
{
    private const string Version = "test-239";

    private readonly string _tempRoot;
    private readonly string _relativeTop;

    public CacheMigrationOwnershipTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"TemnoWp239_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
        // Temp and the test cwd are on different drives on the dev machine, so a relative cache
        // root has to live under the cwd or Path.GetRelativePath hands back an absolute path.
        _relativeTop = "wp239-" + Guid.NewGuid().ToString("N");
    }

    public void Dispose()
    {
        TryDeleteTree(_tempRoot);
        TryDeleteTree(Path.GetFullPath(_relativeTop));
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("forward-slash")]
    [InlineData("relative")]
    [InlineData("trailing-separator")]
    public void HandoutGrace_ProtectsTheSameFile_UnderEveryRootForm(string variant)
    {
        var (cache, file) = PlantOverBudget(variant, "grace", TimeSpan.FromMinutes(1));

        Assert.True(cache.TryGet(Key, ".bin", out _));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2));

        Assert.Equal(0, cache.Evict());
        Assert.True(File.Exists(file));
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("forward-slash")]
    [InlineData("relative")]
    [InlineData("trailing-separator")]
    public void LiveLease_ProtectsTheSameFile_UnderEveryRootForm(string variant)
    {
        var (cache, file) = PlantOverBudget(variant, "lease", TimeSpan.Zero);

        using (var lease = cache.Rent(Key, ".bin"))
        {
            Assert.NotNull(lease);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2));
            Assert.Equal(0, cache.Evict());
            Assert.True(File.Exists(file));
        }

        Assert.Equal(1, cache.Evict());
        Assert.False(File.Exists(file));
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("forward-slash")]
    [InlineData("relative")]
    [InlineData("trailing-separator")]
    public async Task NewFile_SurvivesForcedEviction_UnderEveryRootForm(string variant)
    {
        var cache = Open(variant, "create", maxTotalBytes: 1, handoutGrace: TimeSpan.Zero);
        cache.EnsureDirectory();
        var old = cache.PathForKey("old", ".bin");
        File.WriteAllBytes(old, new byte[40]);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));

        var created = await cache.GetOrCreateAsync(
            "fresh", ".bin", (target, ct) => File.WriteAllBytesAsync(target, new byte[40], ct));

        Assert.True(File.Exists(created));
        Assert.False(File.Exists(old));
    }

    [Theory]
    [InlineData("canonical")]
    [InlineData("forward-slash")]
    [InlineData("relative")]
    [InlineData("trailing-separator")]
    public void UnpinnedEntry_StillEvicts_UnderEveryRootForm(string variant)
    {
        var (cache, file) = PlantOverBudget(variant, "unpinned", TimeSpan.Zero);

        Assert.True(cache.TryGet(Key, ".bin", out _));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2));

        Assert.Equal(1, cache.Evict());
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void UnstampedDirectory_DeletesOnlyObsoleteCacheEntries()
    {
        var dir = Path.Combine(_tempRoot, "cache-unmarked");
        var planted = PlantUnstamped(dir);

        var cache = new DiskCache(dir, keyFormatVersion: Version);
        cache.EnsureDirectory();

        AssertSurvivors(dir, planted);
        Assert.False(File.Exists(planted.Obsolete));
        AssertCleanPublication(dir);
    }

    [Fact]
    public void StaleMarker_DeletesOnlyObsoleteCacheEntries()
    {
        var dir = Path.Combine(_tempRoot, "cache-stale");
        var planted = PlantUnstamped(dir);
        File.WriteAllText(Path.Combine(dir, DiskCache.FormatMarkerFileName), "1");

        var cache = new DiskCache(dir, keyFormatVersion: Version);
        cache.EnsureDirectory();

        AssertSurvivors(dir, planted);
        Assert.False(File.Exists(planted.Obsolete));
        AssertCleanPublication(dir);
    }

    [Fact]
    public void InterruptedMarker_PublishesTheVersion_AndKeepsThePartialTemp()
    {
        var dir = Path.Combine(_tempRoot, "cache-interrupted");
        var planted = PlantUnstamped(dir);
        var marker = Path.Combine(dir, DiskCache.FormatMarkerFileName);
        File.WriteAllText(marker, "tes");
        var partial = marker + ".writing-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(partial, "tes");

        var cache = new DiskCache(dir, keyFormatVersion: Version);
        cache.EnsureDirectory();

        AssertSurvivors(dir, planted);
        Assert.True(File.Exists(partial));
        Assert.False(File.Exists(planted.Obsolete));
        Assert.Equal(Version, File.ReadAllText(marker).Trim());
        Assert.False(File.Exists(Path.Combine(dir, DiskCache.FormatLockFileName)));
        Assert.Equal(new[] { partial }, Directory.GetFiles(dir, ".cache-format.writing-*"));
    }

    [Fact]
    public void MatchingMarker_KeepsCurrentEntries()
    {
        var dir = Path.Combine(_tempRoot, "cache-current");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, DiskCache.FormatMarkerFileName), Version);
        var current = Path.Combine(dir, new string('c', 32) + ".mp4");
        File.WriteAllBytes(current, new byte[8]);
        var ordinary = Path.Combine(dir, "audit-not-a-cache-file.txt");
        File.WriteAllText(ordinary, "fixture only");

        var cache = new DiskCache(dir, keyFormatVersion: Version);
        cache.EnsureDirectory();

        Assert.True(File.Exists(current));
        Assert.True(File.Exists(ordinary));
        AssertCleanPublication(dir);
    }

    [Fact]
    public void TwoInstances_InitializeOnce_AndTheSecondDoesNotResweep()
    {
        var dir = Path.Combine(_tempRoot, "cache-two");
        var planted = PlantUnstamped(dir);
        var first = new DiskCache(dir, keyFormatVersion: Version);
        var second = new DiskCache(dir.Replace('\\', '/'), keyFormatVersion: Version);

        Parallel.Invoke(() => first.EnsureDirectory(), () => second.EnsureDirectory());

        AssertSurvivors(dir, planted);
        Assert.False(File.Exists(planted.Obsolete));
        Assert.Equal(Version, File.ReadAllText(Path.Combine(dir, DiskCache.FormatMarkerFileName)).Trim());

        var current = Path.Combine(dir, new string('c', 32) + ".jpg");
        File.WriteAllBytes(current, new byte[4]);
        var third = new DiskCache(dir + Path.DirectorySeparatorChar, keyFormatVersion: Version);
        third.EnsureDirectory();

        Assert.True(File.Exists(current));
        Assert.True(File.Exists(planted.Ordinary));
        AssertCleanPublication(dir);
    }

    [Fact]
    public void SecondInstance_RereadsTheMarker_InsteadOfSweepingACurrentEntry()
    {
        var dir = Path.Combine(_tempRoot, "cache-reread");
        var planted = PlantUnstamped(dir);
        new DiskCache(dir, keyFormatVersion: Version).EnsureDirectory();
        Assert.False(File.Exists(planted.Obsolete));

        var current = Path.Combine(dir, new string('e', 32) + ".jpg");
        File.WriteAllBytes(current, new byte[4]);
        DiskCache.ForgetEnsuredDirectory(dir);

        new DiskCache(dir.Replace('\\', '/'), keyFormatVersion: Version).EnsureDirectory();

        Assert.True(File.Exists(current));
        Assert.True(File.Exists(planted.Ordinary));
        Assert.True(File.Exists(planted.Writing));
        AssertCleanPublication(dir);
    }

    [Fact]
    public void HeldFormatLock_BlocksTheSweep_UntilItIsReleased()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(_tempRoot, "cache-lock");
        var planted = PlantUnstamped(dir);
        using var held = DiskCache.TryAcquireFormatLock(dir);
        Assert.NotNull(held);

        var cache = new DiskCache(dir, keyFormatVersion: Version);
        Assert.False(cache.TryEnsureFormatOnce());
        Assert.True(File.Exists(planted.Obsolete));
        Assert.True(File.Exists(planted.Ordinary));
        Assert.True(File.Exists(planted.Writing));

        held.Dispose();

        Assert.True(cache.TryEnsureFormatOnce());
        Assert.False(File.Exists(planted.Obsolete));
        AssertSurvivors(dir, planted);
        AssertCleanPublication(dir);
    }

    [Fact]
    public void PinnedObsoleteEntry_SurvivesTheSweep_AcrossPathForms()
    {
        var dir = Path.Combine(_tempRoot, "cache-pinned");
        Directory.CreateDirectory(dir);
        var obsolete = Path.Combine(dir, new string('d', 32) + ".jpg");
        File.WriteAllBytes(obsolete, new byte[16]);
        var slash = obsolete.Replace('\\', '/');
        CachePins.Acquire(slash);
        try
        {
            var cache = new DiskCache(dir, maxTotalBytes: 1, handoutGrace: TimeSpan.Zero, keyFormatVersion: Version);
            cache.EnsureDirectory();
            Assert.True(File.Exists(obsolete));

            Assert.Equal(0, cache.Evict());
            Assert.True(File.Exists(obsolete));
        }
        finally
        {
            CachePins.Release(obsolete);
        }

        var cacheAfter = new DiskCache(dir, maxTotalBytes: 1, handoutGrace: TimeSpan.Zero, keyFormatVersion: Version);
        Assert.Equal(1, cacheAfter.Evict());
        Assert.False(File.Exists(obsolete));
    }

    [Fact]
    public void DirectoryIdentity_IgnoresSeparatorAndTrailingSlash()
    {
        var dir = Path.Combine(_tempRoot, "cache-identity");
        var canonical = DiskCache.CanonicalDirectoryPath(dir);
        Assert.Equal(canonical, DiskCache.CanonicalDirectoryPath(dir + Path.DirectorySeparatorChar));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(canonical, DiskCache.CanonicalDirectoryPath(dir.Replace('\\', '/')));
        }

        Assert.False(canonical.EndsWith(Path.DirectorySeparatorChar) && canonical.Length > Path.GetPathRoot(canonical)!.Length);
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg", true)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.MP4", true)]
    [InlineData("0123456789abcdef0123456789abcdef", true)]
    [InlineData("audit-not-a-cache-file.txt", false)]
    [InlineData("notes.bin", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.writing-0123456789abcdef0123456789abcdef.mp4", false)]
    [InlineData(".cache-format", false)]
    [InlineData(".cache-format.lock", false)]
    [InlineData(".cache-format.writing-0123456789abcdef0123456789abcdef", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.tar.gz", false)]
    public void ObsoleteEntry_IsAFinal32HexName(string name, bool obsolete)
    {
        Assert.Equal(obsolete, DiskCache.IsObsoleteCacheEntryName(name));
    }

    [Fact]
    public void UppercaseAndBareHexEntries_AreSwept()
    {
        var dir = Path.Combine(_tempRoot, "cache-upper");
        Directory.CreateDirectory(dir);
        var upper = Path.Combine(dir, new string('A', 32) + ".JPG");
        var bare = Path.Combine(dir, new string('b', 32));
        File.WriteAllBytes(upper, new byte[4]);
        File.WriteAllBytes(bare, new byte[4]);

        new DiskCache(dir, keyFormatVersion: Version).EnsureDirectory();

        Assert.False(File.Exists(upper));
        Assert.False(File.Exists(bare));
        AssertCleanPublication(dir);
    }

    [Fact]
    public void ExpiredHandoutGrace_Evicts_OnAForwardSlashRoot()
    {
        var cache = Open("forward-slash", "expired", maxTotalBytes: 1, TimeSpan.FromMilliseconds(1));
        cache.EnsureDirectory();
        var file = cache.PathForKey(Key, ".bin");
        File.WriteAllBytes(file, new byte[16]);
        Assert.True(cache.TryGet(Key, ".bin", out _));

        var until = Environment.TickCount64 + 30;
        while (Environment.TickCount64 < until)
        {
            Thread.SpinWait(20);
        }

        Assert.Equal(1, cache.Evict());
        Assert.False(File.Exists(file));
    }

    private (DiskCache Cache, string File) PlantOverBudget(string variant, string name, TimeSpan? grace)
    {
        var cache = Open(variant, name, maxTotalBytes: 1, grace);
        cache.EnsureDirectory();
        var file = cache.PathForKey(Key, ".bin");
        File.WriteAllBytes(file, new byte[16]);
        return (cache, file);
    }

    private DiskCache Open(string variant, string name, long maxTotalBytes, TimeSpan? handoutGrace)
    {
        var canonical = variant == "relative"
            ? Path.Combine(_relativeTop, name)
            : Path.Combine(_tempRoot, name + "-" + variant);
        var root = variant switch
        {
            "forward-slash" => canonical.Replace('\\', '/'),
            "trailing-separator" => canonical + Path.DirectorySeparatorChar,
            _ => canonical,
        };
        return new DiskCache(root, maxTotalBytes, handoutGrace, Version);
    }

    private static Planted PlantUnstamped(string dir)
    {
        Directory.CreateDirectory(dir);
        var ordinary = Path.Combine(dir, "audit-not-a-cache-file.txt");
        File.WriteAllText(ordinary, "fixture only");
        var sub = Path.Combine(dir, "not-a-cache-dir");
        Directory.CreateDirectory(sub);
        var inside = Path.Combine(sub, "inside.txt");
        File.WriteAllText(inside, "keep");
        var writing = Path.Combine(dir, new string('b', 32) + ".writing-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(writing, new byte[8]);
        var notes = Path.Combine(dir, "notes.bin");
        File.WriteAllBytes(notes, new byte[4]);
        var obsolete = Path.Combine(dir, new string('a', 32) + ".jpg");
        File.WriteAllBytes(obsolete, new byte[8]);
        return new Planted(ordinary, inside, sub, writing, notes, obsolete);
    }

    private static void AssertCleanPublication(string dir)
    {
        Assert.Equal(Version, File.ReadAllText(Path.Combine(dir, DiskCache.FormatMarkerFileName)).Trim());
        Assert.False(File.Exists(Path.Combine(dir, DiskCache.FormatLockFileName)));
        Assert.Empty(Directory.GetFiles(dir, ".cache-format.writing-*"));
    }

    private static void AssertSurvivors(string dir, Planted planted)
    {
        Assert.True(File.Exists(planted.Ordinary), "non-cache file deleted in " + dir);
        Assert.True(File.Exists(planted.Inside));
        Assert.True(Directory.Exists(planted.Subdirectory));
        Assert.True(File.Exists(planted.Writing), "writer temporary deleted");
        Assert.True(File.Exists(planted.Notes));
    }

    private static void TryDeleteTree(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string Key => new('a', 32);

    private readonly record struct Planted(
        string Ordinary,
        string Inside,
        string Subdirectory,
        string Writing,
        string Notes,
        string Obsolete);
}
