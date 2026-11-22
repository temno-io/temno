using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Account;
using Temno.Core.Agent;
using Temno.Core.Ui;
using Temno.Environments;
using Temno.Core.IO;
using Temno.Core.Updates;
using Temno.Media.Cache;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Search;
using Windows.ApplicationModel.DataTransfer;

namespace Temno.App.Controls;

public sealed partial class SettingsControl : UserControl
{
    private const string DevCertCommand =
        @".\scripts\new-dev-cert.ps1 -Password 'dev-only-pw' -Trust";

    private const string BuildMsixCommand =
        @".\scripts\build-msix.ps1 -Environment development -CertificatePath '.\artifacts\dev-cert\temno-dev.pfx' -CertificatePassword 'dev-only-pw' -Clean";

    private string _tab = "account";
    private bool _updateBusy;
    // Telemetry has no backend and no switch here; Save keeps whatever value is already stored.
    private bool _telemetryEnabled;
    // WP-397: the search engine settings have no controls here either; Save keeps the stored values.
    private bool _searchPreferOnnx = true;
    private string _searchOnnxModelPath = "";
    // WP-422: the model and reasoning effort a new project's first chat starts on ("" = the default model, High). The
    // menu's models come from the Gateway's catalog and may not have loaded (signed out); the saved choice is then
    // shown by name.
    private string _hostedChoice = "";
    private string _hostedEffort = "";
    private IReadOnlyList<HostedAgentProfile>? _hostedModels;

    /// <summary>WP-191: the pane to show first; the chat gate passes "agent". WP-121 opens Account.</summary>
    public string InitialTab { get; set; } = "account";
    private bool _loading = true;

