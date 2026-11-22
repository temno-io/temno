using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Temno.App.Theme;

namespace Temno.App.Controls.Shared;

/// <summary>
/// One labelled setting: label column, value column, and an optional per-row reset.
/// Mac <c>InspectorRow(label:labelHelp:)</c>; see <c>docs/DESIGN.md</c> §3.
/// </summary>
/// <remarks>
/// Replaces the <c>Header=</c>-stacked pattern (231 uses app-wide). The label is a real column, so
/// values line up down a panel instead of each control carrying its own stacked caption.
/// </remarks>
public sealed partial class EditorRow : UserControl
{
    /// <summary>Label column width. Mac aligns values on a fixed gutter; this is its Windows twin.</summary>
    public const double LabelColumnWidth = AppTheme.ComponentSize.EditorLabelColumnWidth;

    private readonly TextBlock _label = new()
    {
        FontSize = AppTheme.FontSize.EditorLabel,
        Foreground = AppTheme.Text.SecondaryBrush,
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly ContentPresenter _value = new()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Button _reset;
    private readonly Grid _grid;
    private readonly ContentPresenter _accessory = new() { Visibility = Visibility.Collapsed };

    public EditorRow()
    {
        _reset = new Button
        {
            Content = FluentPath.At(Icons.ArrowClockwise,
                AppTheme.IconSize.Xs,
                AppTheme.Text.TertiaryBrush),
            Padding = new Thickness(AppTheme.Spacing.Xxs),
            MinWidth = 0,
            MinHeight = 0,
            Background = null,
            BorderThickness = new Thickness(0),
            Foreground = AppTheme.Text.TertiaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            // WP-196: reserve the cell when there is nothing to reset so rows with a reset
            // button end at the same right edge as rows without one. WinUI has no Hidden:
            // keep the footprint with a transparent, non-hit-testable button instead.
            IsTabStop = false,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _reset.Click += (_, _) => ResetRequested?.Invoke(this, EventArgs.Empty);
        ToolTipService.SetToolTip(_reset, "Reset to default");
        AutomationProperties.SetName(_reset, "Reset to default");
        Motion.AttachHoverFill(_reset);

        _grid = new Grid
        {
            ColumnSpacing = AppTheme.Spacing.SmMd,
            Padding = new Thickness(AppTheme.Spacing.None, AppTheme.Spacing.Xxs, AppTheme.Spacing.None, AppTheme.Spacing.Xxs),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(LabelColumnWidth) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        Grid.SetColumn(_label, 0);
        Grid.SetColumn(_value, 1);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = AppTheme.Spacing.Xs,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { new Border { Width = AppTheme.ComponentSize.ResetSlotWidth, Child = _reset }, _accessory },
        };
        Grid.SetColumn(actions, 2);
        _grid.Children.Add(_label);
        _grid.Children.Add(_value);
        _grid.Children.Add(actions);

        Content = _grid;
        IsTabStop = false;
        Motion.AttachSelectionLabel(_label, this);
    }

    /// <summary>Raised when the user clicks the row's reset affordance.</summary>
    public event EventHandler? ResetRequested;

    public static readonly DependencyProperty RowAccessoryProperty = DependencyProperty.Register(
        nameof(RowAccessory), typeof(object), typeof(EditorRow), new PropertyMetadata(null, (d, e) =>
        {
            var row = (EditorRow)d;
            row._accessory.Content = e.NewValue;
            row._accessory.Visibility = e.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
        }));

    public object? RowAccessory
    {
        get => GetValue(RowAccessoryProperty);
        set => SetValue(RowAccessoryProperty, value);
    }

    public bool ShowLabel
    {
        get => _label.Visibility == Visibility.Visible;
        set
        {
            _label.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _grid.ColumnDefinitions[0].Width = new GridLength(value ? LabelColumnWidth : 0);
            Grid.SetColumn(_value, value ? 1 : 0);
            Grid.SetColumnSpan(_value, value ? 1 : 2);
        }
    }

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(EditorRow),
        new PropertyMetadata("", (d, _) => ((EditorRow)d).ApplyLabel()));

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Per-row help, shown as a tooltip on the label (Mac <c>labelHelp</c>).</summary>
    public static readonly DependencyProperty LabelHelpProperty = DependencyProperty.Register(
        nameof(LabelHelp), typeof(string), typeof(EditorRow),
        new PropertyMetadata(null, (d, _) => ((EditorRow)d).ApplyLabel()));

    public string? LabelHelp
    {
        get => (string?)GetValue(LabelHelpProperty);
        set => SetValue(LabelHelpProperty, value);
    }

    /// <summary>The control this row labels.</summary>
    public static readonly DependencyProperty RowContentProperty = DependencyProperty.Register(
        nameof(RowContent), typeof(object), typeof(EditorRow),
        new PropertyMetadata(null, (d, e) => ((EditorRow)d)._value.Content = e.NewValue));

    public object? RowContent
    {
        get => GetValue(RowContentProperty);
        set => SetValue(RowContentProperty, value);
    }

    /// <summary>
    /// Whether the reset affordance is offered. Bind this to the field's own
    /// <c>DiffersFromDefault</c> so a row at its default shows nothing —
    /// <c>docs/DESIGN.md</c> §3.
    /// </summary>
    public static readonly DependencyProperty CanResetProperty = DependencyProperty.Register(
        nameof(CanReset), typeof(bool), typeof(EditorRow),
        new PropertyMetadata(false, (d, e) => ((EditorRow)d).ApplyResetVisibility((bool)e.NewValue)));

    public bool CanReset
    {
        get => (bool)GetValue(CanResetProperty);
        set => SetValue(CanResetProperty, value);
    }

    private void ApplyLabel()
    {
        _label.Text = Label ?? "";
        ToolTipService.SetToolTip(_reset, $"Reset {Label} to default");
        AutomationProperties.SetName(_reset, $"Reset {Label} to default");
        var help = LabelHelp;
        if (string.IsNullOrWhiteSpace(help))
        {
            ToolTipService.SetToolTip(_label, null);
            AutomationProperties.SetHelpText(this, "");
        }
        else
        {
            ToolTipService.SetToolTip(_label, help);
            AutomationProperties.SetHelpText(this, help);
        }
        AutomationProperties.SetName(this, Label ?? "");
    }

    /// <summary>
    /// WP-196: WinUI has no <c>Visibility.Hidden</c>, so the reset column is reserved by keeping
    /// the button laid out but transparent and non-hit-testable instead of collapsing it.
    /// </summary>
    private void ApplyResetVisibility(bool canReset)
    {
        _reset.Visibility = canReset ? Visibility.Visible : Visibility.Collapsed;
        _reset.IsEnabled = canReset;
        _reset.IsHitTestVisible = canReset;
        _reset.IsTabStop = canReset;
    }
}
