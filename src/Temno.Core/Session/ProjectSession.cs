using Temno.Core.Generation;
using Temno.Core.IO;
using Temno.Core.Models;

namespace Temno.Core.Session;

/// <summary>In-memory open project for UI/MCP (WP-05 will add full undo).</summary>
public sealed class ProjectSession
{
    private byte[]? _rawManifestBytes;
    private int _revision;
    
    // Undo/Redo is implemented via deep-clone serialization snapshots of the ProjectFile and MediaManifest.
    // This is simple, robust, and avoids complex command objects or manual reverse mutations.
    private readonly List<SessionSnapshot> _undoStack = new();
    private readonly List<SessionSnapshot> _redoStack = new();
    private int _maxStackDepth = 100;

    /// <summary>
    /// Thread safety warning: ProjectSession is single-owner/single-threaded and not designed for concurrent multi-thread edits.
    /// </summary>
    public string? PackagePath { get; private set; }
    public ProjectFile ProjectFile { get; private set; }
    public MediaManifest Manifest { get; private set; }
    public bool ManifestUnreadable { get; private set; }
    public bool IsDirty { get; private set; }

    private byte[]? _cleanProjectBytes;
    private byte[]? _cleanManifestBytes;
    private bool _cleanManifestUnreadable;
    private byte[]? _cleanRawManifestBytes;

    public int MaxStackDepth
    {
        get => _maxStackDepth;
        set
        {
            _maxStackDepth = value;
            PruneStacks();
        }
    }

    public bool CanUndo() => _undoStack.Count > 0;
    public bool CanRedo() => _redoStack.Count > 0;

    public MediaResolver Resolver { get; }

    /// <summary>
    /// Which files outside the package this session may use (WP-399). Restricted for a session read
    /// from disk (<see cref="Open"/>) or from a saved template (<see cref="FromTemplate"/>); unrestricted
    /// for a session built in memory, whose entries code in this process put there.
    /// </summary>
    public ExternalMediaAccess ExternalMedia { get; }

    /// <summary>An in-memory session. Do not build one from a manifest read from disk: use <see cref="Open"/>.</summary>
    public ProjectSession(ProjectFile projectFile, MediaManifest? manifest = null, string? packagePath = null)
        : this(projectFile, manifest, packagePath, restrictExternalMedia: false)
    {
    }

    private ProjectSession(ProjectFile projectFile, MediaManifest? manifest, string? packagePath, bool restrictExternalMedia)
    {
        ProjectFile = projectFile;
        Manifest = manifest ?? new MediaManifest();
        PackagePath = packagePath;
        ExternalMedia = restrictExternalMedia
            ? ExternalMediaAccess.Restricted(() => PackagePath, ExternalMediaGrants.For(packagePath))
            : ExternalMediaAccess.Unrestricted(() => PackagePath);
        Resolver = new MediaResolver(() => Manifest, () => PackagePath, ExternalMedia);
        // A transfer check that finds an allowed file leading elsewhere lists it for review: redraw.
        ExternalMedia.StatesChanged += (_, _) => RaiseChanged();
    }

    /// <summary>
    /// Opens a package. Its external media resolves only where the user has allowed that path for this
    /// package (<see cref="ExternalMediaGrants"/>): the package's own <c>media.json</c> cannot vouch for it.
    /// </summary>
    public static ProjectSession Open(string packageDirectory)
    {
        var contents = ProjectPackage.Read(packageDirectory);
        var session = new ProjectSession(
            contents.ProjectFile,
            contents.Manifest ?? new MediaManifest(),
            packageDirectory,
            restrictExternalMedia: true);
        session.ManifestUnreadable = contents.ManifestUnreadable;
        session._rawManifestBytes = contents.RawManifestBytes;
        session.CaptureCleanSnapshot();
        return session;
    }

    /// <summary>
    /// A new project from a saved template. Templates are files under the user's data folder that may
    /// have come from anywhere, so their external media is restricted like an opened package's, with no grants.
    /// </summary>
    public static ProjectSession FromTemplate(ProjectFile projectFile, MediaManifest? manifest)
    {
        var session = new ProjectSession(projectFile, manifest, packagePath: null, restrictExternalMedia: true);
        session.CaptureCleanSnapshot();
        return session;
    }

    public static ProjectSession CreateNew(
        string name = "Timeline 1",
        int fps = 30,
        int width = 1920,
        int height = 1080)
    {
        var timeline = new Timeline
        {
            Name = name,
            Fps = fps,
            Width = width,
            Height = height,
            Tracks =
            [
                new Track { Type = ClipType.video },
                new Track { Type = ClipType.video },
                new Track { Type = ClipType.audio },
                new Track { Type = ClipType.audio },
            ],
        };
        var file = new ProjectFile
        {
            Timelines = [timeline],
            ActiveTimelineId = timeline.Id,
            OpenTimelineIds = [timeline.Id],
        };
        var session = new ProjectSession(file);
        session.CaptureCleanSnapshot();
        return session;
    }

