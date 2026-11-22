using System.Runtime.InteropServices;

namespace Temno.Core.IO;

/// <summary>
/// Publish a staged package. New packages and exports use directory replacement; in-place saves
/// replace only changed metadata files so open media handles do not move.
/// </summary>
public static class PackageDirectorySwap
{
    private sealed record PublishedEntry(
        string DestinationPath,
        string BackupPath,
        bool HadDestination,
        FileAttributes? AttributesToRestore);

    public static void Replace(string staging, string destination, string backupPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(staging);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPrefix);

        if (!Directory.Exists(destination))
        {
            Directory.Move(staging, destination);
            return;
        }

        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Destination has no parent directory.");
        var backup = Path.Combine(parent, $"{backupPrefix}{Guid.NewGuid():N}");
        Directory.Move(destination, backup);
        try
        {
            Directory.Move(staging, destination);
        }
        catch
        {
            if (!Directory.Exists(destination) && Directory.Exists(backup))
                Directory.Move(backup, destination);
            throw;
        }

        try { Directory.Delete(backup, recursive: true); }
        catch { /* replacement succeeded; stale backup is safer than failing the publish */ }
    }

    public static void CloneContents(string sourceDirectory, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        CopyTree(sourceDirectory, destinationDirectory);
    }

    /// <summary>
    /// Publish only the staged metadata files that changed while keeping the live package directory
    /// in place. Replacements are atomic and earlier files are rolled back if a later file fails.
    /// </summary>
    public static void ReplaceEntries(
        string staging,
        string destination,
        IReadOnlyList<string> relativePaths,
        string rollbackPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(staging);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(relativePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(rollbackPrefix);

        if (!Directory.Exists(staging))
            throw new DirectoryNotFoundException(staging);
        if (!Directory.Exists(destination))
            throw new DirectoryNotFoundException(destination);

        var parent = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("Destination has no parent directory.");
        var rollback = Path.Combine(parent, $"{rollbackPrefix}{Guid.NewGuid():N}.partial");
        Directory.CreateDirectory(rollback);
        var published = new List<PublishedEntry>(relativePaths.Count);

        try
        {
            foreach (var relativePath in relativePaths)
            {
                var stagedPath = ResolveContainedPath(staging, relativePath);
                var destinationPath = ResolveContainedPath(destination, relativePath);
                var backupPath = ResolveContainedPath(rollback, relativePath);
                if (!File.Exists(stagedPath))
                    throw new FileNotFoundException($"Staged package entry is missing: {relativePath}", stagedPath);

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);

                if (Directory.Exists(destinationPath))
                    throw new IOException($"Cannot replace directory with file: {destinationPath}");

                var hadDestination = File.Exists(destinationPath);
                FileAttributes? attributesToRestore = null;
                if (hadDestination)
                {
                    var destinationAttributes = File.GetAttributes(destinationPath);
                    if ((destinationAttributes & FileAttributes.ReparsePoint) != 0)
                        throw new NotSupportedException($"Package publish does not replace reparse points: {destinationPath}");

                    if ((destinationAttributes & FileAttributes.ReadOnly) != 0)
                    {
                        attributesToRestore = destinationAttributes;
                        File.SetAttributes(
                            destinationPath,
                            destinationAttributes & ~FileAttributes.ReadOnly);
                    }

                    try
                    {
                        File.Replace(stagedPath, destinationPath, backupPath, ignoreMetadataErrors: true);
                    }
                    catch
                    {
                        TryRestoreAttributes(destinationPath, attributesToRestore);
                        throw;
                    }
                }
                else
                    File.Move(stagedPath, destinationPath);
                published.Add(new PublishedEntry(
                    destinationPath,
                    backupPath,
                    hadDestination,
                    attributesToRestore));
            }
        }
        catch (Exception publishError)
        {
            var rollbackErrors = RollBack(published);
            if (rollbackErrors.Count > 0)
            {
                rollbackErrors.Insert(0, publishError);
                throw new IOException(
                    $"Package metadata publish failed and could not be fully rolled back. Recovery files remain at '{rollback}'.",
                    new AggregateException(rollbackErrors));
            }

            TryDelete(rollback);
            throw;
        }

        TryDelete(rollback);
        TryDelete(staging);
    }

    public static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private static void CopyTree(string sourceDirectory, string destinationDirectory)
    {
        foreach (var sourceEntry in Directory.EnumerateFileSystemEntries(sourceDirectory))
        {
            var attributes = File.GetAttributes(sourceEntry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new NotSupportedException($"Package clone does not follow reparse points: {sourceEntry}");

            var destinationEntry = Path.Combine(destinationDirectory, Path.GetFileName(sourceEntry));
            if ((attributes & FileAttributes.Directory) != 0)
            {
                Directory.CreateDirectory(destinationEntry);
                CopyTree(sourceEntry, destinationEntry);
            }
            else
            {
                CloneFile(sourceEntry, destinationEntry);
            }
        }
    }

    private static void CloneFile(string source, string destination)
    {
        if (TryCreateHardLink(destination, source))
            return;
        File.Copy(source, destination, overwrite: false);
    }

    private static List<Exception> RollBack(IReadOnlyList<PublishedEntry> published)
    {
        var errors = new List<Exception>();
        for (var index = published.Count - 1; index >= 0; index--)
        {
            var entry = published[index];
            try
            {
                if (entry.HadDestination)
                {
                    if (File.Exists(entry.DestinationPath))
                    {
                        File.Replace(
                            entry.BackupPath,
                            entry.DestinationPath,
                            destinationBackupFileName: null,
                            ignoreMetadataErrors: true);
                        TryRestoreAttributes(entry.DestinationPath, entry.AttributesToRestore);
                    }
                    else
                    {
                        File.Move(entry.BackupPath, entry.DestinationPath);
                        TryRestoreAttributes(entry.DestinationPath, entry.AttributesToRestore);
                    }
                }
                else if (File.Exists(entry.DestinationPath))
                {
                    File.Delete(entry.DestinationPath);
                }
            }
            catch (Exception error)
            {
                errors.Add(error);
            }
        }

        return errors;
    }

    private static void TryRestoreAttributes(string path, FileAttributes? attributes)
    {
        if (!attributes.HasValue || !File.Exists(path))
            return;

        try { File.SetAttributes(path, attributes.Value); }
        catch { /* content and the causal publish error are more important than attribute fidelity */ }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
            throw new IOException($"Package entry path must be relative: {relativePath}");

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootWithSeparator, comparison))
            throw new IOException($"Package entry escapes its root: {relativePath}");

        return fullPath;
    }

    private static bool TryCreateHardLink(string newPath, string existingPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return NativeMethods.CreateHardLink(newPath, existingPath, IntPtr.Zero);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return NativeMethods.Link(existingPath, newPath) == 0;
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        [DllImport("libc", SetLastError = true, EntryPoint = "link")]
        public static extern int Link(string oldpath, string newpath);
    }
}
