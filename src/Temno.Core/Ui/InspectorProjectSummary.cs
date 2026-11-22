using Temno.Core.Session;

namespace Temno.Core.Ui;

/// <summary>
/// Project-scope facts for the Inspector's no-selection state. WP-148; moved here by WP-152.
/// </summary>
/// <remarks>
/// Every figure is derived from the live session objects the rest of the app renders from — the
/// manifest the media library lists, the timeline the timeline control draws, and the same
/// <c>MediaResolver</c> that playback and export use to find a file. Nothing here is
/// incremented or cached.
/// <para>
/// That is deliberate. WP-144 shipped a media count that read <c>0 items</c> beside one visible item
/// because it tracked its own counter and a code path forgot to bump it. A count that can disagree
/// with the thing it counts is worse than no count, so this type recomputes from source every time
/// and has no mutable state to drift.
/// </para>
/// </remarks>
public sealed record InspectorProjectSummary(
    int MediaCount,
    int ClipCount,
    int TrackCount,
    int MissingMediaCount,
    int UnresolvedMediaCount,
    int PendingGenerationCount,
    int NetworkUncheckedCount,
    bool ProjectIsSaved,
    string? FirstMissingName)
{
    public static InspectorProjectSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, false, null);

    /// <summary>
    /// Generation placeholders are written into the manifest <b>before</b> their file exists
    /// (<c>ToolExecutor</c> creates them with <c>Source = MediaSource.Project(...)</c> and
    /// <c>GenerationStatus = "preparing"</c>). Until the job lands, the absence of the file is the
    /// expected state, not a fault — and <c>failed:</c> means the file was never written at all.
    /// Calling either "missing from disk" would be a lying figure of exactly the kind this type
    /// exists to prevent. Vocabulary matches <c>ToolExecutor.ShouldResumeGeneration</c>.
    /// </summary>
    private static bool IsFileNotExpectedYet(string? generationStatus)
    {
        if (string.IsNullOrEmpty(generationStatus)) return false;
        return generationStatus is "preparing" or "generating" or "downloading"
            || generationStatus.StartsWith("failed:", StringComparison.Ordinal);
    }

    /// <summary>
    /// How many referenced media files this build can actually prove are gone.
    /// </summary>
    /// <remarks>
    /// <see cref="UnresolvedMediaCount"/> is only a problem once the project has been saved. Before
    /// that there is no package root, so a project-relative entry cannot be resolved and its absence
    /// says nothing about the file. Reporting those as missing would be the lying-figure defect this
    /// type exists to prevent, so an unsaved project reports only what it can actually prove.
    /// <para>
    /// <see cref="NetworkUncheckedCount"/> is deliberately <b>not</b> included at all. Those files
    /// were never probed, so nothing is known about them either way — and the first draft of the
    /// network guard did add them here, which announced "missing from disk" for healthy media on a
    /// reachable share. Not knowing is not the same as being gone, in both directions.
    /// </para>
    /// </remarks>
    public int ReportableProblemCount =>
        MissingMediaCount + (ProjectIsSaved ? UnresolvedMediaCount : 0);

    public static InspectorProjectSummary FromSession(ProjectSession? session)
    {
        if (session is null) return Empty;

        var timeline = session.ActiveTimeline;
        var tracks = timeline.Tracks;
        var clipCount = 0;
        foreach (var track in tracks)
            clipCount += track.Clips.Count;

        var entries = session.Manifest.Entries;
        var missing = 0;
        var unresolved = 0;
        var pending = 0;
        var networkUnchecked = 0;
        string? firstMissingName = null;

        foreach (var entry in entries)
        {
            if (IsFileNotExpectedYet(entry.GenerationStatus))
            {
                pending++;
                continue;
            }

            string? path;
            try
            {
                path = session.Resolver.ResolvePath(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or ArgumentException or NotSupportedException)
            {
                unresolved++;
                continue;
            }

            if (path is null)
            {
                unresolved++;
                continue;
            }

            // A UNC path on a disconnected share makes File.Exists block for seconds, on the UI
            // thread, with no chance for a busy overlay to appear because the thread is inside the
            // call. Network media is therefore never probed, and never reported: an unprobed file
            // is not evidence of anything, and a frozen editor is not worth a slightly better count.
            if (IsNetworkPath(path))
            {
                networkUnchecked++;
                continue;
            }

            try
            {
                if (!File.Exists(path))
                {
                    missing++;
                    firstMissingName ??= string.IsNullOrWhiteSpace(entry.Name)
                        ? Path.GetFileName(path)
                        : entry.Name;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unresolved++;
            }
        }

        return new InspectorProjectSummary(
            MediaCount: entries.Count,
            ClipCount: clipCount,
            TrackCount: tracks.Count,
            MissingMediaCount: missing,
            UnresolvedMediaCount: unresolved,
            PendingGenerationCount: pending,
            NetworkUncheckedCount: networkUnchecked,
            ProjectIsSaved: !string.IsNullOrEmpty(session.PackagePath),
            FirstMissingName: firstMissingName);
    }

    private static bool IsNetworkPath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        path.StartsWith("//", StringComparison.Ordinal);

    /// <summary>
    /// "9 clips · 3 tracks". The separator is deliberate: "9 clips on 3 tracks" reads as though the
    /// clips span all three, and a default project has four empty tracks it never mentions.
    /// An empty timeline says "Empty" rather than "0 clips · 4 tracks".
    /// </summary>
    public string TimelineLabel
    {
        get
        {
            if (ClipCount == 0) return "Empty";
            var clips = ClipCount == 1 ? "1 clip" : $"{ClipCount} clips";
            var tracks = TrackCount == 1 ? "1 track" : $"{TrackCount} tracks";
            return $"{clips} · {tracks}";
        }
    }

    /// <summary>
    /// The warning sentence, or null when there is nothing wrong. Null means the row is not
    /// rendered at all — an all-clear banner would be decoration, not information.
    /// </summary>
    /// <remarks>
    /// One missing file is named, because "1 media file is missing" leaves the user clicking every
    /// row to find out which. Several are counted against the manifest total, so the sentence
    /// carries the scale of the problem without a standing count that duplicates the media library.
    /// </remarks>
    public string? ProblemLabel
    {
        get
        {
            var count = ReportableProblemCount;
            if (count == 0) return null;
            if (count == 1 && !string.IsNullOrWhiteSpace(FirstMissingName))
                return $"“{FirstMissingName}” is missing from disk";
            if (count == 1)
                return "1 media file is missing from disk";
            return $"{count} of {MediaCount} media files are missing from disk";
        }
    }
}
