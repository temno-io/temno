using System.Collections.Concurrent;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;

namespace Temno.Core.IO;

/// <summary>What a session may do with one external media path (WP-399).</summary>
public enum ExternalMediaState
{
    /// <summary>The session may use the path.</summary>
    Allowed,

    /// <summary>The project named the path and the user has not allowed it.</summary>
    NotAllowed,

    /// <summary>
    /// The user allowed the path, but it now leads somewhere else: a link or junction was put in its
    /// place, or one of its folders now points elsewhere. Blocked for transfers until allowed again.
    /// </summary>
    TargetChanged,
}

/// <summary>Why the editor's review list shows a blocked row the way it does (WP-399).</summary>
public enum ExternalMediaConcern
{
    /// <summary>A local path whose extension matches the entry's media type.</summary>
    None,

    /// <summary>A UNC path or a mapped network drive. Allowable, with a warning.</summary>
    NetworkLocation,

    /// <summary>Not a full path (a Mac <c>/Users/…</c> path read on Windows). Relink it instead.</summary>
    NotAFullPath,

    /// <summary>A device namespace (<c>\\?\</c>, <c>\\.\</c>) or an alternate data stream. Never allowed.</summary>
    DevicePath,

    /// <summary>
    /// The file's extension is not one Temno imports as the entry's media type: no Link import or relink
    /// writes such an entry. Relink it instead.
    /// </summary>
    TypeMismatch,

    /// <summary>
    /// The user allowed this path, and a transfer check has since found it leading somewhere else
    /// (<see cref="BlockedExternalMedia.NowLeadsTo"/>). Allowing it again records the new target.
    /// </summary>
    TargetChanged,
}

/// <summary>
/// An external entry the session will not use, for the editor's review list. <see cref="Name"/> is the
/// project's own label and can say anything; show <see cref="Path"/> and <see cref="FileName"/>.
/// </summary>
public sealed record BlockedExternalMedia(
    string MediaRef,
    string Name,
    ClipType Type,
    string Path,
    string FileName,
    ExternalMediaConcern Concern,
    string? NowLeadsTo = null)
{
    public bool CanAllow => Concern is ExternalMediaConcern.None or ExternalMediaConcern.NetworkLocation
        or ExternalMediaConcern.TargetChanged;
}

/// <summary>
/// The external-file policy of one <see cref="ProjectSession"/> (WP-399). A session loaded from data
/// Temno did not write in this process (a package on disk, a saved template) is
/// <see cref="IsRestricted">restricted</see>: an <c>external.absolutePath</c> it names resolves only once
/// the user has allowed that path for that package. A session built in memory is unrestricted: every
/// entry in it was put there by code in this process, and the product's only writers of external
/// sources (Link import and relink) record a grant anyway.
/// </summary>
/// <remarks>
/// Two checks. <see cref="IsAllowed"/> is the hot path (the renderer asks per frame per clip): path
/// text only, no disk access, so neither a dead share nor a host a project names is ever contacted
/// for an entry the user has not allowed. <see cref="StateForTransfer"/> runs where bytes leave the
/// editor (an agent read, a provider upload, a self-contained copy): for an allowed path only, it
/// resolves the path again and compares where it leads now with where it led when the user allowed it.
/// </remarks>
public sealed class ExternalMediaAccess
{
    private readonly Func<string?> _packagePath;
    private readonly object _gate = new();
    private volatile Snapshot _snapshot;
    // Allowed paths a transfer check found leading elsewhere, with where they lead now.
    private readonly ConcurrentDictionary<string, string> _changed = new(ExternalMediaGrants.PathComparer);

    /// <summary>
    /// Raised when a transfer check finds an allowed path leading somewhere new, or leading back where
    /// it did, so the editor can list it for review. Raised on the checking thread; handlers must not block.
    /// </summary>
    public event EventHandler? StatesChanged;

    private ExternalMediaAccess(bool restricted, Func<string?> packagePath, IEnumerable<ExternalMediaGrant> grants)
    {
        IsRestricted = restricted;
        _packagePath = packagePath;
        var map = new Dictionary<string, ExternalMediaGrant>(ExternalMediaGrants.PathComparer);
        foreach (var grant in grants)
        {
            if (Normalize(grant.Path) is { } key)
                map[key] = grant with { Path = key };
        }
        _snapshot = new Snapshot(map);
    }

    public static ExternalMediaAccess Unrestricted(Func<string?> packagePath)
        => new(restricted: false, packagePath, []);

    public static ExternalMediaAccess Restricted(Func<string?> packagePath, IEnumerable<ExternalMediaGrant> grants)
        => new(restricted: true, packagePath, grants);

