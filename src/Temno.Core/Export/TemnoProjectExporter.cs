using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;

namespace Temno.Core.Export;

/// <summary>
/// Writes a self-contained package (reads <c>.palmier</c>, writes <c>.temno</c>): every resolvable media reference is copied
/// into the new bundle's <c>media/</c> directory and rewritten as a project-relative source.
/// Mac: <c>Export/TemnoProjectExporter.swift</c> (WP-112).
/// </summary>
/// <remarks>
/// WP-399: a file outside the package is copied only when the session's resolver vouches for it
/// (<see cref="MediaResolver.ResolvePathForTransfer"/>): the user allowed that path for this project and it
/// still leads where it did. Without a resolver no external file is copied. A project cannot bundle a
/// file of the user's that the user never chose.
/// </remarks>
public static class TemnoProjectExporter
{
    public const string ChatDirectoryName = "chat";

    public sealed class Report
    {
        /// <summary>Entry ids that were external and are now bundled.</summary>
        public List<string> Collected { get; } = [];

        /// <summary>Already-internal media files copied across.</summary>
        public int CopiedInternal { get; set; }

        public List<MissingEntry> Missing { get; } = [];

        /// <summary>
        /// WP-399: external entries left out because Temno may not use them. They stay in the new
        /// manifest as the same dangling external reference, like <see cref="Missing"/>.
        /// </summary>
        public List<MissingEntry> NotAllowed { get; } = [];

        public long TotalBytes { get; set; }

        public IReadOnlyList<string> Warnings
        {
            get
            {
                var warnings = new List<string>(2);
                if (Missing.Count > 0)
                {
                    var files = Missing.Count == 1 ? "media file was" : "media files were";
                    warnings.Add($"{Missing.Count} {files} missing and could not be included.");
                }
                if (NotAllowed.Count > 0)
                {
                    var files = NotAllowed.Count == 1 ? "file outside the project was" : "files outside the project were";
                    warnings.Add($"{NotAllowed.Count} {files} not included because Temno has not been allowed to use them.");
                }
                return warnings;
            }
        }
    }

