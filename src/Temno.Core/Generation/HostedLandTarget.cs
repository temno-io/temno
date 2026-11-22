namespace Temno.Core.Generation;

public readonly record struct HostedLandChoice(string Directory, bool Recovered, string? Notice);

/// <summary>
/// Where a finished job's file goes. The job's own project wins, whichever project is open. A
/// missing project keeps the file and says so.
/// </summary>
public static class HostedLandTarget
{
    public static string RecoveredRoot(string documentsRoot)
        => Path.Combine(documentsRoot, "recovered-results");

    public static HostedLandChoice Choose(string? projectPath, bool projectExists, string recoveredRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recoveredRoot);
        if (projectExists && !string.IsNullOrWhiteSpace(projectPath))
            return new HostedLandChoice(Path.GetFullPath(projectPath), false, null);

        var folder = Path.GetFullPath(recoveredRoot);
        var notice = string.IsNullOrWhiteSpace(projectPath)
            ? $"A generation finished, but its project is gone. The file is in {folder}."
            : $"A generation finished, but its project is gone ({projectPath}). The file is in {folder}.";
        return new HostedLandChoice(folder, true, notice);
    }
}
