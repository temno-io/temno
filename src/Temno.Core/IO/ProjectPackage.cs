using Temno.Core.Agent;
using Temno.Core.Models;
using Temno.Core.Project;

namespace Temno.Core.IO;

public sealed class ProjectPackageContents
{
    public required ProjectFile ProjectFile { get; init; }
    public MediaManifest? Manifest { get; init; }
    public byte[]? RawManifestBytes { get; init; }
    public bool ManifestUnreadable { get; init; }
    public byte[]? GenerationLogBytes { get; init; }
}

public static class ProjectPackage
{
    public static ProjectPackageContents Read(string packageDirectory)
    {
        if (!Directory.Exists(packageDirectory))
            throw new DirectoryNotFoundException(packageDirectory);

        var timelinePath = Path.Combine(packageDirectory, ProjectConstants.TimelineFilename);
        if (!File.Exists(timelinePath))
            throw new FileNotFoundException($"Missing {ProjectConstants.TimelineFilename}", timelinePath);

        var projectBytes = File.ReadAllBytes(timelinePath);
        var projectFile = ProjectFile.Decode(projectBytes);

        MediaManifest? manifest = null;
        byte[]? rawManifest = null;
        var manifestUnreadable = false;
        var manifestPath = Path.Combine(packageDirectory, ProjectConstants.ManifestFilename);
        if (File.Exists(manifestPath))
        {
            rawManifest = File.ReadAllBytes(manifestPath);
            try
            {
                manifest = MediaManifest.Decode(rawManifest);
            }
            catch
            {
                manifest = null;
                manifestUnreadable = true;
            }
        }

        byte[]? genLog = null;
        var genPath = Path.Combine(packageDirectory, ProjectConstants.GenerationLogFilename);
        if (File.Exists(genPath))
            genLog = File.ReadAllBytes(genPath);

        return new ProjectPackageContents
        {
            ProjectFile = projectFile,
            Manifest = manifest,
            RawManifestBytes = rawManifest,
            ManifestUnreadable = manifestUnreadable,
            GenerationLogBytes = genLog,
        };
    }

    /// <summary>Write a new package, Save As copy, or in-place metadata update.</summary>
    /// <param name="chatSessions">
    /// A non-null value retains the legacy whole-directory replacement path. Do not use it while
    /// media inside the destination package is open; normal ProjectSession save/autosave passes null.
    /// </param>
    public static void Write(
        string packageDirectory,
        ProjectFile projectFile,
        MediaManifest? manifest,
        byte[]? generationLogBytes = null,
        bool preserveUnreadableManifest = false,
        byte[]? rawManifestBytes = null,
        string? sourcePackageDirectory = null,
        IReadOnlyList<ChatSession>? chatSessions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentNullException.ThrowIfNull(projectFile);

        var dest = NormalizePackagePath(packageDirectory);
        var source = sourcePackageDirectory is null ? null : NormalizePackagePath(sourcePackageDirectory);
        var saveAs = source is not null && !SamePackage(source, dest);
        if (!saveAs && Directory.Exists(dest) && chatSessions is null)
        {
            WriteInPlace(
                dest,
                projectFile,
                manifest,
                generationLogBytes,
                preserveUnreadableManifest,
                rawManifestBytes);
            return;
        }

        var staging = Stage(
            dest,
            projectFile,
            manifest,
            generationLogBytes,
            preserveUnreadableManifest,
            rawManifestBytes,
            source,
            chatSessions);
        try
        {
            Commit(staging, dest);
        }
        catch
        {
            PackageDirectorySwap.TryDelete(staging);
            throw;
        }
    }

