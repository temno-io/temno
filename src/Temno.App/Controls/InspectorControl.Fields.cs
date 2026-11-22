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
    private static UnitFieldSpec StartSpec => new() { Unit = " fr", Minimum = 0, Maximum = 1_000_000, Step = 1, Decimals = 0, UnitsPerPixel = 1, Default = 0 };
    private static UnitFieldSpec DurationSpec => new() { Unit = " fr", Minimum = 1, Maximum = 1_000_000, Step = 1, Decimals = 0, UnitsPerPixel = 1, Default = 30 };
    // WP-183: display units. The model keeps normalised values; the fields show % and canvas px.
    private static UnitFieldSpec OpacitySpec => new() { Unit = "%", Minimum = 0, Maximum = 100, Step = 1, Decimals = 0, UnitsPerPixel = 1, Default = 100 };
    private static UnitFieldSpec ScaleSpec => new() { Unit = "%", Minimum = 5, Maximum = 400, Step = 1, Decimals = 0, UnitsPerPixel = 2, Default = 100 };
    private static UnitFieldSpec CenterSpec => new() { Unit = " px", Minimum = -8192, Maximum = 16384, Step = 1, Decimals = 0, UnitsPerPixel = 1, Default = 0 };
    private static UnitFieldSpec RotationSpec => new() { Unit = "\u00B0", Minimum = -360, Maximum = 360, Step = 1, Decimals = 0, UnitsPerPixel = 0.5, Default = 0 };
    private static UnitFieldSpec SpeedSpec => new() { Unit = "\u00D7", Minimum = 0.25, Maximum = 4, Step = 0.05, Decimals = 2, UnitsPerPixel = 0.02, Default = 1 };
    private static UnitFieldSpec CropSpec => new() { Unit = "%", Minimum = 0, Maximum = 100, Step = 1, Decimals = 0, UnitsPerPixel = 0.5, Default = 0 };
    private static UnitFieldSpec ExposureSpec => new() { Unit = " EV", Minimum = -3, Maximum = 3, Step = 0.05, Decimals = 2, UnitsPerPixel = 0.02, Default = 0 };
    private static UnitFieldSpec ContrastSpec => new() { Minimum = 0.5, Maximum = 1.5, Step = 0.01, Decimals = 2, UnitsPerPixel = 0.01, Default = 1 };
    private static UnitFieldSpec SaturationSpec => new() { Minimum = 0, Maximum = 2, Step = 0.05, Decimals = 2, UnitsPerPixel = 0.02, Default = 1 };
    private static UnitFieldSpec TemperatureSpec => new() { Unit = " K", Minimum = 2000, Maximum = 11000, Step = 50, Decimals = 0, UnitsPerPixel = 10, Default = ColorTemperature.NeutralKelvin };
    private static UnitFieldSpec TintSpec => new() { Minimum = -100, Maximum = 100, Step = 1, Decimals = 0, UnitsPerPixel = 0.5, Default = 0 };
    private static UnitFieldSpec ToneSpec => new() { Minimum = -1, Maximum = 1, Step = 0.01, Decimals = 2, UnitsPerPixel = 0.005, Default = 0 };
    private static UnitFieldSpec VibranceSpec => new() { Minimum = -1, Maximum = 1, Step = 0.01, Decimals = 2, UnitsPerPixel = 0.005, Default = 0 };
    private static UnitFieldSpec LiftMSpec => new() { Minimum = -0.5, Maximum = 0.5, Step = 0.01, Decimals = 2, UnitsPerPixel = 0.005, Default = 0 };
    private static UnitFieldSpec GammaMSpec => new() { Minimum = 0.5, Maximum = 2, Step = 0.01, Decimals = 2, UnitsPerPixel = 0.01, Default = 1 };
    private static UnitFieldSpec GainMSpec => new() { Minimum = 0.5, Maximum = 1.5, Step = 0.01, Decimals = 2, UnitsPerPixel = 0.01, Default = 1 };
    private static UnitFieldSpec LutIntensitySpec => new() { Minimum = 0, Maximum = 1, Step = 0.05, Decimals = 2, UnitsPerPixel = 0.01, Default = 1 };
    private static UnitFieldSpec VolumeDbSpec => new() { Unit = " dB", Minimum = -60, Maximum = 15, Step = 0.5, Decimals = 1, UnitsPerPixel = 0.2, Default = 0 };
    private static UnitFieldSpec FadeSpec => new() { Unit = " s", Minimum = 0, Maximum = 60, Step = 0.05, Decimals = 2, UnitsPerPixel = 0.02, Default = 0 };
    private static UnitFieldSpec PerWordSpec => new() { Unit = " fr", Minimum = 1, Maximum = 60, Step = 1, Decimals = 0, UnitsPerPixel = 0.5, Default = 6 };

    private void InitializeFieldSpecs()
    {
        StartBox.Spec = StartSpec;
        DurationBox.Spec = DurationSpec;
        OpacityBox.Spec = OpacitySpec;
        ScaleBox.Spec = ScaleSpec;
        PosXBox.Spec = CenterSpec;
        PosYBox.Spec = CenterSpec;
        RotBox.Spec = RotationSpec;
        SpeedBox.Spec = SpeedSpec;
        CropLBox.Spec = CropSpec;
        CropTBox.Spec = CropSpec;
        CropRBox.Spec = CropSpec;
        CropBBox.Spec = CropSpec;
        GradeExposureBox.Spec = ExposureSpec;
        GradeContrastBox.Spec = ContrastSpec;
        GradeSaturationBox.Spec = SaturationSpec;
        GradeTemperatureBox.Spec = TemperatureSpec;
        GradeTintBox.Spec = TintSpec;
        GradeHighlightsBox.Spec = ToneSpec;
        GradeShadowsBox.Spec = ToneSpec;
        GradeBlacksBox.Spec = ToneSpec;
        GradeWhitesBox.Spec = ToneSpec;
        GradeVibranceBox.Spec = VibranceSpec;
        GradeLiftMBox.Spec = LiftMSpec;
        GradeGammaMBox.Spec = GammaMSpec;
        GradeGainMBox.Spec = GainMSpec;
        GradeLutIntensityBox.Spec = LutIntensitySpec;
        VolumeDbBox.Spec = VolumeDbSpec;
        FadeInBox.Spec = FadeSpec;
        FadeOutBox.Spec = FadeSpec;
        VideoFadeInBox.Spec = FadeSpec;
        VideoFadeOutBox.Spec = FadeSpec;
        TextAnimPerWordBox.Spec = PerWordSpec;
    }

    private void WireFieldEvents()
    {
        StartBox.ValueChanged += Numeric_ValueChanged;
        DurationBox.ValueChanged += Numeric_ValueChanged;
        OpacityBox.ValueChanged += Numeric_ValueChanged;
        ScaleBox.ValueChanged += Numeric_ValueChanged;
        PosXBox.ValueChanged += Numeric_ValueChanged;
        PosYBox.ValueChanged += Numeric_ValueChanged;
        RotBox.ValueChanged += Numeric_ValueChanged;
        SpeedBox.ValueChanged += Numeric_ValueChanged;
        CropLBox.ValueChanged += Numeric_ValueChanged;
        CropTBox.ValueChanged += Numeric_ValueChanged;
        CropRBox.ValueChanged += Numeric_ValueChanged;
        CropBBox.ValueChanged += Numeric_ValueChanged;
        VolumeDbBox.ValueChanged += Numeric_ValueChanged;
        FadeInBox.ValueChanged += Numeric_ValueChanged;
        FadeOutBox.ValueChanged += Numeric_ValueChanged;
        VideoFadeInBox.ValueChanged += Numeric_ValueChanged;
        VideoFadeOutBox.ValueChanged += Numeric_ValueChanged;
        GradeExposureBox.ValueChanged += Grade_ValueChanged;
        GradeContrastBox.ValueChanged += Grade_ValueChanged;
        GradeSaturationBox.ValueChanged += Grade_ValueChanged;
        GradeTemperatureBox.ValueChanged += Grade_ValueChanged;
        GradeTintBox.ValueChanged += Grade_ValueChanged;
        GradeHighlightsBox.ValueChanged += Grade_ValueChanged;
        GradeShadowsBox.ValueChanged += Grade_ValueChanged;
        GradeBlacksBox.ValueChanged += Grade_ValueChanged;
        GradeWhitesBox.ValueChanged += Grade_ValueChanged;
        GradeVibranceBox.ValueChanged += Grade_ValueChanged;
        GradeLiftMBox.ValueChanged += Grade_ValueChanged;
        GradeGammaMBox.ValueChanged += Grade_ValueChanged;
        GradeGainMBox.ValueChanged += Grade_ValueChanged;
        GradeLutIntensityBox.ValueChanged += Grade_ValueChanged;
        // WP-269: a scrub drag raises ValueChanged per tick but never takes focus, so the
        // live gesture would stay open without this. ScrubCompleted closes it as one undo step.
        StartBox.ScrubCompleted += Numeric_ScrubCompleted;
        DurationBox.ScrubCompleted += Numeric_ScrubCompleted;
        OpacityBox.ScrubCompleted += Numeric_ScrubCompleted;
        ScaleBox.ScrubCompleted += Numeric_ScrubCompleted;
        PosXBox.ScrubCompleted += Numeric_ScrubCompleted;
        PosYBox.ScrubCompleted += Numeric_ScrubCompleted;
        RotBox.ScrubCompleted += Numeric_ScrubCompleted;
        SpeedBox.ScrubCompleted += Numeric_ScrubCompleted;
        CropLBox.ScrubCompleted += Numeric_ScrubCompleted;
        CropTBox.ScrubCompleted += Numeric_ScrubCompleted;
        CropRBox.ScrubCompleted += Numeric_ScrubCompleted;
        CropBBox.ScrubCompleted += Numeric_ScrubCompleted;
        VolumeDbBox.ScrubCompleted += Numeric_ScrubCompleted;
        FadeInBox.ScrubCompleted += Numeric_ScrubCompleted;
        FadeOutBox.ScrubCompleted += Numeric_ScrubCompleted;
        VideoFadeInBox.ScrubCompleted += Numeric_ScrubCompleted;
        VideoFadeOutBox.ScrubCompleted += Numeric_ScrubCompleted;
        GradeExposureBox.ScrubCompleted += Grade_ScrubCompleted;
        GradeContrastBox.ScrubCompleted += Grade_ScrubCompleted;
        GradeSaturationBox.ScrubCompleted += Grade_ScrubCompleted;
        GradeTemperatureBox.ScrubCompleted += Grade_ScrubCompleted;
        GradeTintBox.ScrubCompleted += Grade_ScrubCompleted;
        GradeHighlightsBox.ScrubCompleted += Grade_ScrubCompleted;
        GradeShadowsBox.ScrubCompleted += Grade_ScrubCompleted;
        GradeBlacksBox.ScrubCompleted += Grade_ScrubCompleted;
        GradeWhitesBox.ScrubCompleted += Grade_ScrubCompleted;
        GradeVibranceBox.ScrubCompleted += Grade_ScrubCompleted;
        GradeLiftMBox.ScrubCompleted += Grade_ScrubCompleted;
        GradeGammaMBox.ScrubCompleted += Grade_ScrubCompleted;
        GradeGainMBox.ScrubCompleted += Grade_ScrubCompleted;
        GradeLutIntensityBox.ScrubCompleted += Grade_ScrubCompleted;
        TextAnimPerWordBox.ScrubCompleted += Numeric_ScrubCompleted;
        StartBox.LostFocus += Numeric_LostFocus;
        StartBox.CommitRequested += Numeric_CommitRequested;
        DurationBox.LostFocus += Numeric_LostFocus;
        DurationBox.CommitRequested += Numeric_CommitRequested;
        OpacityBox.LostFocus += Numeric_LostFocus;
        OpacityBox.CommitRequested += Numeric_CommitRequested;
        ScaleBox.LostFocus += Numeric_LostFocus;
        ScaleBox.CommitRequested += Numeric_CommitRequested;
        PosXBox.LostFocus += Numeric_LostFocus;
        PosXBox.CommitRequested += Numeric_CommitRequested;
        PosYBox.LostFocus += Numeric_LostFocus;
        PosYBox.CommitRequested += Numeric_CommitRequested;
        RotBox.LostFocus += Numeric_LostFocus;
        RotBox.CommitRequested += Numeric_CommitRequested;
        SpeedBox.LostFocus += Numeric_LostFocus;
        SpeedBox.CommitRequested += Numeric_CommitRequested;
        CropLBox.LostFocus += Numeric_LostFocus;
        CropLBox.CommitRequested += Numeric_CommitRequested;
        CropTBox.LostFocus += Numeric_LostFocus;
        CropTBox.CommitRequested += Numeric_CommitRequested;
        CropRBox.LostFocus += Numeric_LostFocus;
        CropRBox.CommitRequested += Numeric_CommitRequested;
        CropBBox.LostFocus += Numeric_LostFocus;
        CropBBox.CommitRequested += Numeric_CommitRequested;
        VolumeDbBox.LostFocus += Numeric_LostFocus;
        VolumeDbBox.CommitRequested += Numeric_CommitRequested;
        FadeInBox.LostFocus += Numeric_LostFocus;
        FadeInBox.CommitRequested += Numeric_CommitRequested;
        FadeOutBox.LostFocus += Numeric_LostFocus;
        FadeOutBox.CommitRequested += Numeric_CommitRequested;
        VideoFadeInBox.LostFocus += Numeric_LostFocus;
        VideoFadeInBox.CommitRequested += Numeric_CommitRequested;
        VideoFadeOutBox.LostFocus += Numeric_LostFocus;
        VideoFadeOutBox.CommitRequested += Numeric_CommitRequested;
        GradeExposureBox.LostFocus += Grade_LostFocus;
        GradeExposureBox.CommitRequested += Grade_LostFocus;
        GradeContrastBox.LostFocus += Grade_LostFocus;
        GradeContrastBox.CommitRequested += Grade_LostFocus;
        GradeSaturationBox.LostFocus += Grade_LostFocus;
        GradeSaturationBox.CommitRequested += Grade_LostFocus;
        GradeTemperatureBox.LostFocus += Grade_LostFocus;
        GradeTemperatureBox.CommitRequested += Grade_LostFocus;
        GradeTintBox.LostFocus += Grade_LostFocus;
        GradeTintBox.CommitRequested += Grade_LostFocus;
        GradeHighlightsBox.LostFocus += Grade_LostFocus;
        GradeHighlightsBox.CommitRequested += Grade_LostFocus;
        GradeShadowsBox.LostFocus += Grade_LostFocus;
        GradeShadowsBox.CommitRequested += Grade_LostFocus;
        GradeBlacksBox.LostFocus += Grade_LostFocus;
        GradeBlacksBox.CommitRequested += Grade_LostFocus;
        GradeWhitesBox.LostFocus += Grade_LostFocus;
        GradeWhitesBox.CommitRequested += Grade_LostFocus;
        GradeVibranceBox.LostFocus += Grade_LostFocus;
        GradeVibranceBox.CommitRequested += Grade_LostFocus;
        GradeLiftMBox.LostFocus += Grade_LostFocus;
        GradeLiftMBox.CommitRequested += Grade_LostFocus;
        GradeGammaMBox.LostFocus += Grade_LostFocus;
        GradeGammaMBox.CommitRequested += Grade_LostFocus;
        GradeGainMBox.LostFocus += Grade_LostFocus;
        GradeGainMBox.CommitRequested += Grade_LostFocus;
        GradeLutIntensityBox.LostFocus += Grade_LostFocus;
        GradeLutIntensityBox.CommitRequested += Grade_LostFocus;
        TextAnimPerWordBox.LostFocus += TextFields_LostFocus;
        TextAnimPerWordBox.CommitRequested += TextFields_LostFocus;
        StartRow.ResetRequested += (_, _) => { StartBox.ResetToDefault(); EndLiveEdit(commit: true); };
        // A clip duration has no universal default; editing it stays explicit and undoable.
        OpacityRow.ResetRequested += (_, _) => { OpacityBox.ResetToDefault(); EndLiveEdit(commit: true); };
        ScaleRow.ResetRequested += (_, _) => { ScaleBox.ResetToDefault(); EndLiveEdit(commit: true); };
        PosXRow.ResetRequested += (_, _) => { PosXBox.ResetToDefault(); EndLiveEdit(commit: true); };
        PosYRow.ResetRequested += (_, _) => { PosYBox.ResetToDefault(); EndLiveEdit(commit: true); };
        RotationRow.ResetRequested += (_, _) => { RotBox.ResetToDefault(); EndLiveEdit(commit: true); };
        SpeedRow.ResetRequested += (_, _) => { SpeedBox.ResetToDefault(); EndLiveEdit(commit: true); };
        CropLRow.ResetRequested += (_, _) => { CropLBox.ResetToDefault(); EndLiveEdit(commit: true); };
        CropTRow.ResetRequested += (_, _) => { CropTBox.ResetToDefault(); EndLiveEdit(commit: true); };
        CropRRow.ResetRequested += (_, _) => { CropRBox.ResetToDefault(); EndLiveEdit(commit: true); };
        CropBRow.ResetRequested += (_, _) => { CropBBox.ResetToDefault(); EndLiveEdit(commit: true); };
        VolumeDbRow.ResetRequested += (_, _) => { VolumeDbBox.ResetToDefault(); EndLiveEdit(commit: true); };
        FadeInRow.ResetRequested += (_, _) => { FadeInBox.ResetToDefault(); EndLiveEdit(commit: true); };
        FadeOutRow.ResetRequested += (_, _) => { FadeOutBox.ResetToDefault(); EndLiveEdit(commit: true); };
        VideoFadeInRow.ResetRequested += (_, _) => { VideoFadeInBox.ResetToDefault(); EndLiveEdit(commit: true); };
        VideoFadeOutRow.ResetRequested += (_, _) => { VideoFadeOutBox.ResetToDefault(); EndLiveEdit(commit: true); };
        TextAnimPerWordRow.ResetRequested += (_, _) => TextAnimPerWordBox.ResetToDefault();
        GradeExposureRow.ResetRequested += (_, _) => { GradeExposureBox.ResetToDefault(); EndLiveEdit(commit: true); };
        GradeContrastRow.ResetRequested += (_, _) => { GradeContrastBox.ResetToDefault(); EndLiveEdit(commit: true); };
        GradeSaturationRow.ResetRequested += (_, _) => { GradeSaturationBox.ResetToDefault(); EndLiveEdit(commit: true); };
        GradeTemperatureRow.ResetRequested += (_, _) => { GradeTemperatureBox.ResetToDefault(); EndLiveEdit(commit: true); };
        GradeTintRow.ResetRequested += (_, _) => { GradeTintBox.ResetToDefault(); EndLiveEdit(commit: true); };
        GradeHighlightsRow.ResetRequested += (_, _) => { GradeHighlightsBox.ResetToDefault(); EndLiveEdit(commit: true); };
        GradeShadowsRow.ResetRequested += (_, _) => { GradeShadowsBox.ResetToDefault(); EndLiveEdit(commit: true); };
        GradeBlacksRow.ResetRequested += (_, _) => { GradeBlacksBox.ResetToDefault(); EndLiveEdit(commit: true); };
        GradeWhitesRow.ResetRequested += (_, _) => { GradeWhitesBox.ResetToDefault(); EndLiveEdit(commit: true); };
        GradeVibranceRow.ResetRequested += (_, _) => { GradeVibranceBox.ResetToDefault(); EndLiveEdit(commit: true); };
        GradeLiftMRow.ResetRequested += (_, _) => { GradeLiftMBox.ResetToDefault(); EndLiveEdit(commit: true); };
        GradeGammaMRow.ResetRequested += (_, _) => { GradeGammaMBox.ResetToDefault(); EndLiveEdit(commit: true); };
        GradeGainMRow.ResetRequested += (_, _) => { GradeGainMBox.ResetToDefault(); EndLiveEdit(commit: true); };
        GradeLutIntensityRow.ResetRequested += (_, _) => { GradeLutIntensityBox.ResetToDefault(); EndLiveEdit(commit: true); };
        TextContentRow.ResetRequested += (_, _) => { TextContentBox.Text = ""; CommitTextPatch(includeContent: true, includeAnimation: false); };
        // WP-201: discrete shared-style edits commit at once; numeric live edits (scrub, typing)
        // commit on the control's LostFocus request, like the old field-level LostFocus wiring.
        TextStyleRows.StyleChanged += (_, e) =>
        {
            if (_suppress || e.IsLiveNumeric) return;
            CommitTextPatch(includeContent: false, includeAnimation: false);
        };
        TextStyleRows.StyleCommitRequested += (_, _) =>
        {
            if (_suppress) return;
            CommitTextPatch(includeContent: false, includeAnimation: false);
        };
        void BindCanReset(UnitNumberField field, EditorRow row)
        {
            void Refresh() => row.CanReset = field.DiffersFromDefault;
            field.RegisterPropertyChangedCallback(UnitNumberField.ValueProperty, (_, _) => Refresh());
            field.RegisterPropertyChangedCallback(UnitNumberField.SpecProperty, (_, _) => Refresh());
            field.RegisterPropertyChangedCallback(UnitNumberField.IsMixedProperty, (_, _) => Refresh());
            Refresh();
        }
        BindCanReset(StartBox, StartRow);
        DurationRow.CanReset = false;
        BindCanReset(OpacityBox, OpacityRow);
        BindCanReset(ScaleBox, ScaleRow);
        BindCanReset(PosXBox, PosXRow);
        BindCanReset(PosYBox, PosYRow);
        BindCanReset(RotBox, RotationRow);
        BindCanReset(SpeedBox, SpeedRow);
        BindCanReset(CropLBox, CropLRow);
        BindCanReset(CropTBox, CropTRow);
        BindCanReset(CropRBox, CropRRow);
        BindCanReset(CropBBox, CropBRow);
        BindCanReset(VolumeDbBox, VolumeDbRow);
        BindCanReset(FadeInBox, FadeInRow);
        BindCanReset(FadeOutBox, FadeOutRow);
        BindCanReset(VideoFadeInBox, VideoFadeInRow);
        BindCanReset(VideoFadeOutBox, VideoFadeOutRow);
        BindCanReset(TextAnimPerWordBox, TextAnimPerWordRow);
        BindCanReset(GradeExposureBox, GradeExposureRow);
        BindCanReset(GradeContrastBox, GradeContrastRow);
        BindCanReset(GradeSaturationBox, GradeSaturationRow);
        BindCanReset(GradeTemperatureBox, GradeTemperatureRow);
        BindCanReset(GradeTintBox, GradeTintRow);
        BindCanReset(GradeHighlightsBox, GradeHighlightsRow);
        BindCanReset(GradeShadowsBox, GradeShadowsRow);
        BindCanReset(GradeBlacksBox, GradeBlacksRow);
        BindCanReset(GradeWhitesBox, GradeWhitesRow);
        BindCanReset(GradeVibranceBox, GradeVibranceRow);
        BindCanReset(GradeLiftMBox, GradeLiftMRow);
        BindCanReset(GradeGammaMBox, GradeGammaMRow);
        BindCanReset(GradeGainMBox, GradeGainMRow);
        BindCanReset(GradeLutIntensityBox, GradeLutIntensityRow);
    }

    private void TextFields_ValueChanged(object? sender, double _)
    {
        if (_suppress) return;
        CommitTextPatch(includeContent: false, includeAnimation: ReferenceEquals(sender, TextAnimPerWordBox));
    }

    private void SelectBlend(string? blend)
    {
        var tag = string.IsNullOrEmpty(blend) ? "normal" : blend;
        for (var i = 0; i < BlendModeBox.Items.Count; i++)
        {
            if (BlendModeBox.Items[i] is ComboBoxItem { Tag: string t } && t == tag)
            {
                BlendModeBox.SelectedIndex = i;
                return;
            }
        }
        BlendModeBox.SelectedIndex = 0;
    }

    private static void BindNumeric(
        UnitNumberField field,
        double focused,
        IReadOnlyList<ClipModel> clips,
        Func<ClipModel, double> extract)
    {
        var values = new double[clips.Count];
        for (var i = 0; i < clips.Count; i++)
            values[i] = extract(clips[i]);
        field.IsMixed = clips.Count > 1 && !MixedValue.TrySharedRounded(values, field.Spec.Decimals, out _);
        field.Value = focused;
        ToolTipService.SetToolTip(field, field.IsMixed
            ? "Values differ across the selection. Type a value to apply it to all."
            : null);
    }

    // --- Live numeric edit (WP-81) ---

    private string? KeyframePropertyForField(UnitNumberField field)
    {
        if (ReferenceEquals(field, OpacityBox)) return "opacity";
        if (ReferenceEquals(field, ScaleBox)) return "scale";
        if (ReferenceEquals(field, PosXBox) || ReferenceEquals(field, PosYBox)) return "position";
        if (ReferenceEquals(field, RotBox)) return "rotation";
        if (ReferenceEquals(field, CropLBox) || ReferenceEquals(field, CropTBox)
            || ReferenceEquals(field, CropRBox) || ReferenceEquals(field, CropBBox)) return "crop";
        if (ReferenceEquals(field, VolumeDbBox)) return "volume";
        return null;
    }

    private void FlushPendingNumericFieldText()
    {
        if (_suppress || _session is null || _clipIds.Count == 0) return;
        UnitNumberField?[] fields =
        [
            StartBox, DurationBox, OpacityBox, ScaleBox, PosXBox, PosYBox, RotBox, SpeedBox,
            CropLBox, CropTBox, CropRBox, CropBBox, VolumeDbBox, FadeInBox, FadeOutBox,
            VideoFadeInBox, VideoFadeOutBox,
        ];
        foreach (var field in fields)
            field?.CommitPendingEdit();
    }

    private bool CommitPendingNumericFields(bool raiseClipEdited)
    {
        if (_suppress || _session is null || _clipIds.Count == 0) return false;
        FlushPendingNumericFieldText();

        if (!_live.IsActive) return false;
        if (raiseClipEdited)
        {
            EndLiveEdit(commit: true);
            return true;
        }
        _live.End(keep: true);
        return true;
    }

    private void Numeric_ValueChanged(object? sender, double newValue)
    {
        if (_suppress || _session is null || _clipIds.Count == 0) return;
        if (sender is not UnitNumberField numericField || !double.IsFinite(numericField.Value)) return;

        var kfProp = KeyframePropertyForField(numericField);
        var kfTargetId = kfProp is not null && _clipIds.Count == 1 ? KeyframeTargetClipId(kfProp) : null;
        var hadKeyBefore = kfProp is not null && !string.IsNullOrEmpty(kfTargetId) && FindClip(kfTargetId) is { } beforeClip
            && KeyframeCommands.HasKeyframeAt(beforeClip, kfProp, PlayheadFrame);

        if (!_live.IsActive)
            _live.Begin(_session);

        var wroteKeyframe = false;
        _live.Mutate(() => wroteKeyframe = ApplyLiveFieldsToClip(numericField));

        if (numericField.IsCommittingFromLostFocus
            && kfProp is not null && !hadKeyBefore && !string.IsNullOrEmpty(kfTargetId) && FindClip(kfTargetId) is { } afterClip
            && KeyframeCommands.HasKeyframeAt(afterClip, kfProp, PlayheadFrame))
        {
            _recentAutoStamped = (kfProp, PlayheadFrame, Environment.TickCount64);
        }

        if (ReferenceEquals(sender, VolumeDbBox))
        {
            var targetId = _pairAudioId ?? _clipId;
            if (!string.IsNullOrEmpty(targetId) && FindClip(targetId) is { } levels)
            {
                UpdateVolumeLinearLabel(levels, isMixed: false, PlayheadFrame);
            }
            else
            {
                var linear = VolumeScale.LinearFromDb(VolumeDbBox.Value);
                VolumeLinearLabel.Text = VolumeScale.IsMuted(linear)
                    ? "Linear: 0 (−∞ dB)"
                    : $"Linear: {linear:0.###}";
            }
        }

        if (wroteKeyframe && _clipIds.Count == 1 && !string.IsNullOrEmpty(_clipId) && FindClip(_clipId) is { } kfClip)
        {
            RefreshKeyframeStatus(kfClip);
            KeyframeGraph?.Refresh();
        }

        ClipPreviewing?.Invoke(this, EventArgs.Empty);
    }

    private void Numeric_CommitRequested(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        _recentAutoStamped = null;
        EndLiveEdit(commit: true);
    }

    private void Numeric_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        EndLiveEdit(commit: true);
        // Refresh the auto-stamp timestamp after RefreshAfterEdit completes so layout/preview
        // latency on LostFocus cannot expire the guard before an adjacent ◆ button Click lands.
        if (_recentAutoStamped is { } recent && recent.Frame == PlayheadFrame)
            _recentAutoStamped = (recent.Property, recent.Frame, Environment.TickCount64);
    }

    private void Numeric_ScrubCompleted(object? sender, EventArgs e)
    {
        if (_suppress) return;
        _recentAutoStamped = null;
        EndLiveEdit(commit: true);
    }

    private void Grade_ScrubCompleted(object? sender, EventArgs e)
    {
        if (_suppress) return;
        EndLiveEdit(commit: true);
    }

    /// <summary>
    /// WP-253: closes an open live edit (a nudge, a scrub, a transition length) as one committed
    /// step. Undo replaces the project, so nothing here may still hold its clips across one.
    /// </summary>
    public void EndPendingEdit()
    {
        CommitPendingNumericFields(raiseClipEdited: true);
        EndLiveEdit(commit: true);
        EndTransitionEdit(commit: true);
    }

    private void EndLiveEdit(bool commit)
    {
        if (!_live.IsActive) return;
        _live.End(keep: commit);
        if (commit)
            ClipEdited?.Invoke(this, EventArgs.Empty);
    }

    private bool ApplyLiveFieldsToClip(UnitNumberField source)
    {
        if (_session is null || _clipIds.Count == 0) return false;
        var fps = Math.Max(1, _session.ActiveTimeline.Fps);
        var frame = PlayheadFrame;
        var wroteKeyframe = false;

        if (source == StartBox && _clipIds.Count == 1 && !string.IsNullOrEmpty(_clipId))
        {
            var clip = FindClip(_clipId);
            if (clip is not null)
            {
                var start = (int)Math.Max(0, StartBox.Value);
                if (start != clip.StartFrame)
                {
                    var delta = start - clip.StartFrame;
                    clip.StartFrame = start;
                    // WP-183: a linked pair moves together.
                    if (_pairAudioId is { } partnerId && FindClip(partnerId) is { } partner)
                        partner.StartFrame = Math.Max(0, partner.StartFrame + delta);
                }
            }
            return false;
        }

        if (source == ScaleBox || source == PosXBox || source == PosYBox || source == RotBox)
        {
            var canvasW = Math.Max(1, _session.ActiveTimeline.Width);
            var canvasH = Math.Max(1, _session.ActiveTimeline.Height);
            foreach (var id in _clipIds)
            {
                var clip = FindClip(id);
                if (clip is null) continue;
                if (source == ScaleBox)
                {
                    var scale = Math.Clamp(ScaleBox.Value / 100, 0.05, 4);
                    if (clip.MediaType == ClipType.text)
                    {
                        var style = ReadTextStyle(clip) ?? new TextStyle();
                        if (clip.ScaleTrack is { IsActive: true })
                        {
                            var baseScale = style.FontScale > 0 ? style.FontScale : 1.0;
                            // scale is already clamped to [0.05, 4.0]; do not clamp ratio to [0.05, 4.0]
                            // or text whose base FontScale != 1 cannot reach the full 5%..400% range.
                            var ratio = scale / baseScale;
                            var baseFit = clip.Transform.Width > 1e-6 && clip.Transform.Height > 1e-6
                                ? (clip.Transform.Width, clip.Transform.Height)
                                : (TextBaseFit(clip, style).Width * baseScale, TextBaseFit(clip, style).Height * baseScale);
                            if (KeyframeCommands.WriteScaleSizeInPlace(clip, frame, baseFit.Item1 * ratio, baseFit.Item2 * ratio))
                                wroteKeyframe = true;
                            continue;
                        }

                        // Text scales through TextStyle.FontScale for crisp vector rasterization and Mac
                        // interchange, keeping Transform.Width/Height proportional for box/keyframe math.
                        var unscaledFit = TextBaseFit(clip, style);
                        style.FontScale = scale;
                        clip.TextStyle = JsonSerializer.SerializeToElement(style, JsonOptions.ForProject);
                        if (KeyframeCommands.WriteScaleSizeInPlace(clip, frame, scale * unscaledFit.Width, scale * unscaledFit.Height))
                            wroteKeyframe = true;
                    }
                    else
                    {
                        // Both axes scale the placed fit, so the source's aspect survives (Mac writeScale).
                        var fit = FitFor(clip);
                        var w = scale * fit.Width;
                        var h = scale * fit.Height;
                        if (KeyframeCommands.WriteScaleSizeInPlace(clip, frame, w, h))
                            wroteKeyframe = true;
                    }
                }
                else if (source == PosXBox || source == PosYBox)
                {
                    var cx = source == PosXBox ? PosXBox.Value / canvasW : (double?)null;
                    var cy = source == PosYBox ? PosYBox.Value / canvasH : (double?)null;
                    if (clip.PositionTrack is { IsActive: true })
                    {
                        if (KeyframeCommands.WritePositionCenterInPlace(clip, frame, cx, cy))
                            wroteKeyframe = true;
                        continue;
                    }

                    var transform = clip.Transform.Clone();
                    if (cx is { } x) transform.CenterX = x;
                    if (cy is { } y) transform.CenterY = y;
                    EditCommands.ApplyClipPropertiesInPlace(
                        _session, [id],
                        durationFrames: null, trimStartFrame: null, trimEndFrame: null,
                        speed: null, volume: null, opacity: null,
                        transform: transform, blendMode: null);
                }
                else
                {
                    if (clip.RotationTrack is { IsActive: true })
                    {
                        if (KeyframeCommands.WriteRotationInPlace(clip, frame, RotBox.Value))
                            wroteKeyframe = true;
                        continue;
                    }

                    var transform = clip.Transform.Clone();
                    transform.Rotation = RotBox.Value;
                    EditCommands.ApplyClipPropertiesInPlace(
                        _session, [id],
                        durationFrames: null, trimStartFrame: null, trimEndFrame: null,
                        speed: null, volume: null, opacity: null,
                        transform: transform, blendMode: null);
                }
            }
            return wroteKeyframe;
        }

        if (source == CropLBox || source == CropTBox || source == CropRBox || source == CropBBox)
        {
            var left = source == CropLBox ? Math.Clamp(CropLBox.Value / 100, 0, 1) : (double?)null;
            var top = source == CropTBox ? Math.Clamp(CropTBox.Value / 100, 0, 1) : (double?)null;
            var right = source == CropRBox ? Math.Clamp(CropRBox.Value / 100, 0, 1) : (double?)null;
            var bottom = source == CropBBox ? Math.Clamp(CropBBox.Value / 100, 0, 1) : (double?)null;
            foreach (var id in _clipIds)
            {
                var clip = FindClip(id);
                if (clip is null) continue;
                if (clip.CropTrack is { IsActive: true })
                {
                    if (KeyframeCommands.WriteCropInPlace(clip, frame, left, top, right, bottom))
                        wroteKeyframe = true;
                    continue;
                }

                var crop = clip.Crop.Clone();
                if (left is { } l) crop.Left = l;
                if (top is { } t) crop.Top = t;
                if (right is { } r) crop.Right = r;
                if (bottom is { } b) crop.Bottom = b;
                EditCommands.ApplyClipPropertiesInPlace(
                    _session, [id],
                    durationFrames: null, trimStartFrame: null, trimEndFrame: null,
                    speed: null, volume: null, opacity: null,
                    transform: null, blendMode: null, crop: crop);
            }
            return wroteKeyframe;
        }

        if (source == OpacityBox)
        {
            var opacityValue = Math.Clamp(OpacityBox.Value / 100, 0, 1);
            foreach (var id in _clipIds)
            {
                var clip = FindClip(id);
                if (clip is null) continue;
                if (clip.OpacityTrack is { IsActive: true })
                {
                    if (KeyframeCommands.WriteOpacityInPlace(clip, frame, opacityValue))
                        wroteKeyframe = true;
                    continue;
                }

                EditCommands.ApplyClipPropertiesInPlace(
                    _session, [id],
                    durationFrames: null, trimStartFrame: null, trimEndFrame: null,
                    speed: null, volume: null, opacity: opacityValue,
                    transform: null, blendMode: null);
            }
            return wroteKeyframe;
        }

        if (source == VolumeDbBox)
        {
            var volumeTargets = _pairAudioId is { } pairId ? new List<string> { pairId } : _clipIds;
            foreach (var id in volumeTargets)
            {
                var clip = FindClip(id);
                if (clip is null) continue;
                if (KeyframeCommands.LiveVolumeKfDb(clip, frame) is not null)
                {
                    if (KeyframeCommands.WriteVolumeDbInPlace(clip, frame, VolumeDbBox.Value))
                        wroteKeyframe = true;
                    continue;
                }

                EditCommands.ApplyClipPropertiesInPlace(
                    _session, [id],
                    durationFrames: null, trimStartFrame: null, trimEndFrame: null,
                    speed: null, volume: VolumeScale.LinearFromDb(VolumeDbBox.Value), opacity: null,
                    transform: null, blendMode: null);
            }
            return wroteKeyframe;
        }

        if (source == VideoFadeInBox || source == VideoFadeOutBox)
        {
            var frames = (int)Math.Round(Math.Max(0, source.Value) * fps, MidpointRounding.AwayFromZero);
            var edge = source == VideoFadeInBox ? FadeEdge.In : FadeEdge.Out;
            var index = new TransitionIndex(_session.ActiveTimeline);
            foreach (var id in _clipIds)
            {
                var pictureClip = FindClip(id);
                if (pictureClip is null || !InspectorClipTabs.HasVideoTab(pictureClip.MediaType))
                    continue;
                FadeCommands.ApplyEdge(index, pictureClip, edge, frames);
            }
            return false;
        }

        int? duration = source == DurationBox && _clipIds.Count == 1
            ? (int)Math.Max(1, DurationBox.Value)
            : null;
        double? speed = source == SpeedBox ? Math.Clamp(SpeedBox.Value, 0.25, 4) : null;
        int? fadeIn = source == FadeInBox
            ? (int)Math.Round(Math.Max(0, FadeInBox.Value) * fps)
            : null;
        int? fadeOut = source == FadeOutBox
            ? (int)Math.Round(Math.Max(0, FadeOutBox.Value) * fps)
            : null;

        // WP-183: Levels target the linked audio partner; a duration change reaches both halves.
        var levelsEdit = source == FadeInBox || source == FadeOutBox;
        var targets = _pairAudioId is { } audioId
            ? (levelsEdit ? new List<string> { audioId } : duration is not null ? new List<string> { _clipId!, audioId } : _clipIds)
            : _clipIds;
        EditCommands.ApplyClipPropertiesInPlace(
            _session,
            targets,
            durationFrames: duration,
            trimStartFrame: null,
            trimEndFrame: null,
            speed: speed,
            volume: null,
            opacity: null,
            transform: null,
            blendMode: null,
            crop: null,
            fadeInFrames: fadeIn,
            fadeOutFrames: fadeOut);
        return false;
    }

    private void RefreshFieldsPreservingSelection()
    {
        if (_clipIds.Count > 1 && _clipId is not null && FindClip(_clipId) is { } primary)
            FillClipFields(primary, multi: true);
        else if (!string.IsNullOrEmpty(_clipId))
            ShowClip(_clipId);
    }

    private void Flip_Click(object sender, RoutedEventArgs e)
    {
        if (_suppress || _session is null || _clipIds.Count == 0) return;
        EndLiveEdit(commit: true);
        _session.RecordBeforeEdit();
        var flipH = FlipHBtn.IsChecked == true;
        var flipV = FlipVBtn.IsChecked == true;
        var any = false;
        foreach (var id in _clipIds)
        {
            var clip = FindClip(id);
            if (clip is null) continue;
            var t = new TransformModel
            {
                Width = clip.Transform.Width,
                Height = clip.Transform.Height,
                CenterX = clip.Transform.CenterX,
                CenterY = clip.Transform.CenterY,
                Rotation = clip.Transform.Rotation,
                FlipHorizontal = flipH,
                FlipVertical = flipV,
            };
            var result = EditCommands.ApplyClipPropertiesInPlace(_session, [id], null, null, null, null, null, null, t, null);
            if (result.Ok) any = true;
        }

        if (any)
        {
            _session.MarkDirty();
            ClipEdited?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _session.RollbackLastRecord();
        }
        RefreshFieldsPreservingSelection();
    }

    private void BlendMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress || _session is null || _clipIds.Count == 0) return;
        if (BlendModeBox.SelectedIndex < 0) return;
        if (BlendModeBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        EndLiveEdit(commit: true);
        var result = EditCommands.SetClipProperties(
            _session, _clipIds, null, null, null, null, null, null, null, tag);
        if (result.Ok) ClipEdited?.Invoke(this, EventArgs.Empty);
    }

    private void Denoise_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress || _session is null || _clipIds.Count == 0) return;
        EndLiveEdit(commit: true);

        // audio.denoise is not in EffectRegistry; persist its enabled state with one undo step.
        _session.RecordBeforeEdit();
        var changed = false;
        foreach (var id in _pairAudioId is { } denoiseTarget ? new[] { denoiseTarget } : _clipIds.ToArray())
        {
            var clip = FindClip(id);
            if (clip is null) continue;
            var stack = clip.Effects ?? [];
            var existing = stack.FirstOrDefault(x => x.Type == ClipModel.DenoiseEffectType);
            if (DenoiseSwitch.IsOn)
            {
                if (existing is null)
                {
                    stack.Add(new Effect
                    {
                        Id = Timeline.NewId(),
                        Type = ClipModel.DenoiseEffectType,
                        Enabled = true,
                        Params = new Dictionary<string, EffectParam>
                        {
                            ["amount"] = new EffectParam { Value = ClipModel.DefaultDenoiseAmount },
                        },
                    });
                    clip.Effects = stack;
                    changed = true;
                }
                else if (!existing.Enabled)
                {
                    existing.Enabled = true;
                    changed = true;
                }
            }
            else if (existing is not null && existing.Enabled)
            {
                existing.Enabled = false;
                changed = true;
            }
        }

        if (changed)
        {
            _session.MarkDirty();
            ClipEdited?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _session.RollbackLastRecord();
        }
        RefreshFieldsPreservingSelection();
    }
}
