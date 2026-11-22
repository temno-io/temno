using Temno.Core.Export;
using Temno.Core.IO;
using Temno.Core.Models;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-154. An export whose media had partly gone offline rendered the survivors, dropped the rest
/// and reported success. These tests pin the judgement the export now makes first: which references
/// the timeline actually reaches, and what is genuinely known about each of them.
/// <para>
/// The disk is injected rather than built, so every case is stated rather than arranged. The network
/// rule is checked by asserting the injected probe was never called AND that the resolver was never
/// consulted — the resolver stats path segments of its own, so a probe assertion alone would miss the
/// filesystem access that actually blocks on a dead share.
/// </para>
/// </summary>
public sealed class ExportMediaPreflightTests
{
    // ---- the ordinary cases ------------------------------------------------------------------

    [Fact]
    public void EverythingPresent_HasNoProblems()
    {
        var (timeline, manifest, resolver) = Project(("a", "red"), ("b", "green"));

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, resolver, fileExists: _ => true);

        Assert.False(report.HasProblems);
        Assert.Null(report.Sentence);
        Assert.All(report.Items, i => Assert.Equal(ExportMediaState.Available, i.State));
    }

    [Fact]
    public void OneFileGone_IsMissingAndNamed()
    {
        var (timeline, manifest, resolver) = Project(("a", "red"), ("b", "green"), ("c", "blue"));

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, resolver, fileExists: p => !p.EndsWith("b.mp4", StringComparison.Ordinal));

        var problem = Assert.Single(report.Problems);
        Assert.Equal(ExportMediaState.Missing, problem.State);
        Assert.Equal("green", problem.Name);
        Assert.Contains("green", report.Sentence);
        Assert.Contains("missing from disk", report.Sentence);
    }

    [Fact]
    public void ReferenceWithNoManifestEntry_IsUnresolvedNotMissing()
    {
        var (timeline, manifest, resolver) = Project(("a", "red"));
        timeline.Tracks[0].Clips.Add(Clip("ghost"));

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, resolver, fileExists: _ => true);

        var problem = Assert.Single(report.Problems);
        Assert.Equal("ghost", problem.Reference);
        Assert.Equal(ExportMediaState.Unresolved, problem.State);
    }

    [Fact]
    public void SameReferenceOnTwoClips_IsJudgedOnce()
    {
        var (timeline, manifest, resolver) = Project(("a", "red"));
        timeline.Tracks[0].Clips.Add(Clip("a"));

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, resolver, fileExists: _ => false);

        Assert.Single(report.Items);
    }

    /// <summary>Scope item 3: a manifest entry nobody references is not an export problem.</summary>
    [Fact]
    public void ManifestEntryTheTimelineDoesNotUse_IsNotJudged()
    {
        var (timeline, manifest, resolver) = Project(("a", "red"));
        manifest.Entries.Add(Entry("unused", "unused", "unused.mp4"));

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, resolver, fileExists: p => !p.Contains("unused", StringComparison.Ordinal));

        Assert.False(report.HasProblems);
        Assert.DoesNotContain(report.Items, i => i.Reference == "unused");
    }

    // ---- the WP-148 round-2 rule -------------------------------------------------------------

    /// <summary>
    /// A healthy <c>\\nas\…</c> file reported as missing is a lying figure, and probing it blocks for
    /// seconds on a disconnected share. Asserting the probe was never called is the only way to show
    /// the file was not touched — a state assertion alone would pass on a probe that returned true.
    /// </summary>
    [Theory]
    [InlineData(@"\\nas\media\shot.mp4")]
    [InlineData("//nas/media/shot.mp4")]
    public void NetworkMedia_IsNeverProbedAndNeverAProblem(string uncPath)
    {
        var manifest = new MediaManifest { Entries = { Entry("n", "shot", uncPath, external: true) } };
        var timeline = TimelineWith(Clip("n"));
        var resolverReads = 0;
        var resolver = new MediaResolver(
            () => { resolverReads++; return manifest; },
            () => @"C:\pkg.palmier");
        var probed = new List<string>();

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, resolver, packageRoot: @"C:\pkg.palmier",
            fileExists: p => { probed.Add(p); return false; });

        Assert.Empty(probed);
        Assert.Equal(0, resolverReads);
        Assert.False(report.HasProblems);
        Assert.Equal(1, report.NetworkUncheckedCount);
        Assert.Equal(ExportMediaState.NetworkUnchecked, Assert.Single(report.Items).State);
    }

    /// <summary>
    /// The hole the first version of this had. A project-relative entry resolves through
    /// <c>MediaResolver.JoinProject</c> → <c>ContainsReparsePoint</c>, which stats every segment of the
    /// path. Asking the resolver first would block on the dead share on its way to deciding not to
    /// probe it — so the decision is taken from the declared source and the package root instead.
    /// </summary>
    [Fact]
    public void ProjectRelativeMediaUnderANetworkPackageRoot_IsDecidedWithoutResolving()
    {
        var manifest = new MediaManifest { Entries = { Entry("a", "shot", "media/a.mp4") } };
        var timeline = TimelineWith(Clip("a"));
        var root = @"\\nas\edit\show.palmier";
        // Counts the project-URL reads, because that is what MediaResolver.ResolvePath(entry) calls on
        // its way into JoinProject -> ContainsReparsePoint, which is the code that would stat the
        // share. Counting manifest reads instead would miss it entirely: the entry overload never
        // touches the manifest, and an earlier version of this test passed against a build with the
        // guard removed for exactly that reason.
        var rootReads = 0;
        var resolver = new MediaResolver(() => manifest, () => { rootReads++; return root; });
        var probed = new List<string>();

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, resolver, packageRoot: root,
            fileExists: p => { probed.Add(p); return false; });

        Assert.Empty(probed);
        Assert.Equal(0, rootReads);
        Assert.Equal(ExportMediaState.NetworkUnchecked, Assert.Single(report.Items).State);
        Assert.False(report.HasProblems);
    }

    /// <summary>
    /// The backstop for a caller that resolves a network path without having declared the root — the
    /// <c>packageRoot</c> argument is optional, and the resolver has its own idea of where the package
    /// is. Deciding up front is the fast path; this is the one that must still be right.
    /// </summary>
    [Fact]
    public void NetworkPathThatOnlyAppearsAfterResolving_IsStillNotProbed()
    {
        var manifest = new MediaManifest { Entries = { Entry("a", "shot", "media/a.mp4") } };
        var timeline = TimelineWith(Clip("a"));
        var resolver = new MediaResolver(() => manifest, () => @"\\nas\edit\show.palmier");
        var probed = new List<string>();

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, resolver, packageRoot: null,
            fileExists: p => { probed.Add(p); return false; });

        Assert.Empty(probed);
        Assert.Equal(ExportMediaState.NetworkUnchecked, Assert.Single(report.Items).State);
    }

    /// <summary>
    /// An unsaved project cannot resolve a project-relative entry, which says nothing about the
    /// media. Reporting that is the same category error as calling an unprobed network file missing.
    /// </summary>
    [Fact]
    public void UnsavedProject_DoesNotReportProjectRelativeEntriesAsProblems()
    {
        var manifest = new MediaManifest { Entries = { Entry("a", "red", "media/a.mp4") } };
        var timeline = TimelineWith(Clip("a"));
        var resolver = new MediaResolver(() => manifest, () => null);

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, resolver, projectIsSaved: false, fileExists: _ => false);

        Assert.False(report.HasProblems);
    }

    [Fact]
    public void SavedProject_DoesReportAnUnresolvableEntry()
    {
        var manifest = new MediaManifest { Entries = { Entry("a", "red", "../escape.mp4") } };
        var timeline = TimelineWith(Clip("a"));
        var resolver = new MediaResolver(() => manifest, () => @"C:\pkg.palmier");

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, resolver, projectIsSaved: true, fileExists: _ => true);

        Assert.Equal(ExportMediaState.Unresolved, Assert.Single(report.Problems).State);
    }

    // ---- generation states, which are not the same thing --------------------------------------

    [Theory]
    [InlineData("preparing")]
    [InlineData("generating")]
    [InlineData("downloading")]
    public void InFlightGeneration_IsPendingNotMissing(string status)
    {
        var manifest = new MediaManifest { Entries = { Entry("g", "shot", "media/g.mp4", status: status) } };
        var timeline = TimelineWith(Clip("g"));
        var resolver = new MediaResolver(() => manifest, () => @"C:\pkg.palmier");

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, resolver, fileExists: _ => false);

        var item = Assert.Single(report.Items);
        Assert.Equal(ExportMediaState.GenerationPending, item.State);
        Assert.Contains("still generating", report.Sentence);
    }

    /// <summary>
    /// Sol's note: the Inspector folds <c>failed:</c> into its pending count, which is right for that
    /// panel's wording and wrong here. A failed generation is not pending — it is never arriving.
    /// </summary>
    [Fact]
    public void FailedGeneration_IsNotFoldedIntoPending()
    {
        var manifest = new MediaManifest
        {
            Entries =
            {
                Entry("g1", "in flight", "media/g1.mp4", status: "generating"),
                Entry("g2", "gave up", "media/g2.mp4", status: "failed: model refused the prompt"),
            },
        };
        var timeline = TimelineWith(Clip("g1"), Clip("g2"));
        var resolver = new MediaResolver(() => manifest, () => @"C:\pkg.palmier");

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, resolver, fileExists: _ => false);

        Assert.Equal(
            ExportMediaState.GenerationPending,
            report.Items.Single(i => i.Reference == "g1").State);
        Assert.Equal(
            ExportMediaState.GenerationFailed,
            report.Items.Single(i => i.Reference == "g2").State);
        Assert.Contains("generation failed", report.Sentence);
    }

    // ---- nested sequences --------------------------------------------------------------------

    /// <summary>The acceptance criterion: media reachable only through a nested sequence is judged.</summary>
    [Fact]
    public void MediaOnlyInsideANestedSequence_IsJudged()
    {
        var manifest = new MediaManifest { Entries = { Entry("deep", "buried", "media/deep.mp4") } };
        var inner = TimelineWith(Clip("deep"));
        var outer = TimelineWith(SequenceClip(inner.Id));

        var report = ExportMediaPreflight.Inspect(
            outer, manifest, new MediaResolver(() => manifest, () => @"C:\pkg.palmier"),
            timelineLookup: id => id == inner.Id ? inner : null,
            fileExists: _ => false);

        var problem = Assert.Single(report.Problems);
        Assert.Equal("deep", problem.Reference);
        Assert.Equal(ExportMediaState.Missing, problem.State);
    }

    [Theory]
    [InlineData(true)]   // MediaType == sequence, which is what the renderer recurses on
    [InlineData(false)]  // SourceClipType == sequence, which is what TimelineExtensions filters on
    public void EitherSequenceMarker_IsFollowed(bool byMediaType)
    {
        var manifest = new MediaManifest { Entries = { Entry("deep", "buried", "media/deep.mp4") } };
        var inner = TimelineWith(Clip("deep"));
        var clip = SequenceClip(inner.Id);
        if (byMediaType) clip.SourceClipType = ClipType.video;
        else clip.MediaType = ClipType.video;
        var outer = TimelineWith(clip);

        var report = ExportMediaPreflight.Inspect(
            outer, manifest, new MediaResolver(() => manifest, () => @"C:\pkg.palmier"),
            timelineLookup: id => id == inner.Id ? inner : null,
            fileExists: _ => false);

        Assert.Equal("deep", Assert.Single(report.Problems).Reference);
    }

    [Fact]
    public void NestedSequenceThatDoesNotResolve_IsItsOwnProblem()
    {
        var manifest = new MediaManifest();
        var outer = TimelineWith(SequenceClip("no-such-timeline"));

        var report = ExportMediaPreflight.Inspect(
            outer, manifest, new MediaResolver(() => manifest, () => @"C:\pkg.palmier"),
            timelineLookup: _ => null,
            fileExists: _ => true);

        var problem = Assert.Single(report.Problems);
        Assert.Equal(ExportMediaState.NestedSequenceMissing, problem.State);
        Assert.Contains("nested sequence not found", report.Sentence);
    }

    [Fact]
    public void SequencesThatReferenceEachOther_Terminate()
    {
        var manifest = new MediaManifest { Entries = { Entry("a", "red", "media/a.mp4") } };
        var first = TimelineWith(Clip("a"));
        var second = TimelineWith();
        first.Tracks[0].Clips.Add(SequenceClip(second.Id));
        second.Tracks[0].Clips.Add(SequenceClip(first.Id));

        var lookup = (string id) => id == first.Id ? first : id == second.Id ? second : null;
        var report = ExportMediaPreflight.Inspect(
            first, manifest, new MediaResolver(() => manifest, () => @"C:\pkg.palmier"),
            timelineLookup: lookup, fileExists: _ => true);

        Assert.False(report.HasProblems);
        Assert.Single(report.Items);
    }

    /// <summary>
    /// Past the renderer's cap it draws a grey placeholder instead of recursing, so there is nothing
    /// deeper for a preflight to judge either. Matching the cap keeps the two from disagreeing.
    /// </summary>
    [Fact]
    public void BeyondTheNestingCap_IsNotWalked()
    {
        var manifest = new MediaManifest { Entries = { Entry("deep", "buried", "media/deep.mp4") } };
        var chain = new List<Timeline>();
        for (var i = 0; i < ExportMediaPreflight.MaxSequenceNestingDepth + 3; i++)
            chain.Add(TimelineWith());
        chain[^1].Tracks[0].Clips.Add(Clip("deep"));
        for (var i = 0; i < chain.Count - 1; i++)
            chain[i].Tracks[0].Clips.Add(SequenceClip(chain[i + 1].Id));

        var byId = chain.ToDictionary(t => t.Id, t => t);
        var report = ExportMediaPreflight.Inspect(
            chain[0], manifest, new MediaResolver(() => manifest, () => @"C:\pkg.palmier"),
            timelineLookup: id => byId.TryGetValue(id, out var t) ? t : null,
            fileExists: _ => false);

        Assert.DoesNotContain(report.Items, i => i.Reference == "deep");
    }

    // ---- shapes the exporter itself accepts ---------------------------------------------------

    /// <summary>
    /// <c>TimelineExporter.ResolveAudioPath</c> accepts an absolute MediaRef that exists on disk even
    /// with no manifest entry, so calling that unresolved would be a figure the exporter disagrees
    /// with — but only on the audio path.
    /// </summary>
    [Fact]
    public void AbsoluteAudioRefThatExists_IsAvailableEvenWithNoManifestEntry()
    {
        var manifest = new MediaManifest();
        var timeline = AudioTimelineWith(Clip(@"C:\elsewhere\loose.wav", ClipType.audio));

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, new MediaResolver(() => manifest, () => @"C:\pkg.palmier"),
            fileExists: p => p == @"C:\elsewhere\loose.wav");

        Assert.False(report.HasProblems);
    }

    /// <summary>
    /// <c>TimelineFrameResolver</c> has no such fallback: with no manifest entry the path is null and
    /// the layer is dropped. Calling it available would leave the user unwarned and then hand them a
    /// hard refusal with no "Export anyway" offered.
    /// </summary>
    [Fact]
    public void AbsoluteVideoRefWithNoManifestEntry_IsStillReported()
    {
        var manifest = new MediaManifest();
        var timeline = TimelineWith(Clip(@"C:\elsewhere\loose.mp4"));

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, new MediaResolver(() => manifest, () => @"C:\pkg.palmier"),
            fileExists: _ => true);

        Assert.Equal(ExportMediaState.Unresolved, Assert.Single(report.Problems).State);
    }

    // ---- only what the export actually reaches -------------------------------------------------

    /// <summary>
    /// <c>TimelineFrameResolver</c> skips hidden tracks and <c>TimelineAudioDecoder</c> skips hidden
    /// or muted ones. Warning about media parked on a hidden track would be false — and the only way
    /// past the warning is "Export anyway", which would then disarm the refusal for a clip that really
    /// is missing in the same export.
    /// </summary>
    [Fact]
    public void MediaOnAHiddenTrack_IsNotJudged()
    {
        var manifest = new MediaManifest { Entries = { Entry("a", "parked", "media/a.mp4") } };
        var timeline = TimelineWith();
        timeline.Tracks[0].Hidden = true;
        timeline.Tracks[0].Clips.Add(Clip("a"));

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, new MediaResolver(() => manifest, () => @"C:\pkg.palmier"),
            fileExists: _ => false);

        Assert.Empty(report.Items);
    }

    [Fact]
    public void MediaOnAMutedAudioTrack_IsNotJudged()
    {
        var manifest = new MediaManifest { Entries = { Entry("a", "parked", "media/a.mp4") } };
        var timeline = AudioTimelineWith(Clip("a", ClipType.audio));
        timeline.Tracks[0].Muted = true;

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, new MediaResolver(() => manifest, () => @"C:\pkg.palmier"),
            fileExists: _ => false);

        Assert.Empty(report.Items);
    }

    /// <summary>Muting a video track silences it; the picture is still drawn.</summary>
    [Fact]
    public void MediaOnAMutedVideoTrack_IsStillJudged()
    {
        var manifest = new MediaManifest { Entries = { Entry("a", "shot", "media/a.mp4") } };
        var timeline = TimelineWith(Clip("a"));
        timeline.Tracks[0].Muted = true;

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, new MediaResolver(() => manifest, () => @"C:\pkg.palmier"),
            fileExists: _ => false);

        Assert.Equal(ExportMediaState.Missing, Assert.Single(report.Problems).State);
    }

    /// <summary>
    /// The plan is a snapshot, so the disk half can run off the dispatcher without enumerating the
    /// session's collections while an MCP client mutates them.
    /// </summary>
    [Fact]
    public void CollectTakesASnapshot_ClassifyNeedsNoTimeline()
    {
        var manifest = new MediaManifest { Entries = { Entry("a", "shot", "media/a.mp4") } };
        var timeline = TimelineWith(Clip("a"));
        var plan = ExportMediaPreflight.Collect(
            timeline, manifest, packageRoot: @"C:\pkg.palmier");

        // Whatever happens to the timeline afterwards, the plan is already decided.
        timeline.Tracks[0].Clips.Clear();
        manifest.Entries.Clear();

        var report = ExportMediaPreflight.Classify(
            plan, resolver: null, fileExists: _ => false);

        Assert.Equal("shot", Assert.Single(report.Items).Name);
    }

    [Fact]
    public void ClipsWithNoMediaReference_AreIgnored()
    {
        var manifest = new MediaManifest();
        var timeline = TimelineWith(new Clip { MediaType = ClipType.text, DurationFrames = 30 });

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, new MediaResolver(() => manifest, () => @"C:\pkg.palmier"),
            fileExists: _ => false);

        Assert.Empty(report.Items);
        Assert.False(report.HasProblems);
    }

    [Fact]
    public void Sentence_NamesAtMostFiveAndCountsTheRest()
    {
        var manifest = new MediaManifest();
        var clips = new List<Clip>();
        for (var i = 0; i < 7; i++)
        {
            manifest.Entries.Add(Entry($"m{i}", $"clip{i}", $"media/m{i}.mp4"));
            clips.Add(Clip($"m{i}"));
        }
        var timeline = TimelineWith([.. clips]);

        var report = ExportMediaPreflight.Inspect(
            timeline, manifest, new MediaResolver(() => manifest, () => @"C:\pkg.palmier"),
            fileExists: _ => false);

        Assert.Equal(7, report.Problems.Count);
        Assert.Contains("clip0", report.Sentence);
        Assert.Contains("clip4", report.Sentence);
        Assert.DoesNotContain("clip5", report.Sentence);
        Assert.Contains("and 2 more", report.Sentence);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static Clip Clip(string mediaRef, ClipType type = ClipType.video) => new()
    {
        MediaRef = mediaRef,
        MediaType = type,
        SourceClipType = type,
        DurationFrames = 30,
    };

    private static Clip SequenceClip(string timelineId) => new()
    {
        MediaRef = timelineId,
        MediaType = ClipType.sequence,
        SourceClipType = ClipType.sequence,
        DurationFrames = 30,
    };

    private static MediaManifestEntry Entry(
        string id, string name, string path, bool external = false, string? status = null) => new()
    {
        Id = id,
        Name = name,
        Type = ClipType.video,
        Source = external ? MediaSource.External(path) : MediaSource.Project(path),
        GenerationStatus = status,
    };

    private static Timeline TimelineWith(params Clip[] clips) => Build(ClipType.video, clips);

    private static Timeline AudioTimelineWith(params Clip[] clips) => Build(ClipType.audio, clips);

    private static Timeline Build(ClipType trackType, params Clip[] clips)
    {
        var track = new Track { Type = trackType };
        foreach (var c in clips) track.Clips.Add(c);
        return new Timeline { Name = "T", Fps = 30, Width = 640, Height = 360, Tracks = { track } };
    }

    private static (Timeline, MediaManifest, MediaResolver) Project(params (string Id, string Name)[] media)
    {
        var manifest = new MediaManifest();
        var clips = new List<Clip>();
        foreach (var (id, name) in media)
        {
            manifest.Entries.Add(Entry(id, name, $"media/{id}.mp4"));
            clips.Add(Clip(id));
        }
        return (TimelineWith([.. clips]), manifest, new MediaResolver(() => manifest, () => @"C:\pkg.palmier"));
    }
}