    private static void WriteInPlace(
        string packageDirectory,
        ProjectFile projectFile,
        MediaManifest? manifest,
        byte[]? generationLogBytes,
        bool preserveUnreadableManifest,
        byte[]? rawManifestBytes)
    {
        var parent = Path.GetDirectoryName(packageDirectory)
            ?? throw new InvalidOperationException("Package path has no parent directory.");
        Directory.CreateDirectory(Path.Combine(packageDirectory, ProjectConstants.MediaDirectoryName));
        var staging = Path.Combine(parent, $".temno-save-{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(staging);
        var changedEntries = new List<string>();

        try
        {
            ReplaceFile(
                Path.Combine(staging, ProjectConstants.TimelineFilename),
                projectFile.Encode());

            if (preserveUnreadableManifest && rawManifestBytes is not null)
            {
                ReplaceFile(
                    Path.Combine(staging, ProjectConstants.ManifestFilename),
                    rawManifestBytes);
                changedEntries.Add(ProjectConstants.ManifestFilename);
            }
            else if (manifest is not null)
            {
                ReplaceFile(
                    Path.Combine(staging, ProjectConstants.ManifestFilename),
                    manifest.Encode());
                changedEntries.Add(ProjectConstants.ManifestFilename);
            }

            if (generationLogBytes is not null)
            {
                ReplaceFile(
                    Path.Combine(staging, ProjectConstants.GenerationLogFilename),
                    generationLogBytes);
                changedEntries.Add(ProjectConstants.GenerationLogFilename);
            }

            // project.json is the final commit marker. For additive imports, publishing it last
            // prevents clips from appearing before their manifest entries; removals still require
            // a journal to be power-loss atomic.
            changedEntries.Add(ProjectConstants.TimelineFilename);

            PackageDirectorySwap.ReplaceEntries(
                staging,
                packageDirectory,
                changedEntries,
                ".temno-save-rollback-");
        }
        catch
        {
            PackageDirectorySwap.TryDelete(staging);
            throw;
        }
    }

    /// <summary>
    /// Writes modelled JSON into a sibling staging directory. The live package is not replaced
    /// until <see cref="Commit"/>.
    /// </summary>
    public static string Stage(
        string packageDirectory,
        ProjectFile projectFile,
        MediaManifest? manifest,
        byte[]? generationLogBytes = null,
        bool preserveUnreadableManifest = false,
        byte[]? rawManifestBytes = null,
        string? sourcePackageDirectory = null,
        IReadOnlyList<ChatSession>? chatSessions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        ArgumentNullException.ThrowIfNull(projectFile);

        var dest = NormalizePackagePath(packageDirectory);
        var source = sourcePackageDirectory is null ? null : NormalizePackagePath(sourcePackageDirectory);
        var saveAs = source is not null && !SamePackage(source, dest);

        if (saveAs)
            ValidateSaveAsDestination(source!, dest);

        var parent = Path.GetDirectoryName(dest)
            ?? throw new InvalidOperationException("Package path has no parent directory.");
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".temno-save-{Guid.NewGuid():N}.partial");
        PackageDirectorySwap.TryDelete(staging);
        Directory.CreateDirectory(staging);

        try
        {
            if (saveAs)
                PackageDirectorySwap.CloneContents(source!, staging);
            else if (Directory.Exists(dest))
                PackageDirectorySwap.CloneContents(dest, staging);

            Directory.CreateDirectory(Path.Combine(staging, ProjectConstants.MediaDirectoryName));
            ReplaceFile(
                Path.Combine(staging, ProjectConstants.TimelineFilename),
                projectFile.Encode());

            var manifestPath = Path.Combine(staging, ProjectConstants.ManifestFilename);
            if (preserveUnreadableManifest && rawManifestBytes is not null)
                ReplaceFile(manifestPath, rawManifestBytes);
            else if (manifest is not null)
                ReplaceFile(manifestPath, manifest.Encode());

            if (generationLogBytes is not null)
            {
                ReplaceFile(
                    Path.Combine(staging, ProjectConstants.GenerationLogFilename),
                    generationLogBytes);
            }

            if (chatSessions is not null)
                WriteChatDirectory(staging, chatSessions);

            return staging;
        }
        catch
        {
            PackageDirectorySwap.TryDelete(staging);
            throw;
        }
    }

    public static void Commit(string stagingDirectory, string packageDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageDirectory);
        PackageDirectorySwap.Replace(
            NormalizePackagePath(stagingDirectory),
            NormalizePackagePath(packageDirectory),
            ".temno-save-backup-");
    }

    private static void ValidateSaveAsDestination(string source, string destination)
    {
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException(source);
        if (IsNestedPath(destination, source))
            throw new IOException("Save As destination cannot be inside the source package.");
        if (File.Exists(destination))
            throw new IOException($"Save As destination is a file: {destination}");

        if (Directory.Exists(destination) &&
            Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new IOException($"Save As destination is not empty: {destination}");
        }

        ValidateCopySource(source);
    }

    private static void WriteChatDirectory(string stagingDirectory, IReadOnlyList<ChatSession> sessions)
    {
        var chatDir = Path.Combine(stagingDirectory, ProjectConstants.ChatDirectoryName);
        if (Directory.Exists(chatDir))
            Directory.Delete(chatDir, recursive: true);
        Directory.CreateDirectory(chatDir);
        foreach (var session in sessions)
        {
            ReplaceFile(
                Path.Combine(chatDir, ChatSessionStore.FileName(session)),
                ChatSessionStore.Encode(session));
        }
    }

    /// <summary>
    /// Same-volume clones use hard links. Deleting the staging name first so a rewrite
    /// does not mutate the live package through a shared inode.
    /// </summary>
    private static void ReplaceFile(string path, byte[] bytes)
    {
        if (File.Exists(path))
            File.Delete(path);
        File.WriteAllBytes(path, bytes);
    }

    private static void ValidateCopySource(string sourceDirectory)
    {
        foreach (var sourceEntry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            var attributes = File.GetAttributes(sourceEntry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException($"Package Save As does not follow reparse points: {sourceEntry}");
            if ((attributes & FileAttributes.Directory) != 0)
                ValidateCopySource(sourceEntry);
        }
    }

    private static bool SamePackage(string left, string right) =>
        string.Equals(
            NormalizePackagePath(left),
            NormalizePackagePath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsNestedPath(string candidate, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var parentWithSeparator = parent.EndsWith(Path.DirectorySeparatorChar)
            || parent.EndsWith(Path.AltDirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(parentWithSeparator, comparison);
    }

    private static string NormalizePackagePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
