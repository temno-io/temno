using System.Diagnostics;
using System.Security.Cryptography;
using Temno.Core.Models;
using Temno.Media;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Xunit;
using Xunit.Abstractions;

namespace Temno.Core.Tests;

/// <summary>
/// WP-216. The export decode transport.
/// <para>
/// <see cref="ExportFrameSource"/> reads decoded frames out of ffmpeg through a named pipe it
/// sizes itself rather than through the redirected stdout pipe. The stdout pipe never returns more
/// than 32 KiB per read, so a 3840x2160 RGBA frame costs 1,013 read transitions; the sized pipe
/// costs about 93. Measured 90.5 to 20.2 ms/frame at 4K — <c>docs/verification/wp-216/DECISION.md</c>.
/// </para>
/// <para>
/// The transport is a <i>handle</i> decision, not a pixel decision: the ffmpeg command is unchanged
/// and only where its output lands differs. So the bar here is <b>byte-identity</b>, not a
/// tolerance (DECISION.md S1.1), and <see cref="SizedTransport_IsByteIdenticalToStdout"/> is the
/// fact the rest of the package rests on.
/// </para>
/// <para>
/// These tests mutate process environment, so they are serialized with the other env-mutating
/// suites rather than racing them.
/// </para>
/// </summary>
[Collection("EnvRegistry")]
public sealed class ExportTransportTests
{
    private const int Fps = 30;
    private const int ClipSeconds = 2;
    // 960x540 RGBA is 2,073,600 bytes per frame, so every frame in these tests is *larger* than
    // the 1 MiB pipe buffer and has to wrap it at least twice. At the previous 640x360 (921,600
    // bytes) a whole frame fitted inside one buffer, so the CI byte-identity test never exercised
    // the boundary the transport exists to cross. WP-216 T2 observed the gap; this closes it.
    private const int Width = 960;
    private const int Height = 540;

    private readonly ITestOutputHelper _output;

    public ExportTransportTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The fact the package rests on: changing the transport must not change a single byte.
    /// A tolerance would be the wrong bar — the same ffmpeg process writes the same rawvideo
    /// either way, so any difference at all means the transport is losing or reordering data.
    /// </summary>
    [Fact]
    public async Task SizedTransport_IsByteIdenticalToStdout()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var clip = Path.Combine(directory, "clip.mp4");
            await FfmpegFixture.WriteTestClipAsync(clip, Fps, ClipSeconds, Width, Height);

            var viaStdout = await HashFramesAsync(clip, "stdout");
            var viaNamed = await HashFramesAsync(clip, "named");

            Assert.Equal(viaStdout.Count, viaNamed.Count);
            Assert.NotEmpty(viaStdout);
            for (var i = 0; i < viaStdout.Count; i++)
            {
                Assert.True(
                    viaStdout[i].SequenceEqual(viaNamed[i]),
                    $"Frame {i} differs between the stdout transport and the sized transport. "
                    + "The transport changed the pixels, which it must never do.");
            }

