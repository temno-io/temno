using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Temno.App.Controls;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.App.ViewModels;
using Temno.App.Views;
using Temno.Core.Captions;
using Temno.Core.Editing;
using Temno.Core.Export;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Project;
using Temno.Core.Updates;
using Temno.Core.Session;
using Temno.Core.Transcription;
using Temno.Core.Ui;
using Temno.Mcp;
using Temno.Media;
using Temno.Media.Audio;
using Temno.Media.Cache;
using Temno.Media.Compositing;
using Temno.Media.Export;
using Temno.Media.Hardware;
using Temno.Media.Search;
using Temno.Media.Transcription;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Temno.App.Views.Editor;

public sealed partial class EditorPage : Page
{
    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        var path = AppSession.Current.Session?.PackagePath;
        if (!string.IsNullOrEmpty(path))
            AppSession.RevealInExplorer(path);
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmLeaveProjectAsync()) return;
        await Preview.EnsureProjectThumbnailAsync();
        AppSession.Current.Close(save: false);
        Frame?.Navigate(typeof(HomePage));
    }

    private static void ApplyProjectWindowSize()
    {
        (App.MainAppWindow as MainWindow)?.ApplyWindowSizing(editor: true);
    }

    // --- WP-74 export queue chrome ---

    private void OnExportJobChanged(ExportJob job)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshExportQueueChrome();
            if (job.State.IsFinished())
            {
                StatusText.Text = job.State switch
                {
                    // WP-154: LastVideoDiagnostics was populated on every export and read by nobody.
                    ExportJobState.Completed => "Exported"
                        + (string.IsNullOrEmpty(job.EncoderName) ? "" : $" ({job.EncoderName})")
                        + (string.IsNullOrEmpty(job.EncoderNote) ? "" : $" — {job.EncoderNote}")
                        + " → " + job.OutputPath
                        + DroppedPictureSuffix(job.Id),
                    ExportJobState.Failed => "Export failed: " + (job.Error ?? "unknown"),
                    ExportJobState.Cancelled => "Export canceled",
                    _ => StatusText.Text,
                };
                _exportersByJob.Remove(job.Id);
            }
            else if (job.State == ExportJobState.Rendering)
            {
                var pct = (int)Math.Round(job.Progress * 100);
                StatusText.Text = $"Exporting… {pct}% ({job.CurrentFrame}/{job.TotalFrames})";
            }
        });
    }
    private string DroppedPictureSuffix(string jobId)
    {
        if (!_exportersByJob.TryGetValue(jobId, out var exporter)) return "";
        var dropped = exporter.LastVideoDiagnostics;
        if (dropped.Count == 0) return "";
        var names = string.Join(", ", dropped.Take(3).Select(d => $"“{MediaName(d.MediaRef)}”"));
        var more = dropped.Count > 3 ? $" and {dropped.Count - 3} more" : "";
        var noun = dropped.Count == 1 ? "clip is" : "clips are";
        return $"  —  {dropped.Count} {noun} missing from the picture: {names}{more}.";
    }

    private string MediaName(string mediaRef)
    {
        var entry = AppSession.Current.Session?.Manifest.Entries.FirstOrDefault(e => e.Id == mediaRef);
        return string.IsNullOrWhiteSpace(entry?.Name) ? mediaRef : entry!.Name;
    }

    /// <summary>
    /// Runs the export preflight and, if anything the timeline uses will not appear, asks. Returns
    /// the report and whether the user accepted a partial picture; a null report means cancel.
    /// </summary>
    private async Task<(bool AllowPartial, ExportMediaPreflightReport? Report)> ConfirmOfflineMediaAsync(
        ProjectSession session, Timeline timeline)
    {
        while (true)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Two halves on purpose. Collecting reads the session's collections, so it runs under the
            // same gate every other reader uses -- an MCP import_media landing mid-enumeration would
            // otherwise throw "collection was modified" and surface as a spurious export failure.
            // Classifying touches the disk, so it runs off the UI thread: File.Exists on a
            // disconnected share blocks for seconds, inside the call, where no busy overlay can appear.
            var plan = ProjectHub.RunSerialized(() => ExportMediaPreflight.Collect(
                timeline,
                session.Manifest,
                SessionTimelineLookup.For(session),
                session.PackagePath));
            var projectIsSaved = !string.IsNullOrEmpty(session.PackagePath);
            var report = await Task.Run(
                () => ExportMediaPreflight.Classify(plan, session.Resolver, projectIsSaved));
            sw.Stop();
            TraceExportPreflight(sw.Elapsed.TotalMilliseconds, report);

            if (!report.HasProblems)
                return (false, report);

            var body = new StackPanel { Spacing = AppTheme.Spacing.SmMd, MinWidth = 380 };
            body.Children.Add(new TextBlock
            {
                Text = report.Sentence,
                TextWrapping = TextWrapping.Wrap,
            });
            body.Children.Add(new TextBlock
            {
                Text = "Exporting now leaves those parts of the picture blank.",
                FontSize = 12,
                Foreground = AppTheme.Text.SecondaryBrush,
                TextWrapping = TextWrapping.Wrap,
            });

            var relinkable = report.Problems.FirstOrDefault(
                p => session.Manifest.Entries.Any(e => e.Id == p.Reference));
            // WP-399: a file outside the project that is only waiting for the user's yes is allowed, not relinked.
            var allowable = session.ExternalMedia.Blocked(session.Manifest)
                .Where(b => b.CanAllow).Select(b => b.MediaRef).ToHashSet(StringComparer.Ordinal);
            var needsAllow = report.Problems.Any(p => p.State == ExportMediaState.NotAllowed && allowable.Contains(p.Reference));
            var dialog = new ContentDialog
            {
                Title = "Some media will not appear",
                Content = body,
                PrimaryButtonText = needsAllow ? "Review…" : relinkable is null ? "" : "Relink…",
                SecondaryButtonText = "Export anyway",
                CloseButtonText = "Cancel",
                DefaultButton = relinkable is null && !needsAllow ? ContentDialogButton.Close : ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };

            var choice = await dialog.ShowAsync();
            if (choice == ContentDialogResult.Secondary)
                return (true, report);
            if (choice == ContentDialogResult.Primary && needsAllow)
            {
                await ReviewExternalMediaAsync();
                continue;
            }
            if (choice != ContentDialogResult.Primary || relinkable is null)
                return (false, null);

            // The point of the route: relink from here, not by hunting the right media row.
            await RelinkMediaAsync(relinkable.Reference, relinkable.Name);
        }
    }

    /// <summary>Shared by the media row's context menu and the export warning.</summary>
    private async Task<bool> RelinkMediaAsync(string mediaRef, string displayName)
    {
        var session = AppSession.Current.Session;
        if (session is null) return false;

        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
        };
        foreach (var ext in MediaImportService.SupportedExtensions) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindowHandle());
        var file = await picker.PickSingleFileAsync();
        if (file is null) return false;

        var result = EditCommands.RelinkMediaEntry(session, mediaRef, file.Path);
        if (!result.Ok)
        {
            StatusText.Text = result.Error ?? "Relink failed";
            return false;
        }
        RefreshAfterEdit();
        StatusText.Text = $"Relinked · {displayName} → {file.Name}";
        return true;
    }

    /// <summary>
    /// Off unless TEMNO_EXPORT_PREFLIGHT_TRACE names a file. WP-154's acceptance asks for the
    /// preflight's cost on a fully-online project, and a cost nobody can read from outside the
    /// process is an unfalsifiable claim. Same seam as TEMNO_DEV_GALLERY.
    /// </summary>
    private static readonly string? ExportPreflightTracePath =
        EnvCompat.Get("TEMNO_EXPORT_PREFLIGHT_TRACE");

    private static void TraceExportPreflight(double milliseconds, ExportMediaPreflightReport report)
    {
        if (string.IsNullOrEmpty(ExportPreflightTracePath)) return;
        try
        {
            File.AppendAllText(
                ExportPreflightTracePath,
                $"{DateTime.UtcNow:HH:mm:ss.fff} preflight {milliseconds:F3} ms, "
                + $"{report.Items.Count} refs, {report.Problems.Count} problems, "
                + $"{report.NetworkUncheckedCount} network-unchecked{Environment.NewLine}");
        }
        catch { /* a diagnostic must never break the editor */ }
    }

    private void RefreshExportQueueChrome()
    {
        var jobs = ToolExecutor.ExportQueue.Jobs;
        var pending = jobs.Where(j => j.State.IsPending()).ToList();
        var last = jobs.FirstOrDefault();

        if (pending.Count > 0)
        {
            var active = pending.FirstOrDefault(j => j.State == ExportJobState.Rendering) ?? pending[0];
            var pct = (int)Math.Round(active.Progress * 100);
            ExportQueueLabel.Text = active.State == ExportJobState.Rendering
                ? $"Export {pct}%"
                : $"Queued {pending.Count}";
            ExportQueueDot.Background = active.State == ExportJobState.Rendering
                ? AppTheme.Status.WarningBrush
                : AppTheme.Accent.TimecodeBrush;
            ToolTipService.SetToolTip(ExportQueueButton,
                $"{pending.Count} active · {active.State.ReportName()} · {System.IO.Path.GetFileName(active.OutputPath)}");
        }
        else if (last is { State: ExportJobState.Failed })
        {
            ExportQueueLabel.Text = "Failed";
            ExportQueueDot.Background = AppTheme.Status.ErrorBrush;
            ToolTipService.SetToolTip(ExportQueueButton, last.Error ?? "Export failed");
        }
        else if (last is { State: ExportJobState.Completed })
        {
            ExportQueueLabel.Text = "Done";
            ExportQueueDot.Background = AppTheme.Status.SuccessBrush;
            var encoder = string.IsNullOrEmpty(last.EncoderName) ? "" : $" · {last.EncoderName}";
            var note = string.IsNullOrEmpty(last.EncoderNote) ? "" : " — " + last.EncoderNote;
            ToolTipService.SetToolTip(ExportQueueButton, "Last export completed" + encoder + note + " · " + last.OutputPath);
        }
        else if (last is { State: ExportJobState.Cancelled })
        {
            ExportQueueLabel.Text = "Canceled";
            ExportQueueDot.Background = AppTheme.Text.MutedBrush;
            ToolTipService.SetToolTip(ExportQueueButton, "Last export canceled · " + last.OutputPath);
        }
        else
        {
            ExportQueueLabel.Text = "Export";
            ExportQueueDot.Background = AppTheme.Text.MutedBrush;
            ToolTipService.SetToolTip(ExportQueueButton, "Export queue (idle)");
        }
    }

    private async void ExportQueueButton_Click(object sender, RoutedEventArgs e)
        => await ShowExportDialogAsync();

    // --- WP-75 timeline tabs / zoom / text / meter ---

    private void RefreshLibraryTimelines()
    {
        var session = AppSession.Current.Session;
        LibraryTimelinesList.ItemsSource = session?.ProjectFile.Timelines.Select(t =>
            new TimelineTabItem(t.Id, t.Name, t.Id == session.ProjectFile.ActiveTimelineId,
                details: $"{t.Width} × {t.Height} · {t.Fps} fps · {t.Tracks.Count} tracks")).ToList();
    }

    private void TimelineTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        var session = AppSession.Current.Session;
        if (session is null) return;
        if (string.Equals(session.ProjectFile.ActiveTimelineId, id, StringComparison.Ordinal)) return;
        if (!TimelineCommands.SetActiveTimeline(session, id).Ok) return;
        BindSession();
        StatusText.Text = "Timeline · " + session.ActiveTimeline.Name;
    }

    private async void TimelineTab_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        await RenameTimelineUiAsync(id);
    }

    private void TimelineTab_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id } fe) return;
        e.Handled = true;
        var session = AppSession.Current.Session;
        if (session is null) return;
        TimelineCommands.EnsureOpenTimelineIds(session);
        var openCount = session.ProjectFile.OpenTimelineIds?.Count ?? 1;
        var projectCount = session.ProjectFile.Timelines.Count;

        var flyout = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "Rename" };
        rename.Click += async (_, _) => await RenameTimelineUiAsync(id);
        var dup = new MenuFlyoutItem { Text = "Duplicate" };
        dup.Click += (_, _) => DuplicateTimelineUi(id);
        var close = new MenuFlyoutItem { Text = "Close Tab", IsEnabled = openCount > 1 };
        close.Click += (_, _) => CloseTimelineTabUi(id);
        var closeOthers = new MenuFlyoutItem { Text = "Close Other Tabs", IsEnabled = openCount > 1 };
        closeOthers.Click += (_, _) => CloseOtherTabsUi(id);
        var del = new MenuFlyoutItem { Text = "Delete Timeline", IsEnabled = projectCount > 1 };
        del.Click += async (_, _) => await DeleteTimelineUiAsync(id);
        flyout.Items.Add(rename);
        flyout.Items.Add(dup);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(close);
        flyout.Items.Add(closeOthers);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(del);
        WindowPopout.ShowContextMenuAt(flyout, fe, e.GetPosition(fe));
    }

    private void TimelineTabClose_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        CloseTimelineTabUi(id);
    }

    private void CloseTimelineTabUi(string id)
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        var result = TimelineCommands.CloseTimelineTab(session, id);
        if (!result.Ok)
        {
            StatusText.Text = result.Error ?? "Close tab failed";
            return;
        }
        BindSession();
        StatusText.Text = "Closed timeline tab";
    }

    private void CloseOtherTabsUi(string keepId)
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        var result = TimelineCommands.CloseOtherTimelineTabs(session, keepId);
        if (!result.Ok)
        {
            StatusText.Text = result.Error ?? "Close others failed";
            return;
        }
        BindSession();
        StatusText.Text = "Closed other tabs";
    }

    private void DuplicateTimelineUi(string id)
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        var result = TimelineCommands.DuplicateTimeline(session, id, out _);
        if (!result.Ok)
        {
            StatusText.Text = result.Error ?? "Duplicate failed";
            return;
        }
        BindSession();
        StatusText.Text = "Duplicated · " + session.ActiveTimeline.Name;
    }

    private async Task RenameTimelineUiAsync(string id)
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        var tl = session.ProjectFile.Timelines.FirstOrDefault(t => t.Id == id);
        if (tl is null) return;

        var box = new TextBox { Text = tl.Name, MaxLength = 120 };
        var dialog = new ContentDialog
        {
            Title = "Rename timeline",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var result = TimelineCommands.RenameTimeline(session, id, box.Text ?? "");
        if (!result.Ok)
        {
            StatusText.Text = result.Error ?? "Rename failed";
            return;
        }
        RefreshAfterEdit();
    }

    private async Task DeleteTimelineUiAsync(string id)
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        var tl = session.ProjectFile.Timelines.FirstOrDefault(t => t.Id == id);
        if (tl is null) return;

        if (session.ProjectFile.Timelines.Count <= 1)
        {
            StatusText.Text = "Cannot delete the only timeline";
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "Delete timeline?",
            Content = $"Delete \"{tl.Name}\"? This cannot be undone except via Undo (Ctrl+Z).",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var result = TimelineCommands.DeleteTimeline(session, id);
        if (!result.Ok)
        {
            StatusText.Text = result.Error ?? "Delete failed";
            return;
        }
        BindSession();
        StatusText.Text = "Deleted timeline · " + tl.Name;
    }

    private void AddTimeline_Click(object sender, RoutedEventArgs e)
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        var result = TimelineCommands.AddTimeline(session, null, null, out _);
        if (!result.Ok)
        {
            StatusText.Text = result.Error ?? "Add timeline failed";
            return;
        }
        BindSession();
        StatusText.Text = "Added timeline · " + session.ActiveTimeline.Name;
    }

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        var fps = Math.Max(1, session.ActiveTimeline.Fps);
        var start = Timeline.PlayheadFrame;
        var end = start + fps * 3; // 3s default title
        var style = new TextStyle();
        new TextStylePatch { FontSize = 64, IsBold = true }.ApplyTo(style);
        var result = TextClipCommands.AddTexts(session,
        [
            new TextClipSpec(null, start, end, "Title", style, null, null),
        ], out var ids);
        if (!result.Ok)
        {
            StatusText.Text = result.Error ?? "Add Text failed";
            return;
        }
        RefreshAfterEdit();
        // WP-184: what you just created is what you are editing next — select it and show it.
        if (ids.Count > 0)
        {
            Timeline.SetSelectedClip(ids[0]);
            Inspector.ShowSelection(Timeline.SelectedClipIds, ids[0]);
        }
        StatusText.Text = "Text clip added — edit it in the Inspector";
    }

    /// <summary>
    /// WP-181: the status bar names the project and its saved state. The location is the tooltip
    /// here, the selectable Path row in the Inspector's Project section, and File → Reveal.
    /// </summary>
    private void RefreshStatusPath()
    {
        if (PathText is null) return;
        PathText.Text = string.IsNullOrEmpty(_vm.ProjectName)
            ? ""
            : $"{_vm.ProjectName} · {(_vm.IsDirty ? "Unsaved changes" : "Saved")}";
        ToolTipService.SetToolTip(PathText, string.IsNullOrEmpty(_vm.ProjectPath) ? null : _vm.ProjectPath);
    }

    /// <summary>
    /// WP-181: the zoom readout says how much time fits in the lanes, not pixels per frame.
    /// </summary>
    private void RefreshZoomReadout(double pixelsPerFrame)
    {
        if (TimelineZoomValue is null) return;
        var fps = Math.Max(1, AppSession.Current.Session?.ActiveTimeline.Fps ?? 30);
        var lanes = Timeline.ActualWidth - TimelineGeometry.DefaultHeaderWidth;
        if (lanes <= 0 || pixelsPerFrame <= 0 || !double.IsFinite(pixelsPerFrame))
        {
            TimelineZoomValue.Text = "";
            return;
        }
        var seconds = lanes / pixelsPerFrame / fps;
        TimelineZoomValue.Text = seconds < 60
            ? $"{seconds:0.#} s in view"
            : $"{(int)(seconds / 60)}:{(int)(seconds % 60):00} in view";
    }

    private void TimelineZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingZoomSlider) return;
        Timeline.PixelsPerFrame = e.NewValue;
        RefreshZoomReadout(e.NewValue);
    }

    private void Timeline_ZoomChanged(object? sender, double ppf)
    {
        SyncZoomSliderFromTimeline();
    }

    private void SyncZoomSliderFromTimeline()
    {
        _syncingZoomSlider = true;
        try
        {
            var ppf = Timeline.PixelsPerFrame;
            TimelineZoomSlider.Minimum = TimelineGeometry.MinPixelsPerFrame;
            TimelineZoomSlider.Maximum = TimelineGeometry.MaxPixelsPerFrame;
            TimelineZoomSlider.Value = Math.Clamp(ppf, TimelineZoomSlider.Minimum, TimelineZoomSlider.Maximum);
            RefreshZoomReadout(ppf);
        }
        finally
        {
            _syncingZoomSlider = false;
        }
    }

    private Task RefreshAudioMeterAsync(bool force = false)
    {
        // Read the already mixed playback buffer so every nested lane, gain and effect agrees
        // with the device output, without asynchronous source probes racing the transport.
        var (left, right) = AppSession.Current.Session is not null && (Preview.IsPlaying || force)
            ? Preview.GetTimelineAudioLevels(Timeline.PlayheadFrame)
            : (0f, 0f);
        UpdateMeterVisual(left, right);
        return Task.CompletedTask;
    }

    private void UpdateMeterVisual(float left, float right)
    {
        // Full-height meter column (Mac AudioMeterView); fall back if not laid out yet.
        var maxH = Math.Max(24, (MeterLeft.Parent as FrameworkElement)?.ActualHeight ?? 80) - 4;
        MeterLeft.Height = Math.Clamp(left, 0, 1) * maxH;
        MeterRight.Height = Math.Clamp(right, 0, 1) * maxH;
        MeterLeft.Fill = left > 0.9 ? AppTheme.Status.ErrorBrush
            : left > 0.7 ? AppTheme.Status.WarningBrush
            : AppTheme.Status.SuccessBrush;
        MeterRight.Fill = right > 0.9 ? AppTheme.Status.ErrorBrush
            : right > 0.7 ? AppTheme.Status.WarningBrush
            : AppTheme.Status.SuccessBrush;
    }
}
