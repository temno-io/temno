using System.Diagnostics;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Playback;
using Xunit;

namespace Temno.Core.Tests;

/// <summary>
/// WP-177 acceptance: adding a timeline during an export of a nested project must not kill the job.
/// This drives the real <see cref="TimelineExporter"/> — the same type the Export dialog and MCP
/// <c>export_project</c> construct — against a real <see cref="ProjectSession"/>, while the command
/// behind the New Timeline button runs on another thread.
/// </summary>
public sealed class NestedExportDuringEditTests
{
    private const int ExportFrames = 240;
    /// <summary>Hard cap; the editor actually stops when the export does.</summary>
    private const int MaxEditsDuringExport = 500;

    /// <summary>
    /// The nested timeline sits last. A lookup that finds its target at index 1 enumerates two
    /// entries and is almost impossible to interrupt; a real project's sequence is somewhere in a
    /// list the user has been adding to, which is the window the WP is about.
    /// </summary>
    private const int TimelinesBeforeTheNestedOne = 48;

    [Fact]
    public async Task NestedExportSurvivesNewTimelineMidJob()
    {
        if (!IsFfmpegAvailable()) return;

        var directory = CreateTempDirectory();
        try
        {
            var session = NestedProject(out var manifest);
            var output = Path.Combine(directory, "nested-during-edit.mp4");

            using var exporter = new TimelineExporter(
                new CpuCompositor(),
                new SolidFrameSource(),
                new MediaResolver(() => manifest, () => null),
                timelineLookup: SessionTimelineLookup.For(session));

            using var editing = new CancellationTokenSource();
            var editor = AddTimelinesAsync(session, editing.Token);
            try
            {
                await exporter.ExportAsync(session.ActiveTimeline, new ExportPreset("nested"), output);
            }
            finally
            {
                editing.Cancel();
            }
            var edits = await editor;

            Assert.True(File.Exists(output), "The export job died before it wrote a file.");
            Assert.True(new FileInfo(output).Length > 0, "The export wrote an empty file.");
            var probe = await new FfmpegMediaProbe().ProbeAsync(output);
            Assert.True(probe.HasVideo, "The export produced no video stream.");
            // Not vacuous: timelines really did land in the shared list while the render read it.
            Assert.True(edits > 0, "No timeline was added during the export.");
            Assert.Equal(2 + TimelinesBeforeTheNestedOne + edits, session.ProjectFile.Timelines.Count);
            Console.WriteLine($"WP-177 export receipt: {ExportFrames} frames exported while {edits} timelines were added.");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>
    /// The same export against the shape being replaced. Reported, not asserted: the outcome is
    /// timing-dependent, and asserting a race would trade one flaky direction for another. The
    /// deterministic control lives in <c>SessionTimelineLookupTests</c>.
    /// </summary>
    [Fact]
    public async Task RawLookupExportLosesTheSameRace()
    {
        if (!IsFfmpegAvailable()) return;

        var directory = CreateTempDirectory();
        try
        {
            var session = NestedProject(out var manifest);
            var output = Path.Combine(directory, "nested-during-edit-raw.mp4");

            using var exporter = new TimelineExporter(
                new CpuCompositor(),
                new SolidFrameSource(),
                new MediaResolver(() => manifest, () => null),
                timelineLookup: id => session.ProjectFile.Timelines.FirstOrDefault(t => t.Id == id));

            using var editing = new CancellationTokenSource();
            var editor = AddTimelinesAsync(session, editing.Token);
            Exception? died = null;
            try
            {
                await exporter.ExportAsync(session.ActiveTimeline, new ExportPreset("nested"), output);
            }
            catch (Exception ex)
            {
                died = ex;
            }
            editing.Cancel();
            var edits = await editor;

            Console.WriteLine(died is null
                ? $"WP-177 export control: the raw lookup survived {edits} timeline additions on this machine."
                : $"WP-177 export control: the export job died after {edits} additions - {died.GetType().Name}: {died.Message}");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    /// <summary>A parent whose only clip is a sequence carrier, so every frame hits the lookup.</summary>
    private static ProjectSession NestedProject(out MediaManifest manifest)
    {
        var still = Path.Combine(Path.GetTempPath(), "temno-wp177-still.png");
        manifest = new MediaManifest
        {
            Entries = [new MediaManifestEntry { Id = "pic", Type = ClipType.video, Source = MediaSource.External(still) }],
        };

        var nested = new Timeline
        {
            Id = "T-NEST",
            Name = "Nested",
            Fps = 30,
            Width = 64,
            Height = 64,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            Id = "nested-pic", MediaRef = "pic",
                            MediaType = ClipType.video, SourceClipType = ClipType.video,
                            StartFrame = 0, DurationFrames = ExportFrames,
                        },
                    ],
                },
            ],
        };

        var parent = new Timeline
        {
            Id = "T-MAIN",
            Name = "Main",
            Fps = 30,
            Width = 64,
            Height = 64,
            Tracks =
            [
                new Track
                {
                    Type = ClipType.video,
                    Clips =
                    [
                        new Clip
                        {
                            Id = "carrier", MediaRef = nested.Id,
                            MediaType = ClipType.sequence, SourceClipType = ClipType.sequence,
                            StartFrame = 0, DurationFrames = ExportFrames,
                        },
                    ],
                },
            ],
        };

        var timelines = new List<Timeline> { parent };
        for (var i = 0; i < TimelinesBeforeTheNestedOne; i++)
            timelines.Add(new Timeline { Name = $"Timeline {i}", Fps = 30, Width = 64, Height = 64 });
        timelines.Add(nested);

        var file = new ProjectFile
        {
            Timelines = timelines,
            ActiveTimelineId = parent.Id,
            OpenTimelineIds = [parent.Id],
        };
        return new ProjectSession(file);
    }

    /// <summary>
    /// What the New Timeline button does, on the thread the export is not running on, for as long
    /// as the export runs. A fixed count finishes during ffmpeg's startup and never overlaps the
    /// render at all — which is how the first version of this test passed against both shapes.
    /// </summary>
    private static Task<int> AddTimelinesAsync(ProjectSession session, CancellationToken stopping)
        => Task.Run(() =>
        {
            var added = 0;
            while (!stopping.IsCancellationRequested && added < MaxEditsDuringExport)
            {
                TimelineCommands.AddTimeline(session, $"New Timeline {added}", null, out _);
                added++;
            }
            return added;
        });

    private static bool IsFfmpegAvailable()
    {
        try
        {
            foreach (var tool in new[] { FFmpegResolver.GetFFmpegPath(), FFmpegResolver.GetFFprobePath() })
            {
                using var process = Process.Start(new ProcessStartInfo(tool, "-version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                process?.WaitForExit();
                if (process?.ExitCode != 0) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"temno_wp177_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); }
        catch { }
    }

    private sealed class SolidFrameSource : IFrameSource
    {
        public Task<VideoFrame> GetFrameAsync(string mediaPath, double seconds, CancellationToken ct = default)
        {
            var pixels = new byte[64 * 64 * 4];
            for (var index = 0; index < pixels.Length; index += 4)
            {
                pixels[index] = 32;
                pixels[index + 1] = 64;
                pixels[index + 2] = 96;
                pixels[index + 3] = 255;
            }
            return Task.FromResult(new VideoFrame(64, 64, pixels, "rgba"));
        }
    }
}