    public bool IsRestricted { get; }

    /// <summary>Grants made or loaded for this session.</summary>
    public IReadOnlyList<ExternalMediaGrant> Grants => _snapshot.Grants.Values.ToList();

    /// <summary>Path text only; no disk access. Unrestricted sessions allow everything.</summary>
    public bool IsAllowed(string? absolutePath)
    {
        if (!IsRestricted)
            return true;
        if (absolutePath is null)
            return false;
        var snapshot = _snapshot;
        return snapshot.Lexical.GetOrAdd(absolutePath, static (path, grants) =>
            Normalize(path) is { } key && grants.ContainsKey(key), snapshot.Grants);
    }

    /// <summary>
    /// Resolves an allowed path again and checks it still leads where it did when the user allowed it.
    /// A path that is not allowed is answered from its text alone, without touching it.
    /// </summary>
    /// <remarks>
    /// An unrestricted session allows a path it holds no grant for, but checks one it does (a Link import
    /// or relink in a new project), so a link swapped in later is caught there too.
    /// </remarks>
    public ExternalMediaState StateForTransfer(string? absolutePath)
    {
        var key = Normalize(absolutePath);
        if (key is null || !_snapshot.Grants.TryGetValue(key, out var grant))
            return IsRestricted ? ExternalMediaState.NotAllowed : ExternalMediaState.Allowed;

        if (string.IsNullOrEmpty(grant.Target))
        {
            // Allowed while nothing was there (an unplugged drive): the first sight of the file records
            // where it leads. Until then there is nothing to read.
            if (!File.Exists(key) || TryResolve(key) is not { } first)
                return ExternalMediaState.Allowed;
            Record([grant with { Target = first }], persist: true);
            return ExternalMediaState.Allowed;
        }

        var target = TryResolve(key);
        if (target is not null && ExternalMediaGrants.PathComparer.Equals(target, grant.Target))
        {
            if (_changed.TryRemove(key, out _))
                StatesChanged?.Invoke(this, EventArgs.Empty);
            return ExternalMediaState.Allowed;
        }
        var now = target ?? "";
        if (!_changed.TryGetValue(key, out var seen) || !ExternalMediaGrants.PathComparer.Equals(seen, now))
        {
            _changed[key] = now;
            StatesChanged?.Invoke(this, EventArgs.Empty);
        }
        return ExternalMediaState.TargetChanged;
    }

    /// <summary>
    /// A grant for a path the user chose, with where it leads now: for a caller that prepares it off the UI
    /// thread (a Link import) and records it with <see cref="AllowPrepared"/>. Opens the path; call it only
    /// for a path the user or the MCP caller chose. Null when the path can never be allowed.
    /// </summary>
    public static ExternalMediaGrant? CreateGrant(string? absolutePath)
    {
        var key = Normalize(absolutePath);
        if (key is null)
            return null;
        // A path with nothing at it yet records no target; its first sight does (see StateForTransfer).
        var target = File.Exists(key) || Directory.Exists(key) ? TryResolve(key) : null;
        return new ExternalMediaGrant(key, target ?? "");
    }

    /// <summary>
    /// Where a full path leads now, after every link and junction, or null. Opens the path: the editor
    /// calls it only after the user said yes, to show a target that differs from the path.
    /// </summary>
    public static string? ResolveTarget(string? absolutePath)
        => Normalize(absolutePath) is { } key ? TryResolve(key) : null;

    /// <summary>Records grants made by <see cref="CreateGrant"/>. Returns how many were recorded and saved.</summary>
    public int AllowPrepared(IEnumerable<ExternalMediaGrant?> grants)
        => Record(grants.Where(g => g is not null && Normalize(g.Path) is not null)
            .Select(g => g! with { Path = Normalize(g!.Path)! }).ToList(), persist: true);

    /// <summary>
    /// Records that the user chose this path for this project: a Link import, a relink, or the editor's
    /// Allow. Saved for the package at once when the session has one, otherwise on its first save. Only
    /// call this for a path the user (or the MCP caller) chose; never for a path the project named.
    /// Returns false when the path cannot be allowed (<see cref="Normalize"/>) or could not be saved; a
    /// grant that could not be saved still applies for the rest of this session.
    /// </summary>
    public bool Allow(string? absolutePath) => Allow([absolutePath]) == 1;

    /// <summary>Allows several paths with one write. Returns how many were allowed and saved.</summary>
    public int Allow(IEnumerable<string?> absolutePaths)
        => Record(absolutePaths.Select(CreateGrant).Where(g => g is not null).Select(g => g!).ToList(), persist: true);

