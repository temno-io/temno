using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.App.Views;
using Temno.App.Views.Editor;
using Temno.Core.Account;
using Temno.Environments;
using Windows.Graphics;

namespace Temno.App;

public sealed partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _closePromptOpen;
    private double _windowScale = 1;

    public MainWindow()
    {
        InitializeComponent();

        App.MainAppWindow = this;

        ExtendsContentIntoTitleBar = true;
        UseHomeTitleBar();

        try
        {
            // Prefer absolute path so shortcuts / publish folders still resolve the icon.
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            AppWindow.SetIcon(System.IO.File.Exists(iconPath) ? iconPath : "Assets/AppIcon.ico");
        }
        catch
        {
            // Icon optional when unpackaged path differs.
        }

        AppWindow.Resize(new SizeInt32(
            (int)AppTheme.Window.HomeDefaultWidth,
            (int)AppTheme.Window.HomeDefaultHeight));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.PreferredMinimumWidth = (int)AppTheme.Window.HomeMinWidth;
            presenter.PreferredMinimumHeight = (int)AppTheme.Window.HomeMinHeight;
        }

        AppWindow.Closing += AppWindow_Closing;
        Activated += MainWindow_Activated;
        RootFrame.Loaded += (_, _) =>
        {
            RootFrame.XamlRoot.Changed += WindowRoot_Changed;
            ApplyWindowSizing(RootFrame.Content is EditorPage, initial: true);
        };
        Closed += (_, _) =>
        {
            if (RootFrame.XamlRoot is { } root) root.Changed -= WindowRoot_Changed;
        };

        RootFrame.Navigated += (_, args) =>
        {
            // Editor installs its own Mac-style title bar; home restores the quiet brand strip.
            if (args.Content is not EditorPage)
                UseHomeTitleBar();
            ApplyWindowSizing(args.Content is EditorPage);
        };

        RootFrame.Navigate(typeof(HomePage));
    }

    private void WindowRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (Math.Abs(sender.RasterizationScale - _windowScale) > .01)
            ApplyWindowSizing(RootFrame.Content is EditorPage);
    }

    public void ApplyWindowSizing(bool editor, bool initial = false)
    {
        if (RootFrame.XamlRoot is not { } root || AppWindow.Presenter is not OverlappedPresenter presenter)
            return;
        _windowScale = root.RasterizationScale;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        var client = AppWindow.ClientSize;
        // XAML tokens are effective pixels; AppWindow and its resize limits use device pixels.
        int Width(double value) => Math.Min(work.Width, (int)Math.Ceiling(value * _windowScale) + size.Width - client.Width);
        int Height(double value) => Math.Min(work.Height, (int)Math.Ceiling(value * _windowScale) + size.Height - client.Height);
        var minWidth = Width(editor ? AppTheme.Window.ProjectMinWidth : AppTheme.Window.HomeMinWidth);
        var minHeight = Height(editor ? AppTheme.Window.ProjectMinHeight : AppTheme.Window.HomeMinHeight);
        presenter.PreferredMinimumWidth = minWidth;
        presenter.PreferredMinimumHeight = minHeight;
        if (presenter.State != OverlappedPresenterState.Restored) return;
        var width = initial ? Width(AppTheme.Window.HomeDefaultWidth) : Math.Clamp(size.Width, minWidth, work.Width);
        var height = initial ? Height(AppTheme.Window.HomeDefaultHeight) : Math.Clamp(size.Height, minHeight, work.Height);
        var position = AppWindow.Position;
        var x = Math.Clamp(position.X, work.X, work.X + work.Width - width);
        var y = Math.Clamp(position.Y, work.Y, work.Y + work.Height - height);
        if (width != size.Width || height != size.Height || x != position.X || y != position.Y)
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void HomeSettings_Click(object sender, RoutedEventArgs e)
    {
        if (RootFrame.Content is HomePage home) home.OpenSettings();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            return;
        _ = AppUpdateService.Current.CheckIfStaleAsync();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        if (_closePromptOpen)
            return;

        _closePromptOpen = true;
        // Closing has no async deferral. Return to the native close/automation caller before
        // hiding popups, changing the session, or requesting the final window teardown.
        if (!DispatcherQueue.TryEnqueue(async () => await CompleteCloseAsync()))
            _closePromptOpen = false;
    }

    private async Task CompleteCloseAsync()
    {
        try
        {
            await AppDialog.DismissOpenAsync(RootFrame.XamlRoot);
            if (RootFrame.Content is EditorPage editor)
            {
                if (!await editor.ConfirmLeaveProjectAsync())
                    return;

                AppSession.Current.Close(save: false);
            }

            await Temno.Media.Transcription.TranscriptionServices.StopCloudRequestsAsync();
            DispatcherQueue.TryEnqueue(() =>
            {
                _allowClose = true;
                Close();
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write("MainWindow.Close", ex);
            if (RootFrame.Content is EditorPage editor)
                editor.ReportSaveFailure(ex.Message);
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    /// <summary>Home: single quiet title strip (no "Windows" badge). Caption buttons remain OS-native on the right.</summary>
    public void UseHomeTitleBar()
    {
        AppTitleBar.Visibility = Visibility.Visible;
        SetTitleBar(HomeDragRegion);
        Title = HomeWindowTitle;
    }

    /// <summary>
    /// WP-260: a development build says so in the home window's name; WP-334 names it as the Start menu does,
    /// Temno-Dev. The editor's title is the project name, which EditorPage rewrites on every edit, so the Account
    /// panel names the backend there.
    /// </summary>
    private static string HomeWindowTitle
        => HostedEndpoints.BuildEnvironment == TemnoEnvironment.Development ? "Temno-Dev" : "Temno";

    /// <summary>
    /// Editor: Mac TitleBarLeading/Trailing inverted for Windows —
    /// drag region is the editor chrome; reserve right inset for caption buttons.
    /// </summary>
    public void UseEditorTitleBar(UIElement editorTitleBar, string windowTitle)
    {
        AppTitleBar.Visibility = Visibility.Collapsed;
        SetTitleBar(editorTitleBar);
        Title = windowTitle;
    }
}
