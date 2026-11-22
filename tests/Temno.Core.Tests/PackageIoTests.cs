using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Xunit;

namespace Temno.Core.Tests;

public class PackageIoTests
{
    private static string PackageDirectory(string prefix) =>
        Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N") + ".palmier");

    private static void DeletePackage(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// WP-399: an imported package's non-Windows external entry (<c>/tmp/…</c>, not a full path on Windows) opens
    /// blocked and can only be relinked, the project-relative entry still resolves, and saving writes the
    /// manifest back with the same keys and the same external path: the boundary adds nothing to the JSON.
    /// </summary>
    [Fact]
    public void ExternalPathFixture_NonWindowsExternalEntryOpensBlocked_AndSavesWithNoNewKeys()
    {
        var dir = PackageDirectory("temno-wp399-external");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ProjectConstants.TimelineFilename), """
                {
                  "timelines": [
                    {
                      "id": "T-MULTI",
                      "name": "Multi",
                      "fps": 30,
                      "width": 1920,
                      "height": 1080,
                      "settingsConfigured": true,
                      "tracks": [
                        {
                          "id": "TV1",
                          "type": "video",
                          "muted": false,
                          "hidden": false,
                          "syncLocked": true,
                          "displayHeight": 50,
                          "clips": [
                            {
                              "id": "C-V1",
                              "mediaRef": "M-VIDEO",
                              "mediaType": "video",
                              "sourceClipType": "video",
                              "startFrame": 0,
                              "durationFrames": 90,
                              "trimStartFrame": 0,
                              "trimEndFrame": 0,
                              "speed": 1.0,
                              "volume": 1.0,
                              "opacity": 1.0
                            }
                          ]
                        },
                        {
                          "id": "TA1",
                          "type": "audio",
                          "muted": false,
                          "hidden": false,
                          "syncLocked": true,
                          "displayHeight": 50,
                          "clips": [
                            {
                              "id": "C-A1",
                              "mediaRef": "M-AUDIO",
                              "mediaType": "audio",
                              "sourceClipType": "audio",
                              "startFrame": 0,
                              "durationFrames": 90,
                              "volume": 0.8,
                              "fadeInFrames": 15,
                              "fadeOutFrames": 15
                            }
                          ]
                        }
                      ],
                      "markers": [],
                      "transitions": []
                    }
                  ],
                  "activeTimelineId": "T-MULTI",
                  "openTimelineIds": ["T-MULTI"]
                }
                """);
            File.WriteAllText(Path.Combine(dir, ProjectConstants.ManifestFilename), """
                {
                  "version": 2,
                  "entries": [
                    {
                      "id": "M-VIDEO",
                      "name": "placeholder-video",
                      "type": "video",
                      "source": {
                        "project": {
                          "relativePath": "media/placeholder.mp4"
                        }
                      },
                      "duration": 3.0,
                      "sourceWidth": 1920,
                      "sourceHeight": 1080,
                      "sourceFPS": 30,
                      "hasAudio": false
                    },
                    {
                      "id": "M-AUDIO",
                      "name": "placeholder-audio",
                      "type": "audio",
                      "source": {
                        "external": {
                          "absolutePath": "/tmp/does-not-need-to-exist-audio.wav"
                        }
                      },
                      "duration": 3.0,
                      "hasAudio": true
                    }
                  ],
                  "folders": []
                }
                """);

