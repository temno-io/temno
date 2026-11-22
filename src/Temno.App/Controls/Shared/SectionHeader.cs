using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Temno.App.Theme;
using Temno.Core.Ui;
using Windows.System;

namespace Temno.App.Controls.Shared;

/// <summary>
/// A collapsible panel section with an optional enable toggle in its header.
/// Mac <c>EditorPanelGroup</c>; screenshots <c>10</c> and <c>11</c>.
/// </summary>
/// <remarks>
/// <para>
/// When the header toggle is off the child rows <b>grey out but stay visible</b>
/// (<c>docs/DESIGN.md</c> §4) so the user can see what enabling the section would offer. That is one
/// opacity on the content host, not per-child disabling.
/// </para>
/// <para>Expand state is remembered per <see cref="SectionId"/> via <see cref="SectionExpandState"/>.</para>
/// </remarks>
public sealed partial class SectionHeader : UserControl
{
    private readonly FluentIcon _chevron = FluentPath.At(Icons.ChevronDown,
                AppTheme.IconSize.Xs,
        AppTheme.Text.TertiaryBrush);

    private readonly TextBlock _title = new()
    {
        FontSize = AppTheme.FontSize.Md,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = AppTheme.Text.PrimaryBrush,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly ToggleSwitch _toggle = new()
    {
        MinWidth = 0,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };

    private readonly Button _headerButton;
    private readonly ContentPresenter _body = new();
    private readonly Border _clipHost = new();
    private readonly StackPanel _root;

    private bool _syncingToggle;
    private bool _motionReady;
    private bool _visualExpanded = true;

    public SectionHeader()
    {
        var headerContent = new Grid
        {
            ColumnSpacing = AppTheme.Spacing.Sm,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            },
        };
        Grid.SetColumn(_chevron, 0);
        Grid.SetColumn(_title, 1);
        headerContent.Children.Add(_chevron);
        headerContent.Children.Add(_title);

        _headerButton = new Button
        {
            Content = headerContent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = null,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(AppTheme.Spacing.None, AppTheme.Spacing.Xs, AppTheme.Spacing.None, AppTheme.Spacing.Xs),
        };
        _headerButton.Click += (_, _) => IsExpanded = !IsExpanded;

        var headerRow = new Grid
        {
            ColumnSpacing = AppTheme.Spacing.Sm,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        Grid.SetColumn(_headerButton, 0);
        Grid.SetColumn(_toggle, 1);
        headerRow.Children.Add(_headerButton);
        headerRow.Children.Add(_toggle);

        _toggle.Toggled += OnToggled;
        Motion.AttachHoverFill(_headerButton);
        _chevron.RotationTransition = Motion.Scalar(Motion.Transition);
        _chevron.SizeChanged += (_, _) =>
        {
            var w = (float)_chevron.ActualWidth;
            var h = (float)_chevron.ActualHeight;
            if (w > 0 && h > 0)
                _chevron.CenterPoint = new System.Numerics.Vector3(w / 2f, h / 2f, 0);
        };

        _clipHost.Child = _body;
        _clipHost.HorizontalAlignment = HorizontalAlignment.Stretch;

        _root = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
        _root.Children.Add(headerRow);
        _root.Children.Add(_clipHost);

        Content = _root;
        IsTabStop = false;
        Loaded += OnLoaded;
    }

    /// <summary>Raised when the header toggle changes. Not raised while syncing from the property.</summary>
    public event EventHandler<bool>? IsOnChanged;

    /// <summary>Stable id used to remember expand state. Sections without one are not remembered.</summary>
    public static readonly DependencyProperty SectionIdProperty = DependencyProperty.Register(
        nameof(SectionId), typeof(string), typeof(SectionHeader), new PropertyMetadata(null));

    public string? SectionId
    {
        get => (string?)GetValue(SectionIdProperty);
        set => SetValue(SectionIdProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SectionHeader),
        new PropertyMetadata("", (d, _) => ((SectionHeader)d).ApplyHeader()));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty SectionContentProperty = DependencyProperty.Register(
        nameof(SectionContent), typeof(object), typeof(SectionHeader),
        new PropertyMetadata(null, (d, e) => ((SectionHeader)d)._body.Content = e.NewValue));

    public object? SectionContent
    {
        get => GetValue(SectionContentProperty);
        set => SetValue(SectionContentProperty, value);
    }

    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(SectionHeader),
        new PropertyMetadata(true, (d, _) => ((SectionHeader)d).ApplyExpanded(remember: true)));

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>Show a toggle in the header (Mac's per-section enable switch).</summary>
    public static readonly DependencyProperty HasToggleProperty = DependencyProperty.Register(
        nameof(HasToggle), typeof(bool), typeof(SectionHeader),
        new PropertyMetadata(false, (d, _) => ((SectionHeader)d).ApplyHeader()));

    public bool HasToggle
    {
        get => (bool)GetValue(HasToggleProperty);
        set => SetValue(HasToggleProperty, value);
    }

    /// <summary>Toggle state. When false the body greys but stays visible.</summary>
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.Register(
        nameof(IsOn), typeof(bool), typeof(SectionHeader),
        new PropertyMetadata(true, (d, _) => ((SectionHeader)d).ApplyHeader()));

    public bool IsOn
    {
        get => (bool)GetValue(IsOnProperty);
        set => SetValue(IsOnProperty, value);
    }

    /// <summary>
    /// Optional label beside the header toggle (WP-196: the captions Source section docks its
    /// Preview switch here so the toggle sits on the header baseline and clears the scrollbar
    /// with the rest of the form instead of floating over the section). Null renders a bare switch.
    /// </summary>
    public static readonly DependencyProperty ToggleOnContentProperty = DependencyProperty.Register(
        nameof(ToggleOnContent), typeof(string), typeof(SectionHeader),
        new PropertyMetadata(null, (d, _) => ((SectionHeader)d).ApplyHeader()));

    public string? ToggleOnContent
    {
        get => (string?)GetValue(ToggleOnContentProperty);
        set => SetValue(ToggleOnContentProperty, value);
    }

    /// <summary>Label beside the header toggle when off. Null renders a bare switch.</summary>
    public static readonly DependencyProperty ToggleOffContentProperty = DependencyProperty.Register(
        nameof(ToggleOffContent), typeof(string), typeof(SectionHeader),
        new PropertyMetadata(null, (d, _) => ((SectionHeader)d).ApplyHeader()));

    public string? ToggleOffContent
    {
        get => (string?)GetValue(ToggleOffContentProperty);
        set => SetValue(ToggleOffContentProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Restore remembered expand state without writing it straight back.
        var remembered = SectionExpandState.IsExpanded(SectionId, IsExpanded);
        if (remembered != IsExpanded) IsExpanded = remembered;
        else ApplyExpanded(remember: false);
        ApplyHeader();
        _motionReady = true;
    }

    private void OnToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingToggle) return;
        IsOn = _toggle.IsOn;
        IsOnChanged?.Invoke(this, IsOn);
    }

