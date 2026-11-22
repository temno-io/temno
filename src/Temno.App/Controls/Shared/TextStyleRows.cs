using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Temno.App.Theme;
using Temno.Core.Models;
using Temno.Core.Ui;
using TextAlign = Temno.Core.Models.TextAlignment;

namespace Temno.App.Controls.Shared;

/// <summary>
/// Arguments for <see cref="TextStyleRows.StyleChanged"/>.
/// </summary>
public sealed class TextStyleChangedEventArgs : EventArgs
{
    public TextStyleChangedEventArgs(TextStyle style, bool isLiveNumeric)
    {
        Style = style;
        IsLiveNumeric = isLiveNumeric;
    }

    /// <summary>The control's working copy. Hosts read it on commit; they must not keep it.</summary>
    public TextStyle Style { get; }

    /// <summary>
    /// True when the edit came from a numeric field's live <c>ValueChanged</c> (typing, scrub,
    /// wheel) rather than a committed discrete edit. The Inspector ignores these and commits on
    /// <see cref="TextStyleRows.StyleCommitRequested"/> (field <c>LostFocus</c>) so spins do not
    /// spam undo; the Captions form treats both as a preview refresh.
    /// </summary>
    public bool IsLiveNumeric { get; }
}

/// <summary>
/// WP-201. The one text-style control set: the <see cref="TextStyle"/>-modelled rows (font,
/// weight, size, alignment, color, background, outline, shadow) in one canonical order, used by
/// both the Inspector text section and the Captions Style section.
/// </summary>
/// <remarks>
/// <para>
/// Contract: <see cref="Value"/> in (the control clones, so the host keeps ownership of its
/// object), <see cref="StyleChanged"/> / <see cref="StyleCommitRequested"/> out, and the host
/// owns what happens next — the Inspector writes a <c>TextPatch</c> to the selected clips, the
/// Captions form rebuilds its request Style for the preview and Generate. Caption defaults and
/// clip edits stay distinct because neither host ever sees the other's <c>TextStyle</c> instance.
/// </para>
/// <para>
/// Deliberately NOT shared: case, position and highlight are not <c>TextStyle</c> fields (case is
/// a transcribe-time transform, position is placement, highlight feeds the animation gallery and
/// karaoke), and shadow color has no <c>TextStylePatch</c> field so the Inspector could not write
/// it without a Core/MCP contract change. Those rows stay host-owned next to the block.
/// </para>
/// </remarks>
public sealed partial class TextStyleRows : UserControl
{
    private TextStyle _style = new();
    private TextStyle _defaults = new();
    private TextStyleRowKind _shownRows = TextStyleRowKind.All;
    private bool _syncing;

    private readonly ComboBox _fontBox = new() { HorizontalAlignment = HorizontalAlignment.Stretch, IsEditable = true };
    private readonly TextBlock _fontResolved = new()
    {
        FontSize = AppTheme.FontSize.Xs,
        Foreground = AppTheme.Text.TertiaryBrush,
        TextWrapping = TextWrapping.WrapWholeWords,
        Visibility = Visibility.Collapsed,
    };
    private readonly EditorRow _fontRow = new() { Label = "Font", LabelHelp = "System families. Helvetica and SF names render as Segoe UI on Windows." };
    private readonly ToggleButton _boldBtn = TraitButton("Bold", "Bold", isBoldFace: true);
    private readonly ToggleButton _italicBtn = TraitButton("Italic", "Italic", isBoldFace: false);
    private readonly EditorRow _weightRow = new() { Label = "Style", LabelHelp = "Bold and italic.", ShowLabel = false };
    private readonly UnitNumberField _sizeField = new();
    private readonly EditorRow _sizeRow = new() { Label = "Size", LabelHelp = "Font size in points." };
    private readonly ToggleButton _alignLeftBtn = TraitButton("Left", "Align left", isBoldFace: false);
    private readonly ToggleButton _alignCenterBtn = TraitButton("Center", "Align center", isBoldFace: false);
    private readonly ToggleButton _alignRightBtn = TraitButton("Right", "Align right", isBoldFace: false);
    private readonly EditorRow _alignRow = new() { Label = "Alignment", LabelHelp = "Horizontal alignment.", ShowLabel = false };
    private readonly ColorWell _colorWell = new() { Purpose = "Text color" };
    private readonly EditorRow _colorRow = new() { Label = "Color", LabelHelp = "Fill color of the text." };
    private readonly ToggleColorRow _backgroundRow = new()
    {
        Label = "Background",
        LabelHelp = "Pill behind the text. Padding and corner radius are not modelled on Windows yet.",
    };
    private readonly ToggleColorRow _outlineRow = new()
    {
        Label = "Outline",
        LabelHelp = "Outline color. Width is not modelled on Windows yet (WP-126).",
    };
    private readonly ToggleSwitch _shadowSwitch = new() { OffContent = "Off", OnContent = "On" };
    private readonly EditorRow _shadowRow = new() { Label = "Shadow", LabelHelp = "Shadow on or off." };
    private readonly UnitNumberField _offsetXField = new();
    private readonly UnitNumberField _offsetYField = new();
    private readonly EditorRow _offsetXRow = new() { Label = "Shadow X", LabelHelp = "Shadow horizontal offset in points." };
    private readonly EditorRow _offsetYRow = new() { Label = "Shadow Y", LabelHelp = "Shadow vertical offset in points." };
    private readonly UnitNumberField _blurField = new();
    private readonly EditorRow _blurRow = new() { Label = "Shadow blur", LabelHelp = "Shadow blur radius." };

