using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Ui;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-152. <see cref="InspectorProjectSummary"/> exists to stop the Inspector displaying a figure
/// that disagrees with the project, and WP-148's review caught it doing exactly that twice — once
/// for in-flight generation placeholders, once for network media after the first fix. Both defects
/// have a test here that fails if the guard is removed.
/// </summary>
public sealed class InspectorProjectSummaryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wp152-" + Guid.NewGuid().ToString("N"));

    public InspectorProjectSummaryTests() => Directory.CreateDirectory(Path.Combine(_root, "media"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temp dir that outlives the test is not a test failure */ }
        catch (UnauthorizedAccessException) { }
    }

    private string WriteMediaFile(string name)
    {
        var path = Path.Combine(_root, "media", name);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private static MediaManifestEntry Entry(
        string name, MediaSource source, string? generationStatus = null) => new()
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Type = ClipType.video,
            Source = source,
            GenerationStatus = generationStatus,
        };

    /// <param name="packagePath">null models a project that has never been saved.</param>
    private static ProjectSession Session(
        string? packagePath, IEnumerable<MediaManifestEntry>? entries = null, int tracks = 0, int clips = 0)
    {
        var timeline = new Timeline { Name = "Timeline 1" };
        for (var t = 0; t < tracks; t++) timeline.Tracks.Add(new Track());
        // Spread the clips across tracks rather than piling them on track 0: a regression to
        // tracks[0].Clips.Count would otherwise still satisfy the count assertions.
        for (var c = 0; c < clips && tracks > 0; c++)
            timeline.Tracks[c % tracks].Clips.Add(
                new Clip { Id = Guid.NewGuid().ToString(), StartFrame = c * 10, DurationFrames = 10 });

        var file = new ProjectFile { Timelines = [timeline], ActiveTimelineId = timeline.Id };
        var manifest = new MediaManifest { Entries = entries?.ToList() ?? [] };
        return new ProjectSession(file, manifest, packagePath);
    }

    [Fact]
    public void NullSession_IsEmpty()
        => Assert.Equal(InspectorProjectSummary.Empty, InspectorProjectSummary.FromSession(null));

    // --- what "missing" is allowed to mean -------------------------------------------------

    [Fact]
    public void UnsavedProject_UnresolvableEntry_IsNotCalledMissing()
    {
        // No package root, so a project-relative path cannot be resolved. That says nothing about
        // the file, and reporting it would be a lying figure.
        var s = InspectorProjectSummary.FromSession(
            Session(null, [Entry("clip", MediaSource.Project("media/clip.mkv"))]));

        Assert.False(s.ProjectIsSaved);
        Assert.Equal(1, s.UnresolvedMediaCount);
        Assert.Equal(0, s.MissingMediaCount);
        Assert.Equal(0, s.ReportableProblemCount);
        Assert.Null(s.ProblemLabel);
    }

    [Fact]
    public void SavedProject_UnresolvableEntry_IsReported()
    {
        // A rooted "relative" path is rejected by MediaResolver, and on a saved project that is a
        // genuinely broken entry rather than an unknown one.
        var s = InspectorProjectSummary.FromSession(
            Session(_root, [Entry("clip", MediaSource.Project("C:/elsewhere/clip.mkv"))]));

        Assert.True(s.ProjectIsSaved);
        Assert.Equal(1, s.UnresolvedMediaCount);
        Assert.Equal(1, s.ReportableProblemCount);
        Assert.NotNull(s.ProblemLabel);
    }

    [Fact]
    public void SavedProject_PresentFile_ProducesNoWarning()
    {
        WriteMediaFile("clip.mkv");
        var s = InspectorProjectSummary.FromSession(
            Session(_root, [Entry("clip", MediaSource.Project("media/clip.mkv"))]));

        Assert.Equal(0, s.MissingMediaCount);
        Assert.Null(s.ProblemLabel);
    }

    // --- WP-148 reviewer round 1: generation placeholders ----------------------------------

    [Theory]
    [InlineData("preparing")]
    [InlineData("generating")]
    [InlineData("downloading")]
    public void InFlightGeneration_IsNotCalledMissing(string status)
    {
        // ToolExecutor writes the manifest entry before the file exists. Until the job lands, the
        // absence of the file is the expected state. Removing the guard makes this fail.
        var s = InspectorProjectSummary.FromSession(
            Session(_root, [Entry("generated shot", MediaSource.Project("media/gen.mp4"), status)]));

        Assert.Equal(1, s.PendingGenerationCount);
        Assert.Equal(0, s.MissingMediaCount);
        Assert.Null(s.ProblemLabel);
    }

    [Fact]
    public void FailedGeneration_IsNotCalledMissing()
    {
        // "failed:" means the file was never written, which is not the same as a file going away.
        var s = InspectorProjectSummary.FromSession(
            Session(_root, [Entry("generated shot", MediaSource.Project("media/gen.mp4"), "failed:quota")]));

        Assert.Equal(1, s.PendingGenerationCount);
        Assert.Equal(0, s.MissingMediaCount);
        Assert.Null(s.ProblemLabel);
    }

    [Fact]
    public void GenerationPlaceholder_DoesNotMaskARealMissingFile()
    {
        // The receipt case: one genuinely missing file and one in-flight placeholder together.
        var s = InspectorProjectSummary.FromSession(Session(_root,
        [
            Entry("real clip", MediaSource.Project("media/gone.mkv")),
            Entry("generated shot", MediaSource.Project("media/gen.mp4"), "preparing"),
        ]));

        Assert.Equal(1, s.MissingMediaCount);
        Assert.Equal(1, s.PendingGenerationCount);
        Assert.Equal("“real clip” is missing from disk", s.ProblemLabel);
    }

    // --- WP-148 reviewer round 2: the regression the first UNC fix introduced ---------------

    [Theory]
    [InlineData(@"\\nas\media\clip.mkv")]
    [InlineData("//nas/media/clip.mkv")]
    public void NetworkMedia_IsNeitherProbedNorReported(string uncPath)
    {
        // Probing a disconnected share blocks the UI thread, so it is never probed — and an
        // unprobed file is not evidence of anything. Counting these as a problem announced
        // "missing from disk" for healthy network media, which is what the reviewer caught.
        var s = InspectorProjectSummary.FromSession(
            Session(_root, [Entry("network clip", MediaSource.External(uncPath))]));

        Assert.Equal(1, s.NetworkUncheckedCount);
        Assert.Equal(0, s.MissingMediaCount);
        Assert.Equal(0, s.UnresolvedMediaCount);
        Assert.Equal(0, s.ReportableProblemCount);
        Assert.Null(s.ProblemLabel);
    }

    [Fact]
    public void NetworkMedia_DoesNotSuppressARealMissingFile()
    {
        var s = InspectorProjectSummary.FromSession(Session(_root,
        [
            Entry("network clip", MediaSource.External(@"\\nas\media\clip.mkv")),
            Entry("real clip", MediaSource.Project("media/gone.mkv")),
        ]));

        Assert.Equal(1, s.NetworkUncheckedCount);
        Assert.Equal(1, s.MissingMediaCount);
        Assert.Equal("“real clip” is missing from disk", s.ProblemLabel);
    }

    // --- label shapes ----------------------------------------------------------------------

    [Fact]
    public void OneMissingFile_IsNamedRatherThanCounted()
    {
        var s = InspectorProjectSummary.FromSession(
            Session(_root, [Entry("holiday.mkv", MediaSource.Project("media/gone.mkv"))]));

        Assert.Equal("“holiday.mkv” is missing from disk", s.ProblemLabel);
    }

    [Fact]
    public void OneMissingFileWithNoName_FallsBackToTheFileName()
    {
        var s = InspectorProjectSummary.FromSession(
            Session(_root, [Entry("   ", MediaSource.Project("media/gone.mkv"))]));

        Assert.Equal("“gone.mkv” is missing from disk", s.ProblemLabel);
    }

    [Fact]
    public void SeveralMissingFiles_AreCountedAgainstTheManifestTotal()
    {
        WriteMediaFile("present.mkv");
        var s = InspectorProjectSummary.FromSession(Session(_root,
        [
            Entry("a", MediaSource.Project("media/gone-a.mkv")),
            Entry("b", MediaSource.Project("media/gone-b.mkv")),
            Entry("c", MediaSource.Project("media/present.mkv")),
        ]));

        Assert.Equal(3, s.MediaCount);
        Assert.Equal("2 of 3 media files are missing from disk", s.ProblemLabel);
    }

    [Fact]
    public void SkippedEntriesStillCountTowardTheManifestTotal()
    {
        // MediaCount is entries.Count, not "entries we classified". If it ever became the latter,
        // the library would show 3 items while the card said "2 of 2" — two figures for one
        // manifest, disagreeing on screen, which is the defect this whole type exists to prevent.
        var s = InspectorProjectSummary.FromSession(Session(_root,
        [
            Entry("a", MediaSource.Project("media/gone-a.mkv")),
            Entry("b", MediaSource.Project("media/gone-b.mkv")),
            Entry("generated shot", MediaSource.Project("media/gen.mp4"), "preparing"),
        ]));

        Assert.Equal(3, s.MediaCount);
        Assert.Equal(2, s.MissingMediaCount);
        Assert.Equal(1, s.PendingGenerationCount);
        Assert.Equal("2 of 3 media files are missing from disk", s.ProblemLabel);
    }

    [Fact]
    public void EmptyTimeline_SaysEmptyRatherThanZero()
        => Assert.Equal("Empty", InspectorProjectSummary.FromSession(Session(null, tracks: 4)).TimelineLabel);

    [Fact]
    public void SingleClipAndTrack_UseSingularWording()
        => Assert.Equal(
            "1 clip · 1 track",
            InspectorProjectSummary.FromSession(Session(null, tracks: 1, clips: 1)).TimelineLabel);

    [Fact]
    public void SeveralClips_UseTheMiddotSeparator()
    {
        // "9 clips on 3 tracks" would imply the clips span all three. They do not.
        var s = InspectorProjectSummary.FromSession(Session(null, tracks: 5, clips: 2));

        Assert.Equal(2, s.ClipCount);
        Assert.Equal(5, s.TrackCount);
        Assert.Equal("2 clips · 5 tracks", s.TimelineLabel);
    }
}
