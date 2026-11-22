using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Temno.App.Audio;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Captions;
using Temno.Core.Editing;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Preview;
using Temno.Core.Session;
using Temno.Core.Ui;
using Temno.Media;
using Temno.Media.Cache;
using Temno.Media.Audio;
using Temno.Media.Playback;
using Windows.Foundation;
using Windows.Media.Playback;
using NativeMediaSource = Windows.Media.Core.MediaSource;

using Temno.App.ViewModels;

namespace Temno.App.Controls;

public sealed partial class PreviewHostControl : UserControl
{

    private readonly PreviewTabController _tabs = new();

    // WP-164: scrub proxies for preview only. The budget is stated here rather than inherited so
    // the on-disk cost is visible at the call site: proxies get 4 GiB, on top of the thumbnail
    // cache's own 2 GiB default under the same root.
    private const long ProxyCacheBudgetBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>
    /// WP-164 measurement seam, the same kind as <c>TEMNO_PLAYBACK_TRACE</c> and
    /// <c>TEMNO_REGISTRY_PATH</c>: set it to run preview on the original media so the
    /// with-proxies and without-proxies numbers come off the same build and the same project.
    /// Unset in normal use, which is the only state the product ships in.
    /// </summary>
    private static readonly bool ProxiesDisabled =
        !string.IsNullOrEmpty(EnvCompat.Get("TEMNO_DISABLE_PROXIES"));

    // Loaded at field initialization, before _proxies/_playbackFrames are built (same rule as _guides).
    private PreviewResolution _previewResolution = UserPreferences.Load().ResolvePreviewResolution();

    private readonly ProxySourceSelector? _proxies;

    // WP-163 + adjustable frame preview: preview decodes at the user's chosen cap (1280 default, or
    // full canvas / preset / custom). Export decodes through ExportFrameSource at output size and never sees this.
    // WP-164: and only this source is given the proxy selector - ExportFrameSource has no
    // parameter to hand one to, which is what keeps proxies out of the exported file.
    private readonly SequentialFrameSource _playbackFrames;

    /// <summary>WP-164: proxy state for the media under the playhead, or null when none applies.</summary>
    private string? _proxyStatus;
    private string? _statusMessage;
    private (int Width, int Height)? _lastPresentedSize;
    private bool _showStatusLabel = true;
    private bool _compactPreviewResolutionLabel;
    private ProjectSession? _session;
    private string? _boundPackagePath;
    private string? _boundTimelineId;
    private PlaybackClock? _clock;
    private readonly StopwatchTimeSource _time = new();
    private PreviewFrameProvider? _provider;
    private DispatcherQueueTimer? _timer;
    private CancellationTokenSource? _decodeCts;
    private CancellationTokenSource _prerollCts = new();
    private CancellationTokenSource? _audioCts;
    private readonly TimelineAudioDecoder _audioDecoder = new();
    private readonly TimelineAudioPlayback _audioPlayback = new();
    private readonly MediaPlayer _sourcePlayer = new();
    private NativeMediaSource? _sourceMedia;
    private string? _nativeSourcePath;
    private Clip? _nativeTimelineClip;
    private int? _pendingNativeSeekFrame;
    private bool _pendingNativePlay;
    private bool _nativeSourceOpened;
    /// <summary>Desired native rate, applied once the engine exists (WP-155).</summary>
    private double _nativePlaybackRate = 1.0;
    private bool _awaitingNativeClipSettle;
    private int _transportGeneration;
    private TimelineAudioProgram? _audioProgram;
    private Timeline? _sourceAudioTimeline;
    private int _displayFrame = -1;
    private WriteableBitmap? _displayBitmap;
    // Exact-sized BGRA conversion pool for RGBA composited frames (avoids ArrayPool<byte>.Shared's
    // 64 MiB power-of-two LOH oversizing and Gen2 eviction pauses on 4K preview playback).
    private readonly FrameBufferPool _displayPool = new();
    private CaptionPreviewImage? _captionOverlay;
    private Border? _captionGuides;
    private Temno.Core.Models.Transform? _captionOverlayTransform;
    private int _captionCanvasWidth = 1920;
    private int _captionCanvasHeight = 1080;
    private bool _captionOverlayDragging;
    private Windows.Foundation.Point _captionDragStart;
    private double _captionDragOriginX = 0.5;
    private double _captionDragOriginY = 0.9;
    private bool _followPlayhead = true;
    private bool _preparingAudio;
    private int _playbackStartId;
    private bool _scrubbing;
    private bool _resumeAfterScrub;
    private bool _decodeBusy;
    private int _decodeGeneration;
    private int? _pendingFrame;
    private long _lastScrubRefreshTicks;
    private int _timelinePlayhead;
    private int _sourcePlayhead;
    private int _sourceDurationFrames = 1;