    public TextStyleRows()
    {
        _fontRow.RowContent = new StackPanel
        {
            Spacing = AppTheme.Spacing.Xxs,
            Children = { _fontBox, _fontResolved },
        };
        _weightRow.RowContent = EqualButtons(_boldBtn, _italicBtn);
        _sizeRow.RowContent = _sizeField;
        _alignRow.RowContent = EqualButtons(_alignLeftBtn, _alignCenterBtn, _alignRightBtn);
        _colorWell.ShowValue = true;
        _colorWell.HorizontalAlignment = HorizontalAlignment.Stretch;
        _colorRow.RowContent = _colorWell;
        _shadowRow.RowContent = _shadowSwitch;
        _offsetXRow.RowContent = _offsetXField;
        _offsetYRow.RowContent = _offsetYField;
        _blurRow.RowContent = _blurField;

        Content = new StackPanel
        {
            Spacing = AppTheme.Spacing.Xxs,
            Children =
            {
                _fontRow, _sizeRow, _weightRow, _alignRow, _colorRow,
                _backgroundRow, _outlineRow, _shadowRow, _offsetXRow, _offsetYRow, _blurRow,
            },
        };
        IsTabStop = false;

        EnsureFontItems();
        ApplySpecs();

        _fontBox.SelectionChanged += (_, _) => OnFontEdited();
        _fontBox.LostFocus += (_, _) => OnFontEdited();
        _fontRow.ResetRequested += (_, _) =>
        {
            _style.FontName = _defaults.FontName;
            _syncing = true;
            try { ApplyFontToWidgets(); }
            finally { _syncing = false; }
            RaiseChanged(isLiveNumeric: false);
        };
        _boldBtn.Click += (_, _) => { _style.IsBold = _boldBtn.IsChecked == true; SyncWeightReset(); RaiseChanged(isLiveNumeric: false); };
        _italicBtn.Click += (_, _) => { _style.IsItalic = _italicBtn.IsChecked == true; SyncWeightReset(); RaiseChanged(isLiveNumeric: false); };
        _weightRow.ResetRequested += (_, _) =>
        {
            _style.IsBold = _defaults.IsBold;
            _style.IsItalic = _defaults.IsItalic;
            _syncing = true;
            try
            {
                _boldBtn.IsChecked = _style.IsBold;
                _italicBtn.IsChecked = _style.IsItalic;
                SyncWeightReset();
            }
            finally { _syncing = false; }
            RaiseChanged(isLiveNumeric: false);
        };
        _sizeField.ValueChanged += (_, _) => { _style.FontSize = _sizeField.Value; SyncSizeReset(); RaiseChanged(isLiveNumeric: true); };
        _sizeField.LostFocus += (_, _) => RaiseCommitted();
        _sizeField.CommitRequested += (_, _) => RaiseCommitted();
        _sizeRow.ResetRequested += (_, _) => ResetNumeric(_sizeField);
        _alignLeftBtn.Click += (_, _) => SetAlignment(TextAlign.Left);
        _alignCenterBtn.Click += (_, _) => SetAlignment(TextAlign.Center);
        _alignRightBtn.Click += (_, _) => SetAlignment(TextAlign.Right);
        _alignRow.ResetRequested += (_, _) => SetAlignment(_defaults.Alignment);
        _colorWell.ColorChanged += (_, c) => { _style.Color = CloneRgba(c); SyncColorReset(); RaiseChanged(isLiveNumeric: false); };
        _colorRow.ResetRequested += (_, _) =>
        {
            _style.Color = CloneRgba(_defaults.Color);
            _syncing = true;
            try
            {
                _colorWell.ColorValue = CloneRgba(_style.Color);
                SyncColorReset();
            }
            finally { _syncing = false; }
            RaiseChanged(isLiveNumeric: false);
        };
        _backgroundRow.IsOnChanged += (_, on) => { _style.Background.Enabled = on; SyncBackgroundReset(); RaiseChanged(isLiveNumeric: false); };
        _backgroundRow.ColorChanged += (_, c) => { _style.Background.Color = CloneRgba(c); SyncBackgroundReset(); RaiseChanged(isLiveNumeric: false); };
        _backgroundRow.ResetRequested += (_, _) =>
        {
            _style.Background.Enabled = _defaults.Background.Enabled;
            _style.Background.Color = CloneRgba(_defaults.Background.Color);
            SyncBackgroundReset();
            RaiseChanged(isLiveNumeric: false);
        };
        _outlineRow.IsOnChanged += (_, on) => { _style.Border.Enabled = on; SyncOutlineReset(); RaiseChanged(isLiveNumeric: false); };
        _outlineRow.ColorChanged += (_, c) => { _style.Border.Color = CloneRgba(c); SyncOutlineReset(); RaiseChanged(isLiveNumeric: false); };
        _outlineRow.ResetRequested += (_, _) =>
        {
            _style.Border.Enabled = _defaults.Border.Enabled;
            _style.Border.Color = CloneRgba(_defaults.Border.Color);
            SyncOutlineReset();
            RaiseChanged(isLiveNumeric: false);
        };
        _shadowSwitch.Toggled += (_, _) =>
        {
            if (_syncing) return;
            _style.Shadow.Enabled = _shadowSwitch.IsOn;
            SyncShadowReset();
            RaiseChanged(isLiveNumeric: false);
        };
        _shadowRow.ResetRequested += (_, _) =>
        {
            _style.Shadow.Enabled = _defaults.Shadow.Enabled;
            _syncing = true;
            try
            {
                _shadowSwitch.IsOn = _style.Shadow.Enabled;
                SyncShadowReset();
            }
            finally { _syncing = false; }
            RaiseChanged(isLiveNumeric: false);
        };
        _offsetXField.ValueChanged += (_, _) => { _style.Shadow.OffsetX = _offsetXField.Value; SyncOffsetReset(); RaiseChanged(isLiveNumeric: true); };
        _offsetYField.ValueChanged += (_, _) => { _style.Shadow.OffsetY = _offsetYField.Value; SyncOffsetReset(); RaiseChanged(isLiveNumeric: true); };
        _offsetXField.LostFocus += (_, _) => RaiseCommitted();
        _offsetXField.CommitRequested += (_, _) => RaiseCommitted();
        _offsetYField.LostFocus += (_, _) => RaiseCommitted();
        _offsetYField.CommitRequested += (_, _) => RaiseCommitted();
        _offsetXRow.ResetRequested += (_, _) => ResetNumeric(_offsetXField);
        _offsetYRow.ResetRequested += (_, _) => ResetNumeric(_offsetYField);
        _blurField.ValueChanged += (_, _) => { _style.Shadow.Blur = _blurField.Value; SyncBlurReset(); RaiseChanged(isLiveNumeric: true); };
        _blurField.LostFocus += (_, _) => RaiseCommitted();
        _blurField.CommitRequested += (_, _) => RaiseCommitted();
        _blurRow.ResetRequested += (_, _) => ResetNumeric(_blurField);

        RefreshFromStyle();
        ApplyShownRows();
    }

