using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Temno.Core.Project;

/// <summary>On-disk names must match macOS Palmier Pro (Utilities/Constants.swift enum Project).</summary>
public static class ProjectConstants
{
    /// <summary>Extension this app writes (WP-193: was "palmier"; Mac still writes "palmier").</summary>
    public const string FileExtension = "temno";

    /// <summary>Mac interchange extension. Read forever, never written by this app.</summary>
    public const string LegacyFileExtension = "palmier";
    public const string TimelineFilename = "project.json";
    public const string ManifestFilename = "media.json";
    public const string GenerationLogFilename = "generation-log.json";
    public const string ThumbnailFilename = "thumbnail.jpg";
    public const string MediaDirectoryName = "media";
    public const string ChatDirectoryName = "chat";
    public const string RegistryFilename = "project-registry.json";
    public const string DefaultProjectName = "Untitled Project";
    public const string TypeIdentifier = "io.palmier.project";

    /// <summary>True for either package extension, with or without a leading dot.</summary>
    public static bool IsPackageExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return false;
        var trimmed = extension.Trim().TrimStart('.');
        return trimmed.Equals(FileExtension, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(LegacyFileExtension, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasPackageExtension(string? path) =>
        !string.IsNullOrWhiteSpace(path) && IsPackageExtension(Path.GetExtension(path));

    /// <summary>
    /// Write-side naming (WP-193, strict per owner decision 2026-09-05): every write emits
    /// <see cref="FileExtension"/>, no exceptions. A legacy <c>.palmier</c> suffix is rewritten
    /// to <c>.temno</c>; <c>.palmier</c> is read-only interchange. Anything else without a
    /// package extension gains <see cref="FileExtension"/>.
    /// </summary>
    public static string EnsureWriteExtension(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.EndsWith("." + FileExtension, StringComparison.OrdinalIgnoreCase))
            return name;
        if (name.EndsWith("." + LegacyFileExtension, StringComparison.OrdinalIgnoreCase))
            return Path.ChangeExtension(name, FileExtension);
        return name + "." + FileExtension;
    }

    /// <summary>
    /// GUI/MCP write-path naming: unique package directory under <paramref name="parentDir"/>
    /// for <paramref name="name"/>, always with the write extension (see
    /// <see cref="EnsureWriteExtension"/>). Appends " 2", " 3", … on collision.
    /// Pure path logic; lives here so the WinUI-free suite can cover the Save As path.
    /// </summary>
    public static string AllocateUniquePackagePath(string parentDir, string name)
    {
        var baseName = EnsureWriteExtension(name);
        var candidate = Path.Combine(parentDir, baseName);
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
            return candidate;

        for (var i = 2; i < 10_000; i++)
        {
            var stem = Path.GetFileNameWithoutExtension(baseName);
            var next = Path.Combine(parentDir, $"{stem} {i}.{FileExtension}");
            if (!Directory.Exists(next) && !File.Exists(next))
                return next;
        }

        throw new IOException("Could not allocate a unique project path.");
    }

    /// <summary>
    /// Package directory for a new project named <paramref name="name"/> under
    /// <paramref name="projectsRoot"/>. Rejects rooted paths, separators, traversal,
    /// reserved device names and any candidate whose final path is not inside the root.
    /// </summary>
    public static string ResolveNewPackagePath(string projectsRoot, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectsRoot);
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Project name is required.");
        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal) || name.EndsWith('.'))
            throw new InvalidOperationException($"Project name '{name}' is not a valid file name.");
        if (Path.IsPathRooted(name)
            || name.StartsWith(@"\\", StringComparison.Ordinal)
            || name.StartsWith("//", StringComparison.Ordinal)
            || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
            || name is "." or "..")
        {
            throw new InvalidOperationException(
                $"Project name '{name}' must be a file name inside the projects directory, not a path.");
        }

        var fileName = EnsureWriteExtension(name);
        var invalid = Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(invalid) >= 0 || fileName.IndexOfAny(invalid) >= 0)
            throw new InvalidOperationException(
                $"Project name '{name}' contains characters that are not allowed in a file name.");
        if (IsReservedDeviceName(fileName))
            throw new InvalidOperationException($"Project name '{name}' is a reserved device name.");

        var root = Path.GetFullPath(projectsRoot);
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));
        var parent = Path.GetDirectoryName(candidate);
        if (parent is null || !string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
            || !PathContainment.IsInside(root, candidate))
        {
            throw new InvalidOperationException($"Project name '{name}' escapes the projects directory.");
        }

        return candidate;
    }

    private static bool IsReservedDeviceName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var head = stem.Split('.')[0];
        if (head.Length == 0)
            return true;
        if (head.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || head.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || head.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || head.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || head.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase))
            return true;
        if (head.Length != 4)
            return false;
        if (!head.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            && !head.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            return false;
        return head[3] is >= '0' and <= '9';
    }
}

/// <summary>
/// Proves a path stays inside a root after lexical normalization and reparse-point resolution.
/// </summary>
public static class PathContainment
{
    public static bool IsLexicalChild(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        var prefix = WithSeparator(fullRoot);
        return fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInside(string root, string candidate)
    {
        var resolvedRoot = Resolve(root);
        var resolvedCandidate = Resolve(candidate);
        if (string.Equals(
                TrimSeparator(resolvedRoot),
                TrimSeparator(resolvedCandidate),
                StringComparison.OrdinalIgnoreCase))
            return false;
        return resolvedCandidate.StartsWith(WithSeparator(resolvedRoot), StringComparison.OrdinalIgnoreCase);
    }

    public static string Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        if (TryFinalPath(full) is { } entire)
            return entire;

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return full;

        var parts = full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            if (TryFinalPath(next) is { } resolved)
            {
                current = resolved;
                continue;
            }

            for (var j = i; j < parts.Length; j++)
                current = Path.Combine(current, parts[j]);
            return Path.GetFullPath(current);
        }

        return Path.GetFullPath(current);
    }

    private static string WithSeparator(string path)
        => TrimSeparator(path) + Path.DirectorySeparatorChar;

    private static string TrimSeparator(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? TryFinalPath(string path)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        if (!File.Exists(path) && !Directory.Exists(path))
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
        if (handle.IsInvalid)
            return null;

        var buffer = new char[4096];
        var length = GetFinalPathNameByHandle(handle, buffer, buffer.Length, 0);
        if (length <= 0 || length >= buffer.Length)
            return null;

        var final = new string(buffer, 0, length);
        if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + final[@"\\?\UNC\".Length..];
        if (final.StartsWith(@"\\?\", StringComparison.Ordinal))
            return final[@"\\?\".Length..];
        return final;
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        char[] lpszFilePath,
        int cchFilePath,
        int dwFlags);
}
