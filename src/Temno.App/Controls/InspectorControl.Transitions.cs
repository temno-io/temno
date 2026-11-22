using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Temno.App.Theme;
using Temno.App.Controls.Shared;
using Temno.Core.Ui;
using Temno.App.ViewModels;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Mcp;
using Temno.Media.Text;
using Windows.Foundation;
using ClipModel = Temno.Core.Models.Clip;
using TextAlign = Temno.Core.Models.TextAlignment;
using TransformModel = Temno.Core.Models.Transform;

namespace Temno.App.Controls;

public sealed partial class InspectorControl : UserControl
{
    // --- WP-252: a transition block selected on the timeline ---
    /// <summary>
    /// WP-252: the block selected on the timeline. A cut transition edits its type, a fade its
    /// curve; both edit their length and can be removed. This is the keyboard route to everything
    /// the block's edges and menu do.
    /// </summary>
    public void ShowTransition(TransitionBlockRef blockRef, bool expandLinks = true)
    {
        EndLiveEdit(commit: true);
        if (_transitionRef != blockRef || _transitionExpandLinks != expandLinks) EndTransitionEdit(commit: true);
        if (_session is null || TransitionBlocks.Find(_session.ActiveTimeline, blockRef) is not { } block)
        {
            _transitionRef = null;
            _transitionExpandLinks = true;
            ShowClip(null);
            return;
        }

        _transitionRef = blockRef;
        _transitionExpandLinks = expandLinks;
        _clipId = null;
        _clipIds = [];
        _pairAudioId = null;
        _lastInspectedWasText = false;
        EmptyLabel.Visibility = Visibility.Collapsed;
        ProjectPanel.Visibility = Visibility.Collapsed;
        FieldsPanel.Visibility = Visibility.Collapsed;
        MediaAssetPanel.Visibility = Visibility.Collapsed;
        MultiSelectBanner.Visibility = Visibility.Collapsed;
        HideAiEdit();
        TransitionPanel.Visibility = Visibility.Visible;
        if (_transitionEdit is null) FillTransition(block);
    }

    /// <summary>Leaves the transition panel: every other Show path calls this first.</summary>
    private void HideTransition()
    {
        EndTransitionEdit(commit: true);
        _transitionRef = null;
        _transitionExpandLinks = true;
        TransitionPanel.Visibility = Visibility.Collapsed;
    }

    private void FillTransition(TransitionBlock block)
    {
        if (_session is null) return;
        _suppress = true;
        var fps = Math.Max(1, _session.ActiveTimeline.Fps);
        var index = new TransitionIndex(_session.ActiveTimeline);
        var manifest = _session.Manifest;

        TransitionTitle.Text = TransitionBlocks.Name(block);
        TransitionPlace.Text = block.ClipB is { } b
            ? $"Between {ClipPresentation.Title(block.Clip, manifest)} and {ClipPresentation.Title(b, manifest)}"
            : $"{(block.Ref.Edge == FadeEdge.In ? "Start" : "End")} of {ClipPresentation.Title(block.Clip, manifest)}";
        TransitionRemoveText.Text = block.IsCut ? "Remove transition" : "Remove fade";
        AutomationProperties.SetName(TransitionRemoveButton, TransitionRemoveText.Text);

        TransitionTypeRow.Visibility = block.IsCut ? Visibility.Visible : Visibility.Collapsed;
        TransitionCurveRow.Visibility = block.IsCut ? Visibility.Collapsed : Visibility.Visible;
        if (block.Transition is { } transition)
        {
            if (TransitionTypeBox.Items.Count == 0)
            {
                foreach (var type in Enum.GetValues<TransitionType>())
                    TransitionTypeBox.Items.Add(new ComboBoxItem { Content = TransitionBlocks.DisplayName(type), Tag = type.ToString() });
            }
            SelectComboByTag(TransitionTypeBox, transition.Type.ToString());
            TransitionTypeRow.CanReset = transition.Type != TransitionBlocks.DefaultType;
        }
        else
        {
            SelectComboByTag(TransitionCurveBox, block.Curve == Interpolation.smooth ? "smooth" : "linear");
            TransitionCurveRow.CanReset = block.Curve == Interpolation.smooth;
        }

        var min = block.IsCut ? TransitionBlocks.MinCutFrames : TransitionBlocks.MinFadeFrames;
        var max = block.ClipB is { } clipB
            ? TransitionBlocks.MaxCut(index, block.Clip, clipB)
            : TransitionBlocks.MaxFade(index, block.Clip, block.Ref.Edge);
        max = Math.Max(max, block.Frames);
        TransitionDurationBox.Spec = new UnitFieldSpec
        {
            Unit = " s",
            Minimum = min / (double)fps,
            Maximum = max / (double)fps,
            Step = 1.0 / fps,
            Decimals = 2,
            UnitsPerPixel = 1.0 / fps,
            Default = Math.Min(max, fps) / (double)fps,
        };
        TransitionDurationBox.Value = block.Frames / (double)fps;
        TransitionRoomText.Text = block.IsCut
            ? $"Up to {TransitionBlocks.FormatSeconds(max, fps)} fits, centred on the cut."
            : $"Up to {TransitionBlocks.FormatSeconds(max, fps)} fits on this edge.";
        _suppress = false;
    }

