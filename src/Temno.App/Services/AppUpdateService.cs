using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Temno.App.Theme;
using Temno.Core.Account;
using Temno.Core.IO;
using Temno.Core.Updates;
using Temno.Environments;

namespace Temno.App.Services;

/// <summary>
/// Optional feed check, Home sidebar card, and editor title-bar chip.
/// Empty feed never hits the network. Rechecks at most once an hour unless forced.
/// Install downloads an .msix, checks it against the installed app (WP-356) and hands it to Windows. Only an official
/// build that Windows installed from a package installs: a build from source names the release and how to merge it
/// (WP-402), and an official build that runs from a folder names it and opens temno.io (WP-451).
/// </summary>
public sealed class AppUpdateService
{
    public static AppUpdateService Current { get; } = new();

    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private Task<UpdateCheckResult>? _inFlight;
    private DateTime _lastCheckUtc;

    public UpdateCheckResult? LastResult { get; private set; }

    public event EventHandler? Changed;

    public bool UpdateAvailable
        => LastResult?.Kind == UpdateCheckKind.UpdateAvailable;

    // Asked once: a copy does not change what it is while it runs, and asking Windows for a package throws in a folder.
    private static readonly Lazy<UpdateDelivery> DeliveryOnce = new(() => UpdateOffer.DeliveryFor(
        BuildProvenance.Current.IsOfficial, WindowsUpdateInstaller.InstalledFromPackage()));

    /// <summary>
    /// How this copy takes a new release. A build from source never downloads the official package: Windows would refuse
    /// another publisher's package, and merging the release keeps the build's own changes (WP-402). An official build
    /// that runs from a folder has no package for Windows to replace, so it is told where to download the release (WP-451).
    /// </summary>
    public static UpdateDelivery Delivery => DeliveryOnce.Value;

    /// <summary>Whether this copy installs update packages: an official build Windows installed from a package.</summary>
    public static bool InstallsPackages => Delivery == UpdateDelivery.InstallsPackage;

    public Task<UpdateCheckResult> CheckIfStaleAsync(CancellationToken cancellationToken = default)
        => CheckAsync(force: false, cancellationToken);

