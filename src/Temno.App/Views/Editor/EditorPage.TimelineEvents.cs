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
    private void Timeline_PlayheadChanged(object? sender, int frame)
    {
        if (_syncingPlayhead) return;
        _syncingPlayhead = true;
        Preview.ActivateTimelineTab(frame);
        Inspector.PlayheadFrame = frame;
        TranscriptIndex.SetPlayheadFrame(frame);
        _syncingPlayhead = false;
        // Meter sampling is expensive during scrub; only update while playing (or forced).
        if (Preview.IsPlaying && Preview.IsTimelineMonitor)
            _ = RefreshAudioMeterAsync();
    }

    private void Preview_PlayheadChanged(object? sender, int frame)
    {
        if (_syncingPlayhead || !Preview.IsTimelineMonitor) return;
        _syncingPlayhead = true;
        Timeline.PlayheadFrame = frame;
        Inspector.PlayheadFrame = frame;
        TranscriptIndex.SetPlayheadFrame(frame);
        // WP-68: keep the playhead in view while the clock advances.
        if (Preview.IsPlaying)
            Timeline.EnsurePlayheadVisible();
        _syncingPlayhead = false;
        if (Preview.IsPlaying)
            _ = RefreshAudioMeterAsync();
    }

    private void Timeline_ScrubStarted(object? sender, EventArgs e)
    {
        Preview.ActivateTimelineTab();
        Preview.BeginScrub();
    }

    private void Timeline_ScrubEnded(object? sender, EventArgs e)
    {
        Preview.EndScrub();
    }

    private void Timeline_SelectionChanged(object? sender, string? clipId)
    {
        if (clipId is not null || Timeline.SelectedTransition is not null)
            Preview.ActivateTimelineTab();
        ShowInspectorForTimelineSelection();
        if (!_suppressTimelineFocusOnSelectionChange)
        {
            SetFocusedPanel(ShellPanel.Timeline);
            RestoreEditorKeyboardFocus();
        }
    }

    /// <summary>WP-252: a selected transition block has its own Inspector panel; otherwise the clips.</summary>
    private void ShowInspectorForTimelineSelection()
    {
        if (Timeline.SelectedTransition is { } block)
            Inspector.ShowTransition(block, Timeline.SelectedTransitionExpandLinks);
        else
            Inspector.ShowSelection(Timeline.SelectedClipIds, Timeline.SelectedClipId);
    }

    /// <summary>
    /// WP-252: while a block edge is dragged, the monitor shows the frame under that edge, like a trim
    /// monitor, without moving the playhead. Stamped like an Inspector scrub (WP-153) so the coalesced
    /// refresh cannot land mid-drag.
    /// </summary>
    private void Timeline_TransitionPreviewing(object? sender, int frame)
    {
        _lastLivePreview = Environment.TickCount64;
        if (!Preview.IsTimelineMonitor || Preview.IsPlaying) return;
        Preview.ShowFrameWithoutSeeking(frame);
    }

    private void Timeline_TransitionPreviewEnded(object? sender, EventArgs e)
    {
        if (Preview.IsTimelineMonitor)
            Preview.InvalidateCurrentFrame();
    }

    /// <summary>WP-184: the clip context menu's commands map onto the same paths the Edit menu uses.</summary>
    private void Timeline_ContextActionRequested(object? sender, string action)
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        switch (action)
        {
            case "split": if (Timeline.TrySplitAtPlayhead()) RefreshAfterEdit(); break;
            case "add-transition":
                // WP-252: the clip menu's Ctrl+D item; a success refreshes through TimelineEdited.
                Timeline.TryAddDefaultTransition(out var added);
                StatusText.Text = added;
                break;
            case "trim-start":
                if (Timeline.TryTrimStartToPlayhead()) RefreshAfterEdit();
                else StatusText.Text = "Trim Start: place the playhead inside the clip";
                break;
            case "trim-end":
                if (Timeline.TryTrimEndToPlayhead()) RefreshAfterEdit();
                else StatusText.Text = "Trim End: place the playhead inside the clip";
                break;
            case "delete": if (Timeline.TryDeleteSelection()) RefreshAfterEdit(); break;
            case "ripple-delete": MenuRippleDelete_Click(this, new RoutedEventArgs()); break;
            case "link": if (Timeline.TryLinkSelection()) RefreshAfterEdit(); break;
            case "unlink": if (Timeline.TryUnlinkSelection()) RefreshAfterEdit(); break;
            case "restore-audio": if (Timeline.TryRestoreLinkedAudioSelection()) RefreshAfterEdit(); break;
            case "reveal-source":
            {
                var clip = session.ActiveTimeline.Tracks.SelectMany(t => t.Clips)
                    .FirstOrDefault(c => c.Id == Timeline.SelectedClipId);
                var item = clip is null ? null : _workspaceMediaItems.FirstOrDefault(i => i.Id == clip.MediaRef);
                if (item?.SourcePath is { Length: > 0 } path && File.Exists(path)) AppSession.RevealInExplorer(path);
                else StatusText.Text = "This clip has no source file on disk to reveal.";
                break;
            }
            case "properties":
                ShowPanel(ShellPanel.Inspector, forceVisible: true);
                SetFocusedPanel(ShellPanel.Inspector);
                break;
        }
    }

    private void Timeline_TimelineEdited(object? sender, EventArgs e)
    {
        Preview.ActivateTimelineTab();
        RefreshAfterEdit();
    }

    private void Inspector_ClipEdited(object? sender, EventArgs e)
        => RefreshAfterEdit();

    private void Inspector_MediaLibraryChanged(object? sender, EventArgs e)
        => RefreshAfterEdit();

    private void Inspector_ClipPreviewing(object? sender, EventArgs e)
    {
        // WP-153: an Inspector scrub calls ClipLiveEdit.Mutate — and therefore MarkDirty — on every
        // value change, deliberately without raising ClipEdited until the gesture commits. Left alone,
        // the coalesced refresh would fire mid-drag, and RefreshAfterEdit begins with
        // Inspector.ShowSelection, which calls EndLiveEdit(commit: true) and ends the user's gesture
        // under their pointer. Timestamped rather than flagged so a gesture that never commits cannot
        // wedge external refreshes off.
        _lastLivePreview = Environment.TickCount64;
        if (Timeline.SelectedTransition is not null)
            Timeline.RedrawTransitionBlocks(); // WP-252: the block follows the duration scrub
        if (Preview.IsTimelineMonitor)
            Preview.InvalidateCurrentFrame();
    }

    private void Inspector_ProjectSettingsChanged(object? sender, EventArgs e)
        => RefreshAfterEdit();

    private void Inspector_SeekPlayheadRequested(object? sender, int frame)
    {
        _syncingPlayhead = true;
        try
        {
            Timeline.PlayheadFrame = frame;
            Preview.ActivateTimelineTab(frame);
            Inspector.PlayheadFrame = frame;
            TranscriptIndex.SetPlayheadFrame(frame);
        }
        finally
        {
            _syncingPlayhead = false;
        }
    }

    /// <summary>
    /// Redundant since WP-153 — the chat's tool calls reach <c>ProjectSession.Changed</c> like every
    /// other path — but retained because retiring it needs <c>EditorPage.xaml</c> and
    /// <c>AgentPanelControl.xaml.cs</c>, outside this package's file list. It no longer calls
    /// <see cref="RefreshAfterEdit"/> directly: <c>AgentChatController</c> raises it from inside
    /// <c>Task.Run</c>, so that was touching WinUI from a thread-pool thread.
    /// </summary>
    private void Agent_ToolsMutated(object? sender, EventArgs e)
        => RequestSessionRefresh();

    private void Agent_CloseRequested(object? sender, EventArgs e)
    {
        if (_maximized) RestoreLayoutFromMaximize();
        _vm.AgentVisible = false;
        ApplyPanelVisibility();
        ScheduleLayoutPersist();
    }

    private void Agent_OpenSettingsRequested(object? sender, EventArgs e)
        => _ = OpenSettingsAsync("agent"); // WP-191: the gate promised the AI Chat key

    private void Agent_OpenAccountRequested(object? sender, EventArgs e)
        => _ = AccountDialog.ShowAsync(XamlRoot);
}
