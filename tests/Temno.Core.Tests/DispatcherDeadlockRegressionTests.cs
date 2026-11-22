using System.Diagnostics;
using Temno.Media;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// Behavioural regression for the MCP dispatcher deadlock.
/// <para>
/// WinUI installs a <c>DispatcherQueueSynchronizationContext</c> on the UI thread. The MCP tool
/// surface ran handlers on that thread and blocked it in <c>GetAwaiter().GetResult()</c>. Any await
/// inside the library that captured the context posted its continuation back to that same, now
/// blocked, thread — so the call never completed and the whole app hung until it was killed.
/// </para>
/// <para>
/// <see cref="SingleThreadSynchronizationContext"/> below reproduces exactly that shape: one thread,
/// one message pump, continuations posted back to it. If any await in the exercised path stops using
/// <c>ConfigureAwait(false)</c>, these tests hang and then fail on their timeout.
/// </para>
/// </summary>
public sealed class DispatcherDeadlockRegressionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    [Fact]
    public async Task Probe_BlockedOnADispatcherThread_CompletesInsteadOfDeadlocking()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var media = Path.Combine(directory, "probe.mp4");
            await FfmpegFixture.WriteTestClipAsync(media, fps: 30, seconds: 1, width: 160, height: 90);

            // Exactly what inspect_timeline / inspect_color do on the UI thread.
            var probe = RunOnDispatcherThread(() =>
                new FfmpegMediaProbe().ProbeAsync(media).GetAwaiter().GetResult());

            Assert.True(probe.HasVideo);
            Assert.Equal(160, probe.Width);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public async Task DecodeAndPngEncode_BlockedOnADispatcherThread_CompleteInsteadOfDeadlocking()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var media = Path.Combine(directory, "frame.mp4");
            await FfmpegFixture.WriteTestClipAsync(media, fps: 30, seconds: 1, width: 160, height: 90);

            // inspect_timeline's two blocking calls: decode a frame, then shell out to encode a PNG.
            var pngLength = RunOnDispatcherThread(() =>
            {
                using var frame = new FfmpegFrameSource()
                    .GetFrameAsync(media, 0.0).GetAwaiter().GetResult();
                var png = FfmpegPngEncoder.EncodeAsync(frame).GetAwaiter().GetResult();
                return png.Length;
            });

            Assert.True(pngLength > 0);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a thread that owns a posting SynchronizationContext and is
    /// blocked inside it, then fails rather than hanging forever if the work never returns.
    /// </summary>
    private static T RunOnDispatcherThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            var context = new SingleThreadSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
            finally { context.Complete(); }
        })
        { IsBackground = true, Name = "fake-ui-dispatcher" };

        var sw = Stopwatch.StartNew();
        thread.Start();
        if (!thread.Join(Timeout))
        {
            Assert.Fail(
                $"Deadlocked: the call did not return within {Timeout.TotalSeconds:F0}s while the "
                + "dispatcher thread was blocked in GetAwaiter().GetResult(). An await in the "
                + "exercised path is capturing the SynchronizationContext — it needs "
                + $"ConfigureAwait(false). (waited {sw.Elapsed.TotalSeconds:F1}s)");
        }

        if (failure is not null) throw failure;
        return result;
    }

    /// <summary>
    /// A minimal stand-in for WinUI's dispatcher: <see cref="Post"/> queues the continuation for the
    /// owning thread instead of running it on the thread pool. That queueing is the whole point —
    /// it is what turns a context-capturing await into a deadlock when the owner is blocked.
    /// </summary>
    private sealed class SingleThreadSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private bool _complete;

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue)
            {
                if (_complete) return;
                _queue.Enqueue((d, state));
                Monitor.Pulse(_queue);
            }
        }

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void Complete()
        {
            lock (_queue)
            {
                _complete = true;
                Monitor.PulseAll(_queue);
            }
        }
    }
}
