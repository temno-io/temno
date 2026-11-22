using Temno.Core.Editing;

namespace Temno.Media.Playback;

/// <summary>
/// Mac: Preview/VideoEngine.swift, EditorViewModel.swift:340-397 (playhead clock).
/// Frame-domain playback transport: the playhead is an int timeline frame computed from
/// elapsed wall time × rate × fps. Not thread-safe — intended for single-threaded (UI) use.
/// Drive it by calling <see cref="Tick"/> from a render/timer loop; real-time vsync pacing
/// is the caller's responsibility (Windows-only).
/// </summary>
public sealed class PlaybackClock
{
    private readonly ITimeSource _timeSource;
    private int _fps;
    private int _durationFrames;
    private double _rate = 1.0;
    private FrameRange? _loopRange;

    // Anchor pair: the playhead was at _anchorFrame when the time source read _anchorElapsed.
    // Re-anchored only on play/seek/rate/fps changes and loop wraps — never on plain ticks,
    // otherwise sub-frame remainders would be discarded each tick and the playhead would stall.
    private int _anchorFrame;
    private double _anchorElapsed;
    private int _lastPublishedFrame;

    public PlaybackClock(ITimeSource timeSource, int fps, int durationFrames)
    {
        ArgumentNullException.ThrowIfNull(timeSource);
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps), "Fps must be positive.");
        if (durationFrames < 0) throw new ArgumentOutOfRangeException(nameof(durationFrames), "Duration cannot be negative.");
        _timeSource = timeSource;
        _fps = fps;
        _durationFrames = durationFrames;
    }

    /// <summary>Timeline frames per second used to convert elapsed seconds to frames.</summary>
    public int Fps
    {
        get => _fps;
        set
        {
            if (value <= 0) throw new ArgumentOutOfRangeException(nameof(value), "Fps must be positive.");
            Reanchor();
            _fps = value;
        }
    }

    /// <summary>Timeline duration in frames; the playhead is clamped to [0, DurationFrames].</summary>
    public int DurationFrames
    {
        get => _durationFrames;
        set
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "Duration cannot be negative.");
            Reanchor();
            _durationFrames = value;
            _anchorFrame = Math.Min(_anchorFrame, value);
        }
    }

    /// <summary>
    /// Playback rate in timeline-frames per wall-clock frame. 1.0 = real time; negative plays
    /// backward (JKL shuttle uses -2..2). Must be finite and non-zero — pause instead of rate 0.
    /// </summary>
    public double Rate
    {
        get => _rate;
        set
        {
            if (!double.IsFinite(value) || value == 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Rate must be finite and non-zero.");
            Reanchor();
            _rate = value;
        }
    }

    /// <summary>
    /// Optional loop range (half-open [Start, End) frames). While playing, the playhead wraps to
    /// stay inside the range. Seeks are not constrained by the loop.
    /// </summary>
    public FrameRange? LoopRange
    {
        get => _loopRange;
        set
        {
            if (value is { } r && (r.Start < 0 || r.End <= r.Start))
                throw new ArgumentOutOfRangeException(nameof(value), "Loop range must satisfy 0 <= Start < End.");
            _loopRange = value;
        }
    }

    public bool IsPlaying { get; private set; }

    /// <summary>Current playhead frame — pure computation, no state change (see <see cref="Tick"/> for auto-pause).</summary>
    public int CurrentFrame => ComputeFrame(out _, out _);

    /// <summary>Raised whenever the published playhead frame changes (on Tick, seek, pause, stop, play-reset).</summary>
    public event Action<int>? FrameChanged;

    /// <summary>Raised when playback starts or stops (including auto-pause at the timeline end).</summary>
    public event Action<bool>? IsPlayingChanged;

    public void Play()
    {
        if (IsPlaying) return;
        // Mac parity: play() with playhead at/past the end restarts from the beginning.
        if (_rate > 0 && _anchorFrame >= EffectiveEnd()) _anchorFrame = EffectiveStart();
        else if (_rate < 0 && _anchorFrame <= EffectiveStart()) _anchorFrame = EffectiveEnd();
        _anchorElapsed = _timeSource.ElapsedSeconds;
        IsPlaying = true;
        PublishFrame(_anchorFrame);
        IsPlayingChanged?.Invoke(true);
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        Reanchor();
        IsPlaying = false;
        PublishFrame(_anchorFrame);
        IsPlayingChanged?.Invoke(false);
    }

    /// <summary>Pause and reset the playhead to frame 0.</summary>
    public void Stop()
    {
        var wasPlaying = IsPlaying;
        IsPlaying = false;
        _anchorFrame = 0;
        _anchorElapsed = _timeSource.ElapsedSeconds;
        PublishFrame(0);
        if (wasPlaying) IsPlayingChanged?.Invoke(false);
    }

    public void TogglePlay()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    /// <summary>Seek to a frame, clamped to [0, DurationFrames]. Playback (if active) continues from there.</summary>
    public void SeekToFrame(int frame)
    {
        _anchorFrame = Math.Clamp(frame, 0, _durationFrames);
        _anchorElapsed = _timeSource.ElapsedSeconds;
        PublishFrame(_anchorFrame);
    }

    /// <summary>
    /// Sample the time source, commit loop wraps, auto-pause at the timeline boundary, and raise
    /// <see cref="FrameChanged"/> if the frame moved. Returns the current frame.
    /// </summary>
    public int Tick()
    {
        var frame = ComputeFrame(out var hitBoundary, out var wrapped);
        if (wrapped)
        {
            _anchorFrame = frame;
            _anchorElapsed = _timeSource.ElapsedSeconds;
        }

        if (hitBoundary && IsPlaying)
        {
            _anchorFrame = frame;
            _anchorElapsed = _timeSource.ElapsedSeconds;
            IsPlaying = false;
            PublishFrame(frame);
            IsPlayingChanged?.Invoke(false);
            return frame;
        }

        PublishFrame(frame);
        return frame;
    }

    private int ComputeFrame(out bool hitBoundary, out bool wrapped)
    {
        hitBoundary = false;
        wrapped = false;
        if (!IsPlaying) return _anchorFrame;

        var elapsed = _timeSource.ElapsedSeconds - _anchorElapsed;
        var raw = _anchorFrame + elapsed * _rate * _fps;
        // Mac rounds seconds×fps half-away-from-zero (Swift .rounded()).
        var frame = (int)Math.Round(raw, MidpointRounding.AwayFromZero);

        if (_loopRange is { } loop && loop.Length > 0)
        {
            if ((_rate > 0 && frame >= loop.End) || (_rate < 0 && frame < loop.Start))
            {
                frame = loop.Start + Mod(frame - loop.Start, loop.Length);
                wrapped = true;
            }
            return Math.Clamp(frame, 0, _durationFrames);
        }

        if (_rate > 0 && frame >= _durationFrames)
        {
            hitBoundary = true;
            return _durationFrames;
        }

        if (_rate < 0 && frame <= 0)
        {
            hitBoundary = true;
            return 0;
        }

        return frame;
    }

    private int EffectiveStart() => _loopRange is { } loop && loop.Length > 0 ? loop.Start : 0;

    private int EffectiveEnd() => _loopRange is { } loop && loop.Length > 0 ? loop.End : _durationFrames;

    /// <summary>Commit the currently computed frame and restart elapsed measurement from now.</summary>
    private void Reanchor()
    {
        _anchorFrame = ComputeFrame(out _, out _);
        _anchorElapsed = _timeSource.ElapsedSeconds;
    }

    private void PublishFrame(int frame)
    {
        if (frame == _lastPublishedFrame) return;
        _lastPublishedFrame = frame;
        FrameChanged?.Invoke(frame);
    }

    private static int Mod(int value, int modulus) => ((value % modulus) + modulus) % modulus;
}
