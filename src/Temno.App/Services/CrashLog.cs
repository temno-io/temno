using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
#if !CRASHLOG_JOURNAL_TESTS
using Microsoft.UI.Xaml;
using Temno.Core.IO;
#endif

namespace Temno.App.Services;

#if !CRASHLOG_JOURNAL_TESTS
/// <summary>
/// A WinUI 3 app with no <see cref="Application.UnhandledException"/> handler dies as a stowed
/// exception (0xc000027b) with the managed stack discarded: WER records "combase.dll / E_FAIL" and
/// nothing else. Two such crashes on 2026-09-02 left no way to tell what threw. This writes the
/// exception to %LOCALAPPDATA%\Temno\logs\crash.log before the process goes down.
/// </summary>
public static class CrashLog
{
    private static CrashRunJournal? _journal;
    private static volatile bool _windowClosed;
    private static volatile bool _faulted;

    public static string Path { get; } = System.IO.Path.Combine(
        UserDataRoots.LocalAppDataRoot,
        "logs",
        "crash.log");

    public static void Install(Application app)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            _journal = new CrashRunJournal(
                System.IO.Path.Combine(directory, "runs"),
                IsSameProcessRunning,
                FindNativeDumps,
                message => WriteRecord("PreviousProcess.UncleanExit", message));
            using var process = Process.GetCurrentProcess();
            _journal.Begin(new CrashRunJournal.Run(
                process.Id, process.StartTime.ToUniversalTime().Ticks, Environment.ProcessPath ?? "Temno.App.exe"));

            // Packaged builds can request an app-local WER dump without machine-wide registry changes.
            // Unpackaged builds still retain the run journal and discover standard WER local dumps.
            var result = WerRegisterAppLocalDump("CrashDumps");
            WriteRecord("NativeDiagnostics", $"App-local WER registration: 0x{result:X8}; run journal: {_journal.DirectoryPath}");
        }
        catch (Exception ex)
        {
            Write("NativeDiagnostics.Install", ex);
        }

        app.UnhandledException += (_, e) =>
        {
            _faulted = true;
            Write("Application.UnhandledException", e.Exception);
            // Leave e.Handled false: swallowing here would hide a broken state, not fix it.
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            _faulted = true;
            Write("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Write("TaskScheduler.UnobservedTaskException", e.Exception);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (_windowClosed && !_faulted)
            {
                try { _journal?.Complete(); }
                catch { /* Keep the marker when clean-exit recording fails. */ }
            }
        };
    }

    public static void MarkWindowClosed() => _windowClosed = true;

    public static void Write(string source, Exception? exception)
        => WriteRecord(source, exception?.ToString() ?? "<no exception object>");

    private static bool WriteRecord(string source, string details)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var text = new StringBuilder()
                .AppendLine("=====================================================")
                .AppendLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}  {source}")
                .AppendLine($"thread {Environment.CurrentManagedThreadId}")
                .AppendLine(details)
                .AppendLine()
                .ToString();
            System.IO.File.AppendAllText(Path, text);
            return true;
        }
        catch
        {
            // A diagnostic must never be the thing that kills the app.
            return false;
        }
    }

    private static bool IsSameProcessRunning(CrashRunJournal.Run run)
    {
        try
        {
            using var process = Process.GetProcessById(run.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == run.StartTimeUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch { return true; } // An inaccessible live process is not evidence of a crash.
    }

    private static IEnumerable<string> FindNativeDumps(CrashRunJournal.Run run)
    {
        var directories = new List<string>
        {
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrashDumps"),
        };
        try { directories.Add(System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "CrashDumps")); }
        catch { /* No package identity in an unpackaged build. */ }
        var name = $"{System.IO.Path.GetFileName(run.ExecutablePath)}.{run.ProcessId}.dmp";
        return directories.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(directory => System.IO.Path.Combine(directory, name))
            .Where(file => System.IO.File.Exists(file) && System.IO.File.GetLastWriteTimeUtc(file).Ticks >= run.StartTimeUtcTicks)
            .ToArray();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int WerRegisterAppLocalDump(string localAppDataRelativePath);
}
#endif

// Linked directly into the portable verification tests; WinUI/bootstrap code above is excluded there.
internal sealed class CrashRunJournal(
    string directoryPath,
    Func<CrashRunJournal.Run, bool> isRunning,
    Func<CrashRunJournal.Run, IEnumerable<string>> findDumps,
    Func<string, bool> report)
{
    internal sealed record Run(int ProcessId, long StartTimeUtcTicks, string ExecutablePath);
    private string? _activeMarker;
    public string DirectoryPath { get; } = directoryPath;

    public void Begin(Run current)
    {
        Directory.CreateDirectory(DirectoryPath);
        _activeMarker = System.IO.Path.Combine(DirectoryPath, $"{current.ProcessId}-{current.StartTimeUtcTicks}.json");
        File.WriteAllText(_activeMarker, JsonSerializer.Serialize(current));

        foreach (var marker in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            if (string.Equals(marker, _activeMarker, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var previous = JsonSerializer.Deserialize<Run>(File.ReadAllText(marker));
                if (previous is null || previous.ProcessId <= 0 || previous.StartTimeUtcTicks <= 0
                    || string.IsNullOrWhiteSpace(previous.ExecutablePath) || isRunning(previous))
                    continue;
                var dumps = findDumps(previous).ToArray();
                var details = $"Process ended without a confirmed clean shutdown; cause is not inferred.\n"
                    + $"pid={previous.ProcessId}; startedUtc={new DateTime(previous.StartTimeUtcTicks, DateTimeKind.Utc):O}; executable={previous.ExecutablePath}\n"
                    + $"WER dumps: {(dumps.Length == 0 ? "none found; dump collection depends on Windows configuration" : string.Join("; ", dumps))}";
                if (report(details))
                    File.Delete(marker);
            }
            catch
            {
                // A malformed, locked, or unwritable record stays available for later inspection.
            }
        }
    }

    public void Complete()
    {
        if (_activeMarker is not null)
            File.Delete(_activeMarker);
    }
}
