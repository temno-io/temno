namespace Temno.Core.Generation;

/// <summary>
/// How the watcher learns a job's state. WP-350 can replace polling with a push tickle behind this
/// same method; the watcher does not know which.
/// </summary>
public interface IHostedJobTransport
{
    Task<HostedJobObservation> AdvanceAsync(HostedPendingJob job, CancellationToken cancellationToken);
}
