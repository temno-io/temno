using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.IO;
using Temno.Core.Session;

namespace Temno.App.Views.Editor;

/// <summary>
/// WP-399: files a project names outside its own folder stay blocked (offline everywhere, never opened)
/// until the user allows them here. The review never opens or contacts a listed path before the user says
/// yes: it shows the path the project wrote, the file name taken from that path, and what Temno can tell
/// from the text alone. After the yes, a chosen path that leads somewhere else through a link is shown with
/// its target and needs a second yes.
/// </summary>
public sealed partial class EditorPage
{
    // The bar stays closed after "x" until the blocked set changes or another project opens.
    private ProjectSession? _externalMediaDismissedSession;
    private string? _externalMediaDismissedSignature;
    private bool _externalMediaReviewOpen;

    private void RefreshExternalMediaBar()
    {
        if (ExternalMediaBar is null) return;
        var session = AppSession.Current.Session;
        var blocked = session?.ExternalMedia.Blocked(session.Manifest) ?? [];
        if (session is null || blocked.Count == 0)
        {
            ExternalMediaBar.IsOpen = false;
            return;
        }

        var signature = Signature(blocked);
        if (ReferenceEquals(_externalMediaDismissedSession, session) && _externalMediaDismissedSignature == signature)
            return;

        ExternalMediaBar.Title = blocked.Count == 1
            ? "1 file outside this project is blocked"
            : $"{blocked.Count} files outside this project are blocked";
        ExternalMediaBar.Message = "Temno won't play, export, or share them with the agent until you allow them.";
        ExternalMediaBar.IsOpen = true;
    }

