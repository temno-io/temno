using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Agent.Skills;
using Temno.Core.Ui;
using Windows.Storage.Pickers;

namespace Temno.App.Controls;

/// <summary>
/// WP-309 Settings > Skills: every skill with its source and state, one skill's full text, New skill, Import,
/// Open folder, turn on or off, and remove. A skill reaches the agent only after the user has read its text
/// here (R-1): Turn on approves exactly the text on screen, and an import is shown whole before it is added.
/// Mac analog: <c>Settings/Skill/SkillsPane.swift</c> and <c>SkillDetailSheet.swift</c>.
/// </summary>
public sealed partial class SkillsControl : UserControl
{
    private const string SkillTemplate = """
        ---
        name: {0}
        description: Say in one line what this skill does and when the assistant should use it.
        ---

        ## Steps
        1. First step.
        2. Second step.
        """;

    private enum Mode
    {
        Skill,
        New,
        Import,
    }

    private Mode _mode;
    private SkillEntry? _entry;
    private SkillImport? _import;
    private string _original = "";
    private string? _shownSha;
    private bool _loadingText;

    public SkillsControl()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
        WindowPopout.AttachMenu(ImportSkillFlyout, ImportSkillButton, PopoutSide.Bottom, PopoutAlignment.Left);
        WindowPopout.AttachFlyout(RemoveFlyout, RemoveButton, scroll: null, PopoutSide.Bottom, PopoutAlignment.Right);
        RemoveFlyout.Opening += (_, _) => RemoveQuestion.Text = _entry is { } entry
            ? $"Remove {entry.Name}? Its folder goes to the Recycle Bin, and the assistant stops seeing it."
            : "";
    }

    private static SkillStore Store => SkillLibrary.Current ??= SkillStore.ForUser();

    /// <summary>An edit or an import on screen that leaving would lose. The Settings dialog will not close over one.</summary>
    public bool HasUnsavedWork => DetailPane.Visibility == Visibility.Visible && IsDirty;

    /// <summary>Says why the dialog stayed open.</summary>
    public void ShowUnsavedWarning()
    {
        ShowError("Not saved", "Save this skill or discard your changes before closing Settings.");
    }

    public void Refresh()
    {
        if (DetailPane.Visibility == Visibility.Visible)
            return;
        SkillSnapshot snapshot;
        try
        {
            snapshot = Store.Scan();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowListError(ex.Message);
            return;
        }
        Rows.Children.Clear();
        foreach (var entry in snapshot.All)
            Rows.Children.Add(Row(entry));
        EmptyState.Visibility = snapshot.All.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // --- list ---

    private Button Row(SkillEntry entry)
    {
        var (state, brush) = StateText(entry);
        var name = new TextBlock
        {
            Text = entry.Document?.Name ?? entry.Name,
            FontSize = AppTheme.FontSize.Md,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = AppTheme.Text.PrimaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var status = new TextBlock
        {
            Text = state,
            FontSize = AppTheme.FontSize.Sm,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(status, 1);
        var head = new Grid { ColumnSpacing = AppTheme.Spacing.SmMd };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(name);
        head.Children.Add(status);

        var body = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
        body.Children.Add(head);
        body.Children.Add(new TextBlock
        {
            Text = entry.Error ?? entry.Description,
            FontSize = AppTheme.FontSize.SmMd,
            Foreground = AppTheme.Text.TertiaryBrush,
            TextWrapping = TextWrapping.WrapWholeWords,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        body.Children.Add(new TextBlock
        {
            Text = SourceText(entry),
            FontSize = AppTheme.FontSize.Sm,
            Foreground = AppTheme.Text.TertiaryBrush,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var row = new Button
        {
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = AppTheme.Padding.Control,
            Background = AppTheme.Background.SurfaceBrush,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(AppTheme.Radius.Sm),
        };
        AutomationProperties.SetName(row, $"{entry.Name}, {state}");
        ToolTipService.SetToolTip(row, "Open " + entry.Name);
        row.Click += (_, _) => OpenSkill(entry);
        return row;
    }

    private static (string Text, Brush Brush) StateText(SkillEntry entry) => entry.Status switch
    {
        SkillStatus.On => ("On", AppTheme.Status.SuccessBrush),
        SkillStatus.Off => ("Off", AppTheme.Text.TertiaryBrush),
        SkillStatus.New => ("Review to turn on", AppTheme.Status.WarningBrush),
        SkillStatus.Changed => ("Changed outside Temno", AppTheme.Status.WarningBrush),
        SkillStatus.Overridden => ("Replaced by yours", AppTheme.Text.TertiaryBrush),
        _ => ("Can't be used", AppTheme.Status.ErrorBrush),
    };

    private static string SourceText(SkillEntry entry)
    {
        if (entry.Source == SkillSource.BuiltIn)
            return "Built-in";
        var origin = entry.Origin switch
        {
            { Kind: SkillOrigin.Imported, From: { } from, At: var at } => $"Imported from {Path.GetFileName(Path.TrimEndingDirectorySeparator(from))} on {at.ToLocalTime():d}",
            { Kind: SkillOrigin.Created, At: var at } => $"Yours, written in Temno on {at.ToLocalTime():d}",
            _ => "Added outside Temno",
        };
        return entry.OverridesBuiltIn ? origin + " · replaces the built-in skill" : origin;
    }

    private void ShowListError(string message)
    {
        ListError.Message = message;
        ListError.IsOpen = true;
    }

    // --- detail ---

    private void OpenSkill(SkillEntry entry)
    {
        _mode = Mode.Skill;
        _entry = entry;
        _import = null;
        string text;
        try
        {
            (text, _shownSha) = Store.ReadText(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            text = "";
            _shownSha = null;
            ShowListError(ex.Message);
        }
        ShowDetail(entry.Name, text, editable: entry.Source == SkillSource.User);
        DetailSource.Text = SourceText(entry);
        ToolTipService.SetToolTip(DetailSource, entry.Origin?.From ?? entry.FilePath);
        ShowNotice(entry);
        if (entry.Error is { } error)
            ShowError("Can't be used", error);
        UpdateButtons();
    }

    private void ShowNotice(SkillEntry entry)
    {
        var notes = new List<string>();
        var severity = InfoBarSeverity.Informational;
        switch (entry.Status)
        {
            case SkillStatus.New:
                notes.Add("This skill was added outside Temno. Read it, then turn it on if you want the assistant to follow it.");
                severity = InfoBarSeverity.Warning;
                break;
            case SkillStatus.Changed:
                notes.Add("This skill changed outside Temno since you turned it on, so it is off. Read it again, then turn it on.");
                severity = InfoBarSeverity.Warning;
                break;
            case SkillStatus.Overridden:
                notes.Add("Your skill with the same name is used instead of this one.");
                break;
        }
        if (entry.OverridesBuiltIn)
            notes.Add("It replaces the built-in skill with the same name.");
        AddFormatNotes(notes, entry.Document);
        SetNotice(notes, severity);
    }

    private static void AddFormatNotes(List<string> notes, SkillDocument? document)
    {
        if (document is null)
            return;
        if (document.IgnoredFields.Contains("allowed-tools"))
            notes.Add("Its allowed-tools list is ignored: a skill grants no tools.");
        var others = document.IgnoredFields.Where(f => f != "allowed-tools").ToArray();
        if (others.Length > 0)
            notes.Add("Temno does not use these fields: " + string.Join(", ", others) + ".");
    }

    private void SetNotice(List<string> notes, InfoBarSeverity severity)
    {
        DetailNotice.Severity = severity;
        DetailNotice.Message = string.Join(" ", notes);
        DetailNotice.IsOpen = notes.Count > 0;
    }

    private void ShowDetail(string title, string text, bool editable)
    {
        DetailTitle.Text = title;
        _loadingText = true;
        Editor.Text = text;
        _loadingText = false;
        _original = Normalize(text);
        Editor.IsReadOnly = !editable;
        DetailError.IsOpen = false;
        ListError.IsOpen = false;
        ListPane.Visibility = Visibility.Collapsed;
        DetailPane.Visibility = Visibility.Visible;
    }

    private void ShowError(string title, string message)
    {
        DetailError.Title = title;
        DetailError.Message = message;
        DetailError.IsOpen = true;
    }

    private bool IsDirty => !Editor.IsReadOnly && Normalize(Editor.Text) != _original;

    // WinUI's TextBox reports line breaks as CR; skills are compared and written with LF.
    private static string Normalize(string text) => text.ReplaceLineEndings("\n");

    private void UpdateButtons()
    {
        var dirty = IsDirty;
        var entry = _entry;
        SaveButton.Content = _mode == Mode.Import ? "Add and turn on" : "Save";
        AutomationProperties.SetName(SaveButton, _mode == Mode.Import ? "Add skill and turn it on" : "Save skill");
        SaveButton.Visibility = _mode == Mode.Import || !Editor.IsReadOnly ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = _mode != Mode.Skill || dirty;
        DiscardButton.Content = _mode == Mode.Skill ? "Discard changes" : "Cancel";
        AutomationProperties.SetName(DiscardButton, _mode switch { Mode.Import => "Cancel import", Mode.New => "Cancel new skill", _ => "Discard changes" });
        DiscardButton.Visibility = _mode != Mode.Skill || dirty ? Visibility.Visible : Visibility.Collapsed;

        var canToggle = _mode == Mode.Skill && entry is not null && !dirty
            && entry.Status is not (SkillStatus.Invalid or SkillStatus.Overridden);
        TurnOnButton.Visibility = canToggle && entry!.Status != SkillStatus.On ? Visibility.Visible : Visibility.Collapsed;
        TurnOffButton.Visibility = canToggle && entry!.Status == SkillStatus.On ? Visibility.Visible : Visibility.Collapsed;
        RevealButton.Visibility = _mode == Mode.Skill && entry is not null ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.Visibility = _mode == Mode.Skill && entry?.Source == SkillSource.User ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = !dirty;
        ToolTipService.SetToolTip(BackButton, dirty ? "Save or discard your changes first" : "Back to the list of skills");

        var (state, brush) = _mode switch
        {
            Mode.New => ("New", AppTheme.Text.TertiaryBrush),
            Mode.Import => ("Not added yet", AppTheme.Text.TertiaryBrush),
            _ when dirty => ("Unsaved changes", AppTheme.Status.WarningBrush),
            _ => entry is null ? ("", AppTheme.Text.TertiaryBrush) : StateText(entry),
        };
        DetailState.Text = state;
        DetailState.Foreground = brush;
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingText)
            UpdateButtons();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => CloseDetail();

    private void CloseDetail()
    {
        _entry = null;
        _import = null;
        _shownSha = null;
        DetailPane.Visibility = Visibility.Collapsed;
        ListPane.Visibility = Visibility.Visible;
        Refresh();
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (_mode == Mode.Skill && _entry is { } entry)
        {
            OpenSkill(Store.Scan().Find(entry.Name, entry.Source) ?? entry);
            return;
        }
        CloseDetail();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = _mode switch
            {
                Mode.New => Store.Create(Normalize(Editor.Text)),
                Mode.Import => Store.CommitImport(_import!),
                _ => Store.Save(_entry!.Name, Normalize(Editor.Text)),
            };
            OpenSkill(saved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(_mode == Mode.Import ? "Not added" : "Not saved", ex.Message);
        }
    }

    private void TurnOn_Click(object sender, RoutedEventArgs e)
    {
        if (_entry is not { } entry)
            return;
        try
        {
            // An unapproved skill is approved as shown: a file changed since stays off (R-1).
            if (entry.Source == SkillSource.User && !entry.Approved)
            {
                if (_shownSha is not { } sha)
                    throw new InvalidOperationException("This skill could not be read, so it cannot be turned on.");
                Store.Approve(entry.Name, sha);
            }
            else
            {
                Store.SetOn(entry.Name, true);
            }
            Reopen(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError("Not turned on", ex.Message);
        }
    }

    private void TurnOff_Click(object sender, RoutedEventArgs e)
    {
        if (_entry is not { } entry)
            return;
        try
        {
            Store.SetOn(entry.Name, false);
            Reopen(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError("Not turned off", ex.Message);
        }
    }

    private void Reopen(SkillEntry entry)
    {
        var fresh = Store.Scan().Find(entry.Name, entry.Source);
        if (fresh is null)
            CloseDetail();
        else
            OpenSkill(fresh);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_entry is { } entry)
            AppSession.RevealInExplorer(entry.FilePath);
    }

    private void ConfirmRemove_Click(object sender, RoutedEventArgs e)
    {
        RemoveFlyout.Hide();
        if (_entry is not { } entry)
            return;
        try
        {
            Store.Remove(entry.Name, folder => Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                folder,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin));
            CloseDetail();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
        {
            ShowError("Not removed", ex.Message);
        }
    }

    // --- new, import, folder ---

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var taken = Store.Scan().All.Where(s => s.Source == SkillSource.User).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        var name = "my-skill";
        for (var n = 2; taken.Contains(name); n++)
            name = $"my-skill-{n}";
        _mode = Mode.New;
        _entry = null;
        _import = null;
        ShowDetail("New skill", string.Format(System.Globalization.CultureInfo.InvariantCulture, SkillTemplate.ReplaceLineEndings("\n"), name), editable: true);
        DetailSource.Text = "Change the name and description, then write the steps. Save turns it on.";
        ToolTipService.SetToolTip(DetailSource, null);
        SetNotice(["The name must match the rules other agents use: lowercase letters, digits and single hyphens."], InfoBarSeverity.Informational);
        UpdateButtons();
        Editor.Focus(FocusState.Programmatic);
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        if (!InitializePicker(picker))
            return;
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            ShowImport(folder.Path);
    }

    private async void ImportZip_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".zip");
        if (!InitializePicker(picker))
            return;
        var file = await picker.PickSingleFileAsync();
        if (file is not null)
            ShowImport(file.Path);
    }

    private bool InitializePicker(object picker)
    {
        if (App.MainAppWindow is not { } window)
        {
            ShowListError("The file picker is unavailable. Copy the skill folder into .temno\\skills with Open folder instead.");
            return false;
        }
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        return true;
    }

    private void ShowImport(string path)
    {
        SkillImport import;
        try
        {
            import = Store.PrepareImport(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowListError(ex.Message);
            return;
        }
        _mode = Mode.Import;
        _entry = null;
        _import = import;
        ShowDetail("Import " + import.Document.Name, import.Text, editable: false);
        DetailSource.Text = "From " + Path.GetFileName(Path.TrimEndingDirectorySeparator(import.SourcePath))
            + ". Read it before you add it: the assistant follows what it says.";
        ToolTipService.SetToolTip(DetailSource, import.SourcePath);
        var notes = new List<string>();
        if (import.ReplacesUserSkill)
            notes.Add($"It replaces your skill named {import.Document.Name}.");
        else if (import.OverridesBuiltIn)
            notes.Add("It replaces the built-in skill with the same name.");
        if (import.IgnoredFiles > 0)
            notes.Add(import.IgnoredFiles == 1
                ? "Only SKILL.md is added; 1 other file is left behind and nothing in it runs."
                : $"Only SKILL.md is added; {import.IgnoredFiles:N0} other files are left behind and nothing in them runs.");
        AddFormatNotes(notes, import.Document);
        SetNotice(notes, import.ReplacesUserSkill ? InfoBarSeverity.Warning : InfoBarSeverity.Informational);
        UpdateButtons();
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Store.UserRoot);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{Store.UserRoot}\"", UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            ShowListError(ex.Message);
        }
    }
}
