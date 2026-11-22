using Temno.Core.Session;
using Temno.Mcp;

namespace Temno.App.Services;

/// <summary>
/// Imports local media files into the active project through the MCP <c>import_media</c> tool, which
/// copies into the package, adds the manifest entry, and probes duration/dimensions via ffprobe.
/// Copy and probe run on the caller thread; only the manifest commit is marshaled onto the
/// project-session dispatcher.
/// </summary>
public sealed class MediaImportService
{
    /// <summary>File-open-picker filter list: every supported extension.</summary>
    public static IReadOnlyList<string> SupportedExtensions => MediaLibraryCommands.SupportedFileExtensions;

    public static bool IsSupported(string path) =>
        MediaLibraryCommands.TryGetTypeFromExtension(Path.GetExtension(path), out _);

    public sealed record ImportOutcome(int Imported, IReadOnlyList<string> Errors);

    public async Task<ImportOutcome> ImportAsync(
        ProjectSession session,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default,
        bool referenceInPlace = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        var errors = new List<string>();
        var imported = 0;

        foreach (var path in paths)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var name = Path.GetFileName(path);
            if (!File.Exists(path)) { errors.Add($"{name}: file not found"); continue; }
            if (!IsSupported(path)) { errors.Add($"{name}: unsupported file type"); continue; }

            try
            {
                // Copy and ffprobe stay on this worker; RunSerialized is only the manifest commit.
                var result = await Task.Run(
                    () => ToolExecutor.ImportMediaFile(
                        session, path, referenceInPlace: referenceInPlace,
                        cancellationToken: cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                if (result.IsError)
                {
                    errors.Add($"{name}: {string.Join(" ", result.Content.Select(c => c.Text))}");
                    continue;
                }

                imported++;
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }

        return new ImportOutcome(imported, errors);
    }
}
