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
    private void FillClipFields(ClipModel clip, bool multi)
    {
        _suppress = true;
        ClipTitle.Text = multi ? $"{_clipIds.Count} clips" : ClipPresentation.Title(clip, _session?.Manifest);
        ClipTypeLabel.Text = multi
            ? $"batch · focused {clip.MediaType}"
            : _pairAudioId is not null ? $"{clip.MediaType} · A/V linked" : clip.MediaType.ToString();
        ToolTipService.SetToolTip(ClipTypeLabel, $"Clip id {clip.Id}"); // WP-183: the id is for tools, not the header

        StartBox.IsEnabled = !multi;
        DurationBox.IsEnabled = !multi;
        foreach (var button in new[] { OpacityKfStampBtn, ScaleKfStampBtn, PositionKfStampBtn, RotationKfStampBtn, CropKfStampBtn })
            button.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        OpacityKfRow.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        ScaleKfRow.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        PositionKfRow.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        RotationKfRow.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        CropKfRow.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;
        KeyframeLaneHost.Visibility = multi ? Visibility.Collapsed : Visibility.Visible;

        var clips = multi
            ? _clipIds.Select(FindClip).OfType<ClipModel>().ToList()
            : [clip];
        if (clips.Count == 0) clips = [clip];

        var canvasW = Math.Max(1, _session?.ActiveTimeline.Width ?? 1920);
        var canvasH = Math.Max(1, _session?.ActiveTimeline.Height ?? 1080);
        PosXBox.Spec = CenterSpec with { Default = Math.Round(canvasW / 2.0) };
        PosYBox.Spec = CenterSpec with { Default = Math.Round(canvasH / 2.0) };
        BindNumeric(StartBox, clip.StartFrame, clips, c => c.StartFrame);
        BindNumeric(DurationBox, clip.DurationFrames, clips, c => c.DurationFrames);
        RefreshAnimatableFields(clip, clips, multi);
        BindNumeric(SpeedBox, clip.Speed, clips, c => c.Speed);
        FlipHBtn.IsChecked = clip.Transform.FlipHorizontal;
        FlipVBtn.IsChecked = clip.Transform.FlipVertical;
        var blendTags = clips.Select(c => string.IsNullOrEmpty(c.BlendMode) ? "normal" : c.BlendMode).ToList();
        if (clips.Count > 1 && !MixedValue.TryShared(blendTags, StringComparer.OrdinalIgnoreCase, out _))
            BlendModeBox.SelectedIndex = -1;
        else
            SelectBlend(clip.BlendMode);

        var fps = Math.Max(1, _session?.ActiveTimeline.Fps ?? 30);
        TimingTimecodeText.Text = multi ? "" :
            $"{TimelineGeometry.FormatTimecodeFull(clip.StartFrame, fps)} → {TimelineGeometry.FormatTimecodeFull(clip.StartFrame + clip.DurationFrames, fps)}";

        // WP-183: for a linked pair the Levels rows read and edit the audio partner.
        var levels = _pairAudioId is { } partnerId && FindClip(partnerId) is { } partner ? partner : clip;
        var volumeClips = multi ? clips : [levels];
        BindNumeric(FadeInBox, levels.FadeInFrames / (double)fps, volumeClips, c => c.FadeInFrames / (double)fps);
        BindNumeric(FadeOutBox, levels.FadeOutFrames / (double)fps, volumeClips, c => c.FadeOutFrames / (double)fps);
        var visual = clips.Where(c => InspectorClipTabs.HasVideoTab(c.MediaType)).ToList();
        if (visual.Count == 0) visual = [clip];
        BindNumeric(VideoFadeInBox, clip.FadeInFrames / (double)fps, visual, c => c.FadeInFrames / (double)fps);
        BindNumeric(VideoFadeOutBox, clip.FadeOutFrames / (double)fps, visual, c => c.FadeOutFrames / (double)fps);
        DenoiseSwitch.IsOn = levels.Effects?.Any(e => e.Type == ClipModel.DenoiseEffectType && e.Enabled) == true;

        LoadTextFields(clip, clips);
        LoadGradeFields(clip, clips);
        PopulateEffects(clip, multi);
        LoadMulticamFields(clip);
        RebuildAvailableTabs(clip);
        ApplyTabVisibility();
        RefreshKeyframeStatus(clip);
        if (!multi)
            KeyframeGraph?.Bind(_session, clip.Id, PlayheadFrame, _pairAudioId);
        else
            KeyframeGraph?.Bind(null, null, PlayheadFrame);
        _suppress = false;
    }

    private void RefreshAnimatableFields(ClipModel clip, IReadOnlyList<ClipModel> clips, bool multi)
    {
        var frame = PlayheadFrame;
        var canvasW = Math.Max(1, _session?.ActiveTimeline.Width ?? 1920);
        var canvasH = Math.Max(1, _session?.ActiveTimeline.Height ?? 1080);

        // WP-183: every value carries the unit a user thinks in — percent, degrees, canvas pixels.
        // Sample at PlayheadFrame so keyframed clips always display their live value at the playhead.
        BindNumeric(OpacityBox, clip.RawOpacityAt(frame) * 100, clips, c => c.RawOpacityAt(frame) * 100);
        BindNumeric(ScaleBox, ScalePercentAt(clip, frame), clips, c => ScalePercentAt(c, frame));
        var sampledTransform = clip.TransformAt(frame);
        BindNumeric(PosXBox, sampledTransform.CenterX * canvasW, clips, c => c.TransformAt(frame).CenterX * canvasW);
        BindNumeric(PosYBox, sampledTransform.CenterY * canvasH, clips, c => c.TransformAt(frame).CenterY * canvasH);
        BindNumeric(RotBox, clip.RotationAt(frame), clips, c => c.RotationAt(frame));

        var sampledCrop = clip.CropAt(frame);
        BindNumeric(CropLBox, sampledCrop.Left * 100, clips, c => c.CropAt(frame).Left * 100);
        BindNumeric(CropTBox, sampledCrop.Top * 100, clips, c => c.CropAt(frame).Top * 100);
        BindNumeric(CropRBox, sampledCrop.Right * 100, clips, c => c.CropAt(frame).Right * 100);
        BindNumeric(CropBBox, sampledCrop.Bottom * 100, clips, c => c.CropAt(frame).Bottom * 100);

        var levels = _pairAudioId is { } partnerId && FindClip(partnerId) is { } partner ? partner : clip;
        var volumeClips = multi ? clips : [levels];
        double VolumeDisplayDb(ClipModel c)
            => KeyframeCommands.LiveVolumeKfDb(c, frame) ?? VolumeScale.DbFromLinear(c.Volume);
        BindNumeric(VolumeDbBox, VolumeDisplayDb(levels), volumeClips, VolumeDisplayDb);
        UpdateVolumeLinearLabel(levels, VolumeDbBox.IsMixed, frame);
    }

    private void UpdateVolumeLinearLabel(ClipModel levels, bool isMixed, int frame)
    {
        if (isMixed)
        {
            VolumeLinearLabel.Text = "Linear: " + MixedValue.Marker;
            return;
        }
        if (KeyframeCommands.LiveVolumeKfDb(levels, frame) is { } kfDb)
        {
            var effective = levels.Volume * VolumeScale.LinearFromDb(kfDb);
            VolumeLinearLabel.Text = VolumeScale.IsMuted(effective)
                ? "Linear: 0 (−∞ dB · keyframed)"
                : $"Linear: {effective:0.###} (keyframed)";
            return;
        }
        VolumeLinearLabel.Text = VolumeScale.IsMuted(levels.Volume)
            ? "Linear: 0 (−∞ dB)"
            : $"Linear: {levels.Volume:0.###}";
    }

    private void KeyframeGraph_Edited(object? sender, EventArgs e)
    {
        ClipEdited?.Invoke(this, EventArgs.Empty);
        if (!string.IsNullOrEmpty(_clipId) && FindClip(_clipId) is { } c)
        {
            _suppress = true;
            try
            {
                RefreshAnimatableFields(c, [c], multi: false);
            }
            finally
            {
                _suppress = false;
            }
            RefreshKeyframeStatus(c);
        }
    }

    private void KeyframeGraph_Seek(object? sender, int frame)
        => SeekPlayheadRequested?.Invoke(this, frame);

    private void LoadMulticamFields(ClipModel clip)
    {
        var group = string.IsNullOrEmpty(clip.MulticamGroupId) || _session is null
            ? null
            : MulticamCommands.FindGroup(_session, clip.MulticamGroupId);
        if (group is null)
        {
            MulticamGroupNameLabel.Text = "";
            MulticamCurrentAngleLabel.Text = "";
            MulticamMembersLabel.Text = "";
            MulticamAngleBox.Items.Clear();
            MulticamSwitchBtn.IsEnabled = false;
            return;
        }

        MulticamGroupNameLabel.Text = string.IsNullOrWhiteSpace(group.Name) ? group.Id : group.Name;
        var current = group.MemberByMediaRef(clip.MediaRef);
        MulticamCurrentAngleLabel.Text = current is null
            ? $"Media: {clip.MediaRef}"
            : $"Current angle: {current.AngleLabel} ({current.Kind})";

        var lines = group.Members.Select(m =>
        {
            var master = m.Id == group.MasterMemberId ? " · master" : "";
            var conf = m.Sync.Locked ? "locked" : $"conf {m.Sync.Confidence:0.###}";
            return $"• {m.AngleLabel} [{m.Kind}]  offset {m.Sync.OffsetSeconds:0.###}s ({conf}){master}";
        });
        MulticamMembersLabel.Text = string.Join("\n", lines);

        MulticamAngleBox.Items.Clear();
        foreach (var angle in group.Angles)
        {
            MulticamAngleBox.Items.Add(new ComboBoxItem
            {
                Content = angle.AngleLabel,
                Tag = angle.AngleLabel,
                IsEnabled = angle.MediaRef != clip.MediaRef,
            });
        }
        // Prefer first different angle
        var pick = -1;
        for (var i = 0; i < MulticamAngleBox.Items.Count; i++)
        {
            if (MulticamAngleBox.Items[i] is ComboBoxItem { IsEnabled: true })
            {
                pick = i;
                break;
            }
        }
        MulticamAngleBox.SelectedIndex = pick >= 0 ? pick : (MulticamAngleBox.Items.Count > 0 ? 0 : -1);
        MulticamSwitchBtn.IsEnabled = MulticamAngleBox.SelectedItem is ComboBoxItem { Tag: string t }
            && !string.Equals(t, current?.AngleLabel, StringComparison.OrdinalIgnoreCase);
    }

    private void LoadTextFields(ClipModel clip, IReadOnlyList<ClipModel> clips)
    {
        TextContentBox.Text = clip.TextContent ?? "";
        // WP-201: the style rows are the shared control; LoadTextFields only feeds it the clip's
        // style (or a fresh default) and the animate rows below keep their own widgets.
        var style = ReadTextStyle(clip) ?? new TextStyle();
        TextStyleRows.Defaults = new TextStyle();
        TextStyleRows.Value = style;

        static string AnimPreset(ClipModel c)
        {
            var preset = ReadTextAnimation(c)?.Preset ?? "off";
            if (string.IsNullOrEmpty(preset) || preset == "none") preset = "off";
            return preset;
        }

        var presets = clips.Select(AnimPreset).ToList();
        if (clips.Count > 1 && !MixedValue.TryShared(presets, StringComparer.OrdinalIgnoreCase, out _))
            TextAnimationBox.SelectedIndex = -1;
        else
            SelectAnimationPreset(AnimPreset(clip));
        BindNumeric(
            TextAnimPerWordBox,
            ReadTextAnimation(clip)?.PerWordFrames > 0 ? ReadTextAnimation(clip)!.PerWordFrames : 6,
            clips,
            c => ReadTextAnimation(c)?.PerWordFrames > 0 ? ReadTextAnimation(c)!.PerWordFrames : 6);
    }

    private static TextAnimation? ReadTextAnimation(Clip clip)
    {
        if (clip.TextAnimation is not { } el) return null;
        try
        {
            return JsonSerializer.Deserialize<TextAnimation>(el.GetRawText(), JsonOptions.ForProject);
        }
        catch
        {
            return null;
        }
    }

    private void SelectAnimationPreset(string preset)
    {
        for (var i = 0; i < TextAnimationBox.Items.Count; i++)
        {
            if (TextAnimationBox.Items[i] is ComboBoxItem { Tag: string t }
                && string.Equals(t, preset, StringComparison.OrdinalIgnoreCase))
            {
                TextAnimationBox.SelectedIndex = i;
                return;
            }
        }
        TextAnimationBox.SelectedIndex = 0;
    }

    private void TextAnimation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (TextAnimationBox.SelectedIndex < 0) return;
        CommitTextPatch(includeContent: false, includeAnimation: true);
    }
}
