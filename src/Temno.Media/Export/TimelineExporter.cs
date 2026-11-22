using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using Temno.Core.Export;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Media.Audio;
using Temno.Media.Compositing;
using Temno.Media.Effects;
using Temno.Media.Hardware;
using Temno.Media.Playback;

namespace Temno.Media.Export;

/// <summary>
/// Exports a timeline to an mp4 file by rendering each frame through the
/// compositor and mixing audio, piping both to FFmpeg's stdin.
/// Mac: Export/ExportService.swift + AVAssetWriter pipeline.
///
/// Strategy: Two-pass approach.
///   Pass 1: Render video → raw RGBA pipe → FFmpeg encodes to temp .mp4 (video-only).
///   Pass 2: Mix audio → raw PCM pipe → FFmpeg muxes audio into the final .mp4.
///
/// Defaults to PreferHardware so NVENC is used when the installed FFmpeg exposes it.
/// </summary>
public sealed class TimelineExporter : IDisposable
{
    /// <summary>
    /// Depth of the frame-ahead render pipeline. Bounded to prevent runaway memory usage:
    /// a 4K RGBA frame is 31.6 MB, so the queue caps at ~63 MB plus the two in flight.
    /// </summary>
    private const int FrameAheadDepth = 2;

    private readonly ICompositor _compositor;
    private readonly IFrameSource _frameSource;
    private readonly MediaResolver? _mediaResolver;
    private readonly TimelineFrameRenderer _timelineRenderer;
    private readonly TimelineAudioDecoder _audioDecoder;
    private readonly Func<string, Timeline?>? _timelineLookup;
    private readonly HardwareAccelerationMode _accelMode;
    private readonly bool _ownsFrameSource;
    private readonly IVideoStreamProbe? _streamProbe;
    private bool _disposed;

