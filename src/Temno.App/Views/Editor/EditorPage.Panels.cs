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
    private void ToggleAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_maximized) RestoreLayoutFromMaximize();
        _vm.AgentVisible = ToggleAgent.IsChecked == true;

        ApplyPanelVisibility();
        UpdateAgentBubbleChrome();
        ScheduleLayoutPersist();
    }

    private void UpdateAgentBubbleChrome()
    {
        // Mac TitleBarLeadingView: filled bubble + stronger when open.
        if (ToggleAgentIcon is null) return;
        ToggleAgentIcon.Opacity = 1;
        ToggleAgentIcon.Fill = _vm.AgentVisible
            ? AppTheme.Accent.ReadableBrush
            : AppTheme.Text.SecondaryBrush;
    }

    private void ToggleMedia_Click(object sender, RoutedEventArgs e)
    {
        if (_maximized) RestoreLayoutFromMaximize();
        _vm.MediaVisible = ToggleMedia.IsChecked == true;
        if (_vm.MediaVisible && MediaCol.Width.Value < 10)
            MediaCol.Width = new GridLength(_mediaWidth > 0 ? _mediaWidth : EditorLayoutPreferences.DefaultMediaWidth);
        ApplyPanelVisibility();
        ScheduleLayoutPersist();
    }

    private void ToggleInspector_Click(object sender, RoutedEventArgs e)
    {
        if (_maximized) RestoreLayoutFromMaximize();
        _vm.InspectorVisible = ToggleInspector.IsChecked == true;
        if (_vm.InspectorVisible) _vm.AgentVisible = false;
        if (_vm.InspectorVisible && InspectorCol.Width.Value < 10)
            InspectorCol.Width = new GridLength(_inspectorWidth > 0 ? _inspectorWidth : EditorLayoutPreferences.DefaultInspectorWidth);
        ApplyPanelVisibility();
        ScheduleLayoutPersist();
    }

    private void CaptureLiveDividerSizes()
    {
        if (_maximized) return;
        // WP-185: while the window is forcing the side columns narrower than the user asked for,
        // the live widths are not preferences and must not be captured as such.
        if (!_responsiveFitActive)
        {
            if (MediaCol.Width.IsAbsolute && MediaCol.Width.Value >= EditorLayoutPreferences.CollapsedEpsilon)
                _mediaWidth = MediaCol.Width.Value;
            if (InspectorCol.Width.IsAbsolute && InspectorCol.Width.Value >= EditorLayoutPreferences.CollapsedEpsilon)
            {
                if (_rightDockShowsAgent) _agentWidth = InspectorCol.Width.Value;
                else _inspectorWidth = InspectorCol.Width.Value;
            }
        }
        if (TimelineRow.Height.IsAbsolute && TimelineRow.Height.Value >= EditorLayoutPreferences.MinTimelineHeight * 0.5)
            _timelineHeight = TimelineRow.Height.Value;
    }

    private void ApplyPanelVisibility()
    {
        CaptureLiveDividerSizes();
        RefreshMenuCheckmarks();

        AgentPanel.Visibility = _vm.AgentVisible ? Visibility.Visible : Visibility.Collapsed;
        AgentSplitter.Visibility = _vm.AgentVisible ? Visibility.Visible : Visibility.Collapsed;
        MediaPanel.Visibility = _vm.MediaVisible ? Visibility.Visible : Visibility.Collapsed;
        MediaSplitter.Visibility = _vm.MediaVisible ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanel.Visibility = !_vm.AgentVisible && _vm.InspectorVisible ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitter.Visibility = _vm.InspectorVisible ? Visibility.Visible : Visibility.Collapsed;

        // Preview visibility depends on maximize / vertical topology; default visible when not maximized.
        if (!_maximized)
        {
            PreviewPanel.Visibility = Visibility.Visible;
            TimelinePanel.Visibility = Visibility.Visible;
            TimelineSplitter.Visibility = Visibility.Visible;
            UpperGrid.Visibility = Visibility.Visible;
            MainStack.Visibility = Visibility.Visible;
            WorkspaceGrid.Visibility = Visibility.Visible;
        }



        if (!_vm.MediaVisible)
        {
            MediaCol.Width = new GridLength(0);
            MediaSplitterCol.Width = new GridLength(0);
        }
        else
        {
            if (!MediaCol.Width.IsAbsolute || MediaCol.Width.Value < 10)
                MediaCol.Width = new GridLength(_mediaWidth);
            MediaSplitterCol.Width = new GridLength(5);
        }

        ApplyRightDock();
        FitVerticalPanels();

        if (!_maximized && (!_verticalTopology || !_previewOnSide))
        {
            if (!PreviewCol.Width.IsStar)
                PreviewCol.Width = new GridLength(1, GridUnitType.Star);
        }

        if (!_maximized && TimelineRow.Height.IsAbsolute && TimelineRow.Height.Value < 40)
            TimelineRow.Height = new GridLength(_timelineHeight > 40 ? _timelineHeight : EditorLayoutPreferences.DefaultTimelineHeight);

        ToggleAgent.IsChecked = _vm.AgentVisible;
        ToggleMedia.IsChecked = _vm.MediaVisible;
        ToggleInspector.IsChecked = _vm.InspectorVisible;
        UpdateAgentBubbleChrome();
        UpdateFocusRings();
    }
    private void MenuToggleAgent_Click(object sender, RoutedEventArgs e)
    {
        if (_maximized) RestoreLayoutFromMaximize();
        ShowPanel(ShellPanel.Agent, forceVisible: false);
    }

    private void MenuToggleMedia_Click(object sender, RoutedEventArgs e)
    {
        if (_maximized) RestoreLayoutFromMaximize();
        ShowPanel(ShellPanel.Media, forceVisible: false);
    }

    private void MenuToggleInspector_Click(object sender, RoutedEventArgs e)
    {
        if (_maximized) RestoreLayoutFromMaximize();
        ShowPanel(ShellPanel.Inspector, forceVisible: false);
    }

    /// <summary>Recovery from a layout that hid chrome. Not a toggle.</summary>
    private void MenuShowHiddenPanels_Click(object sender, RoutedEventArgs e)
    {
        if (_maximized) RestoreLayoutFromMaximize();
        ShowPanel(ShellPanel.Media, forceVisible: true);
        ShowPanel(ShellPanel.Inspector, forceVisible: true);
        ShowPanel(ShellPanel.Agent, forceVisible: true);
        StatusText.Text = "Media, Inspector, and Tools shown";
    }

    private void MenuShowMedia_Click(object sender, RoutedEventArgs e)
        => ShowPanel(ShellPanel.Media, forceVisible: true);

    private void MenuShowInspector_Click(object sender, RoutedEventArgs e)
        => ShowPanel(ShellPanel.Inspector, forceVisible: true);

    private void MenuShowAgent_Click(object sender, RoutedEventArgs e)
        => ShowPanel(ShellPanel.Agent, forceVisible: true);

    private void ShowPanel(ShellPanel panel, bool forceVisible)
    {
        switch (panel)
        {
            case ShellPanel.Media:
                _vm.MediaVisible = forceVisible || !_vm.MediaVisible;
                if (_vm.MediaVisible && MediaCol.Width.Value < 10)
                    MediaCol.Width = new GridLength(_mediaWidth > 10 ? _mediaWidth : EditorLayoutPreferences.DefaultMediaWidth);
                break;
            case ShellPanel.Inspector:
                _vm.InspectorVisible = forceVisible || !_vm.InspectorVisible;
                if (_vm.InspectorVisible && InspectorCol.Width.Value < 10)
                    InspectorCol.Width = new GridLength(_inspectorWidth > 10 ? _inspectorWidth : 300);
                break;
            case ShellPanel.Agent:
                _vm.AgentVisible = forceVisible || !_vm.AgentVisible;

                break;
        }
        EnsurePreviewInUpperGrid();
        ApplyPanelVisibility();
        ScheduleLayoutPersist();
        StatusText.Text = panel switch
        {
            ShellPanel.Media => _vm.MediaVisible ? "Media panel shown" : "Media panel hidden",
            ShellPanel.Inspector => _vm.InspectorVisible ? "Inspector shown" : "Inspector hidden",
            ShellPanel.Agent => _vm.AgentVisible ? "Agent panel shown" : "Agent panel hidden",
            _ => StatusText.Text,
        };
    }
    // --- WP-71 focus + maximize ---
    private void Panel_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<ShellPanel>(tag, out var panel)) return;
        SetFocusedPanel(panel);
        if (panel is not (ShellPanel.Timeline or ShellPanel.Preview)) return;
        if (FindAncestor<GenerationComposer>(e.OriginalSource) is not null) return;
        // Interactive descendants need to retain pointer capture/focus through release so their
        // Click/Flyout logic can run. Only a click on non-interactive panel chrome should restore
        // the editor's keyboard focus.
        var interactive = FindAncestor<ButtonBase>(e.OriginalSource)
            ?? (DependencyObject?)FindAncestor<ScrollBar>(e.OriginalSource);
        if (!e.Handled && interactive is null)
            RestoreEditorKeyboardFocus();
    }

    private void SetFocusedPanel(ShellPanel panel)
    {
        _focusedPanel = panel;
        UpdateFocusRings();
        StatusText.Text = $"Focus · {panel}" + (_maximized ? " (maximized)" : "");
    }

    private void UpdateFocusRings()
    {
        // Mac: thin warm stroke (~1.5 @ 0.6) — avoid thick white frames.
        void Style(Border border, ShellPanel panel)
        {
            var focused = panel == _focusedPanel;
            border.BorderBrush = focused
                ? AppTheme.Accent.TimecodeBrush
                : AppTheme.Border.SubtleBrush;
            border.BorderThickness = new Thickness(1);
            border.Opacity = focused ? 1.0 : 1.0;
        }

        Style(AgentPanel, ShellPanel.Agent);
        Style(MediaPanel, ShellPanel.Media);
        Style(PreviewPanel, ShellPanel.Preview);
        Style(InspectorPanel, ShellPanel.Inspector);
        Style(TimelinePanel, ShellPanel.Timeline);
    }

    private void ToggleMaximizeFocused()
    {
        if (_maximized)
        {
            RestoreLayoutFromMaximize();
            return;
        }

        CaptureLiveDividerSizes();
        _savedAgentVisible = _vm.AgentVisible;
        _savedMediaVisible = _vm.MediaVisible;
        _savedInspectorVisible = _vm.InspectorVisible;
        _savedAgentWidth = _agentWidth;
        _savedMediaWidth = _mediaWidth;
        _savedInspectorWidth = _inspectorWidth;
        _savedTimelineHeight = _timelineHeight > 40 ? _timelineHeight : EditorLayoutPreferences.DefaultTimelineHeight;
        _savedVerticalTopology = _verticalTopology;

        _maximized = true;
        // Collapse everything first, then expand the focused leaf to fill the shell.
        SetAgentShell(false);
        SetUpperPanels(media: false, preview: false, inspector: false);
        SetTimelineShell(false);
        WorkspaceGrid.Visibility = Visibility.Visible;
        MainStack.Visibility = Visibility.Visible;

        switch (_focusedPanel)
        {
            case ShellPanel.Agent:
                EnsurePreviewInUpperGrid();
                UpperGrid.Visibility = Visibility.Visible;
                UpperRow.MinHeight = 0;
                SetUpperPanels(media: false, preview: false, inspector: false);
                MediaCol.Width = new GridLength(0);
                PreviewCol.Width = new GridLength(0);
                UpperRow.Height = new GridLength(1, GridUnitType.Star);
                SetAgentShell(true, star: true);
                break;
            case ShellPanel.Media:
                SetAgentShell(false);
                SetTimelineShell(false);
                EnsurePreviewInUpperGrid();
                UpperGrid.Visibility = Visibility.Visible;
                UpperRow.MinHeight = 0;
                SetUpperPanels(media: true, preview: false, inspector: false);
                MediaCol.Width = new GridLength(1, GridUnitType.Star);
                PreviewCol.Width = new GridLength(0);
                InspectorCol.Width = new GridLength(0);
                UpperRow.Height = new GridLength(1, GridUnitType.Star);
                break;
            case ShellPanel.Inspector:
                SetAgentShell(false);
                SetTimelineShell(false);
                EnsurePreviewInUpperGrid();
                UpperGrid.Visibility = Visibility.Visible;
                UpperRow.MinHeight = 0;
                SetUpperPanels(media: false, preview: false, inspector: true);
                MediaCol.Width = new GridLength(0);
                PreviewCol.Width = new GridLength(0);
                InspectorCol.Width = new GridLength(1, GridUnitType.Star);
                UpperRow.Height = new GridLength(1, GridUnitType.Star);
                break;
            case ShellPanel.Preview:
                SetAgentShell(false);
                SetTimelineShell(false);
                EnsurePreviewInUpperGrid();
                UpperGrid.Visibility = Visibility.Visible;
                UpperRow.MinHeight = 0;
                SetUpperPanels(media: false, preview: true, inspector: false);
                MediaCol.Width = new GridLength(0);
                PreviewCol.Width = new GridLength(1, GridUnitType.Star);
                InspectorCol.Width = new GridLength(0);
                UpperRow.Height = new GridLength(1, GridUnitType.Star);
                break;
            case ShellPanel.Timeline:
                SetAgentShell(false);
                SetUpperPanels(media: false, preview: false, inspector: false);
                UpperGrid.Visibility = Visibility.Collapsed;
                UpperRow.MinHeight = 0;
                UpperRow.Height = new GridLength(0);
                TimelineSplitterRow.Height = new GridLength(0);
                TimelineSplitter.Visibility = Visibility.Collapsed;
                TimelinePanel.Visibility = Visibility.Visible;
                TimelineRow.MinHeight = 0;
                TimelineRow.MaxHeight = double.PositiveInfinity;
                TimelineRow.Height = new GridLength(1, GridUnitType.Star);
                break;
        }

        ToggleAgent.IsChecked = _vm.AgentVisible;
        ToggleMedia.IsChecked = _vm.MediaVisible;
        ToggleInspector.IsChecked = _vm.InspectorVisible;
        UpdateFocusRings();
        StatusText.Text = $"Maximized · {_focusedPanel} (Esc or ` to restore)";
        if (_focusedPanel == ShellPanel.Timeline)
            ScheduleTimelineLayoutRefresh();
    }

    private void ApplyRightDock(bool star = false)
    {
        _rightDockShowsAgent = _vm.AgentVisible;
        var visible = _vm.AgentVisible || _vm.InspectorVisible;
        AgentPanel.Visibility = _vm.AgentVisible ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanel.Visibility = !_vm.AgentVisible && _vm.InspectorVisible ? Visibility.Visible : Visibility.Collapsed;
        AgentSplitter.Visibility = _vm.AgentVisible && !star ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitter.Visibility = !_vm.AgentVisible && _vm.InspectorVisible && !star ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitterCol.Width = new GridLength(visible && !star ? 5 : 0);
        InspectorCol.Width = !visible ? new GridLength(0) : star ? new GridLength(1, GridUnitType.Star)
            : new GridLength(_vm.AgentVisible ? _agentWidth : _inspectorWidth);
    }

    private void SetAgentShell(bool visible, bool star = false)
    {
        _vm.AgentVisible = visible;
        ApplyRightDock(star);
    }

    private void SetUpperPanels(bool media, bool preview, bool inspector)
    {
        _vm.MediaVisible = media;
        _vm.InspectorVisible = inspector;
        MediaPanel.Visibility = media ? Visibility.Visible : Visibility.Collapsed;
        MediaSplitter.Visibility = media && preview ? Visibility.Visible : Visibility.Collapsed;
        MediaSplitterCol.Width = media && preview ? new GridLength(5) : new GridLength(0);
        PreviewPanel.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        InspectorPanel.Visibility = inspector ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitter.Visibility = inspector && preview ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitterCol.Width = inspector && preview ? new GridLength(5) : new GridLength(0);
        if (!media) MediaCol.Width = new GridLength(0);
        if (!inspector) InspectorCol.Width = new GridLength(0);
        if (!preview && !_previewOnSide) PreviewCol.Width = new GridLength(0);
    }

    private void SetTimelineShell(bool visible)
    {
        TimelinePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TimelineSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        TimelineSplitterRow.Height = visible ? new GridLength(5) : new GridLength(0);
        if (!visible)
        {
            TimelineRow.Height = new GridLength(0);
            TimelineRow.MinHeight = 0;
        }
        else
        {
            TimelineRow.MinHeight = EditorLayoutPreferences.MinTimelineHeight;
            TimelineRow.MaxHeight = Theme.Layout.TimelineMaxHeight;
            TimelineRow.Height = new GridLength(_timelineHeight > 40 ? _timelineHeight : EditorLayoutPreferences.DefaultTimelineHeight);
            UpperRow.MinHeight = 80;
            UpperRow.Height = new GridLength(1, GridUnitType.Star);
        }
    }

    private void RestoreLayoutFromMaximize()
    {
        if (!_maximized) return;
        _maximized = false;

        _agentWidth = _savedAgentWidth > 10 ? _savedAgentWidth : EditorLayoutPreferences.DefaultAgentWidth;
        _mediaWidth = _savedMediaWidth > 10 ? _savedMediaWidth : EditorLayoutPreferences.DefaultMediaWidth;
        _inspectorWidth = _savedInspectorWidth > 10 ? _savedInspectorWidth : 300;
        _timelineHeight = _savedTimelineHeight > 40 ? _savedTimelineHeight : EditorLayoutPreferences.DefaultTimelineHeight;

        // Prefer pre-maximize visibility; if all side panels were off, re-open media+inspector
        // so the user is never stuck with only a black preview after Esc/` .
        _vm.AgentVisible = _savedAgentVisible;
        _vm.MediaVisible = _savedMediaVisible;
        _vm.InspectorVisible = _savedInspectorVisible;
        if (!_vm.MediaVisible && !_vm.InspectorVisible && !_vm.AgentVisible)
        {
            _vm.MediaVisible = true;
            _vm.InspectorVisible = true;
        }

        UpperGrid.Visibility = Visibility.Visible;
        WorkspaceGrid.Visibility = Visibility.Visible;
        MainStack.Visibility = Visibility.Visible;
        TimelinePanel.Visibility = Visibility.Visible;
        TimelineSplitter.Visibility = Visibility.Visible;
        TimelineRow.MinHeight = EditorLayoutPreferences.MinTimelineHeight;
        TimelineRow.MaxHeight = Theme.Layout.TimelineMaxHeight;
        UpperRow.MinHeight = 80;
        UpperRow.Height = new GridLength(1, GridUnitType.Star);
        TimelineSplitterRow.Height = new GridLength(5);

        // Re-apply Default or Vertical topology from before maximize.
        EnsurePreviewInUpperGrid();
        ApplyPanelVisibility();

        MediaCol.Width = _vm.MediaVisible ? new GridLength(_mediaWidth) : new GridLength(0);
        InspectorCol.Width = _vm.InspectorVisible ? new GridLength(_inspectorWidth) : new GridLength(0);
        PreviewCol.Width = new GridLength(1, GridUnitType.Star);
        TimelineRow.Height = new GridLength(_timelineHeight);
        ApplyVerticalTopology(_savedVerticalTopology);
        ApplyRightDock();
        StatusText.Text = "Layout restored";
        ScheduleTimelineLayoutRefresh();
    }

    private void ApplyLayoutPreset(EditorLayoutPreset preset)
    {
        if (_maximized) RestoreLayoutFromMaximize();
        var seed = EditorLayoutPreferences.ForPreset(preset);
        _activePreset = preset;
        _timelineHeightAuto = preset == EditorLayoutPreset.Default;
        _agentWidth = seed.AgentWidth;
        _mediaWidth = seed.MediaWidth;
        _inspectorWidth = seed.InspectorWidth;
        _timelineHeight = seed.TimelineHeight;
        _vm.AgentVisible = seed.AgentVisible;
        _vm.MediaVisible = seed.MediaVisible;
        _vm.InspectorVisible = seed.InspectorVisible;

        ApplyVerticalTopology(preset == EditorLayoutPreset.Vertical);

        MediaCol.Width = seed.MediaVisible ? new GridLength(seed.MediaWidth) : new GridLength(0);
        InspectorCol.Width = seed.InspectorVisible ? new GridLength(seed.InspectorWidth) : new GridLength(0);
        TimelineRow.Height = new GridLength(seed.TimelineHeight);
        ApplyPanelVisibility();
        PersistLayoutNow();
        StatusText.Text = $"Layout · {preset}";
        if (_timelineHeightAuto)
            DispatcherQueue.TryEnqueue(() => ApplyResponsiveShell(RootGrid.ActualWidth, RootGrid.ActualHeight));
    }

    private void ApplyVerticalTopology(bool vertical)
    {
        _verticalTopology = vertical;
        if (vertical)
            EnsurePreviewOnSide();
        else
            EnsurePreviewInUpperGrid();
    }

    private void FitVerticalPanels()
    {
        if (!_verticalTopology || !_previewOnSide || _maximized) return;
        var dockVisible = _vm.AgentVisible || _vm.InspectorVisible;
        var dockWidth = dockVisible
            ? Math.Max(Theme.Layout.InspectorDefault, _vm.AgentVisible ? _agentWidth : _inspectorWidth)
            : 0;
        MainStackCol.MinWidth = (_vm.MediaVisible ? MinMediaColumnWidth : 0)
            + dockWidth + (_vm.MediaVisible && dockVisible ? Theme.Layout.PanelGap : 0);
        // The side preview owns its own column; the media panel fills the space it vacated.
        MediaCol.Width = new GridLength(_vm.MediaVisible ? 1 : 0,
            _vm.MediaVisible ? GridUnitType.Star : GridUnitType.Pixel);
        MediaSplitterCol.Width = new GridLength(0);
        MediaSplitter.Visibility = Visibility.Collapsed;
        InspectorCol.Width = new GridLength(dockWidth);
    }

    private void EnsurePreviewOnSide()
    {
        if (_previewOnSide && PreviewPanel.Parent == WorkspaceGrid) return;
        if (PreviewPanel.Parent is Panel parent)
            parent.Children.Remove(PreviewPanel);
        WorkspaceGrid.Children.Add(PreviewPanel);
        Grid.SetColumn(PreviewPanel, 2);
        Grid.SetRow(PreviewPanel, 0);
        Grid.SetRowSpan(PreviewPanel, 1);
        PreviewPanel.Margin = AppTheme.Padding.LeftXxs;
        PreviewPanel.Visibility = Visibility.Visible;
        SidePreviewSplitterCol.Width = new GridLength(5);
        SidePreviewCol.Width = new GridLength(1, GridUnitType.Star);
        SidePreviewSplitter.Visibility = Visibility.Visible;
        MainStackCol.Width = new GridLength(1, GridUnitType.Star);
        // Upper becomes Media | Inspector: media stars to fill left stack width.
        PreviewCol.Width = new GridLength(0);
        MediaSplitterCol.Width = new GridLength(0);
        InspectorSplitterCol.Width = new GridLength(5);
        if (_vm.MediaVisible)
            MediaCol.Width = new GridLength(1, GridUnitType.Star);
        if (_vm.InspectorVisible)
            InspectorCol.Width = new GridLength(_inspectorWidth > 0 ? _inspectorWidth : EditorLayoutPreferences.DefaultInspectorWidth);
        _previewOnSide = true;
    }

    private void EnsurePreviewInUpperGrid()
    {
        // Mac Default: Media | Preview | Inspector over full-width Timeline.
        // Always restore column topology (not only when reparenting) so leaving Vertical
        // or loading Default never leaves Preview as a side full-height column.
        if (PreviewPanel.Parent != UpperGrid)
        {
            if (PreviewPanel.Parent is Panel parent)
                parent.Children.Remove(PreviewPanel);
            UpperGrid.Children.Add(PreviewPanel);
        }

        Grid.SetColumn(PreviewPanel, 2);
        Grid.SetRow(PreviewPanel, 0);
        Grid.SetRowSpan(PreviewPanel, 1);
        PreviewPanel.Margin = AppTheme.Padding.HXxsBottomXxs;
        PreviewPanel.Visibility = Visibility.Visible;

        SidePreviewSplitterCol.Width = new GridLength(0);
        SidePreviewCol.Width = new GridLength(0);
        SidePreviewSplitter.Visibility = Visibility.Collapsed;
        MainStackCol.Width = new GridLength(1, GridUnitType.Star);

        MediaSplitterCol.Width = new GridLength(5);
        MainStackCol.MinWidth = EditorLayoutPreferences.MinMediaWidth;
        InspectorSplitterCol.Width = new GridLength(5);
        PreviewCol.Width = new GridLength(1, GridUnitType.Star);
        if (_vm.MediaVisible)
            MediaCol.Width = new GridLength(_mediaWidth > 10 ? _mediaWidth : EditorLayoutPreferences.DefaultMediaWidth);
        else
            MediaCol.Width = new GridLength(0);
        if (_vm.InspectorVisible)
            InspectorCol.Width = new GridLength(_inspectorWidth > 10 ? _inspectorWidth : 300);
        else
            InspectorCol.Width = new GridLength(0);

        _previewOnSide = false;
    }

    private void LoadAndApplyPersistedLayout()
    {
        var prefs = EditorLayoutStore.Load();
        _activePreset = prefs.ParsedPreset;
        _agentWidth = prefs.AgentWidth;
        _mediaWidth = prefs.MediaWidth;
        _inspectorWidth = prefs.InspectorWidth;
        _timelineHeight = prefs.TimelineHeight;
        _timelineHeightAuto = prefs.TimelineHeightAuto;
        _vm.AgentVisible = prefs.AgentVisible;
        _vm.MediaVisible = prefs.MediaVisible;
        _vm.InspectorVisible = prefs.InspectorVisible;
        ApplyVerticalTopology(prefs.ParsedPreset == EditorLayoutPreset.Vertical);

        MediaCol.Width = prefs.MediaVisible ? new GridLength(prefs.MediaWidth) : new GridLength(0);
        InspectorCol.Width = prefs.InspectorVisible ? new GridLength(prefs.InspectorWidth) : new GridLength(0);
        TimelineRow.Height = new GridLength(prefs.TimelineHeight);
        ApplyPanelVisibility();
    }

    private void ScheduleLayoutPersist()
    {
        if (_maximized) return;
        _layoutSaveTimer ??= DispatcherQueue.CreateTimer();
        _layoutSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _layoutSaveTimer.IsRepeating = false;
        _layoutSaveTimer.Tick -= LayoutSaveTimer_Tick;
        _layoutSaveTimer.Tick += LayoutSaveTimer_Tick;
        _layoutSaveTimer.Start();
    }

    private void LayoutSaveTimer_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        PersistLayoutNow();
    }

    private void PersistLayoutNow()
    {
        // While maximized, columns may be star/zero — flush the pre-maximize field sizes only.
        if (!_maximized)
            CaptureLiveDividerSizes();

        // WP-185: an auto-collapsed agent panel is the window's doing, not the user's preference.
        var agentVis = _maximized ? _savedAgentVisible : _vm.AgentVisible;
        var mediaVis = _maximized ? _savedMediaVisible : _vm.MediaVisible;
        var inspVis = _maximized ? _savedInspectorVisible : _vm.InspectorVisible;
        var agentW = _maximized ? _savedAgentWidth : _agentWidth;
        var mediaW = _maximized ? _savedMediaWidth : _mediaWidth;
        var inspW = _maximized ? _savedInspectorWidth : _inspectorWidth;
        var tlH = _maximized ? _savedTimelineHeight : _timelineHeight;

        var prefs = EditorLayoutPreferences.MergeLiveSizes(
            new EditorLayoutPreferences
            {
                Preset = EditorLayoutPreferences.ToWire(_activePreset),
                AgentWidth = agentW,
                MediaWidth = mediaW,
                InspectorWidth = inspW,
                TimelineHeight = tlH,
                AgentVisible = agentVis,
                MediaVisible = mediaVis,
                InspectorVisible = inspVis,
                TimelineHeightAuto = _timelineHeightAuto,
            },
            agentW, mediaW, inspW, tlH,
            agentVis, mediaVis, inspVis);
        try { EditorLayoutStore.Save(prefs); }
        catch { /* ignore disk errors */ }
    }
}
