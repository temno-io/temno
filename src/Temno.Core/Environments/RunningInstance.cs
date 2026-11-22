using System.Text.Json;

namespace Temno.Environments;

/// <summary>
/// The copy of Temno that holds its environment's single-instance lock, as that copy records itself in
/// <c>instance.json</c> beside <c>instance.pid</c> (WP-402 D-1). A build from source and the installed Temno share one data
/// folder, sign-in and MCP port, so only one runs at a time. A second launch of the same build brings it forward; a
/// launch of a different build names the open one instead of switching to it.
/// </summary>
public sealed record RunningInstance(int ProcessId, bool IsOfficial, string ExecutablePath, string Version)
{
    public const string FileName = "instance.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Serialize() => JsonSerializer.Serialize(
        new Record { Pid = ProcessId, Official = IsOfficial, Path = ExecutablePath, Version = Version }, JsonOptions);

    /// <summary>The record in <paramref name="json"/>, or null when it is missing or unreadable.</summary>
    public static RunningInstance? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            var record = JsonSerializer.Deserialize<Record>(json, JsonOptions);
            if (record is null || record.Pid <= 0 || string.IsNullOrWhiteSpace(record.Path))
                return null;
            return new RunningInstance(record.Pid, record.Official, record.Path, record.Version ?? "");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What a second launch says, or null when it should bring the open window forward as before. The open copy is
    /// known by the executable path of the process that holds the lock (<paramref name="openPath"/>, null when it
    /// cannot be read) and by its record, which counts only when it names that same process and path, so a record
    /// left by an earlier run is never believed. With neither, the launch keeps the old behaviour.
    /// </summary>
    public static string? SecondLaunchMessage(
        string appName,
        bool selfOfficial,
        string selfPath,
        int openPid,
        string? openPath,
        RunningInstance? openRecord)
    {
        // Without the holder's path, a record names it only if its executable is there and is Temno by name: a process
        // number from an earlier run can belong to another process by now.
        var record = openRecord is not null
                     && openRecord.ProcessId == openPid
                     && (openPath is not null
                         ? SamePath(openRecord.ExecutablePath, openPath)
                         : File.Exists(openRecord.ExecutablePath) && SameFileName(openRecord.ExecutablePath, selfPath))
            ? openRecord
            : null;
        var path = openPath ?? record?.ExecutablePath;
        if (path is null || SamePath(path, selfPath))
            return null;
        // Without a record, a process by another name is not Temno: instance.pid was left by an earlier run and its
        // number reused before the lock's holder wrote its own.
        if (record is null && !SameFileName(path, selfPath))
            return null;

        var why = $"Both use the same projects, settings and sign-in, so one copy of {appName} runs at a time.";
        var where = "The open copy runs from " + path + ".";
        string first;
        if (record is null)
            first = $"Another copy of {appName} is already open. Close it to start this one.";
        else if (record.IsOfficial && !selfOfficial)
            first = $"{appName} is already open. Close it to start the copy you built from source.";
        else if (!record.IsOfficial && selfOfficial)
            first = $"A copy of {appName} built from source is already open. Close it to start {appName}.";
        else if (!record.IsOfficial)
            first = $"Another copy of {appName} built from source is already open. Close it to start this one.";
        else
            first = $"Another copy of {appName} is already open. Close it to start this one.";
        return first + "\n\n" + why + "\n" + where;
    }

    private static bool SameFileName(string a, string b)
        => string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);

    public static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class Record
    {
        public int Pid { get; set; }
        public bool Official { get; set; }
        public string? Path { get; set; }
        public string? Version { get; set; }
    }
}
