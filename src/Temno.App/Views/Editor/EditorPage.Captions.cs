using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Temno.App.Controls;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Account;
using Temno.Core.Captions;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Ui;
using Temno.Media.Text;
using Temno.Media.Transcription;
using TextAlign = Temno.Core.Models.TextAlignment;

namespace Temno.App.Views.Editor;

public sealed partial class EditorPage
{
    private bool _generatingCaptions;
    private bool _syncingCaptionsSource;
    private bool _syncingCaptionForm;
    private bool _captionFormBuilt;
    private readonly StackPanel _captionDefaultsHost = new() { Spacing = AppTheme.Spacing.MdLg };
    private CaptionPresetGallery? _captionPresetGallery;
    private CancellationTokenSource? _captionCts;
    private double _captionCenterX = CaptionFormLogic.DefaultCenterX;
    private double _captionCenterY = CaptionFormLogic.DefaultCenterY;

    private ComboBox CaptionsModeBox = null!;
    private ComboBox CaptionsSourceBox = null!;
    private ComboBox CaptionsLanguageBox = null!;
    private TextBlock CaptionsSourceLabel = null!;
    private TextBlock CaptionsHonestyLabel = null!;
    private SectionHeader CaptionsSourceSection = null!;
    private ToggleSwitch CaptionsPreviewToggle = null!;
    private bool _captionDefaultsOpen;
    private ComboBox CaptionsCaseBox = null!;
    private TextStyleRows CaptionsStyleRows = null!;
    private UnitNumberField CaptionsGapField = null!;
    private UnitNumberField CaptionsMaxWordsField = null!;
    private UnitNumberField CaptionsMaxCharsField = null!;
    private UnitNumberField CaptionsPosXField = null!;
    private UnitNumberField CaptionsPosYField = null!;
    private UnitNumberField CaptionsShadowOpacityField = null!;
    private UnitNumberField CaptionsShadowOffsetXField = null!;
    private UnitNumberField CaptionsShadowOffsetYField = null!;
    private UnitNumberField CaptionsShadowBlurField = null!;
    private UnitNumberField CaptionsBackgroundOpacityField = null!;
    private ColorWell CaptionsOutlineColorWell = null!;
    private ColorWell CaptionsShadowColorWell = null!;
    private ColorWell CaptionsBackgroundColorWell = null!;
    private ColorWell CaptionsHighlightWell = null!;
    private SectionHeader CaptionsOutlineSection = null!;
    private SectionHeader CaptionsShadowSection = null!;
    private SectionHeader CaptionsBackgroundSection = null!;
    private StackPanel CaptionsAnimationHost = null!;
    private EditorRow CaptionsHighlightRow = null!;
    private EditorRow CaptionsSourceRow = null!;
    private EditorRow CaptionsCaseRow = null!;
    private EditorRow CaptionsLanguageRow = null!;
    private EditorRow CaptionsPositionRow = null!;

    private static readonly TextStyle.RGBA DefaultFill = new() { R = 1, G = 1, B = 1, A = 1 };
    private static readonly TextStyle.RGBA DefaultOutlineColor = new() { R = 0, G = 0, B = 0, A = 1 };
    private static readonly TextStyle.RGBA DefaultShadowColor = new() { R = 0, G = 0, B = 0, A = 0.6 };
    private static readonly TextStyle.RGBA DefaultBackgroundColor = new() { R = 0, G = 0, B = 0, A = 0.6 };

    private static UnitFieldSpec GapSpec => new()
    {
        Unit = "ms", Minimum = 0, Maximum = CaptionFormLogic.GapMsMaximum, Step = 10,
        UnitsPerPixel = 5, Default = CaptionFormLogic.GapMsDefault,
    };

    private static UnitFieldSpec MaxWordsSpec => new()
    {
        Minimum = 0, Maximum = CaptionFormLogic.MaxWordsLimit, Step = 1, Default = CaptionFormLogic.DefaultMaxWords,
    };

    private static UnitFieldSpec MaxCharsSpec => new()
    {
        Minimum = 0, Maximum = CaptionFormLogic.MaxCharactersLimit, Step = 1, Default = 0,
    };

    private static UnitFieldSpec OpacitySpec(double fallback) => new()
    {
        Unit = "%", Minimum = 0, Maximum = 100, Step = 1, UnitsPerPixel = 1, Default = fallback,
    };

    private static UnitFieldSpec OffsetXSpec => new()
    {
        Unit = " px", Minimum = -200, Maximum = 200, Step = 0.5, Decimals = 1, UnitsPerPixel = 1, Default = 0,
    };

    private static UnitFieldSpec OffsetYSpec => new()
    {
        Unit = " px", Minimum = -200, Maximum = 200, Step = 0.5, Decimals = 1, UnitsPerPixel = 1, Default = -2,
    };

    private static UnitFieldSpec BlurSpec => new()
    {
        Unit = " px", Minimum = 0, Maximum = 80, Step = 0.5, Decimals = 1, UnitsPerPixel = 1, Default = 6,
    };