    private void ExternalMediaBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        var session = AppSession.Current.Session;
        if (session is null) return;
        _externalMediaDismissedSession = session;
        _externalMediaDismissedSignature = Signature(session.ExternalMedia.Blocked(session.Manifest));
    }

    private async void ExternalMediaReview_Click(object sender, RoutedEventArgs e) => await ReviewExternalMediaAsync();

    private static string Signature(IReadOnlyList<BlockedExternalMedia> blocked)
        => string.Join("\n", blocked.Select(b => b.MediaRef + "|" + b.Path + "|" + b.Concern));

    /// <summary>Lists the blocked files and allows the ones the user ticks. Returns how many were allowed.</summary>
    private async Task<int> ReviewExternalMediaAsync()
    {
        var session = AppSession.Current.Session;
        if (session is null || _externalMediaReviewOpen) return 0;
        var blocked = session.ExternalMedia.Blocked(session.Manifest);
        if (blocked.Count == 0)
        {
            RefreshExternalMediaBar();
            return 0;
        }

        _externalMediaReviewOpen = true;
        try
        {
            var chosen = await ChooseExternalMediaAsync(blocked);
            if (chosen.Count == 0) return 0;

            // Resolving where each chosen path leads opens it: only now, after the yes, and off the UI thread.
            BeginMediaBusy("Checking files…", null);
            List<(string Path, string? Target)> targets;
            try
            {
                targets = await Task.Run(() => chosen.Select(p => (p, ExternalMediaAccess.ResolveTarget(p))).ToList());
            }
            finally
            {
                EndMediaBusy();
            }

            var redirected = targets
                .Where(t => t.Target is not null && !string.Equals(t.Target, ExternalMediaAccess.Normalize(t.Path), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (redirected.Count > 0 && !await ConfirmRedirectedAsync(redirected))
                chosen = chosen.Except(redirected.Select(r => r.Path)).ToList();
            if (chosen.Count == 0) return 0;

            BeginMediaBusy("Allowing files…", null);
            int saved;
            try
            {
                saved = await Task.Run(() => session.AllowExternalMedia(chosen));
            }
            finally
            {
                EndMediaBusy();
            }
            if (!ReferenceEquals(AppSession.Current.Session, session)) return saved;
            RefreshAfterEdit();
            var noun = chosen.Count == 1 ? "file" : "files";
            StatusText.Text = saved == chosen.Count
                ? $"Allowed {chosen.Count} {noun} outside this project"
                : $"Allowed {chosen.Count} {noun} for now · Temno couldn't save this, so it will ask again next time";
            return chosen.Count;
        }
        finally
        {
            _externalMediaReviewOpen = false;
        }
    }

    /// <summary>The review list. Returns the paths the user ticked and allowed; nothing is opened here.</summary>
    private async Task<List<string>> ChooseExternalMediaAsync(IReadOnlyList<BlockedExternalMedia> blocked)
    {
        var body = new StackPanel { Spacing = AppTheme.Spacing.SmMd, MinWidth = 440, MaxWidth = 560 };
        body.Children.Add(new TextBlock
        {
            Text = "This project names files outside its own folder. Temno has not opened them. "
                + "Allow only files you recognise: once allowed, Temno can play them, copy them into a "
                + "self-contained export, and give them to the agent or a generation provider.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = AppTheme.FontSize.Md,
            Foreground = AppTheme.Text.SecondaryBrush,
        });

        var rows = new List<(CheckBox Box, BlockedExternalMedia Item)>();
        var list = new StackPanel { Spacing = AppTheme.Spacing.MdLg };
        foreach (var item in blocked)
        {
            var details = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
            details.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(item.FileName) ? item.Path : item.FileName,
                FontSize = AppTheme.FontSize.Md,
                Foreground = AppTheme.Text.PrimaryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            details.Children.Add(new TextBlock
            {
                Text = item.Path,
                FontSize = AppTheme.FontSize.SmMd,
                Foreground = AppTheme.Text.SecondaryBrush,
                TextWrapping = TextWrapping.Wrap,
            });
            if (ConcernText(item) is { } concern)
            {
                details.Children.Add(new TextBlock
                {
                    Text = concern,
                    FontSize = AppTheme.FontSize.SmMd,
                    Foreground = item.CanAllow ? AppTheme.Status.WarningBrush : AppTheme.Text.TertiaryBrush,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            details.Children.Add(new TextBlock
            {
                Text = $"Called “{item.Name}” in this project · {item.Type}",
                FontSize = AppTheme.FontSize.SmMd,
                Foreground = AppTheme.Text.TertiaryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            var box = new CheckBox
            {
                Content = details,
                IsChecked = false,
                IsEnabled = item.CanAllow,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                // DESIGN §4: a row that can't be allowed stays visible, greyed at Opacity.Strong.
                Opacity = item.CanAllow ? AppTheme.Opacity.Opaque : AppTheme.Opacity.Strong,
            };
            AutomationProperties.SetName(box, $"Allow {item.Path}");
            ToolTipService.SetToolTip(box, item.CanAllow ? "Allow this file for this project" : "This file can't be allowed; relink it instead");
            rows.Add((box, item));
            list.Children.Add(box);
        }

        var allowable = rows.Where(r => r.Item.CanAllow).ToList();
        var selectAll = new CheckBox
        {
            Content = allowable.Count == 1 ? "Select the file that can be allowed" : $"Select all {allowable.Count} files that can be allowed",
            IsEnabled = allowable.Count > 0,
        };
        AutomationProperties.SetName(selectAll, "Select all files that can be allowed");
        ToolTipService.SetToolTip(selectAll, "Tick every file in the list that Temno can allow");
        if (allowable.Count > 1)
            body.Children.Add(selectAll);

        body.Children.Add(new ScrollViewer
        {
            Content = list,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        });

        var dialog = AppDialog.Create(XamlRoot);
        dialog.Title = "Allow files outside this project?";
        dialog.Content = body;
        dialog.PrimaryButtonText = "Allow selected";
        dialog.CloseButtonText = "Not now";
        dialog.DefaultButton = ContentDialogButton.Close;
        dialog.IsPrimaryButtonEnabled = false;

        void SyncPrimary() => dialog.IsPrimaryButtonEnabled = rows.Any(r => r.Box.IsChecked == true);
        var syncingAll = false;
        foreach (var (box, _) in rows)
        {
            box.Checked += (_, _) => SyncPrimary();
            box.Unchecked += (_, _) =>
            {
                if (!syncingAll) selectAll.IsChecked = false;
                SyncPrimary();
            };
        }
        selectAll.Checked += (_, _) =>
        {
            syncingAll = true;
            foreach (var (box, _) in allowable) box.IsChecked = true;
            syncingAll = false;
        };
        selectAll.Unchecked += (_, _) =>
        {
            syncingAll = true;
            foreach (var (box, _) in allowable) box.IsChecked = false;
            syncingAll = false;
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return [];
        return rows.Where(r => r.Box.IsChecked == true && r.Item.CanAllow).Select(r => r.Item.Path).Distinct().ToList();
    }

    /// <summary>A second yes for chosen paths that lead somewhere else through a link or junction.</summary>
    private async Task<bool> ConfirmRedirectedAsync(IReadOnlyList<(string Path, string? Target)> redirected)
    {
        var body = new StackPanel { Spacing = AppTheme.Spacing.SmMd, MinWidth = 440, MaxWidth = 560 };
        body.Children.Add(new TextBlock
        {
            Text = redirected.Count == 1
                ? "This file is reached through a link or junction. Temno will use the file it leads to:"
                : "These files are reached through a link or junction. Temno will use the files they lead to:",
            TextWrapping = TextWrapping.Wrap,
            FontSize = AppTheme.FontSize.Md,
            Foreground = AppTheme.Text.SecondaryBrush,
        });
        foreach (var (path, target) in redirected)
        {
            var row = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
            row.Children.Add(new TextBlock { Text = path, FontSize = AppTheme.FontSize.SmMd, Foreground = AppTheme.Text.SecondaryBrush, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = "leads to " + target, FontSize = AppTheme.FontSize.SmMd, Foreground = AppTheme.Status.WarningBrush, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(row);
        }

        var dialog = AppDialog.Create(XamlRoot);
        dialog.Title = redirected.Count == 1 ? "This file leads somewhere else" : "These files lead somewhere else";
        dialog.Content = new ScrollViewer { Content = body, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dialog.PrimaryButtonText = "Allow anyway";
        dialog.CloseButtonText = redirected.Count == 1 ? "Don't allow it" : "Don't allow these";
        dialog.DefaultButton = ContentDialogButton.Close;
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string? ConcernText(BlockedExternalMedia item) => item.Concern switch
    {
        ExternalMediaConcern.NetworkLocation => "On a network location. Allowing it lets Temno connect there.",
        ExternalMediaConcern.TypeMismatch => $"Its file type does not match this {item.Type} media. If it belongs here, relink it from its media row.",
        ExternalMediaConcern.NotAFullPath => "Not a full path on this PC (a Mac path, for example). Relink it from its media row.",
        ExternalMediaConcern.DevicePath => "Not an ordinary file path. Temno never allows these.",
        ExternalMediaConcern.TargetChanged => item.NowLeadsTo is { } now
            ? $"You allowed it before, but it now leads to {now}. Temno won't share it until you allow it again."
            : "You allowed it before, but it now leads somewhere else. Temno won't share it until you allow it again.",
        _ => null,
    };
}
