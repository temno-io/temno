using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Temno.App.Theme;
using Temno.App.ViewModels;
using Temno.Core.Captions;
using Temno.Core.Models;
using Temno.Media.Text;
using Windows.System;

namespace Temno.App.Controls;

/// <summary>
/// Mac <c>CaptionPresetGallery</c>: an adaptive grid of cells that show the real renderer, animating
/// on hover/selection/focus and holding a poster frame at rest.
/// </summary>
/// <remarks>
/// Built in code (no companion XAML). Hosted in the Captions Animation stack.
/// </remarks>
public sealed class CaptionPresetGallery : UserControl
{
    public const double CellWidth = CaptionPresetGalleryLayout.PreferredCellWidth;
    public const double CellHeight = CaptionPresetGalleryLayout.PreferredCellHeight;

    private readonly IReadOnlyList<CaptionPresetItem> _items;
    private CaptionPresetGalleryLayout _layout = CaptionPresetGalleryLayout.Preferred;
    private (int W, int H) _lastCellPixels;
    private readonly Dictionary<CaptionPresetItem, CellVisual> _visuals = [];
    private readonly Dictionary<CaptionPresetItem, string> _configKeys = [];
    private readonly TextBlock _perLineHeader;
    private readonly TextBlock _perWordHeader;
    private readonly Grid _perLineHost;
    private readonly Grid _perWordHost;
    private readonly SolidColorBrush _transparent = new(Microsoft.UI.Colors.Transparent);

    private TextStyle.RGBA? _highlight;
    private bool _windowActive = true;
    private bool _inViewport;
    private bool _ancestorsVisible = true;
    private bool _hookedWindow;
    private bool _built;
    private bool _layoutQueued;
    private XamlRoot? _hookedRoot;

    public CaptionPresetGallery()
    {
        IsTabStop = false;
        AutomationProperties.SetName(this, "Caption animation presets");

        _perLineHeader = MakeHeader("Per line");
        _perWordHeader = MakeHeader("Per word");
        _perLineHost = new Grid();
        _perWordHost = new Grid();

        var perLine = new StackPanel { Spacing = AppTheme.Spacing.Sm };
        perLine.Children.Add(_perLineHeader);
        perLine.Children.Add(_perLineHost);

        var perWord = new StackPanel { Spacing = AppTheme.Spacing.Sm };
        perWord.Children.Add(_perWordHeader);
        perWord.Children.Add(_perWordHost);

        var root = new StackPanel { Spacing = AppTheme.Spacing.Lg };
        root.Children.Add(perLine);
        root.Children.Add(perWord);
        Content = root;

        _items = CaptionPresetItem.FromGalleryLists();
        foreach (var item in _items)
            item.PropertyChanged += Item_PropertyChanged;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsEnabledChanged += (_, _) => RefreshLive();
        EffectiveViewportChanged += OnViewportChanged;
        SizeChanged += OnSizeChanged;
        ActualThemeChanged += (_, _) => ReconfigureAll();
    }

    public event EventHandler? SelectionChanged;

    public static readonly DependencyProperty SelectedPresetProperty = DependencyProperty.Register(
        nameof(SelectedPreset), typeof(string), typeof(CaptionPresetGallery),
        new PropertyMetadata("none", OnSelectedPresetChanged));

    public string SelectedPreset
    {
        get => (string)GetValue(SelectedPresetProperty);
        set => SetValue(SelectedPresetProperty, value);
    }

    /// <summary>Feeds per-word cells. Null uses <see cref="TextAnimation.DefaultHighlight"/>.</summary>
    public TextStyle.RGBA? Highlight
    {
        get => _highlight;
        set
        {
            _highlight = value;
            ReconfigureHighlightCells();
        }
    }

    public IReadOnlyList<CaptionPresetItem> Items => _items;

    private static TextBlock MakeHeader(string text) => new()
    {
        Text = text,
        FontSize = AppTheme.FontSize.Xs,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = AppTheme.Text.TertiaryBrush,
    };

