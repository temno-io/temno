using System.Reflection;
using System.Text.Json.Nodes;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-153. The editor used to learn about a mutation only when the caller happened to tell it, and
/// the loopback MCP path never did — an external client could add clips to the open project while
/// the timeline, media library, preview and Inspector all kept drawing the pre-edit session.
/// <para>
/// The fix is one signal on the session itself, so no caller needs a list of which tool names
/// mutate. These tests hold that signal to three properties: it fires for every mutation whatever
/// the call path, it does not fire for a read-only call, and a handler that only posts cannot
/// deadlock against the locks the mutating callers hold.
/// </para>
/// </summary>
[Collection("EnvRegistry")]
public sealed class SessionMutationSignalTests
{
    // ---- the signal itself ------------------------------------------------------------------

    [Fact]
    public void MarkDirty_RaisesChangedAndBumpsRevision()
    {
        var session = ProjectSession.CreateNew();
        var before = session.Revision;
        var raised = 0;
        session.Changed += (_, _) => raised++;

        session.MarkDirty();

        Assert.Equal(1, raised);
        Assert.NotEqual(before, session.Revision);
    }

    [Fact]
    public void Revision_IsMonotonicAcrossRepeatedMutations()
    {
        var session = ProjectSession.CreateNew();
        var seen = new List<int> { session.Revision };
        for (var i = 0; i < 5; i++)
        {
            session.MarkDirty();
            seen.Add(session.Revision);
        }

        Assert.Equal(seen.OrderBy(x => x).ToList(), seen);
        Assert.Equal(seen.Distinct().Count(), seen.Count);
    }

    [Fact]
    public void Undo_RaisesChanged()
    {
        var session = ProjectSession.CreateNew();
        session.RecordBeforeEdit();
        Assert.True(EditCommands.AddClip(session, 0, Clip()).Ok);

        var raised = 0;
        var before = session.Revision;
        session.Changed += (_, _) => raised++;

        session.Undo();

        // `undo` is an MCP tool and ProjectSession.Undo does not route through MarkDirty, so
        // without its own raise an external client could undo the user's work invisibly.
        Assert.Equal(1, raised);
        Assert.NotEqual(before, session.Revision);
        Assert.Empty(session.ActiveTimeline.Tracks[0].Clips);
    }

    [Fact]
    public void Redo_RaisesChanged()
    {
        var session = ProjectSession.CreateNew();
        session.RecordBeforeEdit();
        Assert.True(EditCommands.AddClip(session, 0, Clip()).Ok);
        session.Undo();

        var raised = 0;
        var before = session.Revision;
        session.Changed += (_, _) => raised++;

        session.Redo();

        Assert.Equal(1, raised);
        Assert.NotEqual(before, session.Revision);
        Assert.Single(session.ActiveTimeline.Tracks[0].Clips);
    }

    [Fact]
    public void RollbackLastRecord_RaisesChanged()
    {
        var session = ProjectSession.CreateNew();
        session.RecordBeforeEdit();
        Assert.True(EditCommands.AddClip(session, 0, Clip()).Ok);

        var raised = 0;
        session.Changed += (_, _) => raised++;

        session.RollbackLastRecord();

        Assert.Equal(1, raised);
        Assert.Empty(session.ActiveTimeline.Tracks[0].Clips);
    }

    [Fact]
    public void RollbackLastRecord_WithNothingRecorded_DoesNotRaise()
    {
        var session = ProjectSession.CreateNew();
        var raised = 0;
        session.Changed += (_, _) => raised++;

        session.RollbackLastRecord();

        Assert.Equal(0, raised);
    }

    [Fact]
    public void Undo_WithEmptyStack_DoesNotRaise()
    {
        var session = ProjectSession.CreateNew();
        var raised = 0;
        session.Changed += (_, _) => raised++;

        session.Undo();
        session.Redo();

        Assert.Equal(0, raised);
    }

