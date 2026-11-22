using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Session;

namespace Temno.Core.Export;

/// <summary>How a media reference used by an export looks before the encode starts. WP-154.</summary>
public enum ExportMediaState
{
    /// <summary>Resolved to a file that is there.</summary>
    Available,

    /// <summary>Resolved to a local path with nothing at it. The layer will be dropped.</summary>
    Missing,

    /// <summary>No path could be produced at all — no manifest entry, or resolution refused it.</summary>
    Unresolved,

    /// <summary>A generation is still running. The file is not late; it has not been made yet.</summary>
    GenerationPending,

    /// <summary>A generation failed. Distinct from pending: this one is never arriving.</summary>
    GenerationFailed,

    /// <summary>
    /// A network path, deliberately not probed and never reported as a problem. Never merge this into
    /// <see cref="Missing"/>: WP-148 round 2 shipped a healthy <c>\\nas\…</c> file reported as
    /// missing, and probing it blocks for seconds on a disconnected share.
    /// </summary>
    NetworkUnchecked,

    /// <summary>A nested-sequence clip whose timeline id resolves to nothing.</summary>
    NestedSequenceMissing,

    /// <summary>
    /// WP-399: a file outside the project that the user has not allowed this project to use. Decided from
    /// the declared path alone, before the network rule, so a blocked <c>\\host\…</c> entry is named
    /// rather than skipped, and nothing a project names is contacted. The user can allow it.
    /// </summary>
    NotAllowed,
}

/// <summary>One media reference the export will use, and what is known about it.</summary>
/// <param name="Reference">Media id, or a timeline id for a nested sequence.</param>
/// <param name="Name">Display name — the manifest name where there is one, else the reference.</param>
public sealed record ExportMediaItem(string Reference, string Name, ExportMediaState State)
{
    /// <summary>What to tell the user, in the terms of what actually happened.</summary>
    public string Explanation => State switch
    {
        ExportMediaState.Missing => "missing from disk",
        ExportMediaState.Unresolved => "could not be resolved to a file",
        ExportMediaState.GenerationPending => "still generating",
        ExportMediaState.GenerationFailed => "generation failed",
        ExportMediaState.NestedSequenceMissing => "nested sequence not found",
        ExportMediaState.NetworkUnchecked => "on a network path, not checked",
        ExportMediaState.NotAllowed => "outside the project, not allowed",
        _ => "available",
    };
}

/// <summary>
/// One reference the export will reach, captured from the session so that classifying it needs no
/// further session access. WP-154.
/// </summary>
/// <param name="ReachedByVideo">
/// False when only the audio decode reaches it. The two paths resolve differently:
/// <c>TimelineExporter.ResolveAudioPath</c> accepts an absolute <c>MediaRef</c> that exists on disk
/// with no manifest entry, and <c>TimelineFrameResolver</c> does not.
/// </param>
public sealed record ExportMediaCandidate(
    string Reference,
    string Name,
    bool ReachedByVideo,
    MediaManifestEntry? Entry,
    ExportMediaState? Predetermined = null);

/// <summary>
/// What an export will reach, captured in one pass over the session. Taking this snapshot is a short,
/// allocation-only read that belongs under the session gate; classifying it touches the disk and
/// belongs off the dispatcher. Keeping them apart is why they are two calls.
/// </summary>
public sealed record ExportMediaPlan(IReadOnlyList<ExportMediaCandidate> Candidates, string? PackageRoot);

/// <summary>
/// What a preflight found. <see cref="Problems"/> is the list the user is warned about; it
/// deliberately excludes <see cref="ExportMediaState.NetworkUnchecked"/>, which is an absence of
/// evidence rather than evidence of absence.
/// </summary>
public sealed record ExportMediaPreflightReport
{
    public ExportMediaPreflightReport(IReadOnlyList<ExportMediaItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items = items;
    }

    public IReadOnlyList<ExportMediaItem> Items { get; init; }

    // Computed, not captured in a constructor: a `with` expression would otherwise carry the old
    // problem list beside a new item list.
    public IReadOnlyList<ExportMediaItem> Problems =>
        [.. Items.Where(i => i.State is not (ExportMediaState.Available or ExportMediaState.NetworkUnchecked))];

    public bool HasProblems => Problems.Count > 0;

    public int NetworkUncheckedCount =>
        Items.Count(i => i.State == ExportMediaState.NetworkUnchecked);

    /// <summary>Null when there is nothing to say. An all-clear banner would be decoration.</summary>
    public string? Sentence
    {
        get
        {
            var problems = Problems;
            if (problems.Count == 0) return null;
            var named = string.Join(", ", problems.Take(5).Select(p => $"“{p.Name}” ({p.Explanation})"));
            var more = problems.Count > 5 ? $", and {problems.Count - 5} more" : "";
            var subject = problems.Count == 1
                ? "1 media file this timeline uses will not appear in the export"
                : $"{problems.Count} media files this timeline uses will not appear in the export";
            return $"{subject}: {named}{more}.";
        }
    }
}

