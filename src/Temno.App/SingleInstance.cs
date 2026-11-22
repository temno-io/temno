using System.Diagnostics;
using System.Runtime.InteropServices;
using Temno.App.Services;
using Temno.Core.Account;
using Temno.Core.Generation;
using Temno.Core.IO;
using Temno.Environments;

namespace Temno.App;

/// <summary>
/// One process per environment. A second launch of the same build signals the first and leaves, so two watchers never
/// land the same job. Temno and Temno-Dev use different names and still run side by side. A build from source and the
/// installed Temno share one lock, data folder, sign-in and MCP port (WP-402 D-1), so a launch of the other build says
/// which copy is open and leaves without switching to it.
/// </summary>
internal static class SingleInstance
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _activate;

    public static bool TryClaim(Action activated)
    {
        var environment = HostedEndpoints.BuildEnvironment.Name();
        _mutex = new Mutex(false, SingleInstanceNames.Mutex(environment));
        try
        {
            if (!_mutex.WaitOne(0))
            {
                var message = SecondLaunchMessage();
                if (message is null)
                    Signal(environment);
                else
                    _ = MessageBoxW(IntPtr.Zero, message, AppName, MbOk | MbIconInformation | MbSetForeground);
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous process exited without releasing. This process owns the mutex now.
        }

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, SingleInstanceNames.Activate(environment));
        WritePid();
        WriteRecord();
        var signal = _activate;
        _ = Task.Run(() =>
        {
            while (signal.WaitOne())
            {
                try { activated(); }
                catch (Exception) { /* the window may already be closing */ }
            }
        });
        return true;
    }

    private static string AppName
        => HostedEndpoints.BuildEnvironment == TemnoEnvironment.Production ? "Temno" : "Temno-Dev";

    private static string PidPath => Path.Combine(UserDataRoots.LocalAppDataRoot, "instance.pid");

    private static string RecordPath => Path.Combine(UserDataRoots.LocalAppDataRoot, RunningInstance.FileName);

    /// <summary>Null when the open copy is this build, or cannot be told apart from it: then the old hand-off runs.</summary>
    private static string? SecondLaunchMessage()
    {
        try
        {
            var self = Environment.ProcessPath;
            var pid = ReadPid();
            if (string.IsNullOrEmpty(self) || pid <= 0 || pid == Environment.ProcessId)
                return null;
            string? openPath = null;
            try
            {
                using var open = Process.GetProcessById(pid);
                openPath = open.MainModule?.FileName;
            }
            catch (ArgumentException)
            {
                // instance.pid names a process that has exited: the lock's holder has not written its own yet.
                return null;
            }
            catch (Exception)
            {
                // Its path is unreadable; its record may still name it.
            }

            RunningInstance? record = null;
            try { record = RunningInstance.Parse(File.ReadAllText(RecordPath)); }
            catch (Exception) { /* an older build writes no record */ }

            return RunningInstance.SecondLaunchMessage(
                AppName, BuildProvenance.Current.IsOfficial, self, pid, openPath, record);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Signal(string environment)
    {
        try
        {
            AllowSetForegroundWindow(ReadPid());
            using var existing = EventWaitHandle.OpenExisting(SingleInstanceNames.Activate(environment));
            existing.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first process has not created the event yet. It still holds the watcher.
        }
        catch (Exception)
        {
            // Hand-off is best effort. This process still exits.
        }
    }

    private static void WritePid()
    {
        try
        {
            Directory.CreateDirectory(UserDataRoots.LocalAppDataRoot);
            File.WriteAllText(PidPath, Environment.ProcessId.ToString());
        }
        catch (Exception)
        {
            // Foreground hand-off can fall back to a normal activate.
        }
    }

    private static void WriteRecord()
    {
        try
        {
            var record = new RunningInstance(
                Environment.ProcessId,
                BuildProvenance.Current.IsOfficial,
                Environment.ProcessPath ?? "",
                AppUpdateService.InstalledVersion());
            File.WriteAllText(RecordPath, record.Serialize());
        }
        catch (Exception)
        {
            // A second launch without a record falls back to the holder's executable path.
        }
    }

    private static int ReadPid()
    {
        try
        {
            var text = File.ReadAllText(PidPath).Trim();
            return int.TryParse(text, out var pid) ? pid : -1;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private const uint MbOk = 0x0;
    private const uint MbIconInformation = 0x40;
    private const uint MbSetForeground = 0x10000;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