            var before = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ProjectConstants.ManifestFilename)))!;

            var session = ProjectSession.Open(dir);
            Assert.True(session.ExternalMedia.IsRestricted);
            Assert.Null(session.Resolver.ResolvePath("M-AUDIO"));
            var blocked = Assert.Single(session.ExternalMedia.Blocked(session.Manifest));
            Assert.Equal("M-AUDIO", blocked.MediaRef);
            Assert.Equal(ExternalMediaConcern.NotAFullPath, blocked.Concern);
            Assert.Equal(Path.Combine(dir, "media", "placeholder.mp4"), session.Resolver.ResolvePath("M-VIDEO"));

            session.MarkDirty();
            session.Save();
            var after = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ProjectConstants.ManifestFilename)))!;
            var beforeEntries = before["entries"]!.AsArray();
            var afterEntries = after["entries"]!.AsArray();
            Assert.Equal(beforeEntries.Count, afterEntries.Count);
            for (var i = 0; i < beforeEntries.Count; i++)
            {
                var beforeKeys = beforeEntries[i]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);
                var afterKeys = afterEntries[i]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal);
                Assert.Equal(beforeKeys, afterKeys);
                Assert.Equal(beforeEntries[i]!["source"]!.ToJsonString(), afterEntries[i]!["source"]!.ToJsonString());
            }
            Assert.Equal(before.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal), after.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal));
        }
        finally
        {
            DeletePackage(dir);
        }
    }

    [Fact]
    public void WriteThenRead_RoundTripsTimelineAndManifest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "temno-test-" + Guid.NewGuid().ToString("N") + ".palmier");
        try
        {
            var session = ProjectSession.CreateNew("Test", fps: 24);
            session.Manifest.Entries.Add(new MediaManifestEntry
            {
                Id = "M1",
                Name = "clip.mp4",
                Type = ClipType.video,
                Source = MediaSource.Project("media/clip.mp4"),
                Duration = 2.5,
            });
            session.ActiveTimeline.Tracks[0].Clips.Add(new Clip
            {
                Id = "C1",
                MediaRef = "M1",
                StartFrame = 0,
                DurationFrames = 60,
            });
            session.MarkDirty();
            session.Save(dir);

            Assert.True(File.Exists(Path.Combine(dir, ProjectConstants.TimelineFilename)));
            Assert.True(File.Exists(Path.Combine(dir, ProjectConstants.ManifestFilename)));
            Assert.True(Directory.Exists(Path.Combine(dir, ProjectConstants.MediaDirectoryName)));

            var loaded = ProjectSession.Open(dir);
            Assert.Equal(24, loaded.ActiveTimeline.Fps);
            Assert.Single(loaded.ActiveTimeline.Tracks[0].Clips);
            Assert.Equal("C1", loaded.ActiveTimeline.Tracks[0].Clips[0].Id);
            Assert.Single(loaded.Manifest.Entries);
            Assert.Equal("M1", loaded.Manifest.Entries[0].Id);

            var path = loaded.Resolver.ResolvePath("M1");
            Assert.Equal(Path.Combine(dir, "media", "clip.mp4"), path);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Read_CorruptManifest_SetsUnreadableFlag()
    {
        var dir = Path.Combine(Path.GetTempPath(), "temno-bad-" + Guid.NewGuid().ToString("N") + ".palmier");
        try
        {
            Directory.CreateDirectory(dir);
            var pf = ProjectSession.CreateNew().ProjectFile;
            File.WriteAllBytes(Path.Combine(dir, ProjectConstants.TimelineFilename), pf.Encode());
            File.WriteAllText(Path.Combine(dir, ProjectConstants.ManifestFilename), "{ not json !!!");

            var contents = ProjectPackage.Read(dir);
            Assert.True(contents.ManifestUnreadable);
            Assert.Null(contents.Manifest);
            Assert.NotNull(contents.RawManifestBytes);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Read_LegacyBareTimeline()
    {
        var dir = Path.Combine(Path.GetTempPath(), "temno-legacy-" + Guid.NewGuid().ToString("N") + ".palmier");
        try
        {
            Directory.CreateDirectory(dir);
            var timeline = new Timeline { Id = "LEG", Name = "Old", Fps = 30 };
            File.WriteAllBytes(
                Path.Combine(dir, ProjectConstants.TimelineFilename),
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(timeline, JsonOptions.ForProject));

            var contents = ProjectPackage.Read(dir);
            Assert.Equal("LEG", contents.ProjectFile.Timelines[0].Id);
            Assert.Equal("LEG", contents.ProjectFile.ActiveTimelineId);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void MediaResolver_ProjectSourceCannotEscapePackage()
    {
        var package = PackageDirectory("temno-contained");
        var outside = Path.Combine(Path.GetDirectoryName(package)!, "outside-" + Guid.NewGuid().ToString("N") + ".mp4");
        try
        {
            Directory.CreateDirectory(Path.Combine(package, "media"));
            File.WriteAllText(outside, "PRIVATE");
            var manifest = new MediaManifest
            {
                Entries =
                [
                    new MediaManifestEntry { Id = "safe", Source = MediaSource.Project("media/clip.mp4") },
                    new MediaManifestEntry { Id = "escape", Source = MediaSource.Project("../" + Path.GetFileName(outside)) },
                    new MediaManifestEntry { Id = "rooted", Source = MediaSource.Project(outside) },
                ],
            };
            var resolver = new MediaResolver(() => manifest, () => package);

            Assert.Equal(Path.Combine(package, "media", "clip.mp4"), resolver.ResolvePath("safe"));
            Assert.Null(resolver.ResolvePath("escape"));
            Assert.Null(resolver.ResolvePath("rooted"));
        }
        finally
        {
            DeletePackage(package);
            if (File.Exists(outside)) File.Delete(outside);
        }
    }

    [Fact]
    public void SaveAs_CopiesPackageArtifactsAndReopens()
    {
        var source = PackageDirectory("temno-source");
        var destination = PackageDirectory("temno-copy");
        try
        {
            var original = ProjectSession.CreateNew("Source");
            original.Manifest.Entries.Add(new MediaManifestEntry
            {
                Id = "M1",
                Name = "nested/clip.bin",
                Type = ClipType.video,
                Source = MediaSource.Project("media/nested/clip.bin"),
                Duration = 1,
            });
            original.Save(source);

            var artifacts = new Dictionary<string, byte[]>
            {
                ["media/nested/clip.bin"] = [1, 2, 3, 4],
                ["thumbnail.jpg"] = [5, 6, 7],
                ["generation-log.json"] = "{\"version\":1}".Select(c => (byte)c).ToArray(),
                ["chat/session-1.json"] = "{\"messages\":[]}".Select(c => (byte)c).ToArray(),
                ["future/opaque.bin"] = [8, 9, 10],
                ["project.json.tmp"] = [11, 12],
            };
            foreach (var (relativePath, bytes) in artifacts)
            {
                var path = Path.Combine(source, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, bytes);
            }

            var opened = ProjectSession.Open(source);
            opened.ActiveTimeline.Name = "Copied";
            opened.MarkDirty();
            opened.Save(destination);

            foreach (var (relativePath, expected) in artifacts)
            {
                var copied = Path.Combine(destination, relativePath);
                Assert.True(File.Exists(copied), $"Missing copied artifact {relativePath}");
                Assert.Equal(expected, File.ReadAllBytes(copied));
            }

            var reloaded = ProjectSession.Open(destination);
            Assert.Equal("Copied", reloaded.ActiveTimeline.Name);
            Assert.Equal(
                Path.Combine(destination, "media", "nested", "clip.bin"),
                reloaded.Resolver.ResolvePath("M1"));

            reloaded.Save();
            Assert.Equal(artifacts["future/opaque.bin"], File.ReadAllBytes(Path.Combine(destination, "future", "opaque.bin")));
        }
        finally
        {
            DeletePackage(source);
            DeletePackage(destination);
        }
    }

    [Fact]
    public void SaveAs_PreservesUnreadableManifestAndRejectsNonEmptyDestination()
    {
        var source = PackageDirectory("temno-corrupt-source");
        var destination = PackageDirectory("temno-corrupt-copy");
        var occupiedDestination = PackageDirectory("temno-occupied");
        try
        {
            Directory.CreateDirectory(source);
            var project = ProjectSession.CreateNew().ProjectFile;
            File.WriteAllBytes(Path.Combine(source, ProjectConstants.TimelineFilename), project.Encode());
            var corruptManifest = "{ not valid json }"u8.ToArray();
            File.WriteAllBytes(Path.Combine(source, ProjectConstants.ManifestFilename), corruptManifest);
            File.WriteAllBytes(Path.Combine(source, ProjectConstants.ThumbnailFilename), [42]);

            var session = ProjectSession.Open(source);
            Assert.True(session.ManifestUnreadable);
            session.Save(destination);

            Assert.Equal(
                corruptManifest,
                File.ReadAllBytes(Path.Combine(destination, ProjectConstants.ManifestFilename)));
            Assert.Equal(
                [42],
                File.ReadAllBytes(Path.Combine(destination, ProjectConstants.ThumbnailFilename)));

            Directory.CreateDirectory(occupiedDestination);
            var sentinel = Path.Combine(occupiedDestination, "keep.bin");
            File.WriteAllBytes(sentinel, [99]);

            var reopened = ProjectSession.Open(source);
            var error = Assert.Throws<IOException>(() => reopened.Save(occupiedDestination));
            Assert.Contains("not empty", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal([99], File.ReadAllBytes(sentinel));
            Assert.False(File.Exists(Path.Combine(occupiedDestination, ProjectConstants.TimelineFilename)));
        }
        finally
        {
            DeletePackage(source);
            DeletePackage(destination);
            DeletePackage(occupiedDestination);
        }
    }

    [Fact]
    public void StageWithoutCommit_LeavesLivePackageUnchangedThenCommitPublishesBothFiles()
    {
        var dir = PackageDirectory("temno-stage");
        try
        {
            var original = ProjectSession.CreateNew("Alpha");
            original.Manifest.Entries.Add(new MediaManifestEntry
            {
                Id = "M1",
                Name = "one",
                Type = ClipType.video,
                Source = MediaSource.Project("media/clip.mp4"),
                Duration = 1,
            });
            original.Save(dir);

            var stagedSession = ProjectSession.Open(dir);
            stagedSession.ActiveTimeline.Name = "Beta";
            stagedSession.Manifest.Entries[0].Name = "two";
            stagedSession.Manifest.Entries.Add(new MediaManifestEntry
            {
                Id = "M2",
                Name = "second",
                Type = ClipType.audio,
                Source = MediaSource.Project("media/second.wav"),
                Duration = 2,
            });

            var staging = ProjectPackage.Stage(dir, stagedSession.ProjectFile, stagedSession.Manifest);
            Assert.True(Directory.Exists(staging));

            var live = ProjectSession.Open(dir);
            Assert.Equal("Alpha", live.ActiveTimeline.Name);
            Assert.Single(live.Manifest.Entries);
            Assert.Equal("M1", live.Manifest.Entries[0].Id);
            Assert.Equal("one", live.Manifest.Entries[0].Name);

            ProjectPackage.Commit(staging, dir);
            Assert.False(Directory.Exists(staging));

            var committed = ProjectSession.Open(dir);
            Assert.Equal("Beta", committed.ActiveTimeline.Name);
            Assert.Equal(2, committed.Manifest.Entries.Count);
            Assert.Equal("two", committed.Manifest.Entries.First(e => e.Id == "M1").Name);
            Assert.Contains(committed.Manifest.Entries, e => e.Id == "M2");
        }
        finally
        {
            DeletePackage(dir);
        }
    }

    [Fact]
    public void SaveInPlace_WithOpenPackageMediaHandle_PersistsAndReopens()
    {
        var root = Path.Combine(Path.GetTempPath(), "temno-open-media-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "locked.palmier");
        try
        {
            var initial = ProjectSession.CreateNew("Before");
            initial.Manifest.Entries.Add(new MediaManifestEntry
            {
                Id = "M1",
                Name = "before.mp4",
                Type = ClipType.video,
                Source = MediaSource.Project("media/clip.mp4"),
                Duration = 1,
            });
            initial.Save(package);

            var mediaPath = Path.Combine(package, "media", "clip.mp4");
            File.WriteAllBytes(mediaPath, [1, 2, 3, 4]);
            var opened = ProjectSession.Open(package);
            opened.ActiveTimeline.Name = "After";
            opened.Manifest.Entries[0].Name = "after.mp4";
            opened.MarkDirty();

            using (var mediaHandle = new FileStream(
                mediaPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                opened.Save();

                Assert.False(opened.IsDirty);
                Assert.Equal(1, mediaHandle.ReadByte());
                Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(mediaPath));

                var reopened = ProjectSession.Open(package);
                Assert.Equal("After", reopened.ActiveTimeline.Name);
                Assert.Equal("after.mp4", reopened.Manifest.Entries[0].Name);
            }

            Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".temno-save-*.partial"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveInPlace_WhenFinalMetadataReplacementIsLocked_RollsBackEarlierFileOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "temno-save-rollback-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "rollback.palmier");
        try
        {
            var initial = ProjectSession.CreateNew("Before");
            initial.Manifest.Entries.Add(new MediaManifestEntry
            {
                Id = "M1",
                Name = "before.mp4",
                Type = ClipType.video,
                Source = MediaSource.Project("media/clip.mp4"),
                Duration = 1,
            });
            initial.Save(package);

            var opened = ProjectSession.Open(package);
            opened.ActiveTimeline.Name = "After";
            opened.Manifest.Entries[0].Name = "after.mp4";
            opened.MarkDirty();

            var projectPath = Path.Combine(package, ProjectConstants.TimelineFilename);
            using (new FileStream(
                projectPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read))
            {
                Assert.ThrowsAny<IOException>(() => opened.Save());
            }

            Assert.True(opened.IsDirty);
            var reopened = ProjectSession.Open(package);
            Assert.Equal("Before", reopened.ActiveTimeline.Name);
            Assert.Equal("before.mp4", reopened.Manifest.Entries[0].Name);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".temno-save-*.partial"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReplaceEntries_WhenLaterEntryIsInvalid_RollsBackEarlierFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "temno-entry-rollback-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, "staging");
        var destination = Path.Combine(root, "destination");
        try
        {
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(staging, "first.json"), "new-first");
            File.WriteAllText(Path.Combine(staging, "second.json"), "new-second");
            File.WriteAllText(Path.Combine(destination, "first.json"), "old-first");
            Directory.CreateDirectory(Path.Combine(destination, "second.json"));

            Assert.Throws<IOException>(() => PackageDirectorySwap.ReplaceEntries(
                staging,
                destination,
                ["first.json", "second.json"],
                ".rollback-"));

            Assert.Equal("old-first", File.ReadAllText(Path.Combine(destination, "first.json")));
            Assert.True(Directory.Exists(Path.Combine(destination, "second.json")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(root, ".rollback-*.partial"));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SaveInPlace_WithReadOnlyProjectMetadata_SucceedsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(Path.GetTempPath(), "temno-readonly-save-" + Guid.NewGuid().ToString("N"));
        var package = Path.Combine(root, "readonly.palmier");
        var projectPath = Path.Combine(package, ProjectConstants.TimelineFilename);
        try
        {
            var initial = ProjectSession.CreateNew("Before");
            initial.Save(package);
            File.SetAttributes(projectPath, File.GetAttributes(projectPath) | FileAttributes.ReadOnly);

            var opened = ProjectSession.Open(package);
            opened.ActiveTimeline.Name = "After";
            opened.MarkDirty();
            opened.Save();

            Assert.Equal("After", ProjectSession.Open(package).ActiveTimeline.Name);
            Assert.False((File.GetAttributes(projectPath) & FileAttributes.ReadOnly) != 0);
        }
        finally
        {
            if (File.Exists(projectPath))
                File.SetAttributes(projectPath, FileAttributes.Normal);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
