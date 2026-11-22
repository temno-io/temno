using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;

namespace Temno.Media;

/// <summary>How the decoded bytes cross from ffmpeg into this process (WP-216).</summary>
public enum FrameTransport
{
    /// <summary>
    /// The redirected stdout pipe .NET creates. Never returns more than 32 KiB per read, so a
    /// 3840x2160 RGBA frame costs 1,013 read transitions — measured at 84.9 ms/frame.
    /// </summary>
    AnonymousPipe,

    /// <summary>
    /// A named pipe created with a frame-scale buffer. Same bytes, ~93 reads per 4K frame,
    /// measured at 16.0 ms/frame. See <c>docs/verification/wp-216/DECISION.md</c> §1.
    /// </summary>
    NamedPipe,
}

/// <summary>
/// One long-lived ffmpeg process that emits sequential BGRA frames for playback.
/// Seek-per-frame process spawn cannot keep realtime on 1440p+ sources (~700ms/frame).
/// Output is CFR at <c>outputFps</c> via the fps filter (PTS-accurate); <c>-r</c> as a muxer
/// option does not align the first frame to t=0 on VFR captures.
/// </summary>
public sealed class FfmpegPlaybackDecoder : IDisposable
{
    /// <summary>
    /// Named-pipe buffer. 1 MiB was the fastest of 1/16/64 MiB and the cheapest in non-paged pool;
    /// past 1 MiB the three are inside run-to-run spread. The win is having a buffer at all, not
    /// its size — <c>docs/verification/wp-216/DECISION.md</c> §1.
    /// </summary>
    /// <summary>Internal so the Settings row can state the size it actually is, not a literal
    /// that silently stops matching when this changes (WP-216 audit).</summary>
    internal const int NamedPipeBufferBytes = 1024 * 1024;

    /// <summary>
    /// How long to wait for ffmpeg to open the pipe before giving up and using stdout. A process
    /// that dies before connecting is detected directly rather than waited out, so this only has to
    /// cover a slow process start.
    /// </summary>
    private static readonly TimeSpan NamedPipeConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the first frame may take before the transport is judged not to work. Deliberately
    /// much larger than the connect timeout: this covers a seek into a long file on a cold cache,
    /// and falling back <i>wrongly</i> costs the whole 4x the sized transport is worth.
    /// </summary>
    private static readonly TimeSpan NamedPipeFirstFrameTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to wait for ffmpeg to actually exit after it has stopped delivering, so its exit
    /// code can be read. Short: it only runs on a failure path that is about to spawn a second
    /// ffmpeg anyway, and in the benign case (a seek past the end) the process is already gone.
    /// </summary>
    private static readonly TimeSpan PostConnectExitGrace = TimeSpan.FromSeconds(2);

    private readonly Process _process;
    private readonly Stream _stdout;
    private readonly IDisposable? _transportOwner;
    private byte[]? _discard;
    private VideoFrame? _prefetchedFrame;
    private bool _disposed;

    private FfmpegPlaybackDecoder(
        Process process, Stream stdout, int width, int height, string pixelFormat,
        FrameTransport transport = FrameTransport.AnonymousPipe,
        string? transportDetail = null,
        IDisposable? transportOwner = null,
        bool transportFellBack = false)
    {
        _process = process;
        _stdout = stdout;
        _transportOwner = transportOwner;
        Width = width;
        Height = height;
        PixelFormat = pixelFormat;
        Transport = transport;
        TransportDetail = transportDetail;
        TransportFellBack = transportFellBack;
    }

    /// <summary>Pixel format of the frames this decoder emits ("bgra" or "rgba").</summary>
    public string PixelFormat { get; }

    /// <summary>Which transport actually carried the frames — never inferred from what was asked for.</summary>
    public FrameTransport Transport { get; }

    /// <summary>
    /// Why <see cref="Transport"/> is what it is: the buffer size when the sized transport was
    /// obtained, or the reason it was not. Null when nothing was asked for.
    /// </summary>
    public string? TransportDetail { get; }

    /// <summary>
    /// True only when the sized transport was asked for and lost to something that would have
    /// carried frames — a pipe that could not be created, a name already taken, an ffmpeg that
    /// refused its command line.
    /// <para>
    /// It is deliberately <b>false</b> when the stream simply had no frames at the requested
    /// position (a clip trimmed past the end of its media seeks past EOF, ffmpeg emits nothing and
    /// exits clean). Both cases take the stdout path, because there is nothing to carry either way,
    /// but only the first is a fault. Recording the second as a fallback would leave a healthy
    /// machine permanently reporting "Fell back at least once" — a diagnostic that cries wolf is
    /// worse than none, which is the whole point of WP-215's Scope 4.
    /// </para>
    /// </summary>
    public bool TransportFellBack { get; }