    /// <summary>
    /// WP-201. Rows the host shows. The Inspector leaves <see cref="TextStyleRowKind.All"/>;
    /// Captions shows only the rows it owns and keeps its Outline/Background/Shadow sections.
    /// Hidden rows keep their values — hosts that hide a row read it from their own section.
    /// </summary>
    public TextStyleRowKind ShownRows
    {
        get => _shownRows;
        set
        {
            _shownRows = value;
            ApplyShownRows();
        }
    }

    private void ApplyShownRows()
    {
        _fontRow.Visibility = RowVisibility(TextStyleRowKind.Font);
        _weightRow.Visibility = RowVisibility(TextStyleRowKind.Traits);
        _sizeRow.Visibility = RowVisibility(TextStyleRowKind.Size);
        _alignRow.Visibility = RowVisibility(TextStyleRowKind.Alignment);
        _colorRow.Visibility = RowVisibility(TextStyleRowKind.Color);
        _backgroundRow.Visibility = RowVisibility(TextStyleRowKind.Background);
        _outlineRow.Visibility = RowVisibility(TextStyleRowKind.Outline);
        _shadowRow.Visibility = RowVisibility(TextStyleRowKind.Shadow);
        var geometry = RowVisibility(TextStyleRowKind.ShadowGeometry);
        _offsetXRow.Visibility = geometry;
        _offsetYRow.Visibility = geometry;
        _blurRow.Visibility = geometry;
    }

