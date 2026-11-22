using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Temno.Media.Cache;

/// <summary>
/// A single cache directory with keyed get-or-create, LRU eviction by budget, and size/clear helpers.
/// Mac: Utilities/DiskCache.swift — plus the cross-cutting write convention (temp ".writing-{uuid}" then
/// rename; losing a rename race to an existing final file is success). The Mac app has no automatic
/// eviction; the byte budget + LRU prune is a Windows addition per WP-25.
///
/// <para><b>WP-179 — a hit is pinned, not merely lucky.</b> Before this package a hit from
/// <see cref="TryGet"/> bumped the file's mtime and returned a path; a concurrent
/// <see cref="Evict"/> that had already snapshotted the directory could still select and delete that
/// file in the window before the caller opened it. Nothing stopped it but the OS refusing to delete
/// an open file, which is a Windows accident rather than a design. Eviction now consults
/// <see cref="CachePins"/> immediately before each delete: a file with a live lease
/// (<see cref="Rent"/>) is never taken, and a file handed out by <see cref="TryGet"/> or
/// <see cref="GetOrCreateAsync"/> is protected for <see cref="HandoutGrace"/> so the caller has an
/// unhurried window to open it. Pins are keyed by full path and are process-wide, because two
/// <see cref="DiskCache"/> instances over the same directory are normal (the timeline and the media
/// panel each build their own <see cref="ThumbnailCache"/>).</para>
///
/// <para><b>WP-179 — the key format is recorded on disk.</b> Cache entries are opaque 32-hex names,
/// so an entry written under an older <see cref="MediaCacheKey.FormatVersion"/> cannot be told from a
/// current one by inspection and would sit unreachable until the byte budget happened to prune it.
/// Each directory therefore carries a <see cref="FormatMarkerFileName"/> stamp; the first use of a
/// directory whose stamp does not match sweeps it once and rewrites the stamp. The marker is excluded
/// from the byte budget and from LRU so eviction cannot cause a re-sweep.</para>
///
/// <para><b>WP-239 — the pin and the directory are the same path the filesystem will report.</b>
/// A root with forward slashes or a relative path made <c>Path.Combine</c> disagree with
/// <c>FileInfo.FullName</c>, so the pin table missed and eviction deleted a live handout. Every pin
/// and every directory-identity check goes through <see cref="CanonicalPinPath"/> /
/// <see cref="CanonicalDirectoryPath"/> first. The format sweep deletes only a finalized 32-hex entry:
/// an unrelated file, a subdirectory and an in-flight <c>.writing-</c> temporary are not cache entries
/// and are left alone. The stamp is published by rename while an exclusive lock is held, so a second
/// instance either waits or sees the finished stamp.</para>
/// </summary>
public sealed class DiskCache
{
    public const long DefaultMaxTotalBytes = 2L * 1024 * 1024 * 1024; // 2 GiB

    /// <summary>Records which <see cref="MediaCacheKey.FormatVersion"/> wrote this directory.</summary>
    public const string FormatMarkerFileName = ".cache-format";

    /// <summary>
    /// Exclusive lock held only while the stamp is read, swept and published. Deleted when the
    /// holder closes it, so a crashed initializer does not leave a lock behind.
    /// </summary>
    public const string FormatLockFileName = ".cache-format.lock";

    /// <summary>
    /// How long a path handed out by <see cref="TryGet"/> / <see cref="GetOrCreateAsync"/> is held
    /// against eviction. Generous on purpose: the window that has to be covered is a lookup followed
    /// by an ffmpeg spawn or a bitmap decode, and the cost of over-covering is bounded — a handed-out
    /// file has just been touched, so LRU would pick it last anyway.
    /// </summary>
    public static readonly TimeSpan DefaultHandoutGrace = TimeSpan.FromSeconds(30);

    private const string TempMarker = ".writing-";
    private const int FormatLockWaitMs = 10_000;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private static readonly object FormatGate = new();
    private static readonly HashSet<string> FormatEnsuredDirectories = new(PathComparer);

    private readonly long _handoutGraceMs;
    private readonly string _keyFormatVersion;
    private bool _formatEnsured;

