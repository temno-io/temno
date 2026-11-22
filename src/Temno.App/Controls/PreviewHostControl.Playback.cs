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
    private bool CanPlayActiveTab()
    {
        if (_tabs.IsTimeline) return true;
        var type = _tabs.Active.MediaType;
        if (!SourceMonitorPlayback.CanPlay(type)) return false;
        return type is not ClipType.video || ActiveSourceFileExists();
    }

    private bool ActiveSourceFileExists()
    {
        var mediaRef = _tabs.Active.MediaRef;
        if (mediaRef is null) return false;
        var path = _session?.Resolver.ResolvePath(mediaRef);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private void CancelPrerolls()
    {
        var previous = _prerollCts;
        _prerollCts = new CancellationTokenSource();
        try { previous.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void ResetTransportState()
    {
        PlaybackTrace("lifecycle-reset-frame", PlayheadFrame);
        _decodeGeneration++;
        _decodeCts?.Cancel();
        CancelPrerolls();
        _audioCts?.Cancel();
        _audioPlayback.Clear();
        _audioProgram = null;
        _scrubbing = false;
        _resumeAfterScrub = false;
        _decodeBusy = false;
        _pendingFrame = null;
        StopTimer();
        _sourcePlayer.Pause();
        if (_clock?.IsPlaying == true)
            _clock.Pause();
        SetPlayIcon(isPlaying: false);
        SetAudioStatus(null);
        _displayFrame = -1;
    }

    private void EnsureClock(bool reset = false)
    {
        var fps = MonitorFps;
        var dur = MonitorDurationFrames;
        _playbackFrames.OutputFps = fps;
        if (_clock is null || reset)
        {
            _clock = new PlaybackClock(_time, fps, dur);
            _clock.FrameChanged += OnClockFrame;
            _clock.IsPlayingChanged += OnClockPlayingChanged;
        }
        else
        {
            _clock.Fps = fps;
            _clock.DurationFrames = dur;
        }
    }

    private void OnClockFrame(int frame)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StorePlayhead(frame);
            UpdateLabels(frame);
            if (TracingPlaybackTicks)
            {
                PlaybackTrace("tick", frame);
                TraceGcPause();
            }
            if (_followPlayhead && !UsesNativeVideo)
            {
                RequestFrame(frame);
                PrerollUpcomingClips(frame);
            }
            RaisePlayheadChanged(frame);
        });
    }

    /// <summary>
    /// WP-248. Starts the decoder for each clip that becomes visible within
    /// <see cref="PlaybackPreroll.LeadSeconds"/>, so a cut finds its first frame already decoded.
    /// Without this every clip boundary restarted ffmpeg on demand and the picture froze for
    /// 377–685 ms while audio played on. Starts run on the pool: spawning a process is synchronous
    /// and does not belong on the dispatcher. Playing the timeline only — a scrub or a paused
    /// playhead has no next cut to get ahead of.
    /// </summary>
    private void PrerollUpcomingClips(int frame)
    {
        if (!_tabs.IsTimeline || _scrubbing || _clock?.IsPlaying != true || _resourcesDisposed) return;
        var session = _session;
        if (session is null) return;

        var timeline = session.ActiveTimeline;
        var prerollToken = _prerollCts.Token;
        var targets = PlaybackPreroll.Plan(
            timeline, frame, PlaybackPreroll.LeadFrames(timeline.Fps), session.Resolver);
        foreach (var target in targets)
        {
            var frames = _playbackFrames;
            var mediaPath = target.MediaPath;
            var sourceSeconds = target.SourceSeconds;
            ObserveDecode(Task.Run(() => frames.PrerollAsync(mediaPath, sourceSeconds, prerollToken), prerollToken));
        }
    }

    private void OnClockPlayingChanged(bool isPlaying)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (isPlaying || _resourcesDisposed || _clock?.IsPlaying == true) return;
            StopTimer();
            _pendingNativePlay = false;
            if (UsesNativeVideo)
                _sourcePlayer.Pause();
            if (_audioPlayback.IsReady)
                _audioPlayback.PauseAtFrame(_clock?.CurrentFrame ?? 0);
            SetPlayIcon(isPlaying: false);
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private async Task PrepareAudioForScrubAsync()
    {
        if (UsesNativeSourceVideo)
            return;
        if (await PrepareAudioAsync().ConfigureAwait(true) && _scrubbing)
            _audioPlayback.PlayScrubAtFrame(PlayheadFrame);
    }

    private async Task StartPlaybackAsync()
    {
        PlaybackTrace("lifecycle-start-frame", PlayheadFrame);
        if (_clock?.IsPlaying == true) return;
        if (_preparingAudio)
        {
            _audioCts?.Cancel();
            _preparingAudio = false;
        }
        var startId = ++_playbackStartId;
        var playbackGeneration = _transportGeneration;
        EnsureClock();
        if (!string.IsNullOrEmpty(PlaybackTracePath))
        {
            PlaybackTrace("fps", MonitorFps);
            PlaybackTrace("dur", MonitorDurationFrames);
        }
        var frame = _clock?.CurrentFrame ?? 0;
        if (_tabs.IsTimeline)
            ConfigureTimelineNative(frame);
        var useNative = UsesNativeVideo;
        var mixWasapi = _tabs.IsTimeline || !useNative;
        _clock?.Play();
        if (useNative)
        {
            RouteNativeAudio();
            SeekNativeToMonitorFrame(frame);
            _pendingNativePlay = true;
            if (_nativeSourceOpened)
            {
                _pendingNativePlay = false;
                _sourcePlayer.Play();
            }
        }
        StartTimer();
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
        _preparingAudio = true;
        SetAudioPreparingUi(true);
        SetPlayIcon(isPlaying: true);
        try
        {
            var playbackSession = _session;
            var playbackMonitorId = _tabs.ActiveId;

            var audioReady = false;
            if (mixWasapi)
            {
                audioReady = await PrepareAudioAsync().ConfigureAwait(true);
            }

            if (PlaybackStartAborted(startId, playbackGeneration, playbackSession, playbackMonitorId))
            {
                return;
            }

            if (mixWasapi && audioReady)
                _audioPlayback.PlayFromFrame(_clock?.CurrentFrame ?? frame);

            SetPlayIcon(isPlaying: true);
        }
        finally
        {
            if (startId == _playbackStartId)
            {
                _preparingAudio = false;
                SetAudioPreparingUi(false);
            }
        }
    }

    private bool PlaybackStartAborted(
        int startId,
        int generation,
        ProjectSession? session,
        string monitorId)
        => startId != _playbackStartId
            || generation != _transportGeneration
            || !ReferenceEquals(session, _session)
            || !string.Equals(monitorId, _tabs.ActiveId, StringComparison.Ordinal);

    private void PauseTransport()
    {
        PlaybackTrace("lifecycle-pause-frame", PlayheadFrame);
        _transportGeneration++;
        CancelPrerolls();
        _audioCts?.Cancel();
        _preparingAudio = false;
        _pendingNativePlay = false;
        SetAudioPreparingUi(false);
        if (_clock is null)
        {
            SetPlayIcon(isPlaying: false);
            PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var native = UsesNativeVideo;
        var frame = native ? NativeMonitorFrame() : _clock.CurrentFrame;
        if (native)
        {
            _sourcePlayer.Pause();
            _clock.SeekToFrame(frame);
        }
        _clock.Pause();
        if (_audioPlayback.IsReady)
            _audioPlayback.PauseAtFrame(frame);
        StopTimer();
        SetPlayIcon(isPlaying: false);
        PlaybackStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetPlayIcon(bool isPlaying)
    {
        PlayIcon.Icon = isPlaying ? Temno.App.Controls.Shared.Icons.Pause : Temno.App.Controls.Shared.Icons.Play;
    }

    private async Task<bool> PrepareAudioAsync()
    {
        if (_audioPlayback.IsReady && _audioProgram is not null)
            return true;

        var session = _session;
        if (session is null)
        {
            SetAudioStatus("Audio unavailable: no timeline.");
            return false;
        }

        var timeline = _tabs.IsTimeline ? session.ActiveTimeline : _sourceAudioTimeline;
        if (timeline is null)
        {
            if (SourceMonitorPlayback.IsSequence(_tabs.Active.MediaType))
                SetAudioStatus(SourceMonitorPlayback.SequenceAudioStatus);
            else
                SetAudioStatus(null);
            return false;
        }

        _audioCts?.Cancel();
        _audioCts = new CancellationTokenSource();
        var cancellationToken = _audioCts.Token;
        SetAudioStatus("Preparing audio…");
        try
        {
            var decoded = await _audioDecoder.DecodeAsync(
                timeline,
                session.Resolver.ResolvePath,
                cancellationToken: cancellationToken,
                timelineLookup: SessionTimelineLookup.For(session)).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested || !ReferenceEquals(session, _session))
                return false;
            if (decoded.Segments.Count == 0)
            {
                var reason = decoded.Diagnostics.FirstOrDefault()?.Reason ?? "no active audio clips.";
                if (TimelineHasRepairableVideo(session, timeline))
                    reason = "clips lack linked audio: Edit > Restore Linked Audio";
                SetAudioStatus($"Audio unavailable: {ShortStatus(reason)}");
                return false;
            }

            var program = decoded.Mix(timeline);
            if (!_audioPlayback.TryLoad(program, out var error))
            {
                SetAudioStatus($"Audio device unavailable: {ShortStatus(error)}");
                return false;
            }

            _audioProgram = program;
            var appliedEffects = decoded.Effects.Count(effect => effect.State == TimelineAudioEffectState.Applied);
            var bypassedEffects = decoded.Effects.Count(effect => effect.State == TimelineAudioEffectState.Bypassed);
            var unavailableEffects = decoded.Effects.Count(effect => effect.State == TimelineAudioEffectState.Unavailable);
            var statusParts = new List<string> { "Audio ready" };
            if (appliedEffects > 0)
                statusParts.Add($"{appliedEffects} effect(s) applied");
            if (bypassedEffects > 0)
                statusParts.Add($"{bypassedEffects} effect(s) bypassed");
            if (unavailableEffects > 0)
                statusParts.Add($"{unavailableEffects} effect(s) unavailable");
            if (decoded.Diagnostics.Count > 0)
                statusParts.Add($"{decoded.Diagnostics.Count} source(s) skipped");
            SetAudioStatus(string.Join(" · ", statusParts));
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            SetAudioStatus($"Audio unavailable: {ShortStatus(ex.Message)}");
            return false;
        }
    }

    /// <summary>
    /// WP-164. Reflects one media file's proxy state into the preview status line. Only the file
    /// the playhead is actually decoding matters, so the newest non-trivial state wins rather than
    /// the control keeping a per-file list: the label is one line of transport chrome, not a
    /// manifest. "Proxy" tells the owner the picture is coming from the optimised copy; "building"
    /// says the original is still being decoded and the frame rate will improve on its own.
    /// </summary>
    private void OnProxyStateChanged(ProxyStatus status)
    {
        _ = status;
        // Derived from the whole set, not from the event that woke us: a timeline can mix a heavy
        // source with one that needs no proxy, and taking the newest event would let a
        // NotWarranted layer erase the label while a proxy is genuinely in use. Building outranks
        // ready outranks failed, because that is the order the owner cares about.
        var states = _proxies?.Statuses ?? [];
        string? text = null;
        if (states.Any(s => s.State == ProxyState.Generating)) text = "Proxy building…";
        else if (states.Any(s => s.State == ProxyState.Ready)) text = "Proxy";
        else if (states.Any(s => s.State == ProxyState.Failed)) text = "Proxy unavailable";

        if (_proxyStatus == text) return;
        _proxyStatus = text;
        // Repaint the label without waiting for the next decode, so a proxy that lands during a
        // pause is still visible.
        RefreshStatusLabel();
    }

    private void SetStatusText(string? message)
    {
        _statusMessage = string.IsNullOrWhiteSpace(message) ? null : message;
        RefreshStatusLabel();
    }

    private void RefreshStatusLabel()
    {
        if (StatusLabel is null) return;
        var text = ComposeStatusText();
        StatusLabel.Text = text;
        StatusLabel.Visibility = _showStatusLabel && !string.IsNullOrEmpty(text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private string ComposeStatusText()
    {
        if (_statusMessage is null)
            return _lastPresentedSize is not null && _tabs.IsTimeline ? (_proxyStatus ?? string.Empty) : string.Empty;
        if (!_tabs.IsTimeline && _lastPresentedSize is not null && _proxyStatus is not null)
            return $"{_statusMessage} · {_proxyStatus}";
        return _statusMessage;
    }

    private void SetAudioStatus(string? status)
    {
        AudioStatusLabel.Text = status ?? string.Empty;
        AudioStatusLabel.Visibility = string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Diagnostics-only mirror of <c>EditCommands.RestoreLinkedAudio</c>'s predicate: is there a video
    /// clip on a video track whose media has audio but which has no audio-track partner yet? Lets the
    /// empty-mix message name the fix instead of stating a technically-true useless fact (WP-162).
    /// </summary>
    private static bool TimelineHasRepairableVideo(ProjectSession session, Timeline timeline)
    {
        foreach (var track in timeline.Tracks)
        {
            if (track.Muted || track.Hidden || track.Type != ClipType.video) continue;
            foreach (var clip in track.Clips)
            {
                if (clip.MediaType != ClipType.video) continue;
                if (session.Manifest.Entries.FirstOrDefault(e => e.Id == clip.MediaRef)?.HasAudio != true)
                    continue;
                if (!string.IsNullOrEmpty(clip.LinkGroupId)
                    && timeline.Tracks.Any(t => t.Type == ClipType.audio
                        && t.Clips.Any(c => c.LinkGroupId == clip.LinkGroupId)))
                    continue;
                return true;
            }
        }
        return false;
    }

    private void SetAudioPreparingUi(bool preparing)
    {
        if (AudioPrepareRing is null) return;
        AudioPrepareRing.IsActive = preparing;
        AudioPrepareRing.Visibility = preparing ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string ShortStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown error";
        return value.Length > 52 ? value[..52] + "…" : value;
    }

    private void StartTimer()
    {
        StopTimer();
        PlaybackTimerResolution.Acquire();
        _timer = DispatcherQueue.CreateTimer();
        // Sample the clock more often than frames are due; dispatcher jitter must not skip frames.
        // PlaybackClock only emits FrameChanged when the logical frame actually advances.
        _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / (2 * Math.Max(30, MonitorFps)));
        _timer.Tick += (_, _) => TickTransport();
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer is null)
        {
            PlaybackTimerResolution.Release();
            return;
        }
        _timer.Stop();
        _timer = null;
        PlaybackTimerResolution.Release();
    }

    /// <summary>
    /// WP-163 (step c). The 30 Hz transport timer fired on a steady ~47 ms grid (73% of
    /// intervals in 45–50 ms on traces with and without decode load) — the repeating
    /// <c>DispatcherQueueTimer</c> due time quantizes up to the process timer tick, so the
    /// preview could never be asked for more than ~21 demands/s whatever the decode cost.
    /// Holding 1 ms resolution while the transport runs restores the 33 ms cadence; every
    /// start path funnels through <see cref="StartTimer"/> and every stop through
    /// <see cref="StopTimer"/>, so the request is strictly scoped to active playback.
    /// Standard media-player practice; the export path never touches it.
    /// </summary>
    private static class PlaybackTimerResolution
    {
        private static int _held;

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);

        [System.Runtime.InteropServices.DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);

        public static void Acquire()
        {
            if (System.Threading.Interlocked.Exchange(ref _held, 1) == 0)
                timeBeginPeriod(1);
        }

        public static void Release()
        {
            if (System.Threading.Interlocked.Exchange(ref _held, 0) == 1)
                timeEndPeriod(1);
        }
    }

    private void RequestFrame(int frame, bool force = false)
    {
        if (_resourcesDisposed) return;
        if (UsesNativeVideo)
        {
            _pendingFrame = null;
            return;
        }

        if (!force && frame == _displayFrame)
            return;

        if (_decodeBusy)
        {
            if (TracingPlayback)
                PlaybackTrace("coalesce", frame);
            _pendingFrame = frame;
            return;
        }

        if (TracingPlayback)
            PlaybackTrace("request", frame);
        ObserveDecode(RefreshFrameAsync(frame));
    }

    /// <summary>
    /// WP-161. Env-gated playback trace: one line per clock tick, decode request, UI marshal,
    /// presented frame and coalesced frame — only when <c>TEMNO_PLAYBACK_TRACE</c> names a file,
    /// and only while the timeline is playing (scrubbing and native playback leave their own
    /// shapes: scrub has no ticks, native has ticks but no decode lines). Same measurement seam
    /// as <c>TEMNO_REFRESH_TRACE</c> (WP-153): a dropped-frame claim that cannot be counted
    /// from outside the process is unfalsifiable. Timestamps are Stopwatch ticks; the file opens
    /// with a <c>freq</c> line. <c>TimelineControl</c> feeds lane redraws and strip extractions
    /// into the same file through this method. A diagnostic must never break playback, so every
    /// write is guarded.
    /// </summary>
    internal static void PlaybackTrace(string evt, int value)
    {
        if (string.IsNullOrEmpty(PlaybackTracePath)) return;
        // WP-163: restart events arrive on pool threads from concurrent pipes; the UI-thread
        // sites never contend, but two restarts racing must not interleave one line.
        lock (_traceLock)
        {
            try
            {
                if (!_playbackTraceHeader)
                {
                    _playbackTraceHeader = true;
                    File.AppendAllText(PlaybackTracePath, $"freq {System.Diagnostics.Stopwatch.Frequency}{Environment.NewLine}");
                }
                File.AppendAllText(PlaybackTracePath, $"{System.Diagnostics.Stopwatch.GetTimestamp()} {evt} {value}{Environment.NewLine}");
            }
            catch { /* a diagnostic must never break playback */ }
        }
    }

    /// <summary>
    /// WP-248. Cumulative GC pause and gen-2 count, written only when they move, so a UI-thread
    /// stall in the trace can be told apart from a GC suspending every thread.
    /// </summary>
    private static void TraceGcPause()
    {
        var pause = (int)GC.GetTotalPauseDuration().TotalMilliseconds;
        if (pause != _tracedGcPauseMs)
        {
            _tracedGcPauseMs = pause;
            PlaybackTrace("gcpause", pause);
        }

        var gen2 = GC.CollectionCount(2);
        if (gen2 != _tracedGen2)
        {
            _tracedGen2 = gen2;
            PlaybackTrace("gen2", gen2);
        }
    }

    private static int _tracedGcPauseMs = -1;
    private static int _tracedGen2 = -1;

    private static readonly object _traceLock = new();

    private static readonly string? PlaybackTracePath =
        EnvCompat.Get("TEMNO_PLAYBACK_TRACE");

    private static bool _playbackTraceHeader;

    /// <summary>True while a playback trace is being recorded on the timeline.</summary>
    internal bool TracingPlayback =>
        !string.IsNullOrEmpty(PlaybackTracePath)
        && _clock?.IsPlaying == true
        && _tabs.IsTimeline
        && !UsesNativeVideo;

    /// <summary>True while a playback trace is being recorded, native path included (ticks only).</summary>
    internal bool TracingPlaybackTicks =>
        !string.IsNullOrEmpty(PlaybackTracePath)
        && _clock?.IsPlaying == true
        && _tabs.IsTimeline;

    /// <summary>
    /// The decode task is deliberately not awaited, so a fault in it is invisible until the finalizer
    /// rethrows it as an unobserved task exception. That is how WP-156 stayed hidden: every scrub that
    /// superseded an in-flight decode threw <c>RPC_E_WRONG_THREAD</c> into this task and dropped the
    /// pending frame, and nothing said so.
    /// </summary>
    private static void ObserveDecode(Task task)
    {
        _ = task.ContinueWith(
            static t => CrashLog.Write("PreviewHostControl decode", t.Exception),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task RefreshFrameAsync(int frame)
    {
        if (UsesNativeVideo)
        {
            _pendingFrame = null;
            return;
        }

        if (_session is null)
        {
            ShowPlaceholder();
            return;
        }

        if (frame == _displayFrame && (PreviewImage.Source is not null || Placeholder.Visibility == Visibility.Visible))
        {
            DrainPendingFrame();
            return;
        }

        _decodeBusy = true;
        var decodeId = ++_decodeGeneration;
        if (!IsPlaying || _scrubbing)
            _decodeCts?.Cancel();
        _decodeCts = new CancellationTokenSource();
        var ct = _decodeCts.Token;
        if (PreviewImage.Source is null && Placeholder.Visibility != Visibility.Visible)
            SetStatusText("Decoding…");

        try
        {
            if (!_tabs.IsTimeline)
            {
                await RefreshSourceFrameAsync(frame, ct).ConfigureAwait(true);
                return;
            }

            if (_provider is null)
            {
                ShowPlaceholder();
                return;
            }

            // The transport parks on the exclusive out-point, which no clip contains.
            var picture = TimelineGeometry.MonitorPictureFrame(frame, _session.ActiveTimeline.TotalFrames);
            using var video = await _provider.GetCompositedFrameAsync(_session.ActiveTimeline, picture, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            if (video is null)
            {
                await EnqueueUiAsync(() =>
                {
                    if (ct.IsCancellationRequested || UsesNativeVideo) return;
                    _lastPresentedSize = null;
                    ShowPlaceholder();
                    SetStatusText("No media under playhead");
                    UpdatePreviewResolutionLabel();
                    _displayFrame = frame;
                }).ConfigureAwait(true);
                return;
            }

            await PresentSoftwareFrameAsync(video, frame, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // superseded
        }
        catch (Exception ex)
        {
            // Runs on whichever thread the fault surfaced on -- the compositor await below resumes on
            // the pool. Touching StatusLabel here directly replaced the decoder's message with a
            // wrong-thread COM error, so the status bar could never say why a frame failed.
            await ShowDecodeErrorAsync(
                ex.Message.Length > 48 ? ex.Message[..48] + "…" : ex.Message).ConfigureAwait(false);
        }
        finally
        {
            CompleteDecode(decodeId);
        }
    }

    private async Task ShowDecodeErrorAsync(string message)
    {
        try
        {
            await EnqueueUiAsync(() =>
            {
                if (UsesNativeVideo) return;
                _lastPresentedSize = null;
                ShowPlaceholder();
                SetStatusText(message);
                UpdatePreviewResolutionLabel();
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            // Dispatcher queue shut down; the window is going away.
        }
    }

    /// <summary>
    /// WP-156. The compositor await keeps <c>ConfigureAwait(false)</c> on purpose:
    /// <see cref="PresentSoftwareFrameAsync"/> runs <see cref="EncodeDisplayBgra"/> — a full-frame
    /// BGRA conversion, 14.7 MB at 2560×1440 — before it marshals, and resuming on the UI thread
    /// would move that copy onto it once per decoded frame. The cost of that choice is that a
    /// cancelled or faulted decode runs its <c>finally</c> on a pool thread, and
    /// <see cref="DrainPendingFrame"/> re-enters <see cref="RefreshFrameAsync"/>, which reads
    /// <c>PreviewImage.Source</c>. Reading a XAML member off-thread throws, the throw lands in an
    /// un-awaited task, and the pending frame dies with it — the preview stops updating exactly while
    /// the user is scrubbing. Marshalling here keeps both properties.
    /// </summary>
    private void CompleteDecode(int decodeId)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            if (DispatcherQueue.TryEnqueue(() => CompleteDecode(decodeId))) return;
            if (decodeId == _decodeGeneration)
                _decodeBusy = false; // Queue is shut down; at least do not wedge the flag.
            return;
        }

        // A superseded/cancelled decode must not clear _decodeBusy for a newer in-flight decode.
        if (decodeId != _decodeGeneration) return;
        _decodeBusy = false;
        DrainPendingFrame();
    }

    private async Task RefreshSourceFrameAsync(int frame, CancellationToken ct)
    {
        var tab = _tabs.Active;
        var mediaRef = tab.MediaRef;
        if (mediaRef is null)
        {
            ShowPlaceholder("No source");
            _displayFrame = frame;
            return;
        }

        var type = tab.MediaType ?? ClipType.video;
        var path = _session?.Resolver.ResolvePath(mediaRef);
        var fps = MonitorFps;

        if (type is ClipType.audio)
        {
            _lastPresentedSize = null;
            ShowPlaceholder("Audio source");
            SetStatusText(tab.Title);
            UpdatePreviewResolutionLabel();
            _displayFrame = frame;
            return;
        }

        if (type is ClipType.lottie)
        {
            _lastPresentedSize = null;
            ShowPlaceholder("Lottie — plays after flatten on the timeline");
            SetStatusText(tab.Title);
            UpdatePreviewResolutionLabel();
            _displayFrame = frame;
            return;
        }

        if (type is ClipType.text)
        {
            _lastPresentedSize = null;
            ShowPlaceholder("Text source");
            SetStatusText(tab.Title);
            UpdatePreviewResolutionLabel();
            _displayFrame = frame;
            return;
        }

        // WP-173: this branch used to sit *after* the file-exists check and then composite
        // a nested timeline. A real sequence clip's mediaRef is a timeline id, which has no
        // manifest path, so that compositor never ran. A contract-invalid type=sequence
        // media entry would play picture (or a blank) with no audio and no status. The
        // surface now matches Lottie/text: honest placeholder, Play disabled, status set.
        if (type is ClipType.sequence)
        {
            _lastPresentedSize = null;
            ShowPlaceholder(SourceMonitorPlayback.SequencePlaceholder);
            SetStatusText(tab.Title);
            UpdatePreviewResolutionLabel();
            _displayFrame = frame;
            return;
        }

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            _lastPresentedSize = null;
            ShowPlaceholder("Source offline");
            SetStatusText(tab.Title);
            UpdatePreviewResolutionLabel();
            _displayFrame = frame;
            return;
        }

        if (type is ClipType.image)
        {
            _lastPresentedSize = null;
            ShowSoftwarePreview();
            PreviewImage.Source = new BitmapImage(new Uri(path));
            Placeholder.Visibility = Visibility.Collapsed;
            SetStatusText(tab.Title);
            UpdatePreviewResolutionLabel();
            _displayFrame = frame;
            return;
        }

        if (type is ClipType.video)
        {
            if (UsesNativeSourceVideo)
            {
                await EnqueueUiAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    _lastPresentedSize = null;
                    ShowNativeSource();
                    SetStatusText("Source playback");
                    UpdatePreviewResolutionLabel();
                    _displayFrame = frame;
                }).ConfigureAwait(true);
                return;
            }

            var sourceSeconds = CaptionImporter.SecondsFromFrame(
                TimelineGeometry.MonitorPictureFrame(frame, _sourceDurationFrames), fps);
            using var decoded = await _playbackFrames.GetFrameAsync(path, sourceSeconds, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            await PresentSoftwareFrameAsync(decoded, frame, ct, tab.Title).ConfigureAwait(true);
            return;
        }

        var seconds = CaptionImporter.SecondsFromFrame(
            TimelineGeometry.MonitorPictureFrame(frame, _sourceDurationFrames), fps);
        using var video = await _playbackFrames.GetFrameAsync(path, seconds, ct).ConfigureAwait(false);
        if (ct.IsCancellationRequested) return;

        await PresentSoftwareFrameAsync(video, frame, ct).ConfigureAwait(true);
    }

    private void DrainPendingFrame()
    {
        if (UsesNativeVideo)
        {
            _pendingFrame = null;
            return;
        }

        if (_pendingFrame is not { } pending) return;
        if (pending == _displayFrame)
        {
            _pendingFrame = null;
            return;
        }
        _pendingFrame = null;
        _ = RefreshFrameAsync(pending);
    }

    private void ShowPlaceholder(string? message = null)
    {
        ShowSoftwarePreview();
        PreviewImage.Source = null;
        Placeholder.Visibility = Visibility.Visible;
        PlaceholderText.Text = string.IsNullOrWhiteSpace(message) ? "No frame at playhead" : message;
    }

    private Task EnqueueUiAsync(Action action)
    {
        // Already on the UI thread: run now. Posting would defer the work past the caller's own
        // `return`, which silently reorders every call site that is sometimes on-thread.
        if (DispatcherQueue.HasThreadAccess)
        {
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
        {
            tcs.TrySetCanceled();
        }

        return tcs.Task;
    }

    private async Task PresentSoftwareFrameAsync(VideoFrame video, int frame, CancellationToken ct, string? status = null)
    {
        var width = video.Width;
        var height = video.Height;
        var length = checked(width * height * 4);
        // Fast path: frames already decoded in BGRA can marshal straight from the caller's owned
        // VideoFrame without renting a second LOH buffer or copying 33 MB twice at 4K.
        if (string.Equals(video.PixelFormat, "bgra", StringComparison.OrdinalIgnoreCase))
        {
            if (video.Data.Length < length)
                throw new ArgumentException("A complete four-channel frame is required.");
            await PresentBgraBufferAsync(video.Data, length, width, height, frame, ct, status).ConfigureAwait(true);
            return;
        }

        // Rent an exact-sized pooled buffer per present so overlapping cancelled/superseded decodes
        // cannot tear a shared buffer and 4K frames never trigger 64 MiB ArrayPool Gen2 GC churn.
        using var converted = _displayPool.Rent(width, height, "bgra");
        PreviewPixels.CopyToBgra(video, converted.Data.AsSpan(0, length));
        await PresentBgraBufferAsync(converted.Data, length, width, height, frame, ct, status).ConfigureAwait(true);
    }

    private Task PresentBgraBufferAsync(byte[] bgra, int length, int width, int height, int frame, CancellationToken ct, string? status)
        => EnqueueUiAsync(() =>
        {
            if (ct.IsCancellationRequested || UsesNativeVideo) return;
            var tracing = TracingPlayback;
            if (tracing)
                PlaybackTrace("marshal", frame);
            ShowSoftwarePreview();
            PreviewImage.Source = BgraToBitmap(bgra, length, width, height);
            Placeholder.Visibility = Visibility.Collapsed;
            _lastPresentedSize = (width, height);
            SetStatusText(status);
            UpdatePreviewResolutionLabel();
            _displayFrame = frame;
            if (tracing)
                PlaybackTrace("present", frame);
        });

    private static byte[] EncodeDisplayBgra(VideoFrame frame)
    {
        var bgra = new byte[checked(frame.Width * frame.Height * 4)];
        PreviewPixels.CopyToBgra(frame, bgra);
        return bgra;
    }
}
