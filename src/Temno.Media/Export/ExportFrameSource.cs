using System.Diagnostics;
using System.Globalization;
using Temno.Core.IO;

namespace Temno.Media.Export;

/// <summary>
/// Export <see cref="IFrameSource"/>: one long-lived ffmpeg pipe per media path at the
/// source's own rate (<see cref="DecodeRate"/>), instead of a process-per-frame seek. Export
/// walks frames in monotonically increasing timeline order, so each clip's source time is also
/// monotonic.
///
/// A pipe is (re)started when the requested source time moves backward or jumps forward
/// (a later occurrence of the same clip, or a retime). Forward drift inside one clip reads
/// forward through the pipe. The number of live pipes is bounded; least-recently-used evicted.
///
/// Returned frames are read-only: a pipe retains its last frame to answer duplicate timestamps
/// and end-of-stream, and hands out clones of it. Callers must not write into
/// <see cref="VideoFrame.Data"/>.
/// </summary>
public sealed class ExportFrameSource : IFrameSource, IDisposable
{
    /// <summary>Forward jump beyond this many source seconds restarts the pipe instead of reading through.</summary>
    public const double RestartThresholdSeconds = 2.0;

    /// <summary>Live ffmpeg pipes retained across calls. Covers a multi-layer composite.</summary>
    public const int MaxLivePipes = 4;

    /// <summary>Forward skip longer than this many source frames restarts instead of reading through.</summary>
    public const int CatchUpRestartFrames = 90;

    /// <summary>
    /// WP-216. Overrides the frame transport without a rebuild: <c>named</c> or <c>stdout</c>, in the
    /// shape of <c>TEMNO_COMPOSITOR</c>. The sized transport is the default because it measured
    /// <b>84.9 to 16.0 ms/frame</b> at 4K against the threshold fixed in
    /// <c>docs/verification/wp-216/DECISION.md</c> §S1.5, and is byte-identical by construction —
    /// it changes which handle the same ffmpeg output lands in, nothing about the ffmpeg command.
    /// </summary>
    public const string TransportOverrideVariable = "TEMNO_EXPORT_TRANSPORT";

    /// <summary>
    /// WP-216. Asks ffmpeg for <c>-hwaccel d3d11va</c> on the export decode path. <b>Off by
    /// default and deliberately so</b>: measured on an RTX 3090 against a 16-core Zen 4 it is a
    /// <i>regression</i> — 8.38 ms/frame against software's 4.65, because the surface has to come
    /// back down to system memory to reach this process at all (DECISION.md §R1, C2). It exists
    /// because a thin CPU with a modern iGPU is a real Temno machine and the answer there is not
    /// this machine's answer; it is not exposed as a user preference, because a setting that is a
    /// regression on the hardware it was measured on does not belong in a dialog.
    /// </summary>
    public const string HardwareDecodeVariable = "TEMNO_EXPORT_HW_DECODE";

    private readonly IMediaProbe _probe;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Pipe> _pipes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MediaProbeResult> _probeCache = new(StringComparer.OrdinalIgnoreCase);
    private long _useCounter;
    private bool _disposed;

    public ExportFrameSource(IMediaProbe? probe = null)
    {
        _probe = probe ?? new FfmpegMediaProbe();
    }

    /// <summary>Decoder restarts (probe + ffmpeg spawn) since construction or <see cref="Reset"/>.</summary>
    public int RestartCount { get; private set; }

