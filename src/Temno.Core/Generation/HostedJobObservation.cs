namespace Temno.Core.Generation;

public enum HostedWatchKind
{
    StillWorking,
    Succeeded,
    Failed,
    Suspended,
}

/// <summary>
/// One read of a hosted job. <see cref="HostedWatchKind.StillWorking"/> covers a non-terminal server
/// status and every transport failure (429, 5xx, timeout, a dropped connection, an expired token).
/// </summary>
public sealed record HostedJobObservation
{
    public HostedWatchKind Kind { get; init; } = HostedWatchKind.StillWorking;
    public string Phase { get; init; } = HostedJobPhases.Pending;
    public string? Reason { get; init; }
    public IReadOnlyList<string>? ResultUrls { get; init; }
    public long? CostCredits { get; init; }
    public TimeSpan? RetryAfter { get; init; }
    public string? JobId { get; init; }
    public bool ClearCancel { get; init; }
    public bool CantCancel { get; init; }

    public static HostedJobObservation Working(string? phase = null, string? jobId = null, TimeSpan? retryAfter = null)
        => new()
        {
            Kind = HostedWatchKind.StillWorking,
            Phase = string.IsNullOrEmpty(phase) ? HostedJobPhases.Pending : phase,
            JobId = jobId,
            RetryAfter = retryAfter,
        };

    public static HostedJobObservation Succeeded(IReadOnlyList<string>? urls, string? jobId, long? costCredits)
        => new()
        {
            Kind = HostedWatchKind.Succeeded,
            Phase = HostedJobPhases.Landed,
            ResultUrls = urls,
            JobId = jobId,
            CostCredits = costCredits,
            ClearCancel = true,
        };

    public static HostedJobObservation Failed(string reason, string? jobId = null)
        => new()
        {
            Kind = HostedWatchKind.Failed,
            Phase = HostedJobPhases.Failed,
            Reason = string.IsNullOrWhiteSpace(reason) ? "The server said this generation failed." : reason.Trim(),
            JobId = jobId,
            ClearCancel = true,
        };

    public static HostedJobObservation Suspended(string? reason = null)
        => new()
        {
            Kind = HostedWatchKind.Suspended,
            Reason = string.IsNullOrWhiteSpace(reason) ? HostedJobPhases.SuspendedMessage : reason.Trim(),
        };
}
