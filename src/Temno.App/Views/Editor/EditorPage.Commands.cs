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
    // --- WP-70 menu commands ---
    private void MenuUndo_Click(object sender, RoutedEventArgs e)
    {
        AppSession.Current.Session?.Undo();
        RefreshAfterEdit();
    }

    private void MenuRedo_Click(object sender, RoutedEventArgs e)
    {
        AppSession.Current.Session?.Redo();
        RefreshAfterEdit();
    }

    private void MenuSplit_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.TrySplitAtPlayhead()) RefreshAfterEdit();
    }

    /// <summary>
    /// WP-205: the single creation path — the M key, the Edit menu item and the shortcuts
    /// dialog all land here. One undo step via <see cref="EditCommands.AddMarker"/>, and the
    /// existing <c>RefreshAfterEdit</c> fan-out (ruler redraw + Index refresh) runs, so the new
    /// marker shows on the ruler and in the Markers tab with no tab switch. New markers take
    /// Premiere's default marker green; the colour is editable in Edit Marker.
    /// </summary>
    private void AddMarkerAtPlayhead()
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        var result = EditCommands.AddMarker(session, Timeline.PlayheadFrame, NextMarkerName(session), MarkerPalette.DefaultHex);
        if (!result.Ok)
        {
            StatusText.Text = "Add Marker failed: " + (result.Error ?? "unknown error");
            return;
        }
        RefreshAfterEdit();
        StatusText.Text = $"Marker added at {Timeline.PlayheadFrame}";
    }

    /// <summary>WP-205: "Marker N", one past the highest existing Marker number — never a duplicate.</summary>
    private static string NextMarkerName(ProjectSession session)
    {
        var max = 0;
        foreach (var marker in session.ActiveTimeline.Markers)
        {
            var name = marker.Name;
            if (name is not null
                && name.StartsWith("Marker ", StringComparison.Ordinal)
                && int.TryParse(name["Marker ".Length..], out var n))
                max = Math.Max(max, n);
        }
        return "Marker " + (max + 1);
    }

    private void MenuAddMarker_Click(object sender, RoutedEventArgs e)
        => AddMarkerAtPlayhead();

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.TryDeleteSelection()) RefreshAfterEdit();
    }

    private void MenuLink_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.TryLinkSelection()) RefreshAfterEdit();
    }

    private void MenuUnlink_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.TryUnlinkSelection()) RefreshAfterEdit();
    }

    // WP-162 declared single EditorPage action: repair pre-WP-157 timelines from the menu.
    private void MenuRestoreLinkedAudio_Click(object sender, RoutedEventArgs e)
    {
        if (Timeline.TryRestoreLinkedAudioSelection()) RefreshAfterEdit();
    }

    private void MenuAddVideoTrack_Click(object sender, RoutedEventArgs e)
        => Timeline.TryAddTrack(ClipType.video);

    private void MenuAddAudioTrack_Click(object sender, RoutedEventArgs e)
        => Timeline.TryAddTrack(ClipType.audio);

    private void MenuToolSelect_Click(object sender, RoutedEventArgs e)
        => SetToolMode(EditorToolMode.Select);

    private void MenuToolTrim_Click(object sender, RoutedEventArgs e)
        => SetToolMode(EditorToolMode.Trim);

    private void MenuToolBlade_Click(object sender, RoutedEventArgs e)
        => SetToolMode(EditorToolMode.Blade);

    private void ToolSelect_Click(object sender, RoutedEventArgs e)
        => SetToolMode(EditorToolMode.Select);

    private void ToolTrim_Click(object sender, RoutedEventArgs e)
        => SetToolMode(EditorToolMode.Trim);

    private void ToolBlade_Click(object sender, RoutedEventArgs e)
        => SetToolMode(EditorToolMode.Blade);

    private void ToolRipple_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingToolbar) return;
        EditorEditContext.Current.RippleDelete = ToolRipple.IsChecked == true;
        Timeline.SyncEditModeUi();
    }

    private void ToolOverwrite_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingToolbar) return;
        EditorEditContext.Current.OverwriteOnMove = ToolOverwrite.IsChecked == true;
        Timeline.SyncEditModeUi();
    }

    private void ToolSnap_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingToolbar) return;
        EditorEditContext.Current.SnapEnabled = ToolSnap.IsChecked == true;
        Timeline.SyncEditModeUi();
    }

    private void ToolFit_Click(object sender, RoutedEventArgs e)
    {
        Timeline.FitToWindow();
        SyncZoomSliderFromTimeline();
    }

    private void SetToolMode(EditorToolMode mode)
    {
        EditorEditContext.Current.ToolMode = mode;
        Timeline.Rebuild();
        SyncTimelineToolbar();
    }

    private void SyncTimelineToolbar()
    {
        if (ToolSelect is null) return;
        _syncingToolbar = true;
        try
        {
            var mode = EditorEditContext.Current.ToolMode;
            ToolSelect.IsChecked = mode == EditorToolMode.Select;
            ToolTrim.IsChecked = mode == EditorToolMode.Trim;
            ToolBlade.IsChecked = mode == EditorToolMode.Blade;

            // WP-147 (N2): the Edit menu's tool group reads from this same source, so the menu and
            // the toolbar cannot disagree about which tool is active.
            if (MenuToolSelectItem is not null) MenuToolSelectItem.IsChecked = mode == EditorToolMode.Select;
            if (MenuToolTrimItem is not null) MenuToolTrimItem.IsChecked = mode == EditorToolMode.Trim;
            if (MenuToolBladeItem is not null) MenuToolBladeItem.IsChecked = mode == EditorToolMode.Blade;
            ToolRipple.IsChecked = EditorEditContext.Current.RippleDelete;
            ToolOverwrite.IsChecked = EditorEditContext.Current.OverwriteOnMove;
            ToolSnap.IsChecked = EditorEditContext.Current.SnapEnabled;
            Timeline.SyncEditModeUi();
        }
        finally
        {
            _syncingToolbar = false;
        }
    }
}