    private void WireTransitionFields()
    {
        TransitionDurationBox.ValueChanged += TransitionDuration_ValueChanged;
        TransitionDurationBox.LostFocus += (_, _) => EndTransitionEdit(commit: true);
        TransitionDurationBox.CommitRequested += (_, _) => EndTransitionEdit(commit: true);
        // A scrub on the field's label never takes focus, so its release ends the edit.
        TransitionDurationBox.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndTransitionScrub()), handledEventsToo: true);
        TransitionDurationBox.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndTransitionScrub()), handledEventsToo: true);
        // The reset sits outside the field, so nothing else would end the edit it starts.
        TransitionDurationRow.ResetRequested += (_, _) =>
        {
            TransitionDurationBox.ResetToDefault();
            EndTransitionEdit(commit: true);
        };
        void RefreshReset() => TransitionDurationRow.CanReset = TransitionDurationBox.DiffersFromDefault;
        TransitionDurationBox.RegisterPropertyChangedCallback(UnitNumberField.ValueProperty, (_, _) => RefreshReset());
        TransitionDurationBox.RegisterPropertyChangedCallback(UnitNumberField.SpecProperty, (_, _) => RefreshReset());
        // Type and curve reset through their own SelectionChanged, so each is one undo step too.
        TransitionTypeRow.ResetRequested += (_, _) => SelectComboByTag(TransitionTypeBox, TransitionBlocks.DefaultType.ToString());
        TransitionCurveRow.ResetRequested += (_, _) => SelectComboByTag(TransitionCurveBox, "linear");
    }

    private void EndTransitionScrub()
    {
        if (TransitionDurationBox.FocusState == FocusState.Unfocused)
            EndTransitionEdit(commit: true);
    }

    /// <summary>Scrubbing, arrows and typing move the block live; one undo step when the edit ends.</summary>
    private void TransitionDuration_ValueChanged(object? sender, double seconds)
    {
        if (_suppress || _session is null || _transitionRef is not { } blockRef || !double.IsFinite(seconds)) return;
        _transitionEdit ??= TransitionDrag.BeginResize(_session, blockRef, rightEdge: true, expandLinks: _transitionExpandLinks);
        if (_transitionEdit is null) return;
        var fps = Math.Max(1, _session.ActiveTimeline.Fps);
        _transitionEdit.Apply((int)Math.Round(seconds * fps));
        ClipPreviewing?.Invoke(this, EventArgs.Empty);
    }

    private void EndTransitionEdit(bool commit)
    {
        if (_transitionEdit is not { } edit) return;
        _transitionEdit = null;
        if (!commit)
        {
            edit.Cancel();
            return;
        }
        if (edit.Commit())
            ClipEdited?.Invoke(this, EventArgs.Empty);
    }

    private void TransitionType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _session is null || _transitionRef is not { } blockRef) return;
        if (TransitionTypeBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<TransitionType>(tag, out var type)) return;
        EndTransitionEdit(commit: true);
        TransitionTypeRow.CanReset = type != TransitionBlocks.DefaultType;
        if (TransitionCommands.SetType(_session, blockRef, type).Ok)
            ClipEdited?.Invoke(this, EventArgs.Empty);
    }

    private void TransitionCurve_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _session is null || _transitionRef is not { } blockRef) return;
        if (TransitionCurveBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        EndTransitionEdit(commit: true);
        var curve = tag == "smooth" ? Interpolation.smooth : Interpolation.linear;
        TransitionCurveRow.CanReset = curve == Interpolation.smooth;
        if (TransitionCommands.SetCurve(_session, blockRef, curve, expandLinks: _transitionExpandLinks).Ok)
            ClipEdited?.Invoke(this, EventArgs.Empty);
    }

    private void TransitionRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _transitionRef is not { } blockRef) return;
        EndTransitionEdit(commit: true);
        if (TransitionCommands.Remove(_session, blockRef, expandLinks: _transitionExpandLinks).Ok)
            ClipEdited?.Invoke(this, EventArgs.Empty);
    }

    private static void SelectComboByTag(ComboBox box, string tag)
    {
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is ComboBoxItem { Tag: string t } && t == tag)
            {
                box.SelectedIndex = i;
                return;
            }
        }
    }
}
