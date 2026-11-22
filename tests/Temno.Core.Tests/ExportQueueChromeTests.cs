using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-190. The Export dialog's cancel button used to read "Cancel active" on an empty queue and
/// cancel every Waiting job while promising the active one. These pin the label, the enabled
/// state and the scope to the pending count.
/// </summary>
public sealed class ExportQueueChromeTests
{
    [Fact]
    public void Empty_queue_disables_cancel_and_keeps_the_singular_label()
    {
        Assert.False(ExportQueueChrome.CanCancel(0));
        Assert.Equal("Cancel export", ExportQueueChrome.CancelButtonLabel(0));
        Assert.Empty(ExportQueueChrome.JobsToCancel(Array.Empty<string>()));
    }

    [Fact]
    public void One_pending_job_is_cancelled_alone()
    {
        Assert.True(ExportQueueChrome.CanCancel(1));
        Assert.Equal("Cancel export", ExportQueueChrome.CancelButtonLabel(1));
        Assert.Equal(new[] { "a" }, ExportQueueChrome.JobsToCancel(new[] { "a" }));
    }

    [Fact]
    public void Several_pending_jobs_say_all_and_cancel_all()
    {
        Assert.True(ExportQueueChrome.CanCancel(3));
        Assert.Equal("Cancel all 3 exports", ExportQueueChrome.CancelButtonLabel(3));
        Assert.Equal(new[] { "a", "b", "c" }, ExportQueueChrome.JobsToCancel(new[] { "a", "b", "c" }));
    }
}