    public async Task<VideoFrame> GetFrameAsync(string path, double sourceSeconds, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Media file not found.", path);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var t = Math.Max(0, sourceSeconds);
            var pipe = RentPipe(path);
            if (TryReuseLast(pipe, t) is { } reused)
                return reused;

            return await ReadForwardAsync(pipe, path, t, allowRestartRetry: true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Reset()
    {
        lock (_pipes)
        {
            foreach (var pipe in _pipes.Values)
                pipe.Dispose();
            _pipes.Clear();
            _useCounter = 0;
        }

        _probeCache.Clear();
        RestartCount = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Reset();
        _gate.Dispose();
    }

    private Pipe RentPipe(string path)
    {
        lock (_pipes)
        {
            if (!_pipes.TryGetValue(path, out var pipe))
            {
                pipe = new Pipe();
                _pipes[path] = pipe;
            }

            pipe.LastUsed = ++_useCounter;
            EvictLeastRecentlyUsed(keep: pipe);
            return pipe;
        }
    }

    private void EvictLeastRecentlyUsed(Pipe keep)
    {
        while (_pipes.Count > MaxLivePipes)
        {
            string? oldestKey = null;
            var oldest = long.MaxValue;
            foreach (var (key, candidate) in _pipes)
            {
                if (ReferenceEquals(candidate, keep)) continue;
                if (candidate.LastUsed >= oldest) continue;
                oldest = candidate.LastUsed;
                oldestKey = key;
            }

            if (oldestKey is null) return;
            _pipes[oldestKey].Dispose();
            _pipes.Remove(oldestKey);
        }
    }

    private async Task<VideoFrame> ReadForwardAsync(
        Pipe pipe,
        string path,
        double t,
        bool allowRestartRetry,
        CancellationToken cancellationToken)
    {
        try
        {
            // The stream is exhausted and time still runs forward (a clip trimmed past the end of
            // its media): hold the final frame instead of respawning ffmpeg past EOF.
            if (pipe.AtEndOfStream
                && pipe.LastFrame is { } tail
                && t + 0.5 * pipe.FrameIntervalSeconds >= pipe.EmittedSeconds)
                return CloneFrame(tail);

            if (NeedsRestart(pipe, t))
                await RestartAsync(pipe, path, t, cancellationToken).ConfigureAwait(false);

            // Source slower than the timeline (24 fps clip on a 30 fps timeline): the frame already
            // decoded still covers this instant, so repeat it instead of pulling the next one.
            if (pipe.LastFrame is { } held && t + 0.5 * pipe.FrameIntervalSeconds < pipe.NextSeconds)
                return CloneFrame(held);

            // Source faster than the timeline (60 fps clip on a 30 fps timeline): drop the frames
            // between emissions. Each discard advances the cursor by exactly one source interval —
            // without that the condition is loop-invariant, the pipe drains the whole file, and the
            // export freezes on one frame.
            var skipped = 0;
            while (pipe.NextSeconds + 0.5 * pipe.FrameIntervalSeconds < t)
            {
                if (skipped >= CatchUpRestartFrames)
                {
                    await RestartAsync(pipe, path, t, cancellationToken).ConfigureAwait(false);
                    break;
                }

                await pipe.Decoder!.DiscardFrameAsync(cancellationToken).ConfigureAwait(false);
                pipe.NextSeconds += pipe.FrameIntervalSeconds;
                skipped++;
            }

            var frame = await pipe.Decoder!.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            pipe.LastFrame = frame;
            pipe.LastRequestedSeconds = t;
            pipe.EmittedSeconds = pipe.NextSeconds;
            // Advance by the pipe's own cadence, not the timeline instant: NextSeconds tracks where
            // the decoder actually is, which is what the skip/repeat decisions above depend on.
            pipe.NextSeconds += pipe.FrameIntervalSeconds;
            return frame;
        }
        catch (EndOfStreamException)
        {
            if (pipe.LastFrame is { } last)
            {
                pipe.AtEndOfStream = true;
                return CloneFrame(last);
            }

            pipe.Dispose();
            return await SeekOnceAsync(path, t, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            pipe.Dispose();
            throw;
        }
        catch when (allowRestartRetry)
        {
            pipe.Dispose();
            await RestartAsync(pipe, path, t, cancellationToken).ConfigureAwait(false);
            return await ReadForwardAsync(pipe, path, t, allowRestartRetry: false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static VideoFrame? TryReuseLast(Pipe pipe, double t)
    {
        if (pipe.LastFrame is not { } last)
            return null;
        // Same source instant (duplicate frame request within one timeline frame).
        if (Math.Abs(t - pipe.LastRequestedSeconds) < 0.5 * pipe.FrameIntervalSeconds)
            return CloneFrame(last);
        return null;
    }

    private static bool NeedsRestart(Pipe pipe, double t)
    {
        if (pipe.Decoder is null)
            return true;
        // Backward seek (a later occurrence of the same clip, or a retime): a pipe cannot rewind.
        // Compare against the frame currently held, not the next emission — otherwise every repeat
        // of a below-timeline-rate source reads as a backward seek and respawns ffmpeg per frame.
        var floor = pipe.LastFrame is null ? pipe.NextSeconds : pipe.EmittedSeconds;
        if (t + 0.5 * pipe.FrameIntervalSeconds < floor)
            return true;
        // Large forward jump: seeking is cheaper than reading through.
        return t - pipe.NextSeconds > RestartThresholdSeconds;
    }

    private async Task RestartAsync(Pipe pipe, string path, double t, CancellationToken cancellationToken = default)
    {
        pipe.Dispose();
        var info = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        if (!info.HasVideo || info.Width is not int sw || info.Height is not int sh)
            throw new InvalidOperationException("The media file does not contain a valid video stream.");

        // Decode at native size; the compositor scales to output. Source fps drives the pipe so
        // a 24 fps clip is not upsampled to the timeline rate before compositing.
        var (rateNumerator, rateDenominator) = DecodeRate(info.Fps is > 0 ? info.Fps.Value : 30);
        pipe.FrameIntervalSeconds = rateDenominator / (double)rateNumerator;
        // RGBA, not the preview's BGRA: it is the compositor canvas format AND the encoder pipe
        // format, so a full-frame clip needs no channel swap and no composite at all.
        // WP-405: decode at the shown size; ffmpeg emits the turned coded size, and a non-square pixel
        // aspect is scaled out here so the compositor gets square pixels.
        var (dw, dh) = info.DecodedSize ?? (sw, sh);
        pipe.Decoder = await FfmpegPlaybackDecoder.StartAsync(
                path, t, rateNumerator, sw, sh, dw, dh, cancellationToken,
                pixelFormat: "rgba",
                preferHardware: UseHardwareDecode(),
                sizedTransport: UseSizedTransport(),
                outputFpsDenominator: rateDenominator)
            .ConfigureAwait(false);
        ExportTransportDiagnostics.RecordDecoder(
            pipe.Decoder.Transport, pipe.Decoder.TransportDetail, pipe.Decoder.TransportFellBack);
        pipe.LastRequestedSeconds = t;
        // The decoder was seeked to t, so t is where its NEXT emission lands — not t + one interval.
        pipe.NextSeconds = t;
        pipe.EmittedSeconds = t;
        pipe.AtEndOfStream = false;
        RestartCount++;
    }

    /// <summary>
    /// WP-274. The rate a pipe asks ffmpeg for, which is also the rate its cursor steps by. ffmpeg's
    /// fps filter emits frames on the requested rate's own grid, so a cursor stepping by any other
    /// rate drifts: asking for 60 while stepping by 59.94 put the picture 0.6 s behind the sound
    /// after ten minutes (<c>docs/verification/wp-274/</c>).
    /// <para>
    /// A 1001-family rate (23.976, 29.97, 59.94) is asked for exactly, so every source frame comes
    /// through once and the timeline gets the nearest one. Any other rate is rounded to a whole
    /// number, including a variable-rate phone clip's average, whose frames the filter then lays on
    /// that constant grid. Never below 1: a sub-0.5 fps source used to round to 0, which the
    /// decoder refuses.
    /// </para>
    /// </summary>
    internal static (int Numerator, int Denominator) DecodeRate(double sourceFps)
    {
        var ntscBase = Math.Round(sourceFps * 1.001);
        if (ntscBase is >= 1 and <= int.MaxValue / 1000
            && Math.Abs(sourceFps - ntscBase * 1000 / 1001) <= sourceFps * 1e-4)
            return ((int)ntscBase * 1000, 1001);
        return (Math.Max(1, (int)Math.Round(sourceFps)), 1);
    }

    /// <summary>
    /// Whether a restarted pipe asks for the sized transport. Reads the override every time rather
    /// than caching it: a decoder pipe is restarted at clip boundaries, and a cached answer would
    /// make the override untestable without a process restart.
    /// </summary>
    public static bool UseSizedTransport()
        => !string.Equals(EnvCompat.Get(TransportOverrideVariable), "stdout", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a restarted pipe asks ffmpeg for hardware decode. Off unless asked for.</summary>
    public static bool UseHardwareDecode()
    {
        var value = EnvCompat.Get(HardwareDecodeVariable);
        return bool.TryParse(value, out var parsed) ? parsed : value == "1";
    }

    private async Task<VideoFrame> SeekOnceAsync(string path, double t, CancellationToken cancellationToken)
    {
        var info = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        if (!info.HasVideo || info.Width is not int sw || info.Height is not int sh)
            throw new InvalidOperationException("The media file does not contain a valid video stream.");

        var ffmpegPath = FFmpegResolver.GetFFmpegPath();
        var seek = FfmpegPlaybackDecoder.ShouldEmitInputSeek(t, out var ssStr)
            ? $"-ss {ssStr} "
            : "";
        var (dw, dh) = info.DecodedSize ?? (sw, sh);
        var arguments = $"-hide_banner -loglevel error {seek}-i \"{path}\" " +
                        "-frames:v 1 " +
                        ((dw, dh) == (sw, sh) ? "" : $"-vf {FfmpegVideoFilter.Convert(sw, sh, dw, dh, "rgba")} ") +
                        "-f rawvideo -pix_fmt rgba -";

        using var process = new Process();
        process.StartInfo.FileName = ffmpegPath;
        process.StartInfo.Arguments = arguments;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start ffmpeg process: '{ffmpegPath}'");
        using var registration = ProcessCancellation.KillOnCancellation(process, cancellationToken);

        var expected = checked(sw * sh * 4);
        using var ms = new MemoryStream(expected);
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(ms, cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg exited with code {process.ExitCode}. Stderr: {stderr}");

        var data = ms.ToArray();
        if (data.Length == 0)
            throw new InvalidOperationException("No frame data was written by ffmpeg.");
        if (data.Length < expected)
        {
            var padded = new byte[expected];
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            data = padded;
        }
        else if (data.Length > expected)
        {
            var trimmed = new byte[expected];
            Buffer.BlockCopy(data, 0, trimmed, 0, expected);
            data = trimmed;
        }

        return new VideoFrame(sw, sh, data, "rgba");
    }

    private async Task<MediaProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        if (_probeCache.TryGetValue(path, out var cached))
            return cached;
        var info = await _probe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        _probeCache[path] = info;
        return info;
    }

    private static VideoFrame CloneFrame(VideoFrame frame)
        => new(frame.Width, frame.Height, (byte[])frame.Data.Clone(), frame.PixelFormat);

    /// <summary>One media path live ffmpeg pipe plus its forward-read position.</summary>
    private sealed class Pipe : IDisposable
    {
        public FfmpegPlaybackDecoder? Decoder;
        public VideoFrame? LastFrame;

        /// <summary>Source timestamp the decoder will emit NEXT (its own cadence, not the timeline's).</summary>
        public double NextSeconds;

        /// <summary>Source timestamp of <see cref="LastFrame"/>.</summary>
        public double EmittedSeconds;

        public double LastRequestedSeconds;
        public double FrameIntervalSeconds = 1.0 / 30.0;
        public bool AtEndOfStream;
        public long LastUsed;

        public void Dispose()
        {
            Decoder?.Dispose();
            Decoder = null;
            LastFrame = null;
            NextSeconds = 0;
            EmittedSeconds = 0;
            AtEndOfStream = false;
        }
    }
}