    public Timeline ActiveTimeline
    {
        get
        {
            var id = ProjectFile.ActiveTimelineId;
            return ProjectFile.Timelines.FirstOrDefault(x => x.Id == id)
                ?? ProjectFile.Timelines.FirstOrDefault()
                ?? throw new InvalidOperationException("No timelines");
        }
    }

    /// <summary>
    /// Monotonic counter bumped by every content mutation, whatever the caller. A consumer records
    /// the value it last rendered and compares: equal means nothing has changed since, so a queued
    /// refresh can be dropped. WP-153.
    /// </summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>
    /// Raised after every content mutation — local edit, in-app chat, or an external MCP client over
    /// loopback. This is the single mutation signal, so no caller needs a list of which tool names
    /// mutate. WP-153.
    /// </summary>
    /// <remarks>
    /// <para><b>Handlers must not block.</b> The event is raised inline on whichever thread performed
    /// the mutation, and several callers hold a lock while they do it: every generation landing runs
    /// under <c>ToolExecutor._sessionLock</c>, and <c>import_media</c> holds that lock on a
    /// thread-pool thread without the <see cref="ProjectHub"/> gate. A handler that waited on the UI
    /// thread from there would deadlock against a UI thread waiting for the same lock. Post the work
    /// and return — <c>EditorPage</c> uses <c>DispatcherQueue.TryEnqueue</c>.</para>
    /// <para>This is an instance event on purpose. A generation started before the project was closed
    /// lands on the session it captured, which by then may not be
    /// <see cref="ProjectHub.ActiveSession"/>; subscribing per session means a stale landing cannot
    /// refresh a window bound to a different project.</para>
    /// </remarks>
    public event EventHandler? Changed;

    /// <summary>
    /// The editor's Allow (WP-399): records the paths the user approved and raises <see cref="Changed"/>
    /// so every view resolves them again. Not an edit: the project files do not change and the project
    /// does not become dirty. Returns how many paths were allowed and saved.
    /// </summary>
    public int AllowExternalMedia(IEnumerable<string?> absolutePaths)
    {
        var saved = ExternalMedia.Allow(absolutePaths);
        RaiseChanged();
        return saved;
    }

    public void MarkDirty()
    {
        IsDirty = true;
        RaiseChanged();
    }

    /// <summary>
    /// Marks the project dirty <b>without</b> raising <see cref="Changed"/>, for a gesture that
    /// mutates continuously and is only meaningful once it commits — see <see cref="ClipLiveEdit"/>.
    /// </summary>
    /// <remarks>
    /// Every subscriber's response to the signal is to redraw, and a redraw mid-gesture is worse than
    /// no redraw: <c>EditorPage.RefreshAfterEdit</c> calls <c>Inspector.ShowSelection</c>, which ends
    /// the user's live edit under their pointer. A caller that uses this owes the session a
    /// <see cref="MarkDirty"/> when the gesture commits, or a <see cref="RollbackLastRecord"/> when it
    /// does not — both of which signal.
    /// </remarks>
    public void MarkDirtyWithinGesture() => IsDirty = true;

    private void RaiseChanged()
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Deliberately does not raise <see cref="Changed"/>: it changes <see cref="IsDirty"/>, not
    /// content. Every path that saves has already mutated and signalled, and a coalesced refresh
    /// reads the post-save flag anyway. Raising here would make the 60-second autosave trigger a
    /// full workspace refresh — thumbnails and timeline canvas — once a minute for nothing.
    /// </summary>
    public void Save(string? packageDirectory = null)
    {
        var path = packageDirectory ?? PackagePath
            ?? throw new InvalidOperationException("No package path");
        ProjectPackage.Write(
            path,
            ProjectFile,
            ManifestUnreadable ? null : Manifest,
            preserveUnreadableManifest: ManifestUnreadable,
            rawManifestBytes: _rawManifestBytes,
            sourcePackageDirectory: PackagePath);
        // WP-399: a save to a new place keeps the files the user allowed, and only those.
        if (!ExternalMediaGrants.PathComparer.Equals(
                ExternalMediaGrants.PackageKey(path) ?? path,
                ExternalMediaGrants.PackageKey(PackagePath) ?? ""))
            ExternalMedia.PersistTo(path);
        PackagePath = path;
        IsDirty = false;
        CaptureCleanSnapshot();
    }

    public void RecordBeforeEdit()
    {
        _redoStack.Clear();
        var snapshot = new SessionSnapshot(ProjectFile, Manifest, ManifestUnreadable, _rawManifestBytes);
        _undoStack.Add(snapshot);
        PruneStacks();
    }

    public void RollbackLastRecord()
    {
        if (_undoStack.Count > 0)
        {
            var live = Manifest;
            var liveViews = ProjectFile.ViewStates;
            var lastIdx = _undoStack.Count - 1;
            var snapshot = _undoStack[lastIdx];
            _undoStack.RemoveAt(lastIdx);

            ProjectFile = ProjectFile.Decode(snapshot.ProjectFileBytes);
            Manifest = MediaManifest.Decode(snapshot.ManifestBytes);
            ManifestUnreadable = snapshot.ManifestUnreadable;
            _rawManifestBytes = snapshot.RawManifestBytes;
            GenerationEntryGuard.Retain(live, Manifest);
            KeepLiveViewStates(liveViews);
            RaiseChanged();
        }
    }

