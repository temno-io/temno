namespace Temno.Core.Ui;

/// <summary>
/// WP-190. What the Export dialog's cancel button says and whether it is enabled, decided from the
/// number of pending jobs so the button's scope is explicit. Before this the button read
/// "Cancel active" whether the queue was empty or held five jobs, and a click cancelled every
/// pending job (Waiting and Rendering alike) while the label promised the active one.
/// </summary>
public static class ExportQueueChrome
{
    /// <summary>A cancel action needs a target; an empty queue disables the button.</summary>
    public static bool CanCancel(int pendingCount) => pendingCount > 0;

    /// <summary>
    /// One pending job: "Cancel export". Several: "Cancel all N exports", so the user reads the scope
    /// before clicking. Zero keeps the singular label on a disabled button.
    /// </summary>
    public static string CancelButtonLabel(int pendingCount) =>
        pendingCount > 1 ? $"Cancel all {pendingCount} exports" : "Cancel export";

    /// <summary>
    /// With one pending job the click cancels that job; with several the label said "all", so all
    /// pending jobs are cancelled. Returns the ids to cancel from the pending list in queue order.
    /// </summary>
    public static IReadOnlyList<string> JobsToCancel(IReadOnlyList<string> pendingIdsInQueueOrder) =>
        pendingIdsInQueueOrder.Count switch
        {
            0 => Array.Empty<string>(),
            1 => new[] { pendingIdsInQueueOrder[0] },
            _ => pendingIdsInQueueOrder.ToArray(),
        };
}
