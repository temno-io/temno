using System.Text;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Xunit;

namespace Temno.Core.Tests;

public class ExportJobQueueTests
{
    // Hang guard only — deterministic tests never wait this long.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private static TaskCompletionSource NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string NewPath() =>
        Path.Combine(Path.GetTempPath(), $"temno_test_{Guid.NewGuid():N}.mp4");

    /// <summary>Completes when the job reaches a terminal state via JobChanged (no polling).</summary>
    private static Task WhenStateAsync(ExportJobQueue queue, ExportJob job, ExportJobState state)
    {
        var tcs = NewGate();
        queue.JobChanged += changed =>
        {
            if (ReferenceEquals(changed, job) && changed.State == state) tcs.TrySetResult();
        };
        if (job.State == state) tcs.TrySetResult();
        return tcs.Task.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Enqueue_ReturnsWaitingJobWithMetadata()
    {
        var queue = new ExportJobQueue();
        var gate = NewGate();
        var blocker = queue.Enqueue("test", NewPath(), async (_, _) => await gate.Task);

        var path = NewPath();
        var job = queue.Enqueue("video", path, (_, _) => Task.CompletedTask);

        Assert.True(Guid.TryParse(job.Id, out _));
        Assert.Equal("video", job.Kind);
        Assert.Equal(path, job.OutputPath);
        Assert.Equal(ExportJobState.Waiting, job.State);
        Assert.Equal(0, job.Progress);
        Assert.Null(job.Error);
        Assert.Null(job.CompletedAt);

        gate.TrySetResult();
        await WhenStateAsync(queue, job, ExportJobState.Completed);
        Assert.NotNull(job.CompletedAt);
        Assert.Equal(ExportJobState.Completed, blocker.State);
    }

    [Fact]
    public async Task Jobs_RunStrictlySerially()
    {
        var queue = new ExportJobQueue();
        var job1Started = NewGate();
        var job1Gate = NewGate();
        var job2Started = NewGate();

        var job1 = queue.Enqueue("test", NewPath(), async (_, _) =>
        {
            job1Started.TrySetResult();
            await job1Gate.Task;
        });
        var job2 = queue.Enqueue("test", NewPath(), (_, _) =>
        {
            job2Started.TrySetResult();
            return Task.CompletedTask;
        });

        await job1Started.Task.WaitAsync(TestTimeout);
        Assert.Equal(ExportJobState.Rendering, job1.State);
        Assert.Equal(ExportJobState.Waiting, job2.State);
        Assert.False(job2Started.Task.IsCompleted);
        Assert.True(queue.HasActivity);

        job1Gate.TrySetResult();
        await job2Started.Task.WaitAsync(TestTimeout);
        // job1 must have fully finished before job2's work began.
        Assert.Equal(ExportJobState.Completed, job1.State);

        await WhenStateAsync(queue, job2, ExportJobState.Completed);
        Assert.False(queue.HasActivity);
    }

    [Fact]
    public async Task Progress_PropagatesToJob()
    {
        var queue = new ExportJobQueue();
        var reported = NewGate();
        var gate = NewGate();

        var job = queue.Enqueue("test", NewPath(), async (progress, _) =>
        {
            progress.Report((3, 12));
            reported.TrySetResult();
            await gate.Task;
            progress.Report((12, 12));
        });

        await reported.Task.WaitAsync(TestTimeout);
        Assert.Equal(0.25, job.Progress);
        Assert.Equal(3, job.CurrentFrame);
        Assert.Equal(12, job.TotalFrames);

        gate.TrySetResult();
        await WhenStateAsync(queue, job, ExportJobState.Completed);
        Assert.Equal(1, job.Progress);
        Assert.Equal(12, job.CurrentFrame);
        Assert.Equal(12, job.TotalFrames);
    }

    [Fact]
    public async Task FailedJob_RecordsErrorAndQueueContinues()
    {
        var queue = new ExportJobQueue();
        var job1 = queue.Enqueue("test", NewPath(),
            (_, _) => throw new InvalidOperationException("boom"));
        var job2 = queue.Enqueue("test", NewPath(), (_, _) => Task.CompletedTask);

        await WhenStateAsync(queue, job1, ExportJobState.Failed);
        Assert.Equal("boom", job1.Error);
        Assert.NotNull(job1.CompletedAt);

        await WhenStateAsync(queue, job2, ExportJobState.Completed);
        Assert.Null(job2.Error);
    }

    [Fact]
    public async Task Cancel_WaitingJobCancelsImmediatelyAndSkipsItsWork()
    {
        var queue = new ExportJobQueue();
        var job1Gate = NewGate();
        var job2Ran = false;

        var job1 = queue.Enqueue("test", NewPath(), async (_, _) => await job1Gate.Task);
        var job2 = queue.Enqueue("test", NewPath(), (_, _) =>
        {
            job2Ran = true;
            return Task.CompletedTask;
        });

        Assert.True(queue.Cancel(job2.Id));
        Assert.Equal(ExportJobState.Cancelled, job2.State);
        Assert.NotNull(job2.CompletedAt);

        var job3 = queue.Enqueue("test", NewPath(), (_, _) => Task.CompletedTask);
        job1Gate.TrySetResult();
        await WhenStateAsync(queue, job3, ExportJobState.Completed);

        Assert.False(job2Ran);
        Assert.Equal(ExportJobState.Completed, job1.State);
        Assert.Equal(ExportJobState.Cancelled, job2.State);

        // Finished or unknown jobs cannot be cancelled.
        Assert.False(queue.Cancel(job1.Id));
        Assert.False(queue.Cancel(Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task Cancel_SignalsRunningJobsToken()
    {
        var queue = new ExportJobQueue();
        var started = NewGate();

        var job = queue.Enqueue("test", NewPath(), async (_, ct) =>
        {
            started.TrySetResult();
            var cancelled = NewGate();
            await using var registration = ct.Register(() => cancelled.TrySetResult());
            await cancelled.Task;
            ct.ThrowIfCancellationRequested();
        });

        await started.Task.WaitAsync(TestTimeout);
        Assert.Equal(ExportJobState.Rendering, job.State);

        Assert.True(queue.Cancel(job.Id));
        await WhenStateAsync(queue, job, ExportJobState.Cancelled);
        Assert.Null(job.Error);
        Assert.NotNull(job.CompletedAt);
    }

    [Fact]
    public async Task Jobs_SnapshotIsNewestFirstAndTryGetFindsJobs()
    {
        var queue = new ExportJobQueue();
        var gate = NewGate();

        var job1 = queue.Enqueue("a", NewPath(), async (_, _) => await gate.Task);
        var job2 = queue.Enqueue("b", NewPath(), (_, _) => Task.CompletedTask);
        var job3 = queue.Enqueue("c", NewPath(), (_, _) => Task.CompletedTask);

        var jobs = queue.Jobs;
        Assert.Equal(new[] { job3.Id, job2.Id, job1.Id }, jobs.Select(j => j.Id).ToArray());

        Assert.Same(job2, queue.TryGet(job2.Id));
        Assert.Null(queue.TryGet(Guid.NewGuid().ToString()));

        gate.TrySetResult();
        await WhenStateAsync(queue, job3, ExportJobState.Completed);
        Assert.Equal(3, queue.Jobs.Count);
    }

    [Fact]
    public async Task Enqueue_ThrowsWhileDestinationIsReserved()
    {
        var queue = new ExportJobQueue();
        var gate = NewGate();
        var path = NewPath();

        var job1 = queue.Enqueue("test", path, async (_, _) => await gate.Task);
        Assert.True(queue.IsDestinationReserved(path));
        Assert.Throws<InvalidOperationException>(
            () => queue.Enqueue("test", path, (_, _) => Task.CompletedTask));

        gate.TrySetResult();
        await WhenStateAsync(queue, job1, ExportJobState.Completed);

        // A finished job no longer reserves its destination.
        Assert.False(queue.IsDestinationReserved(path));
        var job2 = queue.Enqueue("test", path, (_, _) => Task.CompletedTask);
        await WhenStateAsync(queue, job2, ExportJobState.Completed);
    }

    [Fact]
    public void StateReportNames_MatchMacRawValues()
    {
        Assert.Equal("queued", ExportJobState.Waiting.ReportName());
        Assert.Equal("rendering", ExportJobState.Rendering.ReportName());
        Assert.Equal("completed", ExportJobState.Completed.ReportName());
        Assert.Equal("failed", ExportJobState.Failed.ReportName());
        Assert.Equal("canceled", ExportJobState.Cancelled.ReportName());

        Assert.True(ExportJobState.Waiting.IsPending());
        Assert.True(ExportJobState.Rendering.IsPending());
        Assert.False(ExportJobState.Completed.IsPending());
        Assert.True(ExportJobState.Completed.IsFinished());
        Assert.True(ExportJobState.Failed.IsFinished());
        Assert.True(ExportJobState.Cancelled.IsFinished());
        Assert.False(ExportJobState.Rendering.IsFinished());
    }

    // ---- WP-403: EnqueueVideo carries the caller's overwrite intent to the publish ----------------

    [Fact]
    public async Task EnqueueVideo_OverwriteFalse_FailsBeforeRendering_WhenTheDestinationIsTaken()
    {
        var queue = new ExportJobQueue();
        var path = NewPath();
        var kept = Encoding.ASCII.GetBytes("WP-403 kept " + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, kept);
        try
        {
            var exporter = new TimelineExporter(new CpuCompositor(), new SolidFrameSource());
            var job = queue.EnqueueVideo(exporter, TinyTimeline(), new ExportPreset("wp403"), path, overwrite: false);
            await WhenFinishedAsync(queue, job, TestTimeout);

            Assert.Equal(ExportJobState.Failed, job.State);
            Assert.Contains(path, job.Error, StringComparison.Ordinal);
            Assert.Contains("nothing was rendered", job.Error, StringComparison.Ordinal);
            Assert.Null(exporter.LastEncoderName);
            Assert.Equal(kept, File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The editor asks "Replace existing export?" first, so its call keeps the replacing default.</summary>
    [Fact]
    public async Task EnqueueVideo_ByDefault_ReplacesWhatIsThere()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var queue = new ExportJobQueue();
        var path = NewPath();
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("WP-403 old export"));
        try
        {
            var job = queue.EnqueueVideo(
                new TimelineExporter(new CpuCompositor(), new SolidFrameSource()), TinyTimeline(), new ExportPreset("wp403"), path);
            await WhenFinishedAsync(queue, job, RenderTimeout);

            Assert.Equal(ExportJobState.Completed, job.State);
            Assert.Equal("ftyp", Encoding.ASCII.GetString(File.ReadAllBytes(path), 4, 4));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EnqueueVideo_OverwriteFalse_KeepsAFileThatArrivesWhileRendering()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var queue = new ExportJobQueue();
        var path = NewPath();
        byte[]? arrived = null;
        ExportJob? job = null;
        queue.JobChanged += changed =>
        {
            if (arrived is null && ReferenceEquals(changed, job) && changed.CurrentFrame >= 1)
            {
                arrived = Encoding.ASCII.GetBytes("WP-403 arrived " + Guid.NewGuid().ToString("N"));
                File.WriteAllBytes(path, arrived);
            }
        };
        var gate = NewGate();
        queue.Enqueue("gate", NewPath(), async (_, _) => await gate.Task);
        try
        {
            job = queue.EnqueueVideo(
                new TimelineExporter(new CpuCompositor(), new SolidFrameSource()),
                TinyTimeline(), new ExportPreset("wp403"), path, overwrite: false);
            gate.TrySetResult();
            await WhenFinishedAsync(queue, job, RenderTimeout);

            Assert.Equal(ExportJobState.Failed, job.State);
            Assert.Contains(path, job.Error, StringComparison.Ordinal);
            Assert.Contains("while the export rendered", job.Error, StringComparison.Ordinal);
            Assert.Equal(arrived, File.ReadAllBytes(path));
        }
        finally
        {
            gate.TrySetResult();
            File.Delete(path);
        }
    }

    private static readonly TimeSpan RenderTimeout = TimeSpan.FromMinutes(2);

    private static Task WhenFinishedAsync(ExportJobQueue queue, ExportJob job, TimeSpan timeout)
    {
        var tcs = NewGate();
        queue.JobChanged += changed =>
        {
            if (ReferenceEquals(changed, job) && changed.State.IsFinished()) tcs.TrySetResult();
        };
        if (job.State.IsFinished()) tcs.TrySetResult();
        return tcs.Task.WaitAsync(timeout);
    }

    private static Timeline TinyTimeline() => new()
    {
        Fps = 10,
        Width = 64,
        Height = 48,
        Tracks =
        [
            new Track
            {
                Type = ClipType.video,
                Clips = [new Clip { MediaRef = "solid", MediaType = ClipType.video, DurationFrames = 10 }],
            },
        ],
    };

    private sealed class SolidFrameSource : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(string mediaPath, double seconds, CancellationToken ct = default)
        {
            var data = new byte[64 * 48 * 4];
            Array.Fill(data, (byte)128);
            return Task.FromResult(new VideoFrame(64, 48, data, "rgba"));
        }
    }
}
