using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Audio;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Ui;
using Temno.Media.Audio;
using Temno.Media.Cache;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Colors = Microsoft.UI.Colors;

namespace Temno.App.Controls;

public sealed partial class TimelineControl : UserControl
{
    /// <summary>WP-213: settled waveform state per mediaRef. Active requests live in _waveformInflight.</summary>
    private enum WaveformStatus { Ready, NoAudio, Error, Offline }

    /// <summary>
    /// WP-213: a settled waveform outcome. <see cref="WaveformEntry.Source"/> is the file identity
    /// the outcome was produced from: Ready redraws revalidate it on a bounded cadence, and
    /// NoAudio/Error stand until it changes, so failures neither spin per redraw nor stick forever.
    /// <see cref="WaveformEntry.LastCheckedUtc"/> drives both cadences (Ready revalidation, Offline re-resolve).
    /// </summary>
    private sealed record WaveformEntry(WaveformStatus Status, WaveformEnvelope? Envelope, string Detail, WaveformFileIdentity Source, DateTime LastCheckedUtc)
    {
        public static WaveformEntry ReadyOf(WaveformEnvelope envelope, WaveformFileIdentity identity)
            => new(WaveformStatus.Ready, envelope, "", identity, DateTime.UtcNow);

        public static WaveformEntry FailedOf(WaveformStatus status, WaveformFileIdentity identity, string detail)
            => new(status, null, detail, identity, DateTime.UtcNow);
    }

    private void AddClipWaveform(Clip clip, int fps, double vx, double cy, double vw, double ch)
    {
        // WP-213: every visible column maps its timeline interval to source seconds and
        // aggregates the envelope over that interval. Scroll, zoom, trim and speed all flow
        // through WaveformSampler's canonical clip timing — no whole-file envelope stretch.
        if (vw < 4) return;
        var entry = GetValidWaveformEntry(clip);
        if (entry?.Envelope is not null)
        {
            var rasterScale = XamlRoot?.RasterizationScale ?? 1.0;
            if (!double.IsFinite(rasterScale) || rasterScale <= 0) rasterScale = 1.0;
            var midY = cy + ch / 2;
            var halfH = Math.Max(1, ch / 2 - AppTheme.Spacing.Xs);
            var cols = Math.Clamp((int)Math.Ceiling(vw / AppTheme.Waveform.BarPitch), 2, WaveformColumnsMax);
            var columnPeaks = WaveformSampler.SampleVisibleColumns(
                clip, fps, entry.Envelope, _pixelsPerFrame, vx, vx + vw, cols);
            var geometry = new PathGeometry();
            for (var i = 0; i < cols; i++)
            {
                var rawX = LocalX(vx + (i + 0.5) * (vw / cols));
                var x = (Math.Floor(rawX * rasterScale) + 0.5) / rasterScale;
                var h = columnPeaks[i] * halfH;
                if (h <= 0) continue;
                var bar = new PathFigure
                {
                    StartPoint = new Point(x, midY - h),
                    IsClosed = false,
                    IsFilled = false,
                };
                bar.Segments.Add(new LineSegment { Point = new Point(x, midY + h) });
                geometry.Figures.Add(bar);
            }
            AddLaneChild(new Line
            {
                X1 = LocalX(vx), X2 = LocalX(vx + vw), Y1 = midY, Y2 = midY,
                Stroke = AppTheme.Text.MutedBrush,
                StrokeThickness = AppTheme.BorderWidth.Hairline,
                IsHitTestVisible = false,
            });
            AddLaneChild(new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometry,
                Stroke = AppTheme.Text.SecondaryBrush,
                StrokeThickness = AppTheme.Waveform.StrokeWidth,
                StrokeStartLineCap = PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat,
                UseLayoutRounding = true,
                IsHitTestVisible = false,
            });
            return;
        }

