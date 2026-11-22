using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Temno.App.Theme;
using Temno.Core.Models;
using Temno.Core.Session;
using Windows.System;
using Windows.UI;

namespace Temno.App.Controls.Shared;

/// <summary>
/// A colour swatch that opens a picker. Mac <c>ColorField</c>; see <c>docs/DESIGN.md</c> §3.
/// </summary>
/// <remarks>
/// <para>
/// Replaces typing a hex string into a <see cref="TextBox"/> — the sharpest single example of the
/// editor's form-not-product problem (<c>InspectorControl.xaml:461,463,465</c>). Hex is still
/// reachable, but inside the flyout, where it is a power-user affordance rather than the only way in.
/// </para>
/// <para>
/// The model value is <see cref="TextStyle.RGBA"/>. Conversion uses
/// <see cref="TextClipCommands.ParseHexColor"/> / <see cref="TextClipCommands.ToHexColor"/>, so an
/// untouched colour re-encodes to the identical string and unchanged project JSON stays
/// byte-identical.
/// </para>
/// </remarks>
public sealed partial class ColorWell : UserControl
{
    private readonly Border _swatch = new()
    {
        Width = 28,
        Height = 20,
        CornerRadius = new CornerRadius(AppTheme.Radius.XsSm),
        BorderThickness = new Thickness(AppTheme.BorderWidth.Thin),
        BorderBrush = AppTheme.Border.PrimaryBrush,
    };

    private readonly Button _button;
    private readonly TextBlock _valueLabel = new()
    {
        FontSize = AppTheme.FontSize.EditorValue,
        Foreground = AppTheme.Text.SecondaryBrush,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };

    public bool ShowValue
    {
        get => _valueLabel.Visibility == Visibility.Visible;
        set => _valueLabel.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
    private readonly ColorPicker _picker = new()
    {
        IsAlphaEnabled = true,
        // WinUI's own hex box prints ARGB (#FFF5EFE4); the models store #RRGGBB[AA]. Showing both
        // put two hex fields in one flyout disagreeing about format, so the built-in one is off and
        // the field below is the single hex surface, in the format a project actually stores.
        IsHexInputVisible = false,
        IsColorChannelTextInputVisible = false,
        ColorSpectrumShape = ColorSpectrumShape.Box,
    };

    private readonly TextBox _hex = new()
    {
        FontSize = AppTheme.FontSize.SmMd,
        PlaceholderText = "#RRGGBB",
        Header = "Hex",
        Margin = new Thickness(AppTheme.Spacing.None, AppTheme.Spacing.Sm, AppTheme.Spacing.None, AppTheme.Spacing.None),
    };

    private bool _syncing;
    private readonly Action _flashCommit;

    public ColorWell()
    {
        _button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = AppTheme.Spacing.SmMd,
                Children = { _swatch, _valueLabel },
            },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(AppTheme.Spacing.Xxs),
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(AppTheme.Radius.XsSm),
        };

        var panel = new StackPanel { Spacing = AppTheme.Spacing.Xs };
        panel.Children.Add(_picker);
        panel.Children.Add(_hex);

        var scroll = new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        var flyout = new Flyout { Content = scroll };
        WindowPopout.AttachFlyout(flyout, _button, scroll, Temno.Core.Ui.PopoutSide.Bottom, Temno.Core.Ui.PopoutAlignment.Center);
        _button.Flyout = flyout;

        _picker.ColorChanged += OnPickerChanged;
        _hex.KeyDown += OnHexKeyDown;
        _hex.LostFocus += (_, _) => CommitHex();

        Content = _button;
        IsTabStop = false;
        Motion.AttachHoverFill(_button);
        _flashCommit = Motion.AttachCommitFlash(_swatch);
        Sync();
    }

    /// <summary>Raised after a committed colour change.</summary>
    public event EventHandler<TextStyle.RGBA>? ColorChanged;

    public static readonly DependencyProperty ColorValueProperty = DependencyProperty.Register(
        nameof(ColorValue), typeof(TextStyle.RGBA), typeof(ColorWell),
        new PropertyMetadata(null, (d, _) => ((ColorWell)d).Sync()));

    /// <summary>The model colour. Null renders an empty well and is treated as opaque white.</summary>
    public TextStyle.RGBA? ColorValue
    {
        get => (TextStyle.RGBA?)GetValue(ColorValueProperty);
        set => SetValue(ColorValueProperty, value);
    }

    /// <summary>Label used for the automation name, e.g. "Highlight color".</summary>
    public static readonly DependencyProperty PurposeProperty = DependencyProperty.Register(
        nameof(Purpose), typeof(string), typeof(ColorWell),
        new PropertyMetadata("Color", (d, _) => ((ColorWell)d).Sync()));

    public string Purpose
    {
        get => (string)GetValue(PurposeProperty);
        set => SetValue(PurposeProperty, value);
    }

    /// <summary>The current colour as the hex string the models store.</summary>
    public string Hex => TextClipCommands.ToHexColor(ColorValue ?? Opaque());

    private static TextStyle.RGBA Opaque() => new() { R = 1, G = 1, B = 1, A = 1 };

    private void Commit(TextStyle.RGBA value)
    {
        var before = Hex;
        ColorValue = value;
        if (!string.Equals(before, Hex, StringComparison.Ordinal))
        {
            ColorChanged?.Invoke(this, value);
            _flashCommit();
        }
    }

    private void OnPickerChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_syncing) return;
        Commit(new TextStyle.RGBA
        {
            R = args.NewColor.R / 255.0,
            G = args.NewColor.G / 255.0,
            B = args.NewColor.B / 255.0,
            A = args.NewColor.A / 255.0,
        });
    }

    private void OnHexKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not VirtualKey.Enter) return;
        CommitHex();
        e.Handled = true;
    }

    private void CommitHex()
    {
        if (_syncing) return;
        try
        {
            var parsed = TextClipCommands.ParseHexColor(_hex.Text);
            if (parsed is not null) Commit(parsed);
            else Sync();
        }
        catch (FormatException)
        {
            Sync();   // reject rather than write a guess
        }
    }

    private void Sync()
    {
        _syncing = true;
        var rgba = ColorValue ?? Opaque();
        static byte Channel(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        var color = Color.FromArgb(Channel(rgba.A), Channel(rgba.R), Channel(rgba.G), Channel(rgba.B));

        _swatch.Background = new SolidColorBrush(color);
        _picker.Color = color;
        _hex.Text = TextClipCommands.ToHexColor(rgba);
        _valueLabel.Text = _hex.Text;

        var name = $"{Purpose}, {_hex.Text}";
        AutomationProperties.SetName(_button, name);
        ToolTipService.SetToolTip(_button, name);
        AutomationProperties.SetName(_hex, $"{Purpose} hex value");
        _syncing = false;
    }
}
