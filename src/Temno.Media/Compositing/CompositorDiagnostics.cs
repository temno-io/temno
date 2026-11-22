using System.Diagnostics;
using System.Text;
using Temno.Core.IO;

namespace Temno.Media.Compositing;

/// <summary>Which implementation of <see cref="ICompositor"/> actually rendered a frame.</summary>
public enum CompositorPath
{
    /// <summary><see cref="CpuCompositor"/> — the reference implementation and the fallback.</summary>
    Cpu,

    /// <summary><see cref="GpuCompositor"/> — D3D11 compute.</summary>
    Gpu,
}

/// <summary>
/// WP-215 Scope 4: the user and the log must both be able to tell which compositor ran. A silent
/// fallback to the CPU path is the failure this exists to prevent — before this, nothing in the
/// product reported <see cref="Hardware.HardwareCapabilities"/> at all, so a machine with no D3D11
/// simply ran slower with no way to find out why.
/// <para>
/// Counters are per-process and advance once per composited frame. Transitions — the first
/// selection, and every change of path or reason — are written to <see cref="LogPath"/>; steady
/// state writes nothing, so a long export costs one line, not one line per frame.
/// </para>
/// </summary>
public static class CompositorDiagnostics
{
    private const long MaxLogBytes = 256 * 1024;

    private static readonly object Gate = new();
    private static long _gpuFrames;
    private static long _cpuFrames;
    private static string? _lastTransition;
    private static bool _logDisabled;

    /// <summary>The path that rendered the most recent frame, or null before the first frame.</summary>
    public static CompositorPath? LastPath { get; private set; }

    /// <summary>
    /// Adapter description when <see cref="LastPath"/> is <see cref="CompositorPath.Gpu"/>;
    /// otherwise why the CPU path ran ("D3D11 unavailable: ...", "selected by TEMNO_COMPOSITOR=cpu").
    /// </summary>
    public static string? LastDetail { get; private set; }

    /// <summary>
    /// Set when a GPU render was attempted and did not complete, with the reason. Distinct from
    /// <see cref="LastDetail"/>: a machine with no GPU has a detail and no fallback reason, while a
    /// device lost mid-export has both. Never cleared — a fallback that happened once is worth
    /// reporting after the path recovers.
    /// </summary>
    public static string? LastFallbackReason { get; private set; }

    /// <summary>Frames composited on each path this process.</summary>
    public static long GpuFrames { get { lock (Gate) return _gpuFrames; } }

    /// <summary>Frames composited on each path this process.</summary>
    public static long CpuFrames { get { lock (Gate) return _cpuFrames; } }

    /// <summary>
    /// Raised on a transition only, with the same text written to the log. The Settings pane reads
    /// <see cref="Summary"/> on open rather than subscribing; this is for callers that want the
    /// change as it happens.
    /// </summary>
    public static event Action<string>? Transition;

    /// <summary>Append-only transition log, shared with other processes and attributed per line.</summary>
    public static string LogPath { get; } = Path.Combine(UserDataRoots.LocalAppDataRoot, "logs", "compositor.log");

    /// <summary>One line for a status row: which path is running, on what, and what it cost.</summary>
    public static string Summary
    {
        get
        {
            lock (Gate)
            {
                if (LastPath is null)
                    return "No frame composited yet.";

                // Activity, not identity: the selection line beside this one already names the path
                // and the adapter, and repeating them here read as a stutter in the Settings pane.
                var sb = new StringBuilder();
                sb.Append(_gpuFrames).Append(_gpuFrames == 1 ? " frame" : " frames")
                  .Append(" composited on the GPU path, ").Append(_cpuFrames)
                  .Append(" on the CPU reference.");
                // Only the fallback reason is added: it is the one thing the selection line beside
                // this cannot know, because it describes what was chosen, not what went wrong after.
                if (!string.IsNullOrEmpty(LastFallbackReason))
                    sb.Append(" Fell back at least once: ").Append(LastFallbackReason);
                return sb.ToString();
            }
        }
    }

    internal static void RecordFrame(CompositorPath path, string? detail, string? fallbackReason = null)
    {
        string? transition = null;
        lock (Gate)
        {
            if (path == CompositorPath.Gpu) _gpuFrames++; else _cpuFrames++;
            if (fallbackReason is { Length: > 0 }) LastFallbackReason = fallbackReason;

            var key = path + "|" + detail + "|" + fallbackReason;
            if (!string.Equals(key, _lastTransition, StringComparison.Ordinal))
            {
                _lastTransition = key;
                LastPath = path;
                LastDetail = detail;
                transition = Describe(path, detail, fallbackReason);
            }
            else
            {
                LastPath = path;
                LastDetail = detail;
            }
        }

        if (transition is null) return;
        Write(transition);
        Transition?.Invoke(transition);
    }

    /// <summary>Test seam: counters and the transition memo are process-global by design.</summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _gpuFrames = 0;
            _cpuFrames = 0;
            _lastTransition = null;
            LastPath = null;
            LastDetail = null;
            LastFallbackReason = null;
        }
    }

    private static string Describe(CompositorPath path, string? detail, string? fallbackReason)
    {
        var sb = new StringBuilder();
        sb.Append(path == CompositorPath.Gpu ? "compositor=gpu" : "compositor=cpu");
        if (!string.IsNullOrEmpty(detail)) sb.Append("; detail=").Append(detail);
        if (!string.IsNullOrEmpty(fallbackReason)) sb.Append("; fallback=").Append(fallbackReason);
        return sb.ToString();
    }

    private static void Write(string message)
    {
        if (_logDisabled) return;
        try
        {
            var directory = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(directory);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                File.Delete(LogPath);

            using var process = Process.GetCurrentProcess();
            File.AppendAllText(
                LogPath,
                $"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} {process.ProcessName}({process.Id}) {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never be able to fail a render. One failure disables the sink for the
            // process rather than retrying a locked or read-only path on every transition.
            _logDisabled = true;
        }
    }
}
