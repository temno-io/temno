using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Temno.App.Controls.Shared;
using Temno.App.Theme;

namespace Temno.App.Services;

/// <summary>
/// Themed in-app dialog host. Call sites should use this instead of a bare
/// <see cref="ContentDialog"/> so chrome matches Temno surfaces rather than
/// stock WinUI. Longer-term these migrate to an in-window overlay (no system modal).
/// </summary>
public static class AppDialog
{
    private static readonly HashSet<ContentDialog> OpenDialogs = [];

    public static ContentDialog Create(XamlRoot? xamlRoot)
    {
        var dialog = new ContentDialog
        {
            RequestedTheme = ElementTheme.Dark,
            Background = AppTheme.Background.SurfaceBrush,
            Foreground = AppTheme.Text.PrimaryBrush,
            BorderBrush = AppTheme.Border.PrimaryBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(AppTheme.Radius.Md),
        };
        // The template caps its inner border independently of the dialog's Width.
        // Let content measure within this shared cap instead of overflowing a narrower border.
        dialog.Resources["ContentDialogMaxWidth"] = 800.0;
        // Dialog-open motion (WP-204): ContentDialog shows instantly, so fade the chrome
        // in on AppTheme.Anim.Transition. Pre-hide on Loading (before first render, no flash);
        // Opened plays the fade. Reduce-motion resolves to an instant Opacity = 1 restore.
        // Content here is always dialog chrome — never the preview canvas (DESIGN §5).
        dialog.Loading += (_, _) =>
        {
            if (!Motion.ReduceMotion && dialog.Content is UIElement content)
                content.Opacity = 0;
        };
        dialog.Opened += (_, _) =>
        {
            if (dialog.Content is UIElement content)
                Motion.FadeIn(content);
        };
        dialog.Opened += (_, _) => OpenDialogs.Add(dialog);
        dialog.Closed += (_, _) => OpenDialogs.Remove(dialog);
        if (xamlRoot is not null)
            dialog.XamlRoot = xamlRoot;
        if (Application.Current.Resources.TryGetValue("TemnoDialogPrimaryButton", out var primary)
            && primary is Style primaryStyle)
        {
            dialog.PrimaryButtonStyle = primaryStyle;
        }
        if (Application.Current.Resources.TryGetValue("TemnoDialogCloseButton", out var close)
            && close is Style closeStyle)
        {
            dialog.SecondaryButtonStyle = closeStyle;
            dialog.CloseButtonStyle = closeStyle;
        }

        return dialog;
    }

    public static async Task DismissOpenAsync(XamlRoot? xamlRoot)
    {
        if (xamlRoot is null)
            return;

        var dialogs = OpenDialogs.Where(d => d.XamlRoot == xamlRoot).ToHashSet();
        // Older call sites construct ContentDialog directly, outside this factory.
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot))
            CollectDialogs(popup.Child, dialogs);

        foreach (var dialog in dialogs)
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args) => closed.TrySetResult();
            dialog.Closed += OnClosed;
            try
            {
                dialog.Hide();
                await closed.Task;
            }
            finally
            {
                dialog.Closed -= OnClosed;
            }
        }
    }

    private static void CollectDialogs(DependencyObject? node, HashSet<ContentDialog> dialogs)
    {
        if (node is ContentDialog dialog)
        {
            dialogs.Add(dialog);
            return;
        }
        if (node is null)
            return;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            CollectDialogs(VisualTreeHelper.GetChild(node, i), dialogs);
    }
}
