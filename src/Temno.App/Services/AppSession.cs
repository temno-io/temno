using System.Diagnostics;
using Temno.Core.IO;
using Temno.Core.Project;
using Temno.Core.Session;

namespace Temno.App.Services;

/// <summary>
/// UI-facing project session hub. Wraps Core <see cref="ProjectHub"/> and registry.
/// </summary>
public sealed class AppSession
{
    public static AppSession Current { get; } = new();

    static AppSession()
    {
        ProjectHub.ActiveSessionChanged += (_, _) =>
            Current.SessionChanged?.Invoke(Current, EventArgs.Empty);
    }

    public ProjectSession? Session
    {
        get => ProjectHub.ActiveSession;
        private set => ProjectHub.ActiveSession = value;
    }

    public event EventHandler? SessionChanged;

    public static string ProjectsDirectory
    {
        get
        {
            var env = EnvCompat.Get("TEMNO_PROJECTS_DIR");
            if (!string.IsNullOrWhiteSpace(env))
                return env;

            return Path.Combine(
                UserDataRoots.DocumentsRoot,
                "Projects");
        }
    }

    public static string StorageDirectory =>
        UserDataRoots.DocumentsRoot;

    public IReadOnlyList<ProjectEntry> LoadRecentProjects()
    {
        return ProjectRegistry.Load()
            .OrderByDescending(e => e.LastOpenedDate)
            .ToList();
    }

    public ProjectSession Open(string packagePath)
    {
        var full = Path.GetFullPath(packagePath);
        if (!Directory.Exists(full))
            throw new DirectoryNotFoundException($"No project package at {full}");

        var timeline = Path.Combine(full, ProjectConstants.TimelineFilename);
        if (!File.Exists(timeline))
            throw new FileNotFoundException($"Not a Temno project (missing {ProjectConstants.TimelineFilename})", timeline);

        if (Session is not null)
            throw new InvalidOperationException("Close the current project before opening another.");

        Session = ProjectSession.Open(full);
        ProjectRegistry.Register(full);
        return Session;
    }

    public ProjectSession CreateNew(string name, int fps = 30, int width = 1920, int height = 1080)
    {
        name = SanitizeProjectName(name);
        if (string.IsNullOrWhiteSpace(name))
            name = ProjectConstants.DefaultProjectName;

        if (Session is not null)
            throw new InvalidOperationException("Close the current project before creating another.");

        Directory.CreateDirectory(ProjectsDirectory);
        var packagePath = AllocateUniquePackagePath(ProjectsDirectory, name);

        Directory.CreateDirectory(packagePath);
        Directory.CreateDirectory(Path.Combine(packagePath, ProjectConstants.MediaDirectoryName));

        var session = ProjectSession.CreateNew(name: "Timeline 1", fps: fps, width: width, height: height);
        session.Save(packagePath);

        Session = session;
        ProjectRegistry.Register(packagePath);
        return session;
    }

    public void Close(bool save = true)
    {
        if (Session is null) return;
        if (save && Session.PackagePath is not null)
            Session.Save();

        Session = null;
    }

    public void RemoveFromRegistry(string packagePath)
    {
        ProjectRegistry.Remove(packagePath);
    }

    public static void RevealInExplorer(string packagePath)
    {
        var full = Path.GetFullPath(packagePath);
        if (Directory.Exists(full) || File.Exists(full))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{full}\"",
                UseShellExecute = true,
            });
        }
        else
        {
            var parent = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{parent}\"",
                    UseShellExecute = true,
                });
            }
        }
    }

    public static string PathFromEntry(ProjectEntry entry)
    {
        var url = entry.Url;
        if (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            return new Uri(url).LocalPath;
        return url;
    }

    public static string SanitizeProjectName(string name)
    {
        name = name.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim();
    }

    public static string AllocateUniquePackagePath(string parentDir, string name)
        => ProjectConstants.AllocateUniquePackagePath(parentDir, name);
}