    public SettingsControl()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loading = true;
            LoadFromDisk();
            ShowTab(InitialTab);
            _loading = false;
            _ = LoadHostedModelsAsync();
        };
    }

    public void Save()
    {
        SettingsPrefsMapper.Save(ReadDraft());
    }

    /// <summary>WP-309: a skill edit that closing would lose. Skills save on their own Save, not the dialog's.</summary>
    public bool HasUnsavedWork => SkillsPane.HasUnsavedWork;

    public void ShowUnsavedWarning()
    {
        ShowTab("skills");
        SkillsPane.ShowUnsavedWarning();
    }

    private void LoadFromDisk()
    {
        var draft = SettingsPrefsMapper.FromPreferences(UserPreferences.Load());
        AgentBaseUrlBox.Text = draft.AgentBaseUrl;
        AgentApiKeyBox.Password = draft.AgentApiKey;
        AgentModelBox.Text = draft.AgentModel;
        _hostedChoice = draft.AgentNewChatModel;
        _hostedEffort = draft.AgentNewChatEffort;
        RenderDefaultModel();
        GenerationBaseUrlBox.Text = draft.GenerationBaseUrl;
        GenerationApiKeyBox.Password = draft.GenerationApiKey;
        _telemetryEnabled = draft.TelemetryEnabled;
        _searchPreferOnnx = draft.PreferOnnx;
        _searchOnnxModelPath = draft.OnnxModelPath;
        SearchIndexStatusText.Visibility = Visibility.Collapsed;
        RefreshSearchIndex();
        UpdateFeedUrlBox.Text = draft.UpdateFeedUrl;
        // WP-376: production reads its compiled feed (download.temno.io), so the field is a development tool.
        UpdateFeedPanel.Visibility = HostedEndpoints.BuildEnvironment == TemnoEnvironment.Development
            ? Visibility.Visible
            : Visibility.Collapsed;
        // A build from source installs no package (WP-402), so the sideload signing sentence has nothing to describe.
        var honesty = AppUpdateService.InstallsPackages
            ? UpdateChannelCopy.SideloadHonestyFor(HostedEndpoints.BuildEnvironment)
            : "";
        UpdateHonestyText.Text = honesty;
        UpdateHonestyText.Visibility = string.IsNullOrEmpty(honesty) ? Visibility.Collapsed : Visibility.Visible;
        if (HostedEndpoints.BuildEnvironment == TemnoEnvironment.Development)
            ToolTipService.SetToolTip(TabUpdates, "Update feed and checks. Empty uses this build's own feed");
        PreferHardwareEncodeSwitch.IsOn = draft.PreferHardwareEncode;
        PreferByokSwitch.IsOn = draft.AgentPreferByok;
        // The version the update check compares (WP-356), so "Installed" and "You're up to date" agree.
        var version = AppUpdateService.InstalledVersion();
        CurrentVersionText.Text = "Installed version " + version.Split('+')[0];
        ToolTipService.SetToolTip(CurrentVersionText, version);
        AgentStatusText.Text = string.IsNullOrWhiteSpace(UserPreferences.Load().ResolveAgentApiKey())
            ? (HostedEndpoints.GatewayConfigured() && AccountSessionService.Current.IsSignedIn
                ? "Hosted chat is available while signed in."
                : "No chat model configured.")
            : "Chat model configured.";
        GenerationStatusText.Text =
            Temno.Mcp.ToolExecutor.IsGenerationConfigured() ? "Generation endpoint configured."
            : Temno.Mcp.ToolExecutor.IsGenerationEndpointMissing() ? Temno.Mcp.ToolExecutor.GenerationEndpointMissingMessage
            : "Generation is unavailable until a BYOK endpoint URL or a signed-in hosted gateway is configured.";
        ApplyUpdateResult(AppUpdateService.Current.LastResult);
        CacheStatusText.Text = "";
        // WP-215 Scope 4: which compositor ran must be answerable without reading a log file.
        // DescribeSelection answers before the first frame; Summary answers after it, and says so
        // when a fallback happened, so a machine quietly running the slow path is visible here.
        CompositorStatusText.Text = CompositorFactory.DescribeSelection();
        CompositorActivityText.Text = CompositorDiagnostics.Summary;
        // WP-216, same shape and same reason one row down: the two transports differ by 4.5x on a
        // 4K export, so a machine that quietly reverted to the standard pipe is a machine whose
        // export is four times slower with nothing on screen to say why.
        TransportStatusText.Text = ExportTransportDiagnostics.DescribeSelection();
        TransportActivityText.Text = ExportTransportDiagnostics.Summary;
        CopyStatus.Visibility = Visibility.Collapsed;
        // WP-264: the dev-certificate command and its password are a development-build affordance.
        // A production build exposes nothing certificate-related.
        DeveloperToolsExpander.Visibility = HostedEndpoints.BuildEnvironment == TemnoEnvironment.Development
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async Task LoadHostedModelsAsync()
    {
        var models = await AgentChatController.LoadCatalogModelsAsync();
        if (models is null || models.Count == 0)
            return;
        _hostedModels = models;
        RenderDefaultModel();
    }

    // The default model's ID: the one new projects start on with nothing chosen, from the loaded catalog or, before it
    // loads, the build's own default.
    private string HostedDefaultId() =>
        _hostedModels is { Count: > 0 } models
            ? HostedModelNames.DefaultFor(models.Where(m => m.Qualified))?.Id ?? HostedModelNames.DefaultModelId
            : HostedModelNames.DefaultModelId;

    private sealed record DefaultPick(string ModelId, string Effort);

    // The button names the model and effort new projects start on. Its menu lists the qualified models under their
    // creator, the default one marked "(default)", and each model opens a submenu of its reasoning levels (a
    // MenuFlyoutSubItem opens on hover, on click and on the right arrow). A saved model the Gateway cannot serve is named
    // as not available; with no catalog the menu is the default and the saved choice by name.
    private void RenderDefaultModel()
    {
        var defaultId = HostedDefaultId();
        var state = HostedModelNames.ChoiceState(_hostedModels, _hostedChoice);
        var profile = _hostedModels is { Count: > 0 } models
            ? HostedModelNames.ForChoice(models.Where(m => m.Qualified), _hostedChoice)
            : null;
        var effort = profile is null
            ? (_hostedEffort.Length > 0 ? _hostedEffort : "high")
            : HostedModelNames.EffortFor(profile, null, chatNamesModel: false, _hostedEffort);

        string text;
        if (state == HostedChoiceState.NotAvailable)
            text = HostedModelNames.LabelFor(_hostedChoice) + " (not available)";
        else
        {
            var label = profile is not null ? HostedModelNames.Label(profile) : HostedModelNames.LabelFor(_hostedChoice.Length > 0 ? _hostedChoice : defaultId);
            text = $"{label} · {AgentChatController.EffortLabel(effort)}";
            // The default is a model at a level, so "(default)" follows the level, and only while nothing is chosen.
            if (_hostedChoice.Length == 0 && _hostedEffort.Length == 0)
                text = HostedModelNames.WithDefaultMark(text);
        }
        AgentDefaultModelButton.Content = text;
        AutomationProperties.SetHelpText(AgentDefaultModelButton, text);
        AgentDefaultModelButton.Flyout = BuildDefaultModelMenu(state, profile, effort, defaultId);

        var fallback = HostedModelNames.LabelFor(profile?.Id ?? defaultId);
        var note = state == HostedChoiceState.NotAvailable
            ? $"{HostedModelNames.LabelFor(_hostedChoice)} is not available right now, so new projects use {fallback}."
            : "Used for new projects. Existing projects keep their last model and effort.";
        AgentHostedModelNote.Text = note;
        AutomationProperties.SetName(AgentHostedModelNote, note);
    }

    private MenuFlyout BuildDefaultModelMenu(HostedChoiceState state, HostedAgentProfile? selected, string effort, string defaultId)
    {
        // Opens below the button, inside the Temno window, and no taller than the cap or the room under the button:
        // a long list scrolls instead of running off the screen.
        var menu = new MenuFlyout();
        WindowPopout.AttachMenu(menu, AgentDefaultModelButton, PopoutSide.Bottom, PopoutAlignment.Center, maxCap: AppTheme.ComponentSize.MenuMaxHeight);
        if (state == HostedChoiceState.NotAvailable)
        {
            menu.Items.Add(NamedItem(new MenuFlyoutItem { Text = HostedModelNames.LabelFor(_hostedChoice) + " (not available)", IsEnabled = false, Style = RowStyle("DefaultModelMenuItemStyle") }));
            menu.Items.Add(GroupSeparator());
        }

        if (_hostedModels is not { Count: > 0 } models)
        {
            // No catalog: the efforts are not known, so the default and the saved choice are plain entries.
            var plain = NamedItem(new MenuFlyoutItem { Text = HostedModelNames.WithDefaultMark(HostedModelNames.LabelFor(defaultId)), Tag = new DefaultPick("", ""), Style = RowStyle("DefaultModelMenuItemStyle") });
            plain.Click += DefaultModelPlain_Click;
            menu.Items.Add(plain);
            if (_hostedChoice.Length > 0 && state == HostedChoiceState.CatalogUnknown)
            {
                var saved = NamedItem(new MenuFlyoutItem { Text = HostedModelNames.LabelFor(_hostedChoice), Tag = new DefaultPick(_hostedChoice, _hostedEffort), Style = RowStyle("DefaultModelMenuItemStyle") });
                saved.Click += DefaultModelPlain_Click;
                menu.Items.Add(saved);
            }
            return menu;
        }

        var first = true;
        foreach (var (creator, group) in HostedModelNames.Grouped(models.Where(m => m.Qualified)))
        {
            if (!first)
                menu.Items.Add(GroupSeparator());
            first = false;
            // A provider's name is smaller type that sits against the models it heads and apart from the group before.
            menu.Items.Add(NamedItem(new MenuFlyoutItem { Text = creator, IsEnabled = false, Style = RowStyle("DefaultModelMenuHeadingStyle") }));
            foreach (var model in group)
            {
                var isDefault = string.Equals(model.Id, defaultId, StringComparison.Ordinal);
                var label = isDefault ? HostedModelNames.WithDefaultMark(HostedModelNames.Label(model)) : HostedModelNames.Label(model);
                var sub = new MenuFlyoutSubItem { Text = label, Style = RowStyle("DefaultModelMenuSubItemStyle") };
                AutomationProperties.SetName(sub, label);
                foreach (var level in model.Efforts)
                {
                    var item = new RadioMenuFlyoutItem
                    {
                        Text = AgentChatController.EffortLabel(level),
                        GroupName = "DefaultModelEffort",
                        IsChecked = state != HostedChoiceState.NotAvailable && selected is not null
                            && string.Equals(selected.Id, model.Id, StringComparison.Ordinal)
                            && string.Equals(effort, level, StringComparison.OrdinalIgnoreCase),
                        Tag = new DefaultPick(model.Id, level),
                        Style = RowStyle("DefaultModelMenuLevelStyle"),
                    };
                    AutomationProperties.SetName(item, AgentChatController.EffortLabel(level));
                    item.Click += DefaultModelEffort_Click;
                    sub.Items.Add(item);
                }
                menu.Items.Add(sub);
            }
        }
        return menu;
    }

    // The rows' styles are in this control's XAML resources: their height and spacing come from their type, not from the stock
    // menu row's template (see DefaultModelMenuItemStyle).
    private Style RowStyle(string key) => (Style)Resources[key];

    // One hairline between groups with 4 DIP above it; the next heading's own 3 DIP follows it, so the line sits nearer the group it opens.
    private static MenuFlyoutSeparator GroupSeparator() =>
        new() { Margin = new Thickness(AppTheme.Spacing.None, AppTheme.Spacing.Xs, AppTheme.Spacing.None, AppTheme.Spacing.None) };

    private static MenuFlyoutItem NamedItem(MenuFlyoutItem item)
    {
        AutomationProperties.SetName(item, item.Text);
        return item;
    }

    // A model and a level of its own: choosing the default model at the level it starts at is saving nothing.
    private void DefaultModelEffort_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DefaultPick pick } || _hostedModels?.FirstOrDefault(m => m.Id == pick.ModelId) is not { } model)
            return;
        _hostedChoice = string.Equals(pick.ModelId, HostedDefaultId(), StringComparison.Ordinal) ? "" : pick.ModelId;
        _hostedEffort = string.Equals(pick.Effort, HostedModelNames.StartingEffort(model), StringComparison.OrdinalIgnoreCase) ? "" : pick.Effort;
        // The menu is closing: rebuild it after the click has finished.
        DispatcherQueue.TryEnqueue(RenderDefaultModel);
    }

    private void DefaultModelPlain_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DefaultPick pick })
            return;
        _hostedChoice = pick.ModelId;
        _hostedEffort = pick.Effort;
        DispatcherQueue.TryEnqueue(RenderDefaultModel);
    }

    private SettingsDraft ReadDraft() => new()
    {
        AgentBaseUrl = AgentBaseUrlBox.Text ?? "",
        AgentApiKey = AgentApiKeyBox.Password ?? "",
        AgentModel = AgentModelBox.Text ?? "",
        AgentNewChatModel = _hostedChoice,
        AgentNewChatEffort = _hostedEffort,
        GenerationBaseUrl = GenerationBaseUrlBox.Text ?? "",
        GenerationApiKey = GenerationApiKeyBox.Password ?? "",
        TelemetryEnabled = _telemetryEnabled,
        PreferOnnx = _searchPreferOnnx,
        OnnxModelPath = _searchOnnxModelPath,
        UpdateFeedUrl = UpdateFeedUrlBox.Text ?? "",
        PreferHardwareEncode = PreferHardwareEncodeSwitch.IsOn,
        AgentPreferByok = PreferByokSwitch.IsOn,
    };

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            ShowTab(id);
    }

    private static readonly string[] KnownTabs = ["account", "mcp", "agent", "skills", "generation", "search", "export", "privacy", "updates"];

    private void ShowTab(string id)
    {
        // T1 finding (WP-191): an unknown id must land somewhere, not collapse every pane.
        _tab = Array.IndexOf(KnownTabs, id) >= 0 ? id : "account";
        PaneAccount.Visibility = Vis("account");
        PaneMcp.Visibility = Vis("mcp");
        PaneAgent.Visibility = Vis("agent");
        PaneSkills.Visibility = Vis("skills");
        PaneGeneration.Visibility = Vis("generation");
        PaneSearch.Visibility = Vis("search");
        PaneExport.Visibility = Vis("export");
        PanePrivacy.Visibility = Vis("privacy");
        PaneUpdates.Visibility = Vis("updates");
        StyleTab(TabAccount, "account");
        StyleTab(TabMcp, "mcp");
        StyleTab(TabAgent, "agent");
        StyleTab(TabSkills, "skills");
        if (_tab == "skills")
            SkillsPane.Refresh();
        StyleTab(TabGeneration, "generation");
        StyleTab(TabSearch, "search");
        if (_tab == "search")
            RefreshSearchIndex();
        StyleTab(TabExport, "export");
        StyleTab(TabPrivacy, "privacy");
        StyleTab(TabUpdates, "updates");
    }

    private Visibility Vis(string id)
        => _tab == id ? Visibility.Visible : Visibility.Collapsed;

    private void StyleTab(Button button, string id)
    {
        var on = _tab == id;
        button.Foreground = on ? AppTheme.Accent.ReadableBrush : AppTheme.Text.SecondaryBrush;
        button.Background = on ? AppTheme.Accent.SelectedBrush : AppTheme.Background.SurfaceBrush;
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _updateBusy)
            return;

        Save();
        SetUpdateBusy(true, "Checking…");
        try
        {
            var result = await AppUpdateService.Current.CheckAsync(force: true);
            ApplyUpdateResult(result);
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private const string InstallUpdateLabel = "Install update";
    private const string InstallUpdateTip =
        "Downloads a remote package, or installs a local one, then asks Windows to install it. Unsigned packages are refused.";
    private const string DownloadPageTip = "Opens temno.io in your browser";

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _updateBusy)
            return;

        // WP-451: a copy that runs from a folder has nothing to install; the same button opens temno.io.
        if (AppUpdateService.Delivery == UpdateDelivery.DownloadsRelease)
        {
            AppUpdateService.OpenDownloadPage();
            return;
        }

        Save();
        SetUpdateBusy(true, "Checking…");
        try
        {
            var progress = new Progress<string>(text => UpdateStatusText.Text = text);
            var outcome = await AppUpdateService.Current.DownloadAndInstallAsync(progress: progress);
            SetUpdateStatus(
                outcome.Message,
                outcome.Installed ? AppTheme.Status.SuccessBrush : AppTheme.Status.ErrorBrush);
            if (outcome.Installed)
                InstallUpdateButton.Visibility = Visibility.Collapsed;
        }
        finally
        {
            SetUpdateBusy(false);
        }
    }

    private void SetUpdateBusy(bool busy, string? status = null)
    {
        _updateBusy = busy;
        CheckNowButton.IsEnabled = !busy;
        InstallUpdateButton.IsEnabled = !busy;
        UpdateProgress.IsActive = busy;
        UpdateProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status is not null)
            SetUpdateStatus(status, AppTheme.Text.SecondaryBrush);
    }

    private void SetUpdateStatus(string text, Microsoft.UI.Xaml.Media.Brush foreground)
    {
        UpdateStatusText.Text = text;
        UpdateStatusText.Foreground = foreground;
        AutomationProperties.SetName(UpdateStatusText, text);
    }

    private void ApplyUpdateResult(UpdateCheckResult? result)
    {
        if (result is null)
        {
            SetUpdateStatus(
                string.IsNullOrWhiteSpace(UserPreferences.Load().ResolveUpdateFeedUrl())
                    ? UpdateChecker.UnconfiguredMessage
                    : "",
                AppTheme.Text.TertiaryBrush);
            InstallUpdateButton.Visibility = Visibility.Collapsed;
            return;
        }

        SetUpdateStatus(AppUpdateService.FormatStatus(result), result.Kind switch
        {
            UpdateCheckKind.Error => AppTheme.Status.ErrorBrush,
            UpdateCheckKind.UpdateAvailable => AppTheme.Accent.TimecodeBrush,
            UpdateCheckKind.Unconfigured => AppTheme.Text.TertiaryBrush,
            _ => AppTheme.Status.SuccessBrush,
        });
        var opensDownloadPage = UpdateOffer.ShowsDownload(result, AppUpdateService.Delivery);
        InstallUpdateButton.Content = opensDownloadPage ? UpdateChannelCopy.DownloadPageLabel : InstallUpdateLabel;
        AutomationProperties.SetName(
            InstallUpdateButton, opensDownloadPage ? UpdateChannelCopy.DownloadPageLabel : InstallUpdateLabel);
        ToolTipService.SetToolTip(InstallUpdateButton, opensDownloadPage ? DownloadPageTip : InstallUpdateTip);
        InstallUpdateButton.Visibility = opensDownloadPage || UpdateOffer.ShowsInstall(result, AppUpdateService.Delivery)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var media = DiskCache.ClearTree(MediaCachePaths.Default.Root);
        var visual = DiskCache.ClearTree(new VisualSearchCache().RootDirectory);
        var result = CacheTreeClearResult.Combine(media, visual);
        CacheStatusText.Text = result.Describe();
        CacheStatusText.Foreground = result.FilesSkipped > 0
            ? AppTheme.Status.ErrorBrush
            : AppTheme.Text.SecondaryBrush;
    }

    private void RefreshSearchIndex()
    {
        var bytes = new VisualSearchCache().MeasureBytes();
        SearchIndexSizeText.Text = bytes > 0
            ? $"Uses {VisualSearchCache.FormatSize(bytes)} on this PC."
            : "Empty. It builds the first time you search in the Media panel.";
        AutomationProperties.SetName(SearchIndexSizeText, SearchIndexSizeText.Text);
        ClearSearchIndexButton.IsEnabled = bytes > 0;
    }

    private void ClearSearchIndex_Click(object sender, RoutedEventArgs e)
    {
        var result = DiskCache.ClearTree(new VisualSearchCache().RootDirectory);
        var text = result.FilesSkipped switch
        {
            0 => result.BytesFreed > 0
                ? $"Cleared {VisualSearchCache.FormatSize(result.BytesFreed)}. The index rebuilds the next time you search."
                : "Cleared. The index rebuilds the next time you search.",
            1 => "1 file is in use and was left. Try again when no search is running.",
            var n => $"{n} files are in use and were left. Try again when no search is running.",
        };
        SearchIndexStatusText.Text = text;
        SearchIndexStatusText.Foreground = result.FilesSkipped > 0
            ? AppTheme.Status.ErrorBrush
            : AppTheme.Text.SecondaryBrush;
        AutomationProperties.SetName(SearchIndexStatusText, text);
        SearchIndexStatusText.Visibility = Visibility.Visible;
        RefreshSearchIndex();
    }

    private void CopyDevCert_Click(object sender, RoutedEventArgs e)
        => Copy(DevCertCommand);

    private void CopyBuildMsix_Click(object sender, RoutedEventArgs e)
        => Copy(BuildMsixCommand);

    private void Copy(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        CopyStatus.Text = "Copied";
        CopyStatus.Visibility = Visibility.Visible;
    }
}
