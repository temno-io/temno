using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Session;
using Temno.Mcp;
using Xunit;

namespace Temno.Core.Tests;

[Collection("EnvRegistry")]
public sealed class McpArgumentAndPathSafetyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _projectsDir;
    private readonly string _templateBase;
    private readonly string _registryPath;
    private readonly List<string> _junctions = [];

    public McpArgumentAndPathSafetyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "TemnoWp208_" + Guid.NewGuid().ToString("N"));
        _projectsDir = Path.Combine(_tempDir, "Projects");
        _templateBase = Path.Combine(_tempDir, "templates-base");
        _registryPath = Path.Combine(_tempDir, "project-registry.json");
        Directory.CreateDirectory(_projectsDir);
        Directory.CreateDirectory(_templateBase);
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", _projectsDir);
        Environment.SetEnvironmentVariable("TEMNO_TEMPLATES_DIR", _templateBase);
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", _registryPath);
        Environment.SetEnvironmentVariable("PALMIER_PROJECTS_DIR", null);
        Environment.SetEnvironmentVariable("PALMIER_TEMPLATES_DIR", null);
        Environment.SetEnvironmentVariable("PALMIER_REGISTRY_PATH", null);
        ProjectHub.ActiveSession = null;
    }

    public void Dispose()
    {
        ProjectHub.ActiveSession = null;
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", null);
        Environment.SetEnvironmentVariable("TEMNO_TEMPLATES_DIR", null);
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", null);
        foreach (var link in _junctions)
        {
            try
            {
                if (Directory.Exists(link))
                    Directory.Delete(link);
            }
            catch
            {
                // The recursive delete below must not follow a junction into another tree.
            }
        }

        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }

    [Fact]
    public void UnknownKeys_AreRejectedOnEveryTool_AndLoggedWithoutValues()
    {
        Assert.Equal(77, McpToolRegistry.Tools.Count);
        var lines = new List<string>();
        void OnLine(string line) => lines.Add(line);
        McpCategoryLog.LineWritten += OnLine;
        try
        {
            foreach (var tool in McpToolRegistry.Tools)
            {
                var before = lines.Count;
                var result = ToolExecutor.Execute(tool.Name, new JsonObject
                {
                    ["__wp208_unknown__"] = "do-not-log-this-secret",
                });
                Assert.True(result.IsError, tool.Name);
                Assert.Contains("__wp208_unknown__", result.Content[0].Text, StringComparison.Ordinal);
                Assert.True(lines.Count > before, tool.Name);
                var line = lines[^1];
                Assert.Contains("category=Mcp", line, StringComparison.Ordinal);
                Assert.Contains("unknown-argument", line, StringComparison.Ordinal);
                Assert.Contains(tool.Name, line, StringComparison.Ordinal);
                Assert.Contains("__wp208_unknown__", line, StringComparison.Ordinal);
                Assert.StartsWith("20", line, StringComparison.Ordinal);
                Assert.DoesNotContain("do-not-log-this-secret", line, StringComparison.Ordinal);
            }
        }
        finally
        {
            McpCategoryLog.LineWritten -= OnLine;
        }

        Assert.Empty(Directory.GetFileSystemEntries(_projectsDir));
        Assert.False(File.Exists(_registryPath));
    }

    [Fact]
    public void MisspelledClipTrack_DoesNotPlaceAClip()
    {
        var session = ProjectSession.CreateNew();
        ProjectHub.ActiveSession = session;

        var nested = ToolExecutor.Execute("add_clips", new JsonObject
        {
            ["entries"] = new JsonArray
            {
                new JsonObject
                {
                    ["mediaRef"] = "m1",
                    ["startFrame"] = 0,
                    ["track"] = 3,
                },
            },
        });
        Assert.True(nested.IsError);
        Assert.Contains("track", nested.Content[0].Text, StringComparison.Ordinal);
        Assert.Empty(session.ActiveTimeline.Tracks.SelectMany(t => t.Clips));

        var topLevel = ToolExecutor.Execute("add_clips", new JsonObject
        {
            ["track"] = 3,
            ["entries"] = new JsonArray
            {
                new JsonObject { ["mediaRef"] = "m1", ["startFrame"] = 0 },
            },
        });
        Assert.True(topLevel.IsError);
        Assert.Contains("track", topLevel.Content[0].Text, StringComparison.Ordinal);
        Assert.Empty(session.ActiveTimeline.Tracks.SelectMany(t => t.Clips));

        var placed = ToolExecutor.Execute("add_clips", new JsonObject
        {
            ["entries"] = new JsonArray
            {
                new JsonObject { ["mediaRef"] = "m1", ["startFrame"] = 0, ["trackIndex"] = 0 },
            },
        });
        Assert.False(placed.IsError, placed.Content[0].Text);
        Assert.Single(session.ActiveTimeline.Tracks[0].Clips);
    }

    [Fact]
    public void CreateTimeline_UndoRestoresThePriorTimelineSet()
    {
        var session = ProjectSession.CreateNew();
        var originalId = session.ProjectFile.ActiveTimelineId;
        ProjectHub.ActiveSession = session;
        Assert.False(session.CanUndo());

        var missing = ToolExecutor.Execute("create_timeline", new JsonObject { ["from"] = "missing" });
        Assert.True(missing.IsError);
        Assert.False(session.CanUndo());
        Assert.Single(session.ProjectFile.Timelines);

        var created = ToolExecutor.Execute("create_timeline", new JsonObject { ["name"] = "Second" });
        Assert.False(created.IsError, created.Content[0].Text);
        Assert.True(session.CanUndo());
        Assert.Equal(2, session.ProjectFile.Timelines.Count);
        Assert.NotEqual(originalId, session.ProjectFile.ActiveTimelineId);

        session.Undo();
        Assert.Single(session.ProjectFile.Timelines);
        Assert.Equal(originalId, session.ProjectFile.ActiveTimelineId);
        Assert.False(session.CanUndo());

        session.Redo();
        Assert.Equal(2, session.ProjectFile.Timelines.Count);
        Assert.Equal("Second", session.ActiveTimeline.Name);
    }

    [Fact]
    public void ApplyEffect_PositionReproducesHandOrder_AndUpdateDoesNotResort()
    {
        var session = ProjectSession.CreateNew();
        Assert.True(EditCommands.AddClip(session, 0, new Clip
        {
            Id = "clip-fx",
            MediaType = ClipType.video,
            MediaRef = "m",
            StartFrame = 0,
            DurationFrames = 30,
        }).Ok);
        ProjectHub.ActiveSession = session;

        var ordered = ToolExecutor.Execute("apply_effect", new JsonObject
        {
            ["clipIds"] = new JsonArray("clip-fx"),
            ["effects"] = new JsonArray
            {
                new JsonObject { ["type"] = "stylize.grain", ["position"] = 0 },
                new JsonObject
                {
                    ["type"] = "blur.gaussian",
                    ["position"] = 1,
                    ["params"] = new JsonObject { ["radius"] = 4 },
                },
            },
        });
        Assert.False(ordered.IsError, ordered.Content[0].Text);
        var effects = Effects(session);
        Assert.Equal(["stylize.grain", "blur.gaussian"], effects.Select(e => e.Type).ToArray());

        var update = ToolExecutor.Execute("apply_effect", new JsonObject
        {
            ["clipIds"] = new JsonArray("clip-fx"),
            ["effects"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "blur.gaussian",
                    ["params"] = new JsonObject { ["radius"] = 9 },
                },
            },
        });
        Assert.False(update.IsError, update.Content[0].Text);
        effects = Effects(session);
        Assert.Equal(["stylize.grain", "blur.gaussian"], effects.Select(e => e.Type).ToArray());
        Assert.Equal(9, effects[1].Params["radius"].Value);

        var outOfRange = ToolExecutor.Execute("apply_effect", new JsonObject
        {
            ["clipIds"] = new JsonArray("clip-fx"),
            ["effects"] = new JsonArray
            {
                new JsonObject { ["type"] = "stylize.vignette", ["position"] = 9 },
            },
        });
        Assert.True(outOfRange.IsError);
        Assert.Equal(["stylize.grain", "blur.gaussian"], Effects(session).Select(e => e.Type).ToArray());
    }

    [Fact]
    public void ProjectNames_RejectEscapeAndReserved_BeforeAnyWrite()
    {
        string[] names =
        [
            "../escaped-project",
            Path.Combine(_tempDir, "absolute-project"),
            "foo/bar",
            "foo\\bar",
            ".",
            "..",
            "CON",
            "con.temno",
            "AUX.palmier",
            "LPT1",
            "COM1",
            "nul",
            "bad:name",
            "trailing.",
            " leading",
            "name ",
            @"\\server\share\project",
        ];

        foreach (var name in names)
        {
            var directories = Directory.GetFileSystemEntries(_projectsDir);
            var registry = File.Exists(_registryPath) ? File.ReadAllBytes(_registryPath) : null;
            ProjectHub.ActiveSession = null;
            var created = ToolExecutor.Execute("new_project", new JsonObject { ["name"] = name });
            Assert.True(created.IsError, name);
            var managed = ToolExecutor.Execute("manage_project", new JsonObject
            {
                ["action"] = "create",
                ["name"] = name,
            });
            Assert.True(managed.IsError, name);
            Assert.Null(ProjectHub.ActiveSession);
            Assert.Equal(directories, Directory.GetFileSystemEntries(_projectsDir));
            var registryAfter = File.Exists(_registryPath) ? File.ReadAllBytes(_registryPath) : null;
            Assert.Equal(registry, registryAfter);
        }
    }

    [Fact]
    public void ProjectNames_CreateSaveAndReopen_ForBothExtensions()
    {
        foreach (var (name, file) in new[]
                 {
                     ("Plain Name", "Plain Name.temno"),
                     ("Reel.temno", "Reel.temno"),
                     ("Legacy.palmier", "Legacy.temno"),
                 })
        {
            ProjectHub.ActiveSession = null;
            var created = ToolExecutor.Execute("new_project", new JsonObject
            {
                ["name"] = name,
                ["fps"] = 24,
            });
            Assert.False(created.IsError, created.Content[0].Text);
            var path = ProjectHub.ActiveSession!.PackagePath!;
            Assert.Equal(Path.Combine(_projectsDir, file), path);
            Assert.True(PathContainment.IsInside(_projectsDir, path));
            Assert.Equal(24, ProjectHub.ActiveSession.ActiveTimeline.Fps);
        }

        var session = ProjectHub.ActiveSession!;
        session.ActiveTimeline.Markers.Add(new Marker { Name = "kept", Frame = 4 });
        var package = session.PackagePath!;
        var closed = ToolExecutor.Execute("close_project", new JsonObject());
        Assert.False(closed.IsError, closed.Content[0].Text);
        var opened = ToolExecutor.Execute("open_project", new JsonObject { ["path"] = package });
        Assert.False(opened.IsError, opened.Content[0].Text);
        Assert.Equal("kept", ProjectHub.ActiveSession!.ActiveTimeline.Markers.Single().Name);
        Assert.Equal(24, ProjectHub.ActiveSession.ActiveTimeline.Fps);
    }

    [Fact]
    public void EscapingJunction_IsRejectedBeforeWrite()
    {
        var outside = Path.Combine(_tempDir, "outside");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "marker.txt");
        File.WriteAllText(marker, "outside-marker");
        MakeJunction(Path.Combine(_projectsDir, "Escape.temno"), outside);

        var created = ToolExecutor.Execute("new_project", new JsonObject { ["name"] = "Escape" });
        Assert.True(created.IsError);
        Assert.Contains("escapes", created.Content[0].Text, StringComparison.Ordinal);
        Assert.Equal("outside-marker", File.ReadAllText(marker));
        Assert.Null(ProjectHub.ActiveSession);
        Assert.False(File.Exists(_registryPath));
    }

    [Fact]
    public void Template_ClonesContent_AndMissingTemplateCreatesNothing()
    {
        var store = new ProjectTemplateStore(_templateBase);
        var source = ProjectSession.CreateNew(name: "From Template", fps: 24, width: 1080, height: 1920);
        source.ActiveTimeline.Markers.Add(new Marker { Name = "template-mark", Frame = 12 });
        store.Save("Launch", source.ProjectFile, source.Manifest);
        Directory.CreateDirectory(store.TemplateDirectory);
        File.WriteAllText(Path.Combine(store.TemplateDirectory, "broken.json"), """{ "nope": true }""");

        var missing = ToolExecutor.Execute("new_project", new JsonObject
        {
            ["name"] = "MissingTemplate",
            ["template"] = "does-not-exist",
        });
        Assert.True(missing.IsError);
        Assert.Contains("not found", missing.Content[0].Text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_projectsDir, "MissingTemplate.temno")));

        var broken = ToolExecutor.Execute("new_project", new JsonObject
        {
            ["name"] = "BrokenTemplate",
            ["template"] = "broken",
        });
        Assert.True(broken.IsError);
        Assert.Contains("unreadable", broken.Content[0].Text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_projectsDir, "BrokenTemplate.temno")));
        Assert.Null(ProjectHub.ActiveSession);

        var created = ToolExecutor.Execute("manage_project", new JsonObject
        {
            ["action"] = "create",
            ["name"] = "FromLaunch",
            ["template"] = "Launch",
        });
        Assert.False(created.IsError, created.Content[0].Text);
        var session = ProjectHub.ActiveSession!;
        Assert.Equal("From Template", session.ActiveTimeline.Name);
        Assert.Equal(24, session.ActiveTimeline.Fps);
        Assert.Equal(1080, session.ActiveTimeline.Width);
        Assert.Equal("template-mark", session.ActiveTimeline.Markers.Single().Name);

        var package = session.PackagePath!;
        Assert.False(ToolExecutor.Execute("close_project", new JsonObject()).IsError);
        Assert.False(ToolExecutor.Execute("open_project", new JsonObject { ["path"] = package }).IsError);
        Assert.Equal("template-mark", ProjectHub.ActiveSession!.ActiveTimeline.Markers.Single().Name);
    }

    [Fact]
    public void ReadSkill_RejectsEscapes_AndFollowsLinksInsideTheRoot()
    {
        foreach (var id in new[] { "../outside", @"..\outside", @"C:\Windows\win.ini", "foo/bar", "foo\\bar", ".", ".." })
        {
            var result = ToolExecutor.Execute("read_skill", new JsonObject { ["id"] = id });
            Assert.True(result.IsError, id);
            Assert.Contains("not a skill name", result.Content[0].Text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-skill", result.Content[0].Text, StringComparison.Ordinal);
        }

        var root = Path.Combine(_tempDir, "skills");
        var real = Path.Combine(root, "legit");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "SKILL.md"), "legit-body");
        var outside = Path.Combine(_tempDir, "outside-skill");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "SKILL.md"), "secret-skill");
        MakeJunction(Path.Combine(root, "linked"), outside);
        MakeJunction(Path.Combine(root, "alias"), real);

        var legit = SkillPathResolver.Require("legit", [root]);
        Assert.Equal("legit-body", File.ReadAllText(legit));
        var alias = SkillPathResolver.Require("alias", [root]);
        Assert.Equal("legit-body", File.ReadAllText(alias));
        Assert.True(PathContainment.IsInside(root, alias));

        var escape = Assert.Throws<InvalidOperationException>(() => SkillPathResolver.Require("linked", [root]));
        Assert.Contains("outside", escape.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-skill", escape.Message, StringComparison.Ordinal);
        Assert.Equal("secret-skill", File.ReadAllText(Path.Combine(outside, "SKILL.md")));
    }

    // WP-321 F1, re-run (WP-398): overwrite=false keeps an existing folder and its sentinel, and the
    // decision is taken after palmier mode adds .temno, so a path that only names the package once
    // normalized is refused too.
    [Fact]
    public void ExportProject_OverwriteFalse_KeepsWhatIsThere_AfterNormalization()
    {
        StartProject("Export Source");
        var unrelated = Path.Combine(_tempDir, "unrelated-directory");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "sentinel.txt"), "WP321_DIRECTORY_SENTINEL");

        var direct = Export(new JsonObject { ["mode"] = "palmier", ["outputPath"] = unrelated, ["overwrite"] = false });
        Assert.True(direct.IsError);
        Assert.Contains("overwrite=false", Text(direct), StringComparison.Ordinal);
        Assert.Equal("WP321_DIRECTORY_SENTINEL", File.ReadAllText(Path.Combine(unrelated, "sentinel.txt")));

        var reel = MakePackage(Path.Combine(_tempDir, "Reel.temno"), "REEL");
        foreach (var requested in new[]
                 {
                     Path.Combine(_tempDir, "Reel"),
                     Path.Combine(_tempDir, "Reel.palmier"),
                     Path.Combine(_tempDir, "Reel") + Path.DirectorySeparatorChar,
                     Path.Combine(_tempDir, "REEL.TEMNO"),
                     Path.Combine(_tempDir, "Reel. "),
                 })
        {
            var result = Export(new JsonObject { ["mode"] = "palmier", ["outputPath"] = requested, ["overwrite"] = false });
            Assert.True(result.IsError, requested);
            Assert.Contains("overwrite=false", Text(result), StringComparison.Ordinal);
            Assert.Contains("Reel.temno", Text(result), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("REEL", File.ReadAllText(Path.Combine(reel, "sentinel.txt")));
        }

        Assert.False(Directory.Exists(Path.Combine(_tempDir, "Reel")));
        Assert.Empty(Directory.EnumerateDirectories(_tempDir, ".temno-export-*"));
    }

    [Fact]
    public void ExportProject_FolderThatIsNotAPackage_IsRefusedEvenWithOverwrite()
    {
        StartProject("Folder Guard");
        var unrelated = Path.Combine(_tempDir, "unrelated-directory");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "sentinel.txt"), "WP398_SENTINEL");
        var empty = Path.Combine(_tempDir, "Empty.temno");
        Directory.CreateDirectory(empty);
        var noJson = Path.Combine(_tempDir, "NoJson.palmier");
        Directory.CreateDirectory(Path.Combine(noJson, ProjectConstants.MediaDirectoryName));
        File.WriteAllText(Path.Combine(noJson, "sentinel.txt"), "NOJSON");

        foreach (var target in new[] { unrelated, empty, noJson, Path.GetPathRoot(_tempDir)! })
        {
            foreach (var overwrite in new bool?[] { true, null })
            {
                var args = new JsonObject { ["mode"] = "palmier", ["outputPath"] = target };
                if (overwrite is { } value)
                    args["overwrite"] = value;
                var result = Export(args);
                Assert.True(result.IsError, $"{target} overwrite={overwrite}");
                Assert.Contains("not a Temno project package", Text(result), StringComparison.Ordinal);
            }
        }

        Assert.Equal("WP398_SENTINEL", File.ReadAllText(Path.Combine(unrelated, "sentinel.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(unrelated));
        Assert.Empty(Directory.EnumerateFileSystemEntries(empty));
        Assert.Equal("NOJSON", File.ReadAllText(Path.Combine(noJson, "sentinel.txt")));
        Assert.Empty(Directory.EnumerateDirectories(_tempDir, ".temno-export-*"));
    }

    [Fact]
    public void ExportProject_ReplacesAnExplicitlyNamedPackage()
    {
        StartProject("Replace Source");
        var old = MakePackage(Path.Combine(_tempDir, "Old.temno"), "OLD");
        var replaced = Export(new JsonObject { ["mode"] = "palmier", ["outputPath"] = old, ["overwrite"] = true });
        Assert.False(replaced.IsError, Text(replaced));
        Assert.Equal(old, OutputPath(replaced));
        Assert.False(File.Exists(Path.Combine(old, "sentinel.txt")));
        Assert.Single(ProjectPackage.Read(old).ProjectFile.Timelines);

        // A legacy package named explicitly is replaced where it is; nothing is written beside it.
        var legacy = MakePackage(Path.Combine(_tempDir, "Legacy.palmier"), "LEGACY");
        var inPlace = Export(new JsonObject { ["mode"] = "palmier", ["outputPath"] = legacy });
        Assert.False(inPlace.IsError, Text(inPlace));
        Assert.Equal(legacy, OutputPath(inPlace));
        Assert.False(File.Exists(Path.Combine(legacy, "sentinel.txt")));
        Assert.False(Directory.Exists(Path.Combine(_tempDir, "Legacy.temno")));

        // A path that names a package once .temno is added is that package.
        var named = MakePackage(Path.Combine(_tempDir, "Named.temno"), "NAMED");
        var normalized = Export(new JsonObject { ["mode"] = "palmier", ["outputPath"] = Path.Combine(_tempDir, "Named") });
        Assert.False(normalized.IsError, Text(normalized));
        Assert.Equal(named, OutputPath(normalized));
        Assert.False(File.Exists(Path.Combine(named, "sentinel.txt")));
        Assert.Empty(Directory.EnumerateDirectories(_tempDir, ".temno-export-*"));
    }

    [Fact]
    public void ExportProject_FilesAndLinks_AreNeverReplacedByAPackage()
    {
        StartProject("Link Guard");
        var file = Path.Combine(_tempDir, "File.temno");
        File.WriteAllText(file, "FILE");
        var notes = Path.Combine(_tempDir, "notes.txt");
        File.WriteAllText(notes, "NOTES");
        var target = MakePackage(Path.Combine(_tempDir, "Target.temno"), "TARGET");
        var link = Path.Combine(_tempDir, "Link.temno");
        MakeJunction(link, target);
        var gone = Path.Combine(_tempDir, "gone");
        Directory.CreateDirectory(gone);
        var dangling = Path.Combine(_tempDir, "Dangling.temno");
        MakeJunction(dangling, gone);
        Directory.Delete(gone);

        foreach (var (path, says) in new[]
                 {
                     (file, "is a file"),
                     (link, "junction or symbolic link"),
                     (dangling, "junction or symbolic link"),
                 })
        {
            var result = Export(new JsonObject { ["mode"] = "palmier", ["outputPath"] = path, ["overwrite"] = true });
            Assert.True(result.IsError, path);
            Assert.Contains(says, Text(result), StringComparison.Ordinal);
        }

        // A file without the package name stays where it is and the package is written beside it.
        var beside = Export(new JsonObject { ["mode"] = "palmier", ["outputPath"] = notes, ["overwrite"] = true });
        Assert.Equal(notes + "." + ProjectConstants.FileExtension, OutputPath(beside));
        Assert.Equal("FILE", File.ReadAllText(file));
        Assert.Equal("NOTES", File.ReadAllText(notes));
        Assert.Equal("TARGET", File.ReadAllText(Path.Combine(target, "sentinel.txt")));
        Assert.False(Directory.Exists(gone));
        Assert.Empty(Directory.EnumerateDirectories(_tempDir, ".temno-export-*"));
    }

    // WP-398 attack pass, P1: an xml export once wrote project.json into a protected *.temno folder,
    // which then read as a package that a palmier export erased. No mode writes into such a folder now.
    [Fact]
    public void ExportProject_NeverWritesIntoAPackageFolder()
    {
        var session = StartProject("Plant Guard");
        var keep = Path.Combine(_tempDir, "Keep.temno");
        Directory.CreateDirectory(Path.Combine(keep, "sub"));
        File.WriteAllText(Path.Combine(keep, "sentinel.txt"), "KEEP");
        File.WriteAllText(Path.Combine(keep, "sub", "deep.txt"), "DEEP");
        var other = MakePackage(Path.Combine(_tempDir, "Other.temno"), "OTHER");
        var plainLink = Path.Combine(_tempDir, "plain-link");
        MakeJunction(plainLink, keep);
        var openPackage = session.PackagePath!;
        var openProjectJson = File.ReadAllBytes(Path.Combine(openPackage, ProjectConstants.TimelineFilename));

        foreach (var (mode, path, says) in new[]
                 {
                     ("xml", Path.Combine(keep, ProjectConstants.TimelineFilename), "inside a project package folder"),
                     ("fcpxml", Path.Combine(keep, "sub", ProjectConstants.TimelineFilename), "inside a project package folder"),
                     ("xml", Path.Combine(plainLink, ProjectConstants.TimelineFilename), "inside a project package folder"),
                     ("xml", Path.Combine(other, ProjectConstants.TimelineFilename), "inside a project package folder"),
                     ("fcpxml", Path.Combine(openPackage, ProjectConstants.TimelineFilename), "inside a project package folder"),
                     ("video", Path.Combine(openPackage, "media", "render.mp4"), "inside a project package folder"),
                     ("palmier", Path.Combine(keep, "Nested"), "inside a project package folder"),
                 })
        {
            foreach (var overwrite in new[] { false, true })
            {
                var result = Export(new JsonObject { ["mode"] = mode, ["outputPath"] = path, ["overwrite"] = overwrite });
                Assert.True(result.IsError, $"{mode} {path} overwrite={overwrite}");
                Assert.Contains(says, Text(result), StringComparison.Ordinal);
            }
        }

        var erase = Export(new JsonObject { ["mode"] = "palmier", ["outputPath"] = keep });
        Assert.True(erase.IsError);
        Assert.Contains("not a Temno project package", Text(erase), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(keep, ProjectConstants.TimelineFilename)));
        Assert.Equal("KEEP", File.ReadAllText(Path.Combine(keep, "sentinel.txt")));
        Assert.Equal("DEEP", File.ReadAllText(Path.Combine(keep, "sub", "deep.txt")));
        Assert.Single(ProjectPackage.Read(other).ProjectFile.Timelines);
        Assert.Equal(openProjectJson, File.ReadAllBytes(Path.Combine(openPackage, ProjectConstants.TimelineFilename)));
    }

    // The open package is protected without a package name too, and a file beside it is still fine.
    [Fact]
    public void ExportProject_FileModes_StayOutOfTheOpenPackage_ButMayWriteBesideIt()
    {
        var plain = Path.Combine(_tempDir, "plain-project");
        ProjectSession.CreateNew().Save(plain);
        ProjectHub.ActiveSession = null;
        var opened = ToolExecutor.Execute("open_project", new JsonObject { ["path"] = plain });
        Assert.False(opened.IsError, opened.Content[0].Text);
        var projectJson = File.ReadAllBytes(Path.Combine(plain, ProjectConstants.TimelineFilename));

        var inside = Export(new JsonObject { ["mode"] = "xml", ["outputPath"] = Path.Combine(plain, ProjectConstants.TimelineFilename) });
        Assert.True(inside.IsError);
        Assert.Contains("inside the open project's package", Text(inside), StringComparison.Ordinal);
        var share = LoopbackShareAlias(Path.Combine(plain, ProjectConstants.TimelineFilename));
        if (share is not null && File.Exists(share))
        {
            var aliased = Export(new JsonObject { ["mode"] = "xml", ["outputPath"] = share });
            Assert.True(aliased.IsError, share);
            Assert.Contains("inside the open project's package", Text(aliased), StringComparison.Ordinal);
        }

        Assert.Equal(projectJson, File.ReadAllBytes(Path.Combine(plain, ProjectConstants.TimelineFilename)));
        var beside = Export(new JsonObject { ["mode"] = "xml", ["outputPath"] = Path.Combine(_tempDir, "beside.xml") });
        Assert.False(beside.IsError, Text(beside));
    }

    // WP-398 T2 F1: a file symbolic link at outputPath once let the file modes write into the open
    // project or a package folder. No mode writes through a link. Creating one needs Developer Mode
    // or elevation; without it there is nothing a caller could point at, and the test says so.
    [Fact]
    public void ExportProject_FileModes_NeverWriteThroughASymbolicLink()
    {
        var plain = Path.Combine(_tempDir, "plain-project");
        ProjectSession.CreateNew().Save(plain);
        ProjectHub.ActiveSession = null;
        Assert.False(ToolExecutor.Execute("open_project", new JsonObject { ["path"] = plain }).IsError);
        var projectJson = File.ReadAllBytes(Path.Combine(plain, ProjectConstants.TimelineFilename));
        var keep = Path.Combine(_tempDir, "Keep.temno");
        Directory.CreateDirectory(keep);
        var live = Path.Combine(_tempDir, "live.xml");
        var dangling = Path.Combine(_tempDir, "dangling.xml");
        var elsewhere = Path.Combine(_tempDir, "elsewhere.xml");
        File.WriteAllText(elsewhere, "ELSEWHERE");
        var plainLink = Path.Combine(_tempDir, "plain.xml");
        try
        {
            File.CreateSymbolicLink(live, Path.Combine(plain, ProjectConstants.TimelineFilename));
            File.CreateSymbolicLink(dangling, Path.Combine(keep, ProjectConstants.TimelineFilename));
            File.CreateSymbolicLink(plainLink, elsewhere);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.False(File.Exists(live), "A link was created but the platform was treated as unable to: " + ex.Message);
            return;
        }

        foreach (var (mode, path) in new[] { ("xml", live), ("fcpxml", dangling), ("xml", plainLink), ("video", live) })
        {
            var result = Export(new JsonObject { ["mode"] = mode, ["outputPath"] = path, ["overwrite"] = true });
            Assert.True(result.IsError, $"{mode} {path}");
            Assert.Contains("symbolic link", Text(result), StringComparison.Ordinal);
        }

        Assert.Equal(projectJson, File.ReadAllBytes(Path.Combine(plain, ProjectConstants.TimelineFilename)));
        Assert.False(File.Exists(Path.Combine(keep, ProjectConstants.TimelineFilename)));
        Assert.Equal("ELSEWHERE", File.ReadAllText(elsewhere));
    }

    // WP-398 T2 F4: picking a free default name only asks whether a name is taken, so a package whose
    // project.json another process holds open is skipped, not an error.
    [Fact]
    public void ExportProject_OmittedPath_SkipsAPackageItCannotRead()
    {
        var downloads = Path.Combine(_tempDir, "Downloads");
        Environment.SetEnvironmentVariable("TEMNO_DOWNLOADS_DIR", downloads);
        try
        {
            StartProject("Locked");
            var held = MakePackage(Path.Combine(downloads, "Locked.temno"), "HELD");
            using var lockStream = new FileStream(
                Path.Combine(held, ProjectConstants.TimelineFilename), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var result = Export(new JsonObject { ["mode"] = "palmier" });
            Assert.Equal(Path.Combine(downloads, "Locked 2.temno"), OutputPath(result));
            Assert.Equal("HELD", File.ReadAllText(Path.Combine(held, "sentinel.txt")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMNO_DOWNLOADS_DIR", null);
        }
    }

    // WP-398 attack pass, P2: two default-path exports at once once got one name and the second
    // replaced the first. Each now gets its own name, whatever the timing.
    [Fact]
    public async Task ExportProject_ConcurrentOmittedPaths_GetDistinctNames()
    {
        var downloads = Path.Combine(_tempDir, "Downloads");
        Environment.SetEnvironmentVariable("TEMNO_DOWNLOADS_DIR", downloads);
        try
        {
            var session = StartProject("Race");
            var media = Path.Combine(_tempDir, "slow.bin");
            using (var stream = File.Create(media))
                stream.SetLength(64L * 1024 * 1024);
            session.Manifest.Entries.Add(new MediaManifestEntry
            {
                Id = "slow",
                Name = "slow",
                Type = ClipType.video,
                Source = MediaSource.External(media),
            });

            var calls = Enumerable.Range(0, 3)
                .Select(_ => Task.Run(() => Export(new JsonObject { ["mode"] = "palmier" })))
                .ToArray();
            var results = await Task.WhenAll(calls);

            var paths = results.Select(OutputPath).ToList();
            Assert.Equal(3, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(paths, p => Assert.True(File.Exists(Path.Combine(p, ProjectConstants.TimelineFilename)), p));
            Assert.Equal(3, Directory.EnumerateDirectories(downloads, "Race*.temno").Count());
            Assert.Empty(Directory.EnumerateDirectories(downloads, ".temno-export-*"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMNO_DOWNLOADS_DIR", null);
        }
    }

    [Fact]
    public void ExportProject_FileModes_RefuseAFolder_AndKeepOverwriteFalse()
    {
        StartProject("File Modes");
        var folder = Path.Combine(_tempDir, "out.xml");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "sentinel.txt"), "FOLDER");
        foreach (var mode in new[] { "xml", "fcpxml", "video" })
        {
            var result = Export(new JsonObject { ["mode"] = mode, ["outputPath"] = folder });
            Assert.True(result.IsError, mode);
            Assert.Contains("is a folder", Text(result), StringComparison.Ordinal);
        }

        Assert.Equal("FOLDER", File.ReadAllText(Path.Combine(folder, "sentinel.txt")));

        var existing = Path.Combine(_tempDir, "existing.xml");
        File.WriteAllText(existing, "SENTINEL");
        var kept = Export(new JsonObject { ["mode"] = "xml", ["outputPath"] = existing, ["overwrite"] = false });
        Assert.True(kept.IsError);
        Assert.Contains("overwrite=false", Text(kept), StringComparison.Ordinal);
        Assert.Equal("SENTINEL", File.ReadAllText(existing));

        var replaced = Export(new JsonObject { ["mode"] = "xml", ["outputPath"] = existing });
        Assert.False(replaced.IsError, Text(replaced));
        Assert.Contains("<xmeml", File.ReadAllText(existing), StringComparison.Ordinal);
    }

    [Fact]
    public void ExportProject_OmittedPath_PicksAFreeName()
    {
        var downloads = Path.Combine(_tempDir, "Downloads");
        Environment.SetEnvironmentVariable("TEMNO_DOWNLOADS_DIR", downloads);
        try
        {
            StartProject("Omitted");
            Directory.CreateDirectory(downloads);
            var previous = MakePackage(Path.Combine(downloads, "Omitted.temno"), "PREVIOUS");

            var package = Export(new JsonObject { ["mode"] = "palmier" });
            Assert.False(package.IsError, Text(package));
            Assert.Equal(Path.Combine(downloads, "Omitted 2.temno"), OutputPath(package));
            Assert.Equal("PREVIOUS", File.ReadAllText(Path.Combine(previous, "sentinel.txt")));

            var first = OutputPath(Export(new JsonObject { ["mode"] = "fcpxml" }));
            var firstBytes = File.ReadAllBytes(first);
            var second = OutputPath(Export(new JsonObject { ["mode"] = "fcpxml" }));
            Assert.Equal(Path.Combine(downloads, "Omitted.fcpxml"), first);
            Assert.Equal(Path.Combine(downloads, "Omitted 2.fcpxml"), second);
            Assert.Equal(firstBytes, File.ReadAllBytes(first));
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMNO_DOWNLOADS_DIR", null);
        }
    }

    // The open package is refused as a destination under an alias as well as by its own path. A
    // loopback admin share is checked only where this account can reach one.
    [Fact]
    public void ExportProject_RefusesTheOpenPackageUnderAnAlias()
    {
        var session = StartProject("Aliased");
        var package = session.PackagePath!;
        var alias = Path.Combine(_tempDir, "alias");
        MakeJunction(alias, _projectsDir);
        var aliases = new List<string> { package, Path.Combine(alias, Path.GetFileName(package)) };
        var share = LoopbackShareAlias(package);
        if (share is not null && Directory.Exists(share))
            aliases.Add(share);

        foreach (var target in aliases)
        {
            var result = Export(new JsonObject { ["mode"] = "palmier", ["outputPath"] = target, ["overwrite"] = true });
            Assert.True(result.IsError, target);
            Assert.Contains("must not overlap", Text(result), StringComparison.Ordinal);
        }

        Assert.True(File.Exists(Path.Combine(package, ProjectConstants.TimelineFilename)));
        Assert.True(Directory.Exists(Path.Combine(package, ProjectConstants.MediaDirectoryName)));
        Assert.Empty(Directory.EnumerateDirectories(_projectsDir, ".temno-export-*"));
    }

    [Fact]
    public void ManageTracks_LegacyActionStillAddsATrack()
    {
        var session = ProjectSession.CreateNew();
        var before = session.ActiveTimeline.Tracks.Count;
        ProjectHub.ActiveSession = session;
        var result = ToolExecutor.Execute("manage_tracks", new JsonObject
        {
            ["action"] = "add",
            ["type"] = "audio",
        });
        Assert.False(result.IsError, result.Content[0].Text);
        Assert.Equal(before + 1, session.ActiveTimeline.Tracks.Count);
        Assert.Equal(ClipType.audio, session.ActiveTimeline.Tracks[^1].Type);
    }

    [Fact]
    public async Task HttpClient_DrivesEachSafetyCase()
    {
        var port = FreeLoopbackPort();
        var origin = $"http://127.0.0.1:{port}";
        var server = new McpServer("127.0.0.1", port);
        server.Start();
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add(McpDefaults.AuthHeaderName, server.AuthToken);
        try
        {
            var init = await Rpc(client, origin, "initialize", 1, new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "wp-208", ["version"] = "1" },
            });
            Assert.Contains("temno", init.ToJsonString(), StringComparison.OrdinalIgnoreCase);

            var escaped = await Tool(client, origin, 2, "new_project", new JsonObject { ["name"] = "../escaped-project" });
            Assert.True(ToolFailed(escaped));
            Assert.Contains("not a path", ToolText(escaped), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(_projectsDir, "..", "escaped-project.temno")));

            var created = await Tool(client, origin, 3, "new_project", new JsonObject { ["name"] = "Http Receipt", ["fps"] = 24 });
            Assert.False(ToolFailed(created), ToolText(created));

            var misspelled = await Tool(client, origin, 4, "add_clips", new JsonObject
            {
                ["entries"] = new JsonArray
                {
                    new JsonObject { ["mediaRef"] = "m1", ["startFrame"] = 0, ["track"] = 3 },
                },
            });
            Assert.True(ToolFailed(misspelled));
            Assert.Contains("track", ToolText(misspelled), StringComparison.Ordinal);

            var placed = await Tool(client, origin, 5, "add_clips", new JsonObject
            {
                ["entries"] = new JsonArray
                {
                    new JsonObject { ["mediaRef"] = "m1", ["startFrame"] = 0, ["trackIndex"] = 0 },
                },
            });
            Assert.False(ToolFailed(placed), ToolText(placed));

            var timeline = await Tool(client, origin, 6, "get_timeline", new JsonObject());
            var timelineJson = JsonNode.Parse(ToolText(timeline))!;
            var clipId = timelineJson["tracks"]![0]!["clips"]![0]!["id"]!.GetValue<string>();
            var ordered = await Tool(client, origin, 7, "apply_effect", new JsonObject
            {
                ["clipIds"] = new JsonArray(clipId),
                ["effects"] = new JsonArray
                {
                    new JsonObject { ["type"] = "stylize.grain", ["position"] = 0 },
                    new JsonObject { ["type"] = "blur.gaussian", ["position"] = 1 },
                },
            });
            Assert.False(ToolFailed(ordered), ToolText(ordered));
            var afterEffects = await Tool(client, origin, 8, "get_timeline", new JsonObject());
            var effectTypes = JsonNode.Parse(ToolText(afterEffects))!["tracks"]![0]!["clips"]![0]!["effects"]!
                .AsArray()
                .Select(e => e!["type"]!.GetValue<string>())
                .ToArray();
            Assert.Equal(["stylize.grain", "blur.gaussian"], effectTypes);

            var second = await Tool(client, origin, 9, "create_timeline", new JsonObject { ["name"] = "Second" });
            Assert.False(ToolFailed(second), ToolText(second));
            var undone = await Tool(client, origin, 10, "undo", new JsonObject());
            Assert.False(ToolFailed(undone), ToolText(undone));
            var restored = JsonNode.Parse(ToolText(await Tool(client, origin, 11, "get_timeline", new JsonObject())))!;
            Assert.Equal("Timeline 1", restored["name"]!.GetValue<string>());
            Assert.Null(restored["timelines"]);

            var skill = await Tool(client, origin, 12, "read_skill", new JsonObject { ["id"] = "../outside" });
            Assert.True(ToolFailed(skill));
            Assert.Contains("not a skill name", ToolText(skill), StringComparison.Ordinal);

            // WP-321 F1 over the wire (WP-398).
            var guarded = Path.Combine(_tempDir, "http-unrelated-directory");
            Directory.CreateDirectory(guarded);
            File.WriteAllText(Path.Combine(guarded, "sentinel.txt"), "HTTP_SENTINEL");
            var kept = await Tool(client, origin, 20, "export_project", new JsonObject
            {
                ["mode"] = "palmier",
                ["outputPath"] = guarded,
                ["overwrite"] = false,
            });
            Assert.True(ToolFailed(kept));
            Assert.Contains("overwrite=false", ToolText(kept), StringComparison.Ordinal);
            var refused = await Tool(client, origin, 21, "export_project", new JsonObject
            {
                ["mode"] = "palmier",
                ["outputPath"] = guarded,
            });
            Assert.True(ToolFailed(refused));
            Assert.Contains("not a Temno project package", ToolText(refused), StringComparison.Ordinal);
            Assert.Equal("HTTP_SENTINEL", File.ReadAllText(Path.Combine(guarded, "sentinel.txt")));

            var store = new ProjectTemplateStore(_templateBase);
            var source = ProjectSession.CreateNew(name: "Http Template", fps: 12, width: 640, height: 360);
            source.ActiveTimeline.Markers.Add(new Marker { Name = "http-mark", Frame = 3 });
            store.Save("HttpLaunch", source.ProjectFile, source.Manifest);
            Assert.False(ToolFailed(await Tool(client, origin, 13, "close_project", new JsonObject())), "close");
            var fromTemplate = await Tool(client, origin, 14, "new_project", new JsonObject
            {
                ["name"] = "HttpFromTemplate",
                ["template"] = "HttpLaunch",
            });
            Assert.False(ToolFailed(fromTemplate), ToolText(fromTemplate));
            Assert.Contains("Http Template", ToolText(fromTemplate), StringComparison.Ordinal);
            Assert.Contains("12", ToolText(fromTemplate), StringComparison.Ordinal);

            var missingTemplate = await Tool(client, origin, 15, "new_project", new JsonObject
            {
                ["name"] = "HttpMissing",
                ["template"] = "missing-template",
            });
            Assert.True(ToolFailed(missingTemplate));
            Assert.Contains("not found", ToolText(missingTemplate), StringComparison.Ordinal);
            Assert.False(Directory.Exists(Path.Combine(_projectsDir, "HttpMissing.temno")));
        }
        finally
        {
            server.Stop();
        }
    }

    // ---- WP-399: a foreign project's files outside its package (WP-321 F3) ---------------------------

    private const string Wp399Marker = "WP399_FAKE_PRIVATE_CONTENT";

    /// <summary>The F3 package: written straight to disk, as another machine would, so no grant exists here.</summary>
    private (string Package, string Captions, string Image) Wp399ForeignPackage(string name)
    {
        var outside = Path.Combine(_tempDir, name + "-outside");
        Directory.CreateDirectory(outside);
        var captions = Path.Combine(outside, "private.srt");
        File.WriteAllText(captions, $"1\n00:00:00,000 --> 00:00:01,000\n{Wp399Marker}\n");
        var image = Path.Combine(outside, "private.json");
        File.WriteAllText(image, Wp399Marker);
        var package = Path.Combine(_projectsDir, name + ".temno");
        ProjectPackage.Write(package, ProjectSession.CreateNew().ProjectFile, new MediaManifest
        {
            Entries =
            [
                new MediaManifestEntry { Id = "F3-CAPTIONS", Name = "innocent caption", Type = ClipType.subtitle, Source = MediaSource.External(captions) },
                new MediaManifestEntry { Id = "F3-IMAGE", Name = "innocent image", Type = ClipType.image, Source = MediaSource.External(image) },
            ],
        });
        return (package, captions, image);
    }

    private static bool Wp399HasMarker(ToolCallResult result)
        => result.Content.Any(c => c.Text?.Contains(Wp399Marker, StringComparison.Ordinal) == true);

    private static bool Wp399TreeHasMarker(string root)
        => Directory.Exists(root) && Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Any(f => File.ReadAllText(f).Contains(Wp399Marker, StringComparison.Ordinal));

    private static async Task<(int Calls, Exception? Error)> Wp399Upload(ProjectSession session, string mediaRef)
    {
        var client = new Wp399RecordingClient();
        var upload = typeof(ToolExecutor).GetMethod("UploadReferencesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        try
        {
            await (Task<List<string>>)upload.Invoke(null, [session, client, new List<string> { mediaRef }, CancellationToken.None])!;
            return (client.Calls, null);
        }
        catch (Exception ex)
        {
            // The method is async: a refusal surfaces from the await, not as a reflection wrapper.
            return (client.Calls, ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex);
        }
    }

    [Fact]
    public async Task ForeignProject_ExternalFiles_ReachNoMcpSinkBeforeTheUserAllowsThem()
    {
        var (package, captions, _) = Wp399ForeignPackage("Foreign");
        var manifestPath = Path.Combine(package, ProjectConstants.ManifestFilename);
        var written = File.ReadAllBytes(manifestPath);

        var open = ToolExecutor.Execute("open_project", new JsonObject { ["path"] = package });
        Assert.False(open.IsError, Text(open));
        Assert.Equal(2, JsonNode.Parse(Text(open))!["blockedExternalMedia"]!.GetValue<int>());

        foreach (var mediaRef in new[] { "F3-CAPTIONS", "F3-IMAGE" })
        {
            var inspect = ToolExecutor.Execute("inspect_media", new JsonObject { ["mediaRef"] = mediaRef });
            Assert.False(inspect.IsError, Text(inspect));
            Assert.False(Wp399HasMarker(inspect));
            Assert.Single(inspect.Content); // metadata only: no frames
            var meta = JsonNode.Parse(Text(inspect))!;
            Assert.Equal("notAllowed", meta["externalAccess"]!.GetValue<string>());
            Assert.True(meta["offline"]!.GetValue<bool>());
            Assert.Null(meta["byteSize"]);
            Assert.Null(meta["transcription"]);
            Assert.Contains("review it in Temno's media panel", meta["note"]!.GetValue<string>(), StringComparison.Ordinal);
        }

        var captionsTool = ToolExecutor.Execute("add_captions", new JsonObject { ["subtitleMediaRef"] = "F3-CAPTIONS" });
        Assert.True(captionsTool.IsError);
        Assert.Contains("has not allowed", Text(captionsTool), StringComparison.Ordinal);

        var session = ProjectHub.ActiveSession!;
        var (calls, error) = await Wp399Upload(session, "F3-IMAGE");
        Assert.Equal(0, calls);
        Assert.Contains("has not allowed", Assert.IsType<InvalidOperationException>(error).Message, StringComparison.Ordinal);

        var exportPath = Path.Combine(_tempDir, "f3-export.temno");
        var export = Export(new JsonObject { ["mode"] = "palmier", ["outputPath"] = exportPath });
        Assert.False(export.IsError, Text(export));
        var report = JsonNode.Parse(Text(export))!;
        Assert.Equal(2, report["notAllowed"]!.AsArray().Count);
        Assert.Empty(report["collected"]!.AsArray());
        Assert.False(Wp399TreeHasMarker(exportPath));

        var upscale = ToolExecutor.Execute("upscale_media", new JsonObject { ["mediaRef"] = "F3-IMAGE" });
        Assert.True(upscale.IsError);
        Assert.Contains("has not allowed", Text(upscale), StringComparison.Ordinal);
        Assert.DoesNotContain(session.Manifest.Entries, e => e.Name.EndsWith("(upscaled)", StringComparison.Ordinal));

        // The hosted image and media upload paths refuse before they touch their client (null here).
        var imageError = await Wp399Invoke("UploadImageAssetsAsync", session, null, new List<string> { "F3-IMAGE" }, CancellationToken.None);
        Assert.Contains("has not allowed", Assert.IsType<InvalidOperationException>(imageError).Message, StringComparison.Ordinal);
        var mediaError = await Wp399Invoke("UploadMediaAssetsAsync", session, null,
            new List<(string Role, string MediaRef, ClipType Type)> { ("image", "F3-IMAGE", ClipType.image) }, "wp399", CancellationToken.None);
        Assert.Contains("has not allowed", Assert.IsType<InvalidOperationException>(mediaError).Message, StringComparison.Ordinal);

        Assert.False(ToolExecutor.Execute("close_project", new JsonObject()).IsError);
        Assert.Equal(written, File.ReadAllBytes(manifestPath));
        Assert.True(File.Exists(captions));
    }

    [Fact]
    public async Task AllowedExternalFile_WorksForMcp_AndAJunctionSwappedInLaterBlocksItAgain()
    {
        var media = Path.Combine(_tempDir, "media-real");
        var secrets = Path.Combine(_tempDir, "secrets-real");
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(secrets);
        File.WriteAllText(Path.Combine(media, "clip.srt"), "1\n00:00:00,000 --> 00:00:01,000\nhello there\n");
        File.WriteAllText(Path.Combine(secrets, "clip.srt"), $"1\n00:00:00,000 --> 00:00:01,000\n{Wp399Marker}\n");
        var link = Path.Combine(_tempDir, "linked");
        MakeJunction(link, media);
        var viaLink = Path.Combine(link, "clip.srt");
        var package = Path.Combine(_projectsDir, "Linked.temno");
        ProjectPackage.Write(package, ProjectSession.CreateNew().ProjectFile, new MediaManifest
        {
            Entries = [new MediaManifestEntry { Id = "LINKED-CAPTIONS", Name = "clip", Type = ClipType.subtitle, Source = MediaSource.External(viaLink) }],
        });
        Assert.False(ToolExecutor.Execute("open_project", new JsonObject { ["path"] = package }).IsError);
        var session = ProjectHub.ActiveSession!;

        Assert.Equal(1, session.AllowExternalMedia([viaLink]));
        Assert.False(session.IsDirty);
        var allowed = ToolExecutor.Execute("inspect_media", new JsonObject { ["mediaRef"] = "LINKED-CAPTIONS" });
        Assert.Contains(allowed.Content, c => c.Text?.Contains("hello there", StringComparison.Ordinal) == true);
        Assert.Null(JsonNode.Parse(Text(allowed))!["externalAccess"]);
        // Re-opening the active package answers with its snapshot: nothing is blocked any more.
        Assert.Equal(0, JsonNode.Parse(Text(ToolExecutor.Execute("open_project", new JsonObject { ["path"] = package })))!["blockedExternalMedia"]!.GetValue<int>());

        Directory.Delete(link);
        _junctions.Remove(link);
        MakeJunction(link, secrets);

        var swapped = ToolExecutor.Execute("inspect_media", new JsonObject { ["mediaRef"] = "LINKED-CAPTIONS" });
        Assert.False(Wp399HasMarker(swapped));
        Assert.Equal("targetChanged", JsonNode.Parse(Text(swapped))!["externalAccess"]!.GetValue<string>());
        var captionsTool = ToolExecutor.Execute("add_captions", new JsonObject { ["subtitleMediaRef"] = "LINKED-CAPTIONS" });
        Assert.True(captionsTool.IsError);
        Assert.Contains("now leads somewhere other", Text(captionsTool), StringComparison.Ordinal);
        var (calls, error) = await Wp399Upload(session, "LINKED-CAPTIONS");
        Assert.Equal(0, calls);
        Assert.IsType<InvalidOperationException>(error);
        var exportPath = Path.Combine(_tempDir, "swap-export.temno");
        var export = Export(new JsonObject { ["mode"] = "palmier", ["outputPath"] = exportPath });
        Assert.Single(JsonNode.Parse(Text(export))!["notAllowed"]!.AsArray());
        Assert.False(Wp399TreeHasMarker(exportPath));
    }

    private static async Task<Exception?> Wp399Invoke(string method, params object?[] args)
    {
        var info = typeof(ToolExecutor).GetMethod(method,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        try
        {
            await (Task)info.Invoke(null, args)!;
            return null;
        }
        catch (Exception ex)
        {
            return ex is System.Reflection.TargetInvocationException { InnerException: { } inner } ? inner : ex;
        }
    }

    /// <summary>
    /// Blocked and link-redirected clips on a timeline, through the tools that hand back what a file shows
    /// or says: get_transcript and inspect_timeline, and the hosted upload helpers.
    /// </summary>
    [Fact]
    public async Task TimelineClips_BlockedOrRedirected_AreLeftOutOfTranscriptsFramesAndUploads()
    {
        if (!FfmpegFixture.IsAvailable) return;
        var media = Path.Combine(_tempDir, "tl-media");
        var secrets = Path.Combine(_tempDir, "tl-secrets");
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(secrets);
        await FfmpegFixture.WriteTestClipAsync(Path.Combine(media, "clip.mp4"), 30, 2, 160, 90);
        await FfmpegFixture.WriteTestClipAsync(Path.Combine(secrets, "clip.mp4"), 30, 2, 160, 90);
        var link = Path.Combine(_tempDir, "tl-linked");
        MakeJunction(link, media);
        var viaLink = Path.Combine(link, "clip.mp4");
        var blockedFile = Path.Combine(secrets, "clip.mp4");

        var project = ProjectSession.CreateNew();
        project.ActiveTimeline.Tracks[0].Clips.Add(new Clip { Id = "C-BLOCKED", MediaRef = "TL-BLOCKED", MediaType = ClipType.video, SourceClipType = ClipType.video, StartFrame = 0, DurationFrames = 30 });
        project.ActiveTimeline.Tracks[1].Clips.Add(new Clip { Id = "C-LINKED", MediaRef = "TL-LINKED", MediaType = ClipType.video, SourceClipType = ClipType.video, StartFrame = 30, DurationFrames = 30 });
        var package = Path.Combine(_projectsDir, "Timeline.temno");
        ProjectPackage.Write(package, project.ProjectFile, new MediaManifest
        {
            Entries =
            [
                new MediaManifestEntry { Id = "TL-BLOCKED", Name = "b", Type = ClipType.video, HasAudio = true, Duration = 2, Source = MediaSource.External(blockedFile) },
                new MediaManifestEntry { Id = "TL-LINKED", Name = "l", Type = ClipType.video, HasAudio = true, Duration = 2, Source = MediaSource.External(viaLink) },
            ],
        });
        Assert.False(ToolExecutor.Execute("open_project", new JsonObject { ["path"] = package }).IsError);
        var session = ProjectHub.ActiveSession!;
        Assert.Equal(1, session.AllowExternalMedia([viaLink]));

        static bool HasFrame(ToolCallResult r) => r.Content.Any(c => c.Type == "image");
        static string? SkipReason(ToolCallResult r, string clipId)
            => JsonNode.Parse(Text(r))?["skipped"]?.AsArray()
                .FirstOrDefault(s => s?["clipId"]?.GetValue<string>() == clipId)?["reason"]?.GetValue<string>();

        Assert.False(HasFrame(ToolExecutor.Execute("inspect_timeline", new JsonObject { ["startFrame"] = 0 })));
        Assert.True(HasFrame(ToolExecutor.Execute("inspect_timeline", new JsonObject { ["startFrame"] = 40 })));
        var transcript = ToolExecutor.Execute("get_transcript", new JsonObject());
        Assert.Equal("media is outside the project and not allowed", SkipReason(transcript, "C-BLOCKED"));

        Directory.Delete(link);
        _junctions.Remove(link);
        MakeJunction(link, secrets);

        Assert.False(HasFrame(ToolExecutor.Execute("inspect_timeline", new JsonObject { ["startFrame"] = 40 })));
        Assert.StartsWith("media is outside the project and now leads somewhere other",
            SkipReason(ToolExecutor.Execute("get_transcript", new JsonObject()), "C-LINKED"));
        var imageError = await Wp399Invoke("UploadImageAssetsAsync", session, null, new List<string> { "TL-LINKED" }, CancellationToken.None);
        Assert.Contains("now leads somewhere other", Assert.IsType<InvalidOperationException>(imageError).Message, StringComparison.Ordinal);
        var mediaError = await Wp399Invoke("UploadMediaAssetsAsync", session, null,
            new List<(string Role, string MediaRef, ClipType Type)> { ("video", "TL-LINKED", ClipType.video) }, "wp399", CancellationToken.None);
        Assert.Contains("now leads somewhere other", Assert.IsType<InvalidOperationException>(mediaError).Message, StringComparison.Ordinal);

        // The editor now lists the redirected file for review.
        var row = Assert.Single(session.ExternalMedia.Blocked(session.Manifest), b => b.MediaRef == "TL-LINKED");
        Assert.Equal(ExternalMediaConcern.TargetChanged, row.Concern);
    }

    /// <summary>
    /// WP-399 T2 round 1: settling an interrupted import probes its file, so an allowed external file that a
    /// junction now sends elsewhere is left as written on open; the same entry with the junction untouched
    /// settles, so the check is what keeps it.
    /// </summary>
    [Fact]
    public void InterruptedImport_RedirectedAfterAllow_IsLeftAsWritten()
    {
        var media = Path.Combine(_tempDir, "ii-media");
        var secrets = Path.Combine(_tempDir, "ii-secrets");
        Directory.CreateDirectory(media);
        Directory.CreateDirectory(secrets);
        File.WriteAllText(Path.Combine(media, "clip.srt"), "1\n00:00:00,000 --> 00:00:01,000\nhello\n");
        File.WriteAllText(Path.Combine(secrets, "clip.srt"), $"1\n00:00:00,000 --> 00:00:01,000\n{Wp399Marker}\n");
        var link = Path.Combine(_tempDir, "ii-linked");
        MakeJunction(link, media);
        var viaLink = Path.Combine(link, "clip.srt");

        string Package(string name)
        {
            var package = Path.Combine(_projectsDir, name + ".temno");
            ProjectPackage.Write(package, ProjectSession.CreateNew().ProjectFile, new MediaManifest
            {
                Entries =
                [
                    new MediaManifestEntry
                    {
                        Id = "PENDING-" + name, Name = "pending", Type = ClipType.subtitle, Source = MediaSource.External(viaLink),
                        GenerationStatus = ToolExecutor.ImportDownloadingStatus,
                        ImportInput = new MediaImportInput { SourceUrl = "https://media.example/clip.srt" },
                    },
                ],
            });
            Assert.True(ProjectSession.Open(package).ExternalMedia.Allow(viaLink));
            return package;
        }

        string? StatusAfterOpen(string package, string id)
        {
            Assert.False(ToolExecutor.Execute("open_project", new JsonObject { ["path"] = package }).IsError);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            string? status;
            do
            {
                status = ProjectHub.RunSerialized(() => ProjectHub.ActiveSession!.Manifest.Entries.Single(e => e.Id == id).GenerationStatus);
                if (status != ToolExecutor.ImportDownloadingStatus || ToolExecutor.IsImportRunning(id) is false && DateTime.UtcNow > deadline.AddSeconds(-28))
                    break;
                Thread.Sleep(50);
            } while (DateTime.UtcNow < deadline);
            Assert.False(ToolExecutor.Execute("close_project", new JsonObject()).IsError);
            return status;
        }

        var control = Package("Control");
        Assert.NotEqual(ToolExecutor.ImportDownloadingStatus, StatusAfterOpen(control, "PENDING-Control"));

        var redirected = Package("Redirected");
        var manifestPath = Path.Combine(redirected, ProjectConstants.ManifestFilename);
        var written = File.ReadAllBytes(manifestPath);
        Directory.Delete(link);
        _junctions.Remove(link);
        MakeJunction(link, secrets);

        Assert.Equal(ToolExecutor.ImportDownloadingStatus, StatusAfterOpen(redirected, "PENDING-Redirected"));
        Assert.False(ToolExecutor.IsImportRunning("PENDING-Redirected"));
        Assert.Equal(written, File.ReadAllBytes(manifestPath));
    }

    [Fact]
    public void ReferenceImport_AllowsExactlyThatPath()
    {
        var (package, captions, image) = Wp399ForeignPackage("ReferenceImport");
        Assert.False(ToolExecutor.Execute("open_project", new JsonObject { ["path"] = package }).IsError);

        var imported = ToolExecutor.Execute("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = captions, ["reference"] = true },
        });
        Assert.False(imported.IsError, Text(imported));

        // The project's own entry for the same path is now usable; the entry naming another path is not.
        var session = ProjectHub.ActiveSession!;
        Assert.Equal(captions, session.Resolver.ResolvePath("F3-CAPTIONS"));
        Assert.Null(session.Resolver.ResolvePath("F3-IMAGE"));
        Assert.Equal("notAllowed", JsonNode.Parse(Text(ToolExecutor.Execute("inspect_media", new JsonObject { ["mediaRef"] = "F3-IMAGE" })))!["externalAccess"]!.GetValue<string>());
        Assert.True(File.Exists(image));
    }

    [Fact]
    public async Task HttpClient_ForeignProjectExternalFiles_AreBlocked()
    {
        var (package, _, _) = Wp399ForeignPackage("HttpForeign");
        var port = FreeLoopbackPort();
        var origin = $"http://127.0.0.1:{port}";
        var server = new McpServer("127.0.0.1", port);
        server.Start();
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add(McpDefaults.AuthHeaderName, server.AuthToken);
        try
        {
            var open = await Tool(client, origin, 1, "open_project", new JsonObject { ["path"] = package });
            Assert.False(ToolFailed(open), ToolText(open));
            Assert.Equal(2, JsonNode.Parse(ToolText(open))!["blockedExternalMedia"]!.GetValue<int>());
            var inspect = await Tool(client, origin, 2, "inspect_media", new JsonObject { ["mediaRef"] = "F3-CAPTIONS" });
            Assert.False(ToolFailed(inspect), ToolText(inspect));
            Assert.DoesNotContain(Wp399Marker, inspect.ToJsonString(), StringComparison.Ordinal);
            Assert.Equal("notAllowed", JsonNode.Parse(ToolText(inspect))!["externalAccess"]!.GetValue<string>());
            var export = await Tool(client, origin, 3, "export_project", new JsonObject
            {
                ["mode"] = "palmier",
                ["outputPath"] = Path.Combine(_tempDir, "http-export.temno"),
            });
            Assert.False(ToolFailed(export), ToolText(export));
            Assert.Equal(2, JsonNode.Parse(ToolText(export))!["notAllowed"]!.AsArray().Count);
            Assert.False(Wp399TreeHasMarker(Path.Combine(_tempDir, "http-export.temno")));
        }
        finally
        {
            server.Stop();
        }
    }

    private sealed class Wp399RecordingClient : Temno.Generation.IGenerationClient
    {
        public int Calls { get; private set; }

        public Task<string> UploadReferenceAsync(string filePath, string contentType, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult("https://fixture.invalid/upload");
        }

        public Task<List<Temno.Generation.CatalogEntry>> ListModelsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string> SubmitGenerationAsync(string model, Temno.Generation.BackendGenerationParams parameters,
            string? projectId = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Temno.Generation.BackendGenerationJob?> GetJobStatusAsync(string jobId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static async Task<JsonNode> Rpc(HttpClient client, string origin, string method, int id, JsonObject? parameters)
    {
        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
            payload["params"] = parameters;
        using var response = await client.PostAsync(
            origin + "/mcp",
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static Task<JsonNode> Tool(HttpClient client, string origin, int id, string name, JsonObject arguments)
        => Rpc(client, origin, "tools/call", id, new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments,
        });

    private static bool ToolFailed(JsonNode response)
        => response["result"]?["isError"]?.GetValue<bool>() == true;

    private static string ToolText(JsonNode response)
        => response["result"]?["content"]?[0]?["text"]?.GetValue<string>() ?? response.ToJsonString();

    private static int FreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    private static List<Effect> Effects(ProjectSession session)
        => Assert.IsType<List<Effect>>(session.ActiveTimeline.Tracks[0].Clips[0].Effects);

    private static ProjectSession StartProject(string name)
    {
        ProjectHub.ActiveSession = null;
        var created = ToolExecutor.Execute("new_project", new JsonObject { ["name"] = name });
        Assert.False(created.IsError, created.Content[0].Text);
        return ProjectHub.ActiveSession!;
    }

    private static ToolCallResult Export(JsonObject args) => ToolExecutor.Execute("export_project", args);

    private static string Text(ToolCallResult result) => result.Content[0].Text ?? "";

    private static string OutputPath(ToolCallResult result)
    {
        Assert.False(result.IsError, Text(result));
        return JsonNode.Parse(Text(result))!["outputPath"]!.GetValue<string>();
    }

    /// <summary>A minimal project package: project.json, an empty media folder and a sentinel.</summary>
    private static string MakePackage(string path, string sentinel)
    {
        ProjectSession.CreateNew().Save(path);
        File.WriteAllText(Path.Combine(path, "sentinel.txt"), sentinel);
        return path;
    }

    /// <summary><c>\\127.0.0.1\C$\…</c> for a local drive path, or null for any other path.</summary>
    private static string? LoopbackShareAlias(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is not { Length: 3 } || root[1] != ':')
            return null;
        return $@"\\127.0.0.1\{char.ToUpperInvariant(root[0])}$\{path[root.Length..]}";
    }

    private void MakeJunction(string link, string target)
    {
        var start = new ProcessStartInfo
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
        using var process = Process.Start(start)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
            throw new TimeoutException("mklink /J timed out.");
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"mklink /J failed ({process.ExitCode}): {stderr} {stdout}");
        _junctions.Add(link);
    }
}
