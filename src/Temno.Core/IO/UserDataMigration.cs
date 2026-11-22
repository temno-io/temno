using Temno.Core.Project;

namespace Temno.Core.IO;

/// <summary>
/// First-run copy-not-move migration from the legacy Palmier Pro user-data roots to the Temno
/// roots (WP-193). Only the small state files and regenerable caches move: the project registry,
/// user preferences, and the cache directories. <c>Projects\**</c> is never copied — those are
/// the owner's real packages, and the copied registry keeps pointing at them (they are still
/// the real files; dual-read opens them). The old roots are left intact. Never overwrites:
/// a present new file, or a present non-empty new directory, wins.
/// </summary>
public static class UserDataMigration
{
    /// <summary>Test seam: observes each migration/skip line.</summary>
    public static event Action<string>? MigrationObserved;

    /// <summary>
    /// Cache directory names migrated under the LocalAppData root: the media cache
    /// (<c>cache/</c>), the transcript disk cache (<c>transcripts/</c>), and the
    /// visual-search index (<c>visual-search/</c>). Model stores (whisper/siglip) are not
    /// caches; they re-resolve on first use. The home dot-dir (<c>~/.palmier</c>) is not
    /// copied at all: presets and templates dual-read the legacy directory directly.
    /// </summary>
    private static readonly string[] CacheDirectoryNames = ["cache", "transcripts", "visual-search"];

    /// <summary>
    /// Copies the scoped state files and caches. True if anything copied. A development build skips it (WP-334): the
    /// Palmier Pro root is the owner's pre-rename data, and a development root is seeded from the shared Temno root.
    /// </summary>
    public static bool EnsureMigrated()
        => Account.HostedEndpoints.BuildEnvironment != Environments.TemnoEnvironment.Development
           && MigrateAll(
               UserDataRoots.LegacyDocumentsRoot, UserDataRoots.DocumentsRoot,
               UserDataRoots.LegacyLocalAppDataRoot, UserDataRoots.LocalAppDataRoot);

    /// <summary>The small state a development build copies into its own root on first run (WP-334).</summary>
    private static readonly string[] DevelopmentSeedFiles =
    [
        Account.DevelopmentBackendFile.FileName, "user-preferences.json", "editor-layout.json",
        "media-library-view.json", "preview-guides.json",
    ];

    /// <summary>
    /// WP-334: a development build's first run in <c>%LOCALAPPDATA%\Temno-Dev</c>. The developer file and the small UI
    /// state are copied from the root every build shared before. Development sign-ins are moved, never copied: a
    /// refresh token used from two places revokes the session. The MCP token is not carried, so Temno-Dev mints its
    /// own and a script holding the production app's token cannot drive it, or the reverse. The production sign-in
    /// stays. A production build does nothing. True if anything was seeded.
    /// </summary>
    public static bool EnsureDevelopmentRootSeeded()
    {
        if (Account.HostedEndpoints.BuildEnvironment != Environments.TemnoEnvironment.Development)
            return false;
        var productionOrigin = Account.AccountStoreKey.Origin(Environments.EnvironmentIdentity.Production.SupabaseUrl);
        return SeedDevelopmentRoot(
            UserDataRoots.SharedLocalAppDataRoot, UserDataRoots.LocalAppDataRoot,
            productionOrigin is null ? null : Account.AccountStoreKey.Key(productionOrigin));
    }

    /// <summary>The seeding over explicit roots (test seam). Never overwrites and never throws.</summary>
    public static bool SeedDevelopmentRoot(string sharedRoot, string developmentRoot, string? productionAccountKey)
    {
        if (SamePath(sharedRoot, developmentRoot))
            return false;
        var seeded = false;
        foreach (var name in DevelopmentSeedFiles)
            seeded |= TryMigrateFile(Path.Combine(sharedRoot, name), Path.Combine(developmentRoot, name));
        var accounts = Path.Combine(sharedRoot, "account");
        string[] keys;
        try
        {
            keys = Directory.Exists(accounts) ? Directory.GetDirectories(accounts) : [];
        }
        catch (Exception ex)
        {
            Observe($"[temno] Skipped listing '{accounts}' while seeding the development root ({ex.GetType().Name}); continuing.");
            keys = [];
        }
        foreach (var keyDirectory in keys)
        {
            var key = Path.GetFileName(keyDirectory);
            if (string.Equals(key, productionAccountKey, StringComparison.OrdinalIgnoreCase))
                continue;
            seeded |= TryMoveDirectory(keyDirectory, Path.Combine(developmentRoot, "account", key));
        }
        return seeded;
    }

