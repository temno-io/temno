using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Temno.App.Controls;

namespace Temno.App.Services;

public static class AccountDialog
{
    public static async Task ShowAsync(XamlRoot? xamlRoot)
    {
        if (xamlRoot is null)
            return;

        var control = new AccountControl { ShowHeading = false };
        var dialog = AppDialog.Create(xamlRoot);
        dialog.Title = "Account";
        var windowHeight = xamlRoot.Size.Height > 0 ? xamlRoot.Size.Height : 900;
        control.Width = Math.Clamp(xamlRoot.Size.Width - 160, 360, 480);
        control.MinHeight = Theme.AppTheme.ComponentSize.DialogMinContentHeight;
        control.MaxHeight = Math.Clamp(windowHeight - 160, 280, 560);
        dialog.Content = control;
        dialog.CloseButtonText = "Close";
        dialog.DefaultButton = ContentDialogButton.Close;
        dialog.Closed += (_, _) =>
        {
            if (AccountSessionService.Current.SignInInProgress)
                AccountSessionService.Current.CancelSignIn();
        };
        await dialog.ShowAsync();
    }
}
