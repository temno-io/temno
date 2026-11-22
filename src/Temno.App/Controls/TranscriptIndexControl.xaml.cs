using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Transcription;
using Windows.Foundation;
using Windows.System;
using Windows.UI;

namespace Temno.App.Controls;

/// <summary>
/// Mac Index tab: Transcript | Markers. Cache-first word browser, playhead highlight, click-to-seek,
/// search, and Cut via the same <see cref="WordCutPlan"/> as <c>remove_words</c>.
/// </summary>
public sealed partial class TranscriptIndexControl : UserControl
{
    public event EventHandler<int>? SeekRequested;
    public event EventHandler? TimelineEdited;
    public event EventHandler? OpenCaptionsRequested;

    private readonly Dictionary<int, WordChip> _chips = [];
    private readonly Dictionary<int, FrameworkElement> _rowElements = [];
    private readonly SolidColorBrush _selectedFill;

    private TranscriptIndexViewModel? _vm;
    private bool _active;
    private bool _dirty = true;
    private bool _transcriptMode = true;
    private int _loadGeneration;
    private int _playheadFrame;
    private int? _anchorWord;
    private int? _extentWord;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _busyTimer;

    public TranscriptIndexControl()
    {
        var amber = AppTheme.Accent.Timecode;
        _selectedFill = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(255 * AppTheme.Opacity.Moderate), 0, 255),
            amber.R, amber.G, amber.B));

        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (_active)
                _ = ReloadAsync();
        };
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active)
            _ = ReloadAsync();
    }

    public void NotifySessionChanged()
    {
        _dirty = true;
        if (_active)
            _ = ReloadAsync();
    }

    public void SetPlayheadFrame(int frame)
    {
        if (_playheadFrame == frame)
            return;
        _playheadFrame = frame;
        if (_active && _vm is not null)
            ApplyWordChrome();
    }

    private async Task ReloadAsync()
    {
        var session = AppSession.Current.Session;
        if (session is null)
        {
            _vm = null;
            RenderAll(session: null);
            return;
        }

        if (!_dirty && _vm is not null)
        {
            RenderMarkers(session);
            ApplySurface();
            ApplyWordChrome();
            return;
        }

        var gen = ++_loadGeneration;
        ScheduleBusy();
        try
        {
            var availability = TranscriptIndexViewModel.Classify(session);
            TranscriptIndexViewModel? vm = null;
            if (availability == TranscriptIndexAvailability.Cached)
            {
                vm = await TranscriptIndexViewModel
                    .LoadCachedAsync(session)
                    .ConfigureAwait(true);
            }

            if (gen != _loadGeneration)
                return;

            _vm = vm;
            _dirty = false;
            if (_anchorWord is int a && vm is not null && vm.Words.All(w => w.Index != a))
            {
                _anchorWord = null;
                _extentWord = null;
            }

            RenderAll(session);
        }
        catch (Exception ex)
        {
            if (gen != _loadGeneration)
                return;
            _vm = null;
            _dirty = true;
            RenderAll(session);
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            if (gen == _loadGeneration)
                HideBusy();
        }
    }

    private void ScheduleBusy()
    {
        _busyTimer?.Stop();
        _busyTimer = DispatcherQueue.CreateTimer();
        _busyTimer.Interval = TimeSpan.FromMilliseconds(200);
        _busyTimer.IsRepeating = false;
        var gen = _loadGeneration;
        _busyTimer.Tick += (_, _) =>
        {
            if (gen == _loadGeneration)
                ShowBusy();
        };
        _busyTimer.Start();
    }

    private void ShowBusy()
    {
        BusyOverlay.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
    }

    private void HideBusy()
    {
        _busyTimer?.Stop();
        BusyOverlay.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = false;
    }

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag)
            return;
        _transcriptMode = tag != "Markers";
        TranscriptModeBtn.IsChecked = _transcriptMode;
        MarkersModeBtn.IsChecked = !_transcriptMode;
        ApplySurface();
        if (!_transcriptMode)
            RenderMarkers(AppSession.Current.Session);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vm is null)
            return;
        _vm.SetSearchQuery(SearchBox.Text);
        RenderTranscript();
        ApplySurface();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        if (_vm is null)
            return;
        var row = _vm.MoveToNextSearchResult();
        if (row is null)
            return;
        if (_rowElements.TryGetValue(row.FirstWordIndex, out var element))
            element.StartBringIntoView();
        SeekRequested?.Invoke(this, row.StartFrame);
    }

    private void EmptyAction_Click(object sender, RoutedEventArgs e)
        => OpenCaptionsRequested?.Invoke(this, EventArgs.Empty);

    private void Cut_Click(object sender, RoutedEventArgs e)
        => ApplyCut();

    private void ApplyCut()
    {
        var session = AppSession.Current.Session;
        if (session is null || _vm is null || _anchorWord is not int anchor || _extentWord is not int extent)
            return;

        try
        {
            var plan = _vm.CreateCutPlan(anchor, extent);
            var result = plan.ApplyTo(session);
            if (!result.Ok)
            {
                ErrorBar.Message = result.Error ?? "Cut failed.";
                ErrorBar.IsOpen = true;
                return;
            }

            ErrorBar.IsOpen = false;
            _anchorWord = null;
            _extentWord = null;
            _dirty = true;
            TimelineEdited?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private void RenderAll(ProjectSession? session)
    {
        RenderTranscript();
        RenderMarkers(session);
        ApplySurface();
        ApplyWordChrome();
        UpdateCutEnabled();
    }

    private void RenderTranscript()
    {
        TranscriptHost.Children.Clear();
        _chips.Clear();
        _rowElements.Clear();
        if (_vm is null || _vm.IsEmpty)
            return;

        foreach (var row in _vm.VisibleRows)
        {
            var stamp = new TextBlock
            {
                Text = FormatIndexTimecode(row.StartFrame, _vm.Fps),
                FontSize = AppTheme.FontSize.Xs,
                Foreground = AppTheme.Text.TertiaryBrush,
                Margin = new Thickness(AppTheme.Spacing.None, AppTheme.Spacing.Xxs, AppTheme.Spacing.SmMd, AppTheme.Spacing.None),
            };
            AutomationProperties.SetName(stamp, "Sentence time " + stamp.Text);

            var words = new WordWrapPanel();
            foreach (var word in row.Words)
            {
                var chip = new WordChip(word, OnWordClick);
                _chips[word.Index] = chip;
                words.Children.Add(chip.Root);
            }

            var body = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
            if (!string.IsNullOrWhiteSpace(row.Speaker))
            {
                body.Children.Add(new TextBlock
                {
                    Text = row.Speaker,
                    FontSize = AppTheme.FontSize.Xs,
                    Foreground = AppTheme.Text.TertiaryBrush,
                });
            }
            body.Children.Add(words);

            var rowGrid = new Grid { ColumnSpacing = AppTheme.Spacing.Sm };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(stamp, 0);
            Grid.SetColumn(body, 1);
            rowGrid.Children.Add(stamp);
            rowGrid.Children.Add(body);
            AutomationProperties.SetName(rowGrid, row.Text);

            TranscriptHost.Children.Add(rowGrid);
            _rowElements[row.FirstWordIndex] = rowGrid;
        }
    }

    private void RenderMarkers(ProjectSession? session)
    {
        MarkersHost.Children.Clear();
        var markers = session?.ActiveTimeline.Markers
            .OrderBy(m => m.Frame)
            .ToList() ?? [];
        var fps = session?.ActiveTimeline.Fps ?? 30;
        foreach (var marker in markers)
        {
            MarkersHost.Children.Add(BuildMarkerRow(marker, fps));
        }
    }

    private FrameworkElement BuildMarkerRow(Marker marker, int fps)
    {
        // WP-205 (Premiere pass): the swatch is the same diamond as the ruler head so the
        // colour reads identically in both places.
        const double half = 6;
        var swatch = new Polygon
        {
            Points = new PointCollection
            {
                new(half, 0), new(half * 2, half), new(half, half * 2), new(0, half),
            },
            Fill = MarkerPalette.BrushFor(marker.Color),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var name = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(marker.Name) ? "Marker" : marker.Name,
            FontSize = AppTheme.FontSize.MdLg,
            Foreground = AppTheme.Text.PrimaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var time = new TextBlock
        {
            Text = FormatIndexTimecode(marker.Frame, fps),
            FontSize = AppTheme.FontSize.Xs,
            Foreground = AppTheme.Text.TertiaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var edit = new Button
        {
            Content = "Edit",
            FontSize = AppTheme.FontSize.Xs,
            Padding = new Thickness(AppTheme.Spacing.Xs, AppTheme.Spacing.Xxs, AppTheme.Spacing.Xs, AppTheme.Spacing.Xxs),
            Background = null,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(edit, "Edit " + name.Text);
        ToolTipService.SetToolTip(edit, "Edit marker name, color and comments");
        Motion.AttachHoverFill(edit);
        edit.Click += (_, _) => _ = EditMarkerAsync(marker.Id);

        var delete = new Button
        {
            Content = Temno.App.Controls.Shared.FluentPath.At(Temno.App.Controls.Shared.Icons.Delete,
                AppTheme.IconSize.Xs,
                AppTheme.Text.TertiaryBrush),
            Padding = new Thickness(AppTheme.Spacing.Xs),
            Background = null,
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(delete, "Delete " + name.Text);
        ToolTipService.SetToolTip(delete, "Delete marker");
        Motion.AttachHoverFill(delete);
        delete.Click += (_, _) => DeleteMarker(marker.Id);

        var labels = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
        labels.Children.Add(name);
        labels.Children.Add(time);
        if (!string.IsNullOrWhiteSpace(marker.Note))
        {
            labels.Children.Add(new TextBlock
            {
                Text = marker.Note,
                FontSize = AppTheme.FontSize.Xs,
                Foreground = AppTheme.Text.TertiaryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = AppTheme.Spacing.Xxs,
            VerticalAlignment = VerticalAlignment.Center,
        };
        actions.Children.Add(edit);
        actions.Children.Add(delete);

        var row = new Grid { ColumnSpacing = AppTheme.Spacing.SmMd };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(swatch, 0);
        Grid.SetColumn(labels, 1);
        Grid.SetColumn(actions, 2);
        row.Children.Add(swatch);
        row.Children.Add(labels);
        row.Children.Add(actions);

        var border = new Border
        {
            Child = row,
            Background = AppTheme.Background.RaisedBrush,
            BorderBrush = AppTheme.Border.SubtleBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(AppTheme.Radius.Sm),
            Padding = new Thickness(AppTheme.Spacing.SmMd, AppTheme.Spacing.Sm, AppTheme.Spacing.SmMd, AppTheme.Spacing.Sm),
        };
        AutomationProperties.SetName(border, name.Text + " at " + time.Text);
        ToolTipService.SetToolTip(border, "Seek to this marker. Double-click to edit.");
        border.PointerPressed += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject origin
                && FindAncestor<Button>(origin) is not null)
                return;
            SeekRequested?.Invoke(this, marker.Frame);
        };
        // WP-205: double-click the row (name or anywhere but the action buttons) edits.
        border.DoubleTapped += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject origin
                && FindAncestor<Button>(origin) is not null)
                return;
            _ = EditMarkerAsync(marker.Id);
            e.Handled = true;
        };
        return border;
    }

    private void DeleteMarker(string markerId)
    {
        var session = AppSession.Current.Session;
        if (session is null)
            return;
        var result = EditCommands.RemoveMarker(session, markerId);
        if (!result.Ok)
        {
            ErrorBar.Message = result.Error ?? "Could not delete marker.";
            ErrorBar.IsOpen = true;
            return;
        }

        TimelineEdited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// WP-205 (Premiere pass): edit name, colour and comments through the shared Edit Marker
    /// dialog. Cancel (or an empty name) changes nothing; a committed edit is one undo step and
    /// refreshes through <see cref="TimelineEdited"/> so the ruler glyph follows the row.
    /// </summary>
    private async Task EditMarkerAsync(string markerId)
    {
        var session = AppSession.Current.Session;
        if (session is null)
            return;
        var marker = session.ActiveTimeline.Markers.FirstOrDefault(m => m.Id == markerId);
        if (marker is null)
            return;

        var edit = await MarkerPalette.ShowEditDialogAsync(
            XamlRoot, marker.Name, marker.Color, marker.Note, marker.Frame, session.ActiveTimeline.Fps);
        if (edit is null)
            return;

        var result = EditCommands.UpdateMarker(session, markerId, frame: null, edit.Name, edit.ColorHex, edit.Note);
        if (!result.Ok)
        {
            ErrorBar.Message = result.Error ?? "Could not edit marker.";
            ErrorBar.IsOpen = true;
            return;
        }

        TimelineEdited?.Invoke(this, EventArgs.Empty);
    }

    private void ApplySurface()
    {
        var searching = _transcriptMode
            && !string.IsNullOrWhiteSpace(SearchBox.Text)
            && _vm is not null
            && !_vm.IsEmpty;
        var hasTranscript = _vm is not null && !_vm.IsEmpty && _vm.VisibleRows.Count > 0;
        var session = AppSession.Current.Session;
        var hasMarkers = session is not null && session.ActiveTimeline.Markers.Count > 0;
        var availability = session is null
            ? TranscriptIndexAvailability.NoSpeechMedia
            : TranscriptIndexViewModel.Classify(session);

        SearchBox.Visibility = _transcriptMode ? Visibility.Visible : Visibility.Collapsed;
        CutBtn.Visibility = _transcriptMode ? Visibility.Visible : Visibility.Collapsed;

        if (_transcriptMode)
        {
            TranscriptScroll.Visibility = hasTranscript ? Visibility.Visible : Visibility.Collapsed;
            MarkersScroll.Visibility = Visibility.Collapsed;
            if (hasTranscript)
            {
                EmptyState.Visibility = Visibility.Collapsed;
                return;
            }

            EmptyState.Visibility = Visibility.Visible;
            if (searching)
            {
                EmptyTitle.Text = "No matching sentences";
                EmptyBody.Text = "Try a different search.";
                EmptyActionBtn.Visibility = Visibility.Collapsed;
            }
            else if (availability == TranscriptIndexAvailability.NoSpeechMedia)
            {
                EmptyTitle.Text = "No speech on the timeline";
                EmptyBody.Text =
                    "Add video or audio with a soundtrack, then generate a transcript from Captions.";
                EmptyActionBtn.Visibility = Visibility.Visible;
            }
            else if (availability == TranscriptIndexAvailability.Cached)
            {
                EmptyTitle.Text = "No spoken words in the current cuts";
                EmptyBody.Text =
                    "A transcript is cached, but none of it maps onto the clips on the timeline.";
                EmptyActionBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                EmptyTitle.Text = "No transcript yet";
                EmptyBody.Text =
                    "Generate one from the Captions tab. Index reads that transcript; it does not transcribe on its own.";
                EmptyActionBtn.Visibility = Visibility.Visible;
            }
            return;
        }

        TranscriptScroll.Visibility = Visibility.Collapsed;
        MarkersScroll.Visibility = hasMarkers ? Visibility.Visible : Visibility.Collapsed;
        if (hasMarkers)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyState.Visibility = Visibility.Visible;
        EmptyTitle.Text = "No markers on this timeline";
        EmptyBody.Text = "Markers appear here when you add them or run Mark Silence.";
        EmptyActionBtn.Visibility = Visibility.Collapsed;
    }

    private void OnWordClick(TimelineWord word, bool extend)
    {
        if (extend && _anchorWord is int anchor)
            _extentWord = word.Index;
        else
        {
            _anchorWord = word.Index;
            _extentWord = word.Index;
        }

        ApplyWordChrome();
        UpdateCutEnabled();
        if (word.StartFrame >= 0)
            SeekRequested?.Invoke(this, word.StartFrame);
    }

    private void ApplyWordChrome()
    {
        var active = _vm?.ActiveWordAtFrame(_playheadFrame)?.Index;
        int? selFirst = null;
        int? selLast = null;
        if (_anchorWord is int a && _extentWord is int b)
        {
            selFirst = Math.Min(a, b);
            selLast = Math.Max(a, b);
        }

        foreach (var (index, chip) in _chips)
        {
            var selected = selFirst is int first && selLast is int last
                && index >= first && index <= last;
            chip.Apply(index == active, selected, _selectedFill);
        }
    }

    private void UpdateCutEnabled()
        => CutBtn.IsEnabled = _transcriptMode && _anchorWord is not null && _extentWord is not null && _vm is not null;

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var d = start; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is T match)
                return match;
        }

        return null;
    }

    private static string FormatIndexTimecode(int frame, int fps)
    {
        if (fps <= 0)
            fps = 30;
        frame = Math.Max(0, frame);
        var ff = frame % fps;
        var totalSeconds = frame / fps;
        var ss = totalSeconds % 60;
        var mm = (totalSeconds / 60) % 60;
        var hh = totalSeconds / 3600;
        return $"{hh:D2}:{mm:D2}:{ss:D2}:{ff:D2}";
    }

    private static Brush MarkerBrush(string? color) => MarkerPalette.BrushFor(color);

    private sealed class WordChip
    {
        public Button Root { get; }
        private readonly TextBlock _label;

        public WordChip(TimelineWord word, Action<TimelineWord, bool> onClick)
        {
            _label = new TextBlock
            {
                Text = word.Text,
                FontSize = AppTheme.FontSize.MdLg,
                Foreground = AppTheme.Text.PrimaryBrush,
            };
            Root = new Button
            {
                Content = _label,
                Padding = new Thickness(AppTheme.Spacing.Xs, AppTheme.Spacing.Xxs, AppTheme.Spacing.Xs, AppTheme.Spacing.Xxs),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(AppTheme.Radius.XsSm),
                AllowFocusOnInteraction = false,
            };
            AutomationProperties.SetName(Root, word.Text);
            ToolTipService.SetToolTip(Root, "Seek to this word. Shift-click to extend the cut range.");
            Root.Click += (_, _) =>
            {
                var shift = Microsoft.UI.Input.InputKeyboardSource
                    .GetKeyStateForCurrentThread(VirtualKey.Shift)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                onClick(word, shift);
            };
        }

        public void Apply(bool active, bool selected, Brush selectedFill)
        {
            _label.Foreground = active ? AppTheme.Accent.TimecodeBrush : AppTheme.Text.PrimaryBrush;
            _label.FontWeight = active
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal;
            Root.Background = selected ? selectedFill : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }

    private sealed class WordWrapPanel : Panel
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            var hGap = AppTheme.Spacing.Xs;
            var vGap = AppTheme.Spacing.Xxs;
            var maxW = double.IsInfinity(availableSize.Width) ? 10_000 : Math.Max(0, availableSize.Width);
            double x = 0, y = 0, rowH = 0, width = 0;
            foreach (UIElement child in Children)
            {
                child.Measure(new Size(maxW, availableSize.Height));
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > maxW)
                {
                    y += rowH + vGap;
                    x = 0;
                    rowH = 0;
                }

                rowH = Math.Max(rowH, size.Height);
                x += size.Width + hGap;
                width = Math.Max(width, Math.Max(0, x - hGap));
            }

            return new Size(width, y + rowH);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var hGap = AppTheme.Spacing.Xs;
            var vGap = AppTheme.Spacing.Xxs;
            var maxW = Math.Max(0, finalSize.Width);
            double x = 0, y = 0, rowH = 0;
            foreach (UIElement child in Children)
            {
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > maxW)
                {
                    y += rowH + vGap;
                    x = 0;
                    rowH = 0;
                }

                child.Arrange(new Rect(x, y, size.Width, size.Height));
                rowH = Math.Max(rowH, size.Height);
                x += size.Width + hGap;
            }

            return finalSize;
        }
    }
}