            _output.WriteLine($"{viaStdout.Count} frames byte-identical across both transports");
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    /// <summary>
    /// Stops a machine where the named pipe silently fails from turning every fact above into a
    /// no-op that passes. WP-215 needed the same guard for the GPU compositor and for the same
    /// reason: a parity test between two paths proves nothing when both are the same path.
    /// <para>
    /// This is also the regression test for the defect that made the transport look like it worked
    /// while costing 500 ms/frame: without <c>-y</c>, ffmpeg treats the pipe as an existing file,
    /// stops on "Overwrite? [y/N]", and closes it — which arrives as an immediate end of stream,
    /// not as an error.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SizedTransport_WasActuallyObtained_WhereThePlatformSupportsIt()
    {
        if (!FfmpegFixture.IsAvailable) return;
        if (!OperatingSystem.IsWindows())
        {
            _output.WriteLine("named-pipe transport is Windows-only; nothing is claimed here.");
            return;
        }

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var clip = Path.Combine(directory, "clip.mp4");
            await FfmpegFixture.WriteTestClipAsync(clip, Fps, ClipSeconds, Width, Height);

            using var decoder = await FfmpegPlaybackDecoder.StartAsync(
                clip, 0, Fps, Width, Height, Width, Height, CancellationToken.None,
                pixelFormat: "rgba", preferHardware: false, sizedTransport: true);

            _output.WriteLine($"transport={decoder.Transport}; detail={decoder.TransportDetail ?? "(none)"}");

            Assert.True(
                decoder.Transport == FrameTransport.NamedPipe,
                "The sized transport was asked for on Windows and not obtained, so every parity "
                + $"fact in this file compared stdout against stdout: {decoder.TransportDetail}");

            // Connecting is not delivering. Pull a frame and check it is a whole one.
            using var frame = await decoder.ReadFrameAsync();
            Assert.Equal(Width * Height * 4, frame.Data.Length);
            Assert.Contains(frame.Data, b => b != 0);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    /// <summary>
    /// A machine that cannot have the sized transport still exports, by the path that shipped
    /// before this package. The override is the deterministic stand-in for that machine: it takes
    /// exactly the code path a pipe failure falls back to.
    /// </summary>
    [Fact]
    public async Task StdoutTransport_StillDecodes_WhenTheOverrideForcesIt()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        var previous = Environment.GetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable);
        try
        {
            var clip = Path.Combine(directory, "clip.mp4");
            await FfmpegFixture.WriteTestClipAsync(clip, Fps, ClipSeconds, Width, Height);

            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, "stdout");
            Assert.False(ExportFrameSource.UseSizedTransport());

            using var source = new ExportFrameSource();
            for (var f = 0; f < Fps; f++)
            {
                using var frame = await source.GetFrameAsync(clip, f / (double)Fps);
                Assert.Equal(Width * Height * 4, frame.Data.Length);
            }

