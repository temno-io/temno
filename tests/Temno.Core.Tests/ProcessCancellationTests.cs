using System.Diagnostics;
using Temno.Media;
using Xunit;

namespace Temno.Core.Tests;

public sealed class ProcessCancellationTests
{
    [Fact]
    public async Task KillOnCancellation_TerminatesRunningProcessTree()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("ping 127.0.0.1 -n 31 > nul");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("sleep 30");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start cancellation test process.");
        using var cancellation = new CancellationTokenSource();
        using var registration = ProcessCancellation.KillOnCancellation(process, cancellation.Token);

        cancellation.Cancel();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(process.HasExited);
    }
}
