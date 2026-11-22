namespace Temno.Core.Generation;

/// <summary>
/// The account's push channel (WP-350). While its socket is live and the server publishes job events, a tickle wakes
/// the watchers and their own reads slow to a safety poll; otherwise they keep their 2–10 s cadence.
/// </summary>
public static class HostedJobPush
{
    public static readonly TimeSpan SafetyGap = TimeSpan.FromSeconds(60);
    private static int _live;

    public static bool Live
    {
        get => Volatile.Read(ref _live) == 1;
        set
        {
            if (Interlocked.Exchange(ref _live, value ? 1 : 0) == 1 && !value)
                HostedWatcherRegistry.Shared.WakeAll(); // back to the short cadence now, not after a minute
        }
    }

    public static TimeSpan Gap(Random random, TimeSpan? retryAfter)
        => Live
            ? retryAfter is { } wait && wait > SafetyGap ? wait : SafetyGap
            : HostedJobWatcher.NextGap(random, retryAfter);
}

public delegate Task<bool> HostedJobSettled(HostedPendingJob job, HostedJobObservation observation, CancellationToken cancellationToken);

/// <summary>
/// One loop per account. It reads every open job, then waits. A second call for the same account
/// returns the loop already running.
/// </summary>
public sealed class HostedJobWatcher
{
    private readonly HostedPendingStore _store;
    private readonly IHostedJobTransport _transport;
    private readonly string _environment;
    private readonly string _accountId;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<TimeSpan?, TimeSpan> _nextGap;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Random _random;
    private readonly object _delayGate = new();
    private readonly object _backoffGate = new();
    private CancellationTokenSource? _run;
    private CancellationTokenSource _delaySource = new();
    private int _started;
    private int _wakeRequested;
    private bool _suspended;
    private DateTimeOffset _notBeforeUtc;

    public HostedJobWatcher(
        HostedPendingStore store,
        IHostedJobTransport transport,
        string environment,
        string accountId,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<TimeSpan?, TimeSpan>? nextGap = null,
        Random? random = null,
        Func<DateTimeOffset>? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _accountId = accountId ?? throw new ArgumentNullException(nameof(accountId));
        _random = random ?? Random.Shared;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _nextGap = nextGap ?? (retryAfter => HostedJobPush.Gap(_random, retryAfter));
        _delay = delay ?? ((gap, token) => Task.Delay(gap, token));
    }

    public string AccountId => _accountId;
    public string Environment => _environment;
    public bool IsSuspended => _suspended;

    public HostedJobSettled? OnSettled { get; set; }
    public Action<HostedPendingJob, string>? OnPhase { get; set; }
    public Action<string>? OnSuspended { get; set; }

    public static TimeSpan NextGap(Random random, TimeSpan? retryAfter)
    {
        ArgumentNullException.ThrowIfNull(random);
        var seconds = 2d + random.NextDouble() * 8d;
        var gap = TimeSpan.FromMilliseconds(seconds * 1000d);
        if (retryAfter is { } extra && extra > gap)
            return extra;
        return gap;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
            return;
        var run = new CancellationTokenSource();
        _run = run;
        _ = Task.Run(() => LoopAsync(run.Token));
    }