    public event EventHandler<int>? PlayheadChanged;
    public event EventHandler? PlaybackStateChanged;
    public event EventHandler? MonitorChanged;
    public event EventHandler<CaptionOverlayPositionEventArgs>? CaptionOverlayPositionChanged;
    public event EventHandler? ProjectSettingsChanged;

    public PreviewHostControl()
    {
        var initialMaxDimension = _previewResolution.MaxDimension ?? PreviewRaster.PreviewMaxDimension;
        _proxies = ProxiesDisabled
            ? null
            : new ProxySourceSelector(
                new ProxyCache(MediaCachePaths.Default, maxTotalBytes: ProxyCacheBudgetBytes),
                initialMaxDimension);
        _playbackFrames = new SequentialFrameSource(
            maxDimension: initialMaxDimension, proxies: _proxies, preferHardware: true);
        InitializeComponent();
        UpdatePreviewResolutionLabel();
        // WP-164: proxy transcodes finish on a background thread; the label is UI-thread only.
        if (_proxies is not null)
            _proxies.StateChanged += status => DispatcherQueue.TryEnqueue(() => OnProxyStateChanged(status));
        // WP-163: count decoder restarts inside the playback window to test the restart-cycle
        // theory (single-flight + latest-wins forcing a probe+spawn per frame). Gated the same
        // way as every other trace site: nothing is written unless a trace file is armed.
        _playbackFrames.DecoderRestarted += () =>
        {
            if (TracingPlayback) PlaybackTrace("restart", _clock?.CurrentFrame ?? -1);
        };
        _playbackFrames.DecoderPrerolled += () =>
        {
            if (TracingPlayback) PlaybackTrace("preroll", _clock?.CurrentFrame ?? -1);
        };
        // WP-145: _guides is already loaded (field initialiser); this reflects it into the menu and
        // draws the first frame of overlay once the XAML elements exist.
        SyncGuideMenu();
        // Space on these buttons must play, not click the button again. The page handles it
        // first when it can; this catches a preview tab that still has the key.
        var chromeSpace = (KeyEventHandler)Chrome_PreviewKeyDown;
        foreach (var button in new[] { BtnTabBack, BtnTabForward, BtnCloseAllTabs, BtnCanvasSize, BtnToStart, BtnStepBack, BtnPlay, BtnStepFwd, BtnToEnd, BtnPreviewResolution, BtnCaptureFrame, BtnGuides })
            button.AddHandler(UIElement.PreviewKeyDownEvent, chromeSpace, handledEventsToo: false);
        AutomationProperties.SetHelpText(
            GuideOverlay,
            "Composition guides. Not included in export or capture.");
        RedrawGuides();
        _sourcePlayer.AutoPlay = false;
        _sourcePlayer.CommandManager.IsEnabled = false;
        _sourcePlayer.MediaOpened += SourcePlayer_MediaOpened;
        _sourcePlayer.MediaEnded += SourcePlayer_MediaEnded;
        _sourcePlayer.MediaFailed += SourcePlayer_MediaFailed;
        _sourcePlayer.IsLoopingEnabled = false;
        _audioPlayback.DeviceError += message => DispatcherQueue.TryEnqueue(
            () => SetAudioStatus($"Audio device error: {ShortStatus(message)}"));
        Unloaded += (_, _) =>
        {
            PlaybackTrace("lifecycle-unloaded-playing", IsPlaying ? 1 : 0);
            // Reparenting can deliver Loaded before Unloaded. Editor navigation owns transport disposal.
        };
        Loaded += (_, _) =>
        {
            PlaybackTrace("lifecycle-loaded-playing", IsPlaying ? 1 : 0);
            if (_resourcesDisposed) return;
            RebuildTabStrip();
            InvalidateCurrentFrame();
        };
    }

    public bool IsTimelineMonitor => _tabs.IsTimeline;