    public void Undo()
    {
        if (!CanUndo()) return;

        var live = Manifest;
        var liveViews = ProjectFile.ViewStates;
        var currentSnapshot = new SessionSnapshot(ProjectFile, Manifest, ManifestUnreadable, _rawManifestBytes);
        _redoStack.Add(currentSnapshot);

        var lastIdx = _undoStack.Count - 1;
        var snapshot = _undoStack[lastIdx];
        _undoStack.RemoveAt(lastIdx);

        ProjectFile = ProjectFile.Decode(snapshot.ProjectFileBytes);
        Manifest = MediaManifest.Decode(snapshot.ManifestBytes);
        ManifestUnreadable = snapshot.ManifestUnreadable;
        _rawManifestBytes = snapshot.RawManifestBytes;
        GenerationEntryGuard.Retain(live, Manifest);

        PruneStacks();
        RefreshDirtyFromCleanSnapshot();
        KeepLiveViewStates(liveViews);
        RaiseChanged();
    }

    public void Redo()
    {
        if (!CanRedo()) return;

        var live = Manifest;
        var liveViews = ProjectFile.ViewStates;
        var currentSnapshot = new SessionSnapshot(ProjectFile, Manifest, ManifestUnreadable, _rawManifestBytes);
        _undoStack.Add(currentSnapshot);

        var lastIdx = _redoStack.Count - 1;
        var snapshot = _redoStack[lastIdx];
        _redoStack.RemoveAt(lastIdx);

        ProjectFile = ProjectFile.Decode(snapshot.ProjectFileBytes);
        Manifest = MediaManifest.Decode(snapshot.ManifestBytes);
        ManifestUnreadable = snapshot.ManifestUnreadable;
        _rawManifestBytes = snapshot.RawManifestBytes;
        GenerationEntryGuard.Retain(live, Manifest);

        PruneStacks();
        RefreshDirtyFromCleanSnapshot();
        KeepLiveViewStates(liveViews);
        RaiseChanged();
    }

    /// <summary>
    /// WP-435: undo, redo and rollback restore the document, not the view. The timeline keeps showing the live
    /// playhead, zoom and scroll, so each timeline that survives the restore keeps its live view state (the Mac's undo
    /// never moves the playhead), and one the restore brings back keeps the state it was saved with. Called after the
    /// dirty check, so the unsaved-changes flag is decided by the restored document, as before.
    /// </summary>
    private void KeepLiveViewStates(Dictionary<string, TimelineViewState>? live)
    {
        if (live is null || live.Count == 0) return;
        var ids = ProjectFile.Timelines.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (id, state) in live)
        {
            if (!ids.Contains(id)) continue;
            ProjectFile.ViewStates ??= new Dictionary<string, TimelineViewState>();
            ProjectFile.ViewStates[id] = state;
        }
    }

    private void CaptureCleanSnapshot()
    {
        _cleanProjectBytes = ProjectFile.Encode();
        _cleanManifestBytes = Manifest.Encode();
        _cleanManifestUnreadable = ManifestUnreadable;
        _cleanRawManifestBytes = _rawManifestBytes;
    }

    private void RefreshDirtyFromCleanSnapshot()
        => IsDirty = !MatchesCleanSnapshot();

    private bool MatchesCleanSnapshot()
    {
        if (_cleanProjectBytes is null)
            return false;
        if (ManifestUnreadable != _cleanManifestUnreadable)
            return false;
        if (!ProjectFile.Encode().AsSpan().SequenceEqual(_cleanProjectBytes))
            return false;
        if (ManifestUnreadable)
        {
            var current = _rawManifestBytes ?? [];
            var clean = _cleanRawManifestBytes ?? [];
            return current.AsSpan().SequenceEqual(clean);
        }

        return Manifest.Encode().AsSpan().SequenceEqual(_cleanManifestBytes);
    }

    private void PruneStacks()
    {
        while (_undoStack.Count > _maxStackDepth)
        {
            _undoStack.RemoveAt(0);
        }
        while (_redoStack.Count > _maxStackDepth)
        {
            _redoStack.RemoveAt(0);
        }
    }

    private sealed class SessionSnapshot
    {
        public byte[] ProjectFileBytes { get; }
        public byte[] ManifestBytes { get; }
        public bool ManifestUnreadable { get; }
        public byte[]? RawManifestBytes { get; }

        public SessionSnapshot(ProjectFile projectFile, MediaManifest manifest, bool manifestUnreadable, byte[]? rawManifestBytes)
        {
            ProjectFileBytes = projectFile.Encode();
            ManifestBytes = manifest.Encode();
            ManifestUnreadable = manifestUnreadable;
            RawManifestBytes = rawManifestBytes;
        }
    }
}
