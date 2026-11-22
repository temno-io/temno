using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Temno.Core.IO;
using Temno.Core.Models;

namespace Temno.Media.Export;

/// <summary>
/// Delegate for rendering an encode segment within a frame range [startFrame, startFrame + frameCount).
/// </summary>
public delegate Task<int> RenderSegmentDelegate(
    int startFrame,
    int frameCount,
    int framesDoneBefore,
    string segmentOutputPath,
    CancellationToken cancellationToken);

/// <summary>
/// Executes a <see cref="SmartRenderPlan"/>: copies untouched segments, renders modified ones,
/// carries each as an MPEG-TS intermediate (in-band SPS/PPS, so segments from different encoders
/// can follow one another), joins them on the timeline's frame grid and verifies the result.
/// <para>
/// Every gate here checks what a viewer would see — which frames, at which times — not only how
/// many (WP-234). A frame count is satisfied by a copy that shows the wrong last frame.
/// </para>
/// </summary>
public sealed class SmartRenderExecutor
{
    /// <summary>How long a killed ffmpeg gets to exit and release its files before cleanup moves on.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(10);

    /// <summary>Tolerance, in frames, for a timestamp to count as its frame-grid slot.</summary>
    private const double GridToleranceFrames = 0.25;

    // Errors only on stderr, so a failure surfaces its reason rather than a progress log.
    private static readonly string[] Ffmpeg = ["-hide_banner", "-nostats", "-loglevel", "error", "-y"];
    private static readonly string[] Ffprobe = ["-hide_banner", "-v", "error"];

    /// <summary>
    /// Returns the required bitstream filter for remuxing MP4 packets into MPEG-TS Annex B format.
    /// </summary>
    public static string GetBitstreamFilter(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 => "h264_mp4toannexb",
        VideoCodec.H265 or VideoCodec.HevcHdr => "hevc_mp4toannexb",
        _ => throw new NotSupportedException($"Bitstream filter is not supported for codec {codec}."),
    };

