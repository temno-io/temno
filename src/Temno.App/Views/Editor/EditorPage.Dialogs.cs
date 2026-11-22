using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Temno.App.Controls;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.App.ViewModels;
using Temno.App.Views;
using Temno.Core.Captions;
using Temno.Core.Editing;
using Temno.Core.Export;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Updates;
using Temno.Environments;
using Temno.Core.Session;
using Temno.Core.Transcription;
using Temno.Core.Ui;
using Temno.Mcp;
using Temno.Media;
using Temno.Media.Audio;
using Temno.Media.Cache;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Hardware;
using Temno.Media.Search;
using Temno.Media.Transcription;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Temno.App.Views.Editor;

public sealed partial class EditorPage : Page
{
    private void MenuMaximize_Click(object sender, RoutedEventArgs e) => ToggleMaximizeFocused();

    private void MenuRestoreLayout_Click(object sender, RoutedEventArgs e)
    {
        // Always exit maximize and force a usable Default (Media + Preview + Inspector).
        if (_maximized) RestoreLayoutFromMaximize();
        ApplyLayoutPreset(EditorLayoutPreset.Default);
        // Default seed historically hid agent; ensure media/inspector on after recovery.
        _vm.MediaVisible = true;
        _vm.InspectorVisible = true;
        EnsurePreviewInUpperGrid();
        MediaCol.Width = new GridLength(_mediaWidth > 10 ? _mediaWidth : EditorLayoutPreferences.DefaultMediaWidth);
        InspectorCol.Width = new GridLength(_inspectorWidth > 10 ? _inspectorWidth : 300);
        ApplyPanelVisibility();
        ScheduleLayoutPersist();
        StatusText.Text = "Layout restored · Default";
    }

    private void MenuLayoutDefault_Click(object sender, RoutedEventArgs e)
        => ApplyLayoutPreset(EditorLayoutPreset.Default);

    private void MenuLayoutMedia_Click(object sender, RoutedEventArgs e)
        => ApplyLayoutPreset(EditorLayoutPreset.Media);

    private void MenuLayoutVertical_Click(object sender, RoutedEventArgs e)
        => ApplyLayoutPreset(EditorLayoutPreset.Vertical);

    private void MenuLayoutPreview_Click(object sender, RoutedEventArgs e)
        => ApplyLayoutPreset(EditorLayoutPreset.Preview);

