using Temno.Core.Models;

namespace Temno.Core.IO;

/// <summary>Mac: Models/MediaResolver.swift</summary>
/// <remarks>
/// WP-399: an <c>external.absolutePath</c> resolves only when the session's
/// <see cref="ExternalMediaAccess"/> allows it, so a project cannot nominate a file outside its package
/// for preview, render, an agent read, an upload or a self-contained copy. A resolver built without one
/// (a manifest an exporter or a test assembled in memory) resolves external paths as before.
/// </remarks>
public sealed class MediaResolver
{
    private readonly Func<MediaManifest> _manifest;
    private readonly Func<string?> _projectUrl;
    private readonly ExternalMediaAccess? _externalAccess;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ExternalMediaState>? _transferStates;

    public MediaResolver(Func<MediaManifest> manifest, Func<string?> projectUrl, ExternalMediaAccess? externalAccess = null)
        : this(manifest, projectUrl, externalAccess, transfer: false)
    {
    }

    private MediaResolver(Func<MediaManifest> manifest, Func<string?> projectUrl, ExternalMediaAccess? externalAccess, bool transfer)
    {
        _manifest = manifest;
        _projectUrl = projectUrl;
        _externalAccess = externalAccess;
        // Unrestricted sessions too: they check the paths they hold a grant for (a Link import, a relink).
        if (transfer && externalAccess is not null)
            _transferStates = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// A resolver for one agent or MCP call whose results leave the editor (rendered frames, transcripts,
    /// captions sent for transcription): its <see cref="ResolvePath(MediaManifestEntry)"/> applies the
    /// transfer check, once per external path for the life of the returned object (WP-399). Make a new
    /// one per call: a link swapped in after it answered is caught by the next call.
    /// </summary>
    public MediaResolver ForTransfer() => new(_manifest, _projectUrl, _externalAccess, transfer: true);

    public MediaManifestEntry? Entry(string assetId)
        => _manifest().Entries.FirstOrDefault(e => e.Id == assetId);

    public string? ResolvePath(string assetId)
    {
        var entry = Entry(assetId);
        if (entry is null) return null;
        return ResolvePath(entry);
    }

    /// <summary>
    /// The file an entry plays from, or null. Never touches the disk for an external entry: the
    /// renderer asks per frame per clip. Use <see cref="ResolvePathForTransfer"/> where bytes leave the editor.
    /// </summary>
    public string? ResolvePath(MediaManifestEntry entry)
    {
        return entry.Source switch
        {
            MediaSource.ExternalSource e when _transferStates is not null =>
                _transferStates.GetOrAdd(e.AbsolutePath ?? "", static (path, access) => access.StateForTransfer(path), _externalAccess!)
                    == ExternalMediaState.Allowed ? e.AbsolutePath : null,
            MediaSource.ExternalSource e => _externalAccess is null || _externalAccess.IsAllowed(e.AbsolutePath)
                ? e.AbsolutePath
                : null,
            MediaSource.ProjectSource p => JoinProject(p.RelativePath),
            _ => null,
        };
    }

    /// <summary>
    /// Where an entry says its file is, for writers that record the location as text and never read the
    /// file: FCPXML and XMEML. An external path is returned whether or not the session may use it, and is
    /// never touched.
    /// </summary>
    public string? ReferencePath(MediaManifestEntry entry)
        => entry.Source switch
        {
            MediaSource.ExternalSource e => e.AbsolutePath,
            MediaSource.ProjectSource p => JoinProject(p.RelativePath),
            _ => null,
        };

    public string? ReferencePath(string assetId)
        => Entry(assetId) is { } entry ? ReferencePath(entry) : null;

    /// <summary>
    /// Whether a bare absolute path with no manifest entry (an exporter's fallback for a clip whose
    /// <c>mediaRef</c> is a path) may be read. In a restricted session that path came from the project,
    /// so it needs a grant like any external entry (WP-399). Path text only.
    /// </summary>
    public bool AllowsLooseFile(string path)
        => _externalAccess is null
           || (_transferStates is not null
               ? _transferStates.GetOrAdd(path, static (p, access) => access.StateForTransfer(p), _externalAccess) == ExternalMediaState.Allowed
               : _externalAccess.IsAllowed(path));

    /// <summary>An external entry this session may not use. Path text only (WP-399).</summary>
    public bool IsBlocked(MediaManifestEntry entry)
        => entry.Source is MediaSource.ExternalSource e
           && _externalAccess is not null
           && !_externalAccess.IsAllowed(e.AbsolutePath);

    /// <summary>
    /// <see cref="ResolvePath(MediaManifestEntry)"/> for a read that hands the bytes on: to an agent or
    /// MCP caller, a provider upload, a self-contained copy. An external path is resolved again and must
    /// still lead where it led when the user allowed it (WP-399).
    /// </summary>
    public string? ResolvePathForTransfer(MediaManifestEntry entry)
    {
        return entry.Source switch
        {
            MediaSource.ExternalSource e => ExternalState(entry) == ExternalMediaState.Allowed ? e.AbsolutePath : null,
            MediaSource.ProjectSource p => JoinProject(p.RelativePath),
            _ => null,
        };
    }

    /// <summary>
    /// Whether the session may hand on an external entry's bytes; <see cref="ExternalMediaState.Allowed"/>
    /// for a project-relative entry, whose containment <see cref="ResolveProjectPath"/> decides.
    /// </summary>
    public ExternalMediaState ExternalState(MediaManifestEntry entry)
        => entry.Source is MediaSource.ExternalSource e && _externalAccess is not null
            ? _externalAccess.StateForTransfer(e.AbsolutePath)
            : ExternalMediaState.Allowed;

    public bool Exists(string assetId)
    {
        var path = ResolvePath(assetId);
        return path is not null && File.Exists(path);
    }

    private string? JoinProject(string relativePath)
    {
        var root = _projectUrl();
        if (string.IsNullOrEmpty(root)) return null;
        return ResolveProjectPath(root, relativePath);
    }

    public static string? ResolveProjectPath(string packageRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(packageRoot) || string.IsNullOrWhiteSpace(relativePath))
            return null;
        if (Path.IsPathRooted(relativePath))
            return null;

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packageRoot));
            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(root, normalized));
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var rootPrefix = root + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootPrefix, comparison))
                return null;
            return ContainsReparsePoint(root, candidate) ? null : candidate;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                     or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ContainsReparsePoint(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        var current = root;
        foreach (var part in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!File.Exists(current) && !Directory.Exists(current))
                continue;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
        }
        return false;
    }
}
