using System.Text.Json;
using System.Text.Json.Serialization;

namespace Temno.Core.IO;

/// <summary>
/// One file outside a project package that the user let Temno use for that project (WP-399).
/// <see cref="Path"/> is the full path as the user chose it; <see cref="Target"/> is where that path led
/// when they chose it, after every link and junction (<see cref="Project.PathContainment.Resolve"/>).
/// </summary>
public sealed record ExternalMediaGrant(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("target")] string Target);

/// <summary>
/// The user's own record of which external files each project may use (WP-399). It lives in the
/// per-user data root (<c>%LOCALAPPDATA%\Temno\external-media-grants.json</c>, or <c>Temno-Dev</c>),
/// never in the package: a project cannot vouch for the files it names. Keyed by the package's full
/// path, so a project moved outside Temno asks again.
/// </summary>
public static class ExternalMediaGrants
{
    public const string FileName = "external-media-grants.json";

    private const int CurrentVersion = 1;
    private static readonly object Gate = new();
    private static string? _pathOverride;
    private static string? _defaultPathOverride;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static StringComparer PathComparer { get; } =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Per-test file. <c>null</c> falls back to <see cref="SetDefaultPathForTests"/>.</summary>
    public static void SetPathForTests(string? path)
    {
        lock (Gate)
            _pathOverride = path;
    }

    /// <summary>Process-wide test file, installed by the test assembly so no test touches the user's real file.</summary>
    public static void SetDefaultPathForTests(string? path)
    {
        lock (Gate)
            _defaultPathOverride = path;
    }

    public static string FilePath
    {
        get
        {
            lock (Gate)
                return _pathOverride ?? _defaultPathOverride
                    ?? System.IO.Path.Combine(UserDataRoots.LocalAppDataRoot, FileName);
        }
    }

    /// <summary>
    /// The package key: its full path without a trailing separator. Lexical on purpose: no disk access,
    /// so a project on a dead share still opens at once.
    /// </summary>
    public static string? PackageKey(string? packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
            return null;
        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(packagePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The grants recorded for a package. An unreadable or damaged file reads as no grants: the
    /// project's external files are then blocked until the user allows them again (fail closed).
    /// </summary>
    public static IReadOnlyList<ExternalMediaGrant> For(string? packagePath)
    {
        var key = PackageKey(packagePath);
        if (key is null)
            return [];
        lock (Gate)
        {
            Document document;
            try
            {
                document = Read(FilePath, out _);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
            var package = document.Packages.FirstOrDefault(p => PathComparer.Equals(p.Package, key));
            return package?.Files.Where(IsWellFormed).ToList() ?? [];
        }
    }

    /// <summary>
    /// Records grants for a package, replacing any earlier grant for the same path. Throws
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> when the file cannot be
    /// written; callers keep the grant in memory for the session and say so.
    /// </summary>
    public static void Add(string? packagePath, IEnumerable<ExternalMediaGrant> grants)
    {
        var key = PackageKey(packagePath);
        if (key is null)
            return;
        var incoming = grants.Where(IsWellFormed).ToList();
        if (incoming.Count == 0)
            return;

        lock (Gate)
        {
            var file = FilePath;
            using var crossProcess = CrossProcessLock(file);
            // An unreadable file throws here rather than being replaced with only this grant; a damaged
            // one is kept beside the new file, not destroyed.
            var document = Read(file, out var damaged);
            if (damaged)
                File.Move(file, file + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), overwrite: false);
            var package = document.Packages.FirstOrDefault(p => PathComparer.Equals(p.Package, key));
            if (package is null)
            {
                package = new PackageRecord { Package = key };
                document.Packages.Add(package);
            }

            foreach (var grant in incoming)
            {
                package.Files.RemoveAll(g => PathComparer.Equals(g.Path, grant.Path));
                package.Files.Add(grant);
            }

            document.Version = CurrentVersion;
            Write(file, document);
        }
    }

    /// <summary>
    /// Makes a package's grants exactly these, none included (a save to a new place). Same write rules as
    /// <see cref="Add"/>.
    /// </summary>
    public static void Replace(string? packagePath, IEnumerable<ExternalMediaGrant> grants)
    {
        var key = PackageKey(packagePath);
        if (key is null)
            return;
        var files = grants.Where(IsWellFormed).ToList();

        lock (Gate)
        {
            var file = FilePath;
            using var crossProcess = CrossProcessLock(file);
            var document = Read(file, out var damaged);
            var had = document.Packages.RemoveAll(p => PathComparer.Equals(p.Package, key)) > 0;
            if (files.Count == 0 && !had && !damaged)
                return; // nothing recorded and nothing to clear: no file is created
            if (damaged)
                File.Move(file, file + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"), overwrite: false);
            if (files.Count > 0)
                document.Packages.Add(new PackageRecord { Package = key, Files = files });
            document.Version = CurrentVersion;
            Write(file, document);
        }
    }

    // A target may be empty: the path was allowed while nothing was there, and its first sight records it.
    private static bool IsWellFormed(ExternalMediaGrant? grant)
        => grant is not null && !string.IsNullOrWhiteSpace(grant.Path);

    /// <summary>
    /// Reads the file. Missing reads as empty. Damaged JSON, or a version newer than this build, reads as
    /// empty with <paramref name="damaged"/> set. An I/O failure throws.
    /// </summary>
    private static Document Read(string file, out bool damaged)
    {
        damaged = false;
        if (!File.Exists(file))
            return new Document();
        var bytes = File.ReadAllBytes(file);
        try
        {
            var document = JsonSerializer.Deserialize<Document>(bytes, Json);
            if (document is null || document.Version > CurrentVersion)
            {
                damaged = true;
                return new Document();
            }
            document.Packages = document.Packages?.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Package)).ToList() ?? [];
            foreach (var package in document.Packages)
                package.Files = package.Files?.Where(IsWellFormed).ToList() ?? [];
            return document;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            damaged = true;
            return new Document();
        }
    }

    /// <summary>The app and an MCP-driven probe can both write; serialize them per file.</summary>
    private static IDisposable CrossProcessLock(string file)
    {
        var name = @"Local\Temno.ExternalMediaGrants." + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(file.ToUpperInvariant())))[..32];
        var mutex = new Mutex(initiallyOwned: false, name);
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(10)))
                throw new IOException("Timed out waiting for another Temno process to finish writing " + FileName + ".");
        }
        catch (AbandonedMutexException)
        {
            // The previous holder exited mid-write; the atomic replace means the file is whole.
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
        return new Releaser(mutex);
    }

    private sealed class Releaser(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    private static void Write(string file, Document document)
    {
        var directory = System.IO.Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, Json));
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private sealed class Document
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = CurrentVersion;

        [JsonPropertyName("packages")]
        public List<PackageRecord> Packages { get; set; } = [];
    }

    private sealed class PackageRecord
    {
        [JsonPropertyName("package")]
        public string Package { get; set; } = "";

        [JsonPropertyName("files")]
        public List<ExternalMediaGrant> Files { get; set; } = [];
    }
}
