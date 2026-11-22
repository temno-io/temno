using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Temno.App.Services;

public enum UnsavedChangesAction
{
    Save,
    Discard,
    Cancel,
}

public static class UnsavedChangesPrompt
{
    public static async Task<UnsavedChangesAction> AskAsync(XamlRoot? xamlRoot, string projectName, bool isDirty)
    {
        if (!isDirty)
            return UnsavedChangesAction.Discard;
        if (xamlRoot is null)
            return UnsavedChangesAction.Cancel;

        var dialog = AppDialog.Create(xamlRoot);
        dialog.Title = "Unsaved changes";
        dialog.Content = $"“{projectName}” has unsaved changes. Save before continuing?";
        dialog.PrimaryButtonText = "Save";
        dialog.SecondaryButtonText = "Don't Save";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => UnsavedChangesAction.Save,
            ContentDialogResult.Secondary => UnsavedChangesAction.Discard,
            _ => UnsavedChangesAction.Cancel,
        };
    }
}