    private async void MenuShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var dialog = AppDialog.Create(XamlRoot);
        dialog.Title = "Keyboard shortcuts";
        dialog.Content = BuildShortcutsContent();
        dialog.CloseButtonText = "Close";
        await dialog.ShowAsync();
    }

    /// <summary>
    /// WP-187: the shortcut list grouped by what the keys do, with an action column and a key column
    /// that line up. Two columns of groups so the dialog fits the 1366×768 window.
    /// </summary>
    private UIElement BuildShortcutsContent()
    {
        (string Title, (string Keys, string Action)[] Rows)[] groups =
        [
            ("Transport",
            [
                ("Space", "Play / pause"),
                ("J / K / L", "Step back / pause / step forward"),
                ("← / →", "Step one frame"),
                ("Ctrl+← / →", "Step about one second"),
                ("Home", "Go to start"),
            ]),
            ("Editing",
            [
                ("B", "Split selection (and linked A/V) at playhead"),
                ("M", "Add marker at playhead"),
                ("C  or  3", "Blade tool — click a clip to cut"),
                ("V  or  1", "Select tool"),
                ("2", "Trim tool"),
                ("Q / W", "Trim start / end to playhead"),
                ("Delete", "Delete selection (ripple when Ripple is on)"),
                ("Ctrl+Z", "Undo"),
                ("Ctrl+Y  or  Ctrl+Shift+Z", "Redo"),
                ("Ctrl+L / Ctrl+Shift+L", "Link / unlink selection"),
            ]),
            ("Selection",
            [
                ("A", "Select forward on the current track"),
                ("Shift+A", "Select forward on all tracks"),
                ("Ctrl+scroll", "Zoom the timeline at the pointer"),
                ("Shift+scroll  or  Alt+scroll", "Pan the timeline"),
            ]),
            ("Panels",
            [
                ("`", "Maximize the focused panel"),
                ("Esc", "Restore the layout, or leave full screen"),
            ]),
            ("Project",
            [
                ("Ctrl+N", "New project"),
                ("Ctrl+O", "Open project"),
                ("Ctrl+S", "Save"),
                ("Ctrl+I", "Import media"),
                ("Ctrl+M", "Export"),
            ]),
        ];

        var columns = new Grid { ColumnSpacing = AppTheme.Spacing.Xl };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Spacing = AppTheme.Spacing.Lg };
        var right = new StackPanel { Spacing = AppTheme.Spacing.Lg };
        Grid.SetColumn(right, 1);
        columns.Children.Add(left);
        columns.Children.Add(right);

        for (var g = 0; g < groups.Length; g++)
        {
            var (title, rows) = groups[g];
            var host = g < 2 ? left : right;
            var section = new StackPanel { Spacing = AppTheme.Spacing.Sm };
            section.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = AppTheme.FontSize.Md,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = AppTheme.Text.SecondaryBrush,
            });
            var table = new Grid { ColumnSpacing = AppTheme.Spacing.Md, RowSpacing = AppTheme.Spacing.Xs };
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (var r = 0; r < rows.Length; r++)
            {
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var action = new TextBlock
                {
                    Text = rows[r].Action,
                    FontSize = AppTheme.FontSize.SmMd,
                    Foreground = AppTheme.Text.PrimaryBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                };
                Grid.SetRow(action, r);
                table.Children.Add(action);
                var keys = new Border
                {
                    Background = AppTheme.Background.RaisedBrush,
                    BorderBrush = AppTheme.Border.SubtleBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(AppTheme.Radius.XsSm),
                    Padding = new Thickness(AppTheme.Spacing.Sm, AppTheme.Spacing.Xxs, AppTheme.Spacing.Sm, AppTheme.Spacing.Xxs),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock
                    {
                        Text = rows[r].Keys,
                        FontSize = AppTheme.FontSize.Sm,
                        Foreground = AppTheme.Text.PrimaryBrush,
                    },
                };
                Grid.SetRow(keys, r);
                Grid.SetColumn(keys, 1);
                table.Children.Add(keys);
            }
            section.Children.Add(table);
            host.Children.Add(section);
        }

        var body = new StackPanel { Spacing = AppTheme.Spacing.Lg };
        body.Children.Add(columns);
        body.Children.Add(new TextBlock
        {
            Text = "Letter and Delete keys do not run while typing, or from lists and buttons. "
                 + "Space still plays and pauses when the preview or timeline is focused. "
                 + "Ctrl+Z and Ctrl+Y in a text field undo the text, not the timeline.",
            FontSize = AppTheme.FontSize.Sm,
            Foreground = AppTheme.Text.TertiaryBrush,
            TextWrapping = TextWrapping.Wrap,
        });
        var windowHeight = App.MainAppWindow?.AppWindow?.Size.Height ?? 800;
        return new ScrollViewer
        {
            Content = body,
            MaxHeight = Math.Clamp(windowHeight - 200, 360, 640),
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private async void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        var wordmark = new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/TemnoWordmark.png")),
            Width = AppTheme.ComponentSize.WordmarkWidth,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(wordmark, "Temno");
        var content = new StackPanel { Spacing = AppTheme.Spacing.SmMd };
        content.Children.Add(wordmark);
        content.Children.Add(AboutLine("Temno for Windows", AppTheme.FontSize.MdLg, AppTheme.Text.PrimaryBrush));
        content.Children.Add(AboutLine(
            AppUpdateService.VersionLine(),
            AppTheme.FontSize.SmMd,
            AppTheme.Text.SecondaryBrush));
        content.Children.Add(AboutLine(
            "Native WinUI 3 + FFmpeg editor\nReads .palmier, writes .temno",
            AppTheme.FontSize.SmMd,
            AppTheme.Text.SecondaryBrush));
        content.Children.Add(AboutLine(
            "© 2026 Temno",
            AppTheme.FontSize.SmMd,
            AppTheme.Text.SecondaryBrush));
        // The GPL's no-warranty terms are in the License the links open; the box does not repeat them (owner, 2026-11-05).
        content.Children.Add(AboutLine(
            "Licensed under the GNU General Public License, version 3.",
            AppTheme.FontSize.SmMd,
            AppTheme.Text.TertiaryBrush));
        content.Children.Add(AboutLinks());
        var dialog = AppDialog.Create(XamlRoot);
        dialog.Title = "About Temno";
        dialog.Content = content;
        dialog.CloseButtonText = "OK";
        await dialog.ShowAsync();
    }

    private static TextBlock AboutLine(string text, double size, Microsoft.UI.Xaml.Media.Brush brush)
        => new()
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };

    private static TextBlock AboutLinks()
    {
        var links = new TextBlock
        {
            FontSize = AppTheme.FontSize.SmMd,
            TextWrapping = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(links, "About links");
        AddAboutWebLink(links, "Source", UpdateChannelCopy.SourceRepositoryUrl);
        AddAboutFileLink(links, "License", "LICENSE");
        AddAboutFileLink(links, "Notice", "NOTICE");
        AddAboutFileLink(links, "Third-party notices", "THIRD-PARTY-NOTICES.md");
        AddAboutWebLink(links, "Terms", "https://temno.io/terms");
        AddAboutWebLink(links, "Privacy", "https://temno.io/privacy");
        return links;
    }

    private static void AddAboutWebLink(TextBlock host, string label, string url)
    {
        if (host.Inlines.Count > 0)
            host.Inlines.Add(new Run { Text = "  ·  " });
        var link = new Hyperlink { NavigateUri = new Uri(url) };
        link.Inlines.Add(new Run { Text = label });
        host.Inlines.Add(link);
    }

    private static void AddAboutFileLink(TextBlock host, string label, string fileName)
    {
        if (host.Inlines.Count > 0)
            host.Inlines.Add(new Run { Text = "  ·  " });
        var link = new Hyperlink();
        link.Inlines.Add(new Run { Text = label });
        link.Click += async (_, _) =>
        {
            var path = FindAboutFile(fileName);
            if (path is null)
                return;
            try { await OpenInstalledFileAsync(path); }
            catch (Exception ex) { Debug.WriteLine("[temno] About link failed: " + ex.Message); }
        };
        host.Inlines.Add(link);
    }

    private static string? FindAboutFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var hop = 0; hop <= 12 && dir is not null; hop++)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    private static async Task OpenInstalledFileAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            if (await Launcher.LaunchFileAsync(file))
                return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[temno] LaunchFileAsync failed: " + ex.Message);
        }

        var name = Path.GetFileName(path);
        if (!name.Contains('.'))
            name += ".txt";
        var tempDir = Path.Combine(Path.GetTempPath(), "Temno");
        Directory.CreateDirectory(tempDir);
        var temp = Path.Combine(tempDir, name);
        File.Copy(path, temp, overwrite: true);
        Process.Start(new ProcessStartInfo { FileName = temp, UseShellExecute = true });
    }

    // --- Mac menu-stack actions (MainMenu.swift parity) ---

    private void OnAppUpdateChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() => ApplyTitleBarUpdateChrome(AppUpdateService.Current.LastResult));

    private void ApplyTitleBarUpdateChrome(UpdateCheckResult? result)
    {
        var available = result?.Kind == UpdateCheckKind.UpdateAvailable;
        TitleBarUpdateButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
            return;

        var tip = !AppUpdateService.InstallsPackages
            ? AppUpdateService.FormatStatus(result!)
            : string.IsNullOrWhiteSpace(result!.Message) ? "A newer build is available" : result.Message;
        ToolTipService.SetToolTip(TitleBarUpdateButton, tip);
        AutomationProperties.SetName(TitleBarUpdateButton, UpdateOffer.Name(result!, AppUpdateService.Delivery));
    }

    private async void TitleBarUpdate_Click(object sender, RoutedEventArgs e)
        => await AppUpdateService.Current.PromptInstallAsync(XamlRoot);

    private async void MenuCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        UpdateCheckResult result;
        try
        {
            result = await AppUpdateService.Current.CheckAsync(force: true);
        }
        catch (Exception ex)
        {
            await ShowSimpleDialogAsync("Check for Updates", ex.Message);
            return;
        }

        if (result.Kind == UpdateCheckKind.UpdateAvailable)
        {
            await AppUpdateService.Current.PromptInstallAsync(XamlRoot);
            return;
        }

        await ShowSimpleDialogAsync("Check for Updates", AppUpdateService.FormatStatus(result));
    }

    private async void MenuSettings_Click(object sender, RoutedEventArgs e)
        => await OpenSettingsAsync();

    private async Task OpenSettingsAsync(string? tab = null)
    {
        if (!await SettingsDialog.ShowAsync(XamlRoot, tab))
            return;
        Agent.RefreshChatCapability();
        _ = AppUpdateService.Current.CheckAsync(force: true);
    }

    private void MenuQuit_Click(object sender, RoutedEventArgs e)
        => App.MainAppWindow?.Close();

    private async void MenuNewProject_Click(object sender, RoutedEventArgs e)
    {
        // WP-187 / WP-186: the same dialog as Home — name, optional canvas preset and frame rate.
        var request = await NewProjectDialog.ShowAsync(XamlRoot);
        if (request is null) return;
        if (!await ConfirmLeaveProjectAsync()) return;
        ReplaceProject(
            () => AppSession.Current.CreateNew(request.Name, request.Fps, request.Width, request.Height),
            "Created " + request.Name,
            "Could not create project");
    }

    private async void MenuNewFolder_Click(object sender, RoutedEventArgs e)
        => await CreateFolderInCurrentLocationAsync();

    private async void MenuOpenProject_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            picker.CommitButtonText = "Open Project";
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindowHandle());
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            if (!await ConfirmLeaveProjectAsync()) return;
            ReplaceProject(
                () => AppSession.Current.Open(folder.Path),
                "Opened " + folder.Name,
                "Open failed");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Open failed: " + ex.Message;
        }
    }

    private void MenuShareStub_Click(object sender, RoutedEventArgs e)
        => _ = ShowSimpleDialogAsync(
            "Share",
            "There is no system Share sheet for projects on this Windows build.\n\n" +
            "Use File → Reveal project in Explorer, or Export for deliverables.");

    private void MenuTrimStart_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.TryTrimStartToPlayhead()) RefreshAfterEdit();
        else StatusText.Text = "Trim Start: select a clip and place the playhead inside it";
    }

    private void MenuTrimEnd_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.TryTrimEndToPlayhead()) RefreshAfterEdit();
        else StatusText.Text = "Trim End: select a clip and place the playhead inside it";
    }

    private void MenuSelectForwardTrack_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.TrySelectForwardFromCurrentSelection(TimelineControl.SelectForwardScope.Track))
            StatusText.Text = "Select forward on track";
        else StatusText.Text = "Select a clip first";
    }

    private void MenuSelectForwardAll_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.TrySelectForwardFromCurrentSelection(TimelineControl.SelectForwardScope.AllTracks))
            StatusText.Text = "Select forward on all tracks";
        else StatusText.Text = "Select a clip first";
    }

    private async void MenuAccount_Click(object sender, RoutedEventArgs e)
        => await AccountDialog.ShowAsync(XamlRoot);

    private void OnAccountSessionChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(RefreshAccountButton);

    private void RefreshAccountButton()
    {
        if (AccountButton is null)
            return;
        var session = AccountSessionService.Current;
        var signedIn = session.IsSignedIn;
        var email = session.Email;
        HeaderAccountAvatar.Update(session.UserId, session.DisplayName, email, session.AvatarUrl);
        var tip = signedIn
            ? (string.IsNullOrWhiteSpace(email) ? "Account (signed in)" : "Account (" + email + ")")
            : "Account (not signed in)";
        ToolTipService.SetToolTip(AccountButton, tip);
        AutomationProperties.SetName(AccountButton, signedIn
            ? "Account, " + (session.DisplayName ?? email ?? "signed in") : "Account");
        Agent.RefreshChatCapability();
    }

    private void RefreshMenuCheckmarks()
    {
        // WP-187: the View toggles are ToggleMenuFlyoutItems; the check is structural, not a "✓" prefix.
        if (MenuItemMediaPanel is not null)
            MenuItemMediaPanel.IsChecked = _vm.MediaVisible;
        if (MenuItemInspectorPanel is not null)
            MenuItemInspectorPanel.IsChecked = _vm.InspectorVisible;
        if (MenuItemAgentPanel is not null)
            MenuItemAgentPanel.IsChecked = _vm.AgentVisible;
    }

    private async Task ShowSimpleDialogAsync(string title, string body)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            CloseButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    public async Task<bool> ConfirmLeaveProjectAsync()
    {
        var session = AppSession.Current.Session;
        if (session is null)
            return true;

        var action = await UnsavedChangesPrompt.AskAsync(XamlRoot, _vm.ProjectName, session.IsDirty);
        if (action == UnsavedChangesAction.Cancel)
            return false;
        if (action == UnsavedChangesAction.Save)
        {
            try
            {
                session.Save();
                _vm.RefreshFromSession();
                ProjectTitleText.Text = _vm.TitleBarLabel;
                StatusText.Text = "Saved";
            }
            catch (Exception ex)
            {
                ReportSaveFailure(ex.Message);
                return false;
            }
        }

        return true;
    }

    private void ReplaceProject(Func<ProjectSession> open, string okStatus, string failPrefix)
    {
        AppSession.Current.Close(save: false);
        try
        {
            open();
            BindSession(resetPreview: true);
            InstallEditorTitleBar();
            StatusText.Text = okStatus;
        }
        catch (Exception ex)
        {
            StatusText.Text = failPrefix + ": " + ex.Message;
            Frame?.Navigate(typeof(HomePage));
        }
    }

    public void ReportSaveFailure(string message)
    {
        StatusText.Text = "Save failed: " + message;
        _ = ShowSimpleDialogAsync("Save failed", message);
    }

    private void SaveProject()
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        try
        {
            session.Save();
            _vm.RefreshFromSession();
            ProjectTitleText.Text = _vm.TitleBarLabel;
            RefreshStatusPath();
            if (App.MainAppWindow is not null)
                App.MainAppWindow.Title = _vm.TitleBarLabel;
            StatusText.Text = "Saved";
        }
        catch (Exception ex)
        {
            ReportSaveFailure(ex.Message);
        }
    }

    private void StartAutosaveTimer()
    {
        _autosaveTimer ??= DispatcherQueue.CreateTimer();
        _autosaveTimer.Interval = TimeSpan.FromSeconds(60);
        _autosaveTimer.IsRepeating = true;
        _autosaveTimer.Tick -= AutosaveTimer_Tick;
        _autosaveTimer.Tick += AutosaveTimer_Tick;
        _autosaveTimer.Start();
    }

    private void StopAutosaveTimer()
    {
        if (_autosaveTimer is null) return;
        _autosaveTimer.Stop();
        _autosaveTimer.Tick -= AutosaveTimer_Tick;
    }

    private void AutosaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        var session = AppSession.Current.Session;
        if (session is null || !session.IsDirty || session.PackagePath is null)
            return;
        try
        {
            session.Save();
            _vm.RefreshFromSession();
            ProjectTitleText.Text = _vm.TitleBarLabel;
            if (App.MainAppWindow is not null)
                App.MainAppWindow.Title = _vm.TitleBarLabel;
            StatusText.Text = "Autosaved";
            RefreshStatusPath();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Autosave failed: " + ex.Message;
        }
    }

    private void MenuRippleDelete_Click(object sender, RoutedEventArgs e)
    {
        var prev = EditorEditContext.Current.RippleDelete;
        EditorEditContext.Current.RippleDelete = true;
        try
        {
            if (Timeline.TryDeleteSelection())
            {
                RefreshAfterEdit();
                StatusText.Text = "Ripple delete";
            }
            else StatusText.Text = "Nothing selected to ripple-delete";
        }
        finally
        {
            EditorEditContext.Current.RippleDelete = prev;
        }
    }

    private void MenuFullScreen_Click(object sender, RoutedEventArgs e)
    {
        var window = App.MainAppWindow;
        if (window?.AppWindow is null) return;
        if (window.AppWindow.Presenter is FullScreenPresenter)
        {
            window.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            StatusText.Text = "Window restored";
        }
        else
        {
            window.AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            StatusText.Text = "Full screen (Esc to exit)";
        }
        ScheduleTimelineLayoutRefresh();
    }

    private async void MenuTutorial_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Tutorial",
            Content = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text =
                        "1. Import media (+ Import) or drag files into the Media panel.\n" +
                        "2. Drag clips onto the timeline; use Select / Blade tools.\n" +
                        "3. Preview with Space; edit with B (blade), Del, Ctrl+Z.\n" +
                        "4. Toggle Agent from the chat bubble in the title bar.\n" +
                        "5. Export from Export (title bar) or Menu → File → Export…\n" +
                        "6. View → Layout for Default / Media / Vertical presets.\n" +
                        "7. Menu (left) opens the full Mac-style command stack.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                },
                MaxHeight = 360,
            },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async void MenuMcpInstructions_Click(object sender, RoutedEventArgs e)
        => await ShowMcpSetupDialogAsync("MCP Server", new McpSetupControl());

    private async Task ShowMcpSetupDialogAsync(string title, UIElement content)
    {
        var dialog = AppDialog.Create(XamlRoot);
        dialog.Title = title;
        dialog.Content = new ScrollViewer
        {
            Content = content,
            MaxHeight = 560,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        dialog.CloseButtonText = "Close";
        await dialog.ShowAsync();
    }

    private async void MenuFeedback_Click(object sender, RoutedEventArgs e)
    {
        object content = "Share feedback with the Temno team via your project channel or repository issues.\nWindows build: native WinUI shell.";
        // WP-402: support needs to know a report comes from a build from source, so it carries the About box's version
        // line, in text that can be copied into the report.
        if (!BuildProvenance.Current.IsOfficial)
        {
            content = new TextBlock
            {
                Text = content + "\n" + AppUpdateService.VersionLine(),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            };
        }
        var dialog = new ContentDialog
        {
            Title = "Send Feedback",
            Content = content,
            PrimaryButtonText = "OK",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }
}