    private Visibility RowVisibility(TextStyleRowKind kind) =>
        _shownRows.HasFlag(kind) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Raised on discrete edits and on numeric live edits (<see cref="TextStyleChangedEventArgs"/>).</summary>
    public event EventHandler<TextStyleChangedEventArgs>? StyleChanged;

    /// <summary>Raised when a numeric field loses focus — the Inspector commits here.</summary>
    public event EventHandler<TextStyle>? StyleCommitRequested;

    /// <summary>
    /// The working copy. Set clones (host keeps ownership); get returns the live working copy —
    /// read it on commit, do not keep it. Never raises. Named <c>Value</c> because
    /// <c>FrameworkElement.Style</c> already owns <c>Style</c>.
    /// </summary>
    public TextStyle Value
    {
        get => _style;
        set
        {
            _style = CloneStyle(value ?? new TextStyle());
            RefreshFromStyle();
        }
    }

    /// <summary>Per-row reset targets. Never raises.</summary>
    public TextStyle Defaults
    {
        get => _defaults;
        set
        {
            _defaults = CloneStyle(value ?? new TextStyle());
            ApplySpecs();
            SyncAllResets();
        }
    }

    private void RaiseChanged(bool isLiveNumeric)
    {
        if (_syncing) return;
        StyleChanged?.Invoke(this, new TextStyleChangedEventArgs(_style, isLiveNumeric));
    }

    private void RaiseCommitted()
    {
        if (_syncing) return;
        StyleCommitRequested?.Invoke(this, _style);
    }

    /// <summary>
    /// Row reset through the field's own commit path (repaints CanReset), then one explicit change
    /// for the hosts. The live <c>ValueChanged</c> inside is suppressed: the Inspector commits
    /// discrete resets immediately, like its focus-loss commit after today's reset buttons.
    /// </summary>
    private void ResetNumeric(UnitNumberField field)
    {
        _syncing = true;
        try { field.ResetToDefault(); }
        finally { _syncing = false; }
        RaiseChanged(isLiveNumeric: false);
    }

    private void OnFontEdited()
    {
        if (_syncing) return;
        _style.FontName = ReadFontName();
        ShowResolvedFont(_style.FontName);
        SyncFontReset();
        RaiseChanged(isLiveNumeric: false);
    }

    private void SetAlignment(TextAlign alignment)
    {
        if (_syncing) return;
        _style.Alignment = alignment;
        SyncAlignmentButtons();
        _alignRow.CanReset = alignment != _defaults.Alignment;
        RaiseChanged(isLiveNumeric: false);
    }

