using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Temno.App.Controls.Shared;

/// <summary>
/// Fluent 16/24-canvas geometry scaled into Width × Height.
/// PathIcon / Path+Stretch / an unconstrained Viewbox treat the slot as a clip, so a 16-unit
/// glyph in a 12–14 slot loses the grid's bottom row, the camera body, and the person bust.
/// This Grid owns the requested slot while the Path remains unconstrained; only the Geometry is
/// scaled, keeping the drawing and its outer clip in the same coordinate space.
/// </summary>
public sealed class FluentIcon : Grid
{
    private readonly Canvas _canvas = new()
    {
        Width = 0,
        Height = 0,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private readonly Microsoft.UI.Xaml.Shapes.Path _glyph = new()
    {
        Stretch = Stretch.None,
    };

    public FluentIcon()
    {
        IsHitTestVisible = false;
        UseLayoutRounding = true;
        Background = null;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_canvas);
        _canvas.Children.Add(_glyph);
        Loaded += (_, _) => SyncScale();
        SizeChanged += (_, _) => SyncScale();
        RegisterPropertyChangedCallback(WidthProperty, (_, _) => SyncScale());
        RegisterPropertyChangedCallback(HeightProperty, (_, _) => SyncScale());
    }

    public IconDefinition? Icon
    {
        get => (IconDefinition?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(object),
        typeof(FluentIcon),
        new PropertyMetadata(null, (d, e) =>
        {
            var icon = (FluentIcon)d;
            icon._renderedIcon = null;
            icon.SyncScale();
        }));

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill),
        typeof(Brush),
        typeof(FluentIcon),
        new PropertyMetadata(null, (d, e) => ((FluentIcon)d)._glyph.Fill = e.NewValue as Brush));

    private IconDefinition? _renderedIcon;
    private double _renderedSize;

    private void SyncScale()
    {
        var w = Slot(Width, ActualWidth);
        var h = Slot(Height, ActualHeight);
        if (w <= 0 || h <= 0)
        {
            return;
        }

        MaxWidth = w;
        MaxHeight = h;
        _canvas.Width = w;
        _canvas.Height = h;
        Clip = new RectangleGeometry { Rect = new Rect(0, 0, w, h) };
        var display = Math.Min(w, h);
        if (Icon is { } icon && (_renderedIcon != icon || _renderedSize != display))
        {
            _glyph.Data = icon.CreateGeometry(display);
            _renderedIcon = icon;
            _renderedSize = display;
        }
        else if (Icon is null) _glyph.Data = null;
    }

    private static double Slot(double explicitSize, double actual)
    {
        if (!double.IsNaN(explicitSize) && explicitSize > 0)
        {
            return explicitSize;
        }

        return actual > 0 ? actual : 0;
    }
}
