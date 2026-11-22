namespace Temno.Core.Generation;

/// <summary>
/// The states a hosted generation is allowed to be in. A job becomes <see cref="Failed"/> only when
/// the server says so. Transport trouble stays on <see cref="Pending"/>, <see cref="Rendering"/> or
/// <see cref="Recovering"/>.
/// </summary>
public static class HostedJobPhases
{
    public const string Pending = "pending";
    public const string Rendering = "rendering";
    public const string Recovering = "recovering";
    public const string Landed = "landed";
    public const string Failed = "failed";

    public const string CantCancelStatus = "Rendering — can't cancel";
    public const string SuspendedMessage = "This account is suspended. Hosted generation is off.";
    public const string CancelCopy = "Free to cancel until rendering starts.";
    public const string AccountSuspendedCode = "account_suspended";
    public const string AlreadyRenderingCode = "already_rendering";

    public static bool IsOpen(string? phase)
        => phase is Pending or Rendering or Recovering;

    /// <summary>
    /// A download that failed has to ask the server for new links. Stored result URLs expire,
    /// and using them again never refreshes them.
    /// </summary>
    public static bool NeedsFreshResultUrls(string? phase, string? jobId)
        => phase == Recovering && !string.IsNullOrEmpty(jobId);

    public static string ManifestStatus(string? phase, bool cantCancel)
    {
        if (cantCancel)
            return CantCancelStatus;
        return phase switch
        {
            Rendering => "rendering",
            Recovering => "downloading",
            _ => "generating",
        };
    }
}
