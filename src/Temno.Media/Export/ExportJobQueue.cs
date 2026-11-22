using Temno.Core.Models;

namespace Temno.Media.Export;

/// <summary>
/// Lifecycle states for an export job.
/// Mac: Export/ExportQueue.swift ExportJobStatus. The Mac's transient
/// preparing/canceling phases collapse into Waiting/Rendering here; a
/// cancel-signalled running job stays Rendering until its work observes
/// the token and the job finishes as Cancelled.
/// </summary>
public enum ExportJobState
{
    Waiting,
    Rendering,
    Completed,
    Failed,
    Cancelled,
}

public static class ExportJobStateExtensions
{
    /// <summary>
    /// Wire name matching the Mac raw values reported through the agent tools
    /// (ExportJobStatus.rawValue: "queued"/"rendering"/"completed"/"failed"/"canceled").
    /// </summary>
    public static string ReportName(this ExportJobState state) => state switch
    {
        ExportJobState.Waiting => "queued",
        ExportJobState.Rendering => "rendering",
        ExportJobState.Completed => "completed",
        ExportJobState.Failed => "failed",
        ExportJobState.Cancelled => "canceled",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    /// <summary>Mac: ExportJobStatus.isFinished.</summary>
    public static bool IsFinished(this ExportJobState state) =>
        state is ExportJobState.Completed or ExportJobState.Failed or ExportJobState.Cancelled;

    /// <summary>Mac: ExportJobStatus.isPending (waiting or still running).</summary>
    public static bool IsPending(this ExportJobState state) =>
        state is ExportJobState.Waiting or ExportJobState.Rendering;
}

/// <summary>
/// One export in the queue. Mac: Export/ExportQueue.swift ExportJob.
/// Mutated only by <see cref="ExportJobQueue"/>; all mutable properties are
/// safe to read from any thread.
/// </summary>
public sealed class ExportJob
{
    private readonly object _gate = new();
    private ExportJobState _state = ExportJobState.Waiting;
    private double _progress;
    private int _currentFrame;
    private int _totalFrames;
    private string? _error;
    private DateTimeOffset? _completedAt;