    private static void OnSelectedPresetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gallery = (CaptionPresetGallery)d;
        gallery.SyncSelection();
        gallery.SelectionChanged?.Invoke(gallery, EventArgs.Empty);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookWindow(true);
        HookXamlRoot(true);
        SyncSelection();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _ancestorsVisible = AncestorsVisible();
            RefreshLive();
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        HookWindow(false);
        HookXamlRoot(false);
        _inViewport = false;
        _ancestorsVisible = false;
        RefreshLive();
    }

    private void EnsureCells()
    {
        if (_built) return;
        try
        {
            PrepareHost(_perLineHost);
            PrepareHost(_perWordHost);

            foreach (var item in _items)
            {
                var visual = BuildCell(item);
                _visuals[item] = visual;
                var host = item.Group == CaptionPresetItem.PerLineGroup ? _perLineHost : _perWordHost;
                host.Children.Add(visual.Button);
            }

            RelayoutHosts();
            _built = true;
        }
        catch
        {
            _perLineHost.Children.Clear();
            _perWordHost.Children.Clear();
            _visuals.Clear();
        }
    }

    private static void PrepareHost(Grid host)
    {
        host.ColumnDefinitions.Clear();
        host.RowDefinitions.Clear();
        host.ColumnSpacing = AppTheme.Spacing.Sm;
        host.RowSpacing = AppTheme.Spacing.Sm;
    }

    private void RelayoutHosts()
    {
        var perLine = _items.Count(i => i.Group == CaptionPresetItem.PerLineGroup);
        var perWord = _items.Count(i => i.Group == CaptionPresetItem.PerWordGroup);
        _layout = CaptionPresetGalleryLayout.ForWidth(
            Math.Max(ActualWidth, 1), Math.Max(perLine, perWord), AppTheme.Spacing.Sm);
        ApplyCellSize(_layout.CellWidth, _layout.CellHeight);
        LayoutHost(_perLineHost, perLine, _layout.Columns);
        LayoutHost(_perWordHost, perWord, _layout.Columns);
    }

    private void ApplyCellSize(double width, double height)
    {
        var pxW = Math.Max(1, (int)Math.Round(width));
        var pxH = Math.Max(1, (int)Math.Round(height));
        foreach (var visual in _visuals.Values)
        {
            visual.Preview.Width = width;
            visual.Preview.Height = height;
            visual.Tile.Width = width;
            visual.Tile.Height = height;
            visual.Button.MinWidth = width;
            ApplyRenderSize(visual.Preview);
        }

        if (_lastCellPixels == (pxW, pxH))
            return;
        _lastCellPixels = (pxW, pxH);
        if (_built)
            ReconfigureAll();
    }

    private static void LayoutHost(Grid host, int count, int columns)
    {
        columns = Math.Clamp(columns, 1, Math.Max(1, count));
        host.ColumnDefinitions.Clear();
        host.RowDefinitions.Clear();
        for (var c = 0; c < columns; c++)
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var rows = Math.Max(1, (int)Math.Ceiling(count / (double)columns));
        for (var r = 0; r < rows; r++)
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var i = 0;
        foreach (var child in host.Children.OfType<FrameworkElement>())
        {
            Grid.SetColumn(child, i % columns);
            Grid.SetRow(child, i / columns);
            i++;
        }
    }

    private CellVisual BuildCell(CaptionPresetItem item)
    {
        var preview = new CaptionPreviewImage
        {
            Width = CellWidth,
            Height = CellHeight,
            IsHitTestVisible = false,
        };

        var tile = new Border
        {
            Width = CellWidth,
            Height = CellHeight,
            Background = AppTheme.Background.PreviewCanvasBrush,
            BorderBrush = AppTheme.Border.SubtleBrush,
            BorderThickness = new Thickness(AppTheme.BorderWidth.Thin),
            CornerRadius = new CornerRadius(AppTheme.Radius.Sm),
            Child = preview,
        };

        var label = new TextBlock
        {
            Text = item.DisplayName,
            FontSize = AppTheme.FontSize.Xs,
            Foreground = AppTheme.Text.TertiaryBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var stack = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
        stack.Children.Add(tile);
        stack.Children.Add(label);

        var button = new Button
        {
            Content = stack,
            MinWidth = CellWidth,
            MinHeight = 0,
            Padding = AppTheme.Padding.None,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Background = _transparent,
            BorderBrush = _transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Tag = item,
            UseSystemFocusVisuals = true,
        };
        FlattenButton(button);
        AutomationProperties.SetName(button, item.DisplayName);
        ToolTipService.SetToolTip(button, item.DisplayName);
        button.Click += Cell_Click;
        button.PointerEntered += Cell_PointerEntered;
        button.PointerExited += Cell_PointerExited;
        button.GotFocus += Cell_GotFocus;
        button.LostFocus += Cell_LostFocus;
        button.KeyDown += Cell_KeyDown;

        var visual = new CellVisual(button, tile, label, preview);
        preview.Loaded += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                ConfigureItem(item, force: true));
        };
        return visual;
    }

    private void FlattenButton(Button button)
    {
        button.Resources["ButtonBackground"] = _transparent;
        button.Resources["ButtonBackgroundPointerOver"] = _transparent;
        button.Resources["ButtonBackgroundPressed"] = _transparent;
        button.Resources["ButtonBackgroundDisabled"] = _transparent;
        button.Resources["ButtonBorderBrush"] = _transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = _transparent;
        button.Resources["ButtonBorderBrushPressed"] = _transparent;
        button.Resources["ButtonBorderBrushDisabled"] = _transparent;
    }

    private void HookWindow(bool on)
    {
        if (App.MainAppWindow is not { } window) return;
        if (on && !_hookedWindow)
        {
            window.Activated += OnWindowActivated;
            _hookedWindow = true;
            _windowActive = true;
        }
        else if (!on && _hookedWindow)
        {
            window.Activated -= OnWindowActivated;
            _hookedWindow = false;
        }
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs e)
    {
        _windowActive = e.WindowActivationState != WindowActivationState.Deactivated;
        RefreshLive();
    }

    private void HookXamlRoot(bool on)
    {
        if (on)
        {
            if (XamlRoot is null || ReferenceEquals(_hookedRoot, XamlRoot)) return;
            HookXamlRoot(false);
            _hookedRoot = XamlRoot;
            _hookedRoot.Changed += OnXamlRootChanged;
        }
        else if (_hookedRoot is not null)
        {
            _hookedRoot.Changed -= OnXamlRootChanged;
            _hookedRoot = null;
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        => ReconfigureAll();

    private void OnViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        var view = args.EffectiveViewport;
        _inViewport = view.Width > 1 && view.Height > 1;
        _ancestorsVisible = AncestorsVisible();
        RefreshLive();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_built) return;
        _ancestorsVisible = AncestorsVisible();
        RefreshLive();
        if (e.NewSize.Width < 1) return;
        if (_layoutQueued) return;
        _layoutQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _layoutQueued = false;
            RelayoutHosts();
        });
    }

    private void RefreshLive()
    {
        var live = IsLoaded && IsEnabled && _windowActive && _ancestorsVisible
            && Visibility == Visibility.Visible
            && (_inViewport || ActualWidth > 8);
        if (IsLoaded && live)
            EnsureCells();
        foreach (var item in _items)
            item.IsLive = live;
    }

    private bool AncestorsVisible()
    {
        DependencyObject? p = this;
        while (p is not null)
        {
            if (p is UIElement el && el.Visibility != Visibility.Visible)
                return false;
            p = VisualTreeHelper.GetParent(p);
        }
        return true;
    }

    private void SyncSelection()
    {
        var selected = TextAnimation.GallerySelection(SelectedPreset);
        foreach (var item in _items)
            item.IsSelected = selected is not null
                && string.Equals(item.Preset, selected, StringComparison.Ordinal);
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CaptionPresetItem item) return;
        if (e.PropertyName is nameof(CaptionPresetItem.ShouldAnimate))
            ApplyAnimate(item);
        if (e.PropertyName is nameof(CaptionPresetItem.IsSelected))
            ApplyChrome(item);
    }

    private void Cell_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        SelectedPreset = item.Preset;
    }

    private void Cell_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) item.IsPointerOver = true;
    }

    private void Cell_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) item.IsPointerOver = false;
    }

    private void Cell_GotFocus(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) item.IsFocused = true;
    }

    private void Cell_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) item.IsFocused = false;
    }

    private void Cell_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        var index = IndexOf(item);
        if (index < 0) return;

        var delta = e.Key switch
        {
            VirtualKey.Left => -1,
            VirtualKey.Right => 1,
            VirtualKey.Up => -ColumnCount(),
            VirtualKey.Down => ColumnCount(),
            _ => 0,
        };
        if (delta == 0) return;

        var next = Math.Clamp(index + delta, 0, _items.Count - 1);
        if (next != index)
            _visuals[_items[next]].Button.Focus(FocusState.Keyboard);
        e.Handled = true;
    }

    private int ColumnCount() => Math.Max(1, _layout.Columns);

    private int IndexOf(CaptionPresetItem item)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (ReferenceEquals(_items[i], item)) return i;
        }
        return -1;
    }

    private void ConfigureItem(CaptionPresetItem item, bool force)
    {
        if (!_visuals.TryGetValue(item, out var visual)) return;
        var preview = visual.Preview;
        if (!preview.IsLoaded) return;

        try
        {
            ApplyRenderSize(preview);
            var key = $"{item.Preset}|{preview.RenderPixelWidth}x{preview.RenderPixelHeight}|{HighlightKey()}";
            if (!force && _configKeys.TryGetValue(item, out var previous) && previous == key)
                return;

            _configKeys[item] = key;
            preview.Configure(
                CaptionPreview.GallerySample,
                GalleryStyle(),
                CaptionPreview.GalleryTransform(),
                TextAnimation.GalleryPreviewAnimation(item.Preset, Highlight));
            ApplyAnimate(item);
            ApplyChrome(item);
        }
        catch
        {
            // A gallery cell must never take the editor down.
        }
    }

    private static void ApplyRenderSize(CaptionPreviewImage preview)
    {
        // CSS pixels. CaptionPreviewImage multiplies by RasterizationScale (WP-130).
        preview.RenderPixelWidth = Math.Max(1, (int)Math.Round(preview.Width));
        preview.RenderPixelHeight = Math.Max(1, (int)Math.Round(preview.Height));
    }

    private void ApplyAnimate(CaptionPresetItem item)
    {
        if (_visuals.TryGetValue(item, out var visual))
            visual.Preview.Animate = item.ShouldAnimate;
    }

    private void ApplyChrome(CaptionPresetItem item)
    {
        if (!_visuals.TryGetValue(item, out var visual)) return;
        var selected = item.IsSelected;
        visual.Tile.BorderBrush = selected
            ? AppTheme.Accent.TimecodeBrush
            : AppTheme.Border.SubtleBrush;
        visual.Tile.BorderThickness = new Thickness(
            selected ? AppTheme.BorderWidth.Thick : AppTheme.BorderWidth.Thin);
        visual.Label.FontWeight = selected
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
        visual.Label.Foreground = selected
            ? AppTheme.Text.PrimaryBrush
            : AppTheme.Text.TertiaryBrush;
        AutomationProperties.SetName(
            visual.Button,
            selected ? item.DisplayName + ", selected" : item.DisplayName);
    }

    private void ReconfigureAll()
    {
        foreach (var item in _items)
            ConfigureItem(item, force: true);
    }

    private void ReconfigureHighlightCells()
    {
        foreach (var item in _items)
        {
            if (TextAnimation.UsesHighlight(item.Preset))
                ConfigureItem(item, force: true);
        }
    }

    private string HighlightKey()
    {
        var h = Highlight ?? TextAnimation.DefaultHighlight;
        return $"{h.R:0.###},{h.G:0.###},{h.B:0.###},{h.A:0.###}";
    }

    private static TextStyle GalleryStyle() => new()
    {
        FontSize = 300,
        Color = new TextStyle.RGBA { R = 1, G = 1, B = 1, A = 1 },
        Shadow = { Enabled = false },
        Alignment = Temno.Core.Models.TextAlignment.Center,
    };

    private static CaptionPresetItem? ItemOf(object sender)
        => sender is FrameworkElement fe ? fe.Tag as CaptionPresetItem : null;

    private sealed record CellVisual(
        Button Button, Border Tile, TextBlock Label, CaptionPreviewImage Preview);
}
