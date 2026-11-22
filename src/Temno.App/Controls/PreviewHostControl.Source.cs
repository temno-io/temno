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
    public void OpenSourceTab(string mediaRef, string name, ClipType type, double durationSeconds = 0, int sourceFrame = 0)
    {
        if (_session is null) return;
        if (IsPlaying) PauseTransport();
        RememberPlayhead();
        _tabs.OpenMedia(mediaRef, name, type);
        ConfigureSource(mediaRef, type, durationSeconds);
        _sourcePlayhead = Math.Clamp(sourceFrame, 0, Math.Max(0, _sourceDurationFrames - 1));
        ApplyActiveMonitor();
    }

    public void ActivateTimelineTab(int? playhead = null)
    {
        if (playhead is int frame)
            _timelinePlayhead = Math.Max(0, frame);

        if (_tabs.IsTimeline)
        {
            if (playhead is int seek)
                PlayheadFrame = seek;
            return;
        }

        if (IsPlaying) PauseTransport();
        RememberPlayhead();
        _tabs.Select(PreviewMonitorTab.TimelineId);
        ApplyActiveMonitor();
    }

    public void TogglePlay()
    {
        if (_clock?.IsPlaying == true)
        {
            PauseTransport();
            return;
        }

        PlayIfStopped();
    }

    /// <summary>
    /// Starts the active monitor if it is stopped. Used after switching back to the
    /// program tab so a leftover source-tab prepare does not steal Space as pause.
    /// </summary>
    public void PlayIfStopped()
    {
        if (_clock?.IsPlaying == true) return;
        if (!CanPlayActiveTab()) return;
        EnsureClock();
        _ = StartPlaybackAsync();
    }

    public void Step(int delta)
    {
        EnsureClock();
        if (_clock!.IsPlaying) PauseTransport();
        PlayheadFrame = Math.Max(0, PlayheadFrame + delta);
    }

    public void GoToStart()
    {
        if (_clock?.IsPlaying == true) PauseTransport();
        PlayheadFrame = 0;
    }

    public void GoToEnd()
    {
        if (_clock?.IsPlaying == true) PauseTransport();
        PlayheadFrame = Math.Max(0, MonitorDurationFrames - 1);
    }

    /// <summary>WP-66: still overlay without changing the active tab.</summary>
    public void ShowStill(BitmapImage image)
    {
        _lastPresentedSize = null;
        ShowSoftwarePreview();
        PreviewImage.Source = image;
        Placeholder.Visibility = Visibility.Collapsed;
        SetStatusText("Media still");
        UpdatePreviewResolutionLabel();
        _displayFrame = -1;
    }

    public void BeginScrub()
    {
        if (_scrubbing) return;
        _transportGeneration++;
        _audioCts?.Cancel();
        _scrubbing = true;
        _resumeAfterScrub = IsPlaying;
        if (IsPlaying)
            PauseTransport();
        _ = PrepareAudioForScrubAsync();
    }

    public void EndScrub()
    {
        if (!_scrubbing) return;
        var resume = _resumeAfterScrub;
        _scrubbing = false;
        _resumeAfterScrub = false;
        _audioPlayback.StopScrub();
        if (UsesNativeVideo)
            SeekNativeToMonitorFrame(PlayheadFrame);
        else
            RequestFrame(PlayheadFrame, force: true);
        if (resume)
            _ = StartPlaybackAsync();
    }

    private int MonitorFps => Math.Max(1, _session?.ActiveTimeline.Fps ?? 30);

    private bool UsesNativeSourceVideo => !_tabs.IsTimeline
        && _tabs.Active.MediaType == ClipType.video
        && _nativeSourcePath is not null;

    private bool UsesNativeTimelineVideo => _tabs.IsTimeline
        && _nativeTimelineClip is not null
        && _nativeSourcePath is not null;

    private bool UsesNativeVideo => UsesNativeSourceVideo || UsesNativeTimelineVideo;

    private int MonitorDurationFrames
    {
        get
        {
            if (_tabs.IsTimeline)
            {
                var tl = _session?.ActiveTimeline;
                return Math.Max(tl?.TotalFrames ?? 0, MonitorFps);
            }

            return Math.Max(1, _sourceDurationFrames);
        }
    }

    private void RememberPlayhead()
    {
        var frame = _clock?.CurrentFrame ?? 0;
        StorePlayhead(frame);
    }

    private void StorePlayhead(int frame)
    {
        if (_tabs.IsTimeline)
            _timelinePlayhead = frame;
        else
            _sourcePlayhead = frame;
    }

    private void RaisePlayheadChanged(int frame)
    {
        if (_tabs.IsTimeline)
            PlayheadChanged?.Invoke(this, frame);
    }

    private void ApplyActiveMonitor()
    {
        _transportGeneration++;
        _decodeGeneration++;
        _decodeCts?.Cancel();
        CancelPrerolls();
        _audioCts?.Cancel();
        _audioPlayback.Clear();
        _audioProgram = null;
        _decodeBusy = false;
        _pendingFrame = null;
        _displayFrame = -1;
        _lastPresentedSize = null;
        SetAudioStatus(null);
        _playbackFrames.Reset();
        SyncPreviewResolutionPipeline();
        if (!_tabs.IsTimeline)
            SyncSourceFromActiveTab();
        else
        {
            _sourceAudioTimeline = null;
            ConfigureTimelineNative(_timelinePlayhead);
        }

        EnsureClock(reset: false);
        PlayheadFrame = _tabs.IsTimeline ? _timelinePlayhead : _sourcePlayhead;
        RebuildTabStrip();
        UpdatePreviewResolutionLabel();
        MonitorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SyncSourceFromActiveTab()
    {
        var tab = _tabs.Active;
        if (tab.Kind != PreviewMonitorKind.MediaAsset || tab.MediaRef is null)
        {
            _sourceAudioTimeline = null;
            ClearNativeSource();
            return;
        }

        var duration = _session?.Resolver.Entry(tab.MediaRef)?.Duration ?? 0;
        var type = tab.MediaType ?? ClipType.video;
        ConfigureSource(tab.MediaRef, type, duration);
        ConfigureNativeSource(tab.MediaRef, type);
    }

    private void ConfigureSource(string mediaRef, ClipType type, double durationSeconds)
    {
        var fps = MonitorFps;
        _sourceDurationFrames = type is ClipType.image or ClipType.text
            ? 1
            : Math.Max(1, CaptionImporter.FrameFromSeconds(Math.Max(0, durationSeconds), fps));
        if (SourceMonitorPlayback.BuildsSourceAudioTimeline(type))
        {
            _sourceAudioTimeline = BuildSourceAudioTimeline(mediaRef, type, fps, _sourceDurationFrames);
            return;
        }

        _sourceAudioTimeline = null;
        // WP-173: sequence is clip-only in the package contract, so a source tab of that
        // type is not a media-library asset. Explain instead of mixing silence.
        if (SourceMonitorPlayback.IsSequence(type))
            SetAudioStatus(SourceMonitorPlayback.SequenceAudioStatus);
    }

    private void ConfigureNativeSource(string mediaRef, ClipType type)
    {
        // MediaPlayer applies driver video contrast even when frames are copied offscreen.
        // FFmpeg hardware decode plus explicit RGB conversion keeps both monitors faithful.
        ClearNativeSource();
    }

    private void ConfigureTimelineNative(int frame)
    {
        if (_nativeSourcePath is not null)
            ClearNativeSource();
        ShowSoftwarePreview();
    }

    /// <summary>
    /// Timeline native video is picture-only: mix (BGM, linked dialogue, clip volume) is WASAPI.
    /// Source-monitor native keeps the file's embedded audio.
    /// </summary>
    private void RouteNativeAudio()
    {
        if (UsesNativeTimelineVideo)
        {
            _sourcePlayer.IsMuted = true;
            _sourcePlayer.Volume = 0;
            return;
        }

        _sourcePlayer.IsMuted = false;
        _sourcePlayer.Volume = 1;
    }

    private bool TryGetNativeTimelineClip(int frame, out Clip clip, out string path)
    {
        clip = null!;
        path = null!;
        var session = _session;
        if (session is null) return false;
        // Kept with the dormant MediaPlayer transport until a neutral presenter is qualified.
        if (TimelineHasVisibleText(frame))
            return false;

        var layers = TimelineFrameResolver.Resolve(session.ActiveTimeline, frame, session.Resolver);
        if (layers.Count != 1) return false;
        var layer = layers[0];
        if (layer.MediaType != ClipType.video
            || string.IsNullOrWhiteSpace(layer.MediaPath)
            || !File.Exists(layer.MediaPath))
        {
            return false;
        }

        var found = FindTimelineClip(layer.ClipId);
        if (found is null || found.DurationFrames <= 0 || ClipNeedsSoftwareComposite(found, frame)
            || NativePreviewEligibility.HasOverlappingVisuals(session.ActiveTimeline, found))
            return false;
        clip = found;
        path = layer.MediaPath;
        return true;
    }

    private Clip? FindTimelineClip(string clipId)
    {
        var timeline = _session?.ActiveTimeline;
        if (timeline is null) return null;
        foreach (var track in timeline.Tracks)
        {
            var match = track.Clips.FirstOrDefault(c => c.Id == clipId);
            if (match is not null) return match;
        }

        return null;
    }

    private bool TimelineHasVisibleText(int frame)
    {
        var timeline = _session?.ActiveTimeline;
        if (timeline is null) return false;
        foreach (var track in timeline.Tracks)
        {
            if (track.Hidden) continue;
            foreach (var clip in track.Clips)
            {
                if (!clip.Contains(frame)) continue;
                if (clip.MediaType != ClipType.text && string.IsNullOrEmpty(clip.TextContent))
                    continue;
                if (string.IsNullOrEmpty(clip.TextContent)) continue;
                if (clip.OpacityAt(frame) <= 0) continue;
                return true;
            }
        }

        return false;
    }

    private static bool ClipNeedsSoftwareComposite(Clip clip, int frame)
    {
        if (clip.Effects is { Count: > 0 } effects
            && effects.Any(effect =>
                !string.IsNullOrEmpty(effect.Type)
                && !string.Equals(effect.Type, Temno.Core.Models.Clip.DenoiseEffectType, StringComparison.Ordinal)))
        {
            return true;
        }

        if (clip.BlendMode is { Length: > 0 } blend
            && !blend.Equals("normal", StringComparison.OrdinalIgnoreCase)
            && !blend.Equals("sourceOver", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (clip.OpacityAt(frame) < 0.999) return true;
        if (clip.FadeInFrames > 0 || clip.FadeOutFrames > 0) return true;
        if (clip.OpacityTrack is { IsActive: true }) return true;
        if (clip.PositionTrack is { IsActive: true }) return true;
        if (clip.ScaleTrack is { IsActive: true }) return true;
        if (clip.RotationTrack is { IsActive: true }) return true;
        if (clip.CropTrack is { IsActive: true }) return true;
        if (!clip.CropAt(frame).IsIdentity) return true;

        var transform = clip.TransformAt(frame);
        return Math.Abs(transform.CenterX - 0.5) > 0.001
            || Math.Abs(transform.CenterY - 0.5) > 0.001
            || Math.Abs(transform.Width - 1) > 0.001
            || Math.Abs(transform.Height - 1) > 0.001
            || Math.Abs(transform.Rotation) > 0.001
            || transform.FlipHorizontal
            || transform.FlipVertical;
    }

    private void OpenNativePath(string path, int seekFrame, double playbackRate)
    {
        var fullPath = Path.GetFullPath(path);
        if (string.Equals(_nativeSourcePath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            ApplyNativePlaybackRate(playbackRate);
            if (_nativeSourceOpened)
                SeekNativeToMonitorFrame(seekFrame);
            else
                _pendingNativeSeekFrame = seekFrame;
            ShowNativeSource();
            return;
        }

        var keepClip = _nativeTimelineClip;
        ClearNativeSource();
        _nativeTimelineClip = keepClip;
        try
        {
            _sourceMedia = NativeMediaSource.CreateFromUri(new Uri(fullPath));
            _nativeSourcePath = fullPath;
            _pendingNativeSeekFrame = seekFrame;
            ApplyNativePlaybackRate(playbackRate);
            _sourcePlayer.Source = _sourceMedia;
            ShowNativeSource();
        }
        catch (Exception ex)
        {
            ClearNativeSource();
            SetStatusText(ShortStatus(ex.Message));
            RequestFrame(seekFrame, force: true);
        }
    }

    private void ClearNativeSource()
    {
        _sourcePlayer.Pause();
        _sourcePlayer.Source = null;
        _sourceMedia?.Dispose();
        _sourceMedia = null;
        _nativeSourcePath = null;
        _nativeTimelineClip = null;
        _pendingNativeSeekFrame = null;
        _pendingNativePlay = false;
        _nativeSourceOpened = false;
        _nativePlaybackRate = 1.0;
        _awaitingNativeClipSettle = false;
        _sourcePlayer.IsMuted = false;
        _sourcePlayer.Volume = 1;
        ShowSoftwarePreview();
    }

    private void ShowNativeSource()
    {
        _decodeCts?.Cancel();
        _pendingFrame = null;
        if (!ReferenceEquals(SourceVideoPlayer.MediaPlayer, _sourcePlayer))
            SourceVideoPlayer.SetMediaPlayer(_sourcePlayer);
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        SourceVideoPlayer.Visibility = Visibility.Visible;
        Placeholder.Visibility = Visibility.Collapsed;
        _lastPresentedSize = null;
        SetStatusText(_tabs.IsTimeline ? "Timeline playback" : "Source playback");
        UpdatePreviewResolutionLabel();
    }

    private void ShowSoftwarePreview()
    {
        if (ReferenceEquals(SourceVideoPlayer.MediaPlayer, _sourcePlayer))
            SourceVideoPlayer.SetMediaPlayer(null);
        SourceVideoPlayer.Visibility = Visibility.Collapsed;
        PreviewImage.Visibility = Visibility.Visible;
        // Leaving native playback clears both the image and the placeholder. _displayFrame still
        // holds the frame the MediaPlayer was showing, so RequestFrame would short-circuit on
        // "already displayed" and leave an empty canvas. Force the next request to decode.
        if (PreviewImage.Source is null && Placeholder.Visibility != Visibility.Visible)
            _displayFrame = -1;
    }

    /// <summary>
    /// WP-155. <c>MediaPlayer.Source</c> is asynchronous: until <c>MediaOpened</c> fires there is no
    /// media engine behind <c>PlaybackSession</c>, and every member on it answers <c>E_FAIL</c>. The
    /// caller here is usually the <see cref="PlayheadFrame"/> setter running inside a XAML input
    /// callback, so that throw left the process as a stowed exception - deleting a clip that dropped
    /// the timeline to one native-eligible layer killed the app twice for the owner on 2026-09-02.
    /// <c>_pendingNativeSeekFrame</c> is set first and <see cref="SourcePlayer_MediaOpened"/> replays
    /// it, so deferring costs nothing.
    /// </summary>
    private void SeekNativeToMonitorFrame(int frame)
    {
        _pendingNativeSeekFrame = frame;
        if (!UsesNativeVideo || !_nativeSourceOpened)
            return;

        try
        {
            var session = _sourcePlayer.PlaybackSession;
            if (!session.CanSeek)
                return;

            var seconds = NativeSecondsForMonitorFrame(frame);
            var duration = session.NaturalDuration;
            if (duration > TimeSpan.Zero)
                seconds = Math.Min(seconds, duration.TotalSeconds);
            seconds = Math.Max(0, seconds);
            if (_clock?.IsPlaying == true && !_scrubbing)
            {
                var pos = session.Position.TotalSeconds;
                if (Math.Abs(pos - seconds) < 0.75 / MonitorFps)
                {
                    _pendingNativeSeekFrame = null;
                    return;
                }
            }

            session.Position = TimeSpan.FromSeconds(seconds);
        }
        catch (Exception ex) when (IsNativePlayerFault(ex))
        {
            // Engine torn down under us (MediaFailed, or a source swap mid-call). The pending frame
            // stays set so MediaOpened replays it; software preview covers the gap.
            return;
        }

        _pendingNativeSeekFrame = null;
        if (_clock?.IsPlaying != true && !_pendingNativePlay)
            PresentPausedNativeFrame();
    }

    /// <summary>Applies the rate now if the engine exists, otherwise on <c>MediaOpened</c>.</summary>
    private void ApplyNativePlaybackRate(double rate)
    {
        _nativePlaybackRate = rate;
        if (!_nativeSourceOpened) return;
        try
        {
            _sourcePlayer.PlaybackSession.PlaybackRate = rate;
        }
        catch (Exception ex) when (IsNativePlayerFault(ex))
        {
            // Reapplied by MediaOpened if the engine comes back.
        }
    }

    /// <summary>
    /// A WinRT media object whose engine is absent or torn down. Narrow on purpose: a bug in our own
    /// arithmetic must still surface, and the crash log added with this package is what shows it.
    /// </summary>
    private static bool IsNativePlayerFault(Exception ex)
        => ex is COMException or ObjectDisposedException or InvalidOperationException;

    /// <summary>
    /// WinRT MediaPlayer often keeps the last decoded frame when paused after a Position seek.
    /// A muted play/pause forces the surface to the new sample without starting our clock.
    /// </summary>
    private void PresentPausedNativeFrame()
    {
        try
        {
            var muted = _sourcePlayer.IsMuted;
            _sourcePlayer.IsMuted = true;
            _sourcePlayer.Play();
            _sourcePlayer.Pause();
            _sourcePlayer.IsMuted = muted;
        }
        catch
        {
            // Presenter refresh is best-effort; clock/playhead already moved.
        }
    }

    private double NativeSecondsForMonitorFrame(int frame)
    {
        if (UsesNativeTimelineVideo && _nativeTimelineClip is { } clip)
            return Math.Max(0, TimelineFrameResolver.SourceSecondsAt(clip, frame, MonitorFps));
        return CaptionImporter.SecondsFromFrame(frame, MonitorFps);
    }

    private int NativeMonitorFrame()
    {
        if (!UsesNativeVideo || !_nativeSourceOpened)
            return _clock?.CurrentFrame ?? 0;

        double seconds;
        try
        {
            seconds = Math.Max(0, _sourcePlayer.PlaybackSession.Position.TotalSeconds);
        }
        catch (Exception ex) when (IsNativePlayerFault(ex))
        {
            return _clock?.CurrentFrame ?? 0;
        }

        if (UsesNativeTimelineVideo && _nativeTimelineClip is { } clip)
        {
            var frame = TimelineFrameResolver.TimelineFrameAt(clip, seconds, MonitorFps);
            return Math.Clamp(frame, 0, Math.Max(0, MonitorDurationFrames - 1));
        }

        var sourceFrame = CaptionImporter.FrameFromSeconds(seconds, MonitorFps);
        return Math.Clamp(sourceFrame, 0, Math.Max(0, MonitorDurationFrames - 1));
    }

    private bool TimelineNativeReachedClipEnd()
    {
        if (_nativeTimelineClip is not { } clip || clip.DurationFrames <= 0) return false;
        if (!_nativeSourceOpened) return false;
        try
        {
            var session = _sourcePlayer.PlaybackSession;
            var duration = session.NaturalDuration;
            if (duration <= TimeSpan.Zero) return false;
            var fps = MonitorFps;
            var speed = clip.Speed == 0 ? 1.0 : clip.Speed;
            var sourceOut = (clip.TrimStartFrame + clip.DurationFrames * speed) / fps;
            return session.Position.TotalSeconds >= sourceOut - (0.5 / fps);
        }
        catch (Exception ex) when (IsNativePlayerFault(ex))
        {
            return false;
        }
    }

    private void TickTransport()
    {
        if (TracingPlaybackTicks)
            PlaybackTrace("fire", _clock?.CurrentFrame ?? -1);
        if (!UsesNativeVideo)
        {
            _clock?.Tick();
            return;
        }

        if (UsesNativeTimelineVideo && !_awaitingNativeClipSettle && TimelineNativeReachedClipEnd())
        {
            AdvanceNativeTimelinePastCurrentClip();
            return;
        }

        if (_awaitingNativeClipSettle && _nativeTimelineClip is { } settling)
        {
            var settled = NativeMonitorFrame();
            if (settling.Contains(settled) || !TimelineNativeReachedClipEnd())
                _awaitingNativeClipSettle = false;
        }

        var frame = NativeMonitorFrame();
        if (_clock is not null && frame != _clock.CurrentFrame)
            _clock.SeekToFrame(frame);
    }

    private void SourcePlayer_MediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!UsesNativeVideo) return;
            _nativeSourceOpened = true;
            ApplyNativePlaybackRate(_nativePlaybackRate);
            if (NativeCannotPresent(sender))
            {
                var softwareFrame = _clock?.CurrentFrame ?? 0;
                ClearNativeSource();
                SetStatusText("Frame preview (this media cannot play in real time)");
                RequestFrame(softwareFrame, force: true);
                return;
            }

            RouteNativeAudio();
            var seek = _pendingNativeSeekFrame
                ?? (_tabs.IsTimeline ? _timelinePlayhead : _sourcePlayhead);
            SeekNativeToMonitorFrame(seek);
            if (_pendingNativePlay && _clock?.IsPlaying == true)
            {
                _pendingNativePlay = false;
                _sourcePlayer.Play();
            }
        });
    }

    private void SourcePlayer_MediaEnded(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!UsesNativeVideo) return;
            if (_clock?.IsPlaying != true)
                return;
            if (UsesNativeTimelineVideo)
            {
                AdvanceNativeTimelinePastCurrentClip();
                return;
            }

            _pendingNativePlay = false;
            var frame = MonitorDurationFrames;
            _clock?.SeekToFrame(Math.Max(0, frame - 1));
            _clock?.Pause();
            if (_audioPlayback.IsReady)
                _audioPlayback.PauseAtFrame(_clock?.CurrentFrame ?? 0);
        });
    }

    private void SourcePlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var message = args.ErrorMessage;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!UsesNativeVideo) return;
            ClearNativeSource();
            _clock?.Pause();
            _pendingNativePlay = false;
            SetStatusText(ShortStatus(message));
            if (_audioPlayback.IsReady)
                _audioPlayback.PauseAtFrame(_clock?.CurrentFrame ?? 0);
            RequestFrame(_clock?.CurrentFrame ?? 0, force: true);
            UpdateLabels(_clock?.CurrentFrame ?? 0);
        });
    }

    /// <summary>
    /// The media opened but the element has nothing to show: no video dimensions at all, or a
    /// timeline clip whose trim/playhead can never be reached because the session cannot seek.
    /// Both leave a blank MediaPlayerElement, so the software pipe has to take over.
    /// </summary>
    private bool NativeCannotPresent(MediaPlayer player)
    {
        try
        {
            var session = player.PlaybackSession;
            if (session.NaturalVideoWidth == 0 && session.NaturalVideoHeight == 0)
                return true;
            return _tabs.IsTimeline && !session.CanSeek;
        }
        catch (Exception ex) when (IsNativePlayerFault(ex))
        {
            return true;
        }
    }

    private void AdvanceNativeTimelinePastCurrentClip()
    {
        if (_clock is null || _nativeTimelineClip is null)
            return;

        var wasPlaying = _clock.IsPlaying;
        var end = Math.Min(_nativeTimelineClip.EndFrame, MonitorDurationFrames);
        if (end >= MonitorDurationFrames)
        {
            _pendingNativePlay = false;
            _awaitingNativeClipSettle = false;
            _sourcePlayer.Pause();
            var last = Math.Max(0, MonitorDurationFrames - 1);
            _clock.SeekToFrame(last);
            _clock.Pause();
            if (_audioPlayback.IsReady)
                _audioPlayback.PauseAtFrame(last);
            return;
        }

        _awaitingNativeClipSettle = true;
        _clock.SeekToFrame(end);
        ConfigureTimelineNative(end);

        if (UsesNativeTimelineVideo)
        {
            SeekNativeToMonitorFrame(end);
            ShowNativeSource();
            if (wasPlaying)
            {
                _pendingNativePlay = true;
                if (_nativeSourceOpened)
                {
                    _pendingNativePlay = false;
                    _sourcePlayer.Play();
                }
            }

            return;
        }

        ShowSoftwarePreview();
        _pendingNativePlay = false;
        _sourcePlayer.Pause();
        if (wasPlaying)
            RequestFrame(end, force: true);
    }

    private static Timeline BuildSourceAudioTimeline(string mediaRef, ClipType type, int fps, int duration)
        => new()
        {
            Id = "__source_monitor__",
            Name = "Source",
            Fps = fps,
            Width = 16,
            Height = 9,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.audio,
                    Clips =
                    [
                        new Clip
                        {
                            Id = "source",
                            MediaRef = mediaRef,
                            MediaType = ClipType.audio,
                            SourceClipType = type,
                            StartFrame = 0,
                            DurationFrames = duration,
                        }
                    ]
                }
            ]
        };

    private static int ViewStatePlayhead(ProjectSession session)
    {
        if (session.ProjectFile.ViewStates is { } vs
            && session.ProjectFile.ActiveTimelineId is { } id
            && vs.TryGetValue(id, out var state))
        {
            return Math.Max(0, state.PlayheadFrame);
        }

        return 0;
    }
}