    private void EnsureCaptionForm()
    {
        if (_captionFormBuilt || CaptionsFormHost is null) return;

        // WP-196: the auto scrollbar floats over the viewport's right edge. Inset the whole form
        // past it while it is visible so section-header toggles and row ends never slide under
        // the thumb; back to zero when it hides so the form stays symmetric.
        CaptionsFormScroll.LayoutUpdated += (_, _) => SyncCaptionFormInset();
        SyncCaptionFormInset();

        CaptionsFormHost.Children.Add(new TextBlock
        {
            Text = "Generate captions", FontSize = AppTheme.FontSize.MdLg,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        CaptionsHonestyLabel = MutedText("", AppTheme.Text.SecondaryBrush, AppTheme.FontSize.EditorLabel);
        CaptionsFormHost.Children.Add(CaptionsHonestyLabel);

        CaptionsFormHost.Children.Add(BuildSourceSection());
        CaptionsFormHost.Children.Add(BuildSettingsSection());
        _captionDefaultsHost.Children.Add(BuildStyleSection());
        _captionDefaultsHost.Children.Add(BuildOutlineSection());
        _captionDefaultsHost.Children.Add(BuildShadowSection());
        _captionDefaultsHost.Children.Add(BuildBackgroundSection());
        _captionDefaultsHost.Children.Add(BuildAnimationSection());

        EnsureCaptionPresetGallery();
        RefreshCaptionsSourceLabel();
        UpdatePositionFieldSpecs();
        SyncHighlightRow();
        _captionFormBuilt = true;
        ConfigureCaptionsAvailability();
        EnsureCaptionAgentMenu();
    }

    private async void CaptionDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (_captionDefaultsOpen) return;
        EnsureCaptionForm();
        _captionDefaultsOpen = true;
        var dialog = AppDialog.Create(XamlRoot);
        dialog.Title = "Style for new captions";
        var host = new StackPanel { Spacing = AppTheme.Spacing.MdLg };
        host.Children.Add(MutedText("These defaults apply to captions you generate next. Edit selected captions in the Inspector.",
            AppTheme.Text.SecondaryBrush, AppTheme.FontSize.EditorLabel));
        host.Children.Add(_captionDefaultsHost);
        dialog.Content = new ScrollViewer
        {
            Content = host, Width = Math.Clamp(XamlRoot.Size.Width - 112, 320, 480),
            MaxHeight = Math.Max(180, XamlRoot.Size.Height - 220),
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        dialog.CloseButtonText = "Done";
        try { await dialog.ShowAsync(); }
        finally { host.Children.Remove(_captionDefaultsHost); _captionDefaultsOpen = false; }
    }

    private void EnsureCaptionAgentMenu()
    {
        if (CaptionAgentModeBtn is null || CaptionAgentModeBtn.Flyout is not null) return;

        CaptionAgentModeBtn.Foreground = AppTheme.Ai.GradientBrush;
        CaptionAgentModeBtn.BorderThickness = new Thickness(AppTheme.BorderWidth.Thin);

        var flyout = new MenuFlyout();
        foreach (var (id, label) in CaptionAgentRecipes.Primary)
        {
            var item = new MenuFlyoutItem { Text = label, Tag = id };
            AutomationProperties.SetName(item, label);
            item.Click += CaptionAgentRecipe_Click;
            flyout.Items.Add(item);
        }

        var translate = new MenuFlyoutSubItem { Text = CaptionAgentRecipes.TranslateLabel };
        AutomationProperties.SetName(translate, CaptionAgentRecipes.TranslateLabel);
        foreach (var language in CaptionAgentRecipes.TranslateLanguages)
        {
            var item = new MenuFlyoutItem
            {
                Text = language,
                Tag = CaptionAgentRecipes.TranslateItemId(language),
            };
            AutomationProperties.SetName(item, language);
            item.Click += CaptionAgentRecipe_Click;
            translate.Items.Add(item);
        }
        flyout.Items.Add(translate);
        WindowPopout.AttachMenu(flyout, CaptionAgentModeBtn, PopoutSide.Bottom, PopoutAlignment.Right);
        CaptionAgentModeBtn.Flyout = flyout;
    }

    private void CaptionAgentRecipe_Click(object sender, RoutedEventArgs e)
    {
        var id = (sender as MenuFlyoutItem)?.Tag as string;
        if (string.IsNullOrEmpty(id)) return;

        ShowPanel(ShellPanel.Agent, forceVisible: true);
        Agent.ShowChatTab();
        if (!Agent.IsChatConfigured)
        {
            Agent.FocusUnconfiguredChatCta();
            StatusText.Text = "Chat needs an API key";
            return;
        }

        Agent.PrefillChat(CaptionAgentRecipes.PromptFor(id));
        StatusText.Text = "Caption recipe staged in chat — review before sending";
    }

    private SectionHeader BuildSourceSection()
    {
        var rows = new StackPanel { Spacing = AppTheme.Spacing.Xxs };

        CaptionsModeBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        CaptionsModeBox.Items.Add(new ComboBoxItem { Content = "Local · this PC", Tag = "local" });
        CaptionsModeBox.Items.Add(new ComboBoxItem { Content = "Cloud · hosted credits", Tag = "cloud" });
        CaptionsModeBox.SelectedIndex = 0;
        AutomationProperties.SetName(CaptionsModeBox, "Mode");
        CaptionsModeBox.SelectionChanged += CaptionsModeBox_SelectionChanged;
        rows.Children.Add(new EditorRow
        {
            Label = "Mode",
            LabelHelp = "Local uses the bundled English Whisper model. Cloud spends hosted credits and currently admits English and Spanish.",
            RowContent = CaptionsModeBox,
        });

        CaptionsSourceBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(CaptionsSourceBox, "Source");
        CaptionsSourceBox.SelectionChanged += CaptionsSourceBox_SelectionChanged;
        CaptionsSourceRow = new EditorRow
        {
            Label = "Audio source",
            LabelHelp = "Auto picks the track with the most speech. Reset returns to Auto.",
            RowContent = CaptionsSourceBox,
        };
        CaptionsSourceRow.ResetRequested += (_, _) =>
        {
            if (CaptionsSourceBox.Items.Count > 0)
                CaptionsSourceBox.SelectedIndex = 0;
        };
        CaptionsSourceBox.SelectionChanged += (_, _) => SyncSourceReset();
        rows.Children.Add(CaptionsSourceRow);

        CaptionsSourceLabel = MutedText("Source: Auto", AppTheme.Text.SecondaryBrush, AppTheme.FontSize.Sm);
        rows.Children.Add(CaptionsSourceLabel);

        CaptionsSourceSection = new SectionHeader
        {
            SectionId = "captions.source", Title = "Source", SectionContent = rows,
        };
        return CaptionsSourceSection;
    }

    private SectionHeader BuildSettingsSection()
    {
        var rows = new StackPanel { Spacing = AppTheme.Spacing.Xxs };

        CaptionsMaxWordsField = new UnitNumberField { Spec = MaxWordsSpec, Value = CaptionFormLogic.DefaultMaxWords };
        rows.Children.Add(BindValueRow(
            "Max words",
            "0 = unlimited. Caps how many words land in one caption.",
            CaptionsMaxWordsField));

        CaptionsMaxCharsField = new UnitNumberField { Spec = MaxCharsSpec, Value = 0 };
        rows.Children.Add(BindValueRow(
            "Max characters",
            "0 = unlimited. Hard character cap per caption line.",
            CaptionsMaxCharsField));

        CaptionsGapField = new UnitNumberField { Spec = GapSpec, Value = CaptionFormLogic.GapMsDefault };
        rows.Children.Add(BindValueRow(
            "Close gaps",
            "Hold a caption across gaps shorter than this. 0 keeps every transcribed gap.",
            CaptionsGapField));

        CaptionsLanguageBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        CaptionsLanguageBox.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        CaptionsLanguageBox.Items.Add(new ComboBoxItem { Content = "Spanish", Tag = "es" });
        CaptionsLanguageBox.SelectedIndex = 0;
        AutomationProperties.SetName(CaptionsLanguageBox, "Language");
        CaptionsLanguageRow = new EditorRow
        {
            Label = "Language",
            LabelHelp = "The bundled model is English. Cloud currently admits English and Spanish.",
            RowContent = CaptionsLanguageBox,
        };
        rows.Children.Add(CaptionsLanguageRow);

        CaptionsPreviewToggle = new ToggleSwitch { IsOn = false, MinWidth = 0, OnContent = "On", OffContent = "Off" };
        AutomationProperties.SetName(CaptionsPreviewToggle, "Preview captions");
        CaptionsPreviewToggle.Toggled += (_, _) => CaptionsPreview_Toggled();
        rows.Children.Add(new EditorRow
        {
            Label = "Preview captions", RowContent = CaptionsPreviewToggle,
            LabelHelp = "Show a sample caption on the preview. Generate creates real caption clips.",
        });

        return new SectionHeader
        {
            SectionId = "captions.settings",
            Title = "Settings",
            SectionContent = rows,
        };
    }

    private SectionHeader BuildStyleSection()
    {
        var rows = new StackPanel { Spacing = AppTheme.Spacing.Xxs };

        // WP-201: Font, Traits, Size, Alignment and Color live in the shared control (same rows
        // as the Inspector text section). Outline/Background/Shadow stay in their own sections
        // below, which carry extras the shared rows don't model (shadow color, opacities), and
        // Case + Position stay here — they are caption-form concerns, not TextStyle.
        CaptionsStyleRows = new TextStyleRows
        {
            ShownRows = TextStyleRowKind.Font | TextStyleRowKind.Traits | TextStyleRowKind.Size
                | TextStyleRowKind.Alignment | TextStyleRowKind.Color,
            Defaults = new TextStyle
            {
                FontName = "Segoe UI",
                FontSize = CaptionFormLogic.DefaultFontSize,
                IsBold = true,
                IsItalic = false,
                Alignment = TextAlign.Center,
                Color = CloneRgba(DefaultFill),
            },
        };
        // The Style setter clones, so seeding from Defaults cannot alias the reset target.
        CaptionsStyleRows.Value = CaptionsStyleRows.Defaults;
        CaptionsStyleRows.StyleChanged += (_, _) => OnCaptionFormChanged();
        rows.Children.Add(CaptionsStyleRows);

        CaptionsCaseBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        CaptionsCaseBox.Items.Add(new ComboBoxItem { Content = "Auto", Tag = "auto" });
        CaptionsCaseBox.Items.Add(new ComboBoxItem { Content = "Upper", Tag = "upper" });
        CaptionsCaseBox.Items.Add(new ComboBoxItem { Content = "Lower", Tag = "lower" });
        CaptionsCaseBox.SelectedIndex = 0;
        AutomationProperties.SetName(CaptionsCaseBox, "Text case");
        CaptionsCaseRow = new EditorRow
        {
            Label = "Case",
            LabelHelp = "Auto leaves transcribed casing. Reset returns to Auto.",
            RowContent = CaptionsCaseBox,
        };
        CaptionsCaseBox.SelectionChanged += (_, _) =>
        {
            CaptionsCaseRow.CanReset = CaptionsCaseBox.SelectedIndex != 0;
            OnCaptionFormChanged();
        };
        CaptionsCaseRow.ResetRequested += (_, _) => CaptionsCaseBox.SelectedIndex = 0;
        rows.Children.Add(CaptionsCaseRow);

        CaptionsPosXField = new UnitNumberField();
        CaptionsPosYField = new UnitNumberField();
        // WP-196: one axis per line so each field gets the full value width instead of
        // squeezing X and Y into half a narrow panel.
        var pos = new Grid
        {
            ColumnSpacing = AppTheme.Spacing.SmMd,
            RowSpacing = AppTheme.Spacing.Xxs,
        };
        pos.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pos.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pos.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pos.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var xLabel = AxisLabel("X");
        var yLabel = AxisLabel("Y");
        Grid.SetRow(xLabel, 0);
        Grid.SetColumn(xLabel, 0);
        Grid.SetRow(CaptionsPosXField, 0);
        Grid.SetColumn(CaptionsPosXField, 1);
        Grid.SetRow(yLabel, 1);
        Grid.SetColumn(yLabel, 0);
        Grid.SetRow(CaptionsPosYField, 1);
        Grid.SetColumn(CaptionsPosYField, 1);
        pos.Children.Add(xLabel);
        pos.Children.Add(CaptionsPosXField);
        pos.Children.Add(yLabel);
        pos.Children.Add(CaptionsPosYField);
        CaptionsPositionRow = new EditorRow
        {
            Label = "Position",
            LabelHelp = "Caption center in canvas pixels. Default is X = 50% of width, Y = 90% of height.",
            RowContent = pos,
        };
        CaptionsPosXField.ValueChanged += (_, _) => OnPositionFieldChanged(isX: true);
        CaptionsPosYField.ValueChanged += (_, _) => OnPositionFieldChanged(isX: false);
        CaptionsPositionRow.ResetRequested += (_, _) =>
        {
            _captionCenterX = CaptionFormLogic.DefaultCenterX;
            _captionCenterY = CaptionFormLogic.DefaultCenterY;
            UpdatePositionFieldSpecs();
            OnCaptionFormChanged();
        };
        rows.Children.Add(CaptionsPositionRow);

        return new SectionHeader
        {
            SectionId = "captions.style",
            Title = "Style",
            SectionContent = rows,
        };
    }

    private SectionHeader BuildOutlineSection()
    {
        var rows = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
        CaptionsOutlineColorWell = new ColorWell { Purpose = "Outline color", ColorValue = CloneRgba(DefaultOutlineColor) };
        rows.Children.Add(BindColorRow(
            "Color", "Outline color. Width is not modelled on Windows yet (WP-126).",
            CaptionsOutlineColorWell, DefaultOutlineColor));

        CaptionsOutlineSection = new SectionHeader
        {
            SectionId = "captions.outline",
            Title = "Outline",
            HasToggle = true,
            IsOn = false,
            SectionContent = rows,
        };
        CaptionsOutlineSection.IsOnChanged += (_, _) => OnCaptionFormChanged();
        return CaptionsOutlineSection;
    }

    private SectionHeader BuildShadowSection()
    {
        var rows = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
        CaptionsShadowColorWell = new ColorWell { Purpose = "Shadow color", ColorValue = CloneRgba(DefaultShadowColor) };
        rows.Children.Add(BindColorRow("Color", "Shadow color. Opacity is the alpha of this color.", CaptionsShadowColorWell, DefaultShadowColor));
        CaptionsShadowColorWell.ColorChanged += (_, _) => SyncShadowOpacityFromColor();

        CaptionsShadowOpacityField = new UnitNumberField { Spec = OpacitySpec(60), Value = 60 };
        rows.Children.Add(BindValueRow("Opacity", "Shadow opacity.", CaptionsShadowOpacityField, SyncShadowColorFromOpacity));

        CaptionsShadowOffsetXField = new UnitNumberField { Spec = OffsetXSpec, Value = 0 };
        CaptionsShadowOffsetYField = new UnitNumberField { Spec = OffsetYSpec, Value = -2 };
        // WP-196: one axis per line, same as the Style Position row.
        var offset = new Grid
        {
            ColumnSpacing = AppTheme.Spacing.SmMd,
            RowSpacing = AppTheme.Spacing.Xxs,
        };
        offset.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        offset.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        offset.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        offset.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var ox = AxisLabel("X");
        var oy = AxisLabel("Y");
        Grid.SetRow(ox, 0);
        Grid.SetColumn(ox, 0);
        Grid.SetRow(CaptionsShadowOffsetXField, 0);
        Grid.SetColumn(CaptionsShadowOffsetXField, 1);
        Grid.SetRow(oy, 1);
        Grid.SetColumn(oy, 0);
        Grid.SetRow(CaptionsShadowOffsetYField, 1);
        Grid.SetColumn(CaptionsShadowOffsetYField, 1);
        offset.Children.Add(ox);
        offset.Children.Add(CaptionsShadowOffsetXField);
        offset.Children.Add(oy);
        offset.Children.Add(CaptionsShadowOffsetYField);
        var offsetRow = new EditorRow
        {
            Label = "Offset",
            LabelHelp = "Shadow offset in points. Default X 0, Y −2.",
            RowContent = offset,
        };
        void SyncOffsetReset() =>
            offsetRow.CanReset = CaptionsShadowOffsetXField.DiffersFromDefault || CaptionsShadowOffsetYField.DiffersFromDefault;
        CaptionsShadowOffsetXField.ValueChanged += (_, _) => { SyncOffsetReset(); OnCaptionFormChanged(); };
        CaptionsShadowOffsetYField.ValueChanged += (_, _) => { SyncOffsetReset(); OnCaptionFormChanged(); };
        offsetRow.ResetRequested += (_, _) =>
        {
            CaptionsShadowOffsetXField.ResetToDefault();
            CaptionsShadowOffsetYField.ResetToDefault();
        };
        rows.Children.Add(offsetRow);

        CaptionsShadowBlurField = new UnitNumberField { Spec = BlurSpec, Value = 6 };
        rows.Children.Add(BindValueRow("Blur", "Shadow blur radius.", CaptionsShadowBlurField));

        CaptionsShadowSection = new SectionHeader
        {
            SectionId = "captions.shadow",
            Title = "Shadow",
            HasToggle = true,
            IsOn = true,
            SectionContent = rows,
        };
        CaptionsShadowSection.IsOnChanged += (_, _) => OnCaptionFormChanged();
        return CaptionsShadowSection;
    }

    private SectionHeader BuildBackgroundSection()
    {
        var rows = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
        CaptionsBackgroundColorWell = new ColorWell { Purpose = "Background color", ColorValue = CloneRgba(DefaultBackgroundColor) };
        rows.Children.Add(BindColorRow(
            "Color", "Caption pill color. Padding and corner radius wait on WP-126.",
            CaptionsBackgroundColorWell, DefaultBackgroundColor));
        CaptionsBackgroundColorWell.ColorChanged += (_, _) => SyncBackgroundOpacityFromColor();

        CaptionsBackgroundOpacityField = new UnitNumberField { Spec = OpacitySpec(60), Value = 60 };
        rows.Children.Add(BindValueRow("Opacity", "Background opacity.", CaptionsBackgroundOpacityField, SyncBackgroundColorFromOpacity));

        CaptionsBackgroundSection = new SectionHeader
        {
            SectionId = "captions.background",
            Title = "Background",
            HasToggle = true,
            IsOn = false,
            SectionContent = rows,
        };
        CaptionsBackgroundSection.IsOnChanged += (_, _) => OnCaptionFormChanged();
        return CaptionsBackgroundSection;
    }

    private SectionHeader BuildAnimationSection()
    {
        var rows = new StackPanel { Spacing = AppTheme.Spacing.Md };
        CaptionsAnimationHost = new StackPanel { Spacing = AppTheme.Spacing.Lg };
        rows.Children.Add(CaptionsAnimationHost);

        CaptionsHighlightWell = new ColorWell
        {
            Purpose = "Highlight color",
            ColorValue = CloneRgba(TextAnimation.DefaultHighlight),
        };
        CaptionsHighlightRow = BindColorRow(
            "Highlight",
            "Feeds per-word gallery cells and karaoke color.",
            CaptionsHighlightWell,
            TextAnimation.DefaultHighlight);
        CaptionsHighlightWell.ColorChanged += (_, value) =>
        {
            if (_captionPresetGallery is not null)
                _captionPresetGallery.Highlight = value;
        };
        CaptionsHighlightRow.ResetRequested += (_, _) =>
        {
            if (_captionPresetGallery is not null)
                _captionPresetGallery.Highlight = CaptionsHighlightWell.ColorValue;
        };
        rows.Children.Add(CaptionsHighlightRow);

        return new SectionHeader
        {
            SectionId = "captions.animation",
            Title = "Animation",
            SectionContent = rows,
        };
    }

    private EditorRow BindValueRow(string label, string help, UnitNumberField field, Action? extraChanged = null)
    {
        var row = new EditorRow
        {
            Label = label,
            LabelHelp = help,
            RowContent = field,
            CanReset = field.DiffersFromDefault,
        };
        field.ValueChanged += (_, _) =>
        {
            row.CanReset = field.DiffersFromDefault;
            extraChanged?.Invoke();
            OnCaptionFormChanged();
        };
        field.RegisterPropertyChangedCallback(UnitNumberField.ValueProperty, (_, _) => row.CanReset = field.DiffersFromDefault);
        field.RegisterPropertyChangedCallback(UnitNumberField.SpecProperty, (_, _) => row.CanReset = field.DiffersFromDefault);
        row.ResetRequested += (_, _) => field.ResetToDefault();
        return row;
    }

    private EditorRow BindColorRow(string label, string help, ColorWell well, TextStyle.RGBA defaultColor)
    {
        // WP-196: left-align the swatch on the value column like every other row content.
        well.HorizontalAlignment = HorizontalAlignment.Left;
        var row = new EditorRow
        {
            Label = label,
            LabelHelp = help,
            RowContent = well,
        };
        void SyncReset() => row.CanReset = !SameColor(well.ColorValue, defaultColor);
        well.ColorChanged += (_, _) =>
        {
            SyncReset();
            OnCaptionFormChanged();
        };
        row.ResetRequested += (_, _) =>
        {
            well.ColorValue = CloneRgba(defaultColor);
            SyncReset();
            OnCaptionFormChanged();
        };
        SyncReset();
        return row;
    }

    private CaptionRequest BuildCaptionRequestFromUi()
    {
        var request = new CaptionRequest
        {
            Style = BuildCaptionStyleFromUi(),
            CenterX = _captionCenterX,
            CenterY = _captionCenterY,
            MaxWords = CaptionFormLogic.UnlimitedOrCount(CaptionsMaxWordsField?.Value ?? CaptionFormLogic.DefaultMaxWords),
            MaxCharacters = CaptionFormLogic.UnlimitedOrCount(CaptionsMaxCharsField?.Value ?? 0),
            MaximumGapSeconds = CaptionFormLogic.GapMsToSeconds(CaptionsGapField?.Value ?? CaptionFormLogic.GapMsDefault),
        };

        if (CaptionsCaseBox?.SelectedItem is ComboBoxItem caseItem
            && caseItem.Tag is string caseTag
            && Enum.TryParse<CaptionCase>(caseTag, ignoreCase: true, out var textCase))
        {
            request.TextCase = textCase;
        }

        if (CaptionsSourceBox?.SelectedItem is ComboBoxItem { Tag: string trackId }
            && AppSession.Current.Session is { } activeSession)
        {
            var trackIndex = activeSession.ActiveTimeline.Tracks
                .FindIndex(t => string.Equals(t.Id, trackId, StringComparison.Ordinal));
            request.TrackIndex = trackIndex >= 0 ? trackIndex : null;
        }
        else
        {
            request.TrackIndex = null;
        }

        if (CaptionsAnimationGallery is not null
            && !TextAnimation.IsOff(CaptionsAnimationGallery.SelectedPreset))
        {
            var preset = TextAnimation.CanonicalPreset(CaptionsAnimationGallery.SelectedPreset);
            request.Animation = new TextAnimation
            {
                Preset = preset,
                Highlight = TextAnimation.UsesHighlight(preset)
                    ? CloneRgba(CaptionsHighlightWell?.ColorValue) ?? TextAnimation.DefaultHighlight
                    : null,
            };
        }

        request.Language = CaptionsLanguageBox?.SelectedItem is ComboBoxItem { Tag: string language }
            ? language
            : "en";

        return request;
    }

    private TextStyle BuildCaptionStyleFromUi()
    {
        // WP-201: the five owned rows come from the shared control; Outline/Background/Shadow
        // stay with their sections (shadow color, opacities), exactly as before.
        var owned = CaptionsStyleRows.Value;
        return new TextStyle
        {
            FontName = owned.FontName,
            FontSize = owned.FontSize,
            IsBold = owned.IsBold,
            IsItalic = owned.IsItalic,
            Color = CloneRgba(owned.Color) ?? CloneRgba(DefaultFill),
            Alignment = owned.Alignment,
            Shadow = new TextStyle.ShadowStyle
            {
                Enabled = CaptionsShadowSection?.IsOn ?? true,
                Color = CloneRgba(CaptionsShadowColorWell?.ColorValue) ?? CloneRgba(DefaultShadowColor),
                OffsetX = CaptionsShadowOffsetXField?.Value ?? 0,
                OffsetY = CaptionsShadowOffsetYField?.Value ?? -2,
                Blur = CaptionsShadowBlurField?.Value ?? 6,
            },
            Border = new TextStyle.FillStyle
            {
                Enabled = CaptionsOutlineSection?.IsOn ?? false,
                Color = CloneRgba(CaptionsOutlineColorWell?.ColorValue) ?? CloneRgba(DefaultOutlineColor),
            },
            Background = new TextStyle.FillStyle
            {
                Enabled = CaptionsBackgroundSection?.IsOn ?? false,
                Color = CloneRgba(CaptionsBackgroundColorWell?.ColorValue) ?? CloneRgba(DefaultBackgroundColor),
            },
        };
    }

    private CaptionPresetGallery? CaptionsAnimationGallery => _captionPresetGallery;

    private void EnsureCaptionPresetGallery()
    {
        if (CaptionsAnimationHost is null || _captionPresetGallery is not null)
            return;
        var gallery = new CaptionPresetGallery();
        gallery.SelectionChanged += CaptionsAnimationGallery_SelectionChanged;
        gallery.Highlight = CloneRgba(CaptionsHighlightWell?.ColorValue) ?? TextAnimation.DefaultHighlight;
        _captionPresetGallery = gallery;
        CaptionsAnimationHost.Children.Add(gallery);
    }

    private void CaptionsAnimationGallery_SelectionChanged(object? sender, EventArgs e)
    {
        SyncHighlightRow();
        RefreshCaptionPreview();
    }

    private void RefreshCaptionsSourceOptions()
    {
        if (CaptionsSourceBox is null || _syncingCaptionsSource) return;
        _syncingCaptionsSource = true;
        try
        {
            var previousTrackId = CaptionsSourceBox.SelectedItem is ComboBoxItem { Tag: string id } ? id : null;

            CaptionsSourceBox.Items.Clear();
            CaptionsSourceBox.Items.Add(new ComboBoxItem { Content = "Auto (track with the most speech)" });

            var session = AppSession.Current.Session;
            if (session is not null)
            {
                foreach (var source in CaptionGenerator.ListSourceTracks(session))
                {
                    CaptionsSourceBox.Items.Add(new ComboBoxItem
                    {
                        Content = source.DisplayName,
                        Tag = source.TrackId,
                    });
                }
            }

            var restored = previousTrackId is not null
                ? CaptionsSourceBox.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => item.Tag is string t && string.Equals(t, previousTrackId, StringComparison.Ordinal))
                : null;
            CaptionsSourceBox.SelectedItem = restored ?? CaptionsSourceBox.Items[0];
        }
        finally
        {
            _syncingCaptionsSource = false;
        }
    }

    private void CaptionsModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var cloud = CaptionsModeBox.SelectedIndex == 1;
        if (CaptionsLanguageBox is not null)
        {
            CaptionsLanguageBox.IsEnabled = cloud;
            if (!cloud)
                CaptionsLanguageBox.SelectedIndex = 0;
        }

        if (CaptionsLanguageRow is not null)
        {
            CaptionsLanguageRow.LabelHelp = cloud
                ? "Cloud transcription currently admits English and Spanish."
                : "The bundled transcription model supports English.";
        }

        ConfigureCaptionsAvailability();
    }

