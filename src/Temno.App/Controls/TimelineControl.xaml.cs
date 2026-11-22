using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Audio;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Ui;
using Temno.Media.Audio;
using Temno.Media.Cache;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Colors = Microsoft.UI.Colors;

namespace Temno.App.Controls;

public sealed partial class TimelineControl : UserControl
{
    private const double EdgeHitPx = 8;

    private ProjectSession? _session;
    private string? _boundTimelineId;
    private TimelineGeometry _geo = new(TimelineGeometry.DefaultPixelsPerFrame, [TimelineGeometry.DefaultTrackHeight], headerWidth: 0, rulerHeight: 0);
    private double _pixelsPerFrame = TimelineGeometry.DefaultPixelsPerFrame;
    private int _playheadFrame;
    private readonly HashSet<string> _selectedClipIds = new(StringComparer.Ordinal);
    private string? _selectedClipId;
    private bool _syncingScroll;
    private double? _expectedHeaderSyncY;
    private bool _headerSyncQueued;
    private double _lastBodyScrollY;
    private bool _draggingPlayhead;
    private bool _seeking;
    private bool _marqueeSelecting;
    private Point _marqueeStart;
    private HashSet<string> _marqueeBaseSelection = new(StringComparer.Ordinal);
    private Border? _marquee;

    private enum DragKind { None, Move, TrimLeft, TrimRight, Transition }
    private DragKind _dragKind;
    private string? _dragClipId;
    private int _dragPointerStartFrame;
    private int _dragFrameDelta;
    private int _dragTrackOffset;
    private int? _dragNewTrackInsertIndex;
    private ClipType _dragNewTrackType = ClipType.video;
    private Border? _newTrackIndicator;
    private List<DragClipSnapshot> _dragSnapshots = [];
    private bool IsLiveTrim => _dragKind is DragKind.TrimLeft or DragKind.TrimRight;

    // WP-320: a pointer move only records the target. CompositionTarget.Rendering applies it once per frame.
    private bool _trimRenderHooked;
    private bool _trimApplying;
    private bool _trimTargetPending;
    private bool _trimVisualApplied;
    private int _trimRequestedDelta;
    /// <summary>After a trim releases, keep drawing the nearest cached strip cell until the exact ones land.</summary>
    private bool _preferCachedStrip;
    private bool _stripPassUsedFallback;
    private int _stripInflight;
    /// <summary>Lane elements owned by one track, so a trim preview can replace that track and leave the rest.</summary>
    private readonly Dictionary<int, List<UIElement>> _laneByTrack = new();
    private List<UIElement>? _laneBucket;
    private readonly List<Border> _dragGhosts = [];
    private readonly SnapEngine.SnapState _snapState = new();
    private readonly SnapEngine.SnapState _bladeSnapState = new();
    private Line? _snapLine;
    private readonly List<Line> _bladeLines = [];
    private Point? _bladeHoverPoint;
    private double _bladeHoverVerticalOffset;
    private bool _bladeBodyCursor;
    private const int BladePreviewZIndex = 2;
    private const int TransparentCursorSize = 32;
    private InputCursor? _transparentBladeCursor;
    private BeatStore? _beatStore;
    private TrackResizeState? _trackResize;
    private TrackReorderState? _trackReorder;
    private Border? _trackReorderIndicator;

    /// <summary>DataPackage property key for an in-app media-panel → timeline drag (WP-60).</summary>
    public const string MediaRefDragKey = "temnoMediaRef";
    private Border? _dropGhost;
    private Border? _dropNewTrackIndicator;
    private const double FullViewportBottomDropLip = 4;
    private const int DefaultStillDurationSeconds = 5;

