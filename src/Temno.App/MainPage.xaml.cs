using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Temno.App.Views;

namespace Temno.App;

/// <summary>Compatibility shim — redirects to <see cref="HomePage"/>.</summary>
public sealed partial class MainPage : Page
{
    public MainPage()
    {
        InitializeComponent();
    }

    private void Forward_Loaded(object sender, RoutedEventArgs e)
    {
        if (Frame is not null)
            Frame.Navigate(typeof(HomePage));
    }
}