            // One pipe for the whole run. A transport that fails and is answered by the
            // process-per-frame seek fallback shows up here as one restart per frame.
            Assert.Equal(1, source.RestartCount);
            Assert.Equal(FrameTransport.AnonymousPipe, ExportTransportDiagnostics.LastTransport);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, previous);
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    /// <summary>
    /// The sized transport must cost one ffmpeg pipe for a forward run, not one per frame, <b>and
    /// that pipe must be the sized one</b>.
    /// <para>
    /// The restart count alone caught the <c>-y</c> defect as it existed <i>then</i> — 600 pipes for
    /// 600 frames, right pixels, six times slower. It would <b>not</b> catch it now: with the
    /// first-frame prefetch in place a broken sized transport falls back to stdout, which reads
    /// forward perfectly well and restarts once. WP-216's T2 reviewer made exactly that point, and
    /// the transport assertion below is the answer to it: a test whose name says "sized transport"
    /// must fail when the transport is not sized.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SizedTransport_ReadsForward_OnTheSizedPipe_WithoutRestartingPerFrame()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        var previous = Environment.GetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable);
        try
        {
            var clip = Path.Combine(directory, "clip.mp4");
            await FfmpegFixture.WriteTestClipAsync(clip, Fps, ClipSeconds, Width, Height);

            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, "named");
            using var source = new ExportFrameSource();
            for (var f = 0; f < Fps; f++)
                (await source.GetFrameAsync(clip, f / (double)Fps)).Dispose();

            Assert.Equal(1, source.RestartCount);
            Assert.Equal(FrameTransport.NamedPipe, ExportTransportDiagnostics.LastTransport);
            Assert.Null(ExportTransportDiagnostics.LastFallbackReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, previous);
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    /// <summary>
    /// WP-216 T2 P1. A pipe that connects and then delivers nothing is <b>two different events</b>,
    /// and the package shipped with only one of them implemented.
    /// <para>
    /// Exit <b>0</b> is a seek past the end of the media: an exhausted stream, not a fault, and the
    /// alarm must stay silent or every healthy machine reports a fallback it never had — the defect
    /// this package already fixed once. A <b>non-zero</b> exit is ffmpeg opening the pipe and then
    /// failing, which drops the export onto a transport measured 4.5x slower; before this fix it
    /// was filed as "no frames at this position" and nothing anywhere said otherwise.
    /// </para>
    /// <para>
    /// Tested against real exit codes rather than a contrived ffmpeg failure, because which failure
    /// mode a given ffmpeg build produces is exactly the sort of thing that differs between
    /// machines — and this decision must not.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(255, true)]
    public async Task PostConnectFailure_IsAFaultOnlyWhenFfmpegExitedNonZero(int exitCode, bool expectFault)
    {
        using var process = StartExitingProcess(exitCode);
        await process.WaitForExitAsync();

        var classified = await FfmpegPlaybackDecoder.ClassifyPostConnectFailureAsync(
            process,
            new EndOfStreamException("ffmpeg playback pipe closed."),
            CancellationToken.None);

        if (expectFault)
        {
            // Reaches the decoder's general catch, which sets TransportFellBack and arms
            // ExportTransportDiagnostics.LastFallbackReason.
            var fault = Assert.IsType<InvalidOperationException>(classified);
            Assert.Contains(exitCode.ToString(), fault.Message, StringComparison.Ordinal);
            Assert.Contains("after opening the pipe", fault.Message, StringComparison.Ordinal);
        }
        else
        {
            // Reaches the decoder's EndOfStreamException catch, which does not arm the alarm.
            Assert.IsType<EndOfStreamException>(classified);
        }
    }

    /// <summary>
    /// WP-216 T2 P2. A first-frame timeout used to be reported as "ffmpeg did not open the pipe
    /// within 10s". The pipe had opened; it was delivery that timed out, and the connect timeout is
    /// not even the one that fired. A process still running when the grace expires is that case.
    /// </summary>
    [Fact]
    public async Task FirstFrameTimeout_IsNotReportedAsAConnectTimeout()
    {
        // A process that is definitively still running, which is the condition under test: ffmpeg
        // alive, pipe open, no frame delivered. The test process itself is the cheapest honest one
        // — a spawned `timeout`/`sleep` shell is not, as the first version of this test found out
        // when cmd refused a redirected console and exited non-zero in milliseconds, turning the
        // "still running" case into the "exited non-zero" case.
        using var process = Process.GetCurrentProcess();

        var classified = await FfmpegPlaybackDecoder.ClassifyPostConnectFailureAsync(
            process, new OperationCanceledException(), CancellationToken.None);

        var timeout = Assert.IsType<TimeoutException>(classified);
        Assert.Contains("opened the pipe", timeout.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("did not open", timeout.Message, StringComparison.Ordinal);
    }

    /// <summary>A process that exits immediately with a chosen code, on either shell.</summary>
    private static Process StartExitingProcess(int exitCode)
        => Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            ArgumentList =
            {
                OperatingSystem.IsWindows() ? "/c" : "-c",
                OperatingSystem.IsWindows() ? $"exit {exitCode}" : $"exit {exitCode}",
            },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

    /// <summary>
    /// WP-216 T2 round 2. The Settings status row answers what the next export will <b>request</b>,
    /// and must not turn a benign stdout decoder into a claim that the sized transport failed.
    /// <para>
    /// This is the reviewer's own reproduction, made executable: force stdout, decode, clear the
    /// override — at which point the next export <i>will</i> request the sized pipe, so a row
    /// reading "the sized transport was not available" is simply false. It said exactly that for
    /// about an hour, because the row had been changed to report the last observed transport.
    /// </para>
    /// </summary>
    [Fact]
    public async Task StatusRow_DoesNotClaimAFailureAfterABenignStdoutDecoder()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        var previous = Environment.GetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable);
        try
        {
            var clip = Path.Combine(directory, "clip.mp4");
            await FfmpegFixture.WriteTestClipAsync(clip, Fps, ClipSeconds, Width, Height);

            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, "stdout");
            using (var forced = new ExportFrameSource())
                (await forced.GetFrameAsync(clip, 0)).Dispose();

            Assert.Equal(FrameTransport.AnonymousPipe, ExportTransportDiagnostics.LastTransport);
            Assert.Contains("forced", ExportTransportDiagnostics.DescribeSelection(), StringComparison.OrdinalIgnoreCase);

            // Override cleared: the next export asks for the sized pipe, so the row must say so.
            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, null);
            var described = ExportTransportDiagnostics.DescribeSelection();

            Assert.Contains("Sized pipe", described, StringComparison.Ordinal);
            Assert.DoesNotContain("not available", described, StringComparison.OrdinalIgnoreCase);
            // A benign stdout decoder is not a fallback and must not have armed the alarm either.
            Assert.Null(ExportTransportDiagnostics.LastFallbackReason);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, previous);
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    /// <summary>
    /// WP-216 T2 round 2, the reviewer's unverified note taken anyway: a process that closed the
    /// pipe without writing a frame and then did <i>not</i> exit is a broken transport, not an
    /// exhausted stream. An exhausted stream ends with ffmpeg exiting 0, promptly. This arm used to
    /// return the original end-of-stream and stay silent, contradicting its own comment.
    /// </summary>
    [Fact]
    public async Task ClosedPipeFromAProcessThatKeepsRunning_IsAFault_NotAnExhaustedStream()
    {
        using var process = Process.GetCurrentProcess();

        var classified = await FfmpegPlaybackDecoder.ClassifyPostConnectFailureAsync(
            process,
            new EndOfStreamException("ffmpeg playback pipe closed."),
            CancellationToken.None);

        var fault = Assert.IsType<InvalidOperationException>(classified);
        Assert.Contains("did not exit", fault.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Defaults, stated as a test rather than as a comment. Hardware decode is off because it was
    /// measured slower <i>and</i> not colour-neutral (DECISION.md R1 and Scope 5); the sized
    /// transport is on because it was measured 4.5x faster and byte-identical.
    /// </summary>
    [Fact]
    public void Defaults_AreSizedTransportOn_AndHardwareDecodeOff()
    {
        var transport = Environment.GetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable);
        var hardware = Environment.GetEnvironmentVariable(ExportFrameSource.HardwareDecodeVariable);
        try
        {
            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, null);
            Environment.SetEnvironmentVariable(ExportFrameSource.HardwareDecodeVariable, null);
            Assert.True(ExportFrameSource.UseSizedTransport());
            Assert.False(ExportFrameSource.UseHardwareDecode());

            Environment.SetEnvironmentVariable(ExportFrameSource.HardwareDecodeVariable, "1");
            Assert.True(ExportFrameSource.UseHardwareDecode());
            Environment.SetEnvironmentVariable(ExportFrameSource.HardwareDecodeVariable, "true");
            Assert.True(ExportFrameSource.UseHardwareDecode());
            Environment.SetEnvironmentVariable(ExportFrameSource.HardwareDecodeVariable, "0");
            Assert.False(ExportFrameSource.UseHardwareDecode());

            // An unrecognised value is not an opt-in. A typo must not silently enable a path that
            // is a measured regression.
            Environment.SetEnvironmentVariable(ExportFrameSource.HardwareDecodeVariable, "yes");
            Assert.False(ExportFrameSource.UseHardwareDecode());

            // Only the exact opt-out word turns the transport off, for the same reason.
            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, "nonsense");
            Assert.True(ExportFrameSource.UseSizedTransport());
            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, "STDOUT");
            Assert.False(ExportFrameSource.UseSizedTransport());
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, transport);
            Environment.SetEnvironmentVariable(ExportFrameSource.HardwareDecodeVariable, hardware);
        }
    }

    /// <summary>
    /// The acceptance criterion "a machine without it still exports, and the suite proves it", at
    /// the level that matters: a whole export through the real <see cref="TimelineExporter"/>, on
    /// both transports, producing a file with the frames the timeline asked for.
    /// <para>
    /// The two outputs are not compared byte for byte. H.264 amplifies input differences
    /// unpredictably — WP-215 measured a 1-LSB composite delta arriving as ~132 LSB in the encoded
    /// file — so an encoded diff is the wrong instrument even when, as here, the input really is
    /// identical. Byte-identity is asserted where it is meaningful, on the decoded frames, by
    /// <see cref="SizedTransport_IsByteIdenticalToStdout"/>.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("named")]
    [InlineData("stdout")]
    public async Task Export_Completes_OnEitherTransport(string transport)
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        var previous = Environment.GetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable);
        try
        {
            var clip = Path.Combine(directory, "clip.mp4");
            await FfmpegFixture.WriteTestClipAsync(clip, Fps, ClipSeconds, Width, Height);
            var output = Path.Combine(directory, $"export-{transport}.mp4");

            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, transport);

            var timeline = new Timeline
            {
                Fps = Fps,
                Width = Width,
                Height = Height,
                Tracks =
                [
                    new Track
                    {
                        Type = ClipType.video,
                        Clips =
                        [
                            new Clip
                            {
                                MediaRef = clip,
                                MediaType = ClipType.video,
                                DurationFrames = Fps,
                            }
                        ]
                    }
                ]
            };

            using var exporter = new TimelineExporter(
                new CpuCompositor(), new ExportFrameSource(), ownsFrameSource: true);
            await exporter.ExportAsync(timeline, new ExportPreset(transport), output);

            Assert.True(File.Exists(output), $"no file at {output}");
            var probe = await new FfmpegMediaProbe().ProbeAsync(output);
            Assert.True(probe.HasVideo);
            Assert.Equal(Width, probe.Width);
            Assert.Equal(Height, probe.Height);

            _output.WriteLine(
                $"{transport}: {new FileInfo(output).Length:N0} bytes, "
                + $"{probe.DurationSeconds:F2}s; {ExportTransportDiagnostics.Summary}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, previous);
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    /// <summary>
    /// A clip trimmed past the end of its media seeks past EOF; ffmpeg emits nothing and exits
    /// clean. That takes the stdout path — there is nothing for any transport to carry — but it is
    /// <b>not</b> a transport fault and must not arm the fallback alarm.
    /// <para>
    /// Found by reading the transport log after the other tests in this file passed: a healthy
    /// machine was reporting "Fell back at least once: ffmpeg playback pipe closed". A diagnostic
    /// that cries wolf is worse than no diagnostic, which is the point of WP-215's Scope 4.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SeekPastTheEnd_IsNotReportedAsATransportFallback()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var clip = Path.Combine(directory, "clip.mp4");
            await FfmpegFixture.WriteTestClipAsync(clip, Fps, ClipSeconds, Width, Height);

            using var decoder = await FfmpegPlaybackDecoder.StartAsync(
                clip, ClipSeconds + 30, Fps, Width, Height, Width, Height, CancellationToken.None,
                pixelFormat: "rgba", preferHardware: false, sizedTransport: true);

            _output.WriteLine($"transport={decoder.Transport}; fellBack={decoder.TransportFellBack}; "
                              + $"detail={decoder.TransportDetail}");

            // There are no frames out there, so whichever transport is reported, reading must EOF.
            await Assert.ThrowsAsync<EndOfStreamException>(() => decoder.ReadFrameAsync());

            Assert.False(
                decoder.TransportFellBack,
                "An exhausted stream was reported as a transport fallback, which would leave a "
                + $"healthy machine permanently claiming a fault: {decoder.TransportDetail}");
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    /// <summary>
    /// Which transport ran has to be answerable after the fact. A fallback that costs 70 ms/frame
    /// and leaves nothing behind is indistinguishable from one that never happened.
    /// </summary>
    [Fact]
    public async Task Diagnostics_NameTheTransportThatCarriedTheFrames()
    {
        if (!FfmpegFixture.IsAvailable) return;

        var directory = FfmpegFixture.CreateTempDirectory();
        try
        {
            var clip = Path.Combine(directory, "clip.mp4");
            await FfmpegFixture.WriteTestClipAsync(clip, Fps, ClipSeconds, Width, Height);

            using var source = new ExportFrameSource();
            (await source.GetFrameAsync(clip, 0)).Dispose();

            Assert.NotNull(ExportTransportDiagnostics.LastTransport);
            Assert.DoesNotContain("No export decoder started yet", ExportTransportDiagnostics.Summary);
            _output.WriteLine(ExportTransportDiagnostics.Summary);
        }
        finally
        {
            FfmpegFixture.DeleteTempDirectory(directory);
        }
    }

    private async Task<List<byte[]>> HashFramesAsync(string clip, string transport)
    {
        var previous = Environment.GetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable);
        try
        {
            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, transport);
            using var sha = SHA256.Create();
            using var source = new ExportFrameSource();
            var hashes = new List<byte[]>();
            for (var f = 0; f < Fps; f++)
            {
                using var frame = await source.GetFrameAsync(clip, f / (double)Fps);
                hashes.Add(sha.ComputeHash(frame.Data));
            }
            return hashes;
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExportFrameSource.TransportOverrideVariable, previous);
        }
    }
}