    /// <summary>
    /// Save changes <see cref="ProjectSession.IsDirty"/>, not content, and every path that saves has
    /// already mutated and signalled. Raising here would make the editor's 60-second autosave run a
    /// full workspace refresh once a minute. Asserted so the decision is a fact, not a comment.
    /// </summary>
    [Fact]
    public void Save_DoesNotRaiseAndDoesNotBumpRevision()
    {
        var package = TempPackage();
        try
        {
            var session = ProjectSession.CreateNew();
            session.MarkDirty();
            var afterMutation = session.Revision;
            var raised = 0;
            session.Changed += (_, _) => raised++;

            session.Save(package);

            Assert.Equal(0, raised);
            Assert.Equal(afterMutation, session.Revision);
            Assert.False(session.IsDirty);
        }
        finally
        {
            if (Directory.Exists(package)) Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public void Unsubscribing_StopsDelivery()
    {
        var session = ProjectSession.CreateNew();
        var raised = 0;
        void Handler(object? sender, EventArgs e) => raised++;

        session.Changed += Handler;
        session.MarkDirty();
        session.Changed -= Handler;
        session.MarkDirty();

        Assert.Equal(1, raised);
    }

    /// <summary>
    /// The signal is an instance event so a project swap detaches cleanly. A generation started
    /// before the project was closed lands on the session it captured, which by then may not be the
    /// active one; a static signal would let that refresh a window bound to a different project.
    /// </summary>
    [Fact]
    public void ChangedIsPerSession_ASecondSessionDoesNotNotifyTheFirstsSubscriber()
    {
        var first = ProjectSession.CreateNew();
        var second = ProjectSession.CreateNew();
        var raised = 0;
        first.Changed += (_, _) => raised++;

        second.MarkDirty();

        Assert.Equal(0, raised);
    }

    // ---- a live gesture signals once, at the end ---------------------------------------------

    /// <summary>
    /// An Inspector scrub calls <c>Mutate</c> on every value change. Signalling each one would have
    /// the editor redraw mid-drag — and <c>RefreshAfterEdit</c> begins with
    /// <c>Inspector.ShowSelection</c>, which calls <c>EndLiveEdit(commit: true)</c>, ending the
    /// user's gesture under their pointer and opening a fresh undo entry for the next nudge.
    /// </summary>
    [Fact]
    public void LiveGesture_DoesNotSignalPerValueChange()
    {
        var session = ProjectSession.CreateNew();
        var clip = Clip();
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);
        var raised = 0;
        session.Changed += (_, _) => raised++;

        var live = new ClipLiveEdit();
        live.Begin(session);
        for (var i = 1; i <= 20; i++)
            live.Mutate(() => session.ActiveTimeline.Tracks[0].Clips[0].Opacity = i / 20.0);

        Assert.Equal(0, raised);
        // Dirty throughout, though: the project really has changed, it is just not worth redrawing.
        Assert.True(session.IsDirty);
    }

    [Fact]
    public void LiveGesture_SignalsOnceWhenItCommits()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, Clip()).Ok);
        var raised = 0;
        session.Changed += (_, _) => raised++;

        var live = new ClipLiveEdit();
        live.Begin(session);
        for (var i = 0; i < 20; i++)
            live.Mutate(() => session.ActiveTimeline.Tracks[0].Clips[0].Opacity = 0.5);
        live.End(keep: true);

        Assert.Equal(1, raised);
        Assert.Equal(0.5, session.ActiveTimeline.Tracks[0].Clips[0].Opacity);
    }

    [Fact]
    public void LiveGesture_ThatIsAbandoned_SignalsTheRollback()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, Clip()).Ok);
        var raised = 0;
        session.Changed += (_, _) => raised++;

        var live = new ClipLiveEdit();
        live.Begin(session);
        live.Mutate(() => session.ActiveTimeline.Tracks[0].Clips[0].Opacity = 0.25);
        live.End(keep: false);

        // Still exactly one: the content moved back, and the editor has to redraw that too.
        Assert.Equal(1, raised);
        Assert.Equal(1.0, session.ActiveTimeline.Tracks[0].Clips[0].Opacity);
    }

    // ---- every command family reaches it ----------------------------------------------------

    /// <summary>
    /// One case per <c>Temno.Core.Session</c> command family — 8 of the 13 source files that call
    /// <c>MarkDirty</c> — so the claim "every mutation raises it" is checked against the command
    /// surface rather than asserted. The other five are covered elsewhere or not at all, and the WP
    /// says which: <c>ToolExecutor</c> and <c>ToolExecutor.Presets</c> through
    /// <see cref="MutatingTool_BumpsRevision"/>, <c>InspectorControl</c> not at all because
    /// <c>Temno.App</c> is unreachable from this suite, and the two caption files not at all
    /// because each needs a transcript or a subtitle fixture to reach its mutation.
    /// </summary>
    [Theory]
    [InlineData("EditCommands")]
    [InlineData("TimelineCommands")]
    [InlineData("EffectStackCommands")]
    [InlineData("KeyframeCommands")]
    [InlineData("MediaLibraryCommands")]
    [InlineData("TextClipCommands")]
    [InlineData("MulticamCommands")]
    [InlineData("ClipLiveEdit")]
    public void EveryCommandFamily_RaisesChanged(string family)
    {
        var session = ProjectSession.CreateNew();
        var clip = Clip();
        Assert.True(EditCommands.AddClip(session, 0, clip).Ok);

        var raised = 0;
        session.Changed += (_, _) => raised++;

        switch (family)
        {
            case "EditCommands":
                Assert.True(EditCommands.SplitClip(session, clip.Id, 40).Ok);
                break;
            case "TimelineCommands":
                Assert.True(TimelineCommands.AddTimeline(session, "Second", null, out _).Ok);
                break;
            case "EffectStackCommands":
                Assert.True(EffectStackCommands.AddEffect(session, clip.Id, "color.exposure").Ok);
                break;
            case "KeyframeCommands":
                Assert.True(KeyframeCommands.Stamp(session, clip.Id, "opacity", 0).Ok);
                break;
            case "MediaLibraryCommands":
                Assert.True(MediaLibraryCommands.CreateFolder(session, "Folder", null, out _).Ok);
                break;
            case "MulticamCommands":
                var specs = new List<MulticamCommands.MemberSpec>
                {
                    new() { MediaRef = "m1", AngleLabel = "A" },
                    new() { MediaRef = "m2", AngleLabel = "B" },
                };
                Assert.True(MulticamCommands.CreateGroup(
                    session,
                    specs,
                    // Confidence > 0 (or Locked) is what makes a member Usable, and only usable
                    // angles are placed - an unsynced group is rejected before it mutates anything.
                    new Dictionary<string, MulticamSyncMap>
                    {
                        ["m1"] = new MulticamSyncMap { Confidence = 1 },
                        ["m2"] = new MulticamSyncMap { Confidence = 1 },
                    },
                    masterMediaRef: "m1",
                    sourceDurations: new Dictionary<string, double> { ["m1"] = 4, ["m2"] = 4 },
                    mediaTypes: new Dictionary<string, ClipType> { ["m1"] = ClipType.video, ["m2"] = ClipType.video },
                    name: "Group",
                    startFrame: 0,
                    out _).Ok);
                break;
            case "TextClipCommands":
                Assert.True(TextClipCommands.AddTexts(
                    session,
                    [new TextClipSpec(null, 0, 30, "hello", new TextStyle(), null, null)],
                    out _).Ok);
                break;
            case "ClipLiveEdit":
                // Signals at End, not at Mutate - see LiveGesture_* above.
                var live = new ClipLiveEdit();
                live.Begin(session);
                live.Mutate(() => session.ActiveTimeline.Tracks[0].Clips[0].Opacity = 0.5);
                Assert.Equal(0, raised);
                live.End();
                break;
            default:
                throw new InvalidOperationException(family);
        }

        Assert.True(raised > 0, $"{family} mutated the session without raising ProjectSession.Changed.");
    }

    /// <summary>
    /// Forces a decision when someone adds a method to <see cref="ProjectSession"/>: either it is a
    /// mutator with a signal test above, or it is listed here as not changing content. A new
    /// mutation path that signals nothing is WP-153's defect in a new place.
    /// </summary>
    [Fact]
    public void EveryPublicMethodOnProjectSession_IsClassified()
    {
        var raisesChanged = new HashSet<string>(StringComparer.Ordinal)
        {
            "MarkDirty", "Undo", "Redo", "RollbackLastRecord",
            // WP-399: changes what resolves, not the project files; see
            // ExternalMediaAccessTests.AllowExternalMedia_SignalsARedraw_WithoutDirtyingTheProject.
            "AllowExternalMedia",
        };
        var doesNotChangeContent = new HashSet<string>(StringComparer.Ordinal)
        {
            "Save",              // flips IsDirty only; see Save_DoesNotRaiseAndDoesNotBumpRevision
            "RecordBeforeEdit",  // pushes an undo snapshot; content is unchanged
            "CanUndo", "CanRedo",
            // Deliberately silent, and only for a gesture that signals when it ends:
            // see LiveGesture_DoesNotSignalPerValueChange.
            "MarkDirtyWithinGesture",
        };

        var unclassified = typeof(ProjectSession)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Distinct(StringComparer.Ordinal)
            .Where(n => !raisesChanged.Contains(n) && !doesNotChangeContent.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unclassified.Count == 0,
            "New public methods on ProjectSession must be classified in this test: either they change "
            + "content and raise Changed (add a test), or they do not (say so here). Unclassified: "
            + string.Join(", ", unclassified));
    }

    // ---- through the MCP executor, which is the path that was silent -------------------------

    [Theory]
    [InlineData("add_clips", """{"entries":[{"mediaRef":"m1","startFrame":0,"trackIndex":0}]}""")]
    [InlineData("add_marker", """{"frame":10,"name":"M"}""")]
    [InlineData("create_timeline", """{"name":"Second"}""")]
    [InlineData("undo", "{}")]
    public void MutatingTool_BumpsRevision(string tool, string argsJson)
    {
        var previous = ProjectHub.ActiveSession;
        try
        {
            var session = ProjectSession.CreateNew();
            // `undo` needs something on the stack, and add_clips needs somewhere to land.
            session.RecordBeforeEdit();
            Assert.True(EditCommands.AddClip(session, 0, Clip()).Ok);
            ProjectHub.ActiveSession = session;

            var before = session.Revision;
            var raised = 0;
            session.Changed += (_, _) => raised++;

            var result = ToolExecutor.Execute(tool, JsonNode.Parse(argsJson) as JsonObject);

            Assert.False(result.IsError, $"{tool} failed: {result.Content.FirstOrDefault()?.Text}");
            Assert.True(raised > 0, $"{tool} mutated the session but raised nothing.");
            Assert.NotEqual(before, session.Revision);
        }
        finally
        {
            ProjectHub.ActiveSession = previous;
        }
    }

    [Theory]
    [InlineData("get_timeline")]
    [InlineData("get_media")]
    [InlineData("list_markers")]
    [InlineData("list_transitions")]
    public void ReadOnlyTool_RaisesNothing(string tool)
    {
        var previous = ProjectHub.ActiveSession;
        try
        {
            var session = ProjectSession.CreateNew();
            Assert.True(EditCommands.AddClip(session, 0, Clip()).Ok);
            ProjectHub.ActiveSession = session;

            var before = session.Revision;
            var raised = 0;
            session.Changed += (_, _) => raised++;

            var result = ToolExecutor.Execute(tool, []);

            Assert.False(result.IsError, $"{tool} failed: {result.Content.FirstOrDefault()?.Text}");
            Assert.Equal(0, raised);
            Assert.Equal(before, session.Revision);
        }
        finally
        {
            ProjectHub.ActiveSession = previous;
        }
    }

    // ---- the threading contract -------------------------------------------------------------

    /// <summary>
    /// Reproduces the shape that makes a blocking handler fatal. <c>import_media</c> is off-dispatcher
    /// and takes <c>ToolExecutor._sessionLock</c> on a thread-pool thread without the ProjectHub gate
    /// (<c>ToolExecutor.cs:2142</c> → <c>:6244</c>), while every generation landing takes the same
    /// lock from inside <c>RunSerialized</c>, i.e. on the UI thread. If a Changed handler waited for
    /// the dispatcher from the pool side, the pool thread would hold the lock while the UI thread
    /// waited for it. EditorPage's handler only posts, which is what this asserts is sufficient.
    /// </summary>
    [Fact]
    public async Task ChangedHandlerThatOnlyPosts_DoesNotDeadlockWithALockHeldOffDispatcher()
    {
        using var dispatcher = new SingleThreadDispatcher();
        var previousSession = ProjectHub.ActiveSession;
        ProjectHub.ConfigureDispatcher(dispatcher);
        try
        {
            var session = ProjectSession.CreateNew();
            ProjectHub.ActiveSession = session;
            var sessionLock = new object();
            var posted = 0;

            // The EditorPage idiom: post and return, never wait for the other thread.
            session.Changed += (_, _) => dispatcher.Post(() => Interlocked.Increment(ref posted));

            var work = new List<Task>();
            for (var i = 0; i < 25; i++)
            {
                // off-dispatcher caller: pool thread holds the lock while mutating
                work.Add(Task.Run(() =>
                {
                    lock (sessionLock) session.MarkDirty();
                }));
                // dispatcher-marshalled caller: gate then lock, on the dispatcher thread
                work.Add(Task.Run(() => ProjectHub.RunSerialized(() =>
                {
                    lock (sessionLock) session.MarkDirty();
                })));
            }

            var all = Task.WhenAll(work);
            var finished = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(
                ReferenceEquals(finished, all),
                "A session mutation blocked. A Changed handler must post and return, never wait for "
                + "the dispatcher while a lock is held.");
            await all;
            await dispatcher.DrainAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(50, posted);
        }
        finally
        {
            ProjectHub.ActiveSession = previousSession;
            ProjectHub.ConfigureDispatcher(null);
        }
    }

    /// <summary>
    /// The product side of the same rule, read from source the way
    /// <c>McpDispatcherDisciplineTests</c> reads <c>ToolExecutor</c>: the property being guarded is a
    /// property of the code shape, so it must fail when the code changes, not when a user hits it.
    /// </summary>
    [Fact]
    public void EditorPageMutationHandler_OnlyPosts()
    {
        var source = File.ReadAllText(
            Path.Combine(SourceRoot("Temno.App"), "Views", "Editor", "EditorPage.xaml.cs"));

        // Both halves: Session_Changed is the delegate ProjectSession.Changed actually invokes, and
        // scanning only the method it forwards to would let a blocking call land in the one that runs
        // on the HTTP listener thread.
        var post = MethodBody(source, "private void RequestSessionRefresh()");
        var handler = MethodBody(source, "private void Session_Changed(object? sender, EventArgs e)");

        Assert.Contains("TryEnqueue", post);
        Assert.Contains("RequestSessionRefresh", handler);
        foreach (var body in new[] { post, handler })
        {
            foreach (var blocking in new[]
                { "GetAwaiter().GetResult()", ".Wait()", ".Result", "Join()", "lock (" })
            {
                Assert.False(
                    body.Contains(blocking, StringComparison.Ordinal),
                    $"EditorPage's mutation handler must not block ({blocking}). It runs on the thread "
                    + "that mutated the session, sometimes holding ToolExecutor._sessionLock.");
            }
        }
    }

    // ---- helpers ----------------------------------------------------------------------------

    private static Clip Clip() => new()
    {
        Id = "C1",
        MediaRef = "m1",
        MediaType = ClipType.video,
        StartFrame = 0,
        DurationFrames = 100,
    };

    private static string TempPackage()
        => Path.Combine(Path.GetTempPath(), "temno-wp153-" + Guid.NewGuid().ToString("N") + ".palmier");

    private static string SourceRoot(string project)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", project)))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", project);
    }

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signature}' not found — the test is pointed at the wrong code.");
        var open = source.IndexOf('{', start);
        Assert.True(open > 0);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }

    private sealed class SingleThreadDispatcher : IProjectSessionDispatcher, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = [];
        private readonly Thread _thread;

        public SingleThreadDispatcher()
        {
            _thread = new Thread(() =>
            {
                foreach (var work in _queue.GetConsumingEnumerable())
                    work();
            })
            { IsBackground = true };
            _thread.Start();
        }

        public bool HasThreadAccess => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

        /// <summary>Non-blocking, like <c>DispatcherQueue.TryEnqueue</c>.</summary>
        public void Post(Action work) => _queue.Add(work);

        public T Invoke<T>(Func<T> operation)
        {
            if (HasThreadAccess) return operation();
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try { completion.TrySetResult(operation()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            return completion.Task.GetAwaiter().GetResult();
        }

        public async Task DrainAsync(TimeSpan timeout)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() => done.TrySetResult(true));
            await done.Task.WaitAsync(timeout).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(5));
            _queue.Dispose();
        }
    }
}
