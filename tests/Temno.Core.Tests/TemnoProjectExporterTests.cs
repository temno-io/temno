using Temno.Core.Export;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Xunit;

namespace Temno.Core.Tests;

public sealed class TemnoProjectExporterTests
{
    [Fact]
    public void Export_CollectsExternalMediaIntoPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "temno-exp-" + Guid.NewGuid().ToString("N"));
        var sourceMedia = Path.Combine(root, "src");
        var dest = Path.Combine(root, "out.palmier");
        Directory.CreateDirectory(sourceMedia);
        try
        {
            var mediaFile = Path.Combine(sourceMedia, "clip.mp4");
            File.WriteAllBytes(mediaFile, [1, 2, 3, 4, 5]);

            var project = new ProjectFile
            {
                Timelines =
                [
                    new Timeline { Id = "T1", Name = "Main", Fps = 30, Width = 1920, Height = 1080 },
                ],
                ActiveTimelineId = "T1",
            };
            var manifest = new MediaManifest
            {
                Entries =
                [
                    new MediaManifestEntry
                    {
                        Id = "asset1",
                        Name = "Clip",
                        Type = ClipType.video,
                        Duration = 5,
                        Source = MediaSource.External(mediaFile),
                    },
                ],
            };

            // An in-memory manifest's resolver vouches for its own entries (WP-399).
            var report = TemnoProjectExporter.Export(project, manifest, sourcePackageDirectory: null, dest,
                resolver: new MediaResolver(() => manifest, () => null));
            Assert.Contains("asset1", report.Collected);
            Assert.Empty(report.Missing);
            Assert.True(report.TotalBytes >= 5);
            Assert.True(Directory.Exists(dest));
            Assert.True(File.Exists(Path.Combine(dest, ProjectConstants.TimelineFilename)));
            Assert.True(File.Exists(Path.Combine(dest, ProjectConstants.ManifestFilename)));

            var reopened = ProjectPackage.Read(dest);
            Assert.NotNull(reopened.Manifest);
            var entry = Assert.Single(reopened.Manifest!.Entries);
            Assert.IsType<MediaSource.ProjectSource>(entry.Source);
            var rel = ((MediaSource.ProjectSource)entry.Source).RelativePath;
            Assert.StartsWith("media/", rel.Replace('\\', '/'));
            Assert.True(File.Exists(Path.Combine(dest, rel.Replace('/', Path.DirectorySeparatorChar))));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Export_MissingMedia_ReportsAndKeepsDanglingEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "temno-exp-miss-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, "out.palmier");
        Directory.CreateDirectory(root);
        try
        {
            var project = new ProjectFile { Timelines = [new Timeline { Id = "T1", Fps = 30 }] };
            var manifest = new MediaManifest
            {
                Entries =
                [
                    new MediaManifestEntry
                    {
                        Id = "gone",
                        Name = "Missing",
                        Source = MediaSource.External(Path.Combine(root, "no-such.mp4")),
                    },
                ],
            };

            var report = TemnoProjectExporter.Export(project, manifest, null, dest,
                resolver: new MediaResolver(() => manifest, () => null));
            Assert.Single(report.Missing);
            Assert.Equal("gone", report.Missing[0].Id);
            Assert.NotEmpty(report.Warnings);

            var reopened = ProjectPackage.Read(dest);
            Assert.Single(reopened.Manifest!.Entries);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// WP-321 F3 / WP-399: a foreign package names a private file outside itself, used by no clip. The
    /// self-contained export of the opened project must not carry it until the user allows that path.
    /// </summary>
    [Fact]
    public void Export_OpenedForeignProject_LeavesOutAnExternalFileUntilTheUserAllowsIt()
    {
        using var temp = new TempRoot("temno-exp-f3-");
        var secret = Path.Combine(temp.Root, "private.srt");
        File.WriteAllText(secret, "WP399_FAKE_PRIVATE_CONTENT");
        var package = Path.Combine(temp.Root, "foreign.temno");
        ProjectPackage.Write(package, Temno.Core.Session.ProjectSession.CreateNew().ProjectFile, new MediaManifest
        {
            Entries = [new MediaManifestEntry { Id = "S1", Name = "innocent caption", Type = ClipType.subtitle, Source = MediaSource.External(secret) }],
        });
        var session = Temno.Core.Session.ProjectSession.Open(package);

        var blocked = Path.Combine(temp.Root, "blocked.temno");
        var report = TemnoProjectExporter.Export(session.ProjectFile, session.Manifest, package, blocked, resolver: session.Resolver);
        Assert.Equal("S1", Assert.Single(report.NotAllowed).Id);
        Assert.Empty(report.Collected);
        Assert.Contains(report.Warnings, w => w.Contains("not been allowed", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(blocked, "media")));
        Assert.DoesNotContain(Directory.EnumerateFiles(blocked, "*", SearchOption.AllDirectories),
            f => File.ReadAllText(f).Contains("WP399_FAKE_PRIVATE_CONTENT", StringComparison.Ordinal));
        Assert.Equal(secret, Assert.IsType<MediaSource.ExternalSource>(
            Assert.Single(ProjectPackage.Read(blocked).Manifest!.Entries).Source).AbsolutePath);

        // No resolver at all: nothing outside the package is read either.
        var unresolved = TemnoProjectExporter.Export(session.ProjectFile, session.Manifest, package, Path.Combine(temp.Root, "none.temno"));
        Assert.Single(unresolved.NotAllowed);

        Assert.True(session.ExternalMedia.Allow(secret));
        var allowed = Path.Combine(temp.Root, "allowed.temno");
        var collected = TemnoProjectExporter.Export(session.ProjectFile, session.Manifest, package, allowed, resolver: session.Resolver);
        Assert.Equal("S1", Assert.Single(collected.Collected));
        Assert.Empty(collected.NotAllowed);
        Assert.IsType<MediaSource.ProjectSource>(Assert.Single(ProjectPackage.Read(allowed).Manifest!.Entries).Source);
    }

    [Fact]
    public void Export_AllowedFileSwappedForAJunctionTarget_IsLeftOut()
    {
        using var temp = new TempRoot("temno-exp-swap-");
        var media = Path.Combine(temp.Root, "media-real");
        var secrets = Path.Combine(temp.Root, "secrets-real");
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(secrets);
        File.WriteAllText(Path.Combine(media, "clip.mp4"), "VIDEO");
        File.WriteAllText(Path.Combine(secrets, "clip.mp4"), "WP399_FAKE_PRIVATE_CONTENT");
        var link = Path.Combine(temp.Root, "linked");
        temp.Junction(link, media);
        var viaLink = Path.Combine(link, "clip.mp4");
        var package = Path.Combine(temp.Root, "mine.temno");
        ProjectPackage.Write(package, Temno.Core.Session.ProjectSession.CreateNew().ProjectFile, new MediaManifest
        {
            Entries = [new MediaManifestEntry { Id = "V1", Name = "clip", Type = ClipType.video, Source = MediaSource.External(viaLink) }],
        });
        var session = Temno.Core.Session.ProjectSession.Open(package);
        Assert.True(session.ExternalMedia.Allow(viaLink));

        Directory.Delete(link);
        temp.Junction(link, secrets);

        var dest = Path.Combine(temp.Root, "out.temno");
        var report = TemnoProjectExporter.Export(session.ProjectFile, session.Manifest, package, dest, resolver: session.Resolver);
        Assert.Single(report.NotAllowed);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(dest, "media")));
    }

    [Fact]
    public void Export_RejectsSamePathAsSource()
    {
        var path = Path.Combine(Path.GetTempPath(), "temno-same-" + Guid.NewGuid().ToString("N") + ".palmier");
        Directory.CreateDirectory(path);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                TemnoProjectExporter.Export(
                    new ProjectFile(),
                    new MediaManifest(),
                    sourcePackageDirectory: path,
                    destPackageDirectory: path));
        }
        finally
        {
            try { Directory.Delete(path, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Export_ProjectRelativePathCannotCollectFileOutsidePackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "temno-exp-contained-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source.palmier");
        var dest = Path.Combine(root, "out.palmier");
        var outside = Path.Combine(root, "private.mp4");
        Directory.CreateDirectory(source);
        try
        {
            File.WriteAllText(outside, "PRIVATE");
            var manifest = new MediaManifest
            {
                Entries =
                [
                    new MediaManifestEntry
                    {
                        Id = "escape",
                        Name = "Escaped",
                        Source = MediaSource.Project("../private.mp4"),
                    },
                ],
            };

            var report = TemnoProjectExporter.Export(new ProjectFile(), manifest, source, dest);

            Assert.Single(report.Missing);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(dest, "media")));
            var reopened = ProjectPackage.Read(dest);
            Assert.Equal("../private.mp4", Assert.IsType<MediaSource.ProjectSource>(
                Assert.Single(reopened.Manifest!.Entries).Source).RelativePath);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Export_ReplacesExistingDestinationWithoutLeavingOldFilesInNewPackage()
    {
        var root = Path.Combine(Path.GetTempPath(), "temno-exp-replace-" + Guid.NewGuid().ToString("N"));
        var dest = Path.Combine(root, "out.palmier");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, ProjectConstants.TimelineFilename), "{}");
        File.WriteAllText(Path.Combine(dest, "old-only.bin"), "OLD");
        try
        {
            TemnoProjectExporter.Export(new ProjectFile(), new MediaManifest(), null, dest, replaceExistingPackage: true);

            Assert.False(File.Exists(Path.Combine(dest, "old-only.bin")));
            Assert.True(File.Exists(Path.Combine(dest, ProjectConstants.TimelineFilename)));
            Assert.Empty(Directory.EnumerateDirectories(root, ".temno-export-backup-*"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    // WP-398: an existing project package is replaced only when the caller says so.
    [Fact]
    public void Export_RefusesExistingPackage_UnlessReplaceIsAsked()
    {
        using var temp = new TempRoot("temno-exp-keep-");
        var dest = temp.Package("kept.temno", "KEEP");

        var error = Assert.Throws<InvalidOperationException>(() =>
            TemnoProjectExporter.Export(new ProjectFile(), new MediaManifest(), null, dest));

        Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
        Assert.Equal("KEEP", File.ReadAllText(Path.Combine(dest, "sentinel.txt")));
        Assert.Empty(temp.Leftovers());
    }

    // WP-398 / WP-321 F1: a folder that is not a project package is never replaced, whatever the flag.
    [Theory]
    [InlineData("unrelated-directory", false)]
    [InlineData("unrelated-directory", true)]
    [InlineData("named-like.temno", true)]
    [InlineData("named-like.palmier", true)]
    [InlineData("NAMED-LIKE.TEMNO", true)]
    public void Export_NeverReplacesAFolderThatIsNotAPackage(string name, bool replace)
    {
        using var temp = new TempRoot("temno-exp-folder-");
        var dest = Path.Combine(temp.Root, name);
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "sentinel.txt"), "WP398_DIRECTORY_SENTINEL");

        var error = Assert.Throws<InvalidOperationException>(() =>
            TemnoProjectExporter.Export(new ProjectFile(), new MediaManifest(), null, dest, replaceExistingPackage: replace));

        Assert.Contains("not a Temno project package", error.Message, StringComparison.Ordinal);
        Assert.Equal("WP398_DIRECTORY_SENTINEL", File.ReadAllText(Path.Combine(dest, "sentinel.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(dest));
        Assert.Empty(temp.Leftovers());
    }

    [Fact]
    public void Export_RefusesAFolderWhoseProjectJsonIsAFolder()
    {
        using var temp = new TempRoot("temno-exp-fakejson-");
        var dest = Path.Combine(temp.Root, "fake.temno");
        Directory.CreateDirectory(Path.Combine(dest, ProjectConstants.TimelineFilename));
        File.WriteAllText(Path.Combine(dest, "sentinel.txt"), "FAKE");

        Assert.Equal(ExportDestinationKind.Folder, ExportDestination.Inspect(dest));
        Assert.Throws<InvalidOperationException>(() =>
            TemnoProjectExporter.Export(new ProjectFile(), new MediaManifest(), null, dest, replaceExistingPackage: true));
        Assert.Equal("FAKE", File.ReadAllText(Path.Combine(dest, "sentinel.txt")));
    }

    [Fact]
    public void Export_RefusesAFileAtTheDestination()
    {
        using var temp = new TempRoot("temno-exp-file-");
        var dest = Path.Combine(temp.Root, "file.temno");
        File.WriteAllText(dest, "FILE_SENTINEL");

        var error = Assert.Throws<InvalidOperationException>(() =>
            TemnoProjectExporter.Export(new ProjectFile(), new MediaManifest(), null, dest, replaceExistingPackage: true));

        Assert.Contains("is a file", error.Message, StringComparison.Ordinal);
        Assert.Equal("FILE_SENTINEL", File.ReadAllText(dest));
        Assert.Empty(temp.Leftovers());
    }

    // A junction at the destination is refused even when it points at a real package, and a
    // dangling one is refused too: neither the link nor its target is touched.
    [Fact]
    public void Export_RefusesAJunctionAtTheDestination()
    {
        using var temp = new TempRoot("temno-exp-junction-");
        var target = temp.Package("target.temno", "TARGET");
        var link = Path.Combine(temp.Root, "link.temno");
        temp.Junction(link, target);

        var error = Assert.Throws<InvalidOperationException>(() =>
            TemnoProjectExporter.Export(new ProjectFile(), new MediaManifest(), null, link, replaceExistingPackage: true));
        Assert.Contains("junction or symbolic link", error.Message, StringComparison.Ordinal);
        Assert.Equal(ExportDestinationKind.FolderLink, ExportDestination.Inspect(link));
        Assert.Equal("TARGET", File.ReadAllText(Path.Combine(target, "sentinel.txt")));
        Assert.Equal("TARGET", File.ReadAllText(Path.Combine(link, "sentinel.txt")));

        var gone = Path.Combine(temp.Root, "gone");
        Directory.CreateDirectory(gone);
        var dangling = Path.Combine(temp.Root, "dangling.temno");
        temp.Junction(dangling, gone);
        Directory.Delete(gone);
        Assert.False(Directory.Exists(gone));
        Assert.Equal(ExportDestinationKind.FolderLink, ExportDestination.Inspect(dangling));
        Assert.Throws<InvalidOperationException>(() =>
            TemnoProjectExporter.Export(new ProjectFile(), new MediaManifest(), null, dangling, replaceExistingPackage: true));
        Assert.Equal(ExportDestinationKind.FolderLink, ExportDestination.Inspect(dangling));
        Assert.False(Directory.Exists(gone));
        Assert.Empty(temp.Leftovers());
    }

    // Symbolic links need Developer Mode or elevation; where Windows refuses to create one the
    // junction test above still covers the link path, and this says so in the test output.
    [Fact]
    public void Export_RefusesSymbolicLinksAtTheDestination()
    {
        using var temp = new TempRoot("temno-exp-symlink-");
        var target = temp.Package("target.temno", "TARGET");
        var fileTarget = Path.Combine(temp.Root, "target.txt");
        File.WriteAllText(fileTarget, "FILE_TARGET");
        var folderLink = Path.Combine(temp.Root, "folder-link.temno");
        var fileLink = Path.Combine(temp.Root, "file-link.temno");
        var danglingLink = Path.Combine(temp.Root, "dangling-link.temno");
        try
        {
            Directory.CreateSymbolicLink(folderLink, target);
            File.CreateSymbolicLink(fileLink, fileTarget);
            Directory.CreateSymbolicLink(danglingLink, Path.Combine(temp.Root, "nowhere"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.False(Directory.Exists(folderLink) && File.Exists(fileLink),
                "Symbolic links were created but the test treated the platform as unable to: " + ex.Message);
            return;
        }

        Assert.Equal(ExportDestinationKind.FolderLink, ExportDestination.Inspect(folderLink));
        Assert.Equal(ExportDestinationKind.FileLink, ExportDestination.Inspect(fileLink));
        Assert.Equal(ExportDestinationKind.FolderLink, ExportDestination.Inspect(danglingLink));
        foreach (var link in new[] { folderLink, fileLink, danglingLink })
        {
            Assert.Throws<InvalidOperationException>(() =>
                TemnoProjectExporter.Export(new ProjectFile(), new MediaManifest(), null, link, replaceExistingPackage: true));
        }

        Assert.Equal("TARGET", File.ReadAllText(Path.Combine(target, "sentinel.txt")));
        Assert.Equal("FILE_TARGET", File.ReadAllText(fileTarget));
        Assert.Empty(temp.Leftovers());
    }

    // Replacing a real package removes a link inside it as a link; the folder it points at survives.
    [Fact]
    public void Export_ReplacingAPackageLeavesLinkedFoldersUntouched()
    {
        using var temp = new TempRoot("temno-exp-inner-link-");
        var outside = Path.Combine(temp.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "OUTSIDE");
        var dest = temp.Package("old.temno", "OLD");
        temp.Junction(Path.Combine(dest, "linked"), outside);

        TemnoProjectExporter.Export(new ProjectFile(), new MediaManifest(), null, dest, replaceExistingPackage: true);

        Assert.True(File.Exists(Path.Combine(dest, ProjectConstants.TimelineFilename)));
        Assert.False(File.Exists(Path.Combine(dest, "sentinel.txt")));
        Assert.False(Directory.Exists(Path.Combine(dest, "linked")));
        Assert.Equal("OUTSIDE", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
    }

    // What appears at the destination while media is copied is judged again before the swap.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Export_ChecksTheDestinationAgainBeforeTheSwap(bool replace, bool appearsAsPackage)
    {
        using var temp = new TempRoot("temno-exp-race-");
        var media = Path.Combine(temp.Root, "clip.mp4");
        File.WriteAllBytes(media, [1, 2, 3]);
        var manifest = new MediaManifest
        {
            Entries = [new MediaManifestEntry { Id = "a1", Name = "Clip", Type = ClipType.video, Source = MediaSource.External(media) }],
        };
        var dest = Path.Combine(temp.Root, "late.temno");

        void Appear(double _)
        {
            if (Directory.Exists(dest)) return;
            Directory.CreateDirectory(dest);
            File.WriteAllText(Path.Combine(dest, "sentinel.txt"), "LATE");
            if (appearsAsPackage)
                File.WriteAllText(Path.Combine(dest, ProjectConstants.TimelineFilename), "{}");
        }

        Assert.Throws<InvalidOperationException>(() =>
            TemnoProjectExporter.Export(new ProjectFile(), manifest, null, dest, progress: Appear, replaceExistingPackage: replace));

        Assert.Equal("LATE", File.ReadAllText(Path.Combine(dest, "sentinel.txt")));
        Assert.Empty(temp.Leftovers());
    }

    // The open package reached through a junction in its parent path is still the open package.
    [Fact]
    public void Export_RefusesTheOpenPackageThroughAJunctionAlias()
    {
        using var temp = new TempRoot("temno-exp-alias-");
        var source = temp.Package("open.temno", "OPEN");
        var alias = Path.Combine(temp.Root, "alias");
        temp.Junction(alias, temp.Root);
        var aliased = Path.Combine(alias, "open.temno");
        Assert.True(File.Exists(Path.Combine(aliased, "sentinel.txt")));

        var error = Assert.Throws<InvalidOperationException>(() =>
            TemnoProjectExporter.Export(new ProjectFile(), new MediaManifest(), source, aliased, replaceExistingPackage: true));
        Assert.Contains("must not overlap", error.Message, StringComparison.Ordinal);

        var inside = Assert.Throws<InvalidOperationException>(() =>
            TemnoProjectExporter.Export(new ProjectFile(), new MediaManifest(), source, Path.Combine(aliased, "nested.temno")));
        Assert.Contains("must not overlap", inside.Message, StringComparison.Ordinal);
        Assert.Equal("OPEN", File.ReadAllText(Path.Combine(source, "sentinel.txt")));
        Assert.True(ExportDestination.Overlaps(source, aliased));
        Assert.False(ExportDestination.Overlaps(source, Path.Combine(temp.Root, "sibling.temno")));
    }

    // IsWithin has a direction: the folder holding a package is not inside it.
    [Fact]
    public void IsWithin_SeesAliases_AndKeepsItsDirection()
    {
        using var temp = new TempRoot("temno-exp-within-");
        var package = temp.Package("open.temno", "OPEN");
        var alias = Path.Combine(temp.Root, "alias");
        temp.Junction(alias, package);

        Assert.True(ExportDestination.IsWithin(package, package));
        Assert.True(ExportDestination.IsWithin(package, Path.Combine(package, "media", "new.xml")));
        Assert.True(ExportDestination.IsWithin(package, Path.Combine(alias, "project.json")));
        Assert.True(ExportDestination.IsWithin(package, alias));
        Assert.False(ExportDestination.IsWithin(package, temp.Root));
        Assert.False(ExportDestination.IsWithin(package, Path.Combine(temp.Root, "open.temno-beside.xml")));
        Assert.True(ExportDestination.IsWithin(temp.Root, package));
    }

    // WP-398 attack pass: a file that is merely named project.json does not make a folder a package.
    [Theory]
    [InlineData("<?xml version=\"1.0\"?><xmeml version=\"4\"/>")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("\"text\"")]
    public void Inspect_RequiresAJsonObjectProjectJson(string content)
    {
        using var temp = new TempRoot("temno-exp-planted-");
        var folder = Path.Combine(temp.Root, "Keep.temno");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sentinel.txt"), "KEEP");
        File.WriteAllText(Path.Combine(folder, ProjectConstants.TimelineFilename), content);

        Assert.Equal(ExportDestinationKind.Folder, ExportDestination.Inspect(folder));
        Assert.Throws<InvalidOperationException>(() =>
            TemnoProjectExporter.Export(new ProjectFile(), new MediaManifest(), null, folder, replaceExistingPackage: true));
        Assert.Equal("KEEP", File.ReadAllText(Path.Combine(folder, "sentinel.txt")));
        Assert.True(Directory.Exists(Path.Combine(folder, "sub")));

        File.WriteAllText(Path.Combine(folder, ProjectConstants.TimelineFilename), "﻿{ \"timelines\": [] }");
        Assert.Equal(ExportDestinationKind.Package, ExportDestination.Inspect(folder));
    }

    [Fact]
    public void EnclosingPackageFolder_ReadsTheTextAndTheResolvedPath()
    {
        using var temp = new TempRoot("temno-exp-enclosing-");
        var keep = Path.Combine(temp.Root, "Keep.temno");
        Directory.CreateDirectory(keep);
        var link = Path.Combine(temp.Root, "plain-link");
        temp.Junction(link, keep);

        Assert.Equal(keep, ExportDestination.EnclosingPackageFolder(Path.Combine(keep, "project.json")));
        Assert.Equal(keep, ExportDestination.EnclosingPackageFolder(Path.Combine(keep, "deeper", "out.xml")));
        Assert.Equal(keep, ExportDestination.EnclosingPackageFolder(Path.Combine(link, "project.json")), ignoreCase: true);
        Assert.Equal(Path.Combine(temp.Root, "Legacy.PALMIER"), ExportDestination.EnclosingPackageFolder(Path.Combine(temp.Root, "Legacy.PALMIER", "x.temno")));
        Assert.Null(ExportDestination.EnclosingPackageFolder(keep));
        Assert.Null(ExportDestination.EnclosingPackageFolder(Path.Combine(temp.Root, "beside.xml")));
    }

    [Fact]
    public void Inspect_ClassifiesEachKindOfDestination()
    {
        using var temp = new TempRoot("temno-exp-inspect-");
        var file = Path.Combine(temp.Root, "a.txt");
        File.WriteAllText(file, "x");
        var folder = Path.Combine(temp.Root, "folder");
        Directory.CreateDirectory(folder);
        var empty = Path.Combine(temp.Root, "empty.temno");
        Directory.CreateDirectory(empty);
        var package = temp.Package("p.temno", "P");
        var legacy = temp.Package("legacy.PALMIER", "L");

        Assert.Equal(ExportDestinationKind.Absent, ExportDestination.Inspect(Path.Combine(temp.Root, "missing.temno")));
        Assert.Equal(ExportDestinationKind.Absent, ExportDestination.Inspect(Path.Combine(temp.Root, "missing", "deeper.temno")));
        Assert.Equal(ExportDestinationKind.Absent, ExportDestination.Inspect(Path.Combine(file, "under-a-file.temno")));
        Assert.Equal(ExportDestinationKind.File, ExportDestination.Inspect(file));
        Assert.Equal(ExportDestinationKind.Folder, ExportDestination.Inspect(folder));
        Assert.Equal(ExportDestinationKind.Folder, ExportDestination.Inspect(folder + Path.DirectorySeparatorChar));
        Assert.Equal(ExportDestinationKind.Folder, ExportDestination.Inspect(empty));
        Assert.Equal(ExportDestinationKind.Package, ExportDestination.Inspect(package));
        Assert.Equal(ExportDestinationKind.Package, ExportDestination.Inspect(package + Path.AltDirectorySeparatorChar));
        Assert.Equal(ExportDestinationKind.Package, ExportDestination.Inspect(legacy));
        Assert.Equal(ExportDestinationKind.Folder, ExportDestination.Inspect(Path.GetPathRoot(temp.Root)!));
    }

    // The package name is settled before anything is compared: a new path gains .temno, and a path
    // that already names something stays that path.
    [Fact]
    public void ResolvePackagePath_AddsTheWriteExtensionOnlyToANewPath()
    {
        using var temp = new TempRoot("temno-exp-resolve-");
        var folder = Path.Combine(temp.Root, "Reel");
        string At(string name) => Path.Combine(temp.Root, name);

        Assert.Equal(At("Reel.temno"), ExportDestination.ResolvePackagePath(At("Reel")));
        Assert.Equal(At("Reel.temno"), ExportDestination.ResolvePackagePath(At("Reel.palmier")));
        Assert.Equal(At("Reel.temno"), ExportDestination.ResolvePackagePath(At("Reel.temno")));
        Assert.Equal(At("Reel.temno"), ExportDestination.ResolvePackagePath(At("Reel") + Path.DirectorySeparatorChar));
        Assert.Equal(At("Reel.temno"), ExportDestination.ResolvePackagePath(At("Reel. ")));

        Directory.CreateDirectory(folder);
        Assert.Equal(folder, ExportDestination.ResolvePackagePath(folder));
        Assert.Equal(folder, ExportDestination.ResolvePackagePath(folder + Path.DirectorySeparatorChar));
        var legacy = temp.Package("Legacy.palmier", "L");
        Assert.Equal(legacy, ExportDestination.ResolvePackagePath(legacy));
        Assert.Equal(Path.GetPathRoot(temp.Root), ExportDestination.ResolvePackagePath(Path.GetPathRoot(temp.Root)!));

        // A file at the requested path stays beside the new package, as on the Mac; a file that
        // already has the package name is the destination, and is refused as a file.
        File.WriteAllText(At("Notes"), "NOTES");
        Assert.Equal(At("Notes.temno"), ExportDestination.ResolvePackagePath(At("Notes")));
        File.WriteAllText(At("Clip.temno"), "FILE");
        Assert.Equal(At("Clip.temno"), ExportDestination.ResolvePackagePath(At("Clip.temno")));
        Assert.Equal(ExportDestinationKind.File, ExportDestination.Inspect(At("Clip.temno")));
    }

    private sealed class TempRoot : IDisposable
    {
        private readonly List<string> _links = [];

        public TempRoot(string prefix)
        {
            Root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        /// <summary>A minimal project package holding project.json and a sentinel file.</summary>
        public string Package(string name, string sentinel)
        {
            var path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, ProjectConstants.TimelineFilename), "{}");
            File.WriteAllText(Path.Combine(path, "sentinel.txt"), sentinel);
            return path;
        }

        public void Junction(string link, string target)
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(link);
            start.ArgumentList.Add(target);
            using var process = System.Diagnostics.Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"mklink /J failed ({process.ExitCode}): {output}");
            _links.Add(link);
        }

        /// <summary>Staging or backup folders an export left beside its destination.</summary>
        public IEnumerable<string> Leftovers()
            => Directory.EnumerateDirectories(Root, ".temno-export-*");

        public void Dispose()
        {
            // Unlink first so the recursive delete never walks into a link's target.
            foreach (var link in _links)
            {
                try
                {
                    if (Directory.Exists(link) || File.Exists(link))
                        Directory.Delete(link);
                }
                catch
                {
                    // Cleanup is best effort.
                }
            }

            foreach (var path in Directory.EnumerateFileSystemEntries(Root))
            {
                var info = new DirectoryInfo(path);
                if (info.LinkTarget is not null)
                {
                    try { info.Delete(); } catch { /* best effort */ }
                }
            }

            try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Export_RejectsDestinationNestedInsideSource()
    {
        var source = Path.Combine(Path.GetTempPath(), "temno-exp-overlap-" + Guid.NewGuid().ToString("N") + ".palmier");
        var nested = Path.Combine(source, "exports", "out.palmier");
        Directory.CreateDirectory(source);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                TemnoProjectExporter.Export(new ProjectFile(), new MediaManifest(), source, nested));
            Assert.Contains("must not overlap", error.Message);
        }
        finally
        {
            try { Directory.Delete(source, true); } catch { /* ignore */ }
        }
    }
}
