using Temno.Core.IO;

namespace Temno.Media.Search;

/// <summary>
/// Locates a Palmier-exported SigLIP2 ONNX. HuggingFace
/// <c>palmier-io/siglip2-base-coreml</c> is CoreML-only today — those
/// <c>.mlpackage</c> trees are never treated as ONNX.
/// Search order: <c>TEMNO_SIGLIP_MODEL</c>, user-preferences path,
/// %LocalAppData%\Temno\siglip\, app <c>siglip\</c> folder.
/// </summary>
public static class SiglipModelStore
{
    public const string DefaultFileName = "siglip2-base-patch16-256.onnx";
    public const string BundledFolderName = "siglip";
    public const string EnvironmentVariableName = "TEMNO_SIGLIP_MODEL";

    private static string? _bundledOverride;
    private static string? _userOverride;

    public static void SetPathsForTests(string? bundledDirectoryOrFile, string? userDirectoryOrFile)
    {
        _bundledOverride = bundledDirectoryOrFile;
        _userOverride = userDirectoryOrFile;
    }

    public static string BundledDirectory => DirectoryOf(
        _bundledOverride,
        Path.Combine(AppContext.BaseDirectory, BundledFolderName));

    public static string UserDirectory => DirectoryOf(
        _userOverride,
        Path.Combine(
            UserDataRoots.LocalAppDataRoot,
            BundledFolderName));

    public static string BundledModelPath => FileOrDefault(_bundledOverride, BundledDirectory);

    public static string UserModelPath => FileOrDefault(_userOverride, UserDirectory);

    private static string DirectoryOf(string? overridePath, string fallbackDirectory)
    {
        if (string.IsNullOrWhiteSpace(overridePath)) return fallbackDirectory;
        if (Directory.Exists(overridePath)) return overridePath;
        if (File.Exists(overridePath)) return Path.GetDirectoryName(overridePath) ?? fallbackDirectory;
        return overridePath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(overridePath) ?? fallbackDirectory
            : overridePath;
    }

    private static string FileOrDefault(string? overridePath, string directory)
    {
        if (!string.IsNullOrWhiteSpace(overridePath)
            && (File.Exists(overridePath) || overridePath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)))
            return overridePath;
        return Path.Combine(directory, DefaultFileName);
    }

    public static string? TryResolvePath(string? preferencesPath = null)
    {
        if (TryUsableOnnx(EnvCompat.Get(EnvironmentVariableName), out var env))
            return env;
        if (TryUsableOnnx(preferencesPath, out var prefs))
            return prefs;
        if (TryUsableOnnx(UserModelPath, out var user))
            return user;
        if (TryUsableOnnx(UserDirectory, out var userDir))
            return userDir;
        if (TryUsableOnnx(BundledModelPath, out var bundled))
            return bundled;
        if (TryUsableOnnx(BundledDirectory, out var bundledDir))
            return bundledDir;
        return null;
    }

    public static string? TryResolvePath(UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        return TryResolvePath(preferences.ResolveSiglipModelPath());
    }

    public static bool IsReady => TryResolvePath() is not null;

    public static bool IsCoreMlPackage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var trimmed = path.Trim();
        if (trimmed.EndsWith(".mlpackage", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.EndsWith(".mlmodel", StringComparison.OrdinalIgnoreCase)) return true;
        if (trimmed.EndsWith(".mlpackage.zip", StringComparison.OrdinalIgnoreCase)) return true;
        var name = Path.GetFileName(trimmed);
        return name.Equals("ImageEncoder.mlpackage.zip", StringComparison.OrdinalIgnoreCase)
            || name.Equals("TextEncoder.mlpackage.zip", StringComparison.OrdinalIgnoreCase)
            || name.Equals("tokenizer.zip", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOnnxFile(string? path)
        => !string.IsNullOrWhiteSpace(path)
            && path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)
            && !IsCoreMlPackage(path);

    internal static bool TryUsableOnnx(string? candidate, out string resolved)
    {
        resolved = "";
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        var path = candidate.Trim();
        if (IsCoreMlPackage(path)) return false;

        if (Directory.Exists(path))
        {
            var inside = Path.Combine(path, DefaultFileName);
            if (File.Exists(inside) && IsOnnxFile(inside))
            {
                resolved = inside;
                return true;
            }

            return false;
        }

        if (File.Exists(path) && IsOnnxFile(path))
        {
            resolved = path;
            return true;
        }

        return false;
    }
}
