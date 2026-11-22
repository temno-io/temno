using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.App.ViewModels;
using Temno.App.Views.Editor;
using Temno.Core.Updates;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Temno.App.Views;

public sealed partial class HomePage : Page
{
    // WP-203: the unfiltered registry load; ApplyFilterAndSort projects it into ProjectsGrid.
    private List<ProjectItemViewModel> _allProjects = [];

    // WP-203: SortBox.SelectionChanged fires during InitializeComponent, before the rest of the
    // tree (ProjectsGrid, states) exists. The filter/sort handlers stay silent until construction
    // is done; ApplyFilterAndSort null-guards anyway so a stray event can never crash launch.
    private bool _treeReady;

    public HomePage()
    {
        InitializeComponent();
        _treeReady = true;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        AppSession.Current.SessionChanged += OnAppSessionChanged;
        AppUpdateService.Current.Changed += OnAppUpdateChanged;
        ApplyUpdateSidebar(AppUpdateService.Current.LastResult);
        _ = AppUpdateService.Current.CheckIfStaleAsync();
        if (AppSession.Current.Session is not null)
        {
            NavigateToEditor();
            return;
        }

        ReloadProjects();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        AppSession.Current.SessionChanged -= OnAppSessionChanged;
        AppUpdateService.Current.Changed -= OnAppUpdateChanged;
    }