    private void RefreshFromStyle()
    {
        _syncing = true;
        try
        {
            ApplyFontToWidgets();
            _boldBtn.IsChecked = _style.IsBold;
            _italicBtn.IsChecked = _style.IsItalic;
            _sizeField.Value = _style.FontSize;
            SyncAlignmentButtons();
            _colorWell.ColorValue = CloneRgba(_style.Color);
            _backgroundRow.IsOn = _style.Background.Enabled;
            _backgroundRow.ColorValue = CloneRgba(_style.Background.Color);
            _outlineRow.IsOn = _style.Border.Enabled;
            _outlineRow.ColorValue = CloneRgba(_style.Border.Color);
            _shadowSwitch.IsOn = _style.Shadow.Enabled;
            _offsetXField.Value = _style.Shadow.OffsetX;
            _offsetYField.Value = _style.Shadow.OffsetY;
            _blurField.Value = _style.Shadow.Blur;
            SyncAllResets();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void ApplyFontToWidgets()
    {
        SetFont(_style.FontName);
        ShowResolvedFont(_style.FontName);
        SyncFontReset();
    }

    private void SyncAllResets()
    {
        SyncFontReset();
        SyncWeightReset();
        SyncSizeReset();
        _alignRow.CanReset = _style.Alignment != _defaults.Alignment;
        SyncColorReset();
        SyncBackgroundReset();
        SyncOutlineReset();
        SyncShadowReset();
        SyncOffsetReset();
        SyncBlurReset();
    }

    private void SyncFontReset() =>
        _fontRow.CanReset = !string.Equals(ReadFontName(), _defaults.FontName, StringComparison.OrdinalIgnoreCase);

    private void SyncWeightReset() =>
        _weightRow.CanReset = _style.IsBold != _defaults.IsBold || _style.IsItalic != _defaults.IsItalic;

    private void SyncSizeReset() => _sizeRow.CanReset = _sizeField.DiffersFromDefault;
    private void SyncColorReset() => _colorRow.CanReset = !SameColor(_style.Color, _defaults.Color);

    private void SyncBackgroundReset()
    {
        _backgroundRow.IsOn = _style.Background.Enabled;
        _backgroundRow.ColorValue = CloneRgba(_style.Background.Color);
        _backgroundRow.CanReset = _style.Background.Enabled != _defaults.Background.Enabled
            || !SameColor(_style.Background.Color, _defaults.Background.Color);
    }

    private void SyncOutlineReset()
    {
        _outlineRow.IsOn = _style.Border.Enabled;
        _outlineRow.ColorValue = CloneRgba(_style.Border.Color);
        _outlineRow.CanReset = _style.Border.Enabled != _defaults.Border.Enabled
            || !SameColor(_style.Border.Color, _defaults.Border.Color);
    }

    private void SyncShadowReset() => _shadowRow.CanReset = _style.Shadow.Enabled != _defaults.Shadow.Enabled;

    private void SyncOffsetReset()
    {
        var differs = _offsetXField.DiffersFromDefault || _offsetYField.DiffersFromDefault;
        _offsetXRow.CanReset = differs;
        _offsetYRow.CanReset = differs;
    }

    private void SyncBlurReset() => _blurRow.CanReset = _blurField.DiffersFromDefault;

    private void SyncAlignmentButtons()
    {
        _alignLeftBtn.IsChecked = _style.Alignment == TextAlign.Left;
        _alignCenterBtn.IsChecked = _style.Alignment == TextAlign.Center;
        _alignRightBtn.IsChecked = _style.Alignment == TextAlign.Right;
    }

    private void ApplySpecs()
    {
        _sizeField.Spec = new UnitFieldSpec
        {
            Unit = "pt", Minimum = 12, Maximum = 300, Step = 1, Decimals = 0,
            UnitsPerPixel = 0.5, Default = _defaults.FontSize,
        };
        _offsetXField.Spec = OffsetSpec(_defaults.Shadow.OffsetX);
        _offsetYField.Spec = OffsetSpec(_defaults.Shadow.OffsetY);
        _blurField.Spec = new UnitFieldSpec
        {
            Unit = " px", Minimum = 0, Maximum = 80, Step = 0.5, Decimals = 1,
            UnitsPerPixel = 1, Default = _defaults.Shadow.Blur,
        };
    }

    private static UnitFieldSpec OffsetSpec(double def) => new()
    {
        Unit = " px", Minimum = -200, Maximum = 200, Step = 0.5, Decimals = 1,
        UnitsPerPixel = 1, Default = def,
    };

    private void EnsureFontItems()
    {
        if (_fontBox.Items.Count > 0) return;
        foreach (var name in Temno.Media.Text.SystemFontCatalog.FamilyNames())
            _fontBox.Items.Add(name);
        if (!_fontBox.Items.OfType<string>().Any(n =>
                string.Equals(n, _defaults.FontName, StringComparison.OrdinalIgnoreCase))
            && !string.IsNullOrWhiteSpace(_defaults.FontName))
            _fontBox.Items.Insert(0, _defaults.FontName);
    }

    private void SetFont(string? fontName)
    {
        var name = string.IsNullOrWhiteSpace(fontName) ? _defaults.FontName : fontName.Trim();
        for (var i = 0; i < _fontBox.Items.Count; i++)
        {
            if (_fontBox.Items[i] is string item
                && string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
            {
                _fontBox.SelectedIndex = i;
                return;
            }
        }
        _fontBox.Text = name;
    }

    private string ReadFontName()
    {
        if (_fontBox.SelectedItem is string selected && !string.IsNullOrWhiteSpace(selected))
            return selected.Trim();
        var typed = _fontBox.Text?.Trim();
        return string.IsNullOrEmpty(typed) ? _defaults.FontName : typed;
    }

    /// <summary>
    /// WP-183 (moved from the Inspector): the project keeps the Mac name for interchange; say
    /// which installed family Windows renders, next to the request.
    /// </summary>
    private void ShowResolvedFont(string? requested)
    {
        var name = (requested ?? "").Trim();
        var installed = _fontBox.Items.OfType<string>().ToList();
        string resolved;
        if (name.Length == 0 || name.StartsWith("Helvetica", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("SF Pro", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".SF", StringComparison.OrdinalIgnoreCase))
            resolved = "Segoe UI";
        else
        {
            var bare = name;
            foreach (var suffix in new[] { "-Bold", "-Semibold", "-Medium", "-Regular", "-Light", " Bold", " Semibold" })
                if (bare.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { bare = bare[..^suffix.Length].Trim(); break; }
            resolved = installed.FirstOrDefault(f => string.Equals(f, bare, StringComparison.OrdinalIgnoreCase))
                ?? installed.FirstOrDefault(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase))
                ?? "Segoe UI";
        }
        var same = string.Equals(resolved, name, StringComparison.OrdinalIgnoreCase);
        _fontResolved.Text = same ? "" : $"Renders as {resolved} on Windows";
        _fontResolved.Visibility = same ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Grid EqualButtons(params ToggleButton[] buttons)
    {
        var grid = new Grid { ColumnSpacing = AppTheme.Spacing.Xs };
        for (var i = 0; i < buttons.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons[i].HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(buttons[i], i);
            grid.Children.Add(buttons[i]);
        }
        return grid;
    }

    private static ToggleButton TraitButton(string content, string name, bool isBoldFace)
    {
        var button = new ToggleButton
        {
            Content = content,
            Padding = new Thickness(AppTheme.Spacing.SmMd, AppTheme.Spacing.Xs, AppTheme.Spacing.SmMd, AppTheme.Spacing.Xs),
            MinWidth = AppTheme.IconSize.LgXl,
            FontWeight = isBoldFace ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    private static TextStyle CloneStyle(TextStyle source) => new()
    {
        FontName = source.FontName,
        FontSize = source.FontSize,
        FontScale = source.FontScale,
        IsBold = source.IsBold,
        IsItalic = source.IsItalic,
        Color = CloneRgba(source.Color),
        Alignment = source.Alignment,
        Shadow = new TextStyle.ShadowStyle
        {
            Enabled = source.Shadow.Enabled,
            Color = CloneRgba(source.Shadow.Color),
            OffsetX = source.Shadow.OffsetX,
            OffsetY = source.Shadow.OffsetY,
            Blur = source.Shadow.Blur,
        },
        Background = new TextStyle.FillStyle
        {
            Enabled = source.Background.Enabled,
            Color = CloneRgba(source.Background.Color),
        },
        Border = new TextStyle.FillStyle
        {
            Enabled = source.Border.Enabled,
            Color = CloneRgba(source.Border.Color),
        },
        // Unknown Mac JSON rides along untouched in both directions (WP-126).
        ExtensionData = source.ExtensionData,
    };

    private static TextStyle.RGBA CloneRgba(TextStyle.RGBA? color)
    {
        color ??= new TextStyle.RGBA();
        return new TextStyle.RGBA { R = color.R, G = color.G, B = color.B, A = color.A };
    }

    private static bool SameColor(TextStyle.RGBA? a, TextStyle.RGBA? b) =>
        string.Equals(
            Temno.Core.Session.TextClipCommands.ToHexColor(a),
            Temno.Core.Session.TextClipCommands.ToHexColor(b),
            StringComparison.Ordinal);
}
