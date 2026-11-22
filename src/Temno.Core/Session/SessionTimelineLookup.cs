using System.Runtime.CompilerServices;
using Temno.Core.Models;

namespace Temno.Core.Session;

/// <summary>
/// The one safe shape for the <c>timelineLookup</c> a renderer, decoder, scanner or exporter is
/// handed: resolves a nested sequence's <c>mediaRef</c> to its <see cref="Timeline"/> without
/// enumerating the session's live <c>ProjectFile.Timelines</c> on the caller's thread.
/// </summary>
/// <remarks>
/// <para>The lookup is invoked once per nested layer per frame for the whole of a render, and again
/// for the audio pass. A raw <c>Timelines.FirstOrDefault(...)</c> there leaves the shared list
/// unsynchronised for the duration of the job: add a timeline on the UI thread while it runs — New
/// Timeline, or MCP <c>duplicate_timeline</c> — and <c>List&lt;T&gt;</c> throws
/// <c>InvalidOperationException("Collection was modified")</c> inside the lookup, where nothing on
/// the nested path catches it, so the export dies.</para>
/// <para>Wrapping each call in <see cref="ProjectHub.RunSerialized{T}(Func{T})"/> would close the
/// race and pay for it with a blocking round-trip to the UI thread per layer per frame. Instead this
/// enumerates <em>once</em> under the gate into an immutable map, and reuses it until
/// <see cref="ProjectSession.Revision"/> moves. Revision is bumped by every content mutation
/// whatever the caller (WP-153), and by undo, redo and rollback, which replace <c>ProjectFile</c>
/// wholesale — so the map cannot outlive the object graph it came from. That is a statement about
/// <em>freshness</em> only: those three do not themselves take the gate (see
/// <c>docs/WORK_LEDGER.md</c> §4a.4), they just cannot leave a stale map behind.</para>
/// <para>Freshness is not traded away: the revision is read <em>before</em> the snapshot, so the map
/// is always at least as new as the revision it is filed under. A snapshot served on a revision
/// match therefore holds the current list; the worst case is one redundant rebuild.</para>
/// <para>One instance per session, so a preview provider and an export share the same map.</para>
/// </remarks>
public sealed class SessionTimelineLookup
{
    private static readonly ConditionalWeakTable<ProjectSession, SessionTimelineLookup> Instances = new();

    private readonly ProjectSession _session;
    private Snapshot? _snapshot;

    private SessionTimelineLookup(ProjectSession session) => _session = session;

    /// <summary>The safe lookup for <paramref name="session"/>, in the shape consumers take.</summary>
    public static Func<string, Timeline?> For(ProjectSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Instances.GetValue(session, static s => new SessionTimelineLookup(s)).Find;
    }

    /// <summary>Resolves a nested sequence's <c>mediaRef</c>, or null when no timeline carries it.</summary>
    public Timeline? Find(string mediaRef)
    {
        if (string.IsNullOrEmpty(mediaRef)) return null;
        return Current().ById.GetValueOrDefault(mediaRef);
    }

    private Snapshot Current()
    {
        var cached = Volatile.Read(ref _snapshot);
        var revision = _session.Revision;
        if (cached is not null && cached.Revision == revision)
            return cached;

        // First-wins on a duplicate id, matching the FirstOrDefault this replaces.
        var fresh = new Snapshot(
            revision,
            ProjectHub.RunUnderGate(() =>
            {
                var byId = new Dictionary<string, Timeline>(StringComparer.Ordinal);
                foreach (var timeline in _session.ProjectFile.Timelines)
                {
                    if (!string.IsNullOrEmpty(timeline.Id))
                        byId.TryAdd(timeline.Id, timeline);
                }
                return byId;
            }));

        // A racing rebuild may publish either map; both are at least as new as their revision, and a
        // stale one is corrected by the next revision comparison rather than served indefinitely.
        Volatile.Write(ref _snapshot, fresh);
        return fresh;
    }

    private sealed record Snapshot(int Revision, Dictionary<string, Timeline> ById);
}