    public sealed class MissingEntry
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
    }

    /// <param name="destPackageDirectory">Destination package directory path (<c>*.temno</c>).</param>
    /// <param name="replaceExistingPackage">
    /// WP-398: whether a project package already at the destination may be replaced. Nothing else
    /// that is there (another folder, a file or a link) is ever replaced, whatever this says. Checked
    /// before staging and again just before the swap.
    /// </param>
    /// <param name="resolver">
    /// WP-399: the open session's resolver. It decides whether an external entry may be copied; with
    /// none, external entries are reported as <see cref="Report.NotAllowed"/> and nothing outside the
    /// package is read.
    /// </param>
    public static Report Export(
        ProjectFile projectFile,
        MediaManifest manifest,
        string? sourcePackageDirectory,
        string destPackageDirectory,
        byte[]? generationLogBytes = null,
        Action<double>? progress = null,
        bool replaceExistingPackage = false,
        MediaResolver? resolver = null)
    {
        ArgumentNullException.ThrowIfNull(projectFile);
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(destPackageDirectory))
            throw new ArgumentException("Destination package path is required.", nameof(destPackageDirectory));

        destPackageDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destPackageDirectory));
        if (sourcePackageDirectory is not null)
            sourcePackageDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePackageDirectory));

        if (sourcePackageDirectory is not null
            && PathsOverlap(sourcePackageDirectory, destPackageDirectory))
        {
            throw new InvalidOperationException(
                "Self-contained export destination must not overlap the open package path.");
        }

        RequireWritableDestination(destPackageDirectory, replaceExistingPackage);

        var parent = Path.GetDirectoryName(destPackageDirectory)
            ?? throw new InvalidOperationException("Destination has no parent directory.");
        Directory.CreateDirectory(parent);

        var staging = Path.Combine(parent, $".temno-export-{Guid.NewGuid():N}.partial");
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        var mediaDir = Path.Combine(staging, ProjectConstants.MediaDirectoryName);
        Directory.CreateDirectory(mediaDir);

        try
        {
            var report = new Report();
            var newEntries = new List<MediaManifestEntry>();
            var relativeBySource = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var total = Math.Max(1, manifest.Entries.Count);

            for (var index = 0; index < manifest.Entries.Count; index++)
            {
                var entry = manifest.Entries[index];
                progress?.Invoke((index + 1) / (double)total);

                if (entry.Source is MediaSource.ExternalSource
                    && (resolver is null || resolver.ExternalState(entry) != ExternalMediaState.Allowed))
                {
                    report.NotAllowed.Add(new MissingEntry { Id = entry.Id, Name = entry.Name });
                    newEntries.Add(CloneEntry(entry)); // keep the reference, never the bytes
                    continue;
                }

                var srcPath = ResolveSourcePath(entry, sourcePackageDirectory, resolver);
                if (srcPath is null || !File.Exists(srcPath))
                {
                    report.Missing.Add(new MissingEntry { Id = entry.Id, Name = entry.Name });
                    newEntries.Add(CloneEntry(entry)); // keep dangling reference
                    continue;
                }

                var key = Path.GetFullPath(srcPath);
                string relativePath;
                if (relativeBySource.TryGetValue(key, out var existing))
                {
                    relativePath = existing;
                }
                else
                {
                    var preferred = PreferredFileName(entry, srcPath);
                    var destFile = UniquePath(mediaDir, preferred);
                    File.Copy(srcPath, destFile, overwrite: false);
                    relativePath = $"{ProjectConstants.MediaDirectoryName}/{Path.GetFileName(destFile)}";
                    relativeBySource[key] = relativePath;
                    report.TotalBytes += new FileInfo(destFile).Length;
                    if (entry.Source is MediaSource.ProjectSource)
                        report.CopiedInternal++;
                }

                if (entry.Source is MediaSource.ExternalSource)
                    report.Collected.Add(entry.Id);

                var rewritten = CloneEntry(entry);
                rewritten.Source = MediaSource.Project(relativePath);
                newEntries.Add(rewritten);
            }

            var newManifest = new MediaManifest
            {
                Version = manifest.Version,
                Entries = newEntries,
                Folders = manifest.Folders.Select(CloneFolder).ToList(),
            };

            File.WriteAllBytes(
                Path.Combine(staging, ProjectConstants.TimelineFilename),
                projectFile.Encode());
            File.WriteAllBytes(
                Path.Combine(staging, ProjectConstants.ManifestFilename),
                newManifest.Encode());

            if (generationLogBytes is { Length: > 0 })
            {
                File.WriteAllBytes(
                    Path.Combine(staging, ProjectConstants.GenerationLogFilename),
                    generationLogBytes);
            }

            if (sourcePackageDirectory is not null)
            {
                CopyIfPresent(sourcePackageDirectory, staging, ProjectConstants.ThumbnailFilename);
                CopyDirectoryIfPresent(sourcePackageDirectory, staging, ChatDirectoryName);
            }

            // Copying media can take minutes; what is at the destination now is what the swap replaces.
            RequireWritableDestination(destPackageDirectory, replaceExistingPackage);
            PackageDirectorySwap.Replace(staging, destPackageDirectory, ".temno-export-backup-");
            return report;
        }
        catch
        {
            try
            {
                if (Directory.Exists(staging))
                    Directory.Delete(staging, recursive: true);
            }
            catch
            {
                // best effort
            }
            throw;
        }
    }

    private static string? ResolveSourcePath(MediaManifestEntry entry, string? projectUrl, MediaResolver? resolver) => entry.Source switch
    {
        MediaSource.ExternalSource => resolver?.ResolvePathForTransfer(entry),
        MediaSource.ProjectSource p when !string.IsNullOrEmpty(projectUrl) =>
            MediaResolver.ResolveProjectPath(projectUrl, p.RelativePath),
        _ => null,
    };

    private static void RequireWritableDestination(string destination, bool replaceExistingPackage)
    {
        var kind = ExportDestination.Inspect(destination);
        if (ExportDestination.PackageRefusal(destination, kind, replaceExistingPackage) is { } refusal)
            throw new InvalidOperationException(refusal);
    }

    /// <summary>
    /// By path text, by resolved path (links, 8.3 names) and by folder identity (WP-398: a loopback
    /// share names the open package in a way neither path form shows).
    /// </summary>
    private static bool PathsOverlap(string left, string right) => ExportDestination.Overlaps(left, right);

    private static string PreferredFileName(MediaManifestEntry entry, string sourcePath)
    {
        if (entry.Source is MediaSource.ProjectSource)
            return Path.GetFileName(sourcePath);

        var ext = Path.GetExtension(sourcePath);
        var baseName = "import-" + (entry.Id.Length >= 8 ? entry.Id[..8] : entry.Id);
        return string.IsNullOrEmpty(ext) ? baseName : baseName + ext;
    }

    private static string UniquePath(string dir, string preferredName)
    {
        var candidate = Path.Combine(dir, preferredName);
        if (!File.Exists(candidate)) return candidate;
        var baseName = Path.GetFileNameWithoutExtension(preferredName);
        var ext = Path.GetExtension(preferredName);
        for (var n = 1; ; n++)
        {
            var name = $"{baseName}-{n}{ext}";
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) return path;
        }
    }

    private static void CopyIfPresent(string sourceRoot, string destRoot, string fileName)
    {
        var src = Path.Combine(sourceRoot, fileName);
        if (!File.Exists(src)) return;
        RejectReparsePoint(src);
        File.Copy(src, Path.Combine(destRoot, fileName), overwrite: true);
    }

    private static void CopyDirectoryIfPresent(string sourceRoot, string destRoot, string dirName)
    {
        var src = Path.Combine(sourceRoot, dirName);
        if (!Directory.Exists(src)) return;
        var dest = Path.Combine(destRoot, dirName);
        CopyTree(src, dest);
    }

    private static void CopyTree(string source, string dest)
    {
        RejectReparsePoint(source);
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            RejectReparsePoint(file);
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyTree(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new NotSupportedException($"Self-contained export does not follow reparse points: {path}");
    }

    private static MediaManifestEntry CloneEntry(MediaManifestEntry e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Type = e.Type,
        Source = e.Source,
        Duration = e.Duration,
        GenerationInput = e.GenerationInput,
        SourceWidth = e.SourceWidth,
        SourceHeight = e.SourceHeight,
        SourceFps = e.SourceFps,
        HasAudio = e.HasAudio,
        FolderId = e.FolderId,
        CachedRemoteUrl = e.CachedRemoteUrl,
        CachedRemoteUrlExpiresAt = e.CachedRemoteUrlExpiresAt,
        GenerationStatus = e.GenerationStatus,
        ImportInput = e.ImportInput,
    };

    private static MediaFolder CloneFolder(MediaFolder f) => new()
    {
        Id = f.Id,
        Name = f.Name,
        ParentFolderId = f.ParentFolderId,
    };
}