    internal ExportJob(string kind, string outputPath)
    {
        Id = Guid.NewGuid().ToString();
        Kind = kind;
        OutputPath = outputPath;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string Id { get; }

    /// <summary>Short label for what is being exported (e.g. "video").</summary>
    public string Kind { get; }

    public string OutputPath { get; }

    public DateTimeOffset CreatedAt { get; }

    public ExportJobState State { get { lock (_gate) return _state; } }

    /// <summary>Fraction complete, 0..1. Pinned to 1 when the job completes (Mac parity).</summary>
    public double Progress { get { lock (_gate) return _progress; } }

    public int CurrentFrame { get { lock (_gate) return _currentFrame; } }

    public int TotalFrames { get { lock (_gate) return _totalFrames; } }

    public string? Error { get { lock (_gate) return _error; } }

    public DateTimeOffset? CompletedAt { get { lock (_gate) return _completedAt; } }

    /// <summary>FFmpeg encoder that ran (e.g. hevc_nvenc / libx265). Null until the video pass starts.</summary>
    public string? EncoderName { get { lock (_gate) return _encoderName; } }

    /// <summary>
    /// Why a hardware preference was encoded in software, shown in the export status.
    /// Null when hardware ran or the user did not prefer it.
    /// </summary>
    public string? EncoderNote { get { lock (_gate) return _encoderNote; } }

    private string? _encoderName;
    private string? _encoderNote;

    internal void SetEncoderName(string? name)
    {
        lock (_gate) _encoderName = name;
    }

    internal void SetEncoderNote(string? note)
    {
        lock (_gate) _encoderNote = note;
    }

    internal bool TryBeginRendering()
    {
        lock (_gate)
        {
            if (_state != ExportJobState.Waiting) return false;
            _state = ExportJobState.Rendering;
            return true;
        }
    }

    internal bool TryCancelWaiting()
    {
        lock (_gate)
        {
            if (_state != ExportJobState.Waiting) return false;
            _state = ExportJobState.Cancelled;
            _completedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    internal bool TryReportProgress(int current, int total)
    {
        lock (_gate)
        {
            // Mac guards progress updates behind status.isPending.
            if (_state != ExportJobState.Rendering) return false;
            _currentFrame = current;
            _totalFrames = total;
            _progress = total > 0 ? Math.Clamp((double)current / total, 0, 1) : 0;
            return true;
        }
    }

    internal void Finish(ExportJobState state, string? error)
    {
        lock (_gate)
        {
            _state = state;
            _error = error;
            // Mac: progress = status == .completed ? 1 : service.progress.
            if (state == ExportJobState.Completed) _progress = 1;
            _completedAt = DateTimeOffset.UtcNow;
        }
    }
}

/// <summary>
/// Serial background export queue. Mac: Export/ExportQueue.swift ExportQueue.
/// Jobs run strictly one at a time, in enqueue order, on the thread pool.
/// The Mac class is @MainActor-serialized; here thread safety comes from a
/// lock plus per-job locks, and jobs are chained on a tail task instead of
/// startNext() re-entry.
/// </summary>
public sealed class ExportJobQueue
{
    private readonly object _gate = new();
    private readonly List<JobEntry> _entries = new();
    private readonly Dictionary<string, JobEntry> _byId = new();
    private Task _tail = Task.CompletedTask;

    /// <summary>
    /// Raised whenever a job's state or progress changes, from a background
    /// thread. Handlers should re-read the job's current properties rather
    /// than assume which change fired (rapid transitions can coalesce).
    /// For the future title-bar export indicator (Mac: TitleBarTrailingView).
    /// </summary>
    public event Action<ExportJob>? JobChanged;

    /// <summary>Mac: ExportQueue.hasActivity.</summary>
    public bool HasActivity
    {
        get { lock (_gate) return _entries.Any(e => e.Job.State.IsPending()); }
    }

    /// <summary>Snapshot of all jobs, newest first.</summary>
    public IReadOnlyList<ExportJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                var snapshot = new ExportJob[_entries.Count];
                for (var i = 0; i < _entries.Count; i++)
                    snapshot[i] = _entries[_entries.Count - 1 - i].Job;
                return snapshot;
            }
        }
    }

    public ExportJob? TryGet(string id)
    {
        lock (_gate) return _byId.TryGetValue(id, out var entry) ? entry.Job : null;
    }