    // WP-63: clip content (thumbnails + waveforms), generated lazily and cached per mediaRef.
    // WP-213: waveforms are time-indexed envelopes sampled per visible column in source seconds.
    private const int WaveformColumnsMax = 1024;
    private const double WaveformStateMinWidth = 60;
    private readonly ThumbnailCache _thumbnailCache = new(Temno.Media.Cache.MediaCachePaths.Default);
    /// <summary>WP-159: one image per (mediaRef, source second). One per asset was the defect.</summary>
    private readonly Dictionary<(string MediaRef, double Seconds), ImageSource> _stripCache = new();
    /// <summary>Strip cells already asked for, so a redraw does not re-spawn ffmpeg.</summary>
    private readonly HashSet<(string MediaRef, double Seconds)> _stripRequested = new();
    /// <summary>
    /// Thumbnail extraction must not starve preview decode. `SequentialFrameSource` keeps up to
    /// `MaxLivePipes` = 3 decoder pipes for the compositor; two extra ffmpeg runs is the most this
    /// control will add on top of that.
    /// </summary>
    private readonly SemaphoreSlim _stripGate = new(2, 2);
    private CancellationTokenSource _stripCts = new();
    private int _stripGeneration;
    private readonly Dictionary<string, WaveformEntry> _waveformCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _waveformInflight = new(StringComparer.Ordinal);
    private long _waveformGeneration;
    /// <summary>WP-213: Ready revalidation and offline re-resolve cadence — one stat per mediaRef per interval, never per redraw.</summary>
    private static readonly TimeSpan WaveformRevalidateInterval = TimeSpan.FromSeconds(2);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _redrawTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _viewportTimer;
    private double _viewLeft;
    private double _viewWidth = 400;
    private bool _rebuilding;

    public event EventHandler<int>? PlayheadChanged;
    public event EventHandler<string?>? SelectionChanged;
    public event EventHandler? TimelineEdited;
    public event EventHandler<double>? ZoomChanged;
    public event EventHandler? EmptyImportRequested;
    /// <summary>WP-184: a clip context-menu command chosen by the user; the page executes it.</summary>
    public event EventHandler<string>? ContextActionRequested;

    private bool _isPlaying;