    private void RefreshCaptionsSourceLabel()
    {
        RefreshCaptionsSourceOptions();
        UpdateCaptionsSourceLabelText();
        SyncSourceReset();
    }

    private void UpdateCaptionsSourceLabelText()
    {
        var session = AppSession.Current.Session;
        if (CaptionsSourceLabel is null) return;
        if (session is null)
        {
            CaptionsSourceLabel.Text = "Source: (no project)";
            return;
        }

        var sources = CaptionGenerator.ListSourceTracks(session);
        var chosenId = CaptionsSourceBox?.SelectedItem is ComboBoxItem { Tag: string id } ? id : null;
        var selected = chosenId is not null
            ? sources.FirstOrDefault(s => string.Equals(s.TrackId, chosenId, StringComparison.Ordinal))
            : null;

        var scope = selected is not null ? selected.Label : "Auto";
        var audioClips = selected?.ClipCount ?? sources.Sum(s => s.ClipCount);
        CaptionsSourceLabel.Text = audioClips == 0
            ? $"Source: {scope} · no audio clips to caption"
            : $"Source: {scope} · {audioClips} audio {(audioClips == 1 ? "clip" : "clips")} on “{session.ActiveTimeline.Name}”";
    }

    private void CaptionsSourceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingCaptionsSource) return;
        UpdateCaptionsSourceLabelText();
    }

    private void CaptionsPreview_Toggled() => RefreshCaptionPreview();

    /// <summary>
    /// Mirrors the Captions form onto the preview canvas (Mac's `Preview` toggle). What it shows is
    /// rendered by the same renderer as the export, so it cannot drift from the result.
    /// </summary>
    private void RefreshCaptionPreview()
    {
        if (CaptionsSourceSection is null || Preview is null) return;

        if (!CaptionsPreviewToggle.IsOn)
        {
            Preview.HideCaptionOverlay();
            return;
        }

        var session = AppSession.Current.Session;
        var timeline = session?.ActiveTimeline;
        var canvasW = Math.Max(16, timeline?.Width ?? 1920);
        var canvasH = Math.Max(16, timeline?.Height ?? 1080);

        var request = BuildCaptionRequestFromUi();
        var transform = TextClipCommands.FitTransform(
            Temno.Media.Text.CaptionPreview.SampleText, request.Style, canvasW, canvasH, request.CenterX, request.CenterY);

        Preview.ShowCaptionOverlay(
            Temno.Media.Text.CaptionPreview.SampleText, request.Style, transform, request.Animation, canvasW, canvasH);
    }

    private async void GenerateCaptions_Click(object sender, RoutedEventArgs e)
    {
        if (_generatingCaptions) return;
        var session = AppSession.Current.Session;
        if (session is null)
        {
            CaptionsStatusLabel.Text = "Open a project first.";
            return;
        }

        RefreshCaptionsSourceLabel();

        var modeIndex = CaptionsModeBox.SelectedIndex;
        var cloud = modeIndex == 1;
        if (cloud)
        {
            if (!AccountSessionService.Current.IsSignedIn)
            {
                CaptionsStatusLabel.Text = "Sign in to use cloud transcription.";
                StatusText.Text = "Captions: sign in required";
                return;
            }

            if (!HostedEndpoints.GatewayConfigured())
            {
                CaptionsStatusLabel.Text = "Cloud transcription is not configured in this build.";
                StatusText.Text = "Captions: cloud unconfigured";
                return;
            }
        }

        var progress = new Progress<string>(message =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                CaptionsStatusLabel.Text = message;
                StatusText.Text = message;
            });
        });
        var provider = TranscriptionServices.Resolve(cloud, progress);
        var usingFake = TranscriptionServices.UseFakeProvider;

        _captionCts?.Cancel();
        _captionCts?.Dispose();
        _captionCts = new CancellationTokenSource();
        var ct = _captionCts.Token;
        _generatingCaptions = true;
        ConfigureCaptionsAvailability();
        if (usingFake)
            SetCaptionBusy("Generating sample captions (demo)…", "Generating captions…");
        else if (cloud)
            SetCaptionBusy("Uploading audio for cloud transcription…", "Cloud transcription…");
        else if (!WhisperModelStore.IsReady)
            SetCaptionBusy("Downloading the English transcription model (~142 MB)…", "Downloading model…");
        else
            SetCaptionBusy("Transcribing on this PC…", "Transcribing…");
        try
        {
            // One dispatcher turn so the overlay actually paints before GenerateAsync
            // completes (cached / no-speech paths can otherwise finish in the same tick).
            await Task.Delay(50);
            var request = BuildCaptionRequestFromUi();
            var result = await CaptionGenerator.GenerateAsync(session, provider, request, ct);
            RefreshAfterEdit();
            if (result.CreatedClipIds.Count == 0)
            {
                var skip = result.Skipped.Count > 0
                    ? " · " + result.Skipped[0].Reason
                    : " · no speech detected on timeline media";
                CaptionsStatusLabel.Text = "No caption clips created" + skip;
                StatusText.Text = "Captions: nothing created";
                return;
            }

            DismissCaptionPreview();

            var opts = request.MaxWords is { } mw ? $" · max {mw} words" : " · phrase length";
            opts += $" · case {request.TextCase}";
            CaptionsStatusLabel.Text =
                $"Created {result.CreatedClipIds.Count} caption clip(s) · source {result.TranscriptionSource}{opts}";
            StatusText.Text = $"Captions: {result.CreatedClipIds.Count} clip(s)";
        }
        catch (OperationCanceledException)
        {
            CaptionsStatusLabel.Text = "Caption generation stopped.";
            StatusText.Text = "Captions stopped";
        }
        catch (Exception ex)
        {
            CaptionsStatusLabel.Text = "Caption generation failed: " + ex.Message;
            StatusText.Text = "Captions failed";
        }
        finally
        {
            _generatingCaptions = false;
            ConfigureCaptionsAvailability();
        }
    }

    /// <summary>The sample must never be mistaken for a generated caption once real ones exist.</summary>
    private void DismissCaptionPreview()
    {
        if (CaptionsPreviewToggle is { IsOn: true }) CaptionsPreviewToggle.IsOn = false;
        Preview?.HideCaptionOverlay();
    }

    private void StopCaptions_Click(object sender, RoutedEventArgs e)
    {
        if (!_generatingCaptions) return;
        _captionCts?.Cancel();
        CaptionsStatusLabel.Text = "Stopping caption generation…";
        StatusText.Text = "Stopping captions…";
    }

    private void ConfigureCaptionsAvailability()
    {
        var demo = TranscriptionServices.UseFakeProvider;
        var cloud = CaptionsModeBox?.SelectedIndex == 1;
        if (GenerateCaptionsBtn is null) return;
        GenerateCaptionsBtn.IsEnabled = !_generatingCaptions;
        CaptionAgentModeBtn.Visibility = _generatingCaptions ? Visibility.Collapsed : Visibility.Visible;
        CaptionDefaultsButton.IsEnabled = !_generatingCaptions;
        if (StopCaptionsBtn is not null)
            StopCaptionsBtn.Visibility = _generatingCaptions ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(
            GenerateCaptionsBtn,
            demo
                ? "Demo transcription: Generate returns sample captions, not a real transcript."
                : cloud
                    ? "Uploads mono PCM WAV and spends hosted credits. English and Spanish only."
                    : "Transcribes on this PC with the built-in English model.");

        if (CaptionsBusyOverlay is not null)
            CaptionsBusyOverlay.Visibility = _generatingCaptions ? Visibility.Visible : Visibility.Collapsed;
        if (CaptionsBusyRing is not null)
            CaptionsBusyRing.IsActive = _generatingCaptions;
        if (CaptionsFormScroll is not null)
            CaptionsFormScroll.IsHitTestVisible = !_generatingCaptions;

        if (CaptionsHonestyLabel is not null)
        {
            CaptionsHonestyLabel.Text = demo
                ? "Demo mode generates sample captions."
                : cloud
                    ? "Cloud transcription spends hosted credits. English and Spanish only. Local Whisper remains available."
                    : "Transcribes on this PC using the English model.";
            CaptionsHonestyLabel.Foreground = demo ? AppTheme.Status.WarningBrush : AppTheme.Text.SecondaryBrush;

        }
    }

    private void SetCaptionBusy(string panel, string status)
    {
        CaptionsStatusLabel.Text = panel;
        StatusText.Text = status;
        if (CaptionsBusyLabel is not null)
            CaptionsBusyLabel.Text = panel.Contains("Downloading", StringComparison.OrdinalIgnoreCase)
                ? "Downloading…"
                : panel.Contains("demo", StringComparison.OrdinalIgnoreCase)
                    ? "Generating…"
                    : "Transcribing…";
    }

    private void RefreshCaptionFormFromTimeline()
    {
        if (!_captionFormBuilt) return;
        RefreshCaptionsSourceLabel();
        UpdatePositionFieldSpecs();
        RefreshCaptionPreview();
    }

    private void OnCaptionFormChanged()
    {
        if (_syncingCaptionForm || !_captionFormBuilt) return;
        RefreshCaptionPreview();
    }

    private void SyncHighlightRow()
    {
        if (CaptionsHighlightRow is null) return;
        var preset = CaptionsAnimationGallery?.SelectedPreset;
        CaptionsHighlightRow.Visibility = TextAnimation.UsesHighlight(preset)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnPositionFieldChanged(bool isX)
    {
        if (_syncingCaptionForm) return;
        var (w, h) = CaptionCanvasSize();
        if (isX)
            _captionCenterX = CaptionFormLogic.NormalizedFromPixel(CaptionsPosXField.Value, w);
        else
            _captionCenterY = CaptionFormLogic.NormalizedFromPixel(CaptionsPosYField.Value, h);
        SyncPositionReset();
        OnCaptionFormChanged();
    }

    private void UpdatePositionFieldSpecs()
    {
        if (CaptionsPosXField is null || CaptionsPosYField is null) return;
        var (w, h) = CaptionCanvasSize();
        _syncingCaptionForm = true;
        try
        {
            CaptionsPosXField.Spec = new UnitFieldSpec
            {
                Unit = "px", Minimum = 0, Maximum = w, Step = 1, UnitsPerPixel = 1,
                Default = CaptionFormLogic.PixelFromNormalized(CaptionFormLogic.DefaultCenterX, w),
            };
            CaptionsPosYField.Spec = new UnitFieldSpec
            {
                Unit = "px", Minimum = 0, Maximum = h, Step = 1, UnitsPerPixel = 1,
                Default = CaptionFormLogic.PixelFromNormalized(CaptionFormLogic.DefaultCenterY, h),
            };
            CaptionsPosXField.Value = CaptionFormLogic.PixelFromNormalized(_captionCenterX, w);
            CaptionsPosYField.Value = CaptionFormLogic.PixelFromNormalized(_captionCenterY, h);
        }
        finally
        {
            _syncingCaptionForm = false;
        }
        SyncPositionReset();
    }

    private (double Width, double Height) CaptionCanvasSize()
    {
        var timeline = AppSession.Current.Session?.ActiveTimeline;
        return (Math.Max(16, timeline?.Width ?? 1920), Math.Max(16, timeline?.Height ?? 1080));
    }

    private void SyncPositionReset()
    {
        if (CaptionsPositionRow is null) return;
        CaptionsPositionRow.CanReset =
            CaptionsPosXField.DiffersFromDefault || CaptionsPosYField.DiffersFromDefault;
    }

    /// <summary>
    /// WP-196: the captions ScrollViewer overlays its scrollbar, so the form keeps a matching right
    /// inset only while the bar is actually visible. The bar is ~12 px; inset 16.
    /// </summary>
    private void SyncCaptionFormInset()
    {
        if (CaptionsFormHost is null || CaptionsFormScroll is null) return;
        var reserve = CaptionsFormScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible
            ? AppTheme.Spacing.LgXl + AppTheme.Spacing.Xxs
            : 0;
        var margin = CaptionsFormHost.Margin;
        if (Math.Abs(margin.Right - reserve) > double.Epsilon)
            CaptionsFormHost.Margin = new Thickness(margin.Left, margin.Top, reserve, margin.Bottom);
    }

    private void SyncSourceReset()
    {
        if (CaptionsSourceRow is null) return;
        CaptionsSourceRow.CanReset = CaptionsSourceBox.SelectedItem is ComboBoxItem { Tag: string { Length: > 0 } };
    }

    private void SyncShadowOpacityFromColor()
    {
        if (_syncingCaptionForm || CaptionsShadowOpacityField is null) return;
        _syncingCaptionForm = true;
        CaptionsShadowOpacityField.Value = Math.Round((CaptionsShadowColorWell.ColorValue?.A ?? 0.6) * 100);
        _syncingCaptionForm = false;
    }

    private void SyncShadowColorFromOpacity()
    {
        if (_syncingCaptionForm || CaptionsShadowColorWell is null) return;
        _syncingCaptionForm = true;
        var color = CloneRgba(CaptionsShadowColorWell.ColorValue) ?? CloneRgba(DefaultShadowColor);
        color.A = CaptionsShadowOpacityField.Value / 100.0;
        CaptionsShadowColorWell.ColorValue = color;
        _syncingCaptionForm = false;
    }

    private void SyncBackgroundOpacityFromColor()
    {
        if (_syncingCaptionForm || CaptionsBackgroundOpacityField is null) return;
        _syncingCaptionForm = true;
        CaptionsBackgroundOpacityField.Value = Math.Round((CaptionsBackgroundColorWell.ColorValue?.A ?? 0.6) * 100);
        _syncingCaptionForm = false;
    }

    private void SyncBackgroundColorFromOpacity()
    {
        if (_syncingCaptionForm || CaptionsBackgroundColorWell is null) return;
        _syncingCaptionForm = true;
        var color = CloneRgba(CaptionsBackgroundColorWell.ColorValue) ?? CloneRgba(DefaultBackgroundColor);
        color.A = CaptionsBackgroundOpacityField.Value / 100.0;
        CaptionsBackgroundColorWell.ColorValue = color;
        _syncingCaptionForm = false;
    }

    private static TextStyle.RGBA CloneRgba(TextStyle.RGBA? color)
    {
        color ??= new TextStyle.RGBA();
        return new TextStyle.RGBA { R = color.R, G = color.G, B = color.B, A = color.A };
    }

    private static bool SameColor(TextStyle.RGBA? a, TextStyle.RGBA? b)
        => string.Equals(TextClipCommands.ToHexColor(a), TextClipCommands.ToHexColor(b), StringComparison.Ordinal);

    private static TextBlock MutedText(string text, Brush foreground, double size) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = foreground,
        TextWrapping = TextWrapping.WrapWholeWords,
    };

    private static TextBlock AxisLabel(string text) => new()
    {
        Text = text,
        FontSize = AppTheme.FontSize.Sm,
        Foreground = AppTheme.Text.TertiaryBrush,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