    public Task<UpdateCheckResult> CheckAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!force
                && LastResult is not null
                && DateTime.UtcNow - _lastCheckUtc < StaleAfter
                && (_inFlight is null || _inFlight.IsCompleted))
            {
                return Task.FromResult(LastResult);
            }

            if (!force && _inFlight is { IsCompleted: false } running)
                return running;

            _inFlight = CheckCoreAsync(cancellationToken);
            return _inFlight;
        }
    }

    public async Task PromptInstallAsync(XamlRoot? xamlRoot)
    {
        var result = await CheckAsync(force: true).ConfigureAwait(true);
        if (xamlRoot is null)
            return;

        if (result.Kind != UpdateCheckKind.UpdateAvailable)
        {
            await ShowNoteAsync(xamlRoot, "Check for Updates", FormatStatus(result)).ConfigureAwait(true);
            return;
        }

        if (Delivery == UpdateDelivery.MergesSource)
        {
            // Selectable, so the git lines can be copied.
            await ShowNoteAsync(
                xamlRoot,
                "Update available",
                FormatStatus(result) + "\n\n" + UpdateChannelCopy.SourceBuildMergeCommands(result.FeedVersion),
                selectable: true).ConfigureAwait(true);
            return;
        }

        if (Delivery == UpdateDelivery.DownloadsRelease)
        {
            var download = AppDialog.Create(xamlRoot);
            download.Title = "Update available";
            download.Content = Note(FormatStatus(result));
            download.PrimaryButtonText = UpdateChannelCopy.DownloadPageLabel;
            download.CloseButtonText = "Later";
            download.DefaultButton = ContentDialogButton.Primary;
            if (await download.ShowAsync() == ContentDialogResult.Primary)
                OpenDownloadPage();
            return;
        }

        if (!UpdateInstallPlan.TryCreate(result, out var plan, out var planError) || plan is null)
        {
            await ShowNoteAsync(
                xamlRoot,
                "Update available",
                AppendHonesty(planError ?? UpdateChannelCopy.NoPackageUrl)).ConfigureAwait(true);
            return;
        }

        var confirm = AppDialog.Create(xamlRoot);
        confirm.Title = "Update available";
        confirm.Content = Note(AppendHonesty(result.Message));
        confirm.PrimaryButtonText = "Install update";
        confirm.CloseButtonText = "Later";
        confirm.DefaultButton = ContentDialogButton.Primary;
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            return;

        var status = Note(plan.IsLocalFile ? "Installing…" : "Downloading…");
        var progressDialog = AppDialog.Create(xamlRoot);
        progressDialog.Title = "Installing update";
        progressDialog.Content = status;
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        progressDialog.Opened += (_, _) => opened.TrySetResult();
        var shown = progressDialog.ShowAsync();
        var openedInTime = await Task.WhenAny(opened.Task, Task.Delay(3000)).ConfigureAwait(true) == opened.Task;

        UpdateInstallOutcome outcome;
        try
        {
            var progress = new Progress<string>(text => status.Text = text);
            outcome = await DownloadAndInstallAsync(result, progress).ConfigureAwait(true);
        }
        finally
        {
            if (openedInTime)
            {
                try
                {
                    progressDialog.Hide();
                }
                catch (Exception)
                {
                    // The dialog was already closed.
                }
            }
        }

        if (openedInTime)
        {
            try
            {
                await shown;
            }
            catch (Exception)
            {
                // Hide already completed the dialog.
            }
        }

        await ShowNoteAsync(
            xamlRoot,
            outcome.Installed ? "Update installed" : "Update",
            outcome.Message).ConfigureAwait(true);
    }

    public async Task<UpdateInstallOutcome> DownloadAndInstallAsync(
        UpdateCheckResult? alreadyChecked = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = alreadyChecked;
        if (result is null || result.Kind != UpdateCheckKind.UpdateAvailable)
        {
            progress?.Report("Checking…");
            result = await CheckAsync(force: true, cancellationToken).ConfigureAwait(false);
        }

        if (result.Kind != UpdateCheckKind.UpdateAvailable || !InstallsPackages)
            return new UpdateInstallOutcome(false, FormatStatus(result));

        if (!UpdateInstallPlan.TryCreate(result, out var plan, out var error) || plan is null)
            return new UpdateInstallOutcome(false, error ?? UpdateChannelCopy.NoPackageUrl);

        if (!plan.IsLocalFile)
            progress?.Report("Downloading…");
        string? packagePath = null;
        var downloaded = false;
        try
        {
            packagePath = await new UpdatePackageFetcher().MaterializeAsync(
                plan,
                Path.Combine(UserDataRoots.LocalAppDataRoot, "updates"),
                cancellationToken).ConfigureAwait(false);
            downloaded = plan.LocalPath is null;
            return await WindowsUpdateInstaller.InstallAsync(
                packagePath, result.FeedVersion, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateInstallOutcome(false, "Could not install the update: " + ex.Message);
        }
        finally
        {
            if (downloaded && packagePath is not null && File.Exists(packagePath))
            {
                try
                {
                    File.Delete(packagePath);
                }
                catch (IOException)
                {
                    // Windows may still have the file open. The next download uses a new name.
                }
            }
        }
    }

    public static string FormatStatus(UpdateCheckResult result)
        => UpdateOffer.Status(result, Delivery);

    /// <summary>Opens temno.io in the browser, where the current release is (WP-451).</summary>
    public static void OpenDownloadPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = UpdateChannelCopy.DownloadPageUrl, UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser answered. The sentence beside the button already names the address.
        }
    }

    /// <summary>
    /// What the feed is compared with: the installed package's version, which is what Windows installed. The
    /// assembly's informational version is set elsewhere and does not move with it, so comparing that would
    /// offer an installed update again. A build folder has no package and uses the version its source names. A custom
    /// build always does, so one that merged a release stops being told about it (WP-402,
    /// <see cref="BuildProvenance.VersionToCompare"/>).
    /// </summary>
    public static string InstalledVersion()
        => BuildProvenance.Current.VersionToCompare(
            WindowsUpdateInstaller.InstalledIdentity()?.InstalledVersion.ToString(),
            AppInformationalVersion());

    /// <summary>
    /// The About box's version line, which Send Feedback repeats (WP-402). An official build shows the version it
    /// always did; a build from source shows the version its update check compares, with its source commit.
    /// </summary>
    public static string VersionLine()
        => BuildProvenance.Current.VersionLine(
            BuildProvenance.Current.IsOfficial ? AppInformationalVersion() : InstalledVersion().Split('+')[0]);

    private static string AppendHonesty(string message)
    {
        var honesty = UpdateChannelCopy.SideloadHonestyFor(HostedEndpoints.BuildEnvironment);
        return honesty.Length == 0 ? message : message + "\n\n" + honesty;
    }

    public static string AppInformationalVersion()
    {
        var version = typeof(App).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return string.IsNullOrWhiteSpace(version) ? "0.1.0-windows" : version.Trim();
    }

    private static TextBlock Note(string text, bool selectable = false)
        => new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.Text.PrimaryBrush,
            IsTextSelectionEnabled = selectable,
        };

    private static async Task ShowNoteAsync(XamlRoot xamlRoot, string title, string body, bool selectable = false)
    {
        var dialog = AppDialog.Create(xamlRoot);
        dialog.Title = title;
        dialog.Content = Note(body, selectable);
        dialog.CloseButtonText = "OK";
        await dialog.ShowAsync();
    }

    private async Task<UpdateCheckResult> CheckCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var prefs = UserPreferences.Load();
            var result = await new UpdateChecker().CheckAsync(
                prefs.ResolveUpdateFeedUrl(),
                InstalledVersion(),
                cancellationToken).ConfigureAwait(false);
            Publish(result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            var timedOut = new UpdateCheckResult
            {
                Kind = UpdateCheckKind.Error,
                Message = UpdateChecker.UnreachableMessage,
            };
            Publish(timedOut);
            return timedOut;
        }
        catch (Exception ex)
        {
            var result = new UpdateCheckResult
            {
                Kind = UpdateCheckKind.Error,
                Message = "Could not check for updates: " + ex.Message,
            };
            Publish(result);
            return result;
        }
    }

    private void Publish(UpdateCheckResult result)
    {
        LastResult = result;
        _lastCheckUtc = DateTime.UtcNow;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
