namespace Temno.Core.IO;

/// <summary>
/// Dual-read for the Palmier → Temno env-var rename (WP-193): every process-environment lookup goes
/// through here. The canonical name is <c>TEMNO_X</c>; when it is absent, a set
/// <c>PALMIER_X</c> is honoured and one deprecation line per variable per process is logged.
/// </summary>
public static class EnvCompat
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> AnnouncedLegacyNames = new(StringComparer.Ordinal);

    /// <summary>Test seam: observes each legacy-fallback deprecation line (fired at most once per name).</summary>
    public static event Action<string>? LegacyFallbackObserved;

    /// <param name="temnoName">Canonical variable name, e.g. <c>TEMNO_FFMPEG_PATH</c>.</param>
    /// <returns>
    /// The <c>TEMNO_</c> value when set and non-blank, else the <c>PALMIER_</c> value as-is
    /// (empty included, preserving prior empty-string semantics), else null.
    /// </returns>
    public static string? Get(string temnoName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temnoName);
        var current = Environment.GetEnvironmentVariable(temnoName);
        if (!string.IsNullOrWhiteSpace(current))
            return current;

        var legacyName = ToLegacyName(temnoName);
        if (legacyName is null)
            return current;

        var legacy = Environment.GetEnvironmentVariable(legacyName);
        if (string.IsNullOrWhiteSpace(legacy))
            return legacy;

        AnnounceOnce(temnoName, legacyName);
        return legacy;
    }

    /// <summary>Maps <c>TEMNO_X</c> to <c>PALMIER_X</c>; null for names outside the rename.</summary>
    public static string? ToLegacyName(string temnoName)
        => temnoName.StartsWith("TEMNO_", StringComparison.Ordinal)
            ? "PALMIER_" + temnoName.Substring("TEMNO_".Length)
            : null;

    private static void AnnounceOnce(string temnoName, string legacyName)
    {
        var line = $"[temno] {legacyName} is deprecated; set {temnoName} instead. The legacy value is honoured for this run.";
        lock (Gate)
        {
            if (!AnnouncedLegacyNames.Add(legacyName))
                return;
        }

        LegacyFallbackObserved?.Invoke(line);
        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            Console.Error.WriteLine(line);
        }
        catch
        {
            // Packaged WinUI has no console; Debug carries the line there.
        }
    }
}