/// <summary>
/// Judges the media an export will actually reach, before it starts. WP-154.
/// <para>
/// This is deliberately not <c>InspectorProjectSummary</c>. That type is Inspector-shaped: it counts
/// the whole manifest, keeps a single missing name, and folds <c>failed:</c> generations in with
/// in-flight ones — correct for the panel's wording, wrong here, because an export cares about the
/// clips on one timeline and a failed generation is not pending.
/// </para>
/// <para>
/// It does keep that type's hard-won rule, and closes a hole in it: a network path is never probed
/// and never reported, and the decision is taken from the <em>declared</em> source before anything
/// resolves, because <c>MediaResolver.ResolvePath</c> itself stats every path segment on the way to
/// producing a project-relative path and would block on a dead share first.
/// </para>
/// </summary>
public static class ExportMediaPreflight
{
    /// <summary>Matches <c>TimelineFrameRenderer.MaxSequenceNestingDepth</c>.</summary>
    public const int MaxSequenceNestingDepth = 8;

    /// <summary>Collect and classify in one call. Convenient for tests and for callers off-thread.</summary>
    public static ExportMediaPreflightReport Inspect(
        Timeline timeline,
        MediaManifest manifest,
        MediaResolver? resolver,
        Func<string, Timeline?>? timelineLookup = null,
        bool projectIsSaved = true,
        Func<string, bool>? fileExists = null,
        string? packageRoot = null)
        => Classify(
            Collect(timeline, manifest, timelineLookup, packageRoot),
            resolver,
            projectIsSaved,
            fileExists);

    /// <summary>
    /// Walks <paramref name="timeline"/> and every nested sequence it reaches, and records each
    /// distinct media reference the export will use. Reads the session and touches no disk, so it is
    /// safe — and short enough — to run under the session gate.
    /// </summary>
    public static ExportMediaPlan Collect(
        Timeline timeline,
        MediaManifest manifest,
        Func<string, Timeline?>? timelineLookup = null,
        string? packageRoot = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(manifest);

        var byId = new Dictionary<string, MediaManifestEntry>(StringComparer.Ordinal);
        foreach (var entry in manifest.Entries)
            byId.TryAdd(entry.Id, entry);

        var candidates = new List<ExportMediaCandidate>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var visitedTimelines = new HashSet<string>(StringComparer.Ordinal) { timeline.Id };
        var queue = new List<(Timeline T, int Depth)> { (timeline, 0) };

        for (var i = 0; i < queue.Count; i++)
        {
            var (current, depth) = queue[i];
            foreach (var track in current.Tracks)
            {
                // Mirror what the export actually reaches. TimelineFrameResolver skips hidden and audio
                // tracks (non-audio visual tracks such as video/image/lottie/sequence render);
                // TimelineAudioDecoder walks audio and video tracks when unmuted. Warning about media
                // on a hidden track would be a false statement — and the only way past the warning is
                // "Export anyway", which would then disarm the refusal for a clip that really is missing.
                if (track.Hidden) continue;

                foreach (var clip in track.Clips)
                {
                    if (string.IsNullOrEmpty(clip.MediaRef))
                        continue;

                    var reachedByVideo = track.Type != ClipType.audio
                        && clip.MediaType is not (ClipType.text or ClipType.audio);
                    var reachedByAudio = track.Type is ClipType.audio or ClipType.video
                        && !track.Muted;
                    if (!reachedByVideo && !reachedByAudio)
                        continue;

                    // Both fields are tested because they are set independently, and the renderer
                    // recurses on MediaType while TimelineExtensions filters on SourceClipType.
                    if (clip.MediaType == ClipType.sequence || clip.SourceClipType == ClipType.sequence)
                    {
                        var nested = timelineLookup?.Invoke(clip.MediaRef);
                        if (nested is null)
                        {
                            Add(new ExportMediaCandidate(
                                clip.MediaRef, clip.MediaRef, reachedByVideo, null,
                                ExportMediaState.NestedSequenceMissing));
                            continue;
                        }

                        // Root is depth 0; renderer and audio decoder both reach media at the cap.
                        // Only deeper timelines are replaced by a placeholder or omitted.
                        if (depth + 1 <= MaxSequenceNestingDepth && visitedTimelines.Add(nested.Id))
                            queue.Add((nested, depth + 1));
                        continue;
                    }

                    byId.TryGetValue(clip.MediaRef, out var entry);
                    var name = string.IsNullOrWhiteSpace(entry?.Name) ? clip.MediaRef : entry!.Name;
                    Add(new ExportMediaCandidate(clip.MediaRef, name, reachedByVideo, entry));
                }
            }
        }

        return new ExportMediaPlan(candidates, packageRoot);

        void Add(ExportMediaCandidate candidate)
        {
            if (index.TryGetValue(candidate.Reference, out var at))
            {
                // The same asset can be on a video and an audio track. Video reach is the stricter
                // rule, so it wins.
                if (candidate.ReachedByVideo && !candidates[at].ReachedByVideo)
                    candidates[at] = candidates[at] with { ReachedByVideo = true };
                return;
            }

            index[candidate.Reference] = candidates.Count;
            candidates.Add(candidate);
        }
    }

