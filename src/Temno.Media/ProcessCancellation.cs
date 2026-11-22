using System.ComponentModel;
using System.Diagnostics;

namespace Temno.Media;

/// <summary>Kills an external media process when the owning operation is cancelled.</summary>
public static class ProcessCancellation
{
    public static CancellationTokenRegistration KillOnCancellation(
        Process process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!cancellationToken.CanBeCanceled)
            return default;

        return cancellationToken.Register(static state =>
        {
            var child = (Process)state!;
            try
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
            {
                // The process already exited or cannot be killed on this platform.
            }
        }, process);
    }
}
