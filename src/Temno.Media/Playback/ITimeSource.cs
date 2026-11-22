using System.Diagnostics;

namespace Temno.Media.Playback;

/// <summary>
/// Monotonic wall-clock abstraction so <see cref="PlaybackClock"/> stays deterministic in tests.
/// </summary>
public interface ITimeSource
{
    /// <summary>Seconds elapsed since an arbitrary fixed origin. Must be monotonically non-decreasing.</summary>
    double ElapsedSeconds { get; }
}

/// <summary>Production time source backed by <see cref="Stopwatch"/> (monotonic, unaffected by system clock changes).</summary>
public sealed class StopwatchTimeSource : ITimeSource
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public double ElapsedSeconds => _stopwatch.Elapsed.TotalSeconds;
}
