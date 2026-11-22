using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Temno.App.Controls;

namespace Temno.App.Services;

public static class SettingsDialog
{
    /// <summary>
    /// WP-191 / WP-121. <paramref name="initialTab"/> is the pane to open on: the chat gate
    /// asks for "agent" so Open Settings from the Connect card lands on AI Chat; every other
    /// entry defaults to Account. The dialog is sized to the window so its content fits at
    /// 1080p, and the scroll viewer is constrained horizontally so long lines wrap.
    /// </summary>
    public static async Task<bool> ShowAsync(XamlRoot? xamlRoot, string? initialTab = null)
    {
        if (xamlRoot is null)
            return false;

        var settings = new SettingsControl { InitialTab = initialTab ?? "account" };
        var dialog = AppDialog.Create(xamlRoot);
        dialog.Title = "Settings";
        var windowHeight = xamlRoot.Size.Height > 0 ? xamlRoot.Size.Height : 900;
        settings.Width = Math.Clamp(xamlRoot.Size.Width - 112, 480, 720);
        settings.Height = Math.Clamp(windowHeight - Theme.AppTheme.ComponentSize.SettingsVerticalInset,
            Theme.AppTheme.ComponentSize.DialogMinContentHeight, Theme.AppTheme.ComponentSize.SettingsContentMaxHeight);
        dialog.Content = settings;
        dialog.PrimaryButtonText = "Save";
        dialog.CloseButtonText = "Close";
        dialog.DefaultButton = ContentDialogButton.Primary;
        // WP-309: an unsaved skill edit keeps the dialog open, whichever way it is closed.
        dialog.Closing += (_, args) =>
        {
            if (!settings.HasUnsavedWork)
                return;
            args.Cancel = true;
            settings.ShowUnsavedWarning();
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return false;

        settings.Save();
        return true;
    }
}