    /// <summary>
    /// WP-163 (step d). True while the preview transport is playing. While set, filmstrip
    /// extraction requests are deferred: playhead-follow scroll would otherwise slide the visible
    /// range and spawn one ffmpeg run per new cell per redraw (~12/s on the owner project),
    /// each completion triggering another full lane rebuild. Cells already in <c>_stripCache</c>
    /// keep drawing; new cells load on the transition back to false via a coalesced redraw.
    /// WP-159's filmstrip design is untouched — this only bounds *when* it asks.
    /// </summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (value == _isPlaying) return;
            _isPlaying = value;
            if (!value) RequestVisualRedraw();
        }
    }

    public TimelineControl()
    {
        InitializeComponent();
        SizeChanged += (_, e) =>
        {
            // Fullscreen / window chrome changes need a full geometry rebuild, not only the playhead pin.
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5
                || Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5)
                Rebuild();
            else
                UpdatePlayheadVisual();
        };
        SyncModeUi();
        StartTrimProbePoll(); // WP-320 trim probe (TEMNO_TIMELINE_TRACE)
        // ScrollViewer handles the wheel before children; handledEventsToo lets Shift/Alt
        // convert a vertical notch into a time pan after that default handling.
        BodyScroll.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(Timeline_PointerWheelChanged), true);
        HeaderScroll.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(Timeline_PointerWheelChanged), true);
        BodyScroll.SizeChanged += (_, _) => OnScrollViewportHeightChanged();
        HeaderScroll.SizeChanged += (_, _) => OnScrollViewportHeightChanged();
        BodyScroll.RegisterPropertyChangedCallback(ScrollViewer.ViewportHeightProperty, (_, _) => OnScrollViewportHeightChanged());
        BodyScroll.RegisterPropertyChangedCallback(ScrollViewer.ComputedHorizontalScrollBarVisibilityProperty, (_, _) => OnScrollViewportHeightChanged());
        BodyScroll.RegisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, (_, _) => OnScrollViewportHeightChanged());
        BodyScroll.RegisterPropertyChangedCallback(ScrollViewer.ViewportWidthProperty, (_, _) => OnScrollViewportWidthChanged());
        HeaderScroll.RegisterPropertyChangedCallback(ScrollViewer.ViewportHeightProperty, (_, _) => OnScrollViewportHeightChanged());
        // Optional beat cache for snap targets (hydrated analyses only; detection is separate).
        try
        {
            var beatDir = System.IO.Path.Combine(MediaCachePaths.Default.Root, BeatStore.CacheSubdirectoryName);
            _beatStore = new BeatStore(beatDir);
        }
        catch
        {
            _beatStore = null;
        }

        // WP-213: a late waveform completion must not write cache/UI after unload.
        Unloaded += (_, _) =>
        {
            _viewportTimer?.Stop();
            _redrawTimer?.Stop();
            StopTrimRendering();
            CancelStripRequests();
            CancelWaveformRequests();
            _waveformGeneration++;
        };
    }

    /// <summary>Optional beat analysis cache used for beat snap targets (WP-65).</summary>
    public BeatStore? BeatStore
    {
        get => _beatStore;
        set => _beatStore = value;
    }

    public int PlayheadFrame
    {
        get => _playheadFrame;
        set
        {
            var max = Math.Max(0, _session?.ActiveTimeline.TotalFrames ?? 0);
            var next = Math.Clamp(value, 0, Math.Max(max, 0));
            if (next == _playheadFrame) return;
            _playheadFrame = next;
            UpdatePlayheadVisual();
            UpdateLabels();
            // Persist only when not mid-scrub — disk/view-state churn on every pointer sample lagged the UI.
            if (!_draggingPlayhead && !_seeking)
                PersistPlayhead();
            PlayheadChanged?.Invoke(this, _playheadFrame);
        }
    }

    /// <summary>WP-185: ruler plus every track at its current height, for the editor's timeline auto-fit.</summary>
    public double LanesHeight
    {
        get
        {
            var h = Theme.Layout.RulerHeight;
            for (var i = 0; i < _geo.TrackHeights.Count; i++) h += _geo.TrackHeightAt(i);
            return h;
        }
    }

    public double PixelsPerFrame
    {
        get => _pixelsPerFrame;
        set
        {
            var next = Math.Clamp(value, TimelineGeometry.MinPixelsPerFrame, TimelineGeometry.MaxPixelsPerFrame);
            if (Math.Abs(next - _pixelsPerFrame) < 0.0001) return;
            _pixelsPerFrame = next;
            Rebuild();
            ZoomChanged?.Invoke(this, _pixelsPerFrame);
        }
    }

    public string? SelectedClipId => _selectedClipId;
    public IReadOnlySet<string> SelectedClipIds => _selectedClipIds;

    public void Bind(ProjectSession? session)
    {
        var sameSession = session is not null && ReferenceEquals(_session, session);
        _session = session;
        _boundTimelineId = session?.ActiveTimeline.Id;
        CancelStripRequests();
        CancelWaveformRequests();
        _waveformGeneration++;
        if (sameSession)
        {
            // Keep already-decoded filmstrip bitmaps and waveform envelopes for shared project
            // media across timeline switches while stopping outgoing extraction jobs and letting
            // cancelled cells be re-requested when they come back into view.
            _stripRequested.Clear();
            foreach (var key in _stripCache.Keys)
                _stripRequested.Add(key);
        }
        else
        {
            _stripCache.Clear();
            _stripRequested.Clear();
            _waveformCache.Clear();
        }
        if (session?.ProjectFile.ViewStates is { } states
            && session.ProjectFile.ActiveTimelineId is { } id
            && states.TryGetValue(id, out var vs))
        {
            _playheadFrame = Math.Max(0, vs.PlayheadFrame);
            if (vs.ZoomScale > 0)
                _pixelsPerFrame = Math.Clamp(vs.ZoomScale, TimelineGeometry.MinPixelsPerFrame, TimelineGeometry.MaxPixelsPerFrame);
        }
        else _playheadFrame = 0;

        var knownIds = session?.ActiveTimeline.Tracks
            .Where(track => !track.Locked)
            .SelectMany(track => track.Clips)
            .Select(clip => clip.Id)
            .ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
        var persistedSelection = EditorEditContext.Current.SelectedClipIds.Where(knownIds.Contains);
        ApplySelection(persistedSelection, EditorEditContext.Current.SelectedClipId, notify: false, rebuild: false);
        Rebuild();
        PlayheadChanged?.Invoke(this, _playheadFrame);
    }

    public void SetSelectedClip(string? id)
    {
        SetSelectedClips(id is null ? Array.Empty<string>() : [id], id);
    }

    public void SetSelectedClips(IEnumerable<string> ids, string? focusedClipId = null)
    {
        ApplySelection(ids, focusedClipId, notify: true);
    }

    private bool ApplySelection(
        IEnumerable<string> ids,
        string? focusedClipId,
        bool notify,
        bool rebuild = true)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var next = new HashSet<string>(ids.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
        if (_session is not null)
        {
            var known = _session.ActiveTimeline.Tracks
                .Where(track => !track.Locked)
                .SelectMany(track => track.Clips)
                .Select(clip => clip.Id)
                .ToHashSet(StringComparer.Ordinal);
            next.IntersectWith(known);
        }

        var nextFocused = !string.IsNullOrWhiteSpace(focusedClipId) && next.Contains(focusedClipId)
            ? focusedClipId
            : !string.IsNullOrWhiteSpace(_selectedClipId) && next.Contains(_selectedClipId)
                ? _selectedClipId
                : next.OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
        var changed = !_selectedClipIds.SetEquals(next)
            || !string.Equals(_selectedClipId, nextFocused, StringComparison.Ordinal);
        if (!changed) return false;

        _selectedClipIds.Clear();
        _selectedClipIds.UnionWith(next);
        _selectedClipId = nextFocused;
        if (next.Count > 0) _selectedTransition = null;
        EditorEditContext.Current.SetSelectedClips(_selectedClipIds, _selectedClipId);
        if (rebuild) Rebuild();
        if (notify) SelectionChanged?.Invoke(this, _selectedClipId);
        return true;
    }

    public bool TrySplitAtPlayhead()
    {
        if (_session is null || _selectedClipIds.Count == 0) return false;
        var r = EditCommands.SplitClipsAtFrame(
            _session, _selectedClipIds.ToArray(), _playheadFrame, expandLinks: ShouldExpandCurrentSelection());
        if (!r.Ok) return false;
        NotifyEdited();
        return true;
    }

    /// <summary>Mac trimStartToPlayhead — playhead strictly inside selected clips (and link partners unless Alt).</summary>
    public bool TryTrimStartToPlayhead()
        => TryTrimSelectionToPlayhead(leftEdge: true);

    /// <summary>Mac trimEndToPlayhead.</summary>
    public bool TryTrimEndToPlayhead()
        => TryTrimSelectionToPlayhead(leftEdge: false);

    private bool TryTrimSelectionToPlayhead(bool leftEdge)
    {
        if (_session is null || _selectedClipIds.Count == 0) return false;
        var ids = ShouldExpandCurrentSelection()
            ? ExpandLinked(_selectedClipIds)
            : _selectedClipIds;
        if (!ids.SetEquals(_selectedClipIds))
            ApplySelection(ids, _selectedClipId, notify: true);

        var trims = new List<ClipEdgeTrim>();
        foreach (var track in _session.ActiveTimeline.Tracks)
        {
            if (track.Locked) continue;
            foreach (var clip in track.Clips)
            {
                if (!ids.Contains(clip.Id)) continue;
                if (_playheadFrame <= clip.StartFrame || _playheadFrame >= clip.EndFrame) continue;
                trims.Add(new ClipEdgeTrim(clip.Id, LeftEdge: leftEdge, EdgeFrame: _playheadFrame));
            }
        }
        if (trims.Count == 0) return false;
        var r = EditCommands.TrimClipEdges(_session, trims);
        if (!r.Ok) return false;
        NotifyEdited();
        return true;
    }

    public enum SelectForwardScope { Track, AllTracks }

    /// <summary>Mac selectForwardFromCurrentSelection — anchor = earliest selected clip.</summary>
    public bool TrySelectForwardFromCurrentSelection(SelectForwardScope scope)
    {
        if (_session is null || _selectedClipIds.Count == 0) return false;
        var anchorId = ResolveForwardAnchorId();
        if (anchorId is null) return false;
        return SelectForward(anchorId, scope);
    }

    public bool SelectForward(string fromClipId, SelectForwardScope scope)
    {
        if (_session is null) return false;
        var timeline = _session.ActiveTimeline;
        int anchorTrack = -1;
        Clip? anchor = null;
        for (var ti = 0; ti < timeline.Tracks.Count; ti++)
        {
            var c = timeline.Tracks[ti].Clips.FirstOrDefault(x => x.Id == fromClipId);
            if (c is null) continue;
            anchorTrack = ti;
            anchor = c;
            break;
        }
        if (anchor is null || timeline.Tracks[anchorTrack].Locked) return false;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var ti = 0; ti < timeline.Tracks.Count; ti++)
        {
            if (timeline.Tracks[ti].Locked) continue;
            if (scope == SelectForwardScope.Track && ti != anchorTrack) continue;
            foreach (var clip in timeline.Tracks[ti].Clips)
            {
                if (clip.StartFrame >= anchor.StartFrame)
                    ids.Add(clip.Id);
            }
        }
        ids = ExpandLinked(ids);
        SetSelectedClips(ids, focusedClipId: fromClipId);
        return true;
    }

    private string? ResolveForwardAnchorId()
    {
        if (_session is null || _selectedClipIds.Count == 0) return null;
        string? bestId = null;
        var bestStart = int.MaxValue;
        var bestTrack = int.MaxValue;
        for (var ti = 0; ti < _session.ActiveTimeline.Tracks.Count; ti++)
        {
            foreach (var clip in _session.ActiveTimeline.Tracks[ti].Clips)
            {
                if (!_selectedClipIds.Contains(clip.Id)) continue;
                if (clip.StartFrame < bestStart || (clip.StartFrame == bestStart && ti < bestTrack))
                {
                    bestStart = clip.StartFrame;
                    bestTrack = ti;
                    bestId = clip.Id;
                }
            }
        }
        return bestId;
    }

    public bool TryDeleteSelection()
    {
        if (TryRemoveSelectedTransition()) return true;
        if (_session is null || _selectedClipIds.Count == 0) return false;
        var expand = ShouldExpandCurrentSelection();
        var ids = expand ? ExpandLinked(_selectedClipIds) : _selectedClipIds;
        var r = EditCommands.RemoveClips(_session, ids.ToArray(), EditorEditContext.Current.RippleDelete, expandLinks: expand);
        if (!r.Ok) return false;
        SetSelectedClip(null);
        NotifyEdited();
        return true;
    }

    /// <summary>Link currently selected clips into one A/V group (WP-67).</summary>
    public bool TryLinkSelection()
    {
        if (_session is null || _selectedClipIds.Count < 2) return false;
        var r = EditCommands.LinkClips(_session, _selectedClipIds.ToArray());
        if (!r.Ok) return false;
        NotifyEdited();
        return true;
    }

    /// <summary>Clear link groups for the selection and its partners (WP-67).</summary>
    public bool TryUnlinkSelection()
    {
        if (_session is null || _selectedClipIds.Count == 0) return false;
        var r = EditCommands.UnlinkClips(_session, _selectedClipIds.ToArray());
        if (!r.Ok) return false;
        NotifyEdited();
        return true;
    }

    /// <summary>Repair path for pre-WP-157 timelines: create the linked audio partner for every
    /// selected video clip whose media has audio and which has none yet (WP-162).</summary>
    public bool TryRestoreLinkedAudioSelection()
    {
        if (_session is null || _selectedClipIds.Count == 0) return false;
        var (restored, _) = EditCommands.RestoreLinkedAudio(_session, _selectedClipIds.ToArray());
        if (restored.Count == 0) return false;
        NotifyEdited();
        return true;
    }

    private bool _rebuildDeferred;

    public void Rebuild()
    {
        // A Rebuild raised while one is running (SizeChanged during DrawLanes, or a layout-refresh tick
        // arriving mid-build) is deferred to the next dispatcher turn instead of nesting. The first
        // WP-185 receipt pass crashed natively (Microsoft.UI.Xaml.dll, stowed exception) with two
        // Rebuild frames on the UI thread during a 1366x768 resize with a cold filmstrip cache.
        if (_rebuilding)
        {
            if (!_rebuildDeferred)
            {
                _rebuildDeferred = true;
                DispatcherQueue.TryEnqueue(() => { _rebuildDeferred = false; Rebuild(); });
            }
            return;
        }

        var timeline = _session?.ActiveTimeline;
        if (timeline is null)
        {
            _boundTimelineId = null;
            RulerCanvas.Children.Clear();
            LaneCanvas.Children.Clear();
            HeaderCanvas.Children.Clear();
            BodyContent.Width = 400;
            BodyContent.Height = 120;
            SyncViewportSurfaces(120);
            UpdateLabels();
            UpdateEmptyState();
            return;
        }

        // Undo/Redo or MCP timeline switches reach Rebuild() via RefreshAfterEdit() without
        // passing through BindSession(); rebind so outgoing strip/waveform jobs cancel and
        // the incoming timeline's saved playhead/zoom restore cleanly.
        if (!string.Equals(_boundTimelineId, timeline.Id, StringComparison.Ordinal))
        {
            Bind(_session);
            return;
        }

        var knownIds = timeline.Tracks
            .Where(track => !track.Locked)
            .SelectMany(track => track.Clips)
            .Select(clip => clip.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (!_selectedClipIds.IsSubsetOf(knownIds))
            ApplySelection(_selectedClipIds.Where(knownIds.Contains), _selectedClipId, notify: true, rebuild: false);
        DropStaleTransitionSelection(timeline);

        // Ruler lives in the fixed RulerHost row above BodyScroll; LaneCanvas and HeaderCanvas share track-local Y=0.
        _geo = TimelineGeometry.FromTimeline(timeline, _pixelsPerFrame, headerWidth: 0, rulerHeight: 0);
        var totalFrames = Math.Max(timeline.TotalFrames, timeline.Fps * 5);
        var contentW = Math.Max(BodyScroll.ActualWidth > 0 ? BodyScroll.ActualWidth : 400, _geo.ContentWidth(totalFrames) - _geo.HeaderWidth);
        var contentH = Math.Max(120, _geo.ContentHeight);

        _rebuilding = true;
        _viewportTimer?.Stop();
        var rbT0 = System.Diagnostics.Stopwatch.GetTimestamp(); // WP-320 trim probe (TEMNO_TIMELINE_TRACE)
        TimelineTrace.Mark("rb0", LaneCanvas.Children.Count);
        var headerHeightChanged = false;
        try
        {
            BodyContent.Width = contentW;
            BodyContent.Height = contentH;
            HeaderCanvas.Width = HeaderCol.Width.Value;
            headerHeightChanged = SyncHeaderCanvasHeight(contentH);

            if (SyncViewportSurfaces(contentH))
                headerHeightChanged = true;
            DrawLanes(timeline);
            var rbT1 = System.Diagnostics.Stopwatch.GetTimestamp();
            DrawHeaders(timeline);
            TimelineTrace.Mark("rbh", rbT1 - rbT0, System.Diagnostics.Stopwatch.GetTimestamp() - rbT1);
            UpdatePlayheadVisual();
            UpdateLabels();
            SyncModeUi();
            UpdateEmptyState();
        }
        finally
        {
            _rebuilding = false;
            TimelineTrace.Mark("rb1", LaneCanvas.Children.Count, HeaderCanvas.Children.Count);
            if (TimelineTrace.On)
            {
                TimelineTrace.Mark("gcp", (long)GC.GetTotalPauseDuration().TotalMicroseconds, GC.CollectionCount(2));
                TimelineTrace.Mark("gcn", GC.CollectionCount(0), GC.CollectionCount(1));
            }
        }
        if (headerHeightChanged)
            ScheduleHeaderVerticalSync();
    }

    private bool ActiveTimelineHasNoClips()
        => _session?.ActiveTimeline.Tracks.SelectMany(track => track.Clips).Any() != true;

    private void UpdateEmptyState()
    {
        if (EmptyState is null)
            return;
        EmptyState.Visibility = ActiveTimelineHasNoClips() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EmptyImport_Click(object sender, RoutedEventArgs e)
        => EmptyImportRequested?.Invoke(this, EventArgs.Empty);

    private bool SyncViewportSurfaces(double contentH)
    {
        _viewLeft = BodyScroll.HorizontalOffset;
        RulerTransform.X = 0;
        var vw = BodyScroll.ViewportWidth;
        if (vw <= 1)
            vw = BodyScroll.ActualWidth;
        _viewWidth = Math.Max(1, vw > 1 ? vw : 400);
        var rulerH = Theme.Layout.RulerHeight;
        var rulerW = Math.Max(_viewWidth, RulerHost.ActualWidth);
        RulerCanvas.Width = rulerW;
        RulerCanvas.Height = rulerH;
        RulerOverlayCanvas.Width = _viewWidth;
        RulerOverlayCanvas.Height = rulerH;
        RulerPlayheadLine.Height = rulerH;
        RulerOverlayCanvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _viewWidth, rulerH) };
        RulerHost.Clip = new RectangleGeometry { Rect = new Rect(0, 0, rulerW, rulerH) };
        LaneCanvas.Width = _viewWidth;
        LaneCanvas.Height = contentH;
        OverlayCanvas.Width = _viewWidth;
        OverlayCanvas.Height = contentH;
        LaneCanvas.Margin = new Thickness(_viewLeft, 0, 0, 0);
        OverlayCanvas.Margin = new Thickness(_viewLeft, 0, 0, 0);
        return SyncHeaderCanvasHeight(contentH);
    }

    private double CurrentBodyContentHeight()
        => Math.Max(120, double.IsNaN(BodyContent.Height) ? 120 : BodyContent.Height);

    private double HeaderScrollHeightAllowance()
        => TimelineGeometry.HeaderScrollHeightAllowance(
            HeaderScroll.ViewportHeight,
            BodyScroll.ViewportHeight,
            BodyScroll.ActualHeight);

    private bool SyncHeaderCanvasHeight(double contentH)
    {
        var target = contentH + HeaderScrollHeightAllowance();
        if (!double.IsNaN(HeaderCanvas.Height) && Math.Abs(HeaderCanvas.Height - target) <= 0.5)
            return false;
        HeaderCanvas.Height = target;
        return true;
    }

    private void SyncHeaderScrollToBody()
    {
        var heightChanged = SyncHeaderCanvasHeight(CurrentBodyContentHeight());
        var targetY = BodyScroll.VerticalOffset;
        // If HeaderCanvas.Height just grew, HeaderScroll.ScrollableHeight is stale until the next
        // layout pass; calling ChangeView immediately would clamp to the old max and bounce
        // BodyScroll back via async HeaderScroll_ViewChanged.
        if (heightChanged && targetY > HeaderScroll.ScrollableHeight + 0.5)
        {
            ScheduleHeaderVerticalSync();
            return;
        }
        ApplyHeaderVerticalOffset(targetY);
        if (heightChanged)
            ScheduleHeaderVerticalSync();
    }

    private void ApplyHeaderVerticalOffset(double targetY)
    {
        if (_rebuilding || _syncingScroll || Math.Abs(HeaderScroll.VerticalOffset - targetY) <= 0.5)
            return;
        _expectedHeaderSyncY = targetY;
        _syncingScroll = true;
        HeaderScroll.ChangeView(0, targetY, null, disableAnimation: true);
        _syncingScroll = false;
    }

    private void ScheduleHeaderVerticalSync()
    {
        if (_headerSyncQueued) return;
        _headerSyncQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _headerSyncQueued = false;
                SyncHeaderCanvasHeight(CurrentBodyContentHeight());
                ApplyHeaderVerticalOffset(BodyScroll.VerticalOffset);
            }))
        {
            _headerSyncQueued = false;
        }
    }

    private void OnScrollViewportHeightChanged()
    {
        var changed = SyncHeaderCanvasHeight(CurrentBodyContentHeight());
        if (_rebuilding)
        {
            if (changed) ScheduleHeaderVerticalSync();
            return;
        }
        if (_syncingScroll)
            return;
        var targetY = BodyScroll.VerticalOffset;
        if (changed && targetY > HeaderScroll.ScrollableHeight + 0.5)
        {
            ScheduleHeaderVerticalSync();
            return;
        }
        ApplyHeaderVerticalOffset(targetY);
        if (changed)
            ScheduleHeaderVerticalSync();
    }

    private void OnScrollViewportWidthChanged()
    {
        if (_rebuilding || _session is null) return;
        var vw = BodyScroll.ViewportWidth;
        if (vw > 1 && Math.Abs(vw - _viewWidth) > 0.5)
            RequestViewportRedraw();
    }

    private double ContentX(double laneX) => laneX + _viewLeft;
    private double LocalX(double contentX) => contentX - _viewLeft;

    private void NotifyEdited()
    {
        Rebuild();
        TimelineEdited?.Invoke(this, EventArgs.Empty);
    }

    private sealed record ClipTag(string ClipId, int TrackIndex);
    private sealed record EdgeTag(string ClipId, int TrackIndex, bool Left);
    private sealed record DragClipSnapshot(
        string ClipId,
        int TrackIndex,
        int StartFrame,
        int DurationFrames,
        int TrimStartFrame,
        double Speed,
        ClipType MediaType);

    private enum TrackHeaderAction { ToggleMuted, ToggleHidden, ToggleLocked }
    private sealed record TrackHeaderActionTag(string TrackId, TrackHeaderAction Action);
    private sealed record TrackResizeState(string TrackId, double OriginalHeight);
    private sealed record TrackReorderState(string TrackId, int TargetIndex);

}
