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
    private void LoadGradeFields(ClipModel clip, IReadOnlyList<ClipModel> clips)
    {
        BindNumeric(GradeExposureBox, ReadEffectParam(clip, "color.exposure", "ev", 0), clips, c => ReadEffectParam(c, "color.exposure", "ev", 0));
        BindNumeric(GradeContrastBox, ReadEffectParam(clip, "color.contrast", "amount", 1), clips, c => ReadEffectParam(c, "color.contrast", "amount", 1));
        BindNumeric(GradeSaturationBox, ReadEffectParam(clip, "color.saturation", "amount", 1), clips, c => ReadEffectParam(c, "color.saturation", "amount", 1));

        BindNumeric(GradeTemperatureBox, ReadEffectParam(clip, "color.temperature", "temperature", ColorTemperature.NeutralKelvin), clips, c => ReadEffectParam(c, "color.temperature", "temperature", ColorTemperature.NeutralKelvin));
        BindNumeric(GradeTintBox, ReadEffectParam(clip, "color.temperature", "tint", 0), clips, c => ReadEffectParam(c, "color.temperature", "tint", 0));

        BindNumeric(GradeHighlightsBox, ReadEffectParam(clip, "color.highlightsShadows", "highlights", 0), clips, c => ReadEffectParam(c, "color.highlightsShadows", "highlights", 0));
        BindNumeric(GradeShadowsBox, ReadEffectParam(clip, "color.highlightsShadows", "shadows", 0), clips, c => ReadEffectParam(c, "color.highlightsShadows", "shadows", 0));
        BindNumeric(GradeBlacksBox, ReadEffectParam(clip, "color.blacksWhites", "blacks", 0), clips, c => ReadEffectParam(c, "color.blacksWhites", "blacks", 0));
        BindNumeric(GradeWhitesBox, ReadEffectParam(clip, "color.blacksWhites", "whites", 0), clips, c => ReadEffectParam(c, "color.blacksWhites", "whites", 0));
        BindNumeric(GradeVibranceBox, ReadEffectParam(clip, "color.vibrance", "amount", 0), clips, c => ReadEffectParam(c, "color.vibrance", "amount", 0));

        _gradeLiftX = ReadEffectParam(clip, "color.wheels", "lift_x", 0);
        _gradeLiftY = ReadEffectParam(clip, "color.wheels", "lift_y", 0);
        BindNumeric(GradeLiftMBox, ReadEffectParam(clip, "color.wheels", "lift_m", 0), clips, c => ReadEffectParam(c, "color.wheels", "lift_m", 0));
        _gradeGammaX = ReadEffectParam(clip, "color.wheels", "gamma_x", 0);
        _gradeGammaY = ReadEffectParam(clip, "color.wheels", "gamma_y", 0);
        BindNumeric(GradeGammaMBox, ReadEffectParam(clip, "color.wheels", "gamma_m", 1), clips, c => ReadEffectParam(c, "color.wheels", "gamma_m", 1));
        _gradeGainX = ReadEffectParam(clip, "color.wheels", "gain_x", 0);
        _gradeGainY = ReadEffectParam(clip, "color.wheels", "gain_y", 0);
        BindNumeric(GradeGainMBox, ReadEffectParam(clip, "color.wheels", "gain_m", 1), clips, c => ReadEffectParam(c, "color.wheels", "gain_m", 1));
        RefreshWheelPads();

        LoadMasterCurveBoxes(clip);
        RefreshMasterCurveCanvas();
        LoadHueCurveBoxes(clip);
        GradeLutPathBox.Text = ReadEffectStringParam(clip, "color.lut", "path") ?? "";
        BindNumeric(GradeLutIntensityBox, ReadEffectParam(clip, "color.lut", "intensity", 1), clips, c => ReadEffectParam(c, "color.lut", "intensity", 1));
    }

    private void LoadMasterCurveBoxes(ClipModel clip)
    {
        _rgbCurve = new GradeCurve
        {
            Master = PointCurveEditor.Identity,
            Red = [],
            Green = [],
            Blue = [],
        };
        var fx = clip.Effects?.FirstOrDefault(e => e.Type == "color.curves" && e.Enabled);
        if (fx?.Params.TryGetValue("curve", out var p) == true && p.String is { Length: > 0 } json)
        {
            var curve = GradeCurve.Decode(json);
            if (curve is not null)
                _rgbCurve = curve;
        }
        RefreshMasterCurveCanvas();
    }

    private void LoadHueCurveBoxes(ClipModel clip)
    {
        _hueCurvesEdit = new HueCurves
        {
            HueVsHue = PointCurveEditor.NeutralHue,
            HueVsSat = PointCurveEditor.NeutralHue,
            HueVsLum = PointCurveEditor.NeutralHue,
        };
        var hfx = clip.Effects?.FirstOrDefault(e => e.Type == "color.hueCurves" && e.Enabled);
        if (hfx?.Params.TryGetValue("curves", out var hp) == true && hp.String is { Length: > 0 } hjson)
        {
            var hc = HueCurves.Decode(hjson);
            if (hc is not null) _hueCurvesEdit = hc;
        }
        RefreshHueCurveCanvas();
    }

    private static double ReadEffectParam(ClipModel clip, string type, string key, double fallback)
    {
        var fx = clip.Effects?.FirstOrDefault(e => e.Type == type && e.Enabled);
        if (fx?.Params.TryGetValue(key, out var p) == true && p.Value is { } v)
            return v;
        return fallback;
    }

    private static string? ReadEffectStringParam(ClipModel clip, string type, string key)
    {
        var fx = clip.Effects?.FirstOrDefault(e => e.Type == type && e.Enabled);
        if (fx?.Params.TryGetValue(key, out var p) == true)
            return p.String;
        return null;
    }
    // --- WP-269 grade: live during the gesture, one undo step on commit ---

    private void Grade_ValueChanged(object? sender, double newValue)
    {
        if (_suppress || _session is null || _clipIds.Count == 0) return;
        if (sender is not UnitNumberField field || !double.IsFinite(field.Value)) return;

        // A ValueChanged tick always carries a changed value (UnitNumberField.Commit only
        // raises it when the value moves), so every tick joins one live gesture: Begin once,
        // mutate per tick. The gesture closes as one undo step on scrub end (ScrubCompleted),
        // Enter/CommitRequested or focus loss (Grade_LostFocus) — whichever ends it first.
        if (ReferenceEquals(field, GradeLutIntensityBox))
        {
            if (string.IsNullOrWhiteSpace(GradeLutPathBox.Text)) return;
            if (!_live.IsActive) _live.Begin(_session);
            _live.Mutate(ApplyLiveLutValues);
        }
        else if (TryGradeValuesForControl(field, out var type, out var values))
        {
            if (!_live.IsActive) _live.Begin(_session);
            _live.Mutate(() => ApplyLiveGradeValues(type, values));
        }
        RefreshMasterCurveCanvas();
        ClipPreviewing?.Invoke(this, EventArgs.Empty);
    }

    private void Grade_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        EndLiveEdit(commit: true);
    }

    private void ApplyLiveGradeValues(string type, IReadOnlyDictionary<string, double> changedValues)
    {
        if (_session is null) return;
        var descriptor = EffectRegistry.GetDescriptor(type);
        if (descriptor is null) return;
        foreach (var id in _clipIds)
        {
            var clip = FindClip(id);
            if (clip is null || clip.MediaType is not (ClipType.video or ClipType.image)) continue;
            // Multi-edit starts from each clip's own values. A tint edit must not copy the
            // focused clip's temperature, or a Lift pad drag its other wheel positions.
            var existing = clip.Effects?.FirstOrDefault(fx => fx.Type == type);
            var values = descriptor.Params.ToDictionary(
                spec => spec.Key,
                spec => existing?.Params.TryGetValue(spec.Key, out var param) == true
                    && param.Value is { } value ? value : spec.DefaultValue);
            foreach (var (key, value) in changedValues)
                values[key] = value;
            EffectStackCommands.ApplyNumericEffectInPlace(_session, id, type, values, out _);
        }
    }

    /// <summary>
    /// WP-269: the LUT intensity slider's live half. It keeps each selected clip's existing
    /// LUT path; clips without one use the focused path. The open gesture owns undo.
    /// </summary>
    private void ApplyLiveLutValues()
    {
        if (_session is null) return;
        var selectedPath = GradeLutPathBox.Text?.Trim() ?? "";
        var intensity = GradeLutIntensityBox.Value;
        foreach (var id in _clipIds)
        {
            var clip = FindClip(id);
            if (clip is null || clip.MediaType is not (ClipType.video or ClipType.image)) continue;
            var existing = clip.Effects?.FirstOrDefault(fx => fx.Type == "color.lut");
            var path = existing?.Params.GetValueOrDefault("path")?.String?.Trim() ?? selectedPath;
            if (string.IsNullOrEmpty(path))
                continue;
            existing = EnsureEffectInPlace(id, "color.lut");
            if (existing is null) continue;
            EffectStackCommands.SetEffectStringParamInPlace(_session, id, existing.Id, "path", path);
            EffectStackCommands.SetEffectNumericParamsInPlace(
                _session, id, existing.Id, new Dictionary<string, double> { ["intensity"] = intensity });
        }
    }

    private void GradeLutPath_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppress || _session is null || _clipIds.Count == 0) return;
        EndLiveEdit(commit: true);
        CommitGradeLutSelection();
    }

    private async void GradeLutBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (_suppress || _session is null || _clipIds.Count == 0) return;
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary,
            };
            picker.FileTypeFilter.Add(".cube");
            picker.FileTypeFilter.Add(".3dl");
            picker.FileTypeFilter.Add("*");
            var window = App.MainAppWindow;
            if (window is null) return;
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            GradeLutPathBox.Text = file.Path;
            CommitGradeLutSelection();
        }
        catch
        {
            // Picker unavailable (e.g. no HWND); path TextBox remains editable.
        }
    }

    private void GradeLutClear_Click(object sender, RoutedEventArgs e)
    {
        if (_suppress || _session is null || _clipIds.Count == 0) return;
        EndLiveEdit(commit: true);
        GradeLutPathBox.Text = "";
        GradeLutIntensityBox.Value = 1;
        _session.RecordBeforeEdit();
        var any = false;
        foreach (var id in _clipIds)
        {
            var clip = FindClip(id);
            if (clip is null) continue;
            var lut = clip.Effects?.FirstOrDefault(fx => fx.Type == "color.lut");
            if (lut is null) continue;
            var remove = EffectStackCommands.RemoveEffectInPlace(_session, id, lut.Id);
            if (remove.Ok) any = true;
        }
        if (!any)
        {
            _session.RollbackLastRecord();
            return;
        }
        ClipEdited?.Invoke(this, EventArgs.Empty);
        if (_clipId is not null && FindClip(_clipId) is { } refreshed)
            PopulateEffects(refreshed, multi: _clipIds.Count > 1);
    }

    /// <summary>
    /// WP-269: resolves a grade number field to the parameter it changes. Other parameters
    /// are read from each target clip when applying a mixed selection.
    /// LUT intensity uses its separate live path.
    /// </summary>
    private bool TryGradeValuesForControl(
        FrameworkElement el, out string type, out IReadOnlyDictionary<string, double> values)
    {
        if (el == GradeExposureBox)
        {
            type = "color.exposure";
            values = new Dictionary<string, double> { ["ev"] = GradeExposureBox.Value };
            return true;
        }
        else if (el == GradeContrastBox)
        {
            type = "color.contrast";
            values = new Dictionary<string, double> { ["amount"] = GradeContrastBox.Value };
            return true;
        }
        else if (el == GradeSaturationBox)
        {
            type = "color.saturation";
            values = new Dictionary<string, double> { ["amount"] = GradeSaturationBox.Value };
            return true;
        }
        else if (el == GradeTemperatureBox)
        {
            type = "color.temperature";
            values = new Dictionary<string, double> { ["temperature"] = GradeTemperatureBox.Value };
            return true;
        }
        else if (el == GradeTintBox)
        {
            type = "color.temperature";
            values = new Dictionary<string, double> { ["tint"] = GradeTintBox.Value };
            return true;
        }
        else if (el == GradeHighlightsBox)
        {
            type = "color.highlightsShadows";
            values = new Dictionary<string, double> { ["highlights"] = GradeHighlightsBox.Value };
            return true;
        }
        else if (el == GradeShadowsBox)
        {
            type = "color.highlightsShadows";
            values = new Dictionary<string, double> { ["shadows"] = GradeShadowsBox.Value };
            return true;
        }
        else if (el == GradeBlacksBox)
        {
            type = "color.blacksWhites";
            values = new Dictionary<string, double> { ["blacks"] = GradeBlacksBox.Value };
            return true;
        }
        else if (el == GradeWhitesBox)
        {
            type = "color.blacksWhites";
            values = new Dictionary<string, double> { ["whites"] = GradeWhitesBox.Value };
            return true;
        }
        else if (el == GradeVibranceBox)
        {
            type = "color.vibrance";
            values = new Dictionary<string, double> { ["amount"] = GradeVibranceBox.Value };
            return true;
        }
        else if (el == GradeLiftMBox)
        {
            type = "color.wheels";
            values = new Dictionary<string, double> { ["lift_m"] = GradeLiftMBox.Value };
            return true;
        }
        else if (el == GradeGammaMBox)
        {
            type = "color.wheels";
            values = new Dictionary<string, double> { ["gamma_m"] = GradeGammaMBox.Value };
            return true;
        }
        else if (el == GradeGainMBox)
        {
            type = "color.wheels";
            values = new Dictionary<string, double> { ["gain_m"] = GradeGainMBox.Value };
            return true;
        }
        type = "";
        values = new Dictionary<string, double>();
        return false;
    }

    /// <summary>
    /// WP-269: discrete single-step commit for curve add/remove gestures (double-tap, Alt+click).
    /// Drags go live through <see cref="ApplyLiveMasterCurve"/> and close with
    /// <see cref="EndLiveEdit"/>. Folds any open gesture first so one Ctrl+Z undoes the whole edit.
    /// </summary>
    private void CommitGradeCurvesSelection()
    {
        if (_session is null || _clipIds.Count == 0) return;
        EndLiveEdit(commit: true);
        _session.RecordBeforeEdit();
        var any = false;
        foreach (var id in _clipIds)
        {
            var clip = FindClip(id);
            if (clip is null || clip.MediaType is not (ClipType.video or ClipType.image)) continue;
            if (CommitMasterCurve(id))
                any = true;
        }
        if (!any)
        {
            _session.RollbackLastRecord();
            return;
        }
        NotifyGradeEdited();
    }

    private void CommitGradeHueSelection()
    {
        if (_session is null || _clipIds.Count == 0) return;
        EndLiveEdit(commit: true);
        _session.RecordBeforeEdit();
        var any = false;
        foreach (var id in _clipIds)
        {
            var clip = FindClip(id);
            if (clip is null || clip.MediaType is not (ClipType.video or ClipType.image)) continue;
            if (CommitHueCurves(id))
                any = true;
        }
        if (!any)
        {
            _session.RollbackLastRecord();
            return;
        }
        NotifyGradeEdited();
    }

    private void CommitGradeLutSelection()
    {
        if (_session is null || _clipIds.Count == 0) return;
        EndLiveEdit(commit: true);
        _session.RecordBeforeEdit();
        var any = false;
        foreach (var id in _clipIds)
        {
            var clip = FindClip(id);
            if (clip is null || clip.MediaType is not (ClipType.video or ClipType.image)) continue;
            if (CommitLut(id))
                any = true;
        }
        if (!any)
        {
            _session.RollbackLastRecord();
            return;
        }
        NotifyGradeEdited();
    }

    private void NotifyGradeEdited()
    {
        ClipEdited?.Invoke(this, EventArgs.Empty);
        if (_clipId is not null && FindClip(_clipId) is { } refreshed)
        {
            PopulateEffects(refreshed, multi: _clipIds.Count > 1);
            RefreshMasterCurveCanvas();
            RefreshWheelPads();
        }
    }

    private bool CommitHueCurves(string clipId)
    {
        if (_session is null) return false;
        // Ensure empty lists are treated as neutral for identity check
        var curves = new HueCurves
        {
            HueVsHue = _hueCurvesEdit.HueVsHue.Count > 0 ? _hueCurvesEdit.HueVsHue : [],
            HueVsSat = _hueCurvesEdit.HueVsSat.Count > 0 ? _hueCurvesEdit.HueVsSat : [],
            HueVsLum = _hueCurvesEdit.HueVsLum.Count > 0 ? _hueCurvesEdit.HueVsLum : [],
        };
        var clip = FindClip(clipId);
        if (clip is null) return false;
        var existing = clip.Effects?.FirstOrDefault(e => e.Type == "color.hueCurves");
        if (curves.IsIdentity)
        {
            if (existing is not null)
            {
                var remove = EffectStackCommands.RemoveEffectInPlace(_session, clipId, existing.Id);
                return remove.Ok;
            }
            return false;
        }
        var json = curves.Encoded();
        if (json is null) return false;
        if (existing is not null && string.Equals(existing.Params.GetValueOrDefault("curves")?.String, json, StringComparison.Ordinal))
            return false;
        existing = EnsureEffectInPlace(clipId, "color.hueCurves");
        if (existing is null) return false;
        var set = EffectStackCommands.SetEffectStringParamInPlace(_session, clipId, existing.Id, "curves", json);
        return set.Ok;
    }
}
