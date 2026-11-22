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
    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (HandleMenuKey(e)) return;
        if (TryRouteHistoryKey(e))
        {
            e.Handled = true;
            return;
        }
        // Space must be taken before a focused Button (preview tab, transport) marks it handled
        // and clicks itself. That click is why Space plays only while the play button is focused
        // and does nothing after selecting a preview tab.
        if (e.Key == VirtualKey.Space && TryHandleTransportSpace(e))
        {
            e.Handled = true;
            return;
        }
        // Frame jog must win over media-list arrows, native MediaPlayer, and inspector NumberBoxes
        // (WinUI NumberBox hosts a TextBox; selecting a clip focuses Start frame).
        if (e.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Home))
            return;
        if (BlocksFrameJog(e.OriginalSource))
            return;
        if (TryHandleFrameJog(e.Key))
            e.Handled = true;
    }

    /// <summary>
    /// Play/pause from Space while the preview or timeline owns the editor, including when a
    /// chrome button has keyboard focus. Repeats are swallowed so holding Space does not toggle
    /// twice, and so the focused button does not also click.
    /// </summary>
    private bool TryHandleTransportSpace(KeyRoutedEventArgs e)
    {
        if (!CanHandleTransportSpace(e))
            return false;

        // Key-repeat reports WasKeyDown, and some builds also set it on the first message.
        // A repeat shortly after a real toggle is swallowed. The first press still plays,
        // because the stamp is still zero.
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var repeat = e.KeyStatus.WasKeyDown || e.KeyStatus.RepeatCount > 1;
        if (repeat && now - _transportSpaceStamp < System.Diagnostics.Stopwatch.Frequency / 3)
            return true;
        _transportSpaceStamp = now;

        var switchedToProgram = false;
        if (_focusedPanel == ShellPanel.Timeline && !Preview.IsTimelineMonitor)
        {
            Preview.ActivateTimelineTab();
            switchedToProgram = true;
        }

        if (switchedToProgram)
            Preview.PlayIfStopped();
        else
            Preview.TogglePlay();
        return true;
    }

    private bool CanHandleTransportSpace(KeyRoutedEventArgs e)
    {
        if (AppSession.Current.Session is null)
            return false;
        if (IsTypingSurface(e.OriginalSource))
            return false;
        if (FindAncestor<MenuFlyoutItem>(e.OriginalSource) is not null)
            return false;
        if (_focusedPanel is ShellPanel.Timeline or ShellPanel.Preview)
            return true;
        // The focus ring can lag the control that actually has the key.
        return IsInside(e.OriginalSource, PreviewPanel) || IsInside(e.OriginalSource, TimelinePanel);
    }

    private static bool IsInside(object? source, DependencyObject ancestor)
    {
        for (var node = source as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ancestor))
                return true;
        }

        return false;
    }

    /// <summary>
    /// A click in the preview or timeline, including a preview tab or the play button, leaves
    /// keyboard focus on that button. Space then clicks the button. Move focus back to the editor
    /// once the click has run, unless a menu opened from it or the click was in a text field.
    /// </summary>
    private void TransportPanel_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<ShellPanel>(tag, out var panel)) return;
        if (panel is not (ShellPanel.Preview or ShellPanel.Timeline)) return;
        if (IsTypingSurface(e.OriginalSource)) return;
        if (XamlRoot is not null && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count > 0)
            return;
        SetFocusedPanel(panel);
        RestoreEditorKeyboardFocus();
    }

    /// <summary>
    /// WP-187: the File-menu accelerators shown in the menu bar — Ctrl+S Save, Ctrl+N New Project,
    /// Ctrl+O Open Project, Ctrl+I Import Media, Ctrl+M Export. Like Save, they also run while typing.
    /// </summary>
    private bool TryRunProjectAccelerator(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.S:
                SaveProject();
                return true;
            case VirtualKey.N:
                MenuNewProject_Click(this, new RoutedEventArgs());
                return true;
            case VirtualKey.O:
                MenuOpenProject_Click(this, new RoutedEventArgs());
                return true;
            case VirtualKey.I:
                ImportMedia_Click(this, new RoutedEventArgs());
                return true;
            case VirtualKey.M:
                Export_Click(this, new RoutedEventArgs());
                return true;
            default:
                return false;
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var session = AppSession.Current.Session;
        if (session is null) return;

        ReadModifiers(out var ctrl, out var shift);

        if (IsTypingSurface(e.OriginalSource))
        {
            // Save and the File accelerators (WP-187) still run while typing; editing keys do not.
            if (ctrl && TryRunProjectAccelerator(e.Key))
                e.Handled = true;
            return;
        }

        if (e.Handled)
            return;

        var transportFocus = _focusedPanel is ShellPanel.Timeline or ShellPanel.Preview;
        var timelineFocus = _focusedPanel == ShellPanel.Timeline;
        if (timelineFocus && !Preview.IsTimelineMonitor)
            Preview.ActivateTimelineTab();

        switch (e.Key)
        {
            case VirtualKey.J:
                if (!transportFocus) return;
                Preview.Step(-1);
                e.Handled = true;
                break;
            case VirtualKey.L when ctrl && shift:
                if (!timelineFocus) return;
                if (Timeline.TryUnlinkSelection()) RefreshAfterEdit();
                e.Handled = true;
                break;
            case VirtualKey.L when ctrl:
                if (!timelineFocus) return;
                if (Timeline.TryLinkSelection()) RefreshAfterEdit();
                e.Handled = true;
                break;
            case VirtualKey.L:
                if (!transportFocus) return;
                Preview.Step(1);
                e.Handled = true;
                break;
            case VirtualKey.K:
                if (!transportFocus) return;
                if (Preview.IsPlaying) Preview.TogglePlay();
                e.Handled = true;
                break;
            case VirtualKey.B:
                if (!timelineFocus) return;
                if (Timeline.TrySplitAtPlayhead()) RefreshAfterEdit();
                e.Handled = true;
                break;
            // WP-205: bare M adds a marker at the playhead. Timeline-scoped like B/Q/W (the
            // marker lands on the ruler, so it needs the timeline's focus contract); typing
            // surfaces return above, and Ctrl+M is Export via TryRunProjectAccelerator.
            case VirtualKey.M when !ctrl:
                if (!timelineFocus) return;
                AddMarkerAtPlayhead();
                e.Handled = true;
                break;
            case VirtualKey.C:
                SetToolMode(EditorToolMode.Blade);
                e.Handled = true;
                break;
            case VirtualKey.Q:
                if (!timelineFocus) return;
                if (Timeline.TryTrimStartToPlayhead()) RefreshAfterEdit();
                else StatusText.Text = "Trim Start: select a clip and place playhead inside it";
                e.Handled = true;
                break;
            case VirtualKey.W:
                if (!timelineFocus) return;
                if (Timeline.TryTrimEndToPlayhead()) RefreshAfterEdit();
                else StatusText.Text = "Trim End: select a clip and place playhead inside it";
                e.Handled = true;
                break;
            case VirtualKey.A when shift:
                if (!timelineFocus) return;
                if (Timeline.TrySelectForwardFromCurrentSelection(TimelineControl.SelectForwardScope.AllTracks))
                    StatusText.Text = "Select forward · all tracks";
                e.Handled = true;
                break;
            case VirtualKey.A:
                if (!timelineFocus) return;
                if (Timeline.TrySelectForwardFromCurrentSelection(TimelineControl.SelectForwardScope.Track))
                    StatusText.Text = "Select forward · track";
                e.Handled = true;
                break;
            case VirtualKey.Delete:
            case VirtualKey.Back:
                if (!timelineFocus) return;
                if (Timeline.TryDeleteSelection()) RefreshAfterEdit();
                e.Handled = true;
                break;
            // WP-252: Ctrl+D is Premiere's Apply Default Transition, at the edit point nearest the playhead.
            case VirtualKey.D when ctrl:
                if (!timelineFocus) return;
                // A success refreshes through TimelineEdited; either way the status line says what happened.
                Timeline.TryAddDefaultTransition(out var added);
                StatusText.Text = added;
                e.Handled = true;
                break;
            case VirtualKey.Application:
                if (!timelineFocus) return;
                if (Timeline.TryShowSelectedTransitionMenu()) e.Handled = true;
                break;
            case VirtualKey.Escape:
                if (_maximized)
                {
                    RestoreLayoutFromMaximize();
                    e.Handled = true;
                }
                else if (App.MainAppWindow?.AppWindow?.Presenter is FullScreenPresenter)
                {
                    App.MainAppWindow.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
                    ScheduleTimelineLayoutRefresh();
                    e.Handled = true;
                }
                else if (timelineFocus && Timeline.TryClearTransitionSelection())
                {
                    e.Handled = true;
                }
                break;
            case VirtualKey.Z when ctrl:
            case VirtualKey.Y when ctrl:
                RunHistory(EditorUndoRouting.Route(EditorKeySurface.None, false, (char)e.Key, ctrl, shift));
                e.Handled = true;
                break;
            case VirtualKey.S when ctrl:
                SaveProject();
                e.Handled = true;
                break;
            case VirtualKey.N when ctrl:
            case VirtualKey.O when ctrl:
            case VirtualKey.I when ctrl:
            case VirtualKey.M when ctrl:
                if (TryRunProjectAccelerator(e.Key))
                    e.Handled = true;
                break;
            case VirtualKey.V:
            case VirtualKey.Number1:
                SetToolMode(EditorToolMode.Select);
                e.Handled = true;
                break;
            case VirtualKey.Number2:
                SetToolMode(EditorToolMode.Trim);
                e.Handled = true;
                break;
            case VirtualKey.Number3:
                SetToolMode(EditorToolMode.Blade);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// WP-253: undo and redo from a focused Inspector control, taken while tunnelling so a committed
    /// field's text box never rewinds its own text as well. Canvas/ScrollViewer focus also routes
    /// here because the control can consume Ctrl+Z before the bubbling KeyDown handler sees it.
    /// </summary>
    private bool TryRouteHistoryKey(KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Z or VirtualKey.Y) || AppSession.Current.Session is null)
            return false;
        var surface = ClassifyKeySurface(e.OriginalSource, out var dropDownOpen);
        ReadModifiers(out var ctrl, out var shift);
        return RunHistory(EditorUndoRouting.Route(surface, dropDownOpen, (char)e.Key, ctrl, shift));
    }

    private bool RunHistory(EditorHistoryCommand command)
    {
        var session = AppSession.Current.Session;
        if (session is null || command == EditorHistoryCommand.None)
            return false;
        Inspector.EndPendingEdit();
        if (command == EditorHistoryCommand.Undo)
            session.Undo();
        else
            session.Redo();
        RefreshAfterEdit();
        return true;
    }

    private static EditorKeySurface ClassifyKeySurface(object? source, out bool dropDownOpen)
    {
        dropDownOpen = FindAncestor<ComboBoxItem>(source) is not null;
        if (FindAncestor<ContentDialog>(source) is not null)
            return EditorKeySurface.FreeText;
        if (FindAncestor<UnitNumberField>(source) is { } field)
            return field.HasPendingText ? EditorKeySurface.PendingField : EditorKeySurface.CommittedField;
        if (FindAncestor<ComboBox>(source) is { } combo)
        {
            dropDownOpen |= combo.IsDropDownOpen;
            return combo.IsEditable ? EditorKeySurface.FreeText : EditorKeySurface.NoTextUndo;
        }
        if (dropDownOpen || FindAncestor<Slider>(source) is not null)
            return EditorKeySurface.NoTextUndo;
        return IsTypingSurface(source) ? EditorKeySurface.FreeText : EditorKeySurface.None;
    }

    private static bool IsTypingSurface(object? source)
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or NumberBox or ComboBox or Slider)
                return true;
            if (d is ContentDialog)
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when Left/Right/Home should stay with a real text caret, not playhead jog.
    /// Inspector NumberBox/Slider/closed ComboBox are not text carets — clip select focuses them.
    /// </summary>
    private static bool BlocksFrameJog(object? source)
    {
        if (FindAncestor<ContentDialog>(source) is not null)
            return true;
        if (FindAncestor<NumberBox>(source) is not null)
            return false;
        if (FindAncestor<Slider>(source) is not null)
            return false;
        if (FindAncestor<ComboBox>(source) is ComboBox combo)
            return combo.IsDropDownOpen;
        return FindAncestor<TextBox>(source) is not null
            || FindAncestor<PasswordBox>(source) is not null
            || FindAncestor<RichEditBox>(source) is not null
            || FindAncestor<AutoSuggestBox>(source) is not null;
    }

    private static T? FindAncestor<T>(object? source) where T : DependencyObject
    {
        for (var d = source as DependencyObject; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is T match)
                return match;
        }

        return null;
    }

    private void RestoreEditorKeyboardFocus()
    {
        RootGrid.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_focusedPanel is ShellPanel.Timeline or ShellPanel.Preview)
                RootGrid.Focus(FocusState.Programmatic);
        });
    }

    private bool TryHandleFrameJog(VirtualKey key)
    {
        var session = AppSession.Current.Session;
        if (session is null)
            return false;

        ReadModifiers(out var ctrl, out _);
        switch (key)
        {
            case VirtualKey.Left:
                Preview.Step(ctrl ? -session.ActiveTimeline.Fps : -1);
                return true;
            case VirtualKey.Right:
                Preview.Step(ctrl ? session.ActiveTimeline.Fps : 1);
                return true;
            case VirtualKey.Home:
                Preview.GoToStart();
                return true;
            default:
                return false;
        }
    }

    private static void ReadModifiers(out bool ctrl, out bool shift)
    {
        ctrl = KeyIsDown(VirtualKey.Control)
            || KeyIsDown(VirtualKey.LeftControl)
            || KeyIsDown(VirtualKey.RightControl);
        shift = KeyIsDown(VirtualKey.Shift)
            || KeyIsDown(VirtualKey.LeftShift)
            || KeyIsDown(VirtualKey.RightShift);
    }

    private static bool KeyIsDown(VirtualKey key)
        => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
}