        EnsureClipVisual(clip);
        var settled = _waveformCache.TryGetValue(clip.MediaRef, out var fresh) ? fresh : null;
        if (vw >= WaveformStateMinWidth)
            AddWaveformStateLabel(vx, cy, vw, ch, settled);
    }

    /// <summary>
    /// WP-213: returns the drawable Ready entry, or null. A Ready envelope whose revalidation
    /// cadence has elapsed is re-statted; a changed, relinked or deleted source drops the entry
    /// so this redraw regenerates or shows the honest offline state instead of stale peaks.
    /// </summary>
    private WaveformEntry? GetValidWaveformEntry(Clip clip)
    {
        if (!_waveformCache.TryGetValue(clip.MediaRef, out var entry) || entry.Envelope is null)
            return null;
        if (DateTime.UtcNow - entry.LastCheckedUtc < WaveformRevalidateInterval)
            return entry;
        var path = ResolveWaveformPath(clip.MediaRef);
        if (path is not null && entry.Source.Matches(path))
        {
            var refreshed = entry with { LastCheckedUtc = DateTime.UtcNow };
            _waveformCache[clip.MediaRef] = refreshed;
            return refreshed;
        }
        _waveformCache.Remove(clip.MediaRef);
        return null;
    }

    /// <summary>WP-213: resolves an existing source file for a mediaRef, or null when offline.</summary>
    private string? ResolveWaveformPath(string mediaRef)
    {
        if (_session is null || string.IsNullOrEmpty(mediaRef)) return null;
        var manifestEntry = _session.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
        var path = manifestEntry is null ? null : _session.Resolver.ResolvePath(manifestEntry);
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>WP-213: honest loading / offline / no-audio / error text; token type, colour and spacing only.</summary>
    private void AddWaveformStateLabel(double vx, double cy, double vw, double ch, WaveformEntry? entry)
    {
        var (text, brush) = entry?.Status switch
        {
            WaveformStatus.NoAudio => ("No audio", AppTheme.Text.MutedBrush),
            WaveformStatus.Error => ("Waveform unavailable", AppTheme.Text.TertiaryBrush),
            WaveformStatus.Offline => ("Media offline", AppTheme.Text.MutedBrush),
            _ => ("Loading waveform\u2026", AppTheme.Text.TertiaryBrush),
        };
        var label = new TextBlock
        {
            Text = text,
            FontSize = AppTheme.FontSize.Xs,
            Foreground = brush,
            IsHitTestVisible = false,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = Math.Max(2, vw - AppTheme.Spacing.MdLg),
        };
        var tip = entry?.Status is WaveformStatus.Error or WaveformStatus.Offline
            && !string.IsNullOrEmpty(entry.Detail)
            ? entry.Detail
            : text;
        ToolTipService.SetToolTip(label, tip);
        Canvas.SetLeft(label, LocalX(vx) + AppTheme.Spacing.Sm);
        Canvas.SetTop(label, cy + ch / 2 - AppTheme.Spacing.SmMd);
        AddLaneChild(label);
    }

    private void EnsureClipVisual(Clip clip)
    {
        if (_session is null || string.IsNullOrEmpty(clip.MediaRef)) return;
        // Video and image visuals are the filmstrip's job (WP-159); it asks per cell.
        if (clip.MediaType != ClipType.audio) return;

        if (_waveformCache.TryGetValue(clip.MediaRef, out var settled))
        {
            if (settled.Status == WaveformStatus.Ready) return;
            if (settled.Status == WaveformStatus.Offline)
            {
                // Bounded offline polling: recover when the file returns, no redraw storm.
                if (DateTime.UtcNow - settled.LastCheckedUtc < WaveformRevalidateInterval) return;
                _waveformCache.Remove(clip.MediaRef);
            }
            else
            {
                // A failed entry stands until the source file's identity changes (or the
                // file disappears, which becomes Offline-tracked below): the recovery path
                // for errors without a retry-per-redraw ffmpeg storm.
                if (!WaveformSourceChanged(clip.MediaRef)) return;
                _waveformCache.Remove(clip.MediaRef);
            }
        }
        if (_waveformInflight.ContainsKey(clip.MediaRef)) return; // already requested

        var path = ResolveWaveformPath(clip.MediaRef);
        if (path is null)
        {
            _waveformCache[clip.MediaRef] = WaveformEntry.FailedOf(
                WaveformStatus.Offline, WaveformFileIdentity.Missing, "Source media is offline.");
            return;
        }

        GenerateWaveformAsync(clip.MediaRef, path, _waveformGeneration);
    }

    /// <summary>WP-213: true when the source behind a settled entry changed or disappeared.</summary>
    private bool WaveformSourceChanged(string mediaRef)
    {
        if (!_waveformCache.TryGetValue(mediaRef, out var settled)) return true;
        var path = ResolveWaveformPath(mediaRef);
        if (path is null) return true;
        return !settled.Source.Matches(path);
    }

    private void CancelWaveformRequests()
    {
        foreach (var cts in _waveformInflight.Values)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
            }
            cts.Dispose();
        }
        _waveformInflight.Clear();
    }

    private static bool IsNoAudioFailure(InvalidOperationException ex)
        => ex.Message.Contains("does not contain an audio stream", StringComparison.Ordinal);

    private async void GenerateWaveformAsync(string mediaRef, string path, long generation)
    {
        // Single-flight per mediaRef: a redraw storm must not spawn duplicate decoders.
        if (_waveformInflight.ContainsKey(mediaRef)) return;
        var startIdentity = WaveformFileIdentity.Capture(path);
        var cts = new CancellationTokenSource();
        _waveformInflight[mediaRef] = cts;
        try
        {
            var cache = new WaveformDiskCache();
            WaveformEnvelope? envelope;
            if (cache.TryLoad(path, WaveformEnvelope.DefaultPeaksPerSecond, out var cached)
                && cached is not null
                && startIdentity.Matches(path))
            {
                envelope = cached;
            }
            else
            {
                envelope = await new WaveformExtractor()
                    .ExtractEnvelopeAsync(path, WaveformEnvelope.DefaultPeaksPerSecond, cts.Token)
                    .ConfigureAwait(true);
                if (!startIdentity.Matches(path))
                {
                    // The file changed mid-decode: this envelope mixes two sources. Never
                    // cache it; stamp the start identity so the next redraw retries against
                    // the new file instead of sticking or spinning.
                    if (generation != _waveformGeneration) return;
                    _waveformCache[mediaRef] = WaveformEntry.FailedOf(
                        WaveformStatus.Error, startIdentity, "Source changed during decode.");
                    RequestVisualRedraw();
                    return;
                }
                cache.Save(path, envelope);
            }
            // A completion after Bind/unload switched generations must never overwrite the new cache/UI.
            if (generation != _waveformGeneration) return;
            _waveformCache[mediaRef] = WaveformEntry.ReadyOf(envelope, startIdentity);
            RequestVisualRedraw();
        }
        catch (OperationCanceledException)
        {
            // Bind/unload cancelled us: leave no settled state behind.
        }
        catch (InvalidOperationException ex) when (IsNoAudioFailure(ex))
        {
            if (generation != _waveformGeneration) return;
            _waveformCache[mediaRef] = WaveformEntry.FailedOf(WaveformStatus.NoAudio, startIdentity, ex.Message);
            RequestVisualRedraw();
        }
        catch (Exception ex)
        {
            if (generation != _waveformGeneration) return;
            _waveformCache[mediaRef] = WaveformEntry.FailedOf(WaveformStatus.Error, startIdentity, ex.Message);
            RequestVisualRedraw();
        }
        finally
        {
            // Remove only our own request: Bind may already have started this mediaRef's
            // next-generation request, which must survive our completion. Only our own CTS
            // is disposed here, never the new generation's.
            if (_waveformInflight.TryGetValue(mediaRef, out var current) && ReferenceEquals(current, cts))
                _waveformInflight.Remove(mediaRef);
            cts.Dispose();
        }
    }

    private void CancelStripRequests()
    {
        _stripGeneration++;
        var previous = _stripCts;
        _stripCts = new CancellationTokenSource();
        try { previous.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async void GenerateStripCellAsync(string mediaRef, string path, double seconds)
    {
        var generation = _stripGeneration;
        var token = _stripCts.Token;
        var key = (mediaRef, seconds);
        // WP-161: count filmstrip extractions inside the playback trace window (see DrawLanes).
        PreviewHostControl.PlaybackTrace("strip", (int)seconds);
        // WP-320: GetOrCreateAsync starts ffmpeg before its first await. Hop off the UI thread
        // first, or that start blocks the frame that asked for the cell. Temno.Media stays as it is.
        Interlocked.Increment(ref _stripInflight);
        var canceled = false;
        try
        {
            var pixels = await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                string thumbPath;
                await _stripGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    token.ThrowIfCancellationRequested();
                    var spT0 = System.Diagnostics.Stopwatch.GetTimestamp(); // WP-320 trim probe (TEMNO_TIMELINE_TRACE)
                    var thumbTask = _thumbnailCache.GetOrCreateAsync(path, seconds, 80, token);
                    TimelineTrace.Mark("sp", System.Diagnostics.Stopwatch.GetTimestamp() - spT0, Environment.CurrentManagedThreadId);
                    thumbPath = await thumbTask.ConfigureAwait(false);
                }
                finally
                {
                    _stripGate.Release();
                }
                token.ThrowIfCancellationRequested();
                return await DecodeStripCellAsync(thumbPath).ConfigureAwait(false);
            }, token).ConfigureAwait(true);

            if (generation != _stripGeneration || token.IsCancellationRequested)
            {
                canceled = true;
                return;
            }

            TimelineTrace.Mark("sd", (long)Math.Round(seconds * 1000));
            if (pixels is not null)
                _stripCache[key] = ToStripBitmap(pixels);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        catch
        {
            // Undecodable media or a source that went offline keeps the flat colour. The request is
            // deliberately not retried here: a failing extraction retried on every redraw would spawn
            // ffmpeg per frame of scroll.
        }
        finally
        {
            Interlocked.Decrement(ref _stripInflight);
            if (!canceled && generation == _stripGeneration)
                RequestVisualRedraw();
        }
    }

    private sealed record StripPixels(byte[] Bgra, int Width, int Height);

    /// <summary>
    /// WP-320. Decodes a strip cell off the UI thread. A <see cref="BitmapImage"/> decodes lazily on
    /// the render thread, so a cell drawn before its decode was ready showed the empty lane for a
    /// frame: at 200% a trim blanked the strip for single frames, and so did exact cells landing
    /// after release. Pixels already in memory draw on the frame the cell is added.
    /// </summary>
    /// <remarks>
    /// The full thumbnail is kept, and the GPU scales it to the cell. Decoding to the cell's height
    /// matched BitmapImage's 100% rendering no better than 1-pixel edges on hard colour bars, and a
    /// track made taller later would have drawn those cells upscaled.
    /// </remarks>
    private static async Task<StripPixels?> DecodeStripCellAsync(string thumbPath)
    {
        if (!File.Exists(thumbPath)) return null;
        var file = await StorageFile.GetFileFromPathAsync(thumbPath);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.ColorManageToSRgb);
        return new StripPixels(data.DetachPixelData(), (int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
    }

    private static WriteableBitmap ToStripBitmap(StripPixels pixels)
    {
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(pixels.Bgra, 0, Math.Min(pixels.Bgra.Length, (int)bitmap.PixelBuffer.Capacity));
        bitmap.Invalidate();
        return bitmap;
    }

    private void RequestViewportRedraw()
    {
        if (_rebuilding || _session is null) return;
        if (_viewportTimer is null)
        {
            _viewportTimer = DispatcherQueue.CreateTimer();
            _viewportTimer.Interval = TimeSpan.FromMilliseconds(16);
            _viewportTimer.IsRepeating = false;
            _viewportTimer.Tick += ViewportTimer_Tick;
        }
        // Throttle rather than debounce: calling Start() on a running timer resets the 16 ms
        // window and starves ruler/lane redraws throughout continuous auto-scroll or scrollbar panning.
        if (!_viewportTimer.IsRunning)
            _viewportTimer.Start();
    }

    private void ViewportTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        if (_session?.ActiveTimeline is not { } timeline) return;
        var headerHeightChanged = SyncViewportSurfaces(Math.Max(120, BodyContent.Height));
        TimelineTrace.Mark("vp"); // WP-320 trim probe (TEMNO_TIMELINE_TRACE)
        DrawLanes(timeline);
        UpdatePlayheadVisual();
        if (headerHeightChanged)
            ScheduleHeaderVerticalSync();
    }

    private void RequestVisualRedraw()
    {
        // Coalesce several completing generations into one redraw.
        _redrawTimer ??= DispatcherQueue.CreateTimer();
        _redrawTimer.Interval = TimeSpan.FromMilliseconds(120);
        _redrawTimer.IsRepeating = false;
        _redrawTimer.Tick -= RedrawTimer_Tick;
        _redrawTimer.Tick += RedrawTimer_Tick;
        _redrawTimer.Start();
    }

    private void RedrawTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        TimelineTrace.Mark("rt"); // WP-320 trim probe (TEMNO_TIMELINE_TRACE)
        if (IsLiveTrim)
        {
            RedrawTrimTracks();
            return;
        }
        Rebuild();
    }
}