    /// <summary>
    /// Decides the state of every candidate. This is the half that touches the disk, so it belongs
    /// off the UI thread.
    /// </summary>
    /// <param name="projectIsSaved">
    /// False makes every project-relative entry unresolvable through no fault of the media, so
    /// <see cref="ExportMediaState.Unresolved"/> is not reported. Same rule as
    /// <c>InspectorProjectSummary.ReportableProblemCount</c>.
    /// </param>
    /// <param name="fileExists">Injectable so tests can state the disk rather than build one.</param>
    public static ExportMediaPreflightReport Classify(
        ExportMediaPlan plan,
        MediaResolver? resolver,
        bool projectIsSaved = true,
        Func<string, bool>? fileExists = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        fileExists ??= File.Exists;

        var items = new List<ExportMediaItem>(plan.Candidates.Count);
        foreach (var candidate in plan.Candidates)
        {
            items.Add(candidate.Predetermined is { } fixedState
                ? new ExportMediaItem(candidate.Reference, candidate.Name, fixedState)
                : new ExportMediaItem(
                    candidate.Reference,
                    candidate.Name,
                    StateOf(candidate, plan.PackageRoot, resolver, projectIsSaved, fileExists)));
        }

        return new ExportMediaPreflightReport(items);
    }

    private static ExportMediaState StateOf(
        ExportMediaCandidate candidate,
        string? packageRoot,
        MediaResolver? resolver,
        bool projectIsSaved,
        Func<string, bool> fileExists)
    {
        var entry = candidate.Entry;
        var status = entry?.GenerationStatus;
        if (!string.IsNullOrEmpty(status))
        {
            if (status.StartsWith("failed:", StringComparison.Ordinal))
                return ExportMediaState.GenerationFailed;
            if (status is "preparing" or "generating" or "downloading")
                return ExportMediaState.GenerationPending;
        }

        // WP-399: before the network rule, from the path text only. A bare rooted reference with no entry
        // is a path the project named: in a restricted session it needs a grant like an external entry.
        if (entry is not null && resolver is not null && resolver.IsBlocked(entry))
            return ExportMediaState.NotAllowed;
        if (entry is null && resolver is not null && IsRooted(candidate.Reference)
            && !resolver.AllowsLooseFile(candidate.Reference))
            return ExportMediaState.NotAllowed;

        // Decided from the declared source, before anything resolves. MediaResolver.ResolvePath calls
        // ContainsReparsePoint, which stats every segment of a project-relative path — so asking it
        // first would block on the dead share this rule exists to avoid touching.
        if (IsNetworkSource(entry, packageRoot, candidate.Reference))
            return ExportMediaState.NetworkUnchecked;

        string? path;
        try
        {
            path = entry is null ? null : resolver?.ResolvePath(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return Unresolved(projectIsSaved);
        }

        if (string.IsNullOrEmpty(path))
        {
            // TimelineExporter.ResolveAudioPath accepts an absolute MediaRef that exists on disk with
            // no manifest entry, so reporting that unresolved would be a figure the exporter itself
            // disagrees with. The picture path has no such fallback for a decoded layer (only smart
            // render's stream copy has one, gated the same way since WP-399), so for a reference the
            // video path reaches, no entry means the layer will be dropped. An entry that did not resolve
            // never falls back to its id, whatever the id looks like.
            if (candidate.ReachedByVideo || !IsRooted(candidate.Reference) || entry is not null)
                return Unresolved(projectIsSaved);
            if (TryExists(candidate.Reference, fileExists, out var refExists) && refExists)
                return ExportMediaState.Available;
            return Unresolved(projectIsSaved);
        }

        if (IsNetworkPath(path))
            return ExportMediaState.NetworkUnchecked;

        if (!TryExists(path, fileExists, out var exists))
            return Unresolved(projectIsSaved);

        return exists ? ExportMediaState.Available : ExportMediaState.Missing;
    }

    private static bool IsNetworkSource(MediaManifestEntry? entry, string? packageRoot, string reference)
        => entry?.Source switch
        {
            MediaSource.ExternalSource e => IsNetworkPath(e.AbsolutePath),
            // A project-relative entry lands under the package, so the package's own root decides.
            MediaSource.ProjectSource => IsNetworkPath(packageRoot),
            _ => IsNetworkPath(reference),
        };

    /// <summary>
    /// An unsaved project cannot resolve a project-relative entry, which says nothing about the
    /// media. Reporting it would be the same category error as calling an unprobed network file
    /// missing.
    /// </summary>
    private static ExportMediaState Unresolved(bool projectIsSaved) =>
        projectIsSaved ? ExportMediaState.Unresolved : ExportMediaState.Available;

    private static bool TryExists(string path, Func<string, bool> fileExists, out bool exists)
    {
        try
        {
            exists = fileExists(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exists = false;
            return false;
        }
    }

    private static bool IsRooted(string reference)
    {
        try
        {
            return Path.IsPathRooted(reference);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// UNC only, matching <c>InspectorProjectSummary</c>. A mapped drive letter pointing at a share
    /// is not detectable from the path and still gets probed — the same known gap, not a new one.
    /// </summary>
    private static bool IsNetworkPath(string? path) =>
        path is not null
        && (path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal));
}
