using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-399: a project's <c>external.absolutePath</c> resolves only after the user allowed that path for
/// that package, and an allowed path that now leads elsewhere is blocked again for any transfer.
/// </summary>
public sealed class ExternalMediaAccessTests : IDisposable
{
    private const string Marker = "WP399_FAKE_PRIVATE_CONTENT";
    private readonly string _root;
    private readonly List<string> _junctions = [];

    public ExternalMediaAccessTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "temno-wp399-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var link in _junctions)
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch { /* best effort */ }
        }
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void OpenedPackage_ExternalEntry_IsBlockedUntilAllowed_AndTheGrantOutlivesTheSession()
    {
        var secret = WriteSecret("private.srt");
        var package = ForeignPackage("foreign.temno", ("S1", ClipType.subtitle, secret));
        var manifestBefore = File.ReadAllBytes(Path.Combine(package, ProjectConstants.ManifestFilename));

        var opened = ProjectSession.Open(package);
        var entry = opened.Manifest.Entries.Single();
        Assert.True(opened.ExternalMedia.IsRestricted);
        Assert.Null(opened.Resolver.ResolvePath(entry));
        Assert.Null(opened.Resolver.ResolvePath("S1"));
        Assert.False(opened.Resolver.Exists("S1"));
        Assert.Null(opened.Resolver.ResolvePathForTransfer(entry));
        Assert.Equal(ExternalMediaState.NotAllowed, opened.Resolver.ExternalState(entry));
        var blocked = Assert.Single(opened.ExternalMedia.Blocked(opened.Manifest));
        Assert.Equal("S1", blocked.MediaRef);
        Assert.True(blocked.CanAllow);

        Assert.True(opened.ExternalMedia.Allow(secret));
        Assert.Equal(secret, opened.Resolver.ResolvePath(entry));
        Assert.Equal(secret, opened.Resolver.ResolvePathForTransfer(entry));
        Assert.Empty(opened.ExternalMedia.Blocked(opened.Manifest));
        Assert.False(opened.IsDirty);

        // Allowing writes nothing into the package: project JSON is the Mac's and stays byte-identical.
        Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(package, ProjectConstants.ManifestFilename)));

        var reopened = ProjectSession.Open(package);
        Assert.Equal(secret, reopened.Resolver.ResolvePathForTransfer(reopened.Manifest.Entries.Single()));
    }

    [Fact]
    public void Grant_BelongsToOnePackage()
    {
        var secret = WriteSecret("clip.mp4");
        var mine = ForeignPackage("mine.temno", ("M1", ClipType.video, secret));
        var other = ForeignPackage("other.temno", ("M1", ClipType.video, secret));

        Assert.True(ProjectSession.Open(mine).ExternalMedia.Allow(secret));

        var foreign = ProjectSession.Open(other);
        Assert.Null(foreign.Resolver.ResolvePath("M1"));
        Assert.Equal(secret, ProjectSession.Open(mine).Resolver.ResolvePath("M1"));
    }

    [Fact]
    public void AllowedPath_ThroughAJunction_IsBlockedForTransferOnceTheJunctionPointsElsewhere()
    {
        var media = Path.Combine(_root, "media-real");
        var secrets = Path.Combine(_root, "secrets-real");
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(secrets);
        File.WriteAllText(Path.Combine(media, "clip.srt"), "1\n00:00:00,000 --> 00:00:01,000\nhello\n");
        File.WriteAllText(Path.Combine(secrets, "clip.srt"), Marker);
        var link = Path.Combine(_root, "linked");
        Junction(link, media);
        var viaLink = Path.Combine(link, "clip.srt");
        var package = ForeignPackage("linked.temno", ("L1", ClipType.subtitle, viaLink));

        var session = ProjectSession.Open(package);
        Assert.True(session.ExternalMedia.Allow(viaLink));
        var grant = Assert.Single(session.ExternalMedia.Grants);
        Assert.Equal(Path.Combine(media, "clip.srt"), grant.Target, ignoreCase: true);
        var entry = session.Manifest.Entries.Single();
        Assert.Equal(viaLink, session.Resolver.ResolvePathForTransfer(entry));

        // The folder the user allowed now leads to another file: same text, different target.
        Directory.Delete(link);
        _junctions.Remove(link);
        Junction(link, secrets);

        Assert.Equal(viaLink, session.Resolver.ResolvePath(entry)); // preview: text only, no disk access
        Assert.Equal(ExternalMediaState.TargetChanged, session.Resolver.ExternalState(entry));
        Assert.Null(session.Resolver.ResolvePathForTransfer(entry));
        Assert.Equal(ExternalMediaState.TargetChanged, ProjectSession.Open(package).Resolver.ExternalState(entry));

        // Allowing again records the new target: the user has now seen where it leads.
        Assert.True(session.ExternalMedia.Allow(viaLink));
        Assert.Equal(viaLink, session.Resolver.ResolvePathForTransfer(entry));
    }

    [Fact]
    public void ChangedPath_AfterAuthorization_IsNotCovered()
    {
        var allowed = WriteSecret("allowed.mp4");
        var other = WriteSecret("other.mp4");
        var package = ForeignPackage("changed.temno", ("C1", ClipType.video, allowed));
        Assert.True(ProjectSession.Open(package).ExternalMedia.Allow(allowed));

        // media.json edited on disk after the user allowed the first path.
        var contents = ProjectPackage.Read(package);
        contents.Manifest!.Entries.Single().Source = MediaSource.External(other);
        ProjectPackage.Write(package, contents.ProjectFile, contents.Manifest);

        var reopened = ProjectSession.Open(package);
        Assert.Null(reopened.Resolver.ResolvePath("C1"));
        Assert.Equal(ExternalMediaState.NotAllowed, reopened.Resolver.ExternalState(reopened.Manifest.Entries.Single()));
    }

    [Fact]
    public void Relink_And_LinkImport_RecordGrants()
    {
        var first = WriteSecret("first.mp4");
        var picked = WriteSecret("picked.mp4");
        var package = ForeignPackage("relink.temno", ("R1", ClipType.video, first));
        var session = ProjectSession.Open(package);
        Assert.Null(session.Resolver.ResolvePath("R1"));

        Assert.True(EditCommands.RelinkMediaEntry(session, "R1", picked).Ok);
        Assert.Equal(picked, session.Resolver.ResolvePathForTransfer(session.Manifest.Entries.Single()));

        // Undo puts the project's own path back, and that path is still not allowed.
        session.Undo();
        Assert.Null(session.Resolver.ResolvePath("R1"));
        session.Redo();
        Assert.Equal(picked, session.Resolver.ResolvePath("R1"));

        var linked = WriteSecret("linked.mov");
        Assert.True(MediaLibraryCommands.PrepareImportedFile(
            session.PackagePath, session.ManifestUnreadable, folderExists: true, linked, "Linked", null,
            prepareEntry: null, out var prepared, referenceInPlace: true).Ok);
        Assert.True(MediaLibraryCommands.CommitImportedFile(session, prepared!.Value).Ok);
        Assert.Equal(linked, session.Resolver.ResolvePathForTransfer(prepared.Value.Entry));
        session.Save();

        var reopened = ProjectSession.Open(package);
        Assert.Equal(linked, reopened.Resolver.ResolvePath(prepared.Value.Entry.Id));
        Assert.Equal(picked, reopened.Resolver.ResolvePath("R1"));
    }

    [Fact]
    public void SaveAs_CarriesOnlyTheAllowedGrants()
    {
        var allowed = WriteSecret("allowed.mp4");
        var blocked = WriteSecret("blocked.srt");
        var package = ForeignPackage("source.temno",
            ("A1", ClipType.video, allowed),
            ("B1", ClipType.subtitle, blocked));
        var session = ProjectSession.Open(package);
        Assert.True(session.ExternalMedia.Allow(allowed));

        var copy = Path.Combine(_root, "copy.temno");
        session.Save(copy);

        var reopened = ProjectSession.Open(copy);
        Assert.Equal(allowed, reopened.Resolver.ResolvePath("A1"));
        Assert.Null(reopened.Resolver.ResolvePath("B1"));
    }

    [Fact]
    public void InMemorySession_SavedThenOpened_GrantsNothingByItself()
    {
        // What a project author does on their own machine: their in-memory session resolves the path,
        // and saving it records no grant, so the package opens blocked (the WP-321 F3 probe's shape).
        var secret = WriteSecret("private.json");
        var session = ProjectSession.CreateNew();
        session.Manifest.Entries.Add(new MediaManifestEntry
        {
            Id = "I1", Name = "innocent image", Type = ClipType.image, Source = MediaSource.External(secret),
        });
        Assert.False(session.ExternalMedia.IsRestricted);
        Assert.Equal(secret, session.Resolver.ResolvePathForTransfer(session.Manifest.Entries.Single()));
        var package = Path.Combine(_root, "authored.temno");
        session.Save(package);

        Assert.Null(ProjectSession.Open(package).Resolver.ResolvePath("I1"));
    }

    [Fact]
    public void Template_IsRestricted()
    {
        var secret = WriteSecret("template-secret.srt");
        var manifest = new MediaManifest
        {
            Entries = [new MediaManifestEntry { Id = "T1", Type = ClipType.subtitle, Source = MediaSource.External(secret) }],
        };
        var session = ProjectSession.FromTemplate(ProjectSession.CreateNew().ProjectFile, manifest);
        Assert.True(session.ExternalMedia.IsRestricted);
        Assert.Null(session.Resolver.ResolvePath("T1"));
        Assert.Single(session.ExternalMedia.Blocked(session.Manifest));
    }

    [Theory]
    [InlineData("/Users/someone/Movies/clip.mov")]
    [InlineData(@"\Users\someone\clip.mov")]
    [InlineData(@"C:relative\clip.mov")]
    [InlineData("media/clip.mov")]
    public void PathThatIsNotFullyQualified_CanNeverBeAllowed(string path)
    {
        var package = ForeignPackage("mac.temno", ("P1", ClipType.video, path));
        var session = ProjectSession.Open(package);
        Assert.False(session.ExternalMedia.Allow(path));
        Assert.Null(session.Resolver.ResolvePath("P1"));
        Assert.False(Assert.Single(session.ExternalMedia.Blocked(session.Manifest)).CanAllow);
    }

    [Fact]
    public void GrantTextMatch_IsExact_AfterNormalization()
    {
        var secret = WriteSecret("Clip.MP4");
        var package = ForeignPackage("text.temno",
            ("U1", ClipType.video, secret.ToUpperInvariant()),
            ("D1", ClipType.video, Path.Combine(_root, "outside", "sub", "..", "Clip.MP4")),
            ("X1", ClipType.video, @"\\?\" + secret));
        var session = ProjectSession.Open(package);
        Assert.True(session.ExternalMedia.Allow(secret));

        Assert.NotNull(session.Resolver.ResolvePath("U1")); // Windows paths ignore case
        Assert.NotNull(session.Resolver.ResolvePath("D1")); // ".." normalizes to the same path
        Assert.Null(session.Resolver.ResolvePath("X1"));    // another spelling is another path
    }

    [Fact]
    public void DamagedGrantFile_ReadsAsNoGrants_AndIsReplacedOnTheNextAllow()
    {
        var file = Path.Combine(_root, "grants.json");
        ExternalMediaGrants.SetPathForTests(file);
        try
        {
            var secret = WriteSecret("clip.mp4");
            var package = ForeignPackage("damaged.temno", ("D1", ClipType.video, secret));
            Assert.True(ProjectSession.Open(package).ExternalMedia.Allow(secret));
            Assert.Single(ExternalMediaGrants.For(package));

            File.WriteAllText(file, "{ not json");
            Assert.Empty(ExternalMediaGrants.For(package));
            Assert.Null(ProjectSession.Open(package).Resolver.ResolvePath("D1"));

            Assert.True(ProjectSession.Open(package).ExternalMedia.Allow(secret));
            Assert.Equal(secret, ProjectSession.Open(package).Resolver.ResolvePath("D1"));
            Assert.DoesNotContain(Marker, File.ReadAllText(file), StringComparison.Ordinal);
            // The damaged file is kept beside the new one, never destroyed.
            Assert.Equal("{ not json", File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "grants.json.damaged-*"))));
        }
        finally
        {
            ExternalMediaGrants.SetPathForTests(null);
        }
    }

    [Fact]
    public void TestsNeverTouchTheUsersRealGrantFile()
    {
        Assert.Equal(ExternalMediaGrantsProcessDefault.Path, ExternalMediaGrants.FilePath);
        Assert.StartsWith(Path.GetTempPath(), ExternalMediaGrants.FilePath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"\\?\C:\Users\someone\clip.mp4")]
    [InlineData(@"\\.\C:\Users\someone\clip.mp4")]
    [InlineData(@"\\?\GLOBALROOT\Device\HarddiskVolume1\clip.mp4")]
    [InlineData(@"C:\Users\someone\clip.mp4:hidden")]
    public void DeviceAndStreamPaths_CanNeverBeAllowed(string path)
    {
        Assert.Null(ExternalMediaAccess.Normalize(path));
        var package = ForeignPackage("device.temno", ("P1", ClipType.video, path));
        var session = ProjectSession.Open(package);
        Assert.False(session.ExternalMedia.Allow(path));
        Assert.Equal(ExternalMediaConcern.DevicePath, Assert.Single(session.ExternalMedia.Blocked(session.Manifest)).Concern);
    }

    [Fact]
    public void BlockedRows_ShowThePathsOwnFileName_AndFlagWhatALinkImportNeverWrites()
    {
        var package = ForeignPackage("rows.temno",
            ("OK", ClipType.video, @"C:\Footage\b-roll.mp4"),
            ("NET", ClipType.video, @"\\nas\share\b-roll.mp4"),
            ("MIS", ClipType.image, @"C:\Users\someone\AppData\Local\Temno\user-preferences.json"),
            ("MAC", ClipType.audio, "/Users/someone/Music/take.wav"));
        var session = ProjectSession.Open(package);
        var rows = session.ExternalMedia.Blocked(session.Manifest).ToDictionary(r => r.MediaRef);

        Assert.Equal(ExternalMediaConcern.None, rows["OK"].Concern);
        Assert.Equal("b-roll.mp4", rows["OK"].FileName);
        Assert.True(rows["OK"].CanAllow);
        Assert.Equal(ExternalMediaConcern.NetworkLocation, rows["NET"].Concern);
        Assert.True(rows["NET"].CanAllow);
        Assert.Equal(ExternalMediaConcern.TypeMismatch, rows["MIS"].Concern); // the F3 probe's "image"
        Assert.Equal("user-preferences.json", rows["MIS"].FileName);
        Assert.False(rows["MIS"].CanAllow);
        Assert.Equal(ExternalMediaConcern.NotAFullPath, rows["MAC"].Concern);
        Assert.False(rows["MAC"].CanAllow);
        Assert.Equal("innocent OK", rows["OK"].Name); // the project's own label, never used to decide
    }

    [Fact]
    public void BlockedNetworkPath_IsNeverContacted()
    {
        // TEST-NET-1 is not routable: any attempt to open the path would wait out an SMB timeout.
        const string unc = @"\\192.0.2.1\share\clip.mp4";
        var package = ForeignPackage("unc.temno", ("N1", ClipType.video, unc));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var session = ProjectSession.Open(package);
        var entry = session.Manifest.Entries.Single();
        Assert.Null(session.Resolver.ResolvePath(entry));
        Assert.Null(session.Resolver.ResolvePathForTransfer(entry));
        Assert.Null(session.Resolver.ForTransfer().ResolvePath(entry));
        Assert.Equal(ExternalMediaState.NotAllowed, session.Resolver.ExternalState(entry));
        Assert.True(session.Resolver.IsBlocked(entry));
        Assert.Single(session.ExternalMedia.Blocked(session.Manifest));
        Assert.Equal(unc, session.Resolver.ReferencePath(entry)); // text for XML writers, never opened
        var preflight = Temno.Core.Export.ExportMediaPreflight.Classify(
            new Temno.Core.Export.ExportMediaPlan([new("N1", "clip", ReachedByVideo: true, entry)], package),
            session.Resolver);
        Assert.Equal(Temno.Core.Export.ExportMediaState.NotAllowed, Assert.Single(preflight.Problems).State);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"took {watch.Elapsed}");
    }

    [Fact]
    public void ForTransfer_ChecksEachPathOncePerCall_AndANewCallSeesASwap()
    {
        var media = Path.Combine(_root, "m");
        var secrets = Path.Combine(_root, "s");
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(secrets);
        File.WriteAllText(Path.Combine(media, "a.mp4"), "VIDEO");
        File.WriteAllText(Path.Combine(secrets, "a.mp4"), Marker);
        var link = Path.Combine(_root, "j");
        Junction(link, media);
        var viaLink = Path.Combine(link, "a.mp4");
        var session = ProjectSession.Open(ForeignPackage("call.temno", ("A", ClipType.video, viaLink)));
        Assert.True(session.ExternalMedia.Allow(viaLink));

        var call = session.Resolver.ForTransfer();
        Assert.Equal(viaLink, call.ResolvePath("A"));
        Directory.Delete(link);
        _junctions.Remove(link);
        Junction(link, secrets);

        Assert.Equal(viaLink, call.ResolvePath("A"));                 // one answer per call
        Assert.Null(session.Resolver.ForTransfer().ResolvePath("A")); // the next call sees the swap
    }

    [Fact]
    public void AllowExternalMedia_SignalsARedraw_WithoutDirtyingTheProject()
    {
        var secret = WriteSecret("clip.mp4");
        var session = ProjectSession.Open(ForeignPackage("signal.temno", ("A", ClipType.video, secret)));
        var revision = session.Revision;
        var raised = 0;
        session.Changed += (_, _) => raised++;

        Assert.Equal(1, session.AllowExternalMedia([secret]));
        Assert.Equal(1, raised);
        Assert.True(session.Revision > revision);
        Assert.False(session.IsDirty);
        Assert.False(session.CanUndo());
    }

    [Fact]
    public void VideoExportAudioFallback_NeverReadsAProjectNamedPathWithoutAGrant()
    {
        var wav = WriteSecret("voice-memo.wav");
        var package = ForeignPackage("loose.temno", (wav, ClipType.audio, Path.Combine(_root, "elsewhere.wav")));
        var restricted = ProjectSession.Open(package);
        var unrestricted = ProjectSession.CreateNew();

        Assert.Null(ExporterAudioPath(restricted.Resolver, wav));          // an entry whose id is a path, blocked
        Assert.Null(ExporterAudioPath(restricted.Resolver, WriteSecret("bare.wav"))); // no entry, no grant
        Assert.Equal(wav, ExporterAudioPath(unrestricted.Resolver, wav));  // in-memory: the old fallback

        var plan = new Temno.Core.Export.ExportMediaPlan(
            [new(WriteSecret("bare2.wav"), "bare", ReachedByVideo: false, Entry: null)], package);
        Assert.Equal(Temno.Core.Export.ExportMediaState.NotAllowed,
            Temno.Core.Export.ExportMediaPreflight.Classify(plan, restricted.Resolver).Items.Single().State);
        Assert.Equal(Temno.Core.Export.ExportMediaState.Available,
            Temno.Core.Export.ExportMediaPreflight.Classify(plan, unrestricted.Resolver).Items.Single().State);
    }

    [Fact]
    public void SmartRenderStreamCopy_NeverFallsBackToAProjectNamedPathWithoutAGrant()
    {
        var mp4 = WriteSecret("home.mp4");
        var package = ForeignPackage("smart.temno", (mp4, ClipType.video, Path.Combine(_root, "elsewhere.mp4")));
        var restricted = ProjectSession.Open(package);

        Assert.Null(SmartRenderLoosePath(restricted.Resolver, mp4));                      // entry id is a path, blocked
        Assert.Null(SmartRenderLoosePath(restricted.Resolver, WriteSecret("bare.mp4")));  // no entry, no grant
        Assert.Null(SmartRenderLoosePath(restricted.Resolver, @"\\192.0.2.1\share\x.mp4")); // never contacted
        Assert.Equal(mp4, SmartRenderLoosePath(ProjectSession.CreateNew().Resolver, mp4));  // in-memory: as before
    }

    private static string? SmartRenderLoosePath(MediaResolver resolver, string mediaRef)
    {
        var analyzer = new Temno.Media.Export.SmartRenderAnalyzer(new ThrowingProbe(), resolver);
        var method = typeof(Temno.Media.Export.SmartRenderAnalyzer).GetMethod("LoosePath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (string?)method.Invoke(analyzer, [mediaRef]);
    }

    private sealed class ThrowingProbe : Temno.Media.Export.IVideoStreamProbe
    {
        public Task<Temno.Media.Export.VideoStreamInfo> ProbeAsync(string path, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("not probed in this test");
    }

    [Fact]
    public void ChangedTarget_IsListedForReview_SignalsARedraw_AndAllowingAgainClearsIt()
    {
        var media = Path.Combine(_root, "m2");
        var secrets = Path.Combine(_root, "s2");
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(secrets);
        File.WriteAllText(Path.Combine(media, "a.mp4"), "VIDEO");
        File.WriteAllText(Path.Combine(secrets, "a.mp4"), Marker);
        var link = Path.Combine(_root, "j2");
        Junction(link, media);
        var viaLink = Path.Combine(link, "a.mp4");
        var session = ProjectSession.Open(ForeignPackage("review.temno", ("A", ClipType.video, viaLink)));
        Assert.True(session.ExternalMedia.Allow(viaLink));
        Assert.Empty(session.ExternalMedia.Blocked(session.Manifest));

        Directory.Delete(link);
        _junctions.Remove(link);
        Junction(link, secrets);
        var raised = 0;
        session.Changed += (_, _) => raised++;

        Assert.Equal(ExternalMediaState.TargetChanged, session.Resolver.ExternalState(session.Manifest.Entries.Single()));
        Assert.Equal(1, raised);
        var row = Assert.Single(session.ExternalMedia.Blocked(session.Manifest));
        Assert.Equal(ExternalMediaConcern.TargetChanged, row.Concern);
        Assert.True(row.CanAllow);
        Assert.Equal(Path.Combine(secrets, "a.mp4"), row.NowLeadsTo, ignoreCase: true);
        Assert.Equal(ExternalMediaState.TargetChanged, session.Resolver.ExternalState(session.Manifest.Entries.Single()));
        Assert.Equal(1, raised); // the same finding again is not news

        Assert.Equal(1, session.AllowExternalMedia([viaLink]));
        Assert.Empty(session.ExternalMedia.Blocked(session.Manifest));
        Assert.Equal(viaLink, session.Resolver.ResolvePathForTransfer(session.Manifest.Entries.Single()));
    }

    [Fact]
    public void InMemorySession_ChecksTheTargetOfAPathItAllowed()
    {
        var media = Path.Combine(_root, "m3");
        var secrets = Path.Combine(_root, "s3");
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(secrets);
        File.WriteAllText(Path.Combine(media, "a.srt"), "1\n00:00:00,000 --> 00:00:01,000\nhi\n");
        File.WriteAllText(Path.Combine(secrets, "a.srt"), Marker);
        var link = Path.Combine(_root, "j3");
        Junction(link, media);
        var viaLink = Path.Combine(link, "a.srt");
        var session = ProjectSession.CreateNew();
        session.Manifest.Entries.Add(new MediaManifestEntry { Id = "L", Type = ClipType.subtitle, Source = MediaSource.External(viaLink) });
        Assert.True(session.ExternalMedia.Allow(viaLink)); // what a Link import or relink in a new project records

        Directory.Delete(link);
        _junctions.Remove(link);
        Junction(link, secrets);

        Assert.Equal(viaLink, session.Resolver.ResolvePath("L"));   // preview: unrestricted
        Assert.Null(session.Resolver.ResolvePathForTransfer(session.Manifest.Entries.Single()));
        // WP-399 T2 round 1: the per-call transfer resolver (transcripts, frames, search, video export) too.
        Assert.Null(session.Resolver.ForTransfer().ResolvePath("L"));
        Assert.False(session.Resolver.ForTransfer().AllowsLooseFile(viaLink));
        // A path the in-memory session holds no grant for is still allowed, unchecked.
        var other = WriteSecret("unrelated.mp4");
        session.Manifest.Entries.Add(new MediaManifestEntry { Id = "U", Type = ClipType.video, Source = MediaSource.External(other) });
        Assert.Equal(other, session.Resolver.ForTransfer().ResolvePath("U"));
    }

    [Fact]
    public void SaveToANewPlace_ReplacesWhateverAnOlderProjectThereWasAllowed()
    {
        var secret = WriteSecret("raw.mp4");
        var package = ForeignPackage("Promo.temno", ("A", ClipType.video, secret));
        Assert.True(ProjectSession.Open(package).ExternalMedia.Allow(secret));
        Directory.Delete(package, recursive: true);

        // A new project saved where the old one was must not inherit its grants.
        ProjectSession.CreateNew().Save(package);
        Assert.Empty(ExternalMediaGrants.For(package));
        var contents = ProjectPackage.Read(package);
        contents.Manifest!.Entries.Add(new MediaManifestEntry { Id = "A", Type = ClipType.video, Source = MediaSource.External(secret) });
        ProjectPackage.Write(package, contents.ProjectFile, contents.Manifest);
        Assert.Null(ProjectSession.Open(package).Resolver.ResolvePath("A"));
    }

    [Fact]
    public void AllowedWhileMissing_RecordsWhereItLeadsAtFirstSight()
    {
        var later = Path.Combine(_root, "unplugged", "clip.mp4");
        var package = ForeignPackage("missing.temno", ("M", ClipType.video, later));
        var session = ProjectSession.Open(package);
        Assert.True(session.ExternalMedia.Allow(later));
        Assert.Equal("", Assert.Single(ExternalMediaGrants.For(package)).Target);
        var entry = session.Manifest.Entries.Single();
        Assert.Equal(ExternalMediaState.Allowed, session.Resolver.ExternalState(entry)); // nothing there to read

        Directory.CreateDirectory(Path.GetDirectoryName(later)!);
        File.WriteAllText(later, "VIDEO");
        Assert.Equal(later, session.Resolver.ResolvePathForTransfer(entry));
        Assert.Equal(later, Assert.Single(ExternalMediaGrants.For(package)).Target, ignoreCase: true);
    }

    [Fact]
    public void LinkImport_ResolvesItsGrantWhilePreparing()
    {
        var file = WriteSecret("linked.mp4");
        var package = ForeignPackage("prepared.temno");
        Assert.True(MediaLibraryCommands.PrepareImportedFile(package, false, true, file, "Linked", null,
            prepareEntry: null, out var prepared, referenceInPlace: true).Ok);
        Assert.NotNull(prepared!.Value.Grant);
        Assert.Equal(file, prepared.Value.Grant!.Target, ignoreCase: true);
        var session = ProjectSession.Open(package);
        Assert.True(MediaLibraryCommands.CommitImportedFile(session, prepared.Value).Ok);
        session.Save();
        Assert.Equal(file, ProjectSession.Open(package).Resolver.ResolvePath(prepared.Value.Entry.Id));
    }

    private static string? ExporterAudioPath(MediaResolver resolver, string mediaRef)
    {
        using var frames = new Temno.Media.Export.ExportFrameSource();
        using var exporter = new Temno.Media.Export.TimelineExporter(
            new Temno.Media.Compositing.CpuCompositor(), frames, resolver,
            acceleration: Temno.Media.Hardware.HardwareAccelerationMode.Software);
        var method = typeof(Temno.Media.Export.TimelineExporter).GetMethod("ResolveAudioPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (string?)method.Invoke(exporter, [mediaRef]);
    }

    [Fact]
    public void BlockedRow_SaysSoInTheMediaList()
    {
        Assert.Equal("video · linked · not allowed",
            Temno.Core.Ui.ImageGenerationStatusLabel.Format("video", linked: true, null, null, blocked: true));
        Assert.Equal("video · linked", Temno.Core.Ui.ImageGenerationStatusLabel.Format("video", linked: true, null, null));
    }

    [Fact]
    public void ManifestEntryDefaultSource_IsNeverAllowed()
    {
        // MediaManifestEntry's default source is External("/missing"): not a full path.
        var package = ForeignPackage("default.temno");
        var contents = ProjectPackage.Read(package);
        contents.Manifest!.Entries.Add(new MediaManifestEntry { Id = "D" });
        ProjectPackage.Write(package, contents.ProjectFile, contents.Manifest);
        Assert.Null(ProjectSession.Open(package).Resolver.ResolvePath("D"));
    }

    /// <summary>
    /// The public constructor makes an unrestricted, in-memory session. Product code must reach a project
    /// on disk through <see cref="ProjectSession.Open"/> or a template through
    /// <see cref="ProjectSession.FromTemplate"/>; a new caller of the constructor fails this test.
    /// </summary>
    [Fact]
    public void ProductCode_BuildsSessionsOnlyThroughTheGuardedFactories()
    {
        var src = Path.Combine(BuildProvenanceTests.RepoRoot(), "src");
        var sep = Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
                        && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .Where(f => !f.EndsWith(Path.Combine("Session", "ProjectSession.cs"), StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("new ProjectSession(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(src, f))
            .ToList();
        Assert.Empty(offenders);
    }

    private string WriteSecret(string name)
    {
        var path = Path.Combine(_root, "outside", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Marker);
        return path;
    }

    /// <summary>A package as another machine wrote it: written straight to disk, no grant recorded here.</summary>
    private string ForeignPackage(string name, params (string Id, ClipType Type, string Path)[] entries)
    {
        var package = Path.Combine(_root, name);
        var manifest = new MediaManifest
        {
            Entries = entries.Select(e => new MediaManifestEntry
            {
                Id = e.Id, Name = "innocent " + e.Id, Type = e.Type, Source = MediaSource.External(e.Path),
            }).ToList(),
        };
        ProjectPackage.Write(package, ProjectSession.CreateNew().ProjectFile, manifest);
        return package;
    }

    private void Junction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "/c", "mklink", "/J", link, target })
            start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
            throw new TimeoutException("mklink /J timed out.");
        var output = stdoutTask.GetAwaiter().GetResult() + stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"mklink /J failed ({process.ExitCode}): {output}");
        _junctions.Add(link);
    }
}