    /// <summary>
    /// Executes the smart-render plan, producing a concatenated video file at <paramref name="outputPath"/>.
    /// Returns the total number of frames rendered with visual content.
    /// </summary>
    public async Task<int> ExecuteAsync(
        SmartRenderPlan plan,
        string ffmpegPath,
        string ffprobePath,
        VideoCodec targetCodec,
        string outputPath,
        RenderSegmentDelegate renderEncodeSegmentAsync,
        Action<int, int>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffmpegPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(ffprobePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(renderEncodeSegmentAsync);

        if (plan.Segments.Count == 0)
        {
            throw new InvalidOperationException("Cannot execute an empty smart render plan.");
        }

        if (plan.Fps <= 0)
        {
            throw new InvalidOperationException("Cannot execute a smart render plan without its timeline frame rate.");
        }

        var fps = plan.Fps;
        var bsf = GetBitstreamFilter(targetCodec);
        var tempDir = Path.Combine(Path.GetTempPath(), $"temno_smart_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var concatList = new StringBuilder("ffconcat version 1.0\n");
        var framesDone = 0;
        var framesWithContent = 0;
        var reorderDelay = 0;

        try
        {
            for (var i = 0; i < plan.Segments.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = plan.Segments[i];
                var segTs = Path.Combine(tempDir, $"seg_{i:D4}.ts");

                if (segment.Kind == SmartRenderSegmentKind.Encode)
                {
                    var segMp4 = Path.Combine(tempDir, $"seg_{i:D4}.mp4");
                    var segContentFrames = await renderEncodeSegmentAsync(
                        segment.StartFrame,
                        segment.FrameCount,
                        framesDone,
                        segMp4,
                        cancellationToken).ConfigureAwait(false);

                    framesWithContent += segContentFrames;
                    framesDone += segment.FrameCount;

                    // Annex B with in-band SPS/PPS, so the join does not depend on one segment's avcC.
                    await RunAsync(ffmpegPath,
                        [.. Ffmpeg, "-i", segMp4, "-map", "0:v:0", "-c", "copy", "-bsf:v", bsf, "-f", "mpegts", segTs],
                        "smart-render remux segment", cancellationToken).ConfigureAwait(false);
                    TryDelete(segMp4);
                }
                else if (segment.Kind == SmartRenderSegmentKind.Copy)
                {
                    if (string.IsNullOrEmpty(segment.SourcePath))
                        throw new InvalidOperationException($"Copy segment {i} is missing SourcePath.");

                    // The seek must land ON the keyframe: a target a hair past it makes ffmpeg treat the
                    // keyframe as pre-roll and hide it. -frames:v then takes exactly the decode-order
                    // packets the analyzer proved are this segment's display frames.
                    await RunAsync(ffmpegPath,
                        [.. Ffmpeg, "-ss", Seconds(segment.SourceStartSeconds), "-i", segment.SourcePath,
                         "-map", "0:v:0", "-frames:v", segment.FrameCount.ToString(CultureInfo.InvariantCulture),
                         "-c", "copy", "-bsf:v", bsf, "-f", "mpegts", segTs],
                        "smart-render copy segment", cancellationToken).ConfigureAwait(false);

                    await VerifyCopyStartAsync(ffmpegPath, segment, segTs, i, cancellationToken).ConfigureAwait(false);

                    framesWithContent += segment.FrameCount;
                    framesDone += segment.FrameCount;
                    onProgress?.Invoke(framesDone, plan.TotalFrames);
                }
                else
                {
                    throw new NotSupportedException($"Unsupported segment kind {segment.Kind}.");
                }

                var delay = await VerifySegmentAsync(ffprobePath, segTs, segment, fps, i, cancellationToken)
                    .ConfigureAwait(false);
                reorderDelay = Math.Max(reorderDelay, delay);

                concatList.Append("file ").Append(Path.GetFileName(segTs)).Append('\n')
                    .Append("duration ").Append(Seconds(segment.FrameCount / (double)fps)).Append('\n');
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Join with the concat demuxer, which places each segment's first displayed frame at the
            // running duration. Concatenating the TS bytes instead let ffmpeg re-time each boundary
            // from decode timestamps, so any change in B-frame delay between segments moved the
            // picture by frames against the audio.
            //
            // setts then snaps presentation times onto the frame grid (the concat offsets carry
            // microsecond rounding) and rewrites decode times as one uniform run, delayed by the
            // deepest reordering of any segment. Every segment's own decode order already satisfies
            // that delay, so the result is strictly increasing and never after its presentation time;
            // a copy with no B-frames followed by an encoded segment with them no longer produces
            // non-monotonic timestamps for the muxer to patch.
            var listPath = Path.Combine(tempDir, "segments.ffconcat");
            await File.WriteAllTextAsync(listPath, concatList.ToString(), cancellationToken).ConfigureAwait(false);
            var frameRate = fps.ToString(CultureInfo.InvariantCulture);
            var delayFrames = reorderDelay.ToString(CultureInfo.InvariantCulture);
            var retime = $"setts=pts=round(PTS*TB*{frameRate})/(TB*{frameRate}):dts=(N-{delayFrames})/(TB*{frameRate})";
            await RunAsync(ffmpegPath,
                [.. Ffmpeg, "-f", "concat", "-i", listPath, "-map", "0:v:0", "-c", "copy", "-bsf:v", retime,
                 "-movflags", "+faststart", outputPath],
                "smart-render concat", cancellationToken).ConfigureAwait(false);

            // Mandatory gate: decode the result and require every frame, each at its own time.
            await VerifyOutputAsync(ffprobePath, outputPath, plan.TotalFrames, fps, cancellationToken).ConfigureAwait(false);

            return framesWithContent;
        }
        finally
        {
            await DeleteDirectoryAsync(tempDir).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Confirms a copy begins on the frame the seek names: the first decoded picture of the copy
    /// must equal the first picture a decoding seek to the same time produces. The packet checks
    /// cannot see this — a seek that lands one keyframe early yields an equally regular segment.
    /// </summary>
    private static async Task VerifyCopyStartAsync(
        string ffmpegPath,
        SmartRenderSegment segment,
        string segTs,
        int index,
        CancellationToken cancellationToken)
    {
        var copied = await RunAsync(ffmpegPath,
            [.. Ffmpeg, "-i", segTs, "-map", "0:v:0", "-frames:v", "1", "-f", "md5", "-"],
            "smart-render verify copy start", cancellationToken).ConfigureAwait(false);
        var source = await RunAsync(ffmpegPath,
            [.. Ffmpeg, "-ss", Seconds(segment.SourceStartSeconds), "-i", segment.SourcePath!, "-map", "0:v:0", "-frames:v", "1", "-f", "md5", "-"],
            "smart-render verify copy start", cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(copied) || !string.Equals(copied.Trim(), source.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Smart render verification failed: copy segment {index} does not begin on the source frame at {Seconds(segment.SourceStartSeconds)}s.");
        }
    }

    /// <summary>
    /// Packet-level check of one intermediate: exactly its frame count, a keyframe first, nothing
    /// displayed before that keyframe, and presentation times that are consecutive frame-grid
    /// slots. Returns the segment's reordering delay in frames (how far decode order runs ahead of
    /// display order), which the join needs.
    /// </summary>
    private static async Task<int> VerifySegmentAsync(
        string ffprobePath,
        string segTs,
        SmartRenderSegment segment,
        int fps,
        int index,
        CancellationToken cancellationToken)
    {
        var listing = await RunAsync(ffprobePath,
            [.. Ffprobe, "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", segTs],
            "smart-render verify segment", cancellationToken).ConfigureAwait(false);

        var pts = new List<double>();
        var firstIsKey = false;
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                throw SegmentFailure(index, segment, "a packet has no presentation time");
            if (pts.Count == 0)
                firstIsKey = parts.Length > 1 && parts[1].Contains('K');
            pts.Add(t);
        }

        if (pts.Count != segment.FrameCount)
            throw SegmentFailure(index, segment, $"it holds {pts.Count} packets");
        if (!firstIsKey)
            throw SegmentFailure(index, segment, "it does not start on a keyframe");

        var order = Enumerable.Range(0, pts.Count).OrderBy(j => pts[j]).ToArray();
        if (order[0] != 0)
            throw SegmentFailure(index, segment, "a frame is displayed before its first keyframe");

        var start = pts[0];
        var delay = 0;
        for (var rank = 0; rank < order.Length; rank++)
        {
            if (Math.Abs((pts[order[rank]] - start) * fps - rank) > GridToleranceFrames)
                throw SegmentFailure(index, segment, $"display frame {rank} is not one frame after the previous");
            delay = Math.Max(delay, order[rank] - rank);
        }

        return delay;
    }

    private static InvalidOperationException SegmentFailure(int index, SmartRenderSegment segment, string problem)
        => new($"Smart render verification failed: {segment.Kind.ToString().ToLowerInvariant()} segment {index} "
             + $"(timeline frames {segment.StartFrame}–{segment.StartFrame + segment.FrameCount - 1}) is not exact: {problem}.");

    /// <summary>
    /// Decodes the finished video and requires exactly <paramref name="expectedTotalFrames"/>
    /// pictures, picture n presented at n / fps. Replaces a count-only gate, which accepted
    /// outputs with a wrong last frame and outputs drifting against the audio.
    /// </summary>
    public static async Task VerifyOutputAsync(
        string ffprobePath,
        string outputPath,
        int expectedTotalFrames,
        int fps,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(outputPath))
            throw new InvalidOperationException("Smart render verification failed: no output was written.");

        var listing = await RunAsync(ffprobePath,
            [.. Ffprobe, "-select_streams", "v:0", "-show_entries", "frame=pts_time", "-of", "csv=p=0", outputPath],
            "smart-render verify output", cancellationToken).ConfigureAwait(false);

        var frames = 0;
        foreach (var line in listing.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = line.TrimEnd(',');
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
                || Math.Abs(t * fps - frames) > GridToleranceFrames)
            {
                throw new InvalidOperationException(
                    $"Smart render verification failed: output frame {frames} is presented at {text}s, not {Seconds(frames / (double)fps)}s.");
            }

            frames++;
        }

        if (frames != expectedTotalFrames)
        {
            throw new InvalidOperationException(
                $"Smart render verification failed: output has {frames} frames, expected {expectedTotalFrames}.");
        }
    }

    /// <summary>Seconds as ffmpeg parses them, never in exponent form and never localized.</summary>
    private static string Seconds(double seconds) => seconds.ToString("0.0#########", CultureInfo.InvariantCulture);

    /// <summary>
    /// Runs ffmpeg/ffprobe to completion and returns stdout. On cancellation the process is killed and
    /// then waited for, so no segment is still held open when the temp directory is removed.
    /// </summary>
    private static async Task<string> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string stage,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo.FileName = executable;
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start {Path.GetFileName(executable)} for {stage}.");
        }

        using var cancellationRegistration = ProcessCancellation.KillOnCancellation(process, cancellationToken);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WaitForExitAfterKillAsync(process).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw FfmpegEncodeException.Create(
                stage,
                "copy",
                string.Join(' ', process.StartInfo.ArgumentList),
                process.ExitCode,
                stderr,
                framesWritten: 0);
        }

        return stdout;
    }

    private static async Task WaitForExitAfterKillAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            using var grace = new CancellationTokenSource(ExitGrace);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or it would not die in time; cleanup below retries either way.
        }
    }

    /// <summary>
    /// Removes the intermediates. A cancelled encode segment's ffmpeg is killed by its own owner and
    /// can hold its file for a moment after the export has already been abandoned, so retry briefly
    /// rather than leave the directory in %TEMP%.
    /// </summary>
    private static async Task DeleteDirectoryAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= 50)
                    return;
                await Task.Delay(100, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* removed with the directory */ }
    }
}