    /// <summary>
    /// True when a pending job already targets <paramref name="outputPath"/>.
    /// Mac: ExportQueue.isDestinationReserved.
    /// </summary>
    public bool IsDestinationReserved(string outputPath)
    {
        var normalized = NormalizePath(outputPath);
        lock (_gate)
        {
            return _entries.Any(e => e.Job.State.IsPending()
                && string.Equals(NormalizePath(e.Job.OutputPath), normalized, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Adds a job and returns it immediately; the injected work runs in the
    /// background once all previously enqueued jobs have finished. Throws
    /// <see cref="InvalidOperationException"/> when a pending job already
    /// targets the same output path (Mac: ExportQueueError.destinationInUse).
    /// </summary>
    public ExportJob Enqueue(
        string kind,
        string outputPath,
        Func<IProgress<(int Current, int Total)>, CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(work);

        var entry = new JobEntry(new ExportJob(kind, outputPath), work);
        lock (_gate)
        {
            if (IsDestinationReserved(outputPath))
            {
                throw new InvalidOperationException(
                    $"An export to \"{Path.GetFileName(outputPath)}\" is already waiting or in progress.");
            }
            _entries.Add(entry);
            _byId.Add(entry.Job.Id, entry);
            var previous = _tail;
            // Task.Run keeps execution off the caller's thread; awaiting the
            // previous tail inside RunAsync is what serializes the queue.
            _tail = Task.Run(() => RunAsync(previous, entry));
        }
        RaiseChanged(entry.Job);
        return entry.Job;
    }

    /// <summary>
    /// Convenience wrapper that runs <see cref="TimelineExporter.ExportAsync"/>
    /// as a queued job, mapping the exporter's per-frame progress into the job.
    /// Mac: ExportQueue.enqueueVideo.
    /// </summary>
    /// <param name="disposeExporter">
    /// Dispose <paramref name="exporter"/> once the job settles. Callers build one exporter per
    /// export and keep no other handle on it, so its D3D11 device and decoder pipes would otherwise
    /// stay alive for the life of the process.
    /// </param>
    /// <param name="overwrite">
    /// False when the caller may not replace what is at <paramref name="outputPath"/>. The job may run
    /// minutes after the caller checked, so the intent travels with it and the exporter judges it again
    /// before it renders and when it publishes (WP-403). The editor asks before it replaces, so true.
    /// </param>
    public ExportJob EnqueueVideo(
        TimelineExporter exporter,
        Timeline timeline,
        ExportPreset preset,
        string outputPath,
        bool disposeExporter = true,
        bool overwrite = true)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(preset);

        ExportJob? job = null;
        job = Enqueue("video", outputPath, async (progress, cancellationToken) =>
        {
            void OnProgress(int current, int total) => progress.Report((current, total));
            exporter.Progress += OnProgress;
            try
            {
                var task = exporter.ExportAsync(timeline, preset, outputPath, overwrite, cancellationToken);
                job?.SetEncoderName(exporter.LastEncoderName);
                job?.SetEncoderNote(exporter.LastSoftwareEncodeReason);
                await task.ConfigureAwait(false);
                job?.SetEncoderName(exporter.LastEncoderName);
                job?.SetEncoderNote(exporter.LastSoftwareEncodeReason);
            }
            finally
            {
                exporter.Progress -= OnProgress;
                if (disposeExporter) exporter.Dispose();
            }
        });
        return job;
    }

    /// <summary>
    /// Cancels a job. A waiting job becomes Cancelled immediately and its work
    /// never runs; a running job has its <see cref="CancellationToken"/>
    /// signalled and finishes as Cancelled once the work observes it. Returns
    /// false for unknown or already-finished jobs. Mac: ExportQueue.cancel.
    /// </summary>
    public bool Cancel(string id)
    {
        JobEntry? entry;
        lock (_gate) _byId.TryGetValue(id, out entry);
        if (entry is null) return false;

        if (entry.Job.TryCancelWaiting())
        {
            RaiseChanged(entry.Job);
            return true;
        }
        if (entry.Job.State == ExportJobState.Rendering)
        {
            entry.Cancellation.Cancel();
            return true;
        }
        return false;
    }

    private async Task RunAsync(Task previous, JobEntry entry)
    {
        // A predecessor can only fault via a throwing JobChanged handler;
        // never let that wedge the rest of the queue.
        try { await previous.ConfigureAwait(false); }
        catch { }

        if (!entry.Job.TryBeginRendering()) return; // cancelled while waiting
        RaiseChanged(entry.Job);

        var reporter = new SynchronousProgress<(int Current, int Total)>(value =>
        {
            if (entry.Job.TryReportProgress(value.Current, value.Total))
                RaiseChanged(entry.Job);
        });

        try
        {
            await entry.Work(reporter, entry.Cancellation.Token).ConfigureAwait(false);
            entry.Job.Finish(ExportJobState.Completed, error: null);
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            entry.Job.Finish(ExportJobState.Cancelled, error: null);
        }
        catch (Exception ex)
        {
            entry.Job.Finish(ExportJobState.Failed, ex.Message);
        }
        RaiseChanged(entry.Job);
    }

    private void RaiseChanged(ExportJob job) => JobChanged?.Invoke(job);

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; } // unresolvable paths compare as raw strings
    }

    private sealed class JobEntry
    {
        public JobEntry(ExportJob job, Func<IProgress<(int Current, int Total)>, CancellationToken, Task> work)
        {
            Job = job;
            Work = work;
        }

        public ExportJob Job { get; }
        public Func<IProgress<(int Current, int Total)>, CancellationToken, Task> Work { get; }
        // Never disposed: Cancel may race with completion, and an undisposed
        // (timer-free) CTS is harmless.
        public CancellationTokenSource Cancellation { get; } = new();
    }

    // System.Progress<T> marshals through a SynchronizationContext / thread
    // pool, which reorders reports; exports need loss-free in-order updates.
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
