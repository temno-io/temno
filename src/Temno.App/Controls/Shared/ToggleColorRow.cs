using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Temno.App.Theme;
using Temno.Core.Models;

namespace Temno.App.Controls.Shared;

/// <summary>
/// A switch and a colour well on one row — "Outline", "Shadow", "Background" in Mac's Style panel
/// (<c>ToggleColorControl</c>, screenshot <c>10</c>).
/// </summary>
/// <remarks>
/// Replaces the <see cref="ToggleSwitch"/>-plus-hex-<see cref="TextBox"/> pairs in the Inspector.
/// When the switch is off the well greys but stays visible, per <c>docs/DESIGN.md</c> §4, so the
/// user can see the colour a feature would use before turning it on.
/// </remarks>
public sealed partial class ToggleColorRow : UserControl
{
    private readonly ToggleSwitch _toggle = new()
    {
        OnContent = null,
        OffContent = null,
        MinWidth = 0,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly ColorWell _well = new();
    private readonly EditorRow _row = new();

    private bool _syncing;

    public ToggleColorRow()
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = AppTheme.Spacing.SmMd,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(_toggle);
        content.Children.Add(_well);

        _row.RowContent = content;
        _row.ResetRequested += (_, _) => ResetRequested?.Invoke(this, EventArgs.Empty);

        _toggle.Toggled += (_, _) =>
        {
            if (_syncing) return;
            IsOn = _toggle.IsOn;
            IsOnChanged?.Invoke(this, IsOn);
        };
        _well.ColorChanged += (_, c) => ColorChanged?.Invoke(this, c);

        Content = _row;
        IsTabStop = false;
        Sync();
    }

    public event EventHandler<bool>? IsOnChanged;
    public event EventHandler<TextStyle.RGBA>? ColorChanged;
    public event EventHandler? ResetRequested;

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ToggleColorRow),
        new PropertyMetadata("", (d, _) => ((ToggleColorRow)d).Sync()));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public static readonly DependencyProperty LabelHelpProperty = DependencyProperty.Register(
        nameof(LabelHelp), typeof(string), typeof(ToggleColorRow),
        new PropertyMetadata(null, (d, _) => ((ToggleColorRow)d).Sync()));

    public string? LabelHelp
    {
        get => (string?)GetValue(LabelHelpProperty);
        set => SetValue(LabelHelpProperty, value);
    }

    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(ToggleColorRow),
        new PropertyMetadata(false, (d, _) => ((ToggleColorRow)d).Sync()));

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    public static readonly DependencyProperty ColorValueProperty = DependencyProperty.Register(
        nameof(ColorValue), typeof(TextStyle.RGBA), typeof(ToggleColorRow),
        new PropertyMetadata(null, (d, _) => ((ToggleColorRow)d).Sync()));

    public TextStyle.RGBA? ColorValue
    {
        get => (TextStyle.RGBA?)GetValue(ColorValueProperty);
        set => SetValue(ColorValueProperty, value);
    }

    public static readonly DependencyProperty CanResetProperty = DependencyProperty.Register(
        nameof(CanReset), typeof(bool), typeof(ToggleColorRow),
        new PropertyMetadata(false, (d, _) => ((ToggleColorRow)d).Sync()));

    public bool CanReset
    {
        get => (bool)GetValue(CanResetProperty);
        set => SetValue(CanResetProperty, value);
    }

    private void Sync()
    {
        _syncing = true;
        _row.Label = Label;
        _row.LabelHelp = LabelHelp;
        _row.CanReset = CanReset;

        _toggle.IsOn = IsOn;
        AutomationProperties.SetName(_toggle, $"Enable {Label}");
        ToolTipService.SetToolTip(_toggle, $"Enable {Label}");

        _well.ColorValue = ColorValue;
        _well.Purpose = string.IsNullOrWhiteSpace(Label) ? "Color" : $"{Label} color";

        // Off: greyed but visible and still readable (DESIGN.md §4).
        _well.Opacity = IsOn ? AppTheme.Opacity.Opaque : AppTheme.Opacity.Strong;
        _well.IsHitTestVisible = IsOn;
        _syncing = false;
    }
}