    /// <param name="handoutGrace">
    /// Override for <see cref="DefaultHandoutGrace"/>. <see cref="TimeSpan.Zero"/> disables the
    /// handout pin entirely, which is what a test that wants to observe raw LRU ordering wants;
    /// explicit <see cref="Rent"/> leases are unaffected.
    /// </param>
    /// <param name="keyFormatVersion">
    /// Override for <see cref="MediaCacheKey.FormatVersion"/>, so the migration sweep can be tested
    /// without inventing a second key implementation.
    /// </param>
    public DiskCache(
        string directoryPath,
        long maxTotalBytes = DefaultMaxTotalBytes,
        TimeSpan? handoutGrace = null,
        string? keyFormatVersion = null)
    {
        if (string.IsNullOrEmpty(directoryPath))
        {
            throw new ArgumentException("Directory path cannot be null or empty.", nameof(directoryPath));
        }

        if (maxTotalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTotalBytes), "Byte budget must be positive.");
        }

        var grace = handoutGrace ?? DefaultHandoutGrace;
        if (grace < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(handoutGrace), "Handout grace cannot be negative.");
        }

        DirectoryPath = directoryPath;
        MaxTotalBytes = maxTotalBytes;
        _handoutGraceMs = (long)grace.TotalMilliseconds;
        _keyFormatVersion = keyFormatVersion ?? MediaCacheKey.FormatVersion;
    }

    public string DirectoryPath { get; }

    public long MaxTotalBytes { get; }

    /// <summary>How long a handed-out path is protected from eviction.</summary>
    public TimeSpan HandoutGrace => TimeSpan.FromMilliseconds(_handoutGraceMs);

    /// <summary>Deterministic final path for a key; does not touch the filesystem.</summary>
    public string PathForKey(string key, string extension)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw new ArgumentException("Key cannot be null or empty.", nameof(key));
        }

        return Path.Combine(DirectoryPath, key + NormalizeExtension(extension));
    }

    /// <summary>
    /// Creates the directory and applies the key-format sweep, so a caller that is about to write a
    /// file at <see cref="PathForKey"/> itself cannot have it swept out from under it.
    /// </summary>
    public void EnsureDirectory() => EnsureFormat();

    /// <summary>
    /// Returns true and bumps the LRU timestamp when the cached file exists. The returned path is
    /// pinned against eviction for <see cref="HandoutGrace"/>; take <see cref="Rent"/> instead when
    /// the caller holds the file for longer than that.
    /// </summary>
    public bool TryGet(string key, string extension, out string path)
    {
        EnsureFormat();
        path = PathForKey(key, extension);
        if (!File.Exists(path))
        {
            return false;
        }

        Touch(path);
        CachePins.Grace(path, _handoutGraceMs);
        return true;
    }

    /// <summary>
    /// A hit, held. Eviction skips the file for as long as the lease is undisposed — the
    /// <c>SequentialFrameSource.Pipe.UseCount</c> pattern, applied to the cache directory. Returns
    /// null on a miss.
    /// </summary>
    public CacheLease? Rent(string key, string extension)
    {
        EnsureFormat();
        var path = PathForKey(key, extension);

        // Pin before the existence check: the reverse order leaves the same window this closes.
        var lease = new CacheLease(path);
        if (!File.Exists(path))
        {
            lease.Dispose();
            return null;
        }

        Touch(path);
        return lease;
    }

    /// <summary>
    /// Returns the cached file, or runs <paramref name="generator"/> (given a temp path to write) and
    /// promotes the result. Concurrent creators for the same key are safe: each writes its own temp
    /// file and the first rename wins; losers discard their temp and use the winner's file. The
    /// returned path is pinned for <see cref="HandoutGrace"/>.
    /// </summary>
    public async Task<string> GetOrCreateAsync(
        string key,
        string extension,
        Func<string, CancellationToken, Task> generator,
        CancellationToken cancellationToken = default)
    {
        var lease = await GetOrCreateCoreAsync(key, extension, generator, grace: true, cancellationToken)
            .ConfigureAwait(false);
        return lease.Path;
    }

    /// <summary>Get-or-create, returning the entry held against eviction until the lease is disposed.</summary>
    public async Task<CacheLease> GetOrCreateLeaseAsync(
        string key,
        string extension,
        Func<string, CancellationToken, Task> generator,
        CancellationToken cancellationToken = default)
    {
        return await GetOrCreateCoreAsync(key, extension, generator, grace: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Total bytes currently held (top-level entries only; races tolerated).</summary>
    public long CurrentSizeBytes()
    {
        long total = 0;
        foreach (var file in EnumerateFilesSafe())
        {
            try
            {
                total += file.Length;
            }
            catch (IOException)
            {
            }
        }

        return total;
    }

    /// <summary>Prunes least-recently-used files until the directory fits the byte budget.</summary>
    public int Evict() => EvictCore(protectedPath: null);

    /// <summary>
    /// Deletes every cached file (Mac Settings "Clear cache" analogue). Failures are tolerated.
    /// Deliberately ignores pins: this is the owner asking for the cache to go, not the cache
    /// deciding to prune itself. The format marker is kept so the next use does not re-sweep.
    /// </summary>
    public void Clear()
    {
        foreach (var file in EnumerateFilesSafe())
        {
            TryDelete(file.FullName);
        }
    }

    /// <summary>
    /// WP-178: Settings Clear Cache. Walks a whole tree, deletes per file, and continues past
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> so one locked proxy
    /// cannot abort the sweep or a later root. Empty directories are removed afterwards; a root
    /// that still holds leftovers is left in place.
    /// </summary>
    public static CacheTreeClearResult ClearTree(string root)
    {
        if (string.IsNullOrEmpty(root))
        {
            throw new ArgumentException("Root cannot be null or empty.", nameof(root));
        }

        if (!Directory.Exists(root))
        {
            return CacheTreeClearResult.Missing;
        }

        var files = new List<string>();
        var directories = new List<string>();
        CollectTree(root, files, directories);

        long bytesFreed = 0;
        var removed = 0;
        var skipped = 0;
        foreach (var file in files)
        {
            long length = 0;
            try
            {
                length = new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (TryDeleteFile(file))
            {
                bytesFreed += length;
                removed++;
            }
            else if (File.Exists(file))
            {
                skipped++;
            }
        }

        foreach (var directory in directories
            .Where(d => !string.Equals(d, root, PathComparison))
            .OrderByDescending(d => d.Length))
        {
            TryDeleteDirectory(directory);
        }

        var rootRemoved = TryDeleteDirectory(root);
        return new CacheTreeClearResult(bytesFreed, removed, skipped, RootExisted: true, rootRemoved);
    }

    private async Task<CacheLease> GetOrCreateCoreAsync(
        string key,
        string extension,
        Func<string, CancellationToken, Task> generator,
        bool grace,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(generator);

        EnsureFormat();
        var finalPath = PathForKey(key, extension);
        if (File.Exists(finalPath))
        {
            Touch(finalPath);
            return Hold(finalPath, grace);
        }

        Directory.CreateDirectory(DirectoryPath);

        // Temp keeps the real extension last so extension-sniffing tools (ffmpeg) infer the format.
        var tempPath = Path.Combine(
            DirectoryPath,
            key + TempMarker + Guid.NewGuid().ToString("N") + NormalizeExtension(extension));
        try
        {
            await generator(tempPath, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(tempPath))
            {
                throw new InvalidOperationException($"Cache generator did not produce a file at '{tempPath}'.");
            }

            try
            {
                File.Move(tempPath, finalPath);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                // Lost the race to a concurrent creator; its file is equivalent.
            }
        }
        finally
        {
            TryDelete(tempPath);
        }

        // Held before the prune, so the entry this call just produced is protected by the same rule
        // that protects every other hit rather than by the protectedPath special case alone.
        var held = Hold(finalPath, grace);
        EvictCore(protectedPath: finalPath);
        return held;
    }

    private CacheLease Hold(string path, bool grace)
        => grace ? CacheLease.Graced(path, _handoutGraceMs) : new CacheLease(path);

    private int EvictCore(string? protectedPath)
    {
        var files = EnumerateFilesSafe().ToList();
        long total = 0;
        foreach (var file in files)
        {
            try
            {
                total += file.Length;
            }
            catch (IOException)
            {
            }
        }

        if (total <= MaxTotalBytes)
        {
            return 0;
        }

        var removed = 0;
        var protectedKey = protectedPath is null ? null : CanonicalPinPath(protectedPath);
        foreach (var file in files.OrderBy(SafeLastWriteUtc))
        {
            if (total <= MaxTotalBytes)
            {
                break;
            }

            if (protectedKey != null &&
                PathComparer.Equals(CanonicalPinPath(file.FullName), protectedKey))
            {
                continue;
            }

            // WP-179: the pin is checked here, not when the list above was snapshotted, so a hit
            // handed out after that snapshot is still safe.
            if (CachePins.IsPinned(file.FullName))
            {
                continue;
            }

            try
            {
                var length = file.Length;
                file.Delete();
                total -= length;
                removed++;
            }
            catch (IOException)
            {
                // In use by another process/reader; skip it this round.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    /// <summary>
    /// One-shot per directory per process: stamp the key-format version, sweeping first when the
    /// stamp is absent or stale. Entries written under an older format are unreachable — their names
    /// are opaque hashes of a seed nothing computes any more — so the finalized 32-hex names are swept
    /// rather than left to age out. Anything that is not that shape (a user's stray file, a
    /// subdirectory, a <c>.writing-</c> temporary, this lock) is not a cache entry and stays.
    /// The sweep and the stamp publish share an exclusive lock, and the stamp becomes visible by
    /// rename, so a second instance waits and then sees either the old stamp or the finished one.
    /// </summary>
    private void EnsureFormat()
    {
        var deadline = Environment.TickCount64 + FormatLockWaitMs;
        while (!TryEnsureFormatOnce())
        {
            if (Environment.TickCount64 >= deadline)
            {
                // The other holder is stuck. Leave this instance unstamped so the next call retries
                // instead of treating the directory as already decided.
                return;
            }

            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// One attempt. False means another instance holds the format lock; nothing was swept and the
    /// directory is not recorded as ensured. Tests use this so a held lock is observed without waiting.
    /// </summary>
    internal bool TryEnsureFormatOnce()
    {
        if (_formatEnsured)
        {
            return true;
        }

        var full = CanonicalDirectoryPath(DirectoryPath);
        lock (FormatGate)
        {
            if (FormatEnsuredDirectories.Contains(full))
            {
                _formatEnsured = true;
                return true;
            }
        }

        if (!TryApplyFormat())
        {
            return false;
        }

        lock (FormatGate)
        {
            FormatEnsuredDirectories.Add(full);
        }

        _formatEnsured = true;
        return true;
    }

    /// <summary>
    /// Test seam. The next <see cref="DiskCache"/> over this directory re-reads the stamp instead of
    /// trusting the process-local set.
    /// </summary>
    internal static void ForgetEnsuredDirectory(string directoryPath)
    {
        lock (FormatGate)
        {
            FormatEnsuredDirectories.Remove(CanonicalDirectoryPath(directoryPath));
        }
    }

    /// <summary>Test seam. Holds the same exclusive lock <see cref="EnsureFormat"/> takes.</summary>
    internal static FileStream? TryAcquireFormatLock(string directoryPath)
    {
        var full = CanonicalDirectoryPath(directoryPath);
        Directory.CreateDirectory(full);
        return TryOpenFormatLock(Path.Combine(full, FormatLockFileName));
    }

    private static FileStream? TryOpenFormatLock(string lockPath)
    {
        try
        {
            return new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
        }
        catch (Exception ex) when (ex is (IOException or UnauthorizedAccessException) && IsSharingViolation(ex))
        {
            return null;
        }
    }

    private static bool IsSharingViolation(Exception ex)
    {
        var code = ex.HResult & 0xFFFF;
        return code is ErrorSharingViolation or ErrorLockViolation;
    }

    private bool TryApplyFormat()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }

        FileStream? gate;
        try
        {
            gate = TryOpenFormatLock(Path.Combine(CanonicalDirectoryPath(DirectoryPath), FormatLockFileName));
        }
        catch (IOException)
        {
            ApplyFormatBody();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            ApplyFormatBody();
            return true;
        }

        if (gate is null)
        {
            return false;
        }

        try
        {
            ApplyFormatBody();
            return true;
        }
        finally
        {
            gate.Dispose();
        }
    }

    private void ApplyFormatBody()
    {
        var marker = Path.Combine(DirectoryPath, FormatMarkerFileName);
        try
        {
            string? recorded = null;
            var unreadable = false;
            try
            {
                if (File.Exists(marker))
                {
                    recorded = File.ReadAllText(marker).Trim();
                }
            }
            catch (IOException)
            {
                unreadable = true;
            }
            catch (UnauthorizedAccessException)
            {
                unreadable = true;
            }

            if (unreadable)
            {
                // A stamp that is present but cannot be read is not evidence of a stale format.
                // Sweeping on a transient read failure would throw away an entirely current cache;
                // declining costs at worst a re-check in the next process.
                return;
            }

            if (string.Equals(recorded, _keyFormatVersion, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var file in EnumerateFilesSafe().ToList())
            {
                if (!IsObsoleteCacheEntryName(file.Name))
                {
                    continue;
                }

                if (CachePins.IsPinned(file.FullName))
                {
                    continue;
                }

                TryDelete(file.FullName);
            }

            PublishMarker(marker);
        }
        catch (IOException)
        {
            // A cache we cannot stamp still works; it just re-checks on the next process.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void PublishMarker(string markerPath)
    {
        // Rename, not WriteAllText: a reader never observes a half-written stamp. A crash between
        // the write and the rename leaves a .writing- temporary, which the sweep does not own.
        var temp = markerPath + TempMarker + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, _keyFormatVersion);
            File.Move(temp, markerPath, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// A finalized cache entry: 32 hex digits, optional single alphanumeric extension. Writer
    /// temporaries carry <c>.writing-</c> and are never this, even when the key in front is 32 hex.
    /// </summary>
    internal static bool IsObsoleteCacheEntryName(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) ||
            fileName.Contains(TempMarker, StringComparison.Ordinal) ||
            string.Equals(fileName, FormatMarkerFileName, PathComparison) ||
            string.Equals(fileName, FormatLockFileName, PathComparison))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length != 32)
        {
            return false;
        }

        foreach (var c in stem)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        var extension = Path.GetExtension(fileName);
        if (extension.Length == 0)
        {
            return true;
        }

        if (extension.Length == 1)
        {
            return false;
        }

        for (var i = 1; i < extension.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(extension[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Full path, separators normalized, no trailing separator except a root.</summary>
    internal static string CanonicalPinPath(string path) => TrimTrailingSeparators(FullPathOrSelf(path));

    /// <summary>Same identity <see cref="CanonicalPinPath"/> uses, for the directory the cache owns.</summary>
    internal static string CanonicalDirectoryPath(string path) => CanonicalPinPath(path);

    private IEnumerable<FileInfo> EnumerateFilesSafe()
    {
        var directory = new DirectoryInfo(DirectoryPath);
        if (!directory.Exists)
        {
            return [];
        }

        try
        {
            // The format marker and the format lock are bookkeeping, not cache entries. Both sit
            // outside the byte budget and outside LRU, so pruning cannot delete the marker and
            // cause a spurious re-sweep, or delete a lock another instance still holds.
            return directory.EnumerateFiles()
                .Where(f =>
                    !string.Equals(f.Name, FormatMarkerFileName, PathComparison) &&
                    !string.Equals(f.Name, FormatLockFileName, PathComparison));
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static DateTime SafeLastWriteUtc(FileInfo file)
    {
        try
        {
            return file.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }

    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
            // A concurrent reader/writer holds the file; recency bump is best-effort.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void CollectTree(string directory, List<string> files, List<string> directories)
    {
        directories.Add(directory);

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                files.Add(file);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        try
        {
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                CollectTree(child, files, directories);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FullPathOrSelf(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static string TrimTrailingSeparators(string full)
    {
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || string.Equals(full, root, PathComparison))
        {
            return full;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void TryDelete(string path) => TryDeleteFile(path);

    private static bool TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return !File.Exists(path);
        }
        catch (IOException)
        {
            return !File.Exists(path);
        }
        catch (UnauthorizedAccessException)
        {
            return !File.Exists(path);
        }
    }

    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: false);
            }

            return !Directory.Exists(path);
        }
        catch (IOException)
        {
            return !Directory.Exists(path);
        }
        catch (UnauthorizedAccessException)
        {
            return !Directory.Exists(path);
        }
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrEmpty(extension))
        {
            return string.Empty;
        }

        return extension[0] == '.' ? extension : "." + extension;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}

/// <summary>
/// A hold on one cached file. While it is undisposed, <see cref="DiskCache.Evict"/> will not select
/// the file, whatever the byte budget says and whatever the OS would have allowed. Dispose it as soon
/// as the file is open or copied; a lease that is never disposed keeps one entry out of LRU for the
/// life of the process.
/// </summary>
public sealed class CacheLease : IDisposable
{
    private int _released;

    internal CacheLease(string path)
    {
        Path = path;
        CachePins.Acquire(path);
    }

    private CacheLease(string path, bool _)
    {
        Path = path;
        _released = 1;
    }

    /// <summary>The cached file.</summary>
    public string Path { get; }

    /// <summary>
    /// A path protected only by the handout grace: nothing to release, so disposing is a no-op. Used
    /// by the path-returning APIs, whose callers have no place to put a using.
    /// </summary>
    internal static CacheLease Graced(string path, long graceMs)
    {
        CachePins.Grace(path, graceMs);
        return new CacheLease(path, false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            CachePins.Release(Path);
        }
    }
}

/// <summary>
/// Process-wide table of cache files that must not be evicted, keyed by canonical full path. Two
/// <see cref="DiskCache"/> instances over one directory are normal, so the table cannot live on the
/// instance: the timeline and the media panel each construct their own <see cref="ThumbnailCache"/>
/// over <c>MediaCachePaths.Default</c>, and one must not prune what the other just handed out.
/// </summary>
internal static class CachePins
{
    private const int PruneAboveCount = 1024;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Pin> Entries = new(DiskCache.PathComparer);

    public static void Acquire(string path)
    {
        lock (Gate)
        {
            GetOrAdd(Key(path)).Leases++;
        }
    }

    public static void Release(string path)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(Key(path), out var pin))
            {
                return;
            }

            if (pin.Leases > 0)
            {
                pin.Leases--;
            }

            if (IsDead(pin))
            {
                Entries.Remove(Key(path));
            }
        }
    }

    public static void Grace(string path, long graceMs)
    {
        if (graceMs <= 0)
        {
            return;
        }

        var until = Environment.TickCount64 + graceMs;
        lock (Gate)
        {
            var pin = GetOrAdd(Key(path));
            if (until > pin.GraceUntilMs)
            {
                pin.GraceUntilMs = until;
            }

            if (Entries.Count > PruneAboveCount)
            {
                Prune();
            }
        }
    }

    public static bool IsPinned(string path)
    {
        lock (Gate)
        {
            if (!Entries.TryGetValue(Key(path), out var pin))
            {
                return false;
            }

            if (!IsDead(pin))
            {
                return true;
            }

            Entries.Remove(Key(path));
            return false;
        }
    }

    private static string Key(string path) => DiskCache.CanonicalPinPath(path);

    private static Pin GetOrAdd(string path)
    {
        if (!Entries.TryGetValue(path, out var pin))
        {
            pin = new Pin();
            Entries[path] = pin;
        }

        return pin;
    }

    private static void Prune()
    {
        var dead = Entries.Where(e => IsDead(e.Value)).Select(e => e.Key).ToList();
        foreach (var key in dead)
        {
            Entries.Remove(key);
        }
    }

    private static bool IsDead(Pin pin) => pin.Leases <= 0 && pin.GraceUntilMs <= Environment.TickCount64;

    private sealed class Pin
    {
        public int Leases;
        public long GraceUntilMs;
    }
}

/// <summary>Outcome of <see cref="DiskCache.ClearTree"/> — bytes gone, leftovers still in use.</summary>
public readonly record struct CacheTreeClearResult(
    long BytesFreed,
    int FilesRemoved,
    int FilesSkipped,
    bool RootExisted,
    bool RootRemoved)
{
    public static CacheTreeClearResult Missing { get; } = new(0, 0, 0, RootExisted: false, RootRemoved: false);

    public static CacheTreeClearResult Combine(CacheTreeClearResult a, CacheTreeClearResult b)
        => new(
            a.BytesFreed + b.BytesFreed,
            a.FilesRemoved + b.FilesRemoved,
            a.FilesSkipped + b.FilesSkipped,
            a.RootExisted || b.RootExisted,
            a.RootRemoved && b.RootRemoved);

    /// <summary>Owner-facing Settings status. Size is included whenever anything was actually freed.</summary>
    public string Describe()
    {
        if (!RootExisted)
        {
            return "No cache folders found.";
        }

        var leftover = FilesSkipped switch
        {
            0 => "",
            1 => "1 file still in use and was left.",
            _ => $"{FilesSkipped} files still in use and were left.",
        };

        if (FilesSkipped == 0)
        {
            return BytesFreed == 0
                ? "Cleared thumbnails, proxies, and visual-search cache. They rebuild on next use."
                : $"Freed {FormatByteSize(BytesFreed)} of thumbnails, proxies, and visual-search cache. They rebuild on next use.";
        }

        if (FilesRemoved == 0)
        {
            return leftover + " Nothing was removed.";
        }

        return $"Freed {FormatByteSize(BytesFreed)}. {leftover}";
    }

    internal static string FormatByteSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        var kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return $"{kb:0.#} KB";
        }

        var mb = kb / 1024.0;
        if (mb < 1024)
        {
            return $"{mb:0.#} MB";
        }

        return $"{mb / 1024.0:0.#} GB";
    }
}