    private void ApplyExpanded(bool remember)
    {
        var expanded = IsExpanded;
        if (expanded != _visualExpanded || !_motionReady)
        {
            Motion.SetExpanded(_clipHost, expanded, animate: _motionReady);
            _chevron.RotationTransition = Motion.Scalar(Motion.Transition);
            _chevron.Rotation = expanded ? 0 : -90;
            _visualExpanded = expanded;
        }

        AutomationProperties.SetName(_headerButton, $"{Title}, {(expanded ? "expanded" : "collapsed")}");
        if (remember) SectionExpandState.SetExpanded(SectionId, expanded);
    }

    private void ApplyHeader()
    {
        _title.Text = Title ?? "";
        _toggle.Visibility = HasToggle ? Visibility.Visible : Visibility.Collapsed;

        if (HasToggle)
        {
            _syncingToggle = true;
            _toggle.IsOn = IsOn;
            _toggle.OnContent = ToggleOnContent;
            _toggle.OffContent = ToggleOffContent;
            _syncingToggle = false;
            if (ToggleOnContent is null)
            {
                AutomationProperties.SetName(_toggle, $"Enable {Title}");
                ToolTipService.SetToolTip(_toggle, $"Enable {Title}");
            }
        }

        // Section off: grey the body but keep it readable, so the user can see what it offers.
        var enabled = !HasToggle || IsOn;
        _body.Opacity = enabled ? AppTheme.Opacity.Opaque : AppTheme.Opacity.Strong;
        _body.IsHitTestVisible = enabled;

        ApplyExpanded(remember: false);
    }

    /// <summary>Space/Enter on the header toggles expansion; the header is a Button so this is free.</summary>
    protected override void OnKeyDown(KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left && IsExpanded) { IsExpanded = false; e.Handled = true; return; }
        if (e.Key is VirtualKey.Right && !IsExpanded) { IsExpanded = true; e.Handled = true; return; }
        base.OnKeyDown(e);
    }
}
