using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Temno.App.Controls.Shared;

internal static class FluentPath
{
    public static Geometry Parse(string data) =>
        (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data);

    public static FluentIcon At(IconDefinition icon, double display, Brush fill)
    {
        return new FluentIcon
        {
            Width = display,
            Height = display,
            Fill = fill,
            Icon = icon,
        };
    }
}
