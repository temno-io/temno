using System.Text.Json;
using Temno.Core.IO;
using Temno.Environments;

namespace Temno.Core.Account;

/// <summary>
/// The per-machine, never-tracked description of the development backend
/// (<c>%LOCALAPPDATA%\Temno-Dev\development-backend.json</c>, docs/contracts/environments.md).
/// Only a development build reads it.
/// </summary>
public static class DevelopmentBackendFile
{
    public const string FileName = "development-backend.json";

    /// <summary>How messages name the file, so they read the same on every machine.</summary>
    public const string DisplayPath = @"%LOCALAPPDATA%\Temno-Dev\" + FileName;

    private static readonly object Gate = new();
    private static string? _pathOverride;
    private static string? _defaultPathOverride;
    private static CachedText? _cache;

    public static string DefaultPath
    {
        get
        {
            var own = Path.Combine(UserDataRoots.LocalAppDataRoot, FileName);
            if (File.Exists(own))
                return own;
            // WP-334: until a development build's first run copies it into its own root, the file is in the root every
            // build shared before.
            var shared = Path.Combine(UserDataRoots.SharedLocalAppDataRoot, FileName);
            return File.Exists(shared) ? shared : own;
        }
    }

    public static string EffectivePath
    {
        get
        {
            lock (Gate)
                return _pathOverride ?? _defaultPathOverride ?? DefaultPath;
        }
    }

    /// <summary>Per-test file. <c>null</c> falls back to <see cref="SetDefaultPathForTests"/>, never straight to the machine's file.</summary>
    public static void SetPathForTests(string? path)
    {
        lock (Gate)
        {
            _pathOverride = path;
            _cache = null;
        }
    }

    /// <summary>Process-wide test file, installed by the test assembly so no test reads the developer's real file.</summary>
    public static void SetDefaultPathForTests(string? path)
    {
        lock (Gate)
        {
            _defaultPathOverride = path;
            _cache = null;
        }
    }

    /// <summary>
    /// The file's text, or null when it does not exist. Throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when it exists and cannot be read. The text is
    /// cached against the file's size and write time, because endpoint checks run on UI paths.
    /// </summary>
    public static string? ReadText()
    {
        var path = EffectivePath;
        var info = new FileInfo(path);
        if (!info.Exists)
            return null;
        lock (Gate)
        {
            if (_cache is { } hit && hit.Path == path && hit.Length == info.Length && hit.WrittenUtc == info.LastWriteTimeUtc)
                return hit.Text;
        }

        var text = File.ReadAllText(path);
        lock (Gate)
            _cache = new CachedText(path, info.Length, info.LastWriteTimeUtc, text);
        return text;
    }

    /// <summary>
    /// Parses the file. Returns false with a user-facing <paramref name="problem"/> when the JSON
    /// is unreadable or the file does not say <c>"environment": "development"</c>.
    /// </summary>
    public static bool TryParse(string json, out DevelopmentBackendValues values, out string? problem)
    {
        values = new DevelopmentBackendValues(null, null, null, null);
        problem = null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = $"{DisplayPath} must be a JSON object. Hosted features are off.";
                return false;
            }

            if (!root.TryGetProperty("environment", out var environment)
                || environment.ValueKind != JsonValueKind.String
                || !TemnoEnvironments.TryParse(environment.GetString(), out var named))
            {
                problem = $"{DisplayPath} must say \"environment\": \"development\". Hosted features are off.";
                return false;
            }

            if (named != TemnoEnvironment.Development)
            {
                problem = $"{DisplayPath} says \"environment\": \"{named.Name()}\". A development build uses only a development backend, so hosted features are off.";
                return false;
            }

            string? bad = null;
            string? Member(string name)
            {
                if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                    return null;
                if (value.ValueKind != JsonValueKind.String)
                {
                    bad ??= name;
                    return null;
                }

                return value.GetString();
            }

            values = new DevelopmentBackendValues(
                Member("supabaseUrl"),
                Member("supabaseAnonKey"),
                Member("gatewayUrl"),
                Member("accountsUrl"),
                Member("eventsUrl"),
                Member("updateFeedUrl"));
            if (bad is not null)
            {
                values = new DevelopmentBackendValues(null, null, null, null);
                problem = $"\"{bad}\" in {DisplayPath} must be a string. Hosted features are off.";
                return false;
            }

            return true;
        }
        catch (JsonException)
        {
            problem = $"{DisplayPath} is not valid JSON. Hosted features are off.";
            return false;
        }
    }

    private sealed record CachedText(string Path, long Length, DateTime WrittenUtc, string Text);
}

/// <summary>The endpoint members of <see cref="DevelopmentBackendFile"/>; each is null when absent.</summary>
public sealed record DevelopmentBackendValues(
    string? SupabaseUrl,
    string? SupabaseAnonKey,
    string? GatewayUrl,
    string? AccountsUrl,
    string? EventsUrl = null,
    string? UpdateFeedUrl = null);
