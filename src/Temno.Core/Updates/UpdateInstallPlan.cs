namespace Temno.Core.Updates;

/// <summary>
/// Where a feed's <c>appInstallerUrl</c> points, and the PowerShell that installs a local file.
/// Only an <c>.msix</c> becomes an install: over https from an online feed, or a local file from a local feed.
/// Remote URLs are downloaded first. Empty, http, network-share and non-package URLs never become an install.
/// </summary>
public sealed class UpdateInstallPlan
{
    public required Uri Source { get; init; }
    public string? LocalPath { get; init; }

    public bool IsLocalFile => LocalPath is not null;

    public static bool TryCreate(UpdateCheckResult result, out UpdateInstallPlan? plan, out string? error)
        => TryCreate(result.AppInstallerUrl, result.FeedIsLocal, out plan, out error);

    public static bool TryCreate(string? url, bool feedIsLocal, out UpdateInstallPlan? plan, out string? error)
    {
        plan = null;
        error = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            error = UpdateChannelCopy.NoPackageUrl;
            return false;
        }

        var trimmed = url.Trim();
        if (UpdateChecker.IsNetworkPath(trimmed))
        {
            error = UpdateChannelCopy.NetworkShare;
            return false;
        }

        Uri source;
        string? local = null;
        string extensionSource;

        if (UpdateChecker.TryResolveLocalFeedPath(trimmed, out var localPath))
        {
            if (!feedIsLocal)
            {
                error = UpdateChannelCopy.OnlineFeedLocalPackage;
                return false;
            }

            try
            {
                local = Path.GetFullPath(localPath);
                source = new Uri(local);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
            {
                error = "Could not install the update: the package path is not a usable file path.";
                return false;
            }
            extensionSource = local;
        }
        else if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            source = uri;
            extensionSource = Uri.UnescapeDataString(uri.AbsolutePath);
        }
        else if (Uri.TryCreate(trimmed, UriKind.Absolute, out var http) && http.Scheme == Uri.UriSchemeHttp)
        {
            error = UpdateChannelCopy.HttpsOnly;
            return false;
        }
        else
        {
            error = "Could not install the update: the package must be an https address or a local file.";
            return false;
        }

        if (!Path.GetExtension(extensionSource).Equals(".msix", StringComparison.OrdinalIgnoreCase))
        {
            error = UpdateChannelCopy.MsixOnly;
            return false;
        }

        plan = new UpdateInstallPlan
        {
            Source = source,
            LocalPath = local,
        };
        return true;
    }

    /// <summary>The environment variable the install statement reads the package path from.</summary>
    public const string PackagePathVariable = "TEMNO_UPDATE_PACKAGE";

    /// <summary>
    /// One <c>Add-AppxPackage</c> statement. The path is never part of the script: PowerShell reads it from
    /// <see cref="PackagePathVariable"/>, so no character in a file name can end a quoted string. PowerShell also
    /// treats the curly quotes U+2018 to U+201B as quotes, and WP-356's review ended a quoted path with one.
    /// </summary>
    public static string PowerShell(bool forceApplicationShutdown)
    {
        var statement = "Add-AppxPackage -Path $env:" + PackagePathVariable;
        if (forceApplicationShutdown)
            statement += " -ForceApplicationShutdown";
        return statement;
    }
}