    private int Record(IReadOnlyList<ExternalMediaGrant> made, bool persist)
    {
        if (made.Count == 0)
            return 0;

        var cleared = false;
        lock (_gate)
        {
            var next = new Dictionary<string, ExternalMediaGrant>(_snapshot.Grants, ExternalMediaGrants.PathComparer);
            foreach (var grant in made)
            {
                next[grant.Path] = grant;
                cleared |= _changed.TryRemove(grant.Path, out _);
            }
            _snapshot = new Snapshot(next);
        }
        if (cleared)
            StatesChanged?.Invoke(this, EventArgs.Empty);

        var package = _packagePath();
        if (!persist || string.IsNullOrWhiteSpace(package))
            return made.Count;
        try
        {
            ExternalMediaGrants.Add(package, made);
            return made.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The external entries this session will not use, in manifest order. Decided from path text and the
    /// drive's type only: nothing a project names is opened or contacted.
    /// </summary>
    public IReadOnlyList<BlockedExternalMedia> Blocked(MediaManifest manifest)
    {
        if (!IsRestricted && _changed.IsEmpty)
            return [];
        var blocked = new List<BlockedExternalMedia>();
        foreach (var entry in manifest.Entries)
        {
            if (entry.Source is not MediaSource.ExternalSource source)
                continue;
            var path = source.AbsolutePath ?? "";
            if (!IsAllowed(path))
            {
                blocked.Add(new BlockedExternalMedia(
                    entry.Id, entry.Name, entry.Type, path, FileNameOf(path), ConcernFor(entry.Type, path)));
            }
            else if (Normalize(path) is { } key && _changed.TryGetValue(key, out var now))
            {
                blocked.Add(new BlockedExternalMedia(
                    entry.Id, entry.Name, entry.Type, path, FileNameOf(path), ExternalMediaConcern.TargetChanged,
                    string.IsNullOrEmpty(now) ? null : now));
            }
        }
        return blocked;
    }

    /// <summary>
    /// A save to a new place (Save As, a new project's first save): the package there gets exactly this
    /// session's grants, none at all included, so a project saved where an older one was deleted does not
    /// inherit that project's grants.
    /// </summary>
    internal void PersistTo(string packagePath)
    {
        try
        {
            ExternalMediaGrants.Replace(packagePath, _snapshot.Grants.Values.ToList());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The grants still apply to this session; the reopened package asks again.
        }
    }

    /// <summary>
    /// A full path, normalized as text, or null when it can never be allowed: not fully qualified (it
    /// would depend on the process's current drive), a device namespace (<c>\\?\</c>, <c>\\.\</c>), or a
    /// colon past the drive letter (an alternate data stream).
    /// </summary>
    public static string? Normalize(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !System.IO.Path.IsPathFullyQualified(absolutePath))
            return null;
        if (IsDeviceNamespace(absolutePath) || absolutePath.IndexOf(':', 2) >= 0)
            return null;
        try
        {
            return System.IO.Path.GetFullPath(absolutePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    internal static ExternalMediaConcern ConcernFor(ClipType type, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path))
            return ExternalMediaConcern.NotAFullPath;
        if (Normalize(path) is null)
            return ExternalMediaConcern.DevicePath;
        if (!MediaLibraryCommands.TryGetTypeFromExtension(System.IO.Path.GetExtension(path), out var byExtension)
            || byExtension != type)
            return ExternalMediaConcern.TypeMismatch;
        return IsNetworkLocation(path) ? ExternalMediaConcern.NetworkLocation : ExternalMediaConcern.None;
    }

    /// <summary>A UNC path, or a drive letter Windows maps to a share. Asks the local drive table only.</summary>
    internal static bool IsNetworkLocation(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return true;
        if (!OperatingSystem.IsWindows() || path.Length < 3 || path[1] != ':')
            return false;
        try
        {
            return new DriveInfo(path[..1]).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsDeviceNamespace(string path)
        => path.Length >= 4
           && (path[0] is '\\' or '/') && (path[1] is '\\' or '/')
           && path[2] is '?' or '.'
           && (path[3] is '\\' or '/');

    private static string FileNameOf(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? path[(cut + 1)..] : path;
    }

    private static string? TryResolve(string fullPath)
    {
        try
        {
            return PathContainment.Resolve(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                     or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Grants plus a memo of the hot path's answers. Replaced whole on every change, so a reader on a
    /// render or MCP thread sees one consistent set while the UI thread allows more.
    /// </summary>
    private sealed class Snapshot(Dictionary<string, ExternalMediaGrant> grants)
    {
        public Dictionary<string, ExternalMediaGrant> Grants { get; } = grants;
        public ConcurrentDictionary<string, bool> Lexical { get; } = new(StringComparer.Ordinal);
    }
}
