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
    private async void ImportMedia_Click(object sender, RoutedEventArgs e)
    {
        if (AppSession.Current.Session is null) return;
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
            };
            foreach (var ext in MediaImportService.SupportedExtensions) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindowHandle());

            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;
            await ImportPathsAsync(files.Select(f => f.Path).ToList());
        }
        catch (Exception ex)
        {
            StatusText.Text = "Import failed: " + ex.Message;
            ShowMediaError("Import failed", ex.Message + " Check the file path and try again.");
        }
    }

    private void MediaPanel_DragOver(object sender, DragEventArgs e)
    {
        if (AppSession.Current.Session is not null && e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            if (e.DragUIOverride is not null)
            {
                e.DragUIOverride.Caption = "Import media";
                e.DragUIOverride.IsCaptionVisible = true;
                e.DragUIOverride.IsGlyphVisible = true;
            }
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
        }
    }

    private async void MediaPanel_Drop(object sender, DragEventArgs e)
    {
        if (AppSession.Current.Session is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<StorageFile>().Select(f => f.Path)
                .Where(MediaImportService.IsSupported).ToList();
            if (paths.Count > 0) await ImportPathsAsync(paths);
            else StatusText.Text = "No supported media files were dropped.";
            if (paths.Count == 0)
                ShowMediaError("Nothing imported", "No supported media files were dropped. Use video, image, or audio.");
        }
        catch (Exception ex)
        {
            StatusText.Text = "Import failed: " + ex.Message;
            ShowMediaError("Import failed", ex.Message + " Check the file path and try again.");
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// WP-160: the same picker as Import, but the manifest records the file where it already lives.
    /// Kept as its own button rather than a checkbox on the picker, because it changes whether the
    /// project is self-contained and that is not a setting to leave sticky.
    /// </summary>
    private async void LinkMedia_Click(object sender, RoutedEventArgs e)
    {
        if (AppSession.Current.Session is null) return;
        try
        {
            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.VideosLibrary,
            };
            foreach (var ext in MediaImportService.SupportedExtensions) picker.FileTypeFilter.Add(ext);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindowHandle());

            var files = await picker.PickMultipleFilesAsync();
            if (files is null || files.Count == 0) return;
            await ImportPathsAsync(files.Select(f => f.Path).ToList(), referenceInPlace: true);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Link failed: " + ex.Message;
            ShowMediaError("Link failed", ex.Message + " Check the file path and try again.");
        }
    }

    private async Task ImportPathsAsync(IReadOnlyList<string> paths, bool referenceInPlace = false)
    {
        var session = AppSession.Current.Session;
        if (session is null || paths.Count == 0) return;

        _importCts?.Cancel();
        _importCts?.Dispose();
        _importCts = new CancellationTokenSource();
        var ct = _importCts.Token;

        var verb = referenceInPlace ? "Linking" : "Importing";
        StatusText.Text = $"{verb} {paths.Count} file(s)…";
        BeginMediaBusy(verb + "…", () => _importCts.Cancel());
        MediaImportService.ImportOutcome outcome;
        try
        {
            outcome = await _importer.ImportAsync(session, paths, ct, referenceInPlace);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Import cancelled.";
            return;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Import failed: " + ex.Message;
            ShowMediaError("Import failed", ex.Message + " Check the file and try again.");
            return;
        }
        finally
        {
            EndMediaBusy();
        }

        RefreshAfterEdit();
        var cancelled = ct.IsCancellationRequested;
        var msg = outcome.Imported > 0 ? $"Imported {outcome.Imported} file(s)." : "Nothing imported.";
        if (cancelled) msg += " Remaining files were skipped.";
        if (outcome.Errors.Count > 0)
        {
            msg += $"  {outcome.Errors.Count} skipped — {outcome.Errors[0]}";
            ShowMediaError(
                "Some files were skipped",
                outcome.Errors[0] + " Import the files again after fixing the path or type.");
        }
        StatusText.Text = msg;
    }

    private void ShowMediaError(string title, string message)
    {
        if (MediaErrorBar is null) return;
        MediaErrorBar.Title = title;
        MediaErrorBar.Message = message;
        MediaErrorBar.IsOpen = true;
    }

    private void BeginMediaBusy(string label, Action? cancel)
    {
        _mediaBusyCancel = cancel;
        if (MediaBusyLabel is not null)
            MediaBusyLabel.Text = label;
        if (MediaBusyStopBtn is not null)
            MediaBusyStopBtn.Visibility = cancel is null ? Visibility.Collapsed : Visibility.Visible;
        _mediaBusyDelay ??= DispatcherQueue.CreateTimer();
        _mediaBusyDelay.Interval = TimeSpan.FromMilliseconds(PanelBusyDelayMs);
        _mediaBusyDelay.IsRepeating = false;
        _mediaBusyDelay.Tick -= OnMediaBusyDelayTick;
        _mediaBusyDelay.Tick += OnMediaBusyDelayTick;
        _mediaBusyDelay.Start();
    }

    private void OnMediaBusyDelayTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (MediaBusyOverlay is null) return;
        MediaBusyOverlay.Visibility = Visibility.Visible;
        if (MediaBusyRing is not null)
            MediaBusyRing.IsActive = true;
    }

    private void EndMediaBusy()
    {
        _mediaBusyDelay?.Stop();
        _mediaBusyCancel = null;
        if (MediaBusyOverlay is not null)
            MediaBusyOverlay.Visibility = Visibility.Collapsed;
        if (MediaBusyRing is not null)
            MediaBusyRing.IsActive = false;
    }

    private void MediaBusyStop_Click(object sender, RoutedEventArgs e)
        => _mediaBusyCancel?.Invoke();

    private void ShowAudioError(string title, string message)
    {
        if (AudioErrorBar is null) return;
        AudioErrorBar.Title = title;
        AudioErrorBar.Message = message;
        AudioErrorBar.IsOpen = true;
    }

    private void BeginAudioBusy(string label, Action? cancel)
    {
        _audioBusyCancel = cancel;
        if (AudioBusyLabel is not null)
            AudioBusyLabel.Text = label;
        if (AudioBusyStopBtn is not null)
            AudioBusyStopBtn.Visibility = cancel is null ? Visibility.Collapsed : Visibility.Visible;
        _audioBusyDelay ??= DispatcherQueue.CreateTimer();
        _audioBusyDelay.Interval = TimeSpan.FromMilliseconds(PanelBusyDelayMs);
        _audioBusyDelay.IsRepeating = false;
        _audioBusyDelay.Tick -= OnAudioBusyDelayTick;
        _audioBusyDelay.Tick += OnAudioBusyDelayTick;
        _audioBusyDelay.Start();
    }

    private void OnAudioBusyDelayTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (AudioBusyOverlay is null) return;
        AudioBusyOverlay.Visibility = Visibility.Visible;
        if (AudioBusyRing is not null)
            AudioBusyRing.IsActive = true;
    }

    private void EndAudioBusy()
    {
        _audioBusyDelay?.Stop();
        _audioBusyCancel = null;
        if (AudioBusyOverlay is not null)
            AudioBusyOverlay.Visibility = Visibility.Collapsed;
        if (AudioBusyRing is not null)
            AudioBusyRing.IsActive = false;
    }

    private void AudioBusyStop_Click(object sender, RoutedEventArgs e)
        => _audioBusyCancel?.Invoke();

    private void RefreshAudioEmpty()
    {
        if (AudioEmpty is null) return;
        var session = AppSession.Current.Session;
        var hasClip = session?.ActiveTimeline.Tracks
            .SelectMany(track => track.Clips)
            .Any(clip => clip.MediaType is ClipType.video or ClipType.audio) == true;
        AudioEmpty.Visibility = hasClip ? Visibility.Collapsed : Visibility.Visible;
    }
}
