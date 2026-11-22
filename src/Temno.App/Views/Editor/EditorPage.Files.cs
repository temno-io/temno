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
    private void Save_Click(object sender, RoutedEventArgs e)
        => SaveProject();

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        try
        {
            var nameBox = new TextBox { Text = _vm.ProjectName };
            var dialog = new ContentDialog
            {
                Title = "Save project as",
                Content = nameBox,
                PrimaryButtonText = "Choose folder…",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var name = AppSession.SanitizeProjectName(nameBox.Text);
            if (string.IsNullOrWhiteSpace(name)) name = "Untitled Project";

            var folderPicker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            folderPicker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, MainWindowHandle());
            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder is null) return;

            var newDir = AppSession.AllocateUniquePackagePath(folder.Path, name);
            session.Save(newDir);
            ProjectRegistry.Register(newDir);
            BindSession();
            StatusText.Text = "Saved as " + Path.GetFileName(newDir);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Save As failed: " + ex.Message;
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
        => await ShowExportDialogAsync();

    /// <summary>Export sheet: video (queued) plus FCPXML / XMEML / self-contained temno package.</summary>
    private async Task ShowExportDialogAsync()
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        var timeline = session.ActiveTimeline;

        var formatBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        formatBox.Items.Add("Video");
        formatBox.Items.Add("Final Cut Pro XML (FCPXML)");
        formatBox.Items.Add("Premiere / Resolve XML (XMEML)");
        formatBox.Items.Add("Self-contained .temno package");
        var codecBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        codecBox.Items.Add("H.264");
        codecBox.Items.Add("H.265");
        codecBox.Items.Add("ProRes");
        codecBox.Items.Add("HEVC 10-bit HDR");
        var resBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        resBox.Items.Add(ExportOutputSize.ChoiceLabel(ExportResolution.MatchTimeline, timeline.Width, timeline.Height));
        resBox.Items.Add(ExportOutputSize.ChoiceLabel(ExportResolution.R720p, timeline.Width, timeline.Height));
        resBox.Items.Add(ExportOutputSize.ChoiceLabel(ExportResolution.R1080p, timeline.Width, timeline.Height));
        resBox.Items.Add(ExportOutputSize.ChoiceLabel(ExportResolution.R1440p, timeline.Width, timeline.Height));
        resBox.Items.Add(ExportOutputSize.ChoiceLabel(ExportResolution.R4k, timeline.Width, timeline.Height));
        resBox.Items.Add("Custom");
        var customWidthField = new UnitNumberField
        {
            Spec = new UnitFieldSpec
            {
                Unit = " px",
                Minimum = ExportOutputSize.MinDimension,
                Maximum = ExportOutputSize.MaxDimension,
                Step = 2,
                Decimals = 0,
                UnitsPerPixel = 4,
                Default = timeline.Width,
            },
            Value = timeline.Width,
        };
        var customHeightField = new UnitNumberField
        {
            Spec = new UnitFieldSpec
            {
                Unit = " px",
                Minimum = ExportOutputSize.MinDimension,
                Maximum = ExportOutputSize.MaxDimension,
                Step = 2,
                Decimals = 0,
                UnitsPerPixel = 4,
                Default = timeline.Height,
            },
            Value = timeline.Height,
        };
        var allowUpscaleBox = new CheckBox
        {
            Content = "Allow upscale",
            IsChecked = false,
            FontSize = AppTheme.FontSize.Md,
        };
        var fpsBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        fpsBox.Items.Add(ExportFrameRate.MatchTimelineLabel(timeline.Fps));
        foreach (var fpsPreset in ExportFrameRate.Presets)
            fpsBox.Items.Add(fpsPreset.Label);
        var customFpsIndex = ExportFrameRate.Presets.Count + 1;
        fpsBox.Items.Add("Custom");
        var customFpsField = new UnitNumberField
        {
            Spec = new UnitFieldSpec
            {
                Unit = " fps",
                Minimum = ExportFrameRate.MinFps,
                Maximum = ExportFrameRate.MaxFps,
                Step = 1,
                Decimals = 0,
                UnitsPerPixel = 0.25,
                Default = Math.Clamp(timeline.Fps, (int)ExportFrameRate.MinFps, (int)ExportFrameRate.MaxFps),
            },
            Value = Math.Clamp(timeline.Fps, (int)ExportFrameRate.MinFps, (int)ExportFrameRate.MaxFps),
        };
        var prefs = UserPreferences.Load();
        var hardwareBox = new CheckBox
        {
            Content = "Prefer hardware",
            IsChecked = prefs.ResolvePreferHardwareEncode(),
            FontSize = AppTheme.FontSize.Md,
        };
        var fileNameBox = new TextBox { Text = _vm.ProjectName + ".mp4" };
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (!Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var folderBox = new TextBox { Text = folder };
        AutomationProperties.SetName(fileNameBox, "Export file name");
        AutomationProperties.SetName(folderBox, "Export folder");
        AutomationProperties.SetName(formatBox, "Export format");
        AutomationProperties.SetName(codecBox, "Export codec");
        AutomationProperties.SetName(resBox, "Export resolution");
        AutomationProperties.SetAutomationId(customWidthField, "Export.CustomWidth");
        AutomationProperties.SetName(customWidthField, "Export width");
        AutomationProperties.SetAutomationId(customHeightField, "Export.CustomHeight");
        AutomationProperties.SetName(customHeightField, "Export height");
        AutomationProperties.SetName(allowUpscaleBox, "Allow upscale");
        AutomationProperties.SetAutomationId(fpsBox, "Export.FrameRate");
        AutomationProperties.SetName(fpsBox, "Export frame rate");
        AutomationProperties.SetAutomationId(customFpsField, "Export.CustomFps");
        AutomationProperties.SetName(customFpsField, "Custom export frame rate");
        ToolTipService.SetToolTip(customWidthField, "Custom export width in pixels. Must be even. A different shape is fitted inside with black bars.");
        ToolTipService.SetToolTip(customHeightField, "Custom export height in pixels. Must be even. A different shape is fitted inside with black bars.");
        ToolTipService.SetToolTip(allowUpscaleBox, "Allow a custom size larger than the timeline.");
        ToolTipService.SetToolTip(fpsBox, "Output frame rate. Match Timeline keeps the timeline's native rate.");
        ToolTipService.SetToolTip(customFpsField, "Custom export frame rate (1–120 fps).");
        var validation = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, IsClosable = true };
        Grid Row(string label, FrameworkElement value)
        {
            var row = new Grid { ColumnSpacing = AppTheme.Spacing.Lg };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(AppTheme.ComponentSize.ExportLabelWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center,
                Foreground = AppTheme.Text.TertiaryBrush, FontSize = AppTheme.FontSize.SmMd });
            Grid.SetColumn(value, 1);
            row.Children.Add(value);
            return row;
        }
        var panel = new StackPanel
        {
            Spacing = AppTheme.Spacing.Md,
            Width = Math.Min(AppTheme.ComponentSize.ExportContentMaxWidth,
                Math.Max(AppTheme.ComponentSize.DialogMinContentWidth, XamlRoot.Size.Width - AppTheme.ComponentSize.DialogHorizontalInset)),
        };
        panel.Children.Add(new TextBlock
        {
            Text = $"{timeline.Name} · {TimelineGeometry.FormatTimecodeFull(timeline.TotalFrames, timeline.Fps)} · {timeline.Width} × {timeline.Height} · {timeline.Fps} fps",
            Foreground = AppTheme.Text.SecondaryBrush, Margin = AppTheme.Padding.BottomSmMd,
            FontSize = AppTheme.FontSize.SmMd,
        });
        panel.Children.Add(Row("Format", formatBox));
        var codecRow = Row("Codec", codecBox);
        var resolutionRow = Row("Resolution", resBox);
        var customWidthRow = Row("Width", customWidthField);
        var customHeightRow = Row("Height", customHeightField);
        var upscaleRow = Row("Upscale", allowUpscaleBox);
        var fpsRow = Row("Frame Rate", fpsBox);
        var customFpsRow = Row("Custom FPS", customFpsField);
        customWidthRow.Visibility = customHeightRow.Visibility = upscaleRow.Visibility = customFpsRow.Visibility = Visibility.Collapsed;
        var hardwareRow = Row("Hardware", hardwareBox);
        panel.Children.Add(codecRow);
        panel.Children.Add(resolutionRow);
        panel.Children.Add(customWidthRow);
        panel.Children.Add(customHeightRow);
        panel.Children.Add(upscaleRow);
        panel.Children.Add(fpsRow);
        panel.Children.Add(customFpsRow);
        panel.Children.Add(hardwareRow);
        var destination = new StackPanel { Spacing = AppTheme.Spacing.Md, Margin = AppTheme.Padding.TopSmMd };
        destination.Children.Add(new TextBlock { Text = "Destination", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        destination.Children.Add(Row("File", fileNameBox));
        destination.Children.Add(Row("Folder", folderBox));
        var browse = new Button { Content = "Browse…" };
        browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindowHandle());
                var picked = await picker.PickSingleFolderAsync();
                if (picked is not null) folderBox.Text = picked.Path;
            }
            catch (Exception ex) { validation.Message = ex.Message; validation.IsOpen = true; }
        };
        destination.Children.Add(browse);
        panel.Children.Add(destination);
        panel.Children.Add(validation);
        ExportResolution ResolutionAt(int index) => index switch
        {
            1 => ExportResolution.R720p,
            2 => ExportResolution.R1080p,
            3 => ExportResolution.R1440p,
            4 => ExportResolution.R4k,
            _ => ExportResolution.MatchTimeline,
        };
        void SyncCustomSizeUi()
        {
            var custom = formatBox.SelectedIndex == 0 && resBox.SelectedIndex == 5;
            var vis = custom ? Visibility.Visible : Visibility.Collapsed;
            customWidthRow.Visibility = customHeightRow.Visibility = upscaleRow.Visibility = vis;
        }
        void SyncCustomFpsUi()
        {
            var custom = formatBox.SelectedIndex == 0 && fpsBox.SelectedIndex == customFpsIndex;
            customFpsRow.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        }
        void SyncFormatUi()
        {
            var visibility = formatBox.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
            codecRow.Visibility = resolutionRow.Visibility = fpsRow.Visibility = hardwareRow.Visibility = destination.Visibility = visibility;
            if (visibility != Visibility.Visible)
                customWidthRow.Visibility = customHeightRow.Visibility = upscaleRow.Visibility = customFpsRow.Visibility = Visibility.Collapsed;
            else
            {
                SyncCustomSizeUi();
                SyncCustomFpsUi();
            }
        }
        formatBox.SelectionChanged += (_, _) => SyncFormatUi();
        resBox.SelectionChanged += (_, _) => SyncCustomSizeUi();
        fpsBox.SelectionChanged += (_, _) => SyncCustomFpsUi();
        codecBox.SelectionChanged += (_, _) =>
        {
            fileNameBox.Text = Path.ChangeExtension(fileNameBox.Text,
                codecBox.SelectedIndex is 2 or 3 ? ".mov" : ".mp4");
        };
        SyncFormatUi();
        var queueList = new ExportQueueView();
        void RefreshQueueText() => queueList.Refresh(ToolExecutor.ExportQueue.Jobs);
        RefreshQueueText();
        panel.Children.Add(new TextBlock { Text = "Export queue", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = AppTheme.Padding.TopSmMd });
        panel.Children.Add(new ScrollViewer { Content = queueList, MaxHeight = AppTheme.ComponentSize.ExportQueueMaxHeight });

        var cta = ToolExecutor.ExportQueue.HasActivity ? "Add to Queue" : "Export";
        // WP-190: the cancel button names its scope ("Cancel export" / "Cancel all N exports") and
        // is disabled when nothing is pending. It used to read "Cancel active" on an empty queue and
        // cancel every Waiting job while promising the active one.
        var dialog = AppDialog.Create(XamlRoot);
        dialog.Title = "Export";
        dialog.Content = new ScrollViewer { Content = panel,
            MaxHeight = Math.Max(AppTheme.ComponentSize.DialogMinContentHeight, XamlRoot.Size.Height - AppTheme.ComponentSize.DialogVerticalInset),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        string? outputPath = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (formatBox.SelectedIndex != 0) return;
            var name = fileNameBox.Text.Trim();
            var directory = folderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || name.EndsWith('.') || !Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
            {
                validation.Message = "Enter a valid file name and an existing folder.";
                validation.IsOpen = true;
                args.Cancel = true;
                return;
            }
            outputPath = Path.Combine(directory, Path.ChangeExtension(name, codecBox.SelectedIndex is 2 or 3 ? ".mov" : ".mp4"));
            if (resBox.SelectedIndex == 5)
            {
                var customW = (int)Math.Round(customWidthField.Value);
                var customH = (int)Math.Round(customHeightField.Value);
                if (!ExportOutputSize.TryValidateCustom(
                        customW, customH, timeline.Width, timeline.Height,
                        allowUpscaleBox.IsChecked == true, requireEven: true, out var sizeError))
                {
                    validation.Message = sizeError ?? "Enter a valid custom size.";
                    validation.IsOpen = true;
                    args.Cancel = true;
                    return;
                }
            }
            else if (resBox.SelectedIndex is >= 1 and <= 4
                && !ExportOutputSize.TryResolve(
                    timeline.Width, timeline.Height,
                    new ExportPreset("Export", resolution: ResolutionAt(resBox.SelectedIndex)),
                    out var namedWidth, out var namedHeight, out var namedError))
            {
                validation.Message = namedError ?? $"{namedWidth}×{namedHeight} is not available for this timeline.";
                validation.IsOpen = true;
                args.Cancel = true;
                return;
            }
            if (fpsBox.SelectedIndex == customFpsIndex)
            {
                var customFps = Math.Round(customFpsField.Value);
                if (!ExportFrameRate.TryValidate(customFps, out var fpsError))
                {
                    validation.Message = fpsError ?? "Enter a valid frame rate.";
                    validation.IsOpen = true;
                    args.Cancel = true;
                    return;
                }
            }
            if (ToolExecutor.ExportQueue.IsDestinationReserved(outputPath))
            {
                validation.Message = "An export to that file is already queued. Choose another file name.";
                validation.IsOpen = true;
                args.Cancel = true;
            }
        };
        dialog.PrimaryButtonText = cta;
        dialog.CloseButtonText = "Close";
        dialog.DefaultButton = ContentDialogButton.Primary;
        void RefreshCancelButton()
        {
            var pending = ToolExecutor.ExportQueue.Jobs.Count(j => j.State.IsPending());
            dialog.PrimaryButtonText = pending > 0 ? "Add to queue"
                : ToolExecutor.ExportQueue.Jobs.Count > 0 ? "Export again" : "Export";
            dialog.IsPrimaryButtonEnabled = formatBox.SelectedIndex != 0 || timeline.TotalFrames > 0;
            dialog.SecondaryButtonText = ExportQueueChrome.CancelButtonLabel(pending);
            dialog.IsSecondaryButtonEnabled = ExportQueueChrome.CanCancel(pending);
        }
        RefreshCancelButton();
        formatBox.SelectionChanged += (_, _) => RefreshCancelButton();
        void OnQueueJobChanged(ExportJob _) => DispatcherQueue.TryEnqueue(() =>
        {
            RefreshQueueText();
            RefreshCancelButton();
        });
        ToolExecutor.ExportQueue.JobChanged += OnQueueJobChanged;

        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary)
            {
                var pendingIds = ToolExecutor.ExportQueue.Jobs
                    .Where(j => j.State.IsPending()).Select(j => j.Id).ToList();
                foreach (var id in ExportQueueChrome.JobsToCancel(pendingIds))
                    ToolExecutor.ExportQueue.Cancel(id);
                RefreshExportQueueChrome();
                return;
            }
            if (result != ContentDialogResult.Primary) return;

            if (formatBox.SelectedIndex == 1)
            {
                await ExportXmlAsync(session, timeline, fcpxml: true);
                return;
            }
            if (formatBox.SelectedIndex == 2)
            {
                await ExportXmlAsync(session, timeline, fcpxml: false);
                return;
            }
            if (formatBox.SelectedIndex == 3)
            {
                await ExportTemnoPackageAsync(session);
                return;
            }

            if (timeline.TotalFrames <= 0)
            {
                StatusText.Text = "Nothing to export — the timeline is empty.";
                return;
            }

            // WP-154: judge the media this timeline actually reaches before encoding anything. A
            // partly-offline timeline used to render the survivors, drop the rest and report success.
            // Runs before the save picker so nobody chooses a destination and is then told.
            var (allowPartialPicture, preflight) = await ConfirmOfflineMediaAsync(session, timeline);
            if (preflight is null)
                return;

            var codec = codecBox.SelectedIndex switch
            {
                1 => VideoCodec.H265,
                2 => VideoCodec.ProRes,
                3 => VideoCodec.HevcHdr,
                _ => VideoCodec.H264,
            };
            var resolution = ResolutionAt(resBox.SelectedIndex);
            if (outputPath is null) return;
            if (File.Exists(outputPath))
            {
                var replace = AppDialog.Create(XamlRoot);
                replace.Title = "Replace existing export?";
                replace.Content = new TextBlock { Text = outputPath, TextWrapping = TextWrapping.Wrap };
                replace.PrimaryButtonText = "Replace";
                replace.CloseButtonText = "Cancel";
                if (await replace.ShowAsync() != ContentDialogResult.Primary) return;
            }

            var preferHardware = hardwareBox.IsChecked == true;
            if (preferHardware != prefs.Export.PreferHardwareEncode)
            {
                prefs.Export.PreferHardwareEncode = preferHardware;
                UserPreferences.Save(prefs);
            }

            // WP-215 R1: the compositor and the renderer beneath it resolve from one seam, so
            // preview and export cannot end up on different implementations.
            var acceleration = preferHardware
                ? HardwareAccelerationMode.PreferHardware
                : HardwareAccelerationMode.Software;
            var exporter = new TimelineExporter(
                CompositorFactory.Create(),
                new ExportFrameSource(),
                session.Resolver,
                timelineLookup: SessionTimelineLookup.For(session),
                acceleration: acceleration,
                ownsFrameSource: true);
            exporter.AllowPartialPicture = allowPartialPicture;
            var preset = new ExportPreset("Export", codec, resolution);
            if (resBox.SelectedIndex == 5)
            {
                preset.CustomWidth = (int)Math.Round(customWidthField.Value);
                preset.CustomHeight = (int)Math.Round(customHeightField.Value);
            }
            if (fpsBox.SelectedIndex >= 1 && fpsBox.SelectedIndex <= ExportFrameRate.Presets.Count)
            {
                preset.CustomFps = ExportFrameRate.Presets[fpsBox.SelectedIndex - 1].Fps;
            }
            else if (fpsBox.SelectedIndex == customFpsIndex)
            {
                preset.CustomFps = Math.Round(customFpsField.Value);
            }
            var enqueued = ToolExecutor.ExportQueue.EnqueueVideo(exporter, timeline, preset, outputPath);
            // Held so the completion message can name what was actually dropped, rather than
            // reporting a bare "Exported" over a file with holes in it.
            _exportersByJob[enqueued.Id] = exporter;
            StatusText.Text = allowPartialPicture && preflight.HasProblems
                ? $"Export queued with missing media · {preflight.Sentence}"
                : $"Export queued · {enqueued.Id[..Math.Min(8, enqueued.Id.Length)]}…";
            RefreshExportQueueChrome();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Export failed: " + ex.Message;
        }
        finally
        {
            ToolExecutor.ExportQueue.JobChanged -= OnQueueJobChanged;
        }
    }

    private async Task ExportXmlAsync(ProjectSession session, Timeline timeline, bool fcpxml)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = _vm.ProjectName,
        };
        picker.FileTypeChoices.Add(fcpxml ? "Final Cut Pro XML" : "XMEML XML", new List<string> { ".xml" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindowHandle());
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        var lookup = SessionTimelineLookup.For(session);
        var report = fcpxml
            ? FcpXmlExporter.Export(timeline, session.Resolver, file.Path, resolveTimeline: lookup)
            : XmemlExporter.Export(timeline, session.Resolver, file.Path, resolveTimeline: lookup);
        // WP-236: name what the file does not carry rather than a bare "Exported".
        var exported = (fcpxml ? "Exported FCPXML → " : "Exported XMEML → ") + file.Name;
        StatusText.Text = report.OmissionSummary is { } left
            ? exported + " · not carried: " + left
            : exported;
    }

    private async Task ExportTemnoPackageAsync(ProjectSession session)
    {
        var folderPicker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        folderPicker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, MainWindowHandle());
        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder is null) return;

        var dest = AppSession.AllocateUniquePackagePath(folder.Path, _vm.ProjectName);
        var report = TemnoProjectExporter.Export(
            session.ProjectFile,
            session.Manifest,
            session.PackagePath,
            dest,
            generationLogBytes: null,
            resolver: session.Resolver);
        var missing = report.Missing.Count > 0 ? $" · {report.Missing.Count} missing media" : "";
        // WP-399: files outside the project that the user has not allowed stay out of the package.
        var notAllowed = report.NotAllowed.Count > 0 ? $" · {report.NotAllowed.Count} not allowed, left out" : "";
        StatusText.Text = "Exported package → " + Path.GetFileName(dest) + missing + notAllowed;
    }
}