    public int Width { get; }
    public int Height { get; }
    public int FrameSize => checked(Width * Height * 4);

    /// <summary>
    /// WP-248. When set, <see cref="ReadFrameAsync"/> reads into a buffer rented from this pool
    /// instead of allocating a new full frame (a large-object allocation) per read. Given to
    /// <see cref="StartAsync"/>, it also takes the frame prefetched at start.
    /// </summary>
    public FrameBufferPool? FramePool { get; set; }

    /// <param name="outputFpsDenominator">
    /// WP-274. The output rate is <paramref name="outputFps"/> / this, so export can ask for a
    /// 60000/1001 source's own rate instead of 60. The default of 1 leaves the command line exactly
    /// as it was; preview never passes it.
    /// </param>
    public static async Task<FfmpegPlaybackDecoder> StartAsync(
        string path,
        double startSeconds,
        int outputFps,
        int width,
        int height,
        int sourceWidth = 0,
        int sourceHeight = 0,
        CancellationToken cancellationToken = default,
        string pixelFormat = "bgra",
        bool preferHardware = false,
        bool sizedTransport = false,
        FrameBufferPool? framePool = null,
        int outputFpsDenominator = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("Media file not found.", path);
        if (outputFps <= 0) throw new ArgumentOutOfRangeException(nameof(outputFps));
        if (outputFpsDenominator <= 0) throw new ArgumentOutOfRangeException(nameof(outputFpsDenominator));
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(width <= 0 ? nameof(width) : nameof(height));
        cancellationToken.ThrowIfCancellationRequested();
        var outputRate = FormatOutputRate(outputFps, outputFpsDenominator);

        if (preferHardware && OperatingSystem.IsWindows())
        {
            FfmpegPlaybackDecoder? candidate = null;
            try
            {
                candidate = await StartWithTransportAsync(path, startSeconds, outputRate, width, height,
                    sourceWidth, sourceHeight, pixelFormat, useHardware: true, sizedTransport,
                    framePool, cancellationToken).ConfigureAwait(false);
                // Opening the process is not proof that this device/codec can decode a frame.
                // On the named-pipe transport the frame is already prefetched, and ReadFrameAsync
                // hands that one back — this assignment then puts it where it was. The hardware
                // check is satisfied either way: a frame decoded.
                using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startup.CancelAfter(TimeSpan.FromSeconds(5));
                candidate._prefetchedFrame = await candidate.ReadFrameAsync(startup.Token).ConfigureAwait(false);
                return candidate;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                candidate?.Dispose();
            }
            catch (OperationCanceledException)
            {
                candidate?.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                candidate?.Dispose();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return await StartWithTransportAsync(path, startSeconds, outputRate, width, height,
            sourceWidth, sourceHeight, pixelFormat, useHardware: false, sizedTransport,
            framePool, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// WP-216. Starts the decoder over a named pipe we size ourselves when <paramref name="sizedTransport"/>
    /// asks for it, and over the redirected stdout pipe otherwise — or when the named pipe cannot be
    /// had. The fallback records why in <see cref="TransportDetail"/>: a transport that silently
    /// reverts costs 69 ms/frame at 4K and looks exactly like one that worked.
    /// </summary>
    private static async Task<FfmpegPlaybackDecoder> StartWithTransportAsync(
        string path, double startSeconds, string outputRate, int width, int height,
        int sourceWidth, int sourceHeight, string pixelFormat, bool useHardware,
        bool sizedTransport, FrameBufferPool? framePool, CancellationToken cancellationToken)
    {
        string? fallbackReason = null;
        var fellBack = false;

        if (sizedTransport && OperatingSystem.IsWindows())
        {
            NamedPipeServerStream? server = null;
            Process? process = null;
            try
            {
                var name = "temno-frames-" + Guid.NewGuid().ToString("N");
                // FirstPipeInstance fails rather than joining a name someone else already owns;
                // CurrentUserOnly keeps the ACL to this user. A local process must not be able to
                // answer a decode with pixels of its choosing.
                server = new NamedPipeServerStream(
                    name, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance | PipeOptions.CurrentUserOnly,
                    inBufferSize: NamedPipeBufferBytes, outBufferSize: 0);

                process = StartProcess(path, startSeconds, outputRate, width, height,
                    sourceWidth, sourceHeight, pixelFormat, useHardware, NamedPipeUrl(name));

                using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connect.CancelAfter(NamedPipeConnectTimeout);

                // Race the connection against the process dying. ffmpeg that refuses its command
                // line never opens the pipe, and waiting the full timeout out on every restart
                // would turn one bad argument into ten seconds per clip boundary.
                // Whichever of the two loses the race is abandoned, and the loser can still fault
                // afterwards — WaitForConnectionAsync throws ObjectDisposedException the moment the
                // catch below disposes the server. An abandoned faulted Task surfaces as a
                // TaskScheduler.UnobservedTaskException in the crash log, which this repo has
                // already been bitten by once (WORK_LEDGER §4a.2), so both are observed up front.
                var connected = Observe(server.WaitForConnectionAsync(connect.Token));
                var exited = Observe(process.WaitForExitAsync(connect.Token));
                if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited
                    && !connected.IsCompletedSuccessfully)
                {
                    // Exit 0 without ever opening the output is ffmpeg saying there were no packets
                    // to write — a seek past the end of the media. That is an exhausted stream, not
                    // a broken transport, and the two are told apart below.
                    throw process.ExitCode == 0
                        ? new EndOfStreamException("ffmpeg produced no frames at this position.")
                        : new InvalidOperationException(
                            $"ffmpeg exited with code {process.ExitCode} before opening the pipe");
                }
                await connected.ConfigureAwait(false);

                var candidate = new FfmpegPlaybackDecoder(
                    process, server, width, height, pixelFormat,
                    FrameTransport.NamedPipe, $"{NamedPipeBufferBytes / 1024} KiB buffer", server)
                {
                    FramePool = framePool,
                };

                // Connecting is not proof that a frame will arrive: ffmpeg opens the pipe to decide
                // whether the "file" exists and can close again without writing a byte. Pull the
                // first frame here so a transport that cannot deliver falls back to stdout, instead
                // of surfacing as an end-of-stream that ExportFrameSource answers with a
                // process-per-frame seek at 500 ms/frame. Found exactly that way.
                Exception? prefetchFailure = null;
                try
                {
                    using var firstFrame = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    firstFrame.CancelAfter(NamedPipeFirstFrameTimeout);
                    candidate._prefetchedFrame = await candidate.ReadFrameAsync(firstFrame.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    prefetchFailure = ex;
                }

                if (prefetchFailure is not null)
                {
                    // Classify BEFORE disposing. Dispose takes the process with it, and the exit
                    // code is the only thing that tells an exhausted stream apart from an ffmpeg
                    // that opened the pipe and then failed. WP-216 T2 P1: without this, every
                    // post-connect failure — including the measured `-y` shape, where ffmpeg opens
                    // the pipe, writes nothing and exits non-zero — was filed as "no frames at this
                    // position", so the machine fell back to a 4x slower transport with no alarm.
                    var classified = await ClassifyPostConnectFailureAsync(
                        process, prefetchFailure, cancellationToken).ConfigureAwait(false);
                    candidate.Dispose();   // owns the process and the server from here
                    process = null;
                    server = null;
                    throw classified;
                }

                return candidate;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                KillQuietly(process);
                server?.Dispose();
                throw;
            }
            catch (EndOfStreamException)
            {
                // The stream had nothing at this position. Stdout will produce nothing either, and
                // ExportFrameSource already knows what to do with that (hold the last frame). One
                // wasted ffmpeg spawn on a path that was going to end in EOF regardless; not a
                // fault, and not reported as one.
                KillQuietly(process);
                server?.Dispose();
                fallbackReason = "no frames at this position; the stream is exhausted";
            }
            catch (Exception ex)
            {
                // ffmpeg never opened the pipe, the name was taken, or the platform refused it.
                KillQuietly(process);
                server?.Dispose();
                fellBack = true;
                // WP-216 T2 P2: this used to read "did not open the pipe within 10s" for a
                // first-frame timeout as well, which is a lie in the log and in Settings — the pipe
                // had opened. Only a cancellation that reaches here *without* having connected is
                // the connect timeout; everything else carries its own message.
                fallbackReason = ex is OperationCanceledException
                    ? $"ffmpeg did not open the pipe within {NamedPipeConnectTimeout.TotalSeconds:F0}s"
                    : ex.GetType().Name + ": " + ex.Message;
            }
        }
        else if (sizedTransport)
        {
            fellBack = true;
            fallbackReason = "named-pipe transport is Windows-only";
        }

        cancellationToken.ThrowIfCancellationRequested();
        var plain = StartProcess(path, startSeconds, outputRate, width, height,
            sourceWidth, sourceHeight, pixelFormat, useHardware, outputUrl: "-");
        return new FfmpegPlaybackDecoder(
            plain, plain.StandardOutput.BaseStream, width, height, pixelFormat,
            FrameTransport.AnonymousPipe, fallbackReason, transportOwner: null, transportFellBack: fellBack)
        {
            FramePool = framePool,
        };
    }

    /// <summary>
    /// WP-216 T2 P1 and P2. The pipe connected and then produced no frame. Three very different
    /// things look identical at that point, and only ffmpeg's exit code separates them:
    /// <list type="bullet">
    /// <item>a seek past the end of the media — ffmpeg writes no packets and exits <b>0</b>. Not a
    /// fault, and must not raise the alarm, or every healthy machine reports a fallback it never
    /// had (the defect this package already fixed once).</item>
    /// <item>ffmpeg opening the pipe and then <b>failing</b> — a non-zero exit. This is a real
    /// fallback: the export silently drops to a transport measured 4x slower, and before this
    /// check nothing anywhere said so.</item>
    /// <item>a first-frame <b>timeout</b> — ffmpeg is still alive and simply has not delivered.
    /// Reported as itself rather than as the connect timeout, which it is not.</item>
    /// </list>
    /// Returns the exception the caller should throw; the outer handlers classify on its type.
    /// </summary>
    internal static async Task<Exception> ClassifyPostConnectFailureAsync(
        Process process, Exception original, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return original;

        var timedOut = original is OperationCanceledException;
        try
        {
            // ffmpeg has closed its end of the pipe, so exit is imminent in the failure cases;
            // the grace is bounded so a still-running process cannot hold a restart open.
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grace.CancelAfter(PostConnectExitGrace);
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Still running (or unobservable) after the grace. Either way this is a transport that
            // does not work rather than an empty stream: an exhausted stream ends with ffmpeg
            // exiting 0, promptly. T2 round 2 flagged that the second arm here used to return the
            // original end-of-stream and so stayed silent, contradicting this very comment — a
            // process that closes the pipe without writing a frame and then does not exit is the
            // clearest broken transport there is.
            return timedOut
                ? new TimeoutException(
                    $"ffmpeg opened the pipe but delivered no frame within {NamedPipeFirstFrameTimeout.TotalSeconds:F0}s")
                : new InvalidOperationException(
                    "ffmpeg closed the pipe without writing a frame and did not exit");
        }

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (Exception)
        {
            return original;
        }

        if (exitCode != 0)
        {
            return new InvalidOperationException(
                $"ffmpeg exited with code {exitCode} after opening the pipe without writing a frame");
        }

        // Exit 0 and no frame: an exhausted stream. Preserve an exhaustion signal so the caller
        // keeps treating it as one, even when the trigger was the first-frame timeout.
        return timedOut
            ? new EndOfStreamException("ffmpeg produced no frames at this position.")
            : original;
    }

    /// <summary><c>\\.\pipe\name</c>, spelled without an escape thicket.</summary>
    private static string NamedPipeUrl(string name)
        => new string(Path.DirectorySeparatorChar, 2) + "." + Path.DirectorySeparatorChar + "pipe"
           + Path.DirectorySeparatorChar + name;

    /// <summary>
    /// Marks a task's exception as observed without changing what awaiting it does. Returns the
    /// same task so it reads as a wrapper at the call site.
    /// </summary>
    private static Task Observe(Task task)
    {
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return task;
    }

    private static void KillQuietly(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        try { process.Dispose(); } catch { /* ignore */ }
    }

    /// <summary>
    /// The fps filter's rate argument. An integer rate formats exactly as it always has, which is
    /// what keeps preview's command line byte-identical (WP-274).
    /// </summary>
    internal static string FormatOutputRate(int numerator, int denominator)
        => denominator == 1
            ? numerator.ToString(CultureInfo.InvariantCulture)
            : numerator.ToString(CultureInfo.InvariantCulture) + "/" + denominator.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats an input <c>-ss</c> seek argument when <paramref name="seconds"/> is strictly positive.
    /// At 0 s, omitting <c>-ss</c> starts video at frame 0 while avoiding FFmpeg's <c>image2</c> demuxer
    /// seeking JPEG stills past their single frame to EOF (same root cause as WP-420 in thumbnails).
    /// </summary>
    internal static bool ShouldEmitInputSeek(double seconds, out string formatted)
    {
        if (seconds > 0 && double.IsFinite(seconds))
        {
            var text = seconds.ToString("0.000###", CultureInfo.InvariantCulture);
            if (text != "0.000")
            {
                formatted = text;
                return true;
            }
        }

        formatted = string.Empty;
        return false;
    }

    private static Process StartProcess(string path, double startSeconds, string outputRate,
        int width, int height, int sourceWidth, int sourceHeight, string pixelFormat, bool useHardware,
        string outputUrl)
    {

        var ffmpegPath = FFmpegResolver.GetFFmpegPath();

        var process = new Process();
        process.StartInfo.FileName = ffmpegPath;
        process.StartInfo.UseShellExecute = false;
        // Only the stdout transport needs the redirect; the named-pipe transport leaves ffmpeg's
        // stdout alone so nothing has to drain a second stream to stop it filling.
        process.StartInfo.RedirectStandardOutput = outputUrl == "-";
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-loglevel");
        process.StartInfo.ArgumentList.Add("error");
        // A named pipe exists on disk as far as ffmpeg's file protocol is concerned, so without -y
        // it stops on "File '\\.\pipe\...' already exists. Overwrite? [y/N]", writes nothing, and
        // closes — which reaches us as an immediate end of stream rather than as an error. This
        // decoder never writes a real file: its output is stdout or a pipe we just created.
        process.StartInfo.ArgumentList.Add("-y");
        if (ShouldEmitInputSeek(startSeconds, out var ss))
        {
            process.StartInfo.ArgumentList.Add("-ss");
            process.StartInfo.ArgumentList.Add(ss);
        }
        if (useHardware)
        {
            // Decode on D3D11VA, then let FFmpeg convert the downloaded YUV explicitly.
            // Windows' video processor would also apply the user's video-only contrast settings.
            process.StartInfo.ArgumentList.Add("-hwaccel");
            process.StartInfo.ArgumentList.Add("d3d11va");
        }
        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(path);
        process.StartInfo.ArgumentList.Add("-an");
        process.StartInfo.ArgumentList.Add("-vf");
        process.StartInfo.ArgumentList.Add(
            $"setpts=PTS-STARTPTS,fps={outputRate}:start_time=0:round=near," +
            FfmpegVideoFilter.Convert(width, height, sourceWidth, sourceHeight, pixelFormat));
        process.StartInfo.ArgumentList.Add("-pix_fmt");
        process.StartInfo.ArgumentList.Add(pixelFormat);
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add("rawvideo");
        process.StartInfo.ArgumentList.Add(outputUrl);

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException($"Failed to start ffmpeg process: '{ffmpegPath}'");
        }

        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
        return process;
    }

    public async Task<VideoFrame> ReadFrameAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        // Taken atomically: Dispose may race a read (a Reset stopping a pipe mid-request), and a
        // pooled frame handed to both would be recycled while its reader still holds it.
        if (Interlocked.Exchange(ref _prefetchedFrame, null) is { } prefetched)
            return prefetched;
        if (FramePool is not { } pool)
        {
            var data = new byte[FrameSize];
            await ReadExactAsync(data, FrameSize, cancellationToken).ConfigureAwait(false);
            return new VideoFrame(Width, Height, data, PixelFormat);
        }

        var frame = pool.Rent(Width, Height, PixelFormat);
        // A read that fails leaves its buffer abandoned, not returned: it is never handed to the
        // pool while there is any chance a cancelled read is still landing in it.
        await ReadExactAsync(frame.Data, FrameSize, cancellationToken).ConfigureAwait(false);
        return frame;
    }

    public Task DiscardFrameAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _prefetchedFrame, null) is { } prefetched)
        {
            prefetched.Dispose();
            return Task.CompletedTask;
        }
        // WP-248: made on the first discard, not at start. A preview decoder started ahead of a cut
        // usually never discards, and a full frame per start was enough large-object allocation to
        // set off a gen-2 collection in the middle of playback.
        return ReadExactAsync(_discard ??= new byte[FrameSize], FrameSize, cancellationToken);
    }

    private async Task ReadExactAsync(byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < count)
        {
            var n = await _stdout.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
                throw new EndOfStreamException("ffmpeg playback pipe closed.");
            offset += n;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Exchange(ref _prefetchedFrame, null)?.Dispose();
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // process already gone
        }

        try { _stdout.Dispose(); } catch { /* ignore */ }
        // The named-pipe server IS _stdout, so this is a second Dispose on the same object in that
        // case — idempotent by contract. It is written out rather than special-cased because the
        // owner is null on the stdout transport and forgetting it would leak a pipe instance.
        try { _transportOwner?.Dispose(); } catch { /* ignore */ }
        try { _process.Dispose(); } catch { /* ignore */ }
    }
}
