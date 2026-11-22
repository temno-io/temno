using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Temno.Core.Project;

namespace Temno.Core.Export;

/// <summary>What is already at an export destination (WP-398).</summary>
public enum ExportDestinationKind
{
    /// <summary>Nothing is there.</summary>
    Absent,

    /// <summary>A project package: a folder named <c>*.temno</c> or <c>*.palmier</c> that holds <c>project.json</c>.</summary>
    Package,

    /// <summary>Any other folder, including one named like a package that holds no <c>project.json</c>.</summary>
    Folder,

    /// <summary>A file.</summary>
    File,

    /// <summary>A junction or a symbolic link to a folder, whether or not its target exists.</summary>
    FolderLink,

    /// <summary>A symbolic link to a file, whether or not its target exists.</summary>
    FileLink,
}

/// <summary>
/// Where an export may write (WP-398). A package export creates a new package or replaces an
/// existing project package. It never replaces any other folder, a file or a link, so a destination
/// that names a folder cannot make the export erase that folder.
/// </summary>
public static class ExportDestination
{
    /// <summary>
    /// Classifies what is at <paramref name="path"/>. A link at the path itself is reported as a link
    /// and never followed; trailing separators are ignored. Errors other than "not found" propagate,
    /// so a destination that cannot be inspected is never written.
    /// </summary>
    public static ExportDestinationKind Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = Path.TrimEndingDirectorySeparator(path);

        FileAttributes attributes;
        try
        {
            attributes = System.IO.File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return ExportDestinationKind.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return ExportDestinationKind.Absent;
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if ((attributes & FileAttributes.ReparsePoint) != 0 && IsLink(path, isDirectory))
            return isDirectory ? ExportDestinationKind.FolderLink : ExportDestinationKind.FileLink;
        if (!isDirectory)
            return ExportDestinationKind.File;
        return ProjectConstants.HasPackageExtension(path) && HoldsProjectJson(path)
            ? ExportDestinationKind.Package
            : ExportDestinationKind.Folder;
    }

    /// <summary>
    /// True when anything is at <paramref name="path"/>, a dangling link included, or when the path
    /// cannot be inspected: a name-picking caller then moves on to the next name.
    /// </summary>
    public static bool IsOccupied(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            System.IO.File.GetAttributes(Path.TrimEndingDirectorySeparator(path));
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The package directory a package export writes for <paramref name="requested"/>. When a folder,
    /// or a link to one, is already at the requested path, the destination is that path, which only a
    /// project package can pass <see cref="PackageRefusal"/>. Otherwise it gains the write extension
    /// (WP-193); a file without one is left beside the new package, as on the Mac. Either way the
    /// overwrite decision is taken on the path that is written, not on the one that was asked for.
    /// </summary>
    public static string ResolvePackagePath(string requested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requested);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
        return Inspect(full) is ExportDestinationKind.Folder or ExportDestinationKind.Package or ExportDestinationKind.FolderLink
            ? full
            : ProjectConstants.EnsureWriteExtension(full);
    }

    /// <summary>
    /// The nearest folder above <paramref name="path"/> whose name is a package name (<c>*.temno</c>,
    /// <c>*.palmier</c>), read from the path text and from the resolved path (so a junction or a
    /// substituted drive that leads into one is seen), or null. An export never writes inside such a
    /// folder: a file named <c>project.json</c> written there would make it look like a package.
    /// </summary>
    public static string? EnclosingPackageFolder(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        foreach (var form in new[] { full, Path.TrimEndingDirectorySeparator(PathContainment.Resolve(full)) })
        {
            for (var folder = Path.GetDirectoryName(form); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            {
                if (ProjectConstants.HasPackageExtension(folder))
                    return folder;
            }
        }

        return null;
    }

    /// <summary>
    /// Why a package export may not write <paramref name="destination"/>, or null when it may: when
    /// nothing is there, or when a project package is there and <paramref name="replaceExistingPackage"/>
    /// is true.
    /// </summary>
    public static string? PackageRefusal(string destination, ExportDestinationKind kind, bool replaceExistingPackage)
        => kind switch
        {
            ExportDestinationKind.Absent => null,
            ExportDestinationKind.Package when replaceExistingPackage => null,
            ExportDestinationKind.Package => $"A project package already exists at the export destination: {destination}",
            ExportDestinationKind.Folder =>
                $"The export destination is a folder that is not a Temno project package, so it is never replaced: {destination}. "
                + $"A package export writes a new folder ending in .{ProjectConstants.FileExtension}, or replaces a .{ProjectConstants.FileExtension} "
                + $"or .{ProjectConstants.LegacyFileExtension} package that holds {ProjectConstants.TimelineFilename}.",
            ExportDestinationKind.File => $"The export destination is a file, not a project package, so it is not replaced: {destination}",
            ExportDestinationKind.FolderLink or ExportDestinationKind.FileLink =>
                $"The export destination is a junction or symbolic link, which an export never replaces: {destination}. Name the package's own folder instead.",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

    /// <summary>True when either path is the other or lies inside it (see <see cref="IsWithin"/>).</summary>
    public static bool Overlaps(string left, string right)
        => IsWithin(left, right) || IsWithin(right, left);

    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="folder"/> or lies inside it, by path text,
    /// by resolved path (junctions, symbolic links, 8.3 names) and by the file system's identity of each
    /// existing folder on the way up. Identity sees aliases neither path form shows, such as a
    /// substituted drive or a loopback share like <c>\\localhost\C$</c>. Identity is Windows only;
    /// elsewhere the two path forms stand alone.
    /// </summary>
    public static bool IsWithin(string folder, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullFolder = Path.GetFullPath(folder);
        var fullPath = Path.GetFullPath(path);
        return TextWithin(fullFolder, fullPath)
            || TextWithin(PathContainment.Resolve(fullFolder), PathContainment.Resolve(fullPath))
            || IdentityWithin(fullFolder, fullPath);
    }

    private static bool TextWithin(string folder, string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        folder = Path.TrimEndingDirectorySeparator(folder);
        path = Path.TrimEndingDirectorySeparator(path);
        return string.Equals(folder, path, comparison)
            || path.StartsWith(folder + Path.DirectorySeparatorChar, comparison);
    }

    private static bool IdentityWithin(string folder, string path)
    {
        if (Identity(folder) is not { } target)
            return false;
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (Identity(current) == target)
                return true;
        }

        return false;
    }

    private static bool HoldsProjectJson(string folder)
    {
        // A package's project.json is a JSON object; a file that merely has the name (an XML export
        // written there, say) does not make a folder a package.
        var file = Path.Combine(folder, ProjectConstants.TimelineFilename);
        if (!System.IO.File.Exists(file))
            return false;
        try
        {
            using var stream = System.IO.File.OpenRead(file);
            using var document = System.Text.Json.JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool IsLink(string path, bool isDirectory)
    {
        FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
        return info.LinkTarget is not null;
    }

    private static (uint Volume, ulong Index)? Identity(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        const int FileReadAttributes = 0x80;
        const int FileFlagBackupSemantics = 0x02000000;
        using var handle = CreateFile(
            path,
            FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
            return null;
        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        // Some redirectors report no index; treat that as unknown rather than as one shared folder.
        if (index is 0 or ulong.MaxValue)
            return null;
        return (info.VolumeSerialNumber, index);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        int dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        int dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);
}
