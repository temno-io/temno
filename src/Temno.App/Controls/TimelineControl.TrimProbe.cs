using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;

namespace Temno.App.Controls;

/// <summary>
/// WP-320 measurement aid. Env-gated by <see cref="TimelineTrace"/> (<c>TEMNO_TIMELINE_TRACE</c>),
/// the same kind of switch as <c>TEMNO_PLAYBACK_TRACE</c>. With that set, a file named
/// &lt;trace&gt;.go starts a scripted edge trim (right by default, <c>edge=left</c> for the head) that
/// runs the same methods as a pointer drag (<c>BeginDrag</c>, <c>QueueTrimTarget</c> each move,
/// <c>CommitTrimDrag</c> on release), paced like cap2x.py's trim take: press 160 ms, glide at 60 Hz,
/// hold 220 ms, release, rest 1400 ms.
/// No pointer or keyboard input is involved.
/// </summary>
public sealed partial class TimelineControl
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _probePoll;
    private bool _probeRunning;

    private sealed record TrimProbeSpec(
        string ClipId, int FromEdge, int ToEdge, int Cycles, double GlideMs, int PressMs, int HoldMs,
        int RestMs, double GrabPx, double Hz, bool Frames, bool High, string Out, bool LeftEdge);

    private void StartTrimProbePoll()
    {
        if (!TimelineTrace.On) return;
        LayoutUpdated += (_, _) => TimelineTrace.Mark("lu");
        _probePoll = DispatcherQueue.CreateTimer();
        _probePoll.Interval = TimeSpan.FromMilliseconds(250);
        _probePoll.IsRepeating = true;
        _probePoll.Tick += (_, _) =>
        {
            if (_probeRunning) return;
            var go = TimelineTrace.TracePath + ".go";
            if (!File.Exists(go)) return;
            string text;
            try
            {
                text = File.ReadAllText(go);
                // Several TimelineControls can exist (one per editor page); only the loaded one whose
                // session holds the clip takes the probe.
                var reason = _session is null ? "no session" : XamlRoot is null ? "no xamlroot"
                    : ResolveProbeClip(ParseProbe(text).ClipId) is null ? "clip missing" : null;
                if (reason is not null)
                {
                    File.WriteAllText($"{TimelineTrace.TracePath}.decline-{GetHashCode():x8}", reason);
                    return;
                }
                File.Delete(go);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText($"{TimelineTrace.TracePath}.decline-{GetHashCode():x8}", ex.ToString()); } catch (IOException) { }
                return;
            }
            TimelineTrace.Mark("probe-owner", GetHashCode());
            _ = RunTrimProbeAsync(text);
        };
        _probePoll.Start();
    }

    private string? ResolveProbeClip(string idOrPrefix)
    {
        if (_session is null) return null;
        return _session.ActiveTimeline.Tracks.SelectMany(track => track.Clips)
            .Select(clip => clip.Id)
            .FirstOrDefault(id => id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase));
    }

    private static TrimProbeSpec ParseProbe(string text)
    {
        var d = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim(), StringComparer.Ordinal);
        double D(string k, double def) => d.TryGetValue(k, out var v) ? double.Parse(v, CultureInfo.InvariantCulture) : def;
        return new TrimProbeSpec(
            d["clip"], (int)D("from", 84), (int)D("to", 96), (int)D("cycles", 2), D("glide", 950),
            (int)D("press", 160), (int)D("hold", 220), (int)D("rest", 1400), D("grab", 2.5), D("hz", 60),
            D("frames", 0) != 0, D("high", 0) != 0, d["out"],
            d.TryGetValue("edge", out var edge) && edge.Equals("left", StringComparison.OrdinalIgnoreCase));
    }

    private async Task RunTrimProbeAsync(string text)
    {
        _probeRunning = true;
        string? outPath = null;
        EventHandler<object>? onFrame = null;
        Microsoft.UI.Dispatching.DispatcherQueueTimer? heartbeat = null;
        try
        {
            var spec = ParseProbe(text);
            outPath = spec.Out;
            // MCP reports the first 8 hex digits of a clip id; the model holds the full id.
            spec = spec with { ClipId = ResolveProbeClip(spec.ClipId) ?? spec.ClipId };
            var trackIndex = TrackIndexOf(spec.ClipId);
            if (trackIndex < 0) throw new InvalidOperationException($"clip {spec.ClipId} not on the active timeline");

            heartbeat = DispatcherQueue.CreateTimer();
            heartbeat.Interval = TimeSpan.FromMilliseconds(4);
            heartbeat.IsRepeating = true;
            heartbeat.Tick += (_, _) => TimelineTrace.Mark("hb");
            if (spec.Frames)
            {
                onFrame = (_, _) => TimelineTrace.Mark("fr");
                CompositionTarget.Rendering += onFrame;
            }

            TimelineTrace.Mark("probe", (long)Math.Round(_pixelsPerFrame * 1000), (long)Math.Round((XamlRoot?.RasterizationScale ?? 0) * 100));
            heartbeat.Start();
            await Task.Delay(500);
            for (var c = 0; c < spec.Cycles; c++)
            {
                await GlideTrimAsync(spec, trackIndex, spec.FromEdge, spec.ToEdge);
                await GlideTrimAsync(spec, trackIndex, spec.ToEdge, spec.FromEdge);
            }
            TimelineTrace.Mark("probe-end");
            await Task.Delay(3000);
            TimelineTrace.Mark("tail-end");
        }
        catch (Exception ex)
        {
            TimelineTrace.Mark("probe-error");
            if (outPath is not null)
                File.WriteAllText(outPath + ".err", ex.ToString());
        }
        finally
        {
            heartbeat?.Stop();
            if (onFrame is not null) CompositionTarget.Rendering -= onFrame;
            if (outPath is not null) TimelineTrace.Flush(outPath);
            _probeRunning = false;
        }
    }

    private async Task GlideTrimAsync(TrimProbeSpec spec, int trackIndex, int fromEdge, int toEdge)
    {
        // The pointer grabs the edge a few DIPs inside the clip, as cap2x.py's take did (5 px at 2x).
        var grab = spec.LeftEdge ? spec.GrabPx : -spec.GrabPx;
        var startX = fromEdge * _pixelsPerFrame + grab;
        var endX = toEdge * _pixelsPerFrame + grab;

        // Edge_PointerPressed
        if (!_selectedClipIds.Contains(spec.ClipId))
            SetSelectedClips(LinkedUnlessAlt(spec.ClipId), spec.ClipId);
        var pressFrame = Math.Max(0, (int)Math.Floor(startX / _pixelsPerFrame));
        BeginDrag(spec.LeftEdge ? DragKind.TrimLeft : DragKind.TrimRight, new ClipTag(spec.ClipId, trackIndex), pressFrame);
        TimelineTrace.Mark("down", pressFrame, toEdge);
        await Task.Delay(spec.PressMs);

        // Glide: one "move" per tick; a tick that arrives late reads the wall clock, like a coalesced pointer.
        var sw = Stopwatch.StartNew();
        var done = new TaskCompletionSource();
        void Move(double t)
        {
            var x = startX + (endX - startX) * t;
            var frame = Math.Max(0, (int)Math.Floor(x / _pixelsPerFrame));
            var requested = frame - _dragPointerStartFrame;
            TimelineTrace.Mark("pm", frame, requested);
            if (_dragKind is DragKind.TrimLeft or DragKind.TrimRight)
                QueueTrimTarget(requested);
            TimelineTrace.Mark("pm1", _dragFrameDelta);
        }
        if (spec.High)
        {
            // Pointer-like delivery: a 60 Hz source off the UI thread, coalesced to one pending move,
            // dispatched at High priority the way input outranks ordinary queued work.
            var pending = 0;
            var thread = new Thread(() =>
            {
                var period = 1000.0 / spec.Hz;
                var next = period;
                while (true)
                {
                    while (sw.Elapsed.TotalMilliseconds < next) Thread.SpinWait(200);
                    var t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / spec.GlideMs);
                    if (Interlocked.CompareExchange(ref pending, 1, 0) == 0)
                    {
                        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
                        {
                            Volatile.Write(ref pending, 0);
                            var now = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / spec.GlideMs);
                            Move(now);
                            if (now >= 1.0) done.TrySetResult();
                        });
                    }
                    if (t >= 1.0) break;
                    next += period;
                }
            }) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }
        else
        {
            var timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(1000.0 / spec.Hz);
            timer.IsRepeating = true;
            timer.Tick += (_, _) =>
            {
                var t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / spec.GlideMs);
                Move(t);
                if (t >= 1.0)
                {
                    timer.Stop();
                    done.TrySetResult();
                }
            };
            timer.Start();
        }
        await done.Task;
        await Task.Delay(spec.HoldMs);

        // LaneCanvas_PointerReleased, trim branch. Flush first so `up` reads the delta a frame
        // has not yet applied. CommitTrimDrag is what the release handler calls.
        FlushTrimTarget();
        TimelineTrace.Mark("up", _dragFrameDelta);
        if (_session is not null && _dragKind is DragKind.TrimLeft or DragKind.TrimRight)
            CommitTrimDrag();
        TimelineTrace.Mark("up1");
        await Task.Delay(spec.RestMs);
    }
}
