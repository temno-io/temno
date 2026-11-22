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
    private readonly record struct KfStatusKey(string? ClipId, int Count, bool InClip, bool Has, bool HasPrev, bool HasNext);
    private readonly Dictionary<string, KfStatusKey> _kfStatusCache = new(StringComparer.Ordinal);

    private string? KeyframeTargetClipId(string property)
        => string.Equals(property, "volume", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_pairAudioId)
            ? _pairAudioId
            : _clipId;

    private void RefreshKeyframeStatus(ClipModel? clip)
    {
        if (clip is null)
        {
            _kfStatusCache.Clear();
            SetKfStatus(OpacityKfStatus, OpacityKfStampBtn, OpacityKfPrevBtn, OpacityKfNextBtn, null, "opacity");
            SetKfStatus(VolumeKfStatus, VolumeKfStampBtn, VolumeKfPrevBtn, VolumeKfNextBtn, null, "volume");
            SetKfStatus(ScaleKfStatus, ScaleKfStampBtn, ScaleKfPrevBtn, ScaleKfNextBtn, null, "scale");
            SetKfStatus(PositionKfStatus, PositionKfStampBtn, PositionKfPrevBtn, PositionKfNextBtn, null, "position");
            SetKfStatus(RotationKfStatus, RotationKfStampBtn, RotationKfPrevBtn, RotationKfNextBtn, null, "rotation");
            SetKfStatus(CropKfStatus, CropKfStampBtn, CropKfPrevBtn, CropKfNextBtn, null, "crop");
            RefreshKeyframeLane(null);
            return;
        }

        // WP-183: for a linked A/V pair, volume keyframes live on the audio partner.
        var volumeClip = _pairAudioId is { } partnerId && FindClip(partnerId) is { } partner ? partner : clip;
        SetKfStatus(OpacityKfStatus, OpacityKfStampBtn, OpacityKfPrevBtn, OpacityKfNextBtn, clip, "opacity");
        SetKfStatus(VolumeKfStatus, VolumeKfStampBtn, VolumeKfPrevBtn, VolumeKfNextBtn, volumeClip, "volume");
        SetKfStatus(ScaleKfStatus, ScaleKfStampBtn, ScaleKfPrevBtn, ScaleKfNextBtn, clip, "scale");
        SetKfStatus(PositionKfStatus, PositionKfStampBtn, PositionKfPrevBtn, PositionKfNextBtn, clip, "position");
        SetKfStatus(RotationKfStatus, RotationKfStampBtn, RotationKfPrevBtn, RotationKfNextBtn, clip, "rotation");
        SetKfStatus(CropKfStatus, CropKfStampBtn, CropKfPrevBtn, CropKfNextBtn, clip, "crop");
        RefreshKeyframeLane(clip);
    }

    private void SetKfStatus(
        TextBlock status,
        Button stampBtn,
        Button prevBtn,
        Button nextBtn,
        ClipModel? clip,
        string property)
    {
        if (clip is null)
        {
            status.Text = "";
            stampBtn.IsEnabled = false;
            prevBtn.IsEnabled = false;
            nextBtn.IsEnabled = false;
            SetStampIcon(stampBtn, stamped: false, trackActive: false);
            return;
        }

        var count = KeyframeCommands.KeyframeCount(clip, property);
        var inClip = clip.Contains(PlayheadFrame);
        var has = inClip && KeyframeCommands.HasKeyframeAt(clip, property, PlayheadFrame);
        var hasPrev = KeyframeCommands.PreviousTimelineFrame(clip, property, PlayheadFrame) is not null;
        var hasNext = KeyframeCommands.NextTimelineFrame(clip, property, PlayheadFrame) is not null;

        var state = new KfStatusKey(clip.Id, count, inClip, has, hasPrev, hasNext);
        if (_kfStatusCache.TryGetValue(property, out var prev) && prev == state)
            return;
        _kfStatusCache[property] = state;

        status.Text = count == 0
            ? $"No {property} keys"
            : $"{PropertyLabel(property)}: {count} key(s)" + (has ? " · at playhead" : "");
        stampBtn.IsEnabled = inClip;
        prevBtn.IsEnabled = hasPrev;
        nextBtn.IsEnabled = hasNext;
        ToolTipService.SetToolTip(stampBtn, !inClip
            ? $"Move playhead over this clip to add a {property} keyframe"
            : has
                ? $"Remove {property} keyframe at playhead"
                : count > 0
                    ? $"Add {property} keyframe at playhead (editing the value also auto-keyframes)"
                    : $"Stamp {property} keyframe at playhead");
        SetStampIcon(stampBtn, has, count > 0);
    }

    private static string PropertyLabel(string property) => property switch
    {
        "opacity" => "Opacity",
        "volume" => "Volume",
        "scale" => "Scale",
        "position" => "Position",
        "rotation" => "Rotation",
        "crop" => "Crop",
        _ => property,
    };

    private static void SetStampIcon(Button button, bool stamped, bool trackActive)
    {
        if (button.Content is Temno.App.Controls.Shared.FluentIcon icon)
            icon.Icon = stamped ? Temno.App.Controls.Shared.Icons.DiamondFilled : Temno.App.Controls.Shared.Icons.Diamond;
        button.Foreground = stamped
            ? AppTheme.Accent.TimecodeBrush
            : trackActive
                ? AppTheme.Accent.ReadableBrush
                : AppTheme.Text.TertiaryBrush;
    }

    private static readonly (string Property, string Label)[] LaneProperties =
    [
        ("opacity", "Op"),
        ("scale", "Sc"),
        ("position", "Pos"),
        ("rotation", "Rot"),
        ("crop", "Cr"),
    ];

    private void KeyframeLaneCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => RefreshKeyframeLane(string.IsNullOrEmpty(_clipId) ? null : FindClip(_clipId));

    private void RefreshKeyframeLane(ClipModel? clip)
    {
        if (clip is not null && !IsKeyframesSectionVisible)
            return;
        KeyframeLaneCanvas.Children.Clear();
        var w = KeyframeLaneCanvas.ActualWidth;
        var h = KeyframeLaneCanvas.ActualHeight;
        if (w < 8 || h < 8 || clip is null || clip.DurationFrames <= 0)
            return;

        var labelW = KfLaneLabelW;
        var trackW = Math.Max(8, w - labelW - 4);
        var rowH = h / LaneProperties.Length;
        var playRel = PlayheadFrame - clip.StartFrame;
        var playFrac = Math.Clamp(playRel / (double)clip.DurationFrames, 0, 1);

        for (var i = 0; i < LaneProperties.Length; i++)
        {
            var (prop, label) = LaneProperties[i];
            var yMid = rowH * i + rowH / 2;

            var labelBlock = new TextBlock
            {
                Text = label,
                FontSize = AppTheme.FontSize.Xs,
                Foreground = AppTheme.Text.TertiaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(labelBlock, 0);
            Canvas.SetTop(labelBlock, yMid - 7);
            KeyframeLaneCanvas.Children.Add(labelBlock);

            var baseline = new Line
            {
                X1 = labelW,
                Y1 = yMid,
                X2 = labelW + trackW,
                Y2 = yMid,
                Stroke = AppTheme.Border.SubtleBrush,
                StrokeThickness = 1,
                IsHitTestVisible = false,
            };
            KeyframeLaneCanvas.Children.Add(baseline);

            foreach (var offset in KeyframeCommands.GetOffsets(clip, prop))
            {
                if (offset < 0 || offset >= clip.DurationFrames) continue;
                var drawOff = _kfDragging && _kfDragProperty == prop && offset == _kfDragFromOffset
                    ? _kfDragLiveOffset
                    : offset;
                var frac = drawOff / (double)Math.Max(1, clip.DurationFrames - 1);
                frac = Math.Clamp(frac, 0, 1);
                var x = labelW + frac * trackW;
                var hit = new Border
                {
                    Width = 14,
                    Height = 14,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    Tag = (prop, offset),
                    Child = new TextBlock
                    {
                        Text = "◆",
                        FontSize = 10,
                        Foreground = AppTheme.Accent.TimecodeBrush,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = false,
                    },
                };
                Canvas.SetLeft(hit, x - 7);
                Canvas.SetTop(hit, yMid - 7);
                KeyframeLaneCanvas.Children.Add(hit);
            }
        }

        var px = labelW + playFrac * trackW;
        var playLine = new Line
        {
            X1 = px,
            Y1 = 2,
            X2 = px,
            Y2 = h - 2,
            Stroke = AppTheme.Accent.SpotlightBrush,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 },
            IsHitTestVisible = false,
        };
        KeyframeLaneCanvas.Children.Add(playLine);
    }

    private void KeyframeLane_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null || string.IsNullOrEmpty(_clipId)) return;
        var clip = FindClip(_clipId);
        if (clip is null || clip.DurationFrames <= 0) return;

        // Hit-test diamond borders
        var src = e.OriginalSource as FrameworkElement;
        while (src is not null && src is not Border { Tag: ValueTuple<string, int> })
            src = src.Parent as FrameworkElement;
        if (src is not Border { Tag: ValueTuple<string, int> tag }) return;

        _kfDragging = true;
        _kfDragProperty = tag.Item1;
        _kfDragFromOffset = tag.Item2;
        _kfDragLiveOffset = tag.Item2;
        KeyframeLaneCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void KeyframeLane_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_kfDragging || _session is null || string.IsNullOrEmpty(_clipId)) return;
        var clip = FindClip(_clipId);
        if (clip is null) return;
        var pos = e.GetCurrentPoint(KeyframeLaneCanvas).Position;
        var trackW = Math.Max(8, KeyframeLaneCanvas.ActualWidth - KfLaneLabelW - 4);
        var frac = Math.Clamp((pos.X - KfLaneLabelW) / trackW, 0, 1);
        _kfDragLiveOffset = (int)Math.Round(frac * Math.Max(0, clip.DurationFrames - 1));
        _kfDragLiveOffset = Math.Clamp(_kfDragLiveOffset, 0, Math.Max(0, clip.DurationFrames - 1));
        RefreshKeyframeLane(clip);
        e.Handled = true;
    }

    private void KeyframeLane_PointerReleased(object sender, PointerRoutedEventArgs e)
        => EndKeyframeDrag(e.Pointer);

    private void KeyframeLane_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => EndKeyframeDrag(null);

    private void EndKeyframeDrag(Pointer? pointer)
    {
        if (!_kfDragging) return;
        _kfDragging = false;
        if (pointer is not null)
        {
            try { KeyframeLaneCanvas.ReleasePointerCapture(pointer); } catch { /* ignore */ }
        }

        if (_session is null || string.IsNullOrEmpty(_clipId) || _kfDragProperty is null)
        {
            RefreshKeyframeLane(null);
            return;
        }

        var clip = FindClip(_clipId);
        if (clip is null)
        {
            _kfDragProperty = null;
            return;
        }

        var fromTl = clip.StartFrame + _kfDragFromOffset;
        var toTl = clip.StartFrame + _kfDragLiveOffset;
        if (fromTl != toTl)
        {
            var result = KeyframeCommands.MoveKeyframe(_session, _clipId, _kfDragProperty, fromTl, toTl);
            if (result.Ok)
            {
                ClipEdited?.Invoke(this, EventArgs.Empty);
                SeekPlayheadRequested?.Invoke(this, toTl);
            }
        }
        else
        {
            // Tapping a diamond without dragging jumps the playhead straight to that keyframe.
            SeekPlayheadRequested?.Invoke(this, fromTl);
        }

        _kfDragProperty = null;
        if (!string.IsNullOrEmpty(_clipId))
        {
            var c = FindClip(_clipId);
            RefreshKeyframeStatus(c);
            RefreshKeyframeLane(c);
        }
    }
    // --- WP-82 keyframes ---

    private void OpacityKfStamp_Click(object sender, RoutedEventArgs e)
        => RunKeyframe("opacity", OpacityKfStatus);

    private void OpacityKfPrev_Click(object sender, RoutedEventArgs e)
        => NavKeyframe("opacity", previous: true);

    private void OpacityKfNext_Click(object sender, RoutedEventArgs e)
        => NavKeyframe("opacity", previous: false);

    private void VolumeKfStamp_Click(object sender, RoutedEventArgs e)
        => RunKeyframe("volume", VolumeKfStatus);

    private void VolumeKfPrev_Click(object sender, RoutedEventArgs e)
        => NavKeyframe("volume", previous: true);

    private void VolumeKfNext_Click(object sender, RoutedEventArgs e)
        => NavKeyframe("volume", previous: false);

    private void ScaleKfStamp_Click(object sender, RoutedEventArgs e)
        => RunKeyframe("scale", ScaleKfStatus);

    private void ScaleKfPrev_Click(object sender, RoutedEventArgs e)
        => NavKeyframe("scale", previous: true);

    private void ScaleKfNext_Click(object sender, RoutedEventArgs e)
        => NavKeyframe("scale", previous: false);

    private void PositionKfStamp_Click(object sender, RoutedEventArgs e)
        => RunKeyframe("position", PositionKfStatus);

    private void PositionKfPrev_Click(object sender, RoutedEventArgs e)
        => NavKeyframe("position", previous: true);

    private void PositionKfNext_Click(object sender, RoutedEventArgs e)
        => NavKeyframe("position", previous: false);

    private void RotationKfStamp_Click(object sender, RoutedEventArgs e)
        => RunKeyframe("rotation", RotationKfStatus);

    private void RotationKfPrev_Click(object sender, RoutedEventArgs e)
        => NavKeyframe("rotation", previous: true);

    private void RotationKfNext_Click(object sender, RoutedEventArgs e)
        => NavKeyframe("rotation", previous: false);

    private void CropKfStamp_Click(object sender, RoutedEventArgs e)
        => RunKeyframe("crop", CropKfStatus);

    private void CropKfPrev_Click(object sender, RoutedEventArgs e)
        => NavKeyframe("crop", previous: true);

    private void CropKfNext_Click(object sender, RoutedEventArgs e)
        => NavKeyframe("crop", previous: false);

    private void RunKeyframe(string property, TextBlock statusTarget)
    {
        if (_suppress || _session is null || string.IsNullOrEmpty(_clipId) || _clipIds.Count != 1) return;
        var targetId = KeyframeTargetClipId(property);
        if (string.IsNullOrEmpty(targetId)) return;

        var hadKeyBeforeCommit = FindClip(targetId) is { } beforeCommitClip
            && KeyframeCommands.HasKeyframeAt(beforeCommitClip, property, PlayheadFrame);

        // Flush pending typing into the live edit without closing it yet so typing a value on an
        // unkeyframed property and clicking ◆ lands as ONE atomic undo step and ONE preview refresh.
        FlushPendingNumericFieldText();

        // If committing pending typing in a numeric field just auto-created this keyframe (either
        // inline above because AllowFocusOnInteraction=False prevented blur, or on LostFocus moments
        // ago when focus moved to this ◆ button), keep that keyframe instead of toggle-deleting it.
        var targetAfterCommit = FindClip(targetId);
        var hasKeyAfterCommit = targetAfterCommit is not null
            && KeyframeCommands.HasKeyframeAt(targetAfterCommit, property, PlayheadFrame);
        var autoStampedInline = !hadKeyBeforeCommit && hasKeyAfterCommit;
        var autoStampedOnBlur = _recentAutoStamped is { } recent
            && string.Equals(recent.Property, property, StringComparison.OrdinalIgnoreCase)
            && recent.Frame == PlayheadFrame
            && Environment.TickCount64 - recent.Ticks < 750
            && hasKeyAfterCommit;
        _recentAutoStamped = null;
        if (autoStampedInline || autoStampedOnBlur)
        {
            EndLiveEdit(commit: true);
            var keptClip = FindClip(_clipId);
            if (keptClip is not null)
            {
                RefreshKeyframeStatus(keptClip);
                if (IsKeyframesSectionVisible)
                    KeyframeGraph?.Refresh();
            }
            return;
        }

        EditResult result;
        if (_live.IsActive)
        {
            result = EditResult.Fail($"Clip not found: {targetId}");
            _live.Mutate(() =>
            {
                var liveTarget = FindClip(targetId);
                if (liveTarget is not null)
                    result = KeyframeCommands.ToggleStampInPlace(liveTarget, property, PlayheadFrame);
            });
            EndLiveEdit(commit: true);
            if (!result.Ok)
            {
                statusTarget.Text = result.Error ?? "Keyframe failed";
                return;
            }
        }
        else
        {
            result = KeyframeCommands.ToggleStamp(_session, targetId, property, PlayheadFrame);
            if (!result.Ok)
            {
                statusTarget.Text = result.Error ?? "Keyframe failed";
                return;
            }
            ClipEdited?.Invoke(this, EventArgs.Empty);
        }

        var clip = FindClip(_clipId);
        if (clip is not null)
        {
            _suppress = true;
            try
            {
                RefreshAnimatableFields(clip, [clip], multi: false);
            }
            finally
            {
                _suppress = false;
            }
            RefreshKeyframeStatus(clip);
            if (IsKeyframesSectionVisible)
                KeyframeGraph?.Refresh();
        }
    }

    private void NavKeyframe(string property, bool previous)
    {
        if (_session is null || string.IsNullOrEmpty(_clipId)) return;
        CommitPendingNumericFields(raiseClipEdited: true);
        var targetId = KeyframeTargetClipId(property);
        var targetClip = string.IsNullOrEmpty(targetId) ? null : FindClip(targetId);
        if (targetClip is null) return;
        var frame = previous
            ? KeyframeCommands.PreviousTimelineFrame(targetClip, property, PlayheadFrame)
            : KeyframeCommands.NextTimelineFrame(targetClip, property, PlayheadFrame);
        if (frame is { } f)
            SeekPlayheadRequested?.Invoke(this, f);
        var primary = FindClip(_clipId);
        if (primary is not null) RefreshKeyframeStatus(primary);
    }
}