    /// <param name="ownsFrameSource">
    /// True when disposing the exporter should also dispose <paramref name="frameSource"/>. The app
    /// constructs one per export and has no other handle on it, so it must be owned or its decoder
    /// pipes stay alive until the GC happens to reclaim them.
    /// </param>
    public TimelineExporter(
        ICompositor compositor,
        IFrameSource frameSource,
        MediaResolver? mediaResolver = null,
        IAudioSource? audioSource = null,
        Func<string, Timeline?>? timelineLookup = null,
        HardwareAccelerationMode acceleration = HardwareAccelerationMode.PreferHardware,
        bool ownsFrameSource = false,
        IVideoStreamProbe? streamProbe = null)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frameSource);
        _compositor = compositor;
        _frameSource = frameSource;
        _mediaResolver = mediaResolver;
        _timelineLookup = timelineLookup;
        _accelMode = acceleration;
        _streamProbe = streamProbe;
        _timelineRenderer = new TimelineFrameRenderer(
            frameSource,
            mediaResolver,
            compositor,
            EffectsRendererFactory.Create(acceleration),
            timelineLookup: timelineLookup);
        _audioDecoder = new TimelineAudioDecoder(audioSource);
        _ownsFrameSource = ownsFrameSource;
    }

    /// <summary>
    /// Releases the effects renderer (and its D3D11 device) plus, when owned, the frame source and
    /// its live decoder pipes.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timelineRenderer.Dispose();
        if (_ownsFrameSource)
            (_frameSource as IDisposable)?.Dispose();
    }

    /// <summary>Encoder name used by the most recent successful <see cref="ExportAsync"/> call.</summary>
    public string? LastEncoderName { get; private set; }

    /// <summary>
    /// Set when hardware encoding was preferred but the frame is past the hardware limit, so the
    /// export ran in software. The export status shows this sentence.
    /// </summary>
    public string? LastSoftwareEncodeReason { get; private set; }

    /// <summary>Milliseconds spent in the video encode pass of the most recent export (WP-114).</summary>
    public double LastVideoEncodeMilliseconds { get; private set; }

    /// <summary>Smart-render plan evaluated during the most recent export, if any.</summary>
    public SmartRenderPlan? LastSmartRenderPlan { get; private set; }

    /// <summary>
    /// Why the most recent export abandoned smart rendering for a full re-encode — analysis or a
    /// verification gate failed — or null when it was not attempted or succeeded. The fallback keeps
    /// the file correct; this keeps the lost stream copy visible (WP-234).
    /// </summary>
    public string? LastSmartRenderFallbackReason { get; private set; }

    /// <summary>When true, disables smart rendering and forces conventional full re-encode.</summary>
    public bool DisableSmartRender { get; set; }

    /// <summary>
    /// Raised after each frame is rendered. Args: (currentFrame, totalFrames).
    /// </summary>
    public event Action<int, int>? Progress;

    /// <summary>
    /// Source clips skipped while preparing the most recent export. A non-empty result means a
    /// valid source mix was still encoded, but it did not include every requested audio clip.
    /// </summary>
    public IReadOnlyList<TimelineAudioDiagnostic> LastAudioDiagnostics { get; private set; } = [];

    /// <summary>Applied, bypassed, or unavailable audio effects from the most recent export decode.</summary>
    public IReadOnlyList<TimelineAudioEffectStatus> LastAudioEffects { get; private set; } = [];

    /// <summary>
    /// Visual layers dropped during the most recent export, one entry per distinct clip. A
    /// non-empty result means the file encoded fine but is missing picture the timeline asked for.
    /// </summary>
    public IReadOnlyList<TimelineFrameRenderer.SkippedLayer> LastVideoDiagnostics { get; private set; } = [];

    /// <summary>
    /// WP-154. When false — the default — an export that dropped some but not all of its picture
    /// fails instead of returning a file with holes in it. Before this, only a *wholly* black file
    /// was refused: relink one of three clips and the export reported success and quietly lost a
    /// third of the picture, discoverable only by watching the result.
    /// <para>
    /// A rough cut over offline media is a legitimate thing to want, so the editor sets this to true
    /// after it has shown the user exactly which clips will be dropped and they have chosen to go
    /// ahead. It stays false everywhere else, which means a caller that cannot ask — the MCP
    /// <c>export_project</c> tool — is told what is offline rather than handed the file.
    /// </para>
    /// </summary>
    public bool AllowPartialPicture { get; set; }

    /// <summary>
    /// Exports the timeline to <paramref name="outputPath"/> using the given preset, replacing any
    /// file already there.
    /// </summary>
    public Task ExportAsync(
        Timeline timeline,
        ExportPreset preset,
        string outputPath,
        CancellationToken cancellationToken = default)
        => ExportAsync(timeline, preset, outputPath, overwrite: true, cancellationToken);

    /// <summary>
    /// Exports the timeline to <paramref name="outputPath"/> using the given preset. With
    /// <paramref name="overwrite"/> false nothing at the destination is replaced (WP-403): the export
    /// fails before it renders if something is there, and the publish itself refuses anything that
    /// reached the destination while it rendered. Either way the error names the path.
    /// </summary>
    public async Task ExportAsync(
        Timeline timeline,
        ExportPreset preset,
        string outputPath,
        bool overwrite,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path is required.", nameof(outputPath));

        // A queued job starts minutes after the caller's own check, so check again before a render
        // that the publish would only throw away.
        if (!overwrite && ExportDestination.Inspect(outputPath) != ExportDestinationKind.Absent)
            throw DestinationTaken(outputPath, rendered: false);

        var ffmpeg = FFmpegResolver.GetFFmpegPath();

        // The file is the fitted box. The compositor renders the timeline's own shape, and a
        // custom box of another shape is padded in the encode so the picture is never stretched.
        var fitted = ExportOutputSize.ResolveFrame(timeline, preset);
        var outputFps = ExportFrameRate.ResolveOverride(preset.CustomFps, timeline.Fps);
        var codedW = fitted.Width;
        var codedH = fitted.Height;
        var renderW = fitted.ContentWidth;
        var renderH = fitted.ContentHeight;
        var totalFrames = timeline.TotalFrames;
        if (totalFrames <= 0)
            throw new InvalidOperationException("Timeline has no content to export.");

        LastSmartRenderPlan = null;
        LastSmartRenderFallbackReason = null;
        LastSoftwareEncodeReason = null;

        // maxBitDepth forces the 10-bit HEVC HDR encode path (Mac Export dialog parity).
        var effectiveCodec = preset.MaxBitDepth && preset.Codec is not VideoCodec.HevcHdr
            ? VideoCodec.HevcHdr
            : preset.Codec;

        // Preconditions first: everything below decodes audio or renders frames, and a destination
        // ffmpeg will refuse should cost a second to discover, not a full render.
        ExportContainer.EnsureCodecMatchesContainer(effectiveCodec, outputPath);
        if (effectiveCodec == VideoCodec.HevcHdr)
            FfmpegEncoderCapabilities.RequireHdrColorSupport();

        var preferHw = _accelMode is HardwareAccelerationMode.PreferHardware
            or HardwareAccelerationMode.ForceHardwareWhereAvailable;
        // Known before the first frame: trying hardware and waiting for it to reject the size
        // would burn a full render, then surface ffmpeg's error. Software is chosen up front.
        if (preferHw && FfmpegEncoderCapabilities.ExceedsHardwareEncoderLimit(effectiveCodec, codedW, codedH))
        {
            preferHw = false;
            LastSoftwareEncodeReason = FfmpegEncoderCapabilities.SoftwareEncodeReason(codedW, codedH);
        }

        var codec = FfmpegEncoderCapabilities.RequireEncoder(effectiveCodec, preferHardware: preferHw);
        LastEncoderName = codec;

        var decodedAudio = await DecodeExportAudioAsync(timeline, cancellationToken).ConfigureAwait(false);
        var hasAudio = decodedAudio is not null;

        var bitrate = preset.BitrateMbps ?? ExportEncodeArguments.DefaultBitrateMbps(codedW, codedH, effectiveCodec);
        var crf = preset.MaxRenderQuality ? 18 : 23;
        var containerIsMov = effectiveCodec is VideoCodec.ProRes or VideoCodec.HevcHdr;

        // Both passes write to temp files: a cancelled or failed export must not leave a truncated,
        // unplayable file sitting at the destination the user picked. The final result is moved into
        // place only after every pass has succeeded.
        var tempVideoPath = Path.Combine(Path.GetTempPath(),
            $"temno_export_{Guid.NewGuid():N}_video{(containerIsMov ? ".mov" : ".mp4")}");
        var tempFinalPath = hasAudio
            ? Path.Combine(Path.GetTempPath(),
                $"temno_export_{Guid.NewGuid():N}_final{Path.GetExtension(outputPath)}")
            : tempVideoPath;

        try
        {
            // === Pass 1: Video ===
            var sw = Stopwatch.StartNew();
            var totalFramesWithContent = 0;
            var skipped = new Dictionary<string, TimelineFrameRenderer.SkippedLayer>(StringComparer.Ordinal);
            void RecordSkipped(TimelineFrameRenderer.SkippedLayer s)
            {
                lock (skipped) skipped.TryAdd(s.ClipId, s);
            }

            var smartRenderAttempted = false;
            var smartRenderSucceeded = false;

            // A padded file or resampled frame rate does not match the source bitstream. Stream-copying
            // would drop bars, stretch picture, or emit the wrong frame rate, so smart render stays off.
            if (fitted.IsPadded)
                LastSmartRenderFallbackReason = "padded to a different shape";
            else if (outputFps is { } targetRate)
                LastSmartRenderFallbackReason = $"resampled to {ExportFrameRate.FormatLabel(targetRate)}";

            if (!DisableSmartRender && !fitted.IsPadded && outputFps is null)
            {
                SmartRenderPlan? plan = null;
                try
                {
                    var analyzer = new SmartRenderAnalyzer(_streamProbe ?? new FfmpegStreamProbe(), _mediaResolver);
                    plan = await analyzer.AnalyzeAsync(timeline, effectiveCodec, codedW, codedH, totalFrames, cancellationToken)
                        .ConfigureAwait(false);
                    LastSmartRenderPlan = plan;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Analysis failure falls back to conventional encode.
                    LastSmartRenderFallbackReason = $"smart-render analysis failed: {ex.Message}";
                }

                if (plan is not null && plan.HasCopySegments)
                {
                    smartRenderAttempted = true;
                    try
                    {
                        var executor = new SmartRenderExecutor();
                        totalFramesWithContent = await executor.ExecuteAsync(
                            plan,
                            ffmpeg,
                            FFmpegResolver.GetFFprobePath(),
                            effectiveCodec,
                            tempVideoPath,
                            (startFrame, frameCount, framesDoneBefore, segPath, ct) => RenderVideoPassAsync(
                                ffmpeg, timeline, renderW, renderH, codedW, codedH,
                                startFrame, frameCount, totalFrames, framesDoneBefore,
                                codec, bitrate, crf, preset.BitrateMbps.HasValue, effectiveCodec,
                                writeFaststart: false, segPath, RecordSkipped, ct),
                            (done, total) => Progress?.Invoke(done, total),
                            cancellationToken).ConfigureAwait(false);

                        smartRenderSucceeded = true;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Any exception from the smart path or verification gate triggers fallback to conventional full encode.
                        DeleteIfExists(tempVideoPath);
                        smartRenderSucceeded = false;
                        LastSmartRenderFallbackReason = ex.Message;
                    }
                }
            }

            if (!smartRenderSucceeded)
            {
                if (smartRenderAttempted)
                {
                    lock (skipped) skipped.Clear();
                }

                try
                {
                    totalFramesWithContent = await RenderVideoPassAsync(
                        ffmpeg, timeline, renderW, renderH, codedW, codedH,
                        0, totalFrames, totalFrames, 0,
                        codec, bitrate, crf, preset.BitrateMbps.HasValue, effectiveCodec,
                        writeFaststart: !hasAudio, tempVideoPath, RecordSkipped, cancellationToken,
                        outputFps).ConfigureAwait(false);
                }
                catch (FfmpegEncodeException ex)
                    when (preferHw && IsHardwareEncoder(codec)
                          && ex.IsInitializationFailure && !IsCancellation(ex, cancellationToken))
                {
                    // NVENC/QSV/AMF refuse the job at init (frame below the encoder's minimum dimension,
                    // unsupported rate control, no such device — note `ffmpeg -encoders` lists an encoder
                    // the build supports, which says nothing about this machine having the hardware).
                    // Retry on software only for init failures: a fault mid-render is not an encoder
                    // selection problem, and re-rendering from frame 0 would double an hour-long export.
                    var software = FfmpegEncoderCapabilities.RequireEncoder(effectiveCodec, preferHardware: false);
                    if (software == codec) throw;
                    codec = software;
                    LastEncoderName = codec;
                    lock (skipped) skipped.Clear();
                    totalFramesWithContent = await RenderVideoPassAsync(
                        ffmpeg, timeline, renderW, renderH, codedW, codedH,
                        0, totalFrames, totalFrames, 0,
                        codec, bitrate, crf, preset.BitrateMbps.HasValue, effectiveCodec,
                        writeFaststart: !hasAudio, tempVideoPath, RecordSkipped, cancellationToken,
                        outputFps).ConfigureAwait(false);
                }
            }

            lock (skipped) LastVideoDiagnostics = skipped.Values.ToList();

            // Layers were dropped. Refuse rather than hand back a file that is missing picture the
            // timeline asked for. Only meaningful with a resolver: without one the exporter cannot
            // tell "the media moved" apart from "no media resolution was configured", which is how
            // the unit fakes are wired.
            //
            // WP-154: this used to fire only when totalFramesWithContent == 0, i.e. when *every*
            // layer was skipped. A partial skip returned success and a file with holes in it.
            if (_mediaResolver is not null && LastVideoDiagnostics.Count > 0
                && (totalFramesWithContent == 0 || !AllowPartialPicture))
            {
                // The manifest name, not the asset id: "58FFF09C-0255-..." tells the user nothing
                // about which clip to relink.
                var names = string.Join(", ", LastVideoDiagnostics.Take(5).Select(d =>
                {
                    var label = _mediaResolver.Entry(d.MediaRef)?.Name;
                    return $"{(string.IsNullOrWhiteSpace(label) ? d.MediaRef : label)} ({d.Reason})";
                }));
                var more = LastVideoDiagnostics.Count > 5 ? $" and {LastVideoDiagnostics.Count - 5} more" : "";
                var lead = totalFramesWithContent == 0
                    ? "Export produced no picture: every visual layer was skipped"
                    : $"Export would be missing picture: {LastVideoDiagnostics.Count} "
                        + $"{(LastVideoDiagnostics.Count == 1 ? "clip" : "clips")} could not be drawn";
                throw new InvalidOperationException(
                    $"{lead} — {names}{more}. Relink the media and export again.");
            }

            sw.Stop();
            LastVideoEncodeMilliseconds = sw.Elapsed.TotalMilliseconds;

            // === Pass 2: Mux audio (if any) ===
            if (hasAudio)
            {
                await MuxAudioPassAsync(
                    ffmpeg, timeline, decodedAudio!.Segments, tempVideoPath, tempFinalPath,
                    totalFrames, hdr: effectiveCodec == VideoCodec.HevcHdr, cancellationToken)
                    .ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            PublishResult(tempFinalPath, outputPath, overwrite);
        }
        finally
        {
            DeleteIfExists(tempVideoPath);
            if (!string.Equals(tempFinalPath, tempVideoPath, StringComparison.OrdinalIgnoreCase))
                DeleteIfExists(tempFinalPath);
        }
    }

    /// <summary>
    /// Moves the finished render onto the destination. With <paramref name="overwrite"/> true it replaces
    /// what is there, as it always has. With false (WP-403) the render is first moved, or copied, beside
    /// the destination under a new unique name, then renamed onto the destination without replacing. A
    /// rename within one folder refuses anything at the name, a dangling link included, and never follows
    /// a link; Windows' move across volumes copies, and would have followed a dangling link and written
    /// the render at its target. So nothing that reaches the destination after the caller's check is
    /// replaced or written through.
    /// </summary>
    internal static void PublishResult(string tempPath, string outputPath, bool overwrite)
        => PublishResult(tempPath, outputPath, overwrite, File.Move, File.Copy);

    /// <summary><see cref="PublishResult(string, string, bool)"/> with the move and copy as seams for tests.</summary>
    internal static void PublishResult(
        string tempPath,
        string outputPath,
        bool overwrite,
        Action<string, string, bool> move,
        Action<string, string, bool> copy)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (overwrite)
        {
            MoveOrCopy(tempPath, outputPath, replace: true, move, copy);
            return;
        }

        var staging = StagingPath(outputPath);
        try
        {
            MoveOrCopy(tempPath, staging, replace: false, move, copy);
            try
            {
                move(staging, outputPath, false);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)
                                       && ExportDestination.IsOccupied(outputPath))
            {
                throw DestinationTaken(outputPath, rendered: true);
            }
        }
        finally
        {
            DeleteIfExists(staging);
        }
    }

    private static void MoveOrCopy(
        string source,
        string destination,
        bool replace,
        Action<string, string, bool> move,
        Action<string, string, bool> copy)
    {
        try
        {
            move(source, destination, replace);
        }
        catch (IOException)
        {
            // A move that cannot cross volumes (temp on C:, output on D:) falls back to a copy.
            copy(source, destination, replace);
            DeleteIfExists(source);
        }
    }

    /// <summary>A name beside the destination that nothing else uses, so the last step is a rename in one folder.</summary>
    private static string StagingPath(string outputPath)
    {
        var full = Path.GetFullPath(outputPath);
        return Path.Combine(
            Path.GetDirectoryName(full)!,
            $".{Path.GetFileNameWithoutExtension(full)}.{Guid.NewGuid():N}.partial{Path.GetExtension(full)}");
    }

    /// <summary>
    /// The refusal for an export that may not replace what is at its destination. The wording matches
    /// export_project's call-time refusal, since an omitted outputPath is judged as overwrite=false too.
    /// </summary>
    private static IOException DestinationTaken(string outputPath, bool rendered) => new(
        $"outputPath already exists and overwrite=false: {outputPath}. "
        + (rendered
            ? "It appeared while the export rendered; the render was discarded and what is there was left as it was."
            : "It appeared after the export was queued; nothing was rendered and what is there was left as it was."));

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort cleanup */ }
    }

    private async Task<int> RenderVideoPassAsync(
        string ffmpegPath,
        Timeline timeline,
        int renderW, int renderH,
        int codedW, int codedH,
        int startFrame, int frameCount, int totalFrames, int framesDoneBefore,
        string codec, double bitrateMbps, int crf,
        bool explicitBitrate,
        VideoCodec presetCodec,
        bool writeFaststart,
        string outputPath,
        Action<TimelineFrameRenderer.SkippedLayer>? onLayerSkipped,
        CancellationToken ct,
        double? outputFps = null)
    {
        var args = ExportEncodeArguments.BuildVideoPassArguments(
            codec, renderW, renderH, timeline.Fps, presetCodec, bitrateMbps, crf,
            explicitBitrate, writeFaststart, outputPath, codedW, codedH, outputFps);

        using var process = StartFfmpegProcess(ffmpegPath, args);
        using var cancellationRegistration = ProcessCancellation.KillOnCancellation(process, ct);
        var stdin = process.StandardInput.BaseStream;
        // Drain stderr (and the unused stdout) concurrently with writing frames. Otherwise ffmpeg
        // blocks writing encode progress to a full stderr pipe, stops reading stdin, and the frame
        // loop deadlocks on stdin.WriteAsync.
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);

        void OnSkipped(TimelineFrameRenderer.SkippedLayer s) => onLayerSkipped?.Invoke(s);
        _timelineRenderer.LayerSkipped += OnSkipped;

        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producerToken = producerCts.Token;

        var channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(FrameAheadDepth)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

        ExceptionDispatchInfo? renderException = null;
        var framesWithContent = 0;

        // Producer: renders frames strictly in order and feeds the bounded channel.
        // The frame source is not thread-safe and depends on monotonic time progression.
        var producerTask = Task.Run(async () =>
        {
            try
            {
                var endFrame = startFrame + frameCount;
                for (var frame = startFrame; frame < endFrame; frame++)
                {
                    producerToken.ThrowIfCancellationRequested();

                    var composed = await _timelineRenderer.RenderAsync(timeline, frame, renderW, renderH, producerToken)
                        .ConfigureAwait(false);
                    if (composed is not null) framesWithContent++;
                    var rendered = composed ?? _compositor.Render([], renderW, renderH);

                    try
                    {
                        await channel.Writer.WriteAsync(rendered, producerToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        rendered.Dispose();
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (producerToken.IsCancellationRequested)
            {
                // Export was cancelled or consumer failed; exit cleanly.
            }
            catch (Exception ex)
            {
                // Capture all render-side exceptions so they never propagate through the channel
                // or get misattributed as ffmpeg encode failures.
                renderException = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        var framesWritten = 0;
        var bytesWritten = 0L;
        try
        {
            try
            {
                while (await channel.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (channel.Reader.TryRead(out var rendered))
                    {
                        using (rendered)
                        {
                            ct.ThrowIfCancellationRequested();
                            await stdin.WriteAsync(rendered.Data, ct).ConfigureAwait(false);
                            framesWritten++;
                            bytesWritten += rendered.Data.LongLength;
                            Progress?.Invoke(framesDoneBefore + framesWritten, totalFrames);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // ffmpeg rejected the command line and exited during header write, so the pipe broke
                // mid-frame. Without this the IOException ("The pipe has been ended.") escapes and the
                // captured stderr — the only place the real reason exists — is thrown away.
                producerCts.Cancel();
                throw await EncodeFailureAsync(
                    "video encode", codec, args, process, stderrTask, framesWritten, bytesWritten, ct)
                    .ConfigureAwait(false);
            }

            // Surface any captured render failure with its original type and stack trace.
            renderException?.Throw();

            ct.ThrowIfCancellationRequested();

            try
            {
                await stdin.FlushAsync(ct).ConfigureAwait(false);
                stdin.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                producerCts.Cancel();
                throw await EncodeFailureAsync(
                    "video encode", codec, args, process, stderrTask, framesWritten, bytesWritten, ct)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            producerCts.Cancel();
            await producerTask.ConfigureAwait(false);

            while (channel.Reader.TryRead(out var leftover))
            {
                leftover.Dispose();
            }

            _timelineRenderer.LayerSkipped -= OnSkipped;
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw FfmpegEncodeException.Create(
                "video encode", codec, args, process.ExitCode, stderr, framesWritten, bytesWritten);
        }

        return framesWithContent;
    }

    /// <summary>
    /// Reaps a broken-pipe ffmpeg and turns it into a diagnosable failure carrying the real stderr.
    /// </summary>
    private static async Task<Exception> EncodeFailureAsync(
        string stage, string encoder, string args, Process process,
        Task<string> stderrTask, int framesWritten, long bytesWritten, CancellationToken ct)
    {
        try
        {
            // The token may already be cancelled; reap on an independent short timeout either way.
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(reap.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* fall through with whatever stderr arrived */ }

        var stderr = "";
        try { stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
        catch { /* stderr unavailable */ }

        var exitCode = 0;
        try { exitCode = process.HasExited ? process.ExitCode : 0; } catch { /* ignore */ }
        _ = ct;
        return FfmpegEncodeException.Create(stage, encoder, args, exitCode, stderr, framesWritten, bytesWritten);
    }

    private static bool IsHardwareEncoder(string codec)
        => codec.Contains("nvenc", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("_qsv", StringComparison.OrdinalIgnoreCase)
            || codec.Contains("_amf", StringComparison.OrdinalIgnoreCase);

    private static bool IsCancellation(Exception ex, CancellationToken ct)
        => ct.IsCancellationRequested || ex is OperationCanceledException;

    private async Task MuxAudioPassAsync(
        string ffmpegPath,
        Timeline timeline,
        IReadOnlyList<AudioClipSegment> segments,
        string videoPath,
        string outputPath,
        int totalFrames,
        bool hdr,
        CancellationToken ct)
    {
        const int sampleRate = 48000;
        const int channels = 2;

        var args = ExportEncodeArguments.BuildAudioMuxArguments(videoPath, outputPath, sampleRate, channels, hdr);

        using var process = StartFfmpegProcess(ffmpegPath, args);
        using var cancellationRegistration = ProcessCancellation.KillOnCancellation(process, ct);
        var stdin = process.StandardInput.BaseStream;
        // Drain stderr/stdout concurrently so the mux does not deadlock on a full stderr pipe.
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);

        // Render audio in 1-second chunks.
        var framesPerChunk = timeline.Fps;
        try
        {
            for (var startFrame = 0; startFrame < totalFrames; startFrame += framesPerChunk)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(framesPerChunk, totalFrames - startFrame);
                var pcm = MixAudioChunk(segments, startFrame, count, timeline.Fps, sampleRate, channels);
                var bytes = new byte[pcm.Length * sizeof(float)];
                Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
                await stdin.WriteAsync(bytes, ct).ConfigureAwait(false);
            }

            await stdin.FlushAsync(ct).ConfigureAwait(false);
            stdin.Close();
        }
        catch (IOException)
        {
            // The mux refuses the header when the video stream and the container disagree (ProRes
            // into mp4 is the reachable case). Surface ffmpeg's reason instead of the broken pipe.
            throw await EncodeFailureAsync(
                "audio mux", "copy", args, process, stderrTask, framesWritten: 0, bytesWritten: 0, ct)
                .ConfigureAwait(false);
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw FfmpegEncodeException.Create("audio mux", "copy", args, process.ExitCode, stderr, framesWritten: 0);
    }

    private async Task<TimelineAudioDecodeResult?> DecodeExportAudioAsync(Timeline timeline, CancellationToken cancellationToken)
    {
        LastAudioDiagnostics = [];
        LastAudioEffects = [];
        var hasAudioLane = timeline.Tracks.Any(track =>
            track.Type == ClipType.audio
            && !track.Muted
            && !track.Hidden
            && track.Clips.Any(clip => clip.DurationFrames > 0));
        var hasSequenceClip = timeline.Tracks.Any(track =>
            !track.Muted
            && !track.Hidden
            && track.Clips.Any(clip =>
                clip.DurationFrames > 0
                && (clip.SourceClipType == ClipType.sequence || clip.MediaType == ClipType.sequence)));
        if (!hasAudioLane && !hasSequenceClip)
            return null;

        var decoded = await _audioDecoder.DecodeAsync(
            timeline,
            ResolveAudioPath,
            TimelineAudioDecoder.DefaultSampleRate,
            TimelineAudioDecoder.DefaultChannelCount,
            cancellationToken,
            _timelineLookup).ConfigureAwait(false);
        LastAudioDiagnostics = decoded.Diagnostics;
        LastAudioEffects = decoded.Effects;
        if (decoded.Segments.Count > 0)
            return decoded;

        // Picture-only nested sequences should still export video-only rather than fail.
        if (!hasAudioLane)
            return null;

        var reason = decoded.Diagnostics.FirstOrDefault()?.Reason ?? "no enabled audio clips were eligible.";
        throw new InvalidOperationException($"Audio export has no usable source audio: {reason}");
    }

    private string? ResolveAudioPath(string mediaRef)
    {
        var resolved = _mediaResolver?.ResolvePath(mediaRef);
        if (!string.IsNullOrWhiteSpace(resolved))
            return resolved;

        // WP-399: the bare-path fallback is for a clip with no manifest entry. An entry that did not
        // resolve (offline, or a file outside the project the user has not allowed) stays silent even if
        // its id happens to be a path, and in a restricted session a bare path needs a grant too.
        if (_mediaResolver is not null
            && (_mediaResolver.Entry(mediaRef) is not null || !_mediaResolver.AllowsLooseFile(mediaRef)))
            return null;
        return File.Exists(mediaRef) ? mediaRef : null;
    }

    /// <summary>Mixes one frame-domain export chunk using the same gain/fade math as preview.</summary>
    public static float[] MixAudioChunk(
        IReadOnlyList<AudioClipSegment> segments,
        int startFrame,
        int frameCount,
        int fps,
        int sampleRate = TimelineAudioDecoder.DefaultSampleRate,
        int channelCount = TimelineAudioDecoder.DefaultChannelCount)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return AudioMixer.Mix(segments, startFrame, frameCount, fps, sampleRate, channelCount);
    }

    private static Process StartFfmpegProcess(string ffmpegPath, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start FFmpeg process.");
    }

}
