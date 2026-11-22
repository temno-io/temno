using Temno.Environments;

namespace Temno.Core.Updates;

/// <summary>
/// Sentences the settings pane, the install prompt, and the operator script all use.
/// </summary>
public static class UpdateChannelCopy
{
    public const string NoPackageUrl =
        "This feed has no package URL, so Temno cannot install an update from it.";

    public const string SideloadHonesty =
        "Windows will not install an unsigned package. SmartScreen may still warn for a signed sideload from a publisher this PC does not trust. This build has no purchased certificate.";

    public const string UnsignedBlocked =
        "This package is unsigned. Windows will not install it. SmartScreen blocks unsigned sideload. Sign it with a certificate this PC trusts. This build has no purchased certificate.";

    public const string HttpsOnly =
        "Temno only downloads updates over a secure https connection.";

    public const string NetworkShare =
        "Temno doesn't open updates from network shares.";

    public const string OnlineFeedLocalPackage =
        "An online update feed can only point at an https download.";

    public const string MsixOnly =
        "Temno updates come as a single .msix package.";

    /// <summary>
    /// Where a copy that runs from a folder gets a new release (WP-451). The site, not a file: its Download leads to the
    /// current release in whatever form that takes, and this sentence ships in builds that can never be corrected.
    /// </summary>
    public const string DownloadPageUrl = "https://temno.io";

    /// <summary>The button that opens <see cref="DownloadPageUrl"/>.</summary>
    public const string DownloadPageLabel = "Open temno.io";

    /// <summary>
    /// What an official build that runs from a folder says instead of offering an install (WP-451). It has no package for
    /// Windows to replace, so an Install button could only download the release and then refuse it.
    /// </summary>
    public static string FolderBuildRelease(string? feedVersion)
    {
        const string how =
            "This copy runs from a folder, so it doesn't update itself: download the new release at temno.io and use it instead. Your projects and settings stay where they are.";
        return string.IsNullOrWhiteSpace(feedVersion)
            ? "A new Temno release is out. " + how
            : $"Temno {feedVersion.Trim()} is out. " + how;
    }

    /// <summary>Temno's public source repository, where each release is one commit and one tag.</summary>
    public const string SourceRepositoryUrl = "https://github.com/temno-io/temno";

    /// <summary>The public repository's tag for a release: <c>v</c> and the four-part version the feed announces.</summary>
    public static string ReleaseTag(string version) => "v" + version.Trim();

    /// <summary>
    /// What a build from source says instead of offering an install (WP-402). It never downloads the official package: the
    /// updater would refuse another publisher's package anyway, and merging the release keeps the build's own changes.
    /// </summary>
    public static string SourceBuildRelease(string? feedVersion)
    {
        if (string.IsNullOrWhiteSpace(feedVersion))
            return "A new Temno release is out. This copy was built from source, so it doesn't install Temno's package: merge the release into your copy and rebuild.";
        var version = feedVersion.Trim();
        return $"Temno {version} is out, tagged {ReleaseTag(version)}. This copy was built from source, so it doesn't install Temno's package: merge the release into your copy and rebuild.";
    }

    /// <summary>The commands that merge a release into a clone of the public repository or a fork of it.</summary>
    public static string SourceBuildMergeCommands(string? feedVersion)
    {
        if (string.IsNullOrWhiteSpace(feedVersion))
            return "git fetch " + SourceRepositoryUrl + " --tags";
        var tag = ReleaseTag(feedVersion);
        return $"git fetch {SourceRepositoryUrl} tag {tag}\ngit merge {tag}";
    }

    /// <summary>
    /// Production shows no signing or SmartScreen sentence on the Updates page or in update dialogs.
    /// </summary>
    public static string SideloadHonestyFor(TemnoEnvironment environment)
        => environment == TemnoEnvironment.Production ? "" : SideloadHonesty;

    public static string UnsignedBlockedFor(TemnoEnvironment environment)
        => environment == TemnoEnvironment.Production
            ? "Windows did not install the package."
            : UnsignedBlocked;

    public const string InstallFailed = "Windows couldn't install this update, so Temno is unchanged.";

    /// <summary>
    /// A package that passed Temno's checks and still failed in Windows' installer. Windows' own text can name
    /// certificates, so production shows one sentence; a development build shows what Windows said (WP-356 review).
    /// </summary>
    public static string InstallFailedFor(TemnoEnvironment environment, string detail)
        => environment == TemnoEnvironment.Production || string.IsNullOrWhiteSpace(detail)
            ? InstallFailed
            : "Could not install the update: " + detail.Trim();

    /// <summary>
    /// Why an update was refused, in words a user can act on. Production names no signing, certificate or
    /// SmartScreen (WP-264's owner close); a development build adds what the check saw.
    /// </summary>
    public static string Refused(UpdateRefusal refusal, TemnoEnvironment environment, string detail = "")
    {
        var sentence = refusal switch
        {
            UpdateRefusal.NotAPackage => "This file isn't a Temno update package, so nothing was installed.",
            UpdateRefusal.WrongApp => "This package is a different app, not a Temno update, so nothing was installed.",
            UpdateRefusal.WrongPublisher => "This update isn't from Temno, so it wasn't installed.",
            UpdateRefusal.VersionMismatch => "This package isn't the version Temno's update feed announced, so it wasn't installed.",
            UpdateRefusal.NotNewer => "This package isn't newer than the Temno you have, so it wasn't installed.",
            UpdateRefusal.Tampered => "This Temno update was changed after it was published, so it wasn't installed.",
            UpdateRefusal.Unsigned or UpdateRefusal.Untrusted =>
                "Temno couldn't confirm this update came from Temno, so it wasn't installed.",
            _ => "",
        };
        if (environment == TemnoEnvironment.Production)
            return sentence;
        if (refusal == UpdateRefusal.Unsigned)
            sentence += " " + UnsignedBlocked;
        return string.IsNullOrWhiteSpace(detail) ? sentence : sentence + " (" + detail.Trim() + ")";
    }
}
