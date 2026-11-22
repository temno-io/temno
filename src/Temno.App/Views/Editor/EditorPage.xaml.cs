using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
    private readonly EditorShellViewModel _vm = new();
    private ProjectSession? _boundSession;
    // WP-153: the session this page is subscribed to for mutation signals, tracked separately
    // from _boundSession so the -= always targets the instance the += used.
    private ProjectSession? _signalSession;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _sessionRefreshTimer;
    private int _renderedRevision = -1;
    private readonly MediaImportService _importer = new();
    private readonly ThumbnailCache _thumbnailCache = new(MediaCachePaths.Default);
    private int _thumbnailGeneration;
    private double _agentWidth = EditorLayoutPreferences.DefaultAgentWidth;
    private double _mediaWidth = EditorLayoutPreferences.DefaultMediaWidth;
    private double _inspectorWidth = 300;
    private double _timelineHeight = EditorLayoutPreferences.DefaultTimelineHeight;
    private bool _syncingPlayhead;
    private int _mediaSearchGeneration;
    private bool _mediaSearchActive;
    private bool _syncingZoomSlider;
    private bool _layoutLoaded;
    // WP-185 responsive shell state. None of it is persisted as a preference.
    private bool _responsiveFitActive;
    private bool _rightDockShowsAgent;
    private bool _timelineHeightAuto = true;
    private bool _timelineFitPending;
    private bool _verticalTopology;
    private bool _previewOnSide;
    private EditorLayoutPreset _activePreset = EditorLayoutPreset.Default;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _layoutSaveTimer;
    private string _mediaRailTab = "Media";
    private string? _currentMediaFolderId;
    private IReadOnlyList<MediaEntryItem> _workspaceMediaItems = [];
    // WP-144: media library view mode + thumbnail size, persisted user-local.
    // Loaded at construction, not in OnLoaded. OnLoaded fires after the media workspace has already
    // been built on some open paths, so the persisted mode arrived too late to affect the list that
    // was already bound -- measured 2026-09-01: a fresh Load() returned Grouped while this field
    // still read Folders. A field initialiser has no ordering dependency.
    private MediaLibraryViewState _mediaViewState = MediaLibraryViewStore.Load();
    /// <summary>WP-158: the empty-state button offers the library root, not an import.</summary>
    private bool _mediaEmptyOffersLibraryRoot;
    // Held deliberately: ItemsSource = cvs.View does not keep the CollectionViewSource alive, and
    // IsSourceGrouped lives on the CVS. Letting it collect drops the grouping and the headers.
    private Microsoft.UI.Xaml.Data.CollectionViewSource? _mediaGroupedSource;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autosaveTimer;
    private bool _syncingToolbar;
    private UnitNumberField _minimumPauseField = null!;
    private UnitNumberField _speechPaddingField = null!;
    private const int PanelBusyDelayMs = 200;
    private CancellationTokenSource? _importCts;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _mediaBusyDelay;
    private Action? _mediaBusyCancel;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _audioBusyDelay;
    private Action? _audioBusyCancel;
    private int _audioBusyGeneration;

    private static readonly UnitFieldSpec MinimumPauseSpec = new()
    {
        Unit = "ms",
        Minimum = 0,
        Maximum = 10_000,
        Step = 10,
        UnitsPerPixel = 10,
        Default = SilenceDetectionOptions.DefaultMinimumPauseSeconds * 1000,
    };

    private static readonly UnitFieldSpec SpeechPaddingSpec = new()
    {
        Unit = "ms",
        Minimum = 0,
        Maximum = 5_000,
        Step = 10,
        UnitsPerPixel = 10,
        Default = SilenceDetectionOptions.DefaultSpeechPaddingSeconds * 1000,
    };

    // WP-71 focused panel + maximize
    private enum ShellPanel { Agent, Media, Preview, Inspector, Timeline }
    private ShellPanel _focusedPanel = ShellPanel.Timeline;
    private bool _maximized;
    private bool _suppressTimelineFocusOnSelectionChange;
    private bool _savedAgentVisible = true;
    private bool _savedMediaVisible = true;
    private bool _savedInspectorVisible = true;
    private double _savedAgentWidth = EditorLayoutPreferences.DefaultAgentWidth;
    private double _savedMediaWidth = EditorLayoutPreferences.DefaultMediaWidth;
    private double _savedInspectorWidth = EditorLayoutPreferences.DefaultInspectorWidth;
    private double _savedTimelineHeight = EditorLayoutPreferences.DefaultTimelineHeight;
    private bool _savedVerticalTopology;

    private readonly KeyEventHandler _rootKeyDownHandler;
    private readonly KeyEventHandler _rootPreviewKeyDownHandler;
    private readonly PointerEventHandler _panelPointerPressedHandler;
    private readonly PointerEventHandler _transportPointerReleasedHandler;
    private long _transportSpaceStamp;

    public EditorPage()
    {
        _rootKeyDownHandler = RootGrid_KeyDown;
        _rootPreviewKeyDownHandler = RootGrid_PreviewKeyDown;
        _panelPointerPressedHandler = Panel_PointerPressed;
        _transportPointerReleasedHandler = TransportPanel_PointerReleased;
        try { InitializeComponent(); }
        catch (Exception ex)
        {
            CrashLog.Write("EditorPage.InitializeComponent", ex);
            throw;
        }
        InitializeSilenceSettings();
        InitializeHeaderKeyboard();
        WindowPopout.AttachMenu(MediaViewOptionsFlyout, MediaViewOptionsBtn, PopoutSide.Bottom, PopoutAlignment.Right);
        Timeline.TransitionPreviewing += Timeline_TransitionPreviewing;
        Timeline.TransitionPreviewEnded += Timeline_TransitionPreviewEnded;
        UpperGrid.SizeChanged += (_, _) => FitVerticalPanels();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AgentSplitter.TargetColumn = InspectorCol;
        MediaSplitter.TargetColumn = MediaCol;
        InspectorSplitter.TargetColumn = InspectorCol;
        TimelineSplitter.TargetRow = TimelineRow;
        SidePreviewSplitter.TargetColumn = SidePreviewCol;
        AgentSplitter.ResizeCompleted += LayoutSplitter_ResizeCompleted;
        MediaSplitter.ResizeCompleted += LayoutSplitter_ResizeCompleted;
        InspectorSplitter.ResizeCompleted += LayoutSplitter_ResizeCompleted;
        TimelineSplitter.ResizeCompleted += LayoutSplitter_ResizeCompleted;
        SidePreviewSplitter.ResizeCompleted += LayoutSplitter_ResizeCompleted;
        Preview.PlaybackStateChanged += Preview_PlaybackStateChanged;
        Preview.CaptionOverlayPositionChanged += Preview_CaptionOverlayPositionChanged;
        Preview.FrameCaptured += Preview_FrameCaptured;

        // PreviewKeyDown: steal Left/Right from MediaPlayer / media ListView before they mark Handled.
        // Bubble KeyDown (handledEventsToo): Space/JKL and the rest still reach the page when a child has focus.
        RootGrid.AddHandler(UIElement.PreviewKeyDownEvent, _rootPreviewKeyDownHandler, handledEventsToo: true);
        RootGrid.AddHandler(UIElement.KeyDownEvent, _rootKeyDownHandler, handledEventsToo: true);
        RootGrid.CharacterReceived += RootGrid_CharacterReceived;
        // Interactive controls inside any panel mark PointerPressed handled, so listen with handledEventsToo on all five panels.
        AgentPanel.AddHandler(UIElement.PointerPressedEvent, _panelPointerPressedHandler, handledEventsToo: true);
        MediaPanel.AddHandler(UIElement.PointerPressedEvent, _panelPointerPressedHandler, handledEventsToo: true);
        PreviewPanel.AddHandler(UIElement.PointerPressedEvent, _panelPointerPressedHandler, handledEventsToo: true);
        InspectorPanel.AddHandler(UIElement.PointerPressedEvent, _panelPointerPressedHandler, handledEventsToo: true);
        TimelinePanel.AddHandler(UIElement.PointerPressedEvent, _panelPointerPressedHandler, handledEventsToo: true);
        TimelinePanel.AddHandler(UIElement.PointerReleasedEvent, _transportPointerReleasedHandler, handledEventsToo: true);
        PreviewPanel.AddHandler(UIElement.PointerReleasedEvent, _transportPointerReleasedHandler, handledEventsToo: true);

        if (App.MainAppWindow?.AppWindow is { } appWindow)
            appWindow.Changed += AppWindow_Changed;

        SyncMediaViewOptions();
        MediaSearchBox.PlaceholderText = "Search files and spoken words";
        // WP-144 (N4): whatever changes the displayed items, the count follows. This is the
        // invariant; the explicit SyncMediaCountLabel() calls are just the fast path.
        // Defence in depth, not the fix. A discriminating test (2026-08-30) disabled this line and
        // the label still read correctly, so the explicit SyncMediaCountLabel() calls are what
        // repair the measured case. Whether ItemCollection raises VectorChanged on an ItemsSource
        // *replace* was not established; this catches in-place mutation if it happens.
        // SyncMediaCountLabel no-ops when the text would not change, so a per-item burst on a
        // large library is a string compare, not a layout pass.
        MediaList.Items.VectorChanged += (_, _) => SyncMediaCountLabel();
        ToolTipService.SetToolTip(MediaSearchBox, VisualSearchQueryParser.SupportedGrammar);
        RefreshMenuCheckmarks();
        RootGrid.Focus(FocusState.Programmatic);
        AccountSessionService.Current.Changed += OnAccountSessionChanged;
        GenerationPanelRequests.Requested += OnGenerationPanelRequested;
        GenerationPanel.Submitted += GenerationPanel_Submitted;
        RefreshAccountButton();

        if (!_layoutLoaded)
        {
            LoadAndApplyPersistedLayout();
            _layoutLoaded = true;
            // WP-185: the first SizeChanged usually fires before the layout is loaded and is ignored;
            // run the shell fit once now so the timeline is sized to its tracks on open.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (RootGrid.ActualWidth > 0 && RootGrid.ActualHeight > 0)
                    ApplyResponsiveShell(RootGrid.ActualWidth, RootGrid.ActualHeight);
            });
        }

        ToolExecutor.ExportQueue.JobChanged += OnExportJobChanged;
        RefreshExportQueueChrome();
        ConfigureZoomSliderRange();
        SyncZoomSliderFromTimeline();
        SyncTimelineToolbar();
        EnsureCaptionForm();
        if (CaptionsPosXField is not null)
            AutomationProperties.SetName(CaptionsPosXField, "Caption position X");
        if (CaptionsPosYField is not null)
            AutomationProperties.SetName(CaptionsPosYField, "Caption position Y");
        ConfigureCaptionsAvailability();
        StartAutosaveTimer();
        ScheduleLayoutPersist();
        AppUpdateService.Current.Changed += OnAppUpdateChanged;
        ApplyTitleBarUpdateChrome(AppUpdateService.Current.LastResult);
        _ = AppUpdateService.Current.CheckIfStaleAsync();
    }

    /// <summary>
    /// Configure Slider range in code. XAML cannot set Minimum &gt; 0 while default Value is 0
    /// (RangeBase assignment throws — surfaces as "Could not create project" on EditorPage load).
    /// </summary>
    private void ConfigureZoomSliderRange()
    {
        _syncingZoomSlider = true;
        try
        {
            // Order: raise Maximum first, set Value into range, then raise Minimum last.
            TimelineZoomSlider.Maximum = TimelineGeometry.MaxPixelsPerFrame;
            TimelineZoomSlider.Value = TimelineGeometry.DefaultPixelsPerFrame;
            TimelineZoomSlider.Minimum = TimelineGeometry.MinPixelsPerFrame;
            TimelineZoomSlider.StepFrequency = 0.05;
            TimelineZoomSlider.SmallChange = 0.05;
            TimelineZoomSlider.LargeChange = 1.0;
        }
        finally
        {
            _syncingZoomSlider = false;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        GenerationPanelRequests.Requested -= OnGenerationPanelRequested;
        GenerationPanel.Submitted -= GenerationPanel_Submitted;
        AccountSessionService.Current.Changed -= OnAccountSessionChanged;
        ToolExecutor.ExportQueue.JobChanged -= OnExportJobChanged;
        AgentSplitter.ResizeCompleted -= LayoutSplitter_ResizeCompleted;
        MediaSplitter.ResizeCompleted -= LayoutSplitter_ResizeCompleted;
        InspectorSplitter.ResizeCompleted -= LayoutSplitter_ResizeCompleted;
        TimelineSplitter.ResizeCompleted -= LayoutSplitter_ResizeCompleted;
        SidePreviewSplitter.ResizeCompleted -= LayoutSplitter_ResizeCompleted;
        Preview.PlaybackStateChanged -= Preview_PlaybackStateChanged;
        Preview.CaptionOverlayPositionChanged -= Preview_CaptionOverlayPositionChanged;
        Preview.FrameCaptured -= Preview_FrameCaptured;
        RootGrid.RemoveHandler(UIElement.PreviewKeyDownEvent, _rootPreviewKeyDownHandler);
        RootGrid.RemoveHandler(UIElement.KeyDownEvent, _rootKeyDownHandler);
        RootGrid.CharacterReceived -= RootGrid_CharacterReceived;
        AgentPanel.RemoveHandler(UIElement.PointerPressedEvent, _panelPointerPressedHandler);
        MediaPanel.RemoveHandler(UIElement.PointerPressedEvent, _panelPointerPressedHandler);
        PreviewPanel.RemoveHandler(UIElement.PointerPressedEvent, _panelPointerPressedHandler);
        InspectorPanel.RemoveHandler(UIElement.PointerPressedEvent, _panelPointerPressedHandler);
        TimelinePanel.RemoveHandler(UIElement.PointerPressedEvent, _panelPointerPressedHandler);
        TimelinePanel.RemoveHandler(UIElement.PointerReleasedEvent, _transportPointerReleasedHandler);
        PreviewPanel.RemoveHandler(UIElement.PointerReleasedEvent, _transportPointerReleasedHandler);
        if (App.MainAppWindow?.AppWindow is { } appWindow)
            appWindow.Changed -= AppWindow_Changed;
        AppUpdateService.Current.Changed -= OnAppUpdateChanged;
        PersistLayoutNow();
        if (_layoutSaveTimer is not null)
        {
            _layoutSaveTimer.Stop();
            _layoutSaveTimer.Tick -= LayoutSaveTimer_Tick;
            _layoutSaveTimer = null;
        }
        if (_layoutRefreshTimer is not null)
        {
            _layoutRefreshTimer.Stop();
            _layoutRefreshTimer.Tick -= LayoutRefreshTimer_Tick;
            _layoutRefreshTimer = null;
        }
        if (_sessionRefreshTimer is not null)
        {
            _sessionRefreshTimer.Stop();
            _sessionRefreshTimer.Tick -= SessionRefreshTimer_Tick;
            _sessionRefreshTimer = null;
        }
        StopAutosaveTimer();
        UpdateMeterVisual(0, 0);
    }

    private void LayoutSplitter_ResizeCompleted(object? sender, EventArgs e)
    {
        // WP-185: a drag is an explicit choice. It becomes the preferred size even while the
        // responsive fit is active, and a dragged timeline divider ends the auto-fit for good.
        _responsiveFitActive = false;
        if (ReferenceEquals(sender, TimelineSplitter)) _timelineHeightAuto = false;
        CaptureLiveDividerSizes();
        ScheduleLayoutPersist();
    }

    private void Preview_PlaybackStateChanged(object? sender, EventArgs e)
    {
        // WP-163: the timeline defers filmstrip extraction while the transport plays.
        Timeline.IsPlaying = Preview.IsPlaying;
        _ = RefreshAudioMeterAsync(force: true);
    }

    /// <summary>WP-145: captured PNG is a normal library import so it survives save + reopen.</summary>
    private async void Preview_FrameCaptured(object? sender, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        await ImportPathsAsync([path]);
    }

    /// <summary>
    /// Overlay drag writes normalised centres; the Style fields speak canvas pixels (WP-130).
    /// Live moves keep the numerics in lock-step so a drop is not a surprise.
    /// </summary>
    private void Preview_CaptionOverlayPositionChanged(object? sender, CaptionOverlayPositionEventArgs e)
    {
        _captionCenterX = e.CenterX;
        _captionCenterY = e.CenterY;
        if (CaptionsPosXField is null || CaptionsPosYField is null) return;

        var (w, h) = CaptionCanvasSize();
        _syncingCaptionForm = true;
        try
        {
            CaptionsPosXField.Value = CaptionFormLogic.PixelFromNormalized(_captionCenterX, w);
            CaptionsPosYField.Value = CaptionFormLogic.PixelFromNormalized(_captionCenterY, h);
        }
        finally
        {
            _syncingCaptionForm = false;
        }
        SyncPositionReset();
    }

    private void AppWindow_Changed(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange || args.DidPresenterChange)
            ScheduleTimelineLayoutRefresh();
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5
            && Math.Abs(e.NewSize.Height - e.PreviousSize.Height) < 0.5)
            return;
        ApplyResponsiveShell(e.NewSize.Width, e.NewSize.Height);
        RefreshZoomReadout(Timeline.PixelsPerFrame);
        ScheduleTimelineLayoutRefresh();
    }

    // ---- WP-185: the shell gives way to the window instead of clipping the picture ----------
    private const double MinMediaColumnWidth = 300;
    private const double MinInspectorColumnWidth = 280;
    private const double TimelineChromeHeight = 40 + 14; // unified toolbar and scrollbar

    /// <summary>
    /// Below the breakpoint the agent panel folds away unless the user chose it this session; the
    /// side columns then give up their slack down to their floors so the picture keeps at least
    /// the picture budget in DESIGN §6. Preferred widths are never overwritten here, so a
    /// wider window gets them back. An explicitly persisted layout is respected: the agent panel
    /// only auto-hides when the user has not toggled it, and a dragged divider becomes the new
    /// preference through <see cref="LayoutSplitter_ResizeCompleted"/>.
    /// </summary>
    private void ApplyResponsiveShell(double width, double height)
    {
        if (!_layoutLoaded || _maximized) return;
        FitSideColumnsToWidth(width);
        if ((_timelineFitPending || _timelineHeightAuto) && height > 0)
        {
            _timelineFitPending = false;
            FitTimelineHeightToTracks(height);
        }
    }

    private void FitSideColumnsToWidth(double width)
    {
        if (_maximized || _verticalTopology || _previewOnSide) return;
        var available = width - RootGrid.Padding.Left - RootGrid.Padding.Right;
        var (media, inspector, fitted) = ResponsiveShell.FitSideColumns(
            available,
            _vm.MediaVisible ? _mediaWidth : 0,
            _vm.AgentVisible ? _agentWidth : _vm.InspectorVisible ? _inspectorWidth : 0,
            MinMediaColumnWidth, MinInspectorColumnWidth,
            PreviewPictureBudget.MinimumColumnWidth(RootGrid.ActualHeight));
        _responsiveFitActive = fitted;
        if (_vm.MediaVisible && MediaCol.Width.IsAbsolute && Math.Abs(MediaCol.Width.Value - media) > 0.5)
            MediaCol.Width = new GridLength(media);
        if ((_vm.AgentVisible || _vm.InspectorVisible) && InspectorCol.Width.IsAbsolute && Math.Abs(InspectorCol.Width.Value - inspector) > 0.5)
            InspectorCol.Width = new GridLength(inspector);
    }

    /// <summary>
    /// On open, size the timeline to its tracks (up to a share of the window) so the audio tracks
    /// are visible without scrolling. Off once the user has dragged the timeline divider.
    /// </summary>
    private void FitTimelineHeightToTracks(double windowHeight)
    {
        if (!_timelineHeightAuto || _maximized || _verticalTopology) return;
        var target = PreviewPictureBudget.TimelineHeight(windowHeight, Preview.ActualWidth,
            Timeline.LanesHeight, TimelineChromeHeight, EditorLayoutPreferences.DefaultTimelineHeight,
            EditorLayoutPreferences.MinTimelineHeight);
        if (TimelineRow.Height.IsAbsolute && Math.Abs(TimelineRow.Height.Value - target) < 1) return;
        TimelineRow.Height = new GridLength(target);
        _timelineHeight = target;
        ScheduleLayoutPersist();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _layoutRefreshTimer;

    /// <summary>
    /// Fullscreen / maximize often leaves TimelineControl with a stale ActualWidth until click.
    /// Debounced rebuild after the layout pass.
    /// </summary>
    private void ScheduleTimelineLayoutRefresh()
    {
        _layoutRefreshTimer ??= DispatcherQueue.CreateTimer();
        _layoutRefreshTimer.Interval = TimeSpan.FromMilliseconds(50);
        _layoutRefreshTimer.IsRepeating = false;
        _layoutRefreshTimer.Tick -= LayoutRefreshTimer_Tick;
        _layoutRefreshTimer.Tick += LayoutRefreshTimer_Tick;
        _layoutRefreshTimer.Start();
    }

    private void LayoutRefreshTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        try
        {
            Timeline.Rebuild();
            Timeline.EnsurePlayheadVisible();
        }
        catch { /* control may be mid-unload */ }
    }

    // ---- WP-153: session mutation signal -------------------------------------------------------
    // An external MCP client over loopback mutates the open project on an HTTP listener thread and
    // used to raise nothing, so every panel kept drawing the pre-edit session. ProjectSession.Changed
    // is now the single signal for every path; this is the page's end of it.

    private const int SessionRefreshDebounceMs = 250;

    /// <summary>A sustained agent run must not leave the view stale indefinitely.</summary>
    private const int SessionRefreshMaxWaitMs = 1000;

    /// <summary><c>Environment.TickCount64</c> of the oldest signal not yet drawn; 0 when idle.</summary>
    private long _sessionRefreshPendingSince;

    /// <summary>
    /// Belt and braces around an Inspector scrub. The real fix is in Core — <c>ClipLiveEdit</c> no
    /// longer signals per value change — but any future path that mutates continuously without
    /// committing would otherwise be refreshed into, and <c>RefreshAfterEdit</c> ends a live gesture.
    /// </summary>
    private const int LiveEditQuietMs = 400;

    /// <summary><c>Environment.TickCount64</c> of the last live Inspector change; 0 if there was none.</summary>
    private long _lastLivePreview;

    /// <summary>
    /// Points the mutation subscription at <paramref name="next"/> and detaches the previous one, so
    /// a project swap cannot leave the page listening to the session it just closed — and a
    /// generation landing on an orphaned session cannot refresh a window bound to another project.
    /// </summary>
    private void AttachSessionSignal(ProjectSession? next)
    {
        if (ReferenceEquals(_signalSession, next))
            return;
        if (_signalSession is not null)
            _signalSession.Changed -= Session_Changed;
        _signalSession = next;
        if (_signalSession is not null)
            _signalSession.Changed += Session_Changed;
    }

    /// <summary>
    /// Runs on whichever thread mutated the session: an HTTP listener thread for an external MCP
    /// client, a thread-pool thread for the agent chat, the UI thread for a local edit. Some of those
    /// callers hold <c>ToolExecutor._sessionLock</c> while they raise, so this may only post and
    /// return — blocking here for the UI thread would deadlock against a UI thread waiting on that
    /// same lock. <c>TryEnqueue</c> never blocks and returns false instead of throwing once the queue
    /// has shut down.
    /// </summary>
    private void RequestSessionRefresh()
    {
        try
        {
            DispatcherQueue?.TryEnqueue(ScheduleSessionRefresh);
        }
        catch { /* page may be mid-unload */ }
    }

    private void Session_Changed(object? sender, EventArgs e)
    {
        RequestSessionRefresh();
    }

    /// <summary>
    /// Debounced so a twenty-tool agent run costs one <see cref="RefreshAfterEdit"/> rather than
    /// twenty — it re-decodes a thumbnail per media item and rebuilds the whole timeline canvas.
    /// Same timer shape as <see cref="ScheduleTimelineLayoutRefresh"/>.
    /// </summary>
    private void ScheduleSessionRefresh()
    {
        var now = Environment.TickCount64;
        if (_sessionRefreshPendingSince == 0)
        {
            _sessionRefreshPendingSince = now;
        }
        else if (now - _sessionRefreshPendingSince >= SessionRefreshMaxWaitMs)
        {
            // Pure debounce starves under a continuous burst: the whole point is that the user sees
            // the external edits. Draw what has landed and open a fresh window.
            _sessionRefreshTimer?.Stop();
            RunSessionRefresh();
            return;
        }

        _sessionRefreshTimer ??= DispatcherQueue.CreateTimer();
        _sessionRefreshTimer.Interval = TimeSpan.FromMilliseconds(SessionRefreshDebounceMs);
        _sessionRefreshTimer.IsRepeating = false;
        _sessionRefreshTimer.Tick -= SessionRefreshTimer_Tick;
        _sessionRefreshTimer.Tick += SessionRefreshTimer_Tick;
        _sessionRefreshTimer.Start();
    }

    private void SessionRefreshTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        try
        {
            RunSessionRefresh();
        }
        catch { /* control may be mid-unload */ }
    }

    /// <summary>
    /// Records one line per full refresh, and only when <c>TEMNO_REFRESH_TRACE</c> names a file.
    /// WP-153's acceptance asks for the number of refreshes a burst of external calls produces, and
    /// a coalescer that cannot be counted from outside the process is an unfalsifiable claim. Same
    /// kind of measurement seam as <c>TEMNO_DEV_GALLERY</c> and <c>TEMNO_REGISTRY_PATH</c>.
    /// </summary>
    private static readonly string? RefreshTracePath =
        EnvCompat.Get("TEMNO_REFRESH_TRACE");

    private static void TraceRefresh()
    {
        if (string.IsNullOrEmpty(RefreshTracePath)) return;
        try
        {
            File.AppendAllText(
                RefreshTracePath,
                $"{DateTime.UtcNow:HH:mm:ss.fff} RefreshAfterEdit{Environment.NewLine}");
        }
        catch { /* a diagnostic must never break the editor */ }
    }

    private void RunSessionRefresh()
    {
        // WP-269: an open live gesture defers the coalesced refresh by state, not by recency.
        // Every grade tick signals (EffectStackCommands.*InPlace call MarkDirty), so a drag that
        // pauses longer than LiveEditQuietMs — a slow human drag, a mid-gesture screenshot — used
        // to refresh here, commit the gesture mid-pointer (ShowSelection ends it), and either split
        // it into two undo steps or break pointer capture and deaden the rest of the drag. While
        // the Inspector holds a gesture, always re-arm like the quiet window below: no max-wait
        // cap, because a cap re-opens the same hole for slow drags (traced: a 1200 ms hold split
        // a pad drag at the 1000 ms max-wait). This cannot wedge refreshes: pointer capture
        // guarantees every gesture a terminal event (release or capture-lost), and any abandoned
        // gesture is flushed — joined or committed — by the next gesture, discrete commit, undo,
        // tab switch or selection change. An external edit that lands mid-gesture is drawn by the
        // gesture's own commit, which raises ClipEdited and refreshes synchronously.
        if (Inspector.IsLiveEditActive)
        {
            _sessionRefreshTimer?.Stop();
            _sessionRefreshTimer?.Start();
            return;
        }

        // Mid-gesture: wait for the pointer to settle rather than committing the user's edit for
        // them. The pending-since stamp is kept, so the max-wait still measures from the first signal.
        if (_lastLivePreview != 0 && Environment.TickCount64 - _lastLivePreview < LiveEditQuietMs)
        {
            _sessionRefreshTimer?.Stop();
            _sessionRefreshTimer?.Start();
            return;
        }

        _sessionRefreshPendingSince = 0;
        var session = AppSession.Current.Session;
        // Nothing new to draw: a local edit refreshed synchronously between the signal and this tick.
        // This is what keeps local editing exactly as it was — no second, delayed refresh.
        if (session is null || session.Revision == _renderedRevision)
            return;
        RefreshAfterEdit();
    }

    /// <summary>US backtick is handled in CharacterReceived only so KeyDown does not double-toggle.</summary>
    private void RootGrid_CharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        if (IsTypingSurface(e.OriginalSource))
            return;
        if (e.Character is '`' or '~')
        {
            ToggleMaximizeFocused();
            e.Handled = true;
            RootGrid.Focus(FocusState.Programmatic);
        }
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (AppSession.Current.Session is null)
        {
            Frame?.Navigate(typeof(HomePage));
            return;
        }

        ApplyProjectWindowSize();
        AppSession.Current.SessionChanged += OnAppSessionChanged;
        BindSession();
        InstallEditorTitleBar();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Preview.DisposeResources();
        AppSession.Current.SessionChanged -= OnAppSessionChanged;
        AttachSessionSignal(null);
        if (_sessionRefreshTimer is not null)
        {
            _sessionRefreshTimer.Stop();
            _sessionRefreshTimer.Tick -= SessionRefreshTimer_Tick;
            _sessionRefreshTimer = null;
        }
        _sessionRefreshPendingSince = 0;
        if (App.MainAppWindow is MainWindow main)
            main.UseHomeTitleBar();
    }

    private void OnAppSessionChanged(object? sender, EventArgs e)
    {
        DispatcherQueue.TryEnqueue(ApplyHubSession);
    }

    private void ApplyHubSession()
    {
        var session = AppSession.Current.Session;
        GenerationPanel.SyncProject(session);
        if (session is null)
        {
            _boundSession = null;
            AttachSessionSignal(null);
            Frame?.Navigate(typeof(HomePage));
            return;
        }

        if (!ReferenceEquals(_boundSession, session))
            BindSession(resetPreview: true);
    }

    private void InstallEditorTitleBar()
    {
        if (App.MainAppWindow is not MainWindow main) return;
        var title = string.IsNullOrWhiteSpace(ProjectTitleText.Text) ? "Temno" : ProjectTitleText.Text;
        main.UseEditorTitleBar(EditorDragRegion, title);
    }
    // ---- WP-154: offline media before an export ------------------------------------------------

    /// <summary>Exporters for queued jobs, so a completion can say what the encode had to drop.</summary>
    private readonly Dictionary<string, TimelineExporter> _exportersByJob = new(StringComparer.Ordinal);

}
