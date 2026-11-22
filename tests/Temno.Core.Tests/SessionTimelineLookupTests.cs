using Temno.Core.Models;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-177. The nested-sequence lookup is invoked once per nested layer per frame for the whole of a
/// render and again for the audio pass, so a raw <c>Timelines.FirstOrDefault(...)</c> there leaves
/// the shared list unsynchronised for the length of the job.
/// </summary>
[Collection("EnvRegistry")]
public sealed class SessionTimelineLookupTests
{
    /// <summary>Bounded on purpose: the writer holds a process-wide gate the rest of the suite shares.</summary>
    private const int EditsDuringRender = 150;

    private static ProjectSession NewSession(int nested = 1)
    {
        var session = ProjectSession.CreateNew();
        for (var i = 0; i < nested; i++)
            session.ProjectFile.Timelines.Add(new Timeline { Name = $"Nested {i}" });
        return session;
    }

    [Fact]
    public void ResolvesByMediaRef()
    {
        var session = NewSession();
        var nested = session.ProjectFile.Timelines[^1];

        Assert.Same(nested, SessionTimelineLookup.For(session)(nested.Id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-such-timeline")]
    public void ReturnsNullWhenNothingCarriesTheRef(string mediaRef)
        => Assert.Null(SessionTimelineLookup.For(NewSession())(mediaRef));

    [Fact]
    public void FirstWinsOnADuplicateId()
    {
        var session = NewSession();
        var first = session.ProjectFile.Timelines[^1];
        session.ProjectFile.Timelines.Add(new Timeline { Id = first.Id, Name = "Shadow" });
        session.MarkDirty();

        Assert.Same(first, SessionTimelineLookup.For(session)(first.Id));
    }

    [Fact]
    public void OneLookupPerSession()
    {
        var session = NewSession();

        Assert.Same(
            ((Delegate)SessionTimelineLookup.For(session)).Target,
            ((Delegate)SessionTimelineLookup.For(session)).Target);
    }

    /// <summary>
    /// The reason a snapshot is allowed at all: it is re-taken when the session's revision moves, so
    /// a preview provider bound once at open still resolves a sequence nested minutes later.
    /// </summary>
    [Fact]
    public void SeesATimelineAddedAfterTheFirstLookup()
    {
        var session = NewSession();
        var lookup = SessionTimelineLookup.For(session);
        var late = new Timeline { Name = "Added later" };
        Assert.Null(lookup(late.Id));

        session.ProjectFile.Timelines.Add(late);
        session.MarkDirty();

        Assert.Same(late, lookup(late.Id));
    }

    /// <summary>Undo replaces <c>ProjectFile</c> wholesale; the map must not outlive that graph.</summary>
    [Fact]
    public void FollowsUndoOntoTheRestoredProjectFile()
    {
        var session = NewSession();
        var lookup = SessionTimelineLookup.For(session);
        var baseline = session.ProjectFile.Timelines[^1].Id;
        Assert.NotNull(lookup(baseline));

        session.RecordBeforeEdit();
        var added = new Timeline { Name = "Undone" };
        session.ProjectFile.Timelines.Add(added);
        session.MarkDirty();
        Assert.NotNull(lookup(added.Id));

        session.Undo();

        Assert.Null(lookup(added.Id));
        var restored = lookup(baseline);
        Assert.NotNull(restored);
        Assert.Same(session.ProjectFile.Timelines.First(t => t.Id == baseline), restored);
    }

    /// <summary>
    /// The shape the fix exists to avoid. <see cref="ProjectHub.RunSerialized{T}(Func{T})"/> would
    /// close the race by blocking on a round-trip to the UI thread, which inside a render loop costs
    /// one per nested layer per frame and deadlocks a caller already holding a lock the UI thread
    /// wants. A steady-state lookup must reach the dispatcher zero times.
    /// </summary>
    [Fact]
    public void DoesNotMarshalToTheDispatcher()
    {
        var session = NewSession();
        var nested = session.ProjectFile.Timelines[^1];
        var dispatcher = new CountingDispatcher();
        ProjectHub.ConfigureDispatcher(dispatcher);
        try
        {
            var lookup = SessionTimelineLookup.For(session);
            for (var frame = 0; frame < 500; frame++)
                Assert.Same(nested, lookup(nested.Id));

            Assert.Equal(0, dispatcher.InvocationCount);
        }
        finally
        {
            ProjectHub.ConfigureDispatcher(null);
        }
    }

    /// <summary>
    /// Control, and deterministic: the raw enumeration throws from inside the lookup — where nothing
    /// on the nested path catches it — when a timeline lands while it enumerates. The loop body is
    /// where a concurrent New Timeline gets to interleave. On .NET 10, LINQ's FirstOrDefault over a
    /// List reads its array without the list's modification check, so that shape now races
    /// silently instead of throwing (WP-404); the list's own enumerator still checks.
    /// </summary>
    [Fact]
    public void RawEnumerationThrowsWhenATimelineIsAddedMidLookup()
    {
        var session = NewSession(nested: 4);
        var added = false;
        Func<string, Timeline?> raw = id =>
        {
            foreach (var t in session.ProjectFile.Timelines)
            {
                if (!added)
                {
                    added = true;
                    session.ProjectFile.Timelines.Add(new Timeline { Name = "New Timeline" });
                }
                if (t.Id == id)
                    return t;
            }
            return null;
        };

        var error = Assert.Throws<InvalidOperationException>(() => raw("no-such-timeline"));
        Assert.Contains("Collection was modified", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The WP's scenario, driven through the command the New Timeline button calls: a render
    /// resolving nested layers on one thread while timelines are added on another. Nothing on the
    /// nested path catches <c>InvalidOperationException</c>, so one throw here is the export dying.
    /// </summary>
    [Fact]
    public async Task SurvivesNewTimelineDuringARender()
    {
        var session = NewSession(nested: 64);
        var nested = session.ProjectFile.Timelines[^1];
        var lookup = SessionTimelineLookup.For(session);

        var editing = AddTimelinesAsync(session);
        var frames = 0;
        while (!editing.IsCompleted || frames < 500)
        {
            // The resolvable layer and a missing one: a miss used to enumerate the whole list.
            Assert.Same(nested, lookup(nested.Id));
            Assert.Null(lookup("no-such-timeline"));
            frames++;
        }

        await editing;
        Assert.Equal(65 + EditsDuringRender, session.ProjectFile.Timelines.Count);
    }

    /// <summary>
    /// The same New Timeline harness against the shape being replaced, so the test above cannot pass
    /// by never interleaving. Reported rather than asserted: a machine that refused to interleave
    /// would make an assertion here flaky, and the deterministic control above already establishes
    /// the defect.
    /// </summary>
    [Fact]
    public async Task RawEnumerationLosesTheNewTimelineRace()
    {
        var session = NewSession(nested: 64);
        Func<string, Timeline?> raw = id => session.ProjectFile.Timelines.FirstOrDefault(t => t.Id == id);

        var editing = AddTimelinesAsync(session);
        InvalidOperationException? observed = null;
        while (!editing.IsCompleted)
        {
            try { raw("no-such-timeline"); }
            catch (InvalidOperationException ex) { observed ??= ex; }
        }

        await editing;
        Console.WriteLine(observed is null
            ? "WP-177 control: the raw lookup did not lose the race on this machine."
            : $"WP-177 control: the raw lookup threw - {observed.Message}");
    }

    private static Task AddTimelinesAsync(ProjectSession session) => Task.Run(() =>
    {
        for (var i = 0; i < EditsDuringRender; i++)
            TimelineCommands.AddTimeline(session, $"New Timeline {i}", null, out _);
    });

    private sealed class CountingDispatcher : IProjectSessionDispatcher
    {
        private int _invocations;

        public bool HasThreadAccess => false;
        public int InvocationCount => Volatile.Read(ref _invocations);

        public T Invoke<T>(Func<T> operation)
        {
            Interlocked.Increment(ref _invocations);
            return operation();
        }
    }
}
