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
using Temno.Core.Project;
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
    private bool CommitLut(string clipId)
    {
        if (_session is null) return false;
        var path = GradeLutPathBox.Text?.Trim() ?? "";
        var intensity = GradeLutIntensityBox.Value;
        var clip = FindClip(clipId);
        if (clip is null) return false;
        var existing = clip.Effects?.FirstOrDefault(e => e.Type == "color.lut");

        if (string.IsNullOrEmpty(path))
        {
            if (existing is not null)
            {
                var remove = EffectStackCommands.RemoveEffectInPlace(_session, clipId, existing.Id);
                return remove.Ok;
            }
            return false;
        }

        var currentPath = existing?.Params.GetValueOrDefault("path")?.String;
        var currentIntensity = existing?.Params.GetValueOrDefault("intensity")?.Value;
        if (existing is not null
            && string.Equals(currentPath, path, StringComparison.Ordinal)
            && currentIntensity is { } ci && Math.Abs(ci - intensity) < 0.0005)
        {
            return false;
        }

        existing = EnsureEffectInPlace(clipId, "color.lut");
        if (existing is null) return false;
        var setPath = EffectStackCommands.SetEffectStringParamInPlace(_session, clipId, existing.Id, "path", path);
        var setNum = EffectStackCommands.SetEffectNumericParamsInPlace(
            _session, clipId, existing.Id, new Dictionary<string, double> { ["intensity"] = intensity });
        return setPath.Ok && setNum.Ok;
    }

    private Effect? EnsureEffectInPlace(string clipId, string type)
    {
        if (_session is null) return null;
        var clip = FindClip(clipId);
        if (clip is null) return null;
        var existing = clip.Effects?.FirstOrDefault(e => e.Type == type);
        if (existing is not null) return existing;
        var add = EffectStackCommands.AddEffectInPlace(_session, clipId, type);
        if (!add.Ok) return null;
        clip = FindClip(clipId);
        return clip?.Effects?.FirstOrDefault(e => e.Type == type);
    }

    private void UpsertEffectParam(string clipId, string type, string key, double value)
        => UpsertEffectParams(clipId, type, new Dictionary<string, double> { [key] = value });

    private void UpsertEffectParams(string clipId, string type, IReadOnlyDictionary<string, double> values)
    {
        if (_session is null) return;
        var existing = EnsureEffectInPlace(clipId, type);
        if (existing is null) return;
        EffectStackCommands.SetEffectNumericParamsInPlace(_session, clipId, existing.Id, values);
    }

    // --- WP-87 project settings ---

    private void ProjectSettings_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_projectSettingsSuppress || _session is null) return;
        EditResult result;
        if (ReferenceEquals(sender, SettingsFpsBox))
        {
            if (SettingsFpsBox.SelectedItem is not ComboBoxItem { Tag: string fpsTag }) return;
            if (fpsTag == "custom")
            {
                SettingsCustomFpsLabel.Visibility = Visibility.Visible;
                SettingsCustomFpsBox.Visibility = Visibility.Visible;
                SettingsCustomFpsBox.Value = Math.Clamp(
                    _session.ActiveTimeline.Fps,
                    CanvasSizePresets.MinProjectFps,
                    CanvasSizePresets.MaxProjectFps);
                return;
            }
            if (!int.TryParse(fpsTag, out var fps)) return;
            result = EditCommands.SetProjectSettings(
                _session, fps: fps, width: null, height: null, aspectRatio: null, quality: null);
        }
        else if (ReferenceEquals(sender, SettingsQualityBox))
        {
            if (SettingsQualityBox.SelectedItem is not ComboBoxItem { Tag: string qualityTag } || qualityTag == "custom")
                return;
            result = EditCommands.SetProjectSettings(
                _session, fps: null, width: null, height: null, aspectRatio: null, quality: qualityTag);
        }
        else if (ReferenceEquals(sender, SettingsAspectBox))
        {
            if (SettingsAspectBox.SelectedItem is not ComboBoxItem { Tag: string aspectTag } || aspectTag == "custom")
                return;
            var tl = _session.ActiveTimeline;
            var currentQuality = CanvasSizePresets.MatchQualityTag(tl.Width, tl.Height);
            result = EditCommands.SetProjectSettings(
                _session, fps: null, width: null, height: null, aspectRatio: aspectTag, quality: currentQuality);
        }
        else
        {
            return;
        }

        if (result.Ok)
        {
            RefreshProjectPanel();
            ProjectSettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // --- Effects (existing WP-40, under Adjust) ---

    private void PopulateEffects(Clip clip, bool multi = false)
    {
        EffectsPanel.Children.Clear();
        if (multi)
        {
            EffectCatalog.IsEnabled = false;
            AddEffectButton.IsEnabled = false;
            EffectCatalog.SelectedIndex = -1;
            EffectsStatusLabel.Text = string.Empty;
            EffectsStatusLabel.Foreground = AppTheme.Text.TertiaryBrush;
            EffectsEmptyLabel.Text = "Effects apply to one clip at a time.";
            EffectsEmptyLabel.Visibility = Visibility.Visible;
            return;
        }

        var supportsEffects = clip.MediaType is ClipType.video or ClipType.image;
        EffectCatalog.IsEnabled = supportsEffects;
        AddEffectButton.IsEnabled = supportsEffects;
        EffectCatalog.SelectedIndex = -1;
        EffectsStatusLabel.Text = string.Empty;
        EffectsStatusLabel.Foreground = AppTheme.Text.TertiaryBrush;

        if (!supportsEffects)
        {
            EffectsEmptyLabel.Text = "Effects are available on video and image clips.";
            EffectsEmptyLabel.Visibility = Visibility.Visible;
            return;
        }

        var effects = clip.Effects ?? [];
        EffectsEmptyLabel.Text = "No effects applied";
        EffectsEmptyLabel.Visibility = effects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (var index = 0; index < effects.Count; index++)
            EffectsPanel.Children.Add(CreateEffectRow(effects[index], index, effects.Count));
    }

    private Border CreateEffectRow(Effect effect, int index, int count)
    {
        var descriptor = EffectRegistry.GetDescriptor(effect.Type);
        var content = new StackPanel { Spacing = AppTheme.Spacing.Xs };
        var header = new Grid { ColumnSpacing = AppTheme.Spacing.Xs };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var enabled = new CheckBox
        {
            Content = descriptor is null
                ? effect.Type
                : $"{EffectRegistry.GetCategory(effect.Type)} · {EffectRegistry.GetDisplayName(effect.Type)}",
            IsChecked = effect.Enabled,
            Tag = new EffectIdTag(effect.Id),
            VerticalAlignment = VerticalAlignment.Center,
        };
        enabled.Checked += EffectEnabled_Changed;
        enabled.Unchecked += EffectEnabled_Changed;
        header.Children.Add(enabled);

        var up = CreateEffectButton("↑", new EffectMoveTag(effect.Id, index - 1), MoveEffect_Click);
        up.IsEnabled = index > 0;
        Grid.SetColumn(up, 1);
        header.Children.Add(up);

        var down = CreateEffectButton("↓", new EffectMoveTag(effect.Id, index + 1), MoveEffect_Click);
        down.IsEnabled = index < count - 1;
        Grid.SetColumn(down, 2);
        header.Children.Add(down);

        var remove = CreateEffectButton("Remove", new EffectIdTag(effect.Id), RemoveEffect_Click);
        Grid.SetColumn(remove, 3);
        header.Children.Add(remove);
        content.Children.Add(header);

        if (descriptor is null)
        {
            content.Children.Add(new TextBlock
            {
                Text = "Unsupported effect. Parameters preserved.",
                Foreground = AppTheme.Text.TertiaryBrush,
                FontSize = AppTheme.FontSize.Xs,
                TextWrapping = TextWrapping.WrapWholeWords,
            });
        }
        else if (descriptor.Params.Count > 0)
        {
            content.Children.Add(CreateRendererStatus(effect.Type));
            foreach (var spec in descriptor.Params)
            {
                var value = effect.Params.TryGetValue(spec.Key, out var param)
                    ? param.Value ?? spec.DefaultValue
                    : spec.DefaultValue;
                var slider = new Slider
                {
                    Minimum = spec.Min,
                    Maximum = spec.Max,
                    SmallChange = ParameterSmallChange(spec),
                    LargeChange = Math.Max(ParameterSmallChange(spec) * 5, (spec.Max - spec.Min) / 10),
                    Value = value,
                };
                var step = ParameterSmallChange(spec);
                var parameterField = new UnitNumberField
                {
                    Spec = new UnitFieldSpec
                    {
                        Unit = "",
                        Minimum = spec.Min,
                        Maximum = spec.Max,
                        Step = step,
                        Default = spec.DefaultValue,
                    },
                    Value = value,
                };
                var pair = new EffectParameterInputPair(effect.Id, spec.Key, slider, parameterField);
                slider.Tag = pair;
                parameterField.Tag = pair;
                slider.ValueChanged += EffectSlider_ValueChanged;
                slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(EffectSlider_CommitGesture), handledEventsToo: true);
                slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(EffectSlider_CommitGesture), handledEventsToo: true);
                parameterField.ValueChanged += EffectParameterField_ValueChanged;
                parameterField.LostFocus += EffectParameterField_LostFocus;
                parameterField.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(EffectParameterField_CommitGesture), handledEventsToo: true);
                parameterField.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(EffectParameterField_CommitGesture), handledEventsToo: true);

                var parameterGrid = new Grid { ColumnSpacing = AppTheme.Spacing.Sm };
                parameterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                parameterGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
                parameterGrid.Children.Add(slider);
                Grid.SetColumn(parameterField, 1);
                parameterGrid.Children.Add(parameterField);
                content.Children.Add(parameterGrid);
            }
        }

        return new Border
        {
            Background = AppTheme.Background.RaisedBrush,
            BorderBrush = AppTheme.Border.PrimaryBrush,
            BorderThickness = new Thickness(AppTheme.BorderWidth.Thin),
            CornerRadius = new CornerRadius(AppTheme.Radius.Sm),
            Padding = new Thickness(AppTheme.Spacing.SmMd),
            Child = content,
        };
    }

    private static Button CreateEffectButton(string content, object tag, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = content,
            Tag = tag,
            MinWidth = content.Length == 1 ? AppTheme.IconSize.LgXl : 0,
        };
        button.Click += handler;
        return button;
    }

    private static TextBlock CreateRendererStatus(string effectType)
    {
        // WP-103: all registry effects render on CPU.
        var renderReady = EffectRegistry.GetDescriptor(effectType) is not null;
        return new TextBlock
        {
            Text = renderReady
                ? "Shows in the preview and in exports."
                : "Saved with the project; it does not show in the preview or in exports on Windows yet.",
            Foreground = AppTheme.Text.TertiaryBrush,
            FontSize = AppTheme.FontSize.Xs,
            TextWrapping = TextWrapping.WrapWholeWords,
        };
    }

    private static double ParameterSmallChange(EffectParamSpec spec)
    {
        var range = spec.Max - spec.Min;
        if (range <= 2) return 0.01;
        if (range <= 10) return 0.05;
        if (range <= 100) return 0.5;
        return 1;
    }

    private void AddEffect_Click(object sender, RoutedEventArgs e)
    {
        if (_suppress || _session is null || string.IsNullOrEmpty(_clipId)) return;
        if (EffectCatalog.SelectedItem is not EffectCatalogItem item)
        {
            ShowEffectsStatus("Choose an effect from the catalog first.", isError: true);
            return;
        }
        HandleEffectResult(EffectStackCommands.AddEffect(_session, _clipId, item.Id), "Effect added.");
    }

    private void EffectEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppress || _session is null || string.IsNullOrEmpty(_clipId)) return;
        if (sender is not CheckBox { Tag: EffectIdTag tag } checkBox || checkBox.IsChecked is not bool enabled) return;
        HandleEffectResult(EffectStackCommands.SetEffectEnabled(_session, _clipId, tag.EffectId, enabled), "Effect updated.");
    }

    private void MoveEffect_Click(object sender, RoutedEventArgs e)
    {
        if (_suppress || _session is null || string.IsNullOrEmpty(_clipId)) return;
        if (sender is not Button { Tag: EffectMoveTag tag }) return;
        HandleEffectResult(EffectStackCommands.MoveEffect(_session, _clipId, tag.EffectId, tag.DestinationIndex), "Effect reordered.");
    }

    private void RemoveEffect_Click(object sender, RoutedEventArgs e)
    {
        if (_suppress || _session is null || string.IsNullOrEmpty(_clipId)) return;
        if (sender is not Button { Tag: EffectIdTag tag }) return;
        HandleEffectResult(EffectStackCommands.RemoveEffect(_session, _clipId, tag.EffectId), "Effect removed.");
    }

    private void EffectSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_suppress || sender is not Slider { Tag: EffectParameterInputPair pair } || pair.Syncing) return;
        pair.Syncing = true;
        pair.Field.Value = args.NewValue;
        pair.Syncing = false;
        ApplyLiveEffectParam(pair, args.NewValue);
    }

    private void EffectParameterField_ValueChanged(object? sender, double value)
    {
        if (_suppress || sender is not UnitNumberField { Tag: EffectParameterInputPair pair } || pair.Syncing || !double.IsFinite(value)) return;
        pair.Syncing = true;
        pair.Slider.Value = Math.Clamp(value, pair.Slider.Minimum, pair.Slider.Maximum);
        pair.Syncing = false;
        ApplyLiveEffectParam(pair, value);
    }

    private void EffectSlider_CommitGesture(object sender, PointerRoutedEventArgs e)
        => EndLiveEdit(commit: true);

    private void EffectParameterField_CommitGesture(object sender, PointerRoutedEventArgs e)
        => EndLiveEdit(commit: true);

    private void EffectParameterField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        EndLiveEdit(commit: true);
    }

    private void ApplyLiveEffectParam(EffectParameterInputPair pair, double value)
    {
        if (_session is null || string.IsNullOrEmpty(_clipId) || !double.IsFinite(value)) return;
        if (!_live.IsActive)
            _live.Begin(_session);

        try
        {
            _live.Mutate(() =>
            {
                var result = EffectStackCommands.SetEffectNumericParamsInPlace(
                    _session,
                    _clipId,
                    pair.EffectId,
                    new Dictionary<string, double> { [pair.Key] = value });
                if (!result.Ok)
                    throw new InvalidOperationException(result.Error ?? "Effect parameter edit failed.");
            });
        }
        catch (InvalidOperationException ex)
        {
            ShowEffectsStatus(ex.Message, isError: true);
            EndLiveEdit(commit: false);
            return;
        }

        ClipPreviewing?.Invoke(this, EventArgs.Empty);
    }

    private void HandleEffectResult(EditResult result, string successMessage)
    {
        if (!result.Ok)
        {
            ShowEffectsStatus(result.Error ?? "Effect edit failed.", isError: true);
            return;
        }
        ClipEdited?.Invoke(this, EventArgs.Empty);
        RefreshFieldsPreservingSelection();
        ShowEffectsStatus(successMessage, isError: false);
    }

    private void ShowEffectsStatus(string text, bool isError)
    {
        EffectsStatusLabel.Text = text;
        EffectsStatusLabel.Foreground = isError ? AppTheme.Status.ErrorBrush : AppTheme.Status.SuccessBrush;
    }

    private void BindAiEdit(string? mediaRef, string kind)
    {
        var normalized = kind?.Trim().ToLowerInvariant() ?? "";
        var isAv = normalized is "video" or "image" or "audio";
        var canUpscale = normalized is "video" or "image";
        _aiEditAvailable = isAv && !string.IsNullOrEmpty(mediaRef);
        AiEdit.Bind(_session, mediaRef, normalized, canUpscale, visible: _aiEditAvailable);
        RefreshAiEditVisibility();
    }

    private bool _aiEditAvailable;

    private void RefreshAiEditVisibility()
        => AiEdit.Visibility = _aiEditAvailable
            && (MediaAssetPanel.Visibility == Visibility.Visible || _activeTab == InspectorClipTab.Video)
                ? Visibility.Visible : Visibility.Collapsed;

    private void HideAiEdit()
    {
        _aiEditAvailable = false;
        AiEdit.Bind(null, null, "", canUpscale: false, visible: false);
    }
}