    private void OnAppSessionChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (AppSession.Current.Session is not null)
                NavigateToEditor();
            else
                ReloadProjects();
        });
    }

    private void OnAppUpdateChanged(object? sender, EventArgs e)
        => DispatcherQueue.TryEnqueue(() => ApplyUpdateSidebar(AppUpdateService.Current.LastResult));

    private void ReloadProjects()
    {
        List<ProjectItemViewModel> items;
        try
        {
            items = AppSession.Current.LoadRecentProjects()
                .Select(e => new ProjectItemViewModel(e))
                .ToList();
        }
        catch (Exception ex)
        {
            ShowError("Could not load projects", ex.Message);
            return;
        }

        _allProjects = items;
        ApplyFilterAndSort();
    }

    /// <summary>
    /// WP-203: search (name or folder, case-insensitive substring) then the SortBox order.
    /// Three visible states: the grid, the WP-186 empty state (registry empty), or the
    /// no-results state (search matched nothing). Search/sort chrome shows only when the
    /// registry itself is non-empty — there is nothing to filter otherwise.
    /// </summary>
    private void ApplyFilterAndSort()
    {
        if (!_treeReady || ProjectsGrid is null)
            return;
        var query = SearchBox?.Text?.Trim() ?? "";
        IEnumerable<ProjectItemViewModel> view = _allProjects;
        if (query.Length > 0)
        {
            view = view.Where(i =>
                i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || i.Path.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var sort = (SortBox?.SelectedItem as ComboBoxItem)?.Tag as string ?? "opened";
        view = sort switch
        {
            "name" => view.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            "duration" => view.OrderByDescending(i => i.DurationSeconds).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
            _ => view.OrderByDescending(i => i.LastOpened),
        };

        var shown = view.ToList();
        ProjectsGrid.ItemsSource = shown;
        var registryEmpty = _allProjects.Count == 0;
        var noResults = !registryEmpty && shown.Count == 0;
        EmptyState.Visibility = registryEmpty ? Visibility.Visible : Visibility.Collapsed;
        NoResultsState.Visibility = noResults ? Visibility.Visible : Visibility.Collapsed;
        ProjectsGrid.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (NoResultsBody is not null && noResults)
            NoResultsBody.Text = $"Nothing in your project list matches “{query}”.";
        // WP-186: one New Project control on screen — the empty state's while empty, the header's otherwise.
        NewProjectButton.Visibility = registryEmpty ? Visibility.Collapsed : Visibility.Visible;
        SearchSortRow.Visibility = registryEmpty ? Visibility.Collapsed : Visibility.Visible;
        ProjectsHeading.Text = registryEmpty ? "Projects" : $"Projects · {shown.Count}";
    }

    private void HomeRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (HomeContent is not null)
            HomeContent.Padding = e.NewSize.Width < AppTheme.ComponentSize.HomeCompactBreakpoint
                ? AppTheme.Padding.R3PageCompact : AppTheme.Padding.R3Page;
    }

    public void OpenSettings() => Settings_Click(this, new RoutedEventArgs());

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => ApplyFilterAndSort();

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ApplyFilterAndSort();

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        ApplyFilterAndSort();
    }

    private void HomeRoot_DragOver(object sender, DragEventArgs e)
    {
        // WP-203: accept a dropped package folder; anything else gets the stock refusal cursor.
        e.AcceptedOperation = HasDroppedPackageAsync(e) ? DataPackageOperation.Link : DataPackageOperation.None;
        e.Handled = true;
    }

    private static bool HasDroppedPackageAsync(DragEventArgs e)
    {
        try { return e.DataView.Contains(StandardDataFormats.StorageItems); }
        catch { return false; }
    }

    private async void HomeRoot_DropAsync(object sender, DragEventArgs e)
    {
        IReadOnlyList<IStorageItem>? items = null;
        try { items = await e.DataView.GetStorageItemsAsync(); }
        catch (Exception ex)
        {
            ShowError("Could not open the dropped item", ex.Message);
            return;
        }

        var folder = items.OfType<StorageFolder>().FirstOrDefault();
        if (folder is null)
        {
            ShowError("Drop a project to open it", "Drop a .temno or .palmier package folder onto Home.");
            return;
        }

        OpenPackageFolder(folder.Path);
    }

    private void OpenPackageFolder(string folderPath)
    {
        try
        {
            AppSession.Current.Open(folderPath);
            NavigateToEditor();
        }
        catch (Exception ex)
        {
            ShowError("Could not open project", ex.Message);
        }
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
        => await CreateProjectInteractiveAsync();

    private async void EmptyNew_Click(object sender, RoutedEventArgs e)
        => await CreateProjectInteractiveAsync();

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
        => await OpenProjectPickerAsync();

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (!await SettingsDialog.ShowAsync(XamlRoot))
            return;
        _ = AppUpdateService.Current.CheckAsync(force: true);
    }

    private async void UpdateSidebar_Click(object sender, RoutedEventArgs e)
        => await AppUpdateService.Current.PromptInstallAsync(XamlRoot);

    private void UpdateSidebar_PointerEntered(object sender, PointerRoutedEventArgs e)
        => UpdateSidebarCard.Background = AppTheme.Background.ProminentBrush;

    private void UpdateSidebar_PointerExited(object sender, PointerRoutedEventArgs e)
        => UpdateSidebarCard.Background = AppTheme.Background.RaisedBrush;

    private void ApplyUpdateSidebar(UpdateCheckResult? result)
    {
        var available = result?.Kind == UpdateCheckKind.UpdateAvailable;
        UpdateSidebarCard.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UpdateSidebarCard.Background = AppTheme.Background.RaisedBrush;
        if (!available)
            return;

        UpdateSidebarVersion.Text = string.IsNullOrWhiteSpace(result!.FeedVersion)
            ? "New version available"
            : "Version " + result.FeedVersion;
        var tip = !AppUpdateService.InstallsPackages
            ? AppUpdateService.FormatStatus(result)
            : string.IsNullOrWhiteSpace(result.Message) ? "Click to install update" : result.Message;
        ToolTipService.SetToolTip(UpdateSidebarCard, tip);
        // WP-402, WP-451: in a build from source the card opens the merge note, and in a copy that runs from a folder
        // the note with temno.io, not an install.
        AutomationProperties.SetName(UpdateSidebarCard, AppUpdateService.InstallsPackages
            ? "Click to install update " + (result.FeedVersion ?? "")
            : UpdateOffer.Name(result, AppUpdateService.Delivery));
    }

    private void ProjectsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ProjectItemViewModel item)
            TryOpen(item);
    }

    private void OpenMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: ProjectItemViewModel item })
            TryOpen(item);
    }

    private void RevealMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: ProjectItemViewModel item })
            AppSession.RevealInExplorer(item.Path);
    }

    private void RemoveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: ProjectItemViewModel item })
            return;

        try
        {
            AppSession.Current.RemoveFromRegistry(item.Path);
            ReloadProjects();
        }
        catch (Exception ex)
        {
            ShowError("Could not remove project", ex.Message);
        }
    }

    private async Task CreateProjectInteractiveAsync()
    {
        // WP-186: name-only creation still works; canvas and frame rate presets are optional.
        var request = await NewProjectDialog.ShowAsync(XamlRoot);
        if (request is null)
            return;

        try
        {
            AppSession.Current.CreateNew(request.Name, request.Fps, request.Width, request.Height);
            NavigateToEditor();
        }
        catch (Exception ex)
        {
            ShowError("Could not create project", ex.Message);
        }
    }

    private async Task OpenProjectPickerAsync()
    {
        try
        {
            var window = App.MainAppWindow
                ?? throw new InvalidOperationException("Main window not ready.");
            var hwnd = WindowNative.GetWindowHandle(window);

            // Prefer folder package (.temno or legacy .palmier directory).
            var folderPicker = new FolderPicker();
            InitializeWithWindow.Initialize(folderPicker, hwnd);
            folderPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
            folderPicker.FileTypeFilter.Add("*");
            folderPicker.CommitButtonText = "Open Project";

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder is null)
                return;

            AppSession.Current.Open(folder.Path);
            NavigateToEditor();
        }
        catch (Exception ex)
        {
            ShowError("Could not open project", ex.Message);
        }
    }

    private void TryOpen(ProjectItemViewModel item)
    {
        try
        {
            if (!item.IsAccessible)
            {
                _ = ShowMissingProjectAsync(item);
                return;
            }

            AppSession.Current.Open(item.Path);
            NavigateToEditor();
        }
        catch (Exception ex)
        {
            ShowError("Could not open project", ex.Message);
        }
    }

    /// <summary>
    /// WP-203: the designed state for a missing project — locate it, remove the row, or keep it.
    /// The old path was an error InfoBar with no way forward; the card itself already reads
    /// "Missing" at reduced opacity, and this dialog is where the choice lives.
    /// </summary>
    private async Task ShowMissingProjectAsync(ProjectItemViewModel item)
    {
        var dialog = AppDialog.Create(XamlRoot);
        dialog.Title = "Project missing";
        dialog.Content = new TextBlock
        {
            Text = $"“{item.Name}” is no longer at:\n{item.Path}\n\nLocate the moved package, remove it from the list, or keep the entry.",
            TextWrapping = TextWrapping.WrapWholeWords,
        };
        dialog.PrimaryButtonText = "Locate…";
        dialog.SecondaryButtonText = "Remove from list";
        dialog.CloseButtonText = "Keep";
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Primary)
        {
            await OpenProjectPickerAsync();
        }
        else if (choice == ContentDialogResult.Secondary)
        {
            try
            {
                AppSession.Current.RemoveFromRegistry(item.Path);
                ReloadProjects();
            }
            catch (Exception ex)
            {
                ShowError("Could not remove project", ex.Message);
            }
        }
    }

    private void NavigateToEditor()
    {
        if (Frame is null) return;
        Frame.Navigate(typeof(EditorPage));
    }

    private void ShowError(string title, string message)
    {
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.IsOpen = true;
    }
}