    public void Wake()
    {
        Interlocked.Exchange(ref _wakeRequested, 1);
        CancellationTokenSource source;
        lock (_delayGate)
            source = _delaySource;
        try { source.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>A later wake does not poll before this interval has elapsed.</summary>
    public void HonorRetryAfter(TimeSpan retryAfter)
    {
        if (retryAfter <= TimeSpan.Zero)
            return;
        var until = _clock() + retryAfter;
        lock (_backoffGate)
        {
            if (until > _notBeforeUtc)
                _notBeforeUtc = until;
        }
    }

    public void Stop()
    {
        try { _run?.Cancel(); } catch (ObjectDisposedException) { }
        Wake();
    }

    public async Task<bool> StepAsync(CancellationToken cancellationToken)
    {
        if (_suspended || _store.IsSuspended(_accountId))
        {
            _suspended = true;
            return false;
        }

        var backoff = RetryRemaining();
        if (backoff > TimeSpan.Zero)
        {
            Interlocked.Exchange(ref _wakeRequested, 0);
            await DelayAsync(backoff, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var jobs = _store.Active(_environment, _accountId);
        TimeSpan? retryAfter = null;
        foreach (var job in jobs)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            var observation = await ReadAsync(job, cancellationToken).ConfigureAwait(false);
            if (observation.Kind == HostedWatchKind.Suspended)
            {
                _store.Suspend(_accountId);
                _suspended = true;
                OnSuspended?.Invoke(observation.Reason ?? HostedJobPhases.SuspendedMessage);
                return false;
            }

            if (observation.RetryAfter is { } wait && (retryAfter is null || wait > retryAfter))
            {
                retryAfter = wait;
                HonorRetryAfter(wait);
            }

            var previous = job.Phase;
            Apply(job.PlaceholderId, observation, terminal: observation.Kind is HostedWatchKind.Failed);
            var fresh = _store.FindByPlaceholder(job.PlaceholderId) ?? job;

            if (observation.Kind == HostedWatchKind.Succeeded)
            {
                _store.Update(job.PlaceholderId, current => current.Phase = HostedJobPhases.Recovering);
                var landed = true;
                if (OnSettled is not null)
                {
                    try
                    {
                        landed = await OnSettled(fresh, observation, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        landed = false;
                    }
                }

                _store.Update(job.PlaceholderId, current =>
                    current.Phase = landed ? HostedJobPhases.Landed : HostedJobPhases.Recovering);
            }
            else if (observation.Kind == HostedWatchKind.Failed)
            {
                if (OnSettled is not null)
                {
                    try { await OnSettled(fresh, observation, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch { /* the server failure is already stored */ }
                }
            }
            else if (!string.Equals(previous, fresh.Phase, StringComparison.Ordinal))
            {
                OnPhase?.Invoke(fresh, fresh.Phase);
            }
        }

        var woken = Interlocked.Exchange(ref _wakeRequested, 0) == 1;
        var remaining = RetryRemaining();
        TimeSpan gap;
        if (woken && remaining <= TimeSpan.Zero)
            gap = TimeSpan.Zero;
        else
        {
            gap = _nextGap(retryAfter);
            if (remaining > gap)
                gap = remaining;
        }
        await DelayAsync(gap, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private TimeSpan RetryRemaining()
    {
        lock (_backoffGate)
        {
            var remaining = _notBeforeUtc - _clock();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var again = await StepAsync(cancellationToken).ConfigureAwait(false);
                if (!again)
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopped with the app.
        }
    }

    private async Task<HostedJobObservation> ReadAsync(HostedPendingJob job, CancellationToken cancellationToken)
    {
        try
        {
            return await _transport.AdvanceAsync(job, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            var phase = job.Phase is HostedJobPhases.Rendering or HostedJobPhases.Recovering
                ? job.Phase
                : HostedJobPhases.Pending;
            return HostedJobObservation.Working(phase, job.JobId);
        }
    }

    private void Apply(string placeholderId, HostedJobObservation observation, bool terminal)
    {
        _store.Update(placeholderId, current =>
        {
            if (!string.IsNullOrEmpty(observation.JobId))
                current.JobId = observation.JobId;
            if (observation.ResultUrls is { Count: > 0 })
                current.ResultUrls = observation.ResultUrls.ToList();
            if (observation.CostCredits is long cost)
                current.CostCredits = cost;
            if (observation.ClearCancel)
                current.CancelRequested = false;
            if (observation.CantCancel)
                current.CantCancel = true;
            if (terminal)
            {
                current.Phase = HostedJobPhases.Failed;
                current.ServerReason = observation.Reason;
            }
            else if (observation.Kind == HostedWatchKind.StillWorking && !string.IsNullOrEmpty(observation.Phase))
            {
                current.Phase = observation.Phase;
            }
        });
    }

    private async Task DelayAsync(TimeSpan gap, CancellationToken cancellationToken)
    {
        CancellationTokenSource source;
        lock (_delayGate)
        {
            _delaySource.Dispose();
            _delaySource = new CancellationTokenSource();
            source = _delaySource;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, source.Token);
        try
        {
            await _delay(gap, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Wake asked for a pass now.
        }
    }
}

/// <summary>Process-wide watchers. A second ensure for the same account does not start another loop.</summary>
public sealed class HostedWatcherRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, HostedJobWatcher> _live = new(StringComparer.Ordinal);

    public static HostedWatcherRegistry Shared { get; } = new();

    public int Count
    {
        get { lock (_gate) return _live.Count; }
    }

    public HostedJobWatcher Ensure(string accountId, string environment, Func<HostedJobWatcher> create)
    {
        ArgumentNullException.ThrowIfNull(create);
        var key = environment + "\n" + accountId;
        lock (_gate)
        {
            if (_live.TryGetValue(key, out var existing))
                return existing;
            var created = create();
            _live[key] = created;
            created.Start();
            return created;
        }
    }

    /// <summary>A job tickle (WP-350): every watcher reads now.</summary>
    public void WakeAll()
    {
        lock (_gate)
        {
            foreach (var watcher in _live.Values)
                watcher.Wake();
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            foreach (var watcher in _live.Values)
                watcher.Stop();
            _live.Clear();
        }
    }
}
