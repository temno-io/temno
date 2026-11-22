using System.Text;
using System.Text.Json.Nodes;
using Temno.Core.Session;
using Temno.Mcp;
using Temno.Media.Export;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-403. export_project judged <c>overwrite: false</c> (and an omitted outputPath, judged the same way
/// by WP-398) when the call was made, then published a queued render minutes later with a replacing
/// move. A file that reached the destination in between was replaced. Now the intent travels with the
/// job, the export checks again before it renders, and the publish refuses through the file system's own
/// move or copy. The file modes also refuse a destination a pending video export holds.
/// </summary>
[Collection("EnvRegistry")]
public sealed class ExportPublishOverwriteTests : IDisposable
{
    private const int Fps = 30;
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(2);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "temno-wp403-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A folder on another volume than the temp folder the exporter renders into, so the publish crosses
    /// volumes; null when this machine has one volume.
    /// </summary>
    private readonly string? _otherVolume;

    public ExportPublishOverwriteTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", Path.Combine(_root, "Projects"));
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", Path.Combine(_root, "registry.json"));
        Environment.SetEnvironmentVariable("TEMNO_DOWNLOADS_DIR", Path.Combine(_root, "Downloads"));

        var candidate = Path.Combine(AppContext.BaseDirectory, "temno-wp403-" + Guid.NewGuid().ToString("N"));
        if (!string.Equals(Path.GetPathRoot(candidate), Path.GetPathRoot(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(candidate);
            _otherVolume = candidate;
        }
    }

    public void Dispose()
    {
        ProjectHub.ActiveSession = null;
        Environment.SetEnvironmentVariable("TEMNO_PROJECTS_DIR", null);
        Environment.SetEnvironmentVariable("TEMNO_REGISTRY_PATH", null);
        Environment.SetEnvironmentVariable("TEMNO_DOWNLOADS_DIR", null);
        foreach (var dir in new[] { _root, _otherVolume })
        {
            if (dir is null) continue;
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    // ---- the publish step, no render ---------------------------------------------------------------

    [Fact]
    public void Publish_SameVolume_OverwriteFalse_KeepsAFileThatIsThere()
    {
        var (temp, output) = PublishPair(_root);
        var sentinel = WriteSentinel(output);

        var error = Assert.Throws<IOException>(() => TimelineExporter.PublishResult(temp, output, overwrite: false));

        Assert.Contains(output, error.Message, StringComparison.Ordinal);
        Assert.Contains("overwrite=false", error.Message, StringComparison.Ordinal);
        Assert.Equal(sentinel, File.ReadAllBytes(output));
        AssertNoStaging(_root);
    }

    [Fact]
    public void Publish_SameVolume_OverwriteTrue_Replaces_AndOverwriteFalse_PublishesWhenFree()
    {
        var (temp, output) = PublishPair(_root);
        WriteSentinel(output);
        TimelineExporter.PublishResult(temp, output, overwrite: true);
        Assert.Equal(RenderBytes, File.ReadAllBytes(output));
        Assert.False(File.Exists(temp));

        var (freeTemp, freeOutput) = PublishPair(_root);
        TimelineExporter.PublishResult(freeTemp, freeOutput, overwrite: false);
        Assert.Equal(RenderBytes, File.ReadAllBytes(freeOutput));
        Assert.False(File.Exists(freeTemp));
    }

    [Fact]
    public void Publish_OverwriteFalse_KeepsAFolderOrALinkThatIsThere()
    {
        var (temp, folder) = PublishPair(_root);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "INSIDE");
        var error = Assert.Throws<IOException>(() => TimelineExporter.PublishResult(temp, folder, overwrite: false));
        Assert.Contains(folder, error.Message, StringComparison.Ordinal);
        Assert.Equal("INSIDE", File.ReadAllText(Path.Combine(folder, "inside.txt")));

        var target = Path.Combine(_root, "link-target.txt");
        File.WriteAllText(target, "TARGET");
        var (linkTemp, link) = PublishPair(_root);
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.False(File.Exists(link), "A link was created but the platform was treated as unable to: " + ex.Message);
            return;
        }

        Assert.Throws<IOException>(() => TimelineExporter.PublishResult(linkTemp, link, overwrite: false));
        Assert.Equal(target, new FileInfo(link).LinkTarget);
        Assert.Equal("TARGET", File.ReadAllText(target));
        AssertNoStaging(_root);
    }

    /// <summary>
    /// T1 lead, reproduced: Windows' move across volumes copies, and its copy follows a dangling link at
    /// the destination and writes the render at the link's target. The publish stages beside the
    /// destination and renames, and a rename refuses the link without following it.
    /// </summary>
    [Fact]
    public void Publish_OverwriteFalse_NeverWritesThroughADanglingLink_OnEitherVolume()
    {
        foreach (var folder in OutputFolders())
        {
            var (temp, link) = PublishPair(_root, outputFolder: folder);
            var target = Path.Combine(folder, $"gone-{Guid.NewGuid():N}.mp4");
            if (!TryLink(link, target)) return;

            var error = Assert.Throws<IOException>(() => TimelineExporter.PublishResult(temp, link, overwrite: false));

            Assert.Contains(link, error.Message, StringComparison.Ordinal);
            Assert.Equal(target, new FileInfo(link).LinkTarget);
            Assert.False(File.Exists(target), $"the render was written through the link in {folder}");
            AssertNoStaging(folder);
        }
    }

    /// <summary>
    /// Temp on one volume, the destination on another: Windows' move copies across volumes itself, and
    /// without the replace flag it refuses an existing destination as a rename does.
    /// </summary>
    [Fact]
    public void Publish_CrossVolume_OverwriteFalse_KeepsAFile_AndOverwriteTrue_Replaces()
    {
        if (_otherVolume is null) return;

        var (temp, output) = PublishPair(_root, outputFolder: _otherVolume);
        var sentinel = WriteSentinel(output);
        var error = Assert.Throws<IOException>(() => TimelineExporter.PublishResult(temp, output, overwrite: false));
        Assert.Contains(output, error.Message, StringComparison.Ordinal);
        Assert.Equal(sentinel, File.ReadAllBytes(output));
        AssertNoStaging(_otherVolume);

        File.WriteAllBytes(temp, RenderBytes);
        TimelineExporter.PublishResult(temp, output, overwrite: true);
        Assert.Equal(RenderBytes, File.ReadAllBytes(output));
        Assert.False(File.Exists(temp));
    }

    /// <summary>
    /// The exporter's own fallback, when moving the render fails (a move that cannot cross volumes). A
    /// file that arrives after that failure and before the copy is kept with overwrite=false and replaced
    /// with true; with false the copy goes to a staging name beside it and the rename refuses.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Publish_CopyFallback_JudgesOverwriteOnWhatArrivesBeforeTheCopy(bool overwrite)
    {
        var (temp, output) = PublishPair(_root);
        byte[]? sentinel = null;
        var copied = 0;
        void Copy(string source, string destination, bool replace)
        {
            copied++;
            sentinel = WriteSentinel(output);
            File.Copy(source, destination, replace);
        }

        if (overwrite)
        {
            TimelineExporter.PublishResult(temp, output, overwrite, MoveFailsForTheRender(temp), Copy);
            Assert.Equal(RenderBytes, File.ReadAllBytes(output));
        }
        else
        {
            var error = Assert.Throws<IOException>(
                () => TimelineExporter.PublishResult(temp, output, overwrite, MoveFailsForTheRender(temp), Copy));
            Assert.Contains(output, error.Message, StringComparison.Ordinal);
            Assert.Equal(sentinel, File.ReadAllBytes(output));
        }

        Assert.Equal(1, copied);
        Assert.False(File.Exists(temp));
        AssertNoStaging(_root);
    }

    [Fact]
    public void Publish_CopyFallback_OverwriteFalse_NeverWritesThroughADanglingLink()
    {
        var (temp, link) = PublishPair(_root);
        var target = Path.Combine(_root, $"gone-{Guid.NewGuid():N}.mp4");
        var linked = false;
        void Copy(string source, string destination, bool replace)
        {
            linked = TryLink(link, target);
            File.Copy(source, destination, replace);
        }

        var thrown = Record.Exception(
            () => TimelineExporter.PublishResult(temp, link, overwrite: false, MoveFailsForTheRender(temp), Copy));
        if (!linked) return;
        Assert.IsType<IOException>(thrown);
        Assert.Equal(target, new FileInfo(link).LinkTarget);
        Assert.False(File.Exists(target));
        AssertNoStaging(_root);
    }

    [Fact]
    public void Publish_CopyFallback_OverwriteFalse_PublishesWhenFree()
    {
        var (temp, output) = PublishPair(_root);
        TimelineExporter.PublishResult(temp, output, overwrite: false, MoveFailsForTheRender(temp), File.Copy);
        Assert.Equal(RenderBytes, File.ReadAllBytes(output));
        Assert.False(File.Exists(temp));
        AssertNoStaging(_root);
    }

    // ---- export_project over the in-process MCP --------------------------------------------------

    [Fact]
    public async Task Mcp_OverwriteFalse_AFileThatArrivesWhileRendering_FailsTheExport_AndIsKept()
    {
        if (!FfmpegFixture.IsAvailable) return;
        await StillProjectAsync();

        foreach (var folder in OutputFolders())
        {
            var output = Path.Combine(folder, $"overwrite-false-{Guid.NewGuid():N}.mp4");
            var args = VideoArgs(output);
            args["overwrite"] = false;
            var (row, sentinel) = RunWithAFileArriving(args, output, whileRendering: true);

            Assert.Equal("failed", row["status"]!.GetValue<string>());
            var error = row["error"]!.GetValue<string>();
            Assert.Contains(output, error, StringComparison.Ordinal);
            Assert.Contains("while the export rendered", error, StringComparison.Ordinal);
            Assert.Equal(sentinel, File.ReadAllBytes(output));
        }
    }

    [Fact]
    public async Task Mcp_OmittedPath_AFileThatArrivesWhileRendering_FailsTheExport_AndIsKept()
    {
        if (!FfmpegFixture.IsAvailable) return;
        await StillProjectAsync();

        var args = VideoArgs(outputPath: null);
        var (row, sentinel) = RunWithAFileArriving(args, output: null, whileRendering: true);

        var output = row["path"]!.GetValue<string>();
        Assert.StartsWith(Path.Combine(_root, "Downloads"), output, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("failed", row["status"]!.GetValue<string>());
        Assert.Contains(output, row["error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(sentinel, File.ReadAllBytes(output));
    }

    [Fact]
    public async Task Mcp_OverwriteTrue_AFileThatArrivesWhileRendering_IsReplaced()
    {
        if (!FfmpegFixture.IsAvailable) return;
        await StillProjectAsync();

        foreach (var folder in OutputFolders())
        {
            var output = Path.Combine(folder, $"overwrite-true-{Guid.NewGuid():N}.mp4");
            var args = VideoArgs(output);
            args["overwrite"] = true;
            var (row, sentinel) = RunWithAFileArriving(args, output, whileRendering: true);

            Assert.Equal("completed", row["status"]!.GetValue<string>());
            var written = File.ReadAllBytes(output);
            Assert.NotEqual(sentinel, written);
            Assert.Equal("ftyp", Encoding.ASCII.GetString(written, 4, 4));
        }
    }

    [Fact]
    public async Task Mcp_OverwriteFalse_AFileThatArrivesWhileQueued_FailsBeforeRendering()
    {
        if (!FfmpegFixture.IsAvailable) return;
        await StillProjectAsync();

        var output = Path.Combine(_root, "queued.mp4");
        var args = VideoArgs(output);
        args["overwrite"] = false;
        var (row, sentinel) = RunWithAFileArriving(args, output, whileRendering: false);

        Assert.Equal("failed", row["status"]!.GetValue<string>());
        var error = row["error"]!.GetValue<string>();
        Assert.Contains(output, error, StringComparison.Ordinal);
        Assert.Contains("nothing was rendered", error, StringComparison.Ordinal);
        Assert.Null(row["frames"]);
        Assert.Equal(sentinel, File.ReadAllBytes(output));
    }

    /// <summary>A blocking call (background false) is judged the same way when the publish refuses.</summary>
    [Fact]
    public async Task Mcp_Blocking_OverwriteFalse_ReportsTheRefusal()
    {
        if (!FfmpegFixture.IsAvailable) return;
        await StillProjectAsync();

        var output = Path.Combine(_root, "blocking.mp4");
        var args = VideoArgs(output);
        args["overwrite"] = false;
        args["background"] = false;
        byte[]? sentinel = null;
        void OnChanged(ExportJob job)
        {
            if (sentinel is null && job.OutputPath == output && job.CurrentFrame >= 1)
                sentinel = WriteSentinel(output);
        }

        ToolExecutor.ExportQueue.JobChanged += OnChanged;
        ToolCallResult result;
        try
        {
            result = ToolExecutor.Execute("export_project", args);
        }
        finally
        {
            ToolExecutor.ExportQueue.JobChanged -= OnChanged;
        }

        Assert.True(result.IsError, result.Content[0].Text);
        Assert.Contains(output, result.Content[0].Text!, StringComparison.Ordinal);
        Assert.Equal(sentinel, File.ReadAllBytes(output));
    }

    [Theory]
    [InlineData("xml", true)]
    [InlineData("xml", false)]
    [InlineData("fcpxml", true)]
    public async Task Mcp_FileModes_RefuseAPendingVideoExportsDestination_WhateverOverwriteSays(string mode, bool overwrite)
    {
        if (!FfmpegFixture.IsAvailable) return;
        await StillProjectAsync();

        var output = Path.Combine(_root, $"held-{mode}-{overwrite}.mp4");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateJob = BlockTheQueue(gate);
        try
        {
            var args = VideoArgs(output);
            args["background"] = true;
            var queued = Call("export_project", args);
            var jobId = queued["jobId"]!.GetValue<string>();

            var refused = ToolExecutor.Execute("export_project", new JsonObject
            {
                ["mode"] = mode,
                ["outputPath"] = output,
                ["overwrite"] = overwrite,
            });
            Assert.True(refused.IsError, refused.Content[0].Text);
            Assert.Contains("video export that is waiting or in progress", refused.Content[0].Text!, StringComparison.Ordinal);
            Assert.Contains(output, refused.Content[0].Text!, StringComparison.Ordinal);
            Assert.False(File.Exists(output));

            Assert.True(ToolExecutor.ExportQueue.Cancel(jobId));
        }
        finally
        {
            gate.TrySetResult();
            await WaitUntilFinished(gateJob);
        }

        // Once nothing holds it, the file mode writes there as before.
        var written = Call("export_project", new JsonObject { ["mode"] = mode, ["outputPath"] = output });
        Assert.Equal("success", written["status"]!.GetValue<string>());
        Assert.True(File.Exists(output));
    }

    /// <summary>
    /// T1 finding: the refusal compares destinations, not spellings. An extended-length path, a link to
    /// the folder and a change of case name the pending video's file too.
    /// </summary>
    [Fact]
    public async Task Mcp_FileModes_RefuseAPendingVideoExportsDestination_UnderAnotherSpelling()
    {
        if (!FfmpegFixture.IsAvailable) return;
        await StillProjectAsync();

        var output = Path.Combine(_root, "held-alias.mp4");
        var junction = Path.Combine(Path.GetTempPath(), "temno-wp403-alias-" + Guid.NewGuid().ToString("N"));
        var spellings = new List<(string Mode, string Path)>
        {
            ("xml", @"\\?\" + output),
            ("fcpxml", @"\\?\" + output),
            ("xml", Path.Combine(_root, "HELD-ALIAS.MP4")),
        };
        try
        {
            Directory.CreateSymbolicLink(junction, _root);
            spellings.Add(("xml", Path.Combine(junction, "held-alias.mp4")));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // No Developer Mode: the other spellings still run.
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateJob = BlockTheQueue(gate);
        try
        {
            var args = VideoArgs(output);
            args["background"] = true;
            var jobId = Call("export_project", args)["jobId"]!.GetValue<string>();

            foreach (var (mode, alias) in spellings)
            {
                var refused = ToolExecutor.Execute("export_project", new JsonObject
                {
                    ["mode"] = mode,
                    ["outputPath"] = alias,
                    ["overwrite"] = true,
                });
                Assert.True(refused.IsError, $"{mode} {alias}: {refused.Content[0].Text}");
                Assert.Contains("video export that is waiting or in progress", refused.Content[0].Text!, StringComparison.Ordinal);
                Assert.False(File.Exists(output), $"{mode} {alias} wrote the file");
            }

            Assert.True(ToolExecutor.ExportQueue.Cancel(jobId));
        }
        finally
        {
            gate.TrySetResult();
            await WaitUntilFinished(gateJob);
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static readonly byte[] RenderBytes = Encoding.ASCII.GetBytes("WP-403 finished render");

    private (string Temp, string Output) PublishPair(string tempFolder, string? outputFolder = null)
    {
        var temp = Path.Combine(tempFolder, $"render-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(temp, RenderBytes);
        return (temp, Path.Combine(outputFolder ?? tempFolder, $"out-{Guid.NewGuid():N}.mp4"));
    }

    private static Action<string, string, bool> MoveFailsForTheRender(string temp) => (source, destination, replace) =>
    {
        if (string.Equals(source, temp, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The system cannot move the file to a different disk drive.");
        File.Move(source, destination, replace);
    };

    private static void AssertNoStaging(string folder)
        => Assert.Empty(Directory.GetFiles(folder, "*.partial*", SearchOption.TopDirectoryOnly));

    /// <summary>Creates a file symbolic link, or returns false when this machine may not (no Developer Mode).</summary>
    private static bool TryLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.False(File.Exists(link), "A link was created but the platform was treated as unable to: " + ex.Message);
            return false;
        }
    }

    private static byte[] WriteSentinel(string path)
    {
        var bytes = Encoding.ASCII.GetBytes($"WP-403 sentinel {Guid.NewGuid():N}");
        File.WriteAllBytes(path, bytes);
        return bytes;
    }

    private IEnumerable<string> OutputFolders()
    {
        yield return _root;
        if (_otherVolume is not null) yield return _otherVolume;
    }

    /// <summary>
    /// Holds the shared export queue so the next job stays waiting until <paramref name="gate"/> opens.
    /// Its own destination is a path nothing else uses.
    /// </summary>
    private ExportJob BlockTheQueue(TaskCompletionSource gate)
        => ToolExecutor.ExportQueue.Enqueue(
            "wp403-gate",
            Path.Combine(_root, $"gate-{Guid.NewGuid():N}.bin"),
            async (_, cancellationToken) => await gate.Task.WaitAsync(cancellationToken));

    /// <summary>
    /// Queues the export behind a gate, then puts a file at its destination either while it waits or at
    /// its first rendered frame (the progress event runs on the render thread, so the file is there
    /// before the publish). Returns the finished manage_exports row and the file's bytes.
    /// </summary>
    private (JsonObject Row, byte[] Sentinel) RunWithAFileArriving(JsonObject args, string? output, bool whileRendering)
    {
        args["background"] = true;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateJob = BlockTheQueue(gate);
        byte[]? sentinel = null;
        string? jobId = null;
        string? destination = output;
        void OnChanged(ExportJob job)
        {
            if (whileRendering && sentinel is null && job.Id == jobId && job.CurrentFrame >= 1)
                sentinel = WriteSentinel(destination!);
        }

        ToolExecutor.ExportQueue.JobChanged += OnChanged;
        try
        {
            var queued = Call("export_project", args);
            Assert.Equal("queued", queued["status"]!.GetValue<string>());
            destination = queued["outputPath"]!.GetValue<string>();
            Assert.False(File.Exists(destination));
            jobId = queued["jobId"]!.GetValue<string>();
            if (!whileRendering)
                sentinel = WriteSentinel(destination);
            gate.TrySetResult();
            var row = WaitForRow(jobId);
            Assert.NotNull(sentinel);
            return (row, sentinel!);
        }
        finally
        {
            ToolExecutor.ExportQueue.JobChanged -= OnChanged;
            gate.TrySetResult();
            WaitUntilFinished(gateJob).GetAwaiter().GetResult();
        }
    }

    private static async Task WaitUntilFinished(ExportJob job)
    {
        var until = DateTime.UtcNow + Deadline;
        while (!job.State.IsFinished())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"job {job.Id} did not finish");
            await Task.Delay(20);
        }
    }

    /// <summary>A 320x180 project holding one still image for a second: a still is never stream-copied,
    /// so the render reports every frame.</summary>
    private async Task StillProjectAsync()
    {
        Call("new_project", new JsonObject { ["name"] = "Wp403 " + Guid.NewGuid().ToString("N")[..6], ["fps"] = Fps });
        Call("set_project_settings", new JsonObject { ["width"] = 320, ["height"] = 180 });
        var still = Path.Combine(_root, $"still-{Guid.NewGuid():N}.png");
        await FfmpegFixture.RunAsync($"-hide_banner -loglevel error -y -f lavfi -i color=c=blue:s=320x180 -frames:v 1 \"{still}\"");
        var imported = Call("import_media", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = still, ["reference"] = true },
            ["name"] = "still",
        });
        Call("add_clips", new JsonObject
        {
            ["entries"] = new JsonArray
            {
                new JsonObject { ["mediaRef"] = imported["mediaRef"]!.GetValue<string>(), ["startFrame"] = 0, ["endFrame"] = Fps },
            },
        });
        Assert.Equal(Fps, ProjectHub.ActiveSession!.ActiveTimeline.TotalFrames);
    }

    private static JsonObject VideoArgs(string? outputPath)
    {
        var args = new JsonObject
        {
            ["mode"] = "video",
            ["codec"] = "H.264",
            ["resolution"] = "Match Timeline",
            ["preferHardware"] = false,
        };
        if (outputPath is not null) args["outputPath"] = outputPath;
        return args;
    }

    private static JsonObject WaitForRow(string jobId)
    {
        var until = DateTime.UtcNow + Deadline;
        while (DateTime.UtcNow < until)
        {
            var row = Call("manage_exports", new JsonObject { ["action"] = "list" })["exports"]!.AsArray()
                .Single(r => r!["jobId"]!.GetValue<string>() == jobId)!.AsObject();
            if (row["status"]!.GetValue<string>() is "completed" or "failed" or "canceled") return row;
            Thread.Sleep(50);
        }
        throw new TimeoutException($"export {jobId} did not finish");
    }

    private static JsonObject Call(string tool, JsonObject args)
    {
        var result = ToolExecutor.Execute(tool, args);
        Assert.False(result.IsError, result.Content[0].Text);
        return JsonNode.Parse(result.Content[0].Text!)!.AsObject();
    }
}
