namespace Temno.Core.Updates;

/// <summary>How this copy of Temno takes a new release.</summary>
public enum UpdateDelivery
{
    /// <summary>An official build Windows installed from a package: it downloads the release's package and installs it.</summary>
    InstallsPackage,

    /// <summary>
    /// An official build that runs from a folder: the zip from the release page, or a folder Windows registered. It has no
    /// package to update, so it names the release and where to download it (WP-451).
    /// </summary>
    DownloadsRelease,

    /// <summary>A build from source: it names the release and how to merge it (WP-402).</summary>
    MergesSource,
}

/// <summary>
/// What the Updates page, the Home card, the title-bar chip and the update dialog show for a check result. A packaged
/// official build offers to install. A build from source names the release and how to merge it, and never offers the
/// official package, so it never reaches the updater's "This update isn't from Temno" refusal (WP-402). An official build
/// that runs from a folder names the release and where to download it, and offers no install it could not finish (WP-451).
/// </summary>
public static class UpdateOffer
{
    /// <summary>
    /// The delivery for a build: from source merges, whatever it runs from; an official one installs only where Windows
    /// installed it from a package.
    /// </summary>
    public static UpdateDelivery DeliveryFor(bool officialBuild, bool installedFromPackage)
        => !officialBuild ? UpdateDelivery.MergesSource
            : installedFromPackage ? UpdateDelivery.InstallsPackage
            : UpdateDelivery.DownloadsRelease;

    /// <summary>The status sentence for <paramref name="result"/>.</summary>
    public static string Status(UpdateCheckResult result, UpdateDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Kind == UpdateCheckKind.Unconfigured)
            return UpdateChecker.UnconfiguredMessage;
        if (result.Kind != UpdateCheckKind.UpdateAvailable)
            return result.Message;
        if (delivery == UpdateDelivery.MergesSource)
            return UpdateChannelCopy.SourceBuildRelease(result.FeedVersion);
        if (delivery == UpdateDelivery.DownloadsRelease)
            return UpdateChannelCopy.FolderBuildRelease(result.FeedVersion);
        return UpdateInstallPlan.TryCreate(result, out _, out var error)
            ? result.Message
            : result.Message + "\n" + (error ?? UpdateChannelCopy.NoPackageUrl);
    }

    /// <summary>True when the build should show an Install button for <paramref name="result"/>.</summary>
    public static bool ShowsInstall(UpdateCheckResult? result, UpdateDelivery delivery)
        => delivery == UpdateDelivery.InstallsPackage
           && result?.Kind == UpdateCheckKind.UpdateAvailable
           && UpdateInstallPlan.TryCreate(result, out _, out _);

    /// <summary>
    /// True when the build should offer the download page for <paramref name="result"/>. It needs no package address: the
    /// page has the release, whichever form it takes.
    /// </summary>
    public static bool ShowsDownload(UpdateCheckResult? result, UpdateDelivery delivery)
        => delivery == UpdateDelivery.DownloadsRelease && result?.Kind == UpdateCheckKind.UpdateAvailable;

    /// <summary>The accessible name of the Home card and the title-bar chip while an update is out.</summary>
    public static string Name(UpdateCheckResult result, UpdateDelivery delivery)
    {
        ArgumentNullException.ThrowIfNull(result);
        var version = string.IsNullOrWhiteSpace(result.FeedVersion) ? "" : result.FeedVersion.Trim();
        if (delivery == UpdateDelivery.InstallsPackage)
            return version.Length == 0 ? "Install update" : "Install update " + version;
        return version.Length == 0 ? "A new Temno release is out" : "Temno " + version + " is out";
    }
}
