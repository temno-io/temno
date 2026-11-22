using System.Text.Json;
using Temno.Core.IO;

namespace Temno.Mcp;

/// <summary>
/// Windows writer for the <c>Mcp</c> log category named in <c>docs/ARCHITECTURE.md</c> §12.
/// Lines go to <c>%LOCALAPPDATA%\Temno\logs\mcp.log</c>. Callers pass key names only.
/// </summary>
public static class McpCategoryLog
{
    public const string Category = "Mcp";
    private static readonly object Gate = new();

    public static string LogFilePath { get; } = Path.Combine(UserDataRoots.LocalAppDataRoot, "logs", "mcp.log");

    /// <summary>Raised for every line, including when the file cannot be written. Tests subscribe here.</summary>
    public static event Action<string>? LineWritten;

    public static void UnknownArguments(string toolName, IReadOnlyList<string> keys)
    {
        var line = string.Join('\t',
            DateTimeOffset.UtcNow.ToString("o"),
            "category=Mcp",
            "event=unknown-argument",
            "tool=" + JsonSerializer.Serialize(toolName),
            "keys=" + JsonSerializer.Serialize(keys));
        try
        {
            LineWritten?.Invoke(line);
        }
        catch
        {
            // A listener must not hide the rejection.
        }

        try
        {
            var directory = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            lock (Gate)
                File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }
        catch
        {
            // The call still fails. The in-process event already carried the line.
        }
    }
}