    public (float Left, float Right) GetTimelineAudioLevels(int frame)
        => IsTimelineMonitor && _audioPlayback.IsReady && _audioProgram is { } program
            ? TimelineAudioLevelSampler.PeakAtFrame(program, frame)
            : (0, 0);

    public int PlayheadFrame
    {
        get => _clock?.CurrentFrame ?? 0;
        set
        {
            EnsureClock();
            _clock!.SeekToFrame(value);
            StorePlayhead(value);
            if (_tabs.IsTimeline)
                ConfigureTimelineNative(value);

            if (UsesNativeVideo)
            {
                SeekNativeToMonitorFrame(value);
                ShowNativeSource();
                _displayFrame = value;
                _pendingFrame = null;
            }

            if (_tabs.IsTimeline)
            {
                if (_scrubbing)
                    _audioPlayback.PlayScrubAtFrame(value);
                else if (_audioPlayback.IsReady)
                    _audioPlayback.SeekToFrame(value, _clock.IsPlaying);
            }
            else if (!UsesNativeVideo)
            {
                if (_scrubbing)
                    _audioPlayback.PlayScrubAtFrame(value);
                else if (_audioPlayback.IsReady)
                    _audioPlayback.SeekToFrame(value, _clock.IsPlaying);
            }

            if (_scrubbing)
            {
                var now = Environment.TickCount64;
                if (now - _lastScrubRefreshTicks < 50)
                {
                    _pendingFrame = value;
                    UpdateLabels(value);
                    RaisePlayheadChanged(value);
                    return;
                }
                _lastScrubRefreshTicks = now;
            }

            if (!UsesNativeVideo)
                RequestFrame(value);
            UpdateLabels(value);
            RaisePlayheadChanged(value);
        }
    }

    public bool IsPlaying => _clock?.IsPlaying == true;

    private bool _resourcesDisposed;

    public void DisposeResources()
    {
        if (_resourcesDisposed) return;
        _resourcesDisposed = true;
        StopTimer();
        if (_clock is not null)
        {
            _clock.FrameChanged -= OnClockFrame;
            _clock.IsPlayingChanged -= OnClockPlayingChanged;
            _clock.Pause();
        }
        _decodeCts?.Cancel();
        CancelPrerolls();
        _audioCts?.Cancel();
        // T2 round 2 (non-blocking): leaving the editor released everything here except the
        // provider, which since WP-215 owns a D3D11 device. Same reason as the rebind path.
        _provider?.Dispose();
        _provider = null;
        _audioPlayback.Dispose();
        _playbackFrames.Dispose();
        _displayPool.Clear();
        _proxies?.Dispose();
        ClearNativeSource();
        _sourcePlayer.Dispose();
        PreviewImage.Source = null;
        _displayBitmap = null;
    }

    /// <summary>
    /// WP-252. Renders <paramref name="frame"/> without moving the playhead, for a timeline gesture
    /// that wants a trim-monitor view (a transition edge drag). Requests coalesce like a scrub;
    /// <see cref="InvalidateCurrentFrame"/> goes back to the playhead.
    /// </summary>
    public void ShowFrameWithoutSeeking(int frame)
    {
        if (!_tabs.IsTimeline || UsesNativeVideo) return;
        _displayFrame = -1;
        RequestFrame(Math.Max(0, frame), force: true);
    }

    public void InvalidateCurrentFrame()
    {
        _provider?.Invalidate();
        _displayFrame = -1;
        if (_tabs.IsTimeline)
            ConfigureTimelineNative(PlayheadFrame);
        if (UsesNativeVideo)
            SeekNativeToMonitorFrame(PlayheadFrame);
        else
            RequestFrame(PlayheadFrame, force: true);
    }

