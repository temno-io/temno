using System.Diagnostics;
using System.Text;
using Temno.Core.Account;
using Temno.Core.Updates;
using Temno.Environments;

namespace Temno.App.Services;

public sealed record UpdateInstallOutcome(bool Installed, string Message);

/// <summary>
/// Checks a package against the installed app (WP-356), then asks Windows to install it. Nothing reaches
/// <c>Add-AppxPackage</c> without passing <see cref="UpdatePackageSignature.Verify"/>. A build from source installs nothing
/// (WP-402): it is told to merge the release instead, so it never meets the wrong-publisher refusal.
/// </summary>
public static class WindowsUpdateInstaller
{
    public static async Task<UpdateInstallOutcome> InstallAsync(
        string packagePath,
        string? feedVersion,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!BuildProvenance.Current.IsOfficial)
            return new UpdateInstallOutcome(false, UpdateChannelCopy.SourceBuildRelease(feedVersion));

        if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            return new UpdateInstallOutcome(false, "The update package file was not found.");

        // No surface offers an install to a copy that runs from a folder (WP-451); this is the backstop.
        var expected = InstalledIdentity();
        if (expected is null || RegisteredFromFolder())
            return new UpdateInstallOutcome(false, UpdateChannelCopy.FolderBuildRelease(feedVersion));

        progress?.Report("Checking…");
        // Windows' signature check hashes the whole package, hundreds of megabytes, so it runs off the UI thread.
        var check = await Task.Run(
            () => UpdatePackageSignature.Verify(packagePath, expected, feedVersion),
            cancellationToken).ConfigureAwait(false);
        if (!check.Accepted)
        {
            return new UpdateInstallOutcome(
                false,
                UpdateChannelCopy.Refused(check.Refusal, HostedEndpoints.BuildEnvironment, check.Detail));
        }

        progress?.Report("Installing…");
        var statement = UpdateInstallPlan.PowerShell(forceApplicationShutdown: true);
        // Progress records become CLIXML on a redirected error stream and hide the real failure.
        var command =
            "$ProgressPreference = 'SilentlyContinue'\r\n" +
            "$ErrorActionPreference = 'Stop'\r\n" +
            "try {\r\n" +
            statement + "\r\n" +
            "    Write-Output 'INSTALLED'\r\n" +
            "} catch {\r\n" +
            "    Write-Output ('FAILED: ' + $_.Exception.Message)\r\n" +
            "    exit 1\r\n" +
            "}\r\n";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var powershell = Path.Combine(
            Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(encoded);
        // Data, never script text: see UpdateInstallPlan.PowerShell.
        start.Environment[UpdateInstallPlan.PackagePathVariable] = packagePath;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start Windows PowerShell to install the update.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask, process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);

        var stdout = (await stdoutTask.ConfigureAwait(false)).Trim();
        var stderr = (await stderrTask.ConfigureAwait(false)).Trim();
        if (process.ExitCode == 0 && stdout.Contains("INSTALLED", StringComparison.Ordinal))
        {
            var version = string.IsNullOrWhiteSpace(feedVersion) ? "the update" : feedVersion.Trim();
            return new UpdateInstallOutcome(true, "Installed " + version + ".");
        }

        var detail = stdout.StartsWith("FAILED:", StringComparison.Ordinal)
            ? stdout["FAILED:".Length..].Trim()
            : FirstDetail(stderr, stdout);
        if (string.IsNullOrWhiteSpace(detail) || detail.Contains("CLIXML", StringComparison.Ordinal))
            detail = "Windows did not install the package (exit " + process.ExitCode + ").";
        if (LooksLikeTrustFailure(detail))
        {
            var honesty = UpdateChannelCopy.SideloadHonestyFor(HostedEndpoints.BuildEnvironment);
            if (honesty.Length > 0)
                detail += "\n" + honesty;
        }
        return new UpdateInstallOutcome(false, UpdateChannelCopy.InstallFailedFor(HostedEndpoints.BuildEnvironment, detail));
    }

    /// <summary>
    /// The running package as Windows records it: an update must be this app, from this publisher, and newer.
    /// Null when this copy has no package identity (a build folder), which cannot take an update.
    /// </summary>
    public static UpdateExpectation? InstalledIdentity()
    {
        try
        {
            var id = Windows.ApplicationModel.Package.Current.Id;
            var version = id.Version;
            if (string.IsNullOrEmpty(id.Name) || string.IsNullOrEmpty(id.Publisher))
                return null;
            return new UpdateExpectation(
                id.Name,
                id.Publisher,
                new Version(version.Major, version.Minor, version.Build, version.Revision));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when Windows installed this copy from a package, the only kind a release's package can replace. False for a
    /// build folder, which is what the zip from the release page is, and for a copy registered from a folder (WP-451).
    /// </summary>
    public static bool InstalledFromPackage()
        => InstalledIdentity() is not null && !RegisteredFromFolder();

    /// <summary>
    /// A copy Windows registered from a folder (Developer Mode), as scripts/install-app.ps1 installs Temno and
    /// Temno-Dev. A package installed over it would replace that registration, and install-app.ps1 refuses to touch
    /// an app installed another way, so the folder's own script updates it (WP-375).
    /// </summary>
    private static bool RegisteredFromFolder()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.IsDevelopmentMode;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string FirstDetail(string stderr, string stdout)
    {
        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        detail = detail.Trim();
        return detail.Length <= 600 ? detail : detail[..600] + "…";
    }

    private static bool LooksLikeTrustFailure(string text)
        => text.Contains("certificate", StringComparison.OrdinalIgnoreCase)
           || text.Contains("0x800B0109", StringComparison.OrdinalIgnoreCase)
           || text.Contains("not trusted", StringComparison.OrdinalIgnoreCase)
           || text.Contains("SmartScreen", StringComparison.OrdinalIgnoreCase);
}
