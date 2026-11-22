using System.Runtime.InteropServices;
using Temno.Core.IO;

namespace Temno.Media;

/// <summary>
/// Resolves ffmpeg and ffprobe. On Windows the shipped build lives beside the app
/// (<c>ffmpeg/</c> in the MSIX) or, for a local checkout, at <c>tools/ffmpeg/win-x64</c>.
/// PATH is not consulted: a clean machine must not pick up some other FFmpeg.
/// <c>TEMNO_FFMPEG_PATH</c>, <c>TEMNO_FFPROBE_PATH</c> and <c>TEMNO_FFMPEG_DIR</c> still override.
/// Non-Windows test hosts keep the Homebrew and PATH fallbacks; this package does not ship those binaries.
/// </summary>
public static class FFmpegResolver
{
    public const string BundledFolderName = "ffmpeg";

    private static readonly string[] DevelopmentRelativeParts = ["tools", "ffmpeg", "win-x64"];

    public static string GetFFmpegPath() => Resolve("ffmpeg");

    public static string GetFFprobePath() => Resolve("ffprobe");

    public static string Resolve(string binaryName)
        => Resolve(
            binaryName,
            AppContext.BaseDirectory,
            EnvCompat.Get,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            Environment.GetEnvironmentVariable("PATH"));

    internal static string Resolve(
        string binaryName,
        string startDirectory,
        Func<string, string?> env,
        bool windows,
        string? pathEnv,
        int maxParentHops = 12,
        char? pathSeparator = null)
    {
        var fileName = binaryName + (windows ? ".exe" : "");

        var direct = env($"TEMNO_{binaryName.ToUpperInvariant()}_PATH");
        if (!string.IsNullOrEmpty(direct) && File.Exists(direct))
            return direct;

        var envDir = env("TEMNO_FFMPEG_DIR");
        if (!string.IsNullOrEmpty(envDir))
        {
            var fromDir = Path.Combine(envDir, fileName);
            if (File.Exists(fromDir))
                return fromDir;
        }

        var bundled = Path.Combine(startDirectory, BundledFolderName, fileName);
        if (File.Exists(bundled))
            return bundled;

        var beside = Path.Combine(startDirectory, fileName);
        if (File.Exists(beside))
            return beside;

        var walked = FindDevelopmentBuild(startDirectory, fileName, maxParentHops);
        if (walked is not null)
            return walked;

        if (!windows)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var brewPath = $"/opt/homebrew/bin/{binaryName}";
                if (File.Exists(brewPath))
                    return brewPath;

                var localPath = $"/usr/local/bin/{binaryName}";
                if (File.Exists(localPath))
                    return localPath;
            }

            if (FindOnPath(pathEnv, fileName, pathSeparator ?? ':') is { } onPath)
                return onPath;

            return binaryName;
        }

        return bundled;
    }

    private static string? FindDevelopmentBuild(string startDirectory, string fileName, int maxParentHops)
    {
        var dir = new DirectoryInfo(startDirectory);
        for (var hop = 0; hop <= maxParentHops && dir is not null; hop++)
        {
            var candidate = dir.FullName;
            foreach (var part in DevelopmentRelativeParts)
                candidate = Path.Combine(candidate, part);
            candidate = Path.Combine(candidate, fileName);
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        return null;
    }

    private static string? FindOnPath(string? pathEnv, string fileName, char separator)
    {
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (var dir in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var fullPath = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(fullPath))
                    return fullPath;
            }
            catch (ArgumentException)
            {
                // Ignore an invalid PATH entry.
            }
            catch (IOException)
            {
            }
        }

        return null;
    }
}
