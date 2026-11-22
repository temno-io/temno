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
    private readonly Dictionary<(string Id, string SourcePath, int Height), ImageSource> _mediaThumbCache = new();

    private void BindSession(bool resetPreview = false)
    {
        var session = AppSession.Current.Session;
        if (session is null)
        {
            _mediaThumbCache.Clear();
            _boundSession = null;
            AttachSessionSignal(null);
            Frame?.Navigate(typeof(HomePage));
            return;
        }

        if (!ReferenceEquals(_boundSession, session))
            _mediaThumbCache.Clear();

        _boundSession = session;
        AttachSessionSignal(session);
        _renderedRevision = session.Revision;
        _vm.RefreshFromSession();

        ProjectTitleText.Text = _vm.TitleBarLabel;
        TimelineSummaryText.Text = _vm.TimelineSummary;
        StatusText.Text = _vm.StatusText;
        RefreshStatusPath();
        InstallEditorTitleBar();

        RefreshMediaWorkspace();
        RefreshExternalMediaBar();
        ResetMediaSearch();
        _ = LoadMediaThumbnailsAsync(_workspaceMediaItems);
        SyncTimelineTabsSource(force: true);
        RefreshLibraryTimelines();

        _syncingPlayhead = true;
        try
        {
            Timeline.Bind(session);
            _timelineFitPending = true; // WP-185: sized once the shell has a real height
            if (_layoutLoaded && RootGrid.ActualHeight > 0)
            {
                // Project switch inside a live window: no SizeChanged will come, fit now (Bind rebuilt the lanes).
                _timelineFitPending = false;
                FitTimelineHeightToTracks(RootGrid.ActualHeight);
            }
            Preview.Bind(session, resetMonitor: resetPreview);
            Timeline.IsPlaying = Preview.IsPlaying;
            Inspector.Bind(session);
            Inspector.PlayheadFrame = Timeline.PlayheadFrame;
            TranscriptIndex.SetPlayheadFrame(Timeline.PlayheadFrame);
        }
        finally
        {
            _syncingPlayhead = false;
        }
        ShowInspectorForTimelineSelection();
        TranscriptIndex.NotifySessionChanged();
        ApplyPanelVisibility();
        SyncZoomSliderFromTimeline();
        SyncTimelineToolbar();
        UpdateTimelineToolbarLayout();
        ScrollActiveTimelineTabIntoView();
        SetFocusedPanel(ShellPanel.Timeline);
        _ = RefreshAudioMeterAsync(force: false);
        RefreshCaptionFormFromTimeline();
        RefreshAudioEmpty();
    }

    private void RefreshAfterEdit()
    {
        // WP-153: stamp before drawing, not after. A mutation that lands mid-refresh must leave the
        // revision ahead of the stamp so the queued tick draws again; stamping afterwards would
        // swallow exactly the external edit this signal exists to show.
        _renderedRevision = AppSession.Current.Session?.Revision ?? -1;
        TraceRefresh();
        _vm.RefreshFromSession();
        ProjectTitleText.Text = _vm.TitleBarLabel;
        TimelineSummaryText.Text = _vm.TimelineSummary;
        StatusText.Text = _vm.StatusText;
        RefreshStatusPath();
        if (App.MainAppWindow is not null)
            App.MainAppWindow.Title = _vm.TitleBarLabel;
        RefreshMediaWorkspace();
        RefreshExternalMediaBar();
        if (_mediaSearchActive) ResetMediaSearch();
        _ = LoadMediaThumbnailsAsync(_workspaceMediaItems);
        var tabsChanged = SyncTimelineTabsSource();
        RefreshLibraryTimelines();
        _syncingPlayhead = true;
        try
        {
            Timeline.Rebuild();
            Preview.Bind(AppSession.Current.Session, resetMonitor: false);
            Inspector.PlayheadFrame = Timeline.PlayheadFrame;
            TranscriptIndex.SetPlayheadFrame(Timeline.PlayheadFrame);
        }
        finally
        {
            _syncingPlayhead = false;
        }
        ShowInspectorForTimelineSelection();
        TranscriptIndex.NotifySessionChanged();
        SyncZoomSliderFromTimeline();
        SyncTimelineToolbar();
        UpdateTimelineToolbarLayout();
        if (tabsChanged)
            ScrollActiveTimelineTabIntoView();
        _ = RefreshAudioMeterAsync(force: false);
        RefreshCaptionFormFromTimeline();
        RefreshAudioEmpty();
    }

    // WP-144 (N4): the count is derived from what MediaList actually displays, never written as a
    // snapshot. It used to be assigned in four places from _workspaceMediaItems, and on project open
    // the label kept the constructor's "0 items" while the list showed content -- measured 2026-08-30,
    // label "0 items" against 1 MediaEntryItem. Displayed data must match displayed content.
    private void SyncMediaCountLabel()
    {
        if (MediaSearchStatus is null || MediaList is null) return;
        if (_mediaSearchActive) return; // search owns the label while a query is live
        // ItemCollection.Count is the leaf items, not the group headers (UIA 2026-09-01:
        // 1 ListViewItem + 1 ListViewHeaderItem, label "1 item").
        // WP-158: folder tiles are on screen too. A run that showed two of them still read
        // "0 items" — the same class of untruth as the empty state this package fixed.
        var n = MediaList.Items.Count;
        var folderCount = FolderGrid is { Visibility: Visibility.Visible } ? FolderGrid.Items.Count : 0;
        var text = MediaLibraryScope.CountLabel(folderCount, n);
        if (MediaSearchStatus.Text == text) return;
        MediaSearchStatus.Text = text;
    }

    /// <summary>Grouped mode needs a grouped view; every other mode binds the flat list.</summary>
    private void ApplyMediaListSource()
    {
        if (_mediaViewState.Mode == MediaLibraryViewMode.Grouped)
        {
            _mediaGroupedSource = new Microsoft.UI.Xaml.Data.CollectionViewSource
            {
                IsSourceGrouped = true,
                Source = MediaKindGroup.FromItems(_workspaceMediaItems),
            };
            MediaList.ItemsSource = _mediaGroupedSource.View;
        }
        else
        {
            MediaList.ItemsSource = _workspaceMediaItems;
        }
    }

    private void SyncMediaViewOptions()
    {
        if (MediaModeFolders is null) return;
        var m = _mediaViewState.Mode;
        MediaModeFolders.IsChecked = m == MediaLibraryViewMode.Folders;
        MediaModeList.IsChecked = m == MediaLibraryViewMode.List;
        MediaModeFlat.IsChecked = m == MediaLibraryViewMode.Flat;
        MediaModeGrouped.IsChecked = m == MediaLibraryViewMode.Grouped;
        var z = _mediaViewState.ThumbnailSize;
        MediaSizeSmall.IsChecked = z == MediaThumbnailSize.Small;
        MediaSizeMedium.IsChecked = z == MediaThumbnailSize.Medium;
        MediaSizeLarge.IsChecked = z == MediaThumbnailSize.Large;
        MediaSizeXL.IsChecked = z == MediaThumbnailSize.ExtraLarge;
    }

    private void MediaViewMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } ||
            !Enum.TryParse<MediaLibraryViewMode>(tag, out var mode)) return;
        _mediaViewState = _mediaViewState with { Mode = mode };
        MediaLibraryViewStore.Save(_mediaViewState);
        SyncMediaViewOptions();
        // Refresh rebuilds ItemsSource. If a search is live the label would stay on the match
        // string while the list showed the library — the one hole in the _mediaSearchActive guard.
        if (_mediaSearchActive)
        {
            _mediaSearchGeneration++;
            _mediaSearchActive = false;
            if (MediaSearchBox is not null) MediaSearchBox.Text = string.Empty;
        }
        RefreshMediaWorkspace();
        _ = LoadMediaThumbnailsAsync(_workspaceMediaItems);
    }

    private void MediaThumbSize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } ||
            !Enum.TryParse<MediaThumbnailSize>(tag, out var size)) return;
        _mediaViewState = _mediaViewState with { ThumbnailSize = size };
        MediaLibraryViewStore.Save(_mediaViewState);
        SyncMediaViewOptions();
        // Existing thumbnails were decoded at the old edge; drop them so the new size is fetched.
        _mediaThumbCache.Clear();
        foreach (var item in _workspaceMediaItems) item.Thumbnail = null;
        _ = LoadMediaThumbnailsAsync(_workspaceMediaItems);
    }

    private void RefreshMediaWorkspace()
    {
        var session = AppSession.Current.Session;
        if (session is null)
        {
            _currentMediaFolderId = null;
            _workspaceMediaItems = [];
            FolderGrid.ItemsSource = Array.Empty<MediaFolderItem>();
            MediaList.ItemsSource = Array.Empty<MediaEntryItem>();
            SetMediaEmptyLibraryCopy();
            MediaEmpty.Visibility = Visibility.Visible;
            SyncMediaCountLabel();
            RebuildMediaBreadcrumb();
            return;
        }

        if (_currentMediaFolderId is not null && session.Manifest.Folders.All(folder => folder.Id != _currentMediaFolderId))
            _currentMediaFolderId = null;

        // WP-158: which folders are in view is decided by MediaLibraryScope, in Temno.Core.Ui, so
        // the rule is testable. Folders shows tiles plus this folder's own items; List shows this
        // folder and everything beneath it with no tiles; Flat and Grouped ignore folders entirely.
        var mode = _mediaViewState.Mode;
        var showFolders = MediaLibraryScope.ShowsFolderTiles(mode);
        var visibleFolderIds = MediaLibraryScope.VisibleFolderIds(
            session.Manifest.Folders, _currentMediaFolderId, mode);

        var folders = !showFolders
            ? []
            : session.Manifest.Folders
            .Where(folder => folder.ParentFolderId == _currentMediaFolderId)
            .OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(folder => new MediaFolderItem(folder.Id, folder.Name))
            .ToList();
        _workspaceMediaItems = _vm.MediaItems
            .Where(item => MediaLibraryScope.ItemIsVisible(item.FolderId, visibleFolderIds, mode))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var thumbHeight = _mediaViewState.ThumbnailSize.ToPixels();
        foreach (var item in _workspaceMediaItems)
        {
            if (item.IsVisual && !item.HasThumbnail && !string.IsNullOrEmpty(item.SourcePath)
                && _mediaThumbCache.TryGetValue((item.Id, item.SourcePath, thumbHeight), out var cachedThumb))
            {
                item.Thumbnail = cachedThumb;
            }
        }
        FolderGrid.ItemsSource = folders;
        FolderGrid.Visibility = folders.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyMediaListSource();

        // "No media yet" is reserved for a project that really has none. Showing it over media the
        // user cannot see is the defect this package exists to fix.
        var reason = MediaLibraryScope.EmptyReason(
            _vm.MediaItems.Count, _workspaceMediaItems.Count, folders.Count);
        SetMediaEmptyCopy(reason);
        MediaEmpty.Visibility = reason == MediaLibraryEmptyReason.None
            ? Visibility.Collapsed
            : Visibility.Visible;
        SyncMediaCountLabel();
        RebuildMediaBreadcrumb();
    }

    private void RebuildMediaBreadcrumb()
    {
        MediaBreadcrumbBar.Children.Clear();
        AddBreadcrumbSegment("Project media", null);
        var session = AppSession.Current.Session;
        if (session is null) return;
        foreach (var folder in MediaLibraryCommands.FolderPath(session.Manifest, _currentMediaFolderId))
        {
            MediaBreadcrumbBar.Children.Add(Temno.App.Controls.Shared.FluentPath.At(Temno.App.Controls.Shared.Icons.ChevronRight, AppTheme.IconSize.Xs, AppTheme.Text.TertiaryBrush));
            AddBreadcrumbSegment(folder.Name, folder.Id);
        }
    }

    private void AddBreadcrumbSegment(string label, string? folderId)
    {
        var button = new Button
        {
            Content = label,
            Padding = AppTheme.Padding.HXxs,
            FontSize = 12,
            Tag = folderId,
        };
        button.Click += (_, _) => NavigateToMediaFolder((string?)button.Tag);
        MediaBreadcrumbBar.Children.Add(button);
    }

    private void NavigateToMediaFolder(string? folderId)
    {
        _currentMediaFolderId = folderId;
        _mediaSearchActive = false;
        if (MediaSearchBox is not null) MediaSearchBox.Text = string.Empty;
        RefreshMediaWorkspace();
        _ = LoadMediaThumbnailsAsync(_workspaceMediaItems);
    }

    // --- WP-46: local visual media search ---

    private async Task LoadMediaThumbnailsAsync(IReadOnlyList<MediaEntryItem> items)
    {
        var generation = ++_thumbnailGeneration; // a later refresh supersedes this pass
        foreach (var item in items)
        {
            if (generation != _thumbnailGeneration) return;
            if (!item.IsVisual || item.HasThumbnail || string.IsNullOrEmpty(item.SourcePath) || !File.Exists(item.SourcePath))
                continue;
            // WP-144 (N6): midpoint, then step to ¾ then ¼ if that JPEG is near-black.
            // Stills (duration 0) stay at 0. Audio is skipped by IsVisual above.
            var seconds = MediaPosterTimes.Primary(item.DurationSeconds);
            var height = _mediaViewState.ThumbnailSize.ToPixels();
            try
            {
                var thumbPath = await _thumbnailCache.GetOrCreateAsync(item.SourcePath!, seconds, height);
                if (generation != _thumbnailGeneration) return;
                if (item.DurationSeconds > 0.5 && File.Exists(thumbPath))
                {
                    var lum = await MeanJpegLuminanceAsync(thumbPath);
                    if (lum < NearBlackLuminance)
                    {
                        foreach (var alt in MediaPosterTimes.Fallbacks(item.DurationSeconds, seconds))
                        {
                            if (generation != _thumbnailGeneration) return;
                            var altPath = await _thumbnailCache.GetOrCreateAsync(item.SourcePath!, alt, height);
                            if (generation != _thumbnailGeneration) return;
                            if (File.Exists(altPath) && await MeanJpegLuminanceAsync(altPath) >= NearBlackLuminance)
                            {
                                thumbPath = altPath;
                                break;
                            }
                        }
                    }
                }
                if (File.Exists(thumbPath))
                {
                    var bmp = new BitmapImage(new Uri(thumbPath));
                    item.Thumbnail = bmp;
                    _mediaThumbCache[(item.Id, item.SourcePath!, height)] = bmp;
                }
            }
            catch
            {
                // Undecodable/offline media keeps the type glyph.
            }
        }
    }

    /// <summary>
    /// Mean 8-bit Rec.601 luminance below this is treated as a black slate. The pre-fix
    /// 1.0 s poster on project 55 measured 4.86; the midpoint measured 76.66.
    /// </summary>
    private const double NearBlackLuminance = 16;

    private static async Task<double> MeanJpegLuminanceAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = 16,
            ScaledHeight = 16,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Rgba8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        var bytes = pixels.DetachPixelData();
        double sum = 0;
        var n = bytes.Length / 4;
        for (var i = 0; i < bytes.Length; i += 4)
            sum += 0.299 * bytes[i] + 0.587 * bytes[i + 1] + 0.114 * bytes[i + 2];
        return n == 0 ? 0 : sum / n;
    }

    private void MediaInsert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MediaEntryItem item }) return;
        _vm.AgentVisible = false;
        _vm.InspectorVisible = true;
        ApplyPanelVisibility();
        if (!Timeline.InsertLibraryMediaAtPlayhead(item.Id))
        {
            StatusText.Text = "Could not add this media to the timeline.";
            return;
        }
        Preview.ActivateTimelineTab();
        _focusedPanel = ShellPanel.Timeline;
        RootGrid.Focus(FocusState.Programmatic);
        StatusText.Text = "Added " + item.Name + " at playhead";
    }

    private void MediaList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // Carry the mediaRef as an in-app property so TimelineControl can place it. This is distinct
        // from the StorageItems payload used for Explorer file imports.
        if (e.Items.Count > 0 && e.Items[0] is MediaEntryItem item)
        {
            e.Data.Properties.Add(TimelineControl.MediaRefDragKey, item.Id);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
            // Hide the ListView item bitmap; TimelineControl draws a drop ghost under the cursor.
        }
        else
        {
            e.Cancel = true;
        }
    }

    // --- WP-66: media selection → preview, context menu (reveal/rename/relink/delete) ---

    private void MediaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MediaList.SelectedItem is not MediaEntryItem item)
            return;

        if (Timeline.SelectedClipIds.Count > 0)
        {
            _suppressTimelineFocusOnSelectionChange = true;
            try { Timeline.SetSelectedClip(null); }
            finally { _suppressTimelineFocusOnSelectionChange = false; }
        }
        SetFocusedPanel(ShellPanel.Media);
        Inspector.ShowMediaAsset(item);

        var offline = string.IsNullOrEmpty(item.SourcePath) || !File.Exists(item.SourcePath);
        StatusText.Text = offline
            ? $"Selected · {item.Name} (offline)"
            : $"Selected · {item.Name}";
    }

    private void MediaList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (MediaList.SelectedItem is not MediaEntryItem item)
            return;
        OpenMediaSourceTab(item);
    }

    private void OpenMediaSourceTab(MediaEntryItem item)
    {
        var type = ParseMediaKind(item);
        Preview.OpenSourceTab(item.Id, item.Name, type, item.DurationSeconds);
        SetFocusedPanel(ShellPanel.Preview);
        var offline = string.IsNullOrEmpty(item.SourcePath) || !File.Exists(item.SourcePath);
        StatusText.Text = offline
            ? $"Source · {item.Name} (offline)"
            : $"Source · {item.Name}";
    }

    private static ClipType ParseMediaKind(MediaEntryItem item)
    {
        if (Enum.TryParse(item.Kind, ignoreCase: true, out ClipType type))
            return type;
        return item.IsVisual ? ClipType.video : ClipType.audio;
    }

    private void MediaReveal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MediaEntryItem item }) return;
        if (string.IsNullOrEmpty(item.SourcePath) || !File.Exists(item.SourcePath))
        {
            StatusText.Text = "Cannot reveal — media file is offline.";
            return;
        }
        AppSession.RevealInExplorer(item.SourcePath);
    }

    private async void MediaRename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MediaEntryItem item }) return;
        var session = AppSession.Current.Session;
        if (session is null) return;

        var box = new TextBox { Text = item.Name, Width = 280 };
        var dialog = new ContentDialog
        {
            Title = "Rename media",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = (box.Text ?? string.Empty).Trim();
        if (name.Length == 0) return;
        var result = EditCommands.RenameMediaEntry(session, item.Id, name);
        if (!result.Ok)
        {
            StatusText.Text = result.Error ?? "Rename failed";
            return;
        }
        RefreshAfterEdit();
        StatusText.Text = $"Renamed · {name}";
    }

    private async void MediaRelink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MediaEntryItem item }) return;
        await RelinkMediaAsync(item.Id, item.Name);
    }

    private void MediaDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MediaEntryItem item }) return;
        var session = AppSession.Current.Session;
        if (session is null) return;

        var result = EditCommands.RemoveMediaEntry(session, item.Id);
        if (!result.Ok)
        {
            StatusText.Text = result.Error ?? "Remove failed";
            return;
        }
        RefreshAfterEdit();
        StatusText.Text = $"Removed · {item.Name}";
    }

    private void FolderGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaFolderItem folder)
            NavigateToMediaFolder(folder.Id);
    }

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
        => await CreateFolderInCurrentLocationAsync();

    private async Task CreateFolderInCurrentLocationAsync()
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        var box = new TextBox { Header = "Folder name", PlaceholderText = "New Folder", MaxLength = 120 };
        var dialog = new ContentDialog
        {
            Title = "New media folder",
            Content = box,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var result = MediaLibraryCommands.CreateFolder(session, box.Text, _currentMediaFolderId, out var folderId);
        if (!result.Ok)
        {
            StatusText.Text = result.Error ?? "Could not create folder";
            return;
        }
        RefreshAfterEdit();
        StatusText.Text = $"Created folder · {session.Manifest.Folders.Single(folder => folder.Id == folderId).Name}";
    }

    private async void CreateMatte_Click(object sender, RoutedEventArgs e)
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        var color = new TextBox { Header = "Color", Text = "#000000", PlaceholderText = "#RRGGBB", MaxLength = 7 };
        var aspect = new ComboBox { Header = "Aspect ratio", ItemsSource = new[] { "Project", "16:9", "9:16", "1:1", "4:3", "9:14", "2.4:1" }, SelectedIndex = 0 };
        var content = new StackPanel { Spacing = AppTheme.Spacing.Md };
        content.Children.Add(color);
        content.Children.Add(aspect);
        var dialog = new ContentDialog
        {
            Title = "Create matte",
            Content = content,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var result = MediaLibraryCommands.CreateMatte(
            session,
            color.Text,
            aspect.SelectedItem as string,
            name: null,
            folderId: _currentMediaFolderId,
            out var entry);
        if (!result.Ok || entry is null)
        {
            StatusText.Text = result.Error ?? "Could not create matte";
            return;
        }
        RefreshAfterEdit();
        StatusText.Text = $"Created matte · {entry.Name}";
    }

    private async void MediaMove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MediaEntryItem item }) return;
        var destination = await ChooseMediaDestinationAsync("Move media");
        if (destination is null) return;
        var session = AppSession.Current.Session;
        if (session is null) return;
        var result = MediaLibraryCommands.MoveAssets(session, [item.Id], destination.Id);
        if (!result.Ok)
        {
            StatusText.Text = result.Error ?? "Could not move media";
            return;
        }
        RefreshAfterEdit();
        StatusText.Text = $"Moved · {item.Name} → {destination.Label}";
    }

    private async void FolderMove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MediaFolderItem folder }) return;
        var destination = await ChooseMediaDestinationAsync("Move folder");
        if (destination is null) return;
        var session = AppSession.Current.Session;
        if (session is null) return;
        var result = MediaLibraryCommands.MoveFolders(session, [folder.Id], destination.Id);
        if (!result.Ok)
        {
            StatusText.Text = result.Error ?? "Could not move folder";
            return;
        }
        RefreshAfterEdit();
        StatusText.Text = $"Moved folder · {folder.Name} → {destination.Label}";
    }

    private async Task<MediaFolderDestination?> ChooseMediaDestinationAsync(string title)
    {
        var session = AppSession.Current.Session;
        if (session is null) return null;
        var choices = new List<MediaFolderDestination> { new(null, "Library") };
        choices.AddRange(session.Manifest.Folders
            .Select(folder => new MediaFolderDestination(
                folder.Id,
                string.Join(" / ", MediaLibraryCommands.FolderPath(session.Manifest, folder.Id).Select(path => path.Name))))
            .OrderBy(choice => choice.Label, StringComparer.CurrentCultureIgnoreCase));
        var picker = new ComboBox { ItemsSource = choices, SelectedIndex = 0, MinWidth = 280 };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = picker,
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? picker.SelectedItem as MediaFolderDestination
            : null;
    }

    private void MediaSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Only react to the user clearing the box; submissions are handled by QuerySubmitted.
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput && string.IsNullOrWhiteSpace(sender.Text))
            ResetMediaSearch();
    }

    private void MediaSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = (args.QueryText ?? sender.Text ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            ResetMediaSearch();
            return;
        }

        _ = RunMediaSearchAsync(query);
    }

    private void ResetMediaSearch()
    {
        _mediaSearchGeneration++; // invalidate any in-flight search
        _mediaSearchActive = false;
        if (MediaSearchBox is not null) MediaSearchBox.Text = string.Empty;
        var n = _workspaceMediaItems.Count;
        // WP-144: go through ApplyMediaListSource so Grouped mode's CollectionViewSource is not
        // clobbered by a flat assignment. This method runs immediately after RefreshMediaWorkspace
        // on project open, and a direct assignment here silently dropped the grouping.
        ApplyMediaListSource();
        SyncMediaCountLabel();
        FolderGrid.Visibility = FolderGrid.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SetMediaEmptyLibraryCopy();
        MediaEmpty.Visibility = FolderGrid.Items.Count == 0 && n == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetMediaEmptyLibraryCopy() => SetMediaEmptyCopy(MediaLibraryEmptyReason.ProjectHasNoMedia);

    /// <summary>
    /// WP-158. Two different situations used to share one message. "No media yet · Import video,
    /// image, or audio" told the owner to import assets that were already in the project and already
    /// on the timeline — they were simply filed in a folder the view was not showing.
    /// </summary>
    private void SetMediaEmptyCopy(MediaLibraryEmptyReason reason)
    {
        if (MediaEmptyTitle is null || MediaEmptyBody is null) return;
        _mediaEmptyOffersLibraryRoot = reason == MediaLibraryEmptyReason.NothingInThisFolder;

        if (reason == MediaLibraryEmptyReason.NothingInThisFolder)
        {
            var elsewhere = _vm.MediaItems.Count;
            MediaEmptyTitle.Text = "Nothing here";
            MediaEmptyBody.Text = elsewhere == 1
                ? "This folder is empty. 1 item is elsewhere in the library."
                : $"This folder is empty. {elsewhere} items are elsewhere in the library.";
            if (MediaEmptyAction is not null)
            {
                MediaEmptyAction.Content = "Back to Library";
                AutomationProperties.SetName(MediaEmptyAction, "Back to library root");
                ToolTipService.SetToolTip(MediaEmptyAction, "Show the whole library");
            }

            return;
        }

        MediaEmptyTitle.Text = "No media yet";
        MediaEmptyBody.Text = "Import video, image, or audio to use on the timeline.";
        if (MediaEmptyAction is not null)
        {
            MediaEmptyAction.Content = "Import";
            AutomationProperties.SetName(MediaEmptyAction, "Import media");
            ToolTipService.SetToolTip(MediaEmptyAction, "Import video, image, or audio files");
        }
    }

    private void SetMediaEmptySearchMissCopy(string query)
    {
        MediaEmptyTitle.Text = "No media matched";
        MediaEmptyBody.Text = $"Nothing in this project matches “{query}”. Clear search to see the library.";
        if (MediaEmptyAction is not null)
        {
            MediaEmptyAction.Content = "Clear search";
            AutomationProperties.SetName(MediaEmptyAction, "Clear media search");
            ToolTipService.SetToolTip(MediaEmptyAction, "Clear search and show the library");
        }
    }

    private void MediaEmptyAction_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaSearchActive)
            ResetMediaSearch();
        else if (_mediaEmptyOffersLibraryRoot)
            NavigateMediaFolder(null);
        else
            ImportMedia_Click(sender, e);
    }

    /// <summary>WP-158: the way out of a folder that has nothing in it.</summary>
    private void NavigateMediaFolder(string? folderId)
    {
        _currentMediaFolderId = folderId;
        RefreshMediaWorkspace();
    }

    private void Timeline_EmptyImportRequested(object? sender, EventArgs e)
        => ImportMedia_Click(this, new RoutedEventArgs());

    private void AudioEmptyImport_Click(object sender, RoutedEventArgs e)
        => ImportMedia_Click(sender, e);

    private async Task RunMediaSearchAsync(string query)
    {
        if (AppSession.Current.Session is null) return;

        var generation = ++_mediaSearchGeneration;
        _mediaSearchActive = true;
        MediaSearchStatus.Text = "Searching locally…";
        BeginMediaBusy("Searching…", () =>
        {
            _mediaSearchGeneration++;
            EndMediaBusy();
            ResetMediaSearch();
        });

        var args = new JsonObject
        {
            ["query"] = query,
            ["scope"] = "both",
            ["limit"] = 200,
        };

        MediaSearchResult outcome;
        try
        {
            // Indexing may decode frames via FFmpeg on first run. search_media snapshots the
            // manifest on the session thread, then searches on this worker.
            var result = await Task.Run(() => ToolExecutor.Execute("search_media", args)).ConfigureAwait(true);
            outcome = ParseMediaSearchResult(result);
        }
        catch (Exception ex)
        {
            outcome = MediaSearchResult.Failure(ex.Message);
        }

        if (generation != _mediaSearchGeneration) return; // superseded or reset

        EndMediaBusy();
        ApplyMediaSearchOutcome(query, outcome);
    }

    private void ApplyMediaSearchOutcome(string query, MediaSearchResult outcome)
    {
        if (!outcome.Ok)
        {
            // Invalid grammar or an error: keep the full list, surface the reason inline.
            MediaList.ItemsSource = _workspaceMediaItems;
            FolderGrid.Visibility = FolderGrid.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            SetMediaEmptyLibraryCopy();
            MediaEmpty.Visibility = FolderGrid.Items.Count == 0 && _workspaceMediaItems.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            MediaSearchStatus.Text = outcome.Message;
            ShowMediaError("Search failed", outcome.Message + " Check the query grammar, then try again.");
            return;
        }

        // Tolerate (unexpected) duplicate ids from a hand-edited manifest rather than throwing on a
        // fire-and-forget task, which would leave the status stuck on "Searching…".
        var byId = new Dictionary<string, MediaEntryItem>();
        foreach (var m in _vm.MediaItems) byId[m.Id] = m;
        var matched = new List<MediaEntryItem>();
        foreach (var mediaRef in outcome.OrderedMediaRefs)
        {
            if (byId.TryGetValue(mediaRef, out var item) && !matched.Contains(item))
                matched.Add(item);
        }

        MediaList.ItemsSource = matched;
        FolderGrid.Visibility = Visibility.Collapsed;
        if (matched.Count == 0)
        {
            SetMediaEmptySearchMissCopy(query);
            MediaEmpty.Visibility = Visibility.Visible;
            MediaSearchStatus.Text = $"No media matched “{query}”";
        }
        else
        {
            MediaEmpty.Visibility = Visibility.Collapsed;
            MediaSearchStatus.Text = $"{matched.Count} of {_vm.MediaItems.Count} media match";
        }
    }

    private static MediaSearchResult ParseMediaSearchResult(ToolCallResult result)
    {
        var body = string.Join("\n", result.Content.Select(c => c.Text));
        if (result.IsError)
            return MediaSearchResult.Failure(body.StartsWith("Error: ") ? body[7..] : body);

        if (JsonNode.Parse(body) is not JsonObject obj)
            return MediaSearchResult.Failure("Search returned an unexpected response.");

        var status = obj["status"]?.GetValue<string>() ?? "";
        var message = obj["message"]?.GetValue<string>() ?? "";

        // Grammar rejections / unsupported queries are reported honestly, not as matches.
        if (status is "unsupported_query" or "unsupported" or "model_not_installed")
            return MediaSearchResult.Failure(string.IsNullOrWhiteSpace(message)
                ? $"Unsupported query. {VisualSearchQueryParser.SupportedGrammar}"
                : message);

        var orderedRefs = new List<string>();
        void AddRefs(JsonArray? arr)
        {
            if (arr is null) return;
            foreach (var hit in arr)
            {
                if (hit?["mediaRef"]?.GetValue<string>() is { Length: > 0 } mediaRef && !orderedRefs.Contains(mediaRef))
                    orderedRefs.Add(mediaRef);
            }
        }
        AddRefs(obj["results"] as JsonArray);
        AddRefs(obj["moments"] as JsonArray);
        AddRefs(obj["spoken"] as JsonArray);

        return MediaSearchResult.Success(
            orderedRefs,
            string.IsNullOrWhiteSpace(message) ? "No matches." : message);
    }

    private sealed record MediaSearchResult(
        bool Ok,
        IReadOnlyList<string> OrderedMediaRefs,
        string Message)
    {
        public static MediaSearchResult Success(IReadOnlyList<string> refs, string message)
            => new(true, refs, message);
        public static MediaSearchResult Failure(string message) => new(false, [], message);
    }

    // --- WP-73/WP-131 media panel rail (Media / Index / Captions / Audio) ---

    private void MediaRail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tab) return;
        SetMediaRailTab(tab);
    }

    private void SetMediaRailTab(string tab)
    {
        _mediaRailTab = tab switch
        {
            "Index" => "Index",
            "Captions" => "Captions",
            "Audio" => "Audio",
            _ => "Media",
        };

        // Keep exactly one rail button checked (ToggleButton would uncheck on re-click).
        RailMediaBtn.IsChecked = _mediaRailTab == "Media";
        RailIndexBtn.IsChecked = _mediaRailTab == "Index";
        RailCaptionsBtn.IsChecked = _mediaRailTab == "Captions";
        RailAudioBtn.IsChecked = _mediaRailTab == "Audio";

        // Selection remains visible for keyboard and pointer navigation.
        RailMediaIndicator.Visibility = _mediaRailTab == "Media" ? Visibility.Visible : Visibility.Collapsed;
        RailIndexIndicator.Visibility = _mediaRailTab == "Index" ? Visibility.Visible : Visibility.Collapsed;
        RailCaptionsIndicator.Visibility = _mediaRailTab == "Captions" ? Visibility.Visible : Visibility.Collapsed;
        RailAudioIndicator.Visibility = _mediaRailTab == "Audio" ? Visibility.Visible : Visibility.Collapsed;

        MediaTabPane.Visibility = _mediaRailTab == "Media" ? Visibility.Visible : Visibility.Collapsed;
        IndexTabPane.Visibility = _mediaRailTab == "Index" ? Visibility.Visible : Visibility.Collapsed;
        CaptionsTabPane.Visibility = _mediaRailTab == "Captions" ? Visibility.Visible : Visibility.Collapsed;
        AudioTabPane.Visibility = _mediaRailTab == "Audio" ? Visibility.Visible : Visibility.Collapsed;

        TranscriptIndex.SetActive(_mediaRailTab == "Index");

        if (_mediaRailTab == "Captions")
        {
            EnsureCaptionForm();
            RefreshCaptionsSourceLabel();
        }
    }

    private void TranscriptIndex_SeekRequested(object? sender, int frame)
    {
        _syncingPlayhead = true;
        Timeline.PlayheadFrame = frame;
        Preview.ActivateTimelineTab(frame);
        Inspector.PlayheadFrame = frame;
        TranscriptIndex.SetPlayheadFrame(frame);
        _syncingPlayhead = false;
    }

    private void TranscriptIndex_TimelineEdited(object? sender, EventArgs e)
        => RefreshAfterEdit();

    private void TranscriptIndex_OpenCaptionsRequested(object? sender, EventArgs e)
        => SetMediaRailTab("Captions");
}
