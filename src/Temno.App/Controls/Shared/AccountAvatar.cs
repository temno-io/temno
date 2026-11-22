using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using Temno.App.Theme;
using Temno.Core.Account;

namespace Temno.App.Controls.Shared;

public sealed class AccountAvatar : Grid
{
    private readonly PersonPicture _picture = new()
    {
        Background = AppTheme.Background.RaisedBrush,
        Foreground = AppTheme.Text.SecondaryBrush,
        IsTabStop = false,
    };
    private readonly FluentIcon _signedOut = new()
    {
        Icon = Icons.Person,
        Width = AppTheme.IconSize.Sm,
        Height = AppTheme.IconSize.Sm,
        Fill = AppTheme.Text.MutedBrush,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private BitmapImage? _image;
    private string? _avatarUrl;
    private string? _userId;

    public AccountAvatar()
    {
        Width = Height = AppTheme.IconSize.MdLg;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_picture, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_signedOut, AccessibilityView.Raw);
        Children.Add(_picture);
        Children.Add(_signedOut);
        // Local bindings override PersonPicture's 96-DIP style before its first layout.
        _picture.SetBinding(WidthProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Width)) });
        _picture.SetBinding(HeightProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Height)) });
        Update(null, null, null, null);
    }

    public void Update(string? userId, string? displayName, string? email, string? avatarUrl)
    {
        var signedIn = !string.IsNullOrWhiteSpace(userId);
        _picture.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        _signedOut.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        _picture.DisplayName = signedIn ? displayName ?? email ?? "" : "";
        var safeUrl = signedIn ? AccountProfile.GoogleAvatarUrl(avatarUrl) : null;
        if (_userId == userId && _avatarUrl == safeUrl)
            return;

        _userId = userId;
        _avatarUrl = safeUrl;
        _image = null;
        _picture.ProfilePicture = null;
        if (safeUrl is null)
            return;

        var image = new BitmapImage { DecodePixelWidth = (int)(AppTheme.IconSize.Hero * 2) };
        _image = image;
        image.ImageFailed += (_, _) =>
        {
            if (ReferenceEquals(_image, image))
                _picture.ProfilePicture = null;
        };
        image.UriSource = new Uri(safeUrl);
        // WinUI starts decoding only once the source is attached to a visual.
        _picture.ProfilePicture = image;
    }
}