    /// <param name="resetMonitor">
    /// True when opening a project (Mac starts on the timeline tab). False on timeline edits so a
    /// source tab and its playhead survive <c>RefreshAfterEdit</c>.
    /// </param>
    public void Bind(ProjectSession? session, bool resetMonitor = true)
    {
        PlaybackTrace("lifecycle-bind-playing", IsPlaying ? 1 : 0);
        _transportGeneration++;
        var package = session?.PackagePath;
        var sameSession = session is not null && ReferenceEquals(_session, session) && _provider is not null;
        var sameProject = session is not null
            && _session is not null
            && (ReferenceEquals(_session, session)
                || (!string.IsNullOrEmpty(package)
                    && string.Equals(_boundPackagePath, package, StringComparison.OrdinalIgnoreCase)));

        var previousTimelineId = _boundTimelineId;
        var timelineChanged = sameProject
            && previousTimelineId is not null
            && !string.Equals(previousTimelineId, session?.ActiveTimeline.Id, StringComparison.Ordinal);

        _session = session;
        _boundPackagePath = package;
        _boundTimelineId = session?.ActiveTimeline.Id;

        // Cancel any decode pointing at the outgoing state before rebinding. Reuse the warm
        // PreviewFrameProvider (and its D3D11 compositor, pools, and text raster cache) across
        // edits and timeline switches within the same session; only dispose & recreate when
        // opening/closing/switching projects.
        _decodeGeneration++;
        _decodeBusy = false;
        _decodeCts?.Cancel();
        if (!sameSession)
        {
            var previousProvider = _provider;
            SyncPreviewResolutionPipeline();
            _provider = session is null
                ? null
                : new PreviewFrameProvider(
                    _playbackFrames,
                    session.Resolver,
                    timelineLookup: SessionTimelineLookup.For(session),
                    maxDimension: _previewResolution.MaxDimension);
            previousProvider?.Dispose();
        }
        else
        {
            _provider!.Invalidate();
            SyncPreviewResolutionPipeline();
        }

        // Switching timelines or closing/switching projects immediately stops outgoing ffmpeg
        // decoder pipes, queued prerolls, and background proxy transcodes so inactive timelines
        // never contend with the active timeline's preview decode.
        if (timelineChanged || !sameProject || session is null)
        {
            CancelPrerolls();
            _playbackFrames.Reset();
            _proxies?.CancelPendingTranscodes();
            if (_proxyStatus is not null)
            {
                _proxyStatus = null;
                RefreshStatusLabel();
            }
        }

        var tl = session?.ActiveTimeline;
        ResolutionLabel.Text = tl is null ? "—" : $"{tl.Width}×{tl.Height}";
        BtnCanvasSize.IsEnabled = tl is not null;
        RedrawGuides();

        if (session is null)
        {
            _lastPresentedSize = null;
            ResetTransportState();
            ClearNativeSource();
            _tabs.Reset("Timeline");
            _sourceAudioTimeline = null;
            _playbackFrames.Reset();
            EnsureClock(reset: true);
            PlayheadFrame = 0;
            RebuildTabStrip();
            UpdatePreviewResolutionLabel();
            return;
        }

        var timelineName = string.IsNullOrWhiteSpace(tl?.Name) ? "Timeline" : tl.Name;
        _tabs.RenameTimeline(timelineName);

        if (resetMonitor || !sameProject)
        {
            _lastPresentedSize = null;
            ResetTransportState();
            ClearNativeSource();
            _tabs.Reset(timelineName);
            _sourceAudioTimeline = null;
            _playbackFrames.Reset();
            SyncPreviewResolutionPipeline();
            EnsureClock(reset: true);
            var frame = ViewStatePlayhead(session);
            _timelinePlayhead = frame;
            PlayheadFrame = frame;
            RebuildTabStrip();
            UpdatePreviewResolutionLabel();
            return;
        }

        var liveIds = session.Manifest.Entries.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var activeChanged = _tabs.PruneMissing(liveIds);
        if (timelineChanged)
        {
            _timelinePlayhead = ViewStatePlayhead(session);
            if (!_tabs.IsTimeline)
                _tabs.Select(PreviewMonitorTab.TimelineId);
        }

        var stayOnSource = !resetMonitor && sameProject && !timelineChanged && !_tabs.IsTimeline && !activeChanged;

        if (stayOnSource)
        {
            SyncSourceFromActiveTab();
            SyncPreviewResolutionPipeline();
            EnsureClock(reset: false);
            if (PlayheadFrame >= MonitorDurationFrames)
                PlayheadFrame = Math.Max(0, MonitorDurationFrames - 1);
            RebuildTabStrip();
            UpdateLabels(PlayheadFrame);
            UpdatePreviewResolutionLabel();
            return;
        }

        _lastPresentedSize = null;
        ResetTransportState();
        SyncPreviewResolutionPipeline();
        EnsureClock(reset: false);
        _displayFrame = -1;
        PlayheadFrame = _tabs.IsTimeline ? _timelinePlayhead : _sourcePlayhead;
        RebuildTabStrip();
        UpdatePreviewResolutionLabel();
    }

}