    private static bool TryMoveDirectory(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from) || !Directory.EnumerateFileSystemEntries(from).Any())
                return false;
            if (Directory.Exists(to))
            {
                if (Directory.EnumerateFileSystemEntries(to).Any())
                    return false;
                Directory.Delete(to);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            Directory.Move(from, to);
            Observe($"[temno] Moved '{from}' to '{to}'.");
            return true;
        }
        catch (Exception ex)
        {
            Observe($"[temno] Skipped moving '{from}' ({ex.GetType().Name}); continuing.");
            return false;
        }
    }

    /// <summary>
    /// Scoped migration over explicit roots (test seam; production passes the real roots).
    /// Never throws: every item carries its own catch-all so one unreadable file cannot stop
    /// the app launching (or the rest of the migration).
    /// </summary>
    public static bool MigrateAll(
        string legacyDocumentsRoot, string newDocumentsRoot,
        string legacyLocalAppDataRoot, string newLocalAppDataRoot)
    {
        if (SamePath(legacyDocumentsRoot, newDocumentsRoot)
            || SamePath(legacyLocalAppDataRoot, newLocalAppDataRoot))
            return false;

        var migrated = false;
        migrated |= TryMigrateFile(
            Path.Combine(legacyDocumentsRoot, ProjectConstants.RegistryFilename),
            Path.Combine(newDocumentsRoot, ProjectConstants.RegistryFilename));
        migrated |= TryMigrateFile(
            Path.Combine(legacyLocalAppDataRoot, "user-preferences.json"),
            Path.Combine(newLocalAppDataRoot, "user-preferences.json"));
        foreach (var cache in CacheDirectoryNames)
            migrated |= TryMigrateDirectory(
                Path.Combine(legacyLocalAppDataRoot, cache),
                Path.Combine(newLocalAppDataRoot, cache));
        return migrated;
    }

    private static bool TryMigrateFile(string legacyFile, string newFile)
    {
        try
        {
            if (!File.Exists(legacyFile) || File.Exists(newFile))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(newFile)!);
            // Byte-identical copy: registry entry URLs keep pointing at the legacy packages,
            // which are still the real files. Rewriting them would orphan the entries.
            File.Copy(legacyFile, newFile, overwrite: false);
            Observe($"[temno] Migrated '{legacyFile}' to '{newFile}'. The old file was left intact.");
            return true;
        }
        catch (Exception ex)
        {
            Observe($"[temno] Skipped '{legacyFile}' during migration ({ex.GetType().Name}); continuing.");
            return false;
        }
    }

    private static bool TryMigrateDirectory(string legacyDir, string newDir)
    {
        try
        {
            if (!Directory.Exists(legacyDir))
                return false;
            if (Directory.Exists(newDir) && Directory.EnumerateFileSystemEntries(newDir).Any())
                return false;
            var copied = CopyTree(legacyDir, newDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            if (copied)
                Observe($"[temno] Migrated '{legacyDir}' to '{newDir}'. The old directory was left intact.");
            return copied;
        }
        catch (Exception ex)
        {
            Observe($"[temno] Skipped '{legacyDir}' during migration ({ex.GetType().Name}); continuing.");
            return false;
        }
    }

    /// <returns>True when at least one file was copied.</returns>
    private static bool CopyTree(string source, string destination, HashSet<string> visited)
    {
        var copied = false;
        var normalized = Path.GetFullPath(source);
        if (!visited.Add(normalized))
            return false;

        Directory.CreateDirectory(destination);
        string[] files;
        try
        {
            files = Directory.GetFiles(source);
        }
        catch (Exception ex)
        {
            Observe($"[temno] Skipped listing '{source}' during migration ({ex.GetType().Name}); continuing.");
            return false;
        }

        foreach (var file in files)
        {
            try
            {
                if (IsReparsePoint(file))
                {
                    Observe($"[temno] Skipped reparse point during migration: '{file}'.");
                    continue;
                }

                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
                copied = true;
            }
            catch (Exception ex)
            {
                Observe($"[temno] Skipped '{file}' during migration ({ex.GetType().Name}); continuing.");
            }
        }

        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories(source);
        }
        catch (Exception ex)
        {
            Observe($"[temno] Skipped listing '{source}' during migration ({ex.GetType().Name}); continuing.");
            return copied;
        }

        foreach (var dir in dirs)
        {
            try
            {
                if (IsReparsePoint(dir))
                {
                    Observe($"[temno] Skipped reparse point during migration: '{dir}'.");
                    continue;
                }

                copied |= CopyTree(dir, Path.Combine(destination, Path.GetFileName(dir)), visited);
            }
            catch (Exception ex)
            {
                Observe($"[temno] Skipped '{dir}' during migration ({ex.GetType().Name}); continuing.");
            }
        }

        return copied;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void Observe(string line)
    {
        MigrationObserved?.Invoke(line);
        System.Diagnostics.Debug.WriteLine(line);
    }
}
