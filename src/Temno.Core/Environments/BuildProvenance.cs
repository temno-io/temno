using System.Reflection;

namespace Temno.Environments;

/// <summary>
/// Where this build came from (WP-402): whether it carries Temno's official-build stamp, the release version its
/// source names, and the source commit the SDK recorded. Support reads it; nothing trusts it. Anyone who builds the
/// source can set the stamp, so it is a label and never a security control: the hosted services trust the signed-in
/// account, and the updater trusts the package signature (<c>UpdatePackageSignature</c>).
/// </summary>
public sealed record BuildProvenance(bool IsOfficial, string? SourceCommit, string? AppVersion)
{
    /// <summary>The MSBuild property <c>TemnoOfficialBuild</c>, compiled into Temno.Core.</summary>
    public const string OfficialMetadataKey = "TemnoOfficialBuild";

    /// <summary>The Identity version of <c>src/Temno.App/Package.appxmanifest</c> when Temno.Core was built.</summary>
    public const string AppVersionMetadataKey = "TemnoAppVersion";

    public const string SourceBuildLabel = "Built from source";

    public static BuildProvenance Current { get; } = Read(typeof(BuildProvenance).Assembly);

    /// <summary>Official only for the metadata value <c>true</c>; no metadata, or anything else, is a build from source.</summary>
    public static BuildProvenance Read(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var official = false;
        string? appVersion = null;
        foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(attribute.Key, OfficialMetadataKey, StringComparison.Ordinal))
                official = string.Equals(attribute.Value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
            else if (string.Equals(attribute.Key, AppVersionMetadataKey, StringComparison.Ordinal))
                appVersion = string.IsNullOrWhiteSpace(attribute.Value) ? null : attribute.Value.Trim();
        }

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return new BuildProvenance(official, CommitFrom(informational), appVersion);
    }

    /// <summary>
    /// The commit the SDK appends to an informational version as <c>+sha</c>, lowercase, or null when the build had no
    /// git (a source zip) or the suffix is not a commit.
    /// </summary>
    public static string? CommitFrom(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return null;
        var plus = informationalVersion.LastIndexOf('+');
        if (plus < 0)
            return null;
        var tail = informationalVersion[(plus + 1)..].Trim();
        var length = 0;
        while (length < tail.Length && Uri.IsHexDigit(tail[length]))
            length++;
        // The SDK joins further build metadata with '.', so a commit is a whole hex run of 7 to 40 characters.
        if (length is < 7 or > 40 || (length < tail.Length && tail[length] != '.'))
            return null;
        return tail[..length].ToLowerInvariant();
    }

    /// <summary>
    /// The version the update feed is compared with. An official build uses its installed package's, which is what
    /// Windows installed, and a build folder the version its source names. A build from source never installs a package
    /// update, so it uses the release its source names even when packaged: a fork's package keeps the committed
    /// manifest's version, and would otherwise be told about a release it already merged (WP-402 T1).
    /// </summary>
    public string VersionToCompare(string? packageVersion, string informationalVersion)
        => !IsOfficial && AppVersion is not null
            ? AppVersion
            : packageVersion ?? AppVersion ?? informationalVersion;

    /// <summary>The first 12 characters of <see cref="SourceCommit"/>, enough to find it in a fork.</summary>
    public string? ShortCommit => SourceCommit is null ? null : SourceCommit[..Math.Min(12, SourceCommit.Length)];

    /// <summary>
    /// The About box's version line, which Send Feedback repeats. An official build reads as it did before WP-402.
    /// </summary>
    public string VersionLine(string version)
    {
        var line = "Version " + version;
        if (IsOfficial)
            return line;
        line += " · " + SourceBuildLabel;
        return ShortCommit is null ? line : line + ", commit " + ShortCommit;
    }
}
