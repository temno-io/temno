using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Temno.App.Services;
using Temno.Core.IO;
using Temno.Core.Session;
using Temno.Core.Ui;
using Temno.Mcp;
using Temno.Media.Transcription;

namespace Temno.App;

public partial class App : Application
{
    private Window? _window;
    private McpServer? _mcp;

    public App()
    {
        CrashLog.Install(this);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (!SingleInstance.TryClaim(ActivateExisting))
        {
            Environment.Exit(0);
            return;
        }

        // WP-193: first run copies the legacy Palmier Pro user-data roots to the Temno roots
        // (copy, never move; old roots left intact). Runs before anything reads the new roots.
        // A migration failure must never stop the app launching (fix round 1: an
        // UnauthorizedAccessException out of the copy used to propagate from OnLaunched).
        try
        {
            // WP-334: a development build seeds its own root first, so the newer shared state wins over Palmier Pro's.
            UserDataMigration.EnsureDevelopmentRootSeeded();
            UserDataMigration.EnsureMigrated();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[temno] User-data migration failed; continuing: {ex.Message}");
        }

        // WP-128: section expand/collapse survives app restarts via user-preferences.json.
        SectionExpandState.Store = new PreferencesSectionStore();

        // WP-309: the user's skills and the built-ins, for the agent's instructions in every mode and read_skill.
        Temno.Core.Agent.Skills.SkillLibrary.Current = Temno.Core.Agent.Skills.SkillStore.ForUser();

        // Debug-only: TEMNO_DEV_GALLERY=1 opens the WP-127 shared-control gallery instead of the
        // editor, so the control layer has a click-through surface. Default behaviour is unchanged.
        if (Views.DevControlGallery.Requested)
        {
            _window = new Views.DevControlGallery();
            _window.Closed += (_, _) => CrashLog.MarkWindowClosed();
            _window.Activate();
            return;
        }

        _window = new MainWindow();
        ProjectHub.ConfigureDispatcher(new WinUiProjectSessionDispatcher(_window.DispatcherQueue));
        ApplyMcpPreference();
        AccountSessionService.Current.Start();
        AccountSessionService.Current.Changed += (_, _) => ToolExecutor.StartHostedWatcher();
        AccountSessionService.Current.Changed += (_, _) => TranscriptionServices.ResumeCloudRequests();
        ToolExecutor.StartHostedWatcher();
        TranscriptionServices.ResumeCloudRequests();
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume)
                _window?.DispatcherQueue.TryEnqueue(ToolExecutor.WakeHostedWatcher);
        };
        _window.Closed += (_, _) =>
        {
            CrashLog.MarkWindowClosed();
            StopMcpListener();
            AccountSessionService.Current.Stop();
        };
        _window.Activate();
        _window.DispatcherQueue.TryEnqueue(ShowHostedNotices);
        _ = Task.Run(WarmWhisperModelAsync);
    }

    private void ActivateExisting()
    {
        if (_window is null)
            return;
        _window.DispatcherQueue.TryEnqueue(() =>
        {
            _window.Activate();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            SetForegroundWindow(hwnd);
            ToolExecutor.WakeHostedWatcher();
        });
    }

    private async void ShowHostedNotices()
    {
        try
        {
            if (_window?.Content is not FrameworkElement root)
                return;
            if (root.XamlRoot is null)
            {
                void OnLoaded(object sender, RoutedEventArgs e)
                {
                    root.Loaded -= OnLoaded;
                    ShowHostedNotices();
                }
                root.Loaded += OnLoaded;
                return;
            }
            var notices = ToolExecutor.TakeHostedNotices();
            if (notices.Count == 0)
                return;
            var dialog = new ContentDialog
            {
                Title = "Generation",
                Content = string.Join("\n\n", notices),
                CloseButtonText = "OK",
                XamlRoot = root.XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // A notice must not stop the editor opening.
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public static Window? MainAppWindow { get; internal set; }

    /// <summary>Loopback MCP HTTP endpoint when in-process bind succeeded; otherwise null.</summary>
    public static string? McpEndpoint { get; private set; }

    /// <summary>Persisted POST token (Help → MCP Instructions). Present even when the listener is stopped.</summary>
    public static string? McpAuthToken { get; private set; }

    /// <summary>Bind failure (port in use, URL ACL); in-process Tools still work.</summary>
    public static string? McpStartError { get; private set; }

    public static bool McpHttpRunning { get; private set; }

    /// <summary>Mac UserDefaults mcp.enabled — default on.</summary>
    public static bool McpPreferenceEnabled { get; private set; } = true;

    public static void SetMcpEnabled(bool enabled)
    {
        var prefs = McpPreferences.LoadOrCreate();
        prefs.Enabled = enabled;
        McpPreferences.Save(prefs);
        ((App)Current).ApplyMcpPreference();
    }

    public static void RotateMcpAuthToken()
    {
        var prefs = McpPreferences.LoadOrCreate();
        prefs.AuthToken = McpPreferences.NewAuthToken();
        McpPreferences.Save(prefs);
        ((App)Current).ApplyMcpPreference();
    }

    private void ApplyMcpPreference()
    {
        var prefs = McpPreferences.LoadOrCreate();
        McpPreferenceEnabled = prefs.Enabled;
        McpAuthToken = prefs.AuthToken;
        StopMcpListener();
        if (!prefs.Enabled)
            return;
        TryStartMcp(prefs.AuthToken);
    }

    private void TryStartMcp(string authToken)
    {
        McpHttpRunning = false;
        McpEndpoint = null;
        McpStartError = null;
        try
        {
            _mcp = new McpServer(authToken: authToken);
            _mcp.Start();
            McpHttpRunning = true;
            McpEndpoint = McpDefaults.Endpoint;
            McpAuthToken = _mcp.AuthToken;
        }
        catch (Exception ex)
        {
            _mcp = null;
            McpStartError = ex.Message;
        }
    }

    private void StopMcpListener()
    {
        try
        {
            _mcp?.Stop();
        }
        catch
        {
            // Listener may already be torn down with the process.
        }

        _mcp = null;
        McpHttpRunning = false;
        McpEndpoint = null;
    }

    private static async Task WarmWhisperModelAsync()
    {
        try
        {
            await WhisperModelStore.EnsureDefaultAsync().ConfigureAwait(false);
        }
        catch
        {
            // First Captions Generate / get_transcript will retry and surface the error.
        }
    }
}
