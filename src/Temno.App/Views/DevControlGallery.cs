using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Temno.App.Controls.Shared;
using Temno.App.Theme;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Ui;

namespace Temno.App.Views;

/// <summary>
/// Debug-only surface showing every WP-127 shared control in every state. Not reachable from the
/// product UI: launch with <c>TEMNO_DEV_GALLERY=1</c>.
/// </summary>
/// <remarks>
/// This is the click-through surface for the control layer. WP-115 shipped a Captions tab that
/// fail-fasted on show because nothing ever drove it; a gallery makes "does it actually run" a
/// one-command check instead of an act of faith.
/// </remarks>
public sealed class DevControlGallery : Window
{
    /// <summary>Set <c>TEMNO_DEV_GALLERY=1</c> to open this instead of the editor.</summary>
    public static bool Requested =>
        EnvCompat.Get("TEMNO_DEV_GALLERY") is "1" or "true";

    private static UnitFieldSpec MilliSpec => new()
    {
        Unit = "ms", Minimum = 0, Maximum = 2000, Step = 10, UnitsPerPixel = 5, Default = 500,
    };

    private static UnitFieldSpec PointSpec => new()
    {
        Unit = "pt", Minimum = 12, Maximum = 300, Step = 1, UnitsPerPixel = 1, Default = 48,
    };

    private static UnitFieldSpec PercentSpec => new()
    {
        Unit = "%", Minimum = 0, Maximum = 100, Step = 1, UnitsPerPixel = 1, Default = 100,
    };

    private static UnitFieldSpec PixelSpec => new()
    {
        Unit = "px", Minimum = 0, Maximum = 64, Step = 1, UnitsPerPixel = 1, Default = 0,
    };

    public DevControlGallery()
    {
        Title = "Temno — shared control gallery (WP-127)";

        var stack = new StackPanel
        {
            Spacing = AppTheme.Spacing.LgXl,
            Padding = new Thickness(AppTheme.Spacing.Xl),
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        stack.Children.Add(Caption(
            "Every shared control, every state. Drag a value field sideways to scrub; "
            + "hold Shift for fine. Rows show a reset only when they differ from their default."));

        stack.Children.Add(NumericSection());
        stack.Children.Add(ColorSectionOn());
        stack.Children.Add(ColorSectionOff());
        stack.Children.Add(EdgeCaseSection());

        Content = new ScrollViewer
        {
            Background = AppTheme.Background.BaseBrush,
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        FontSize = AppTheme.FontSize.SmMd,
        Foreground = AppTheme.Text.TertiaryBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>A value row wired the way a real panel wires one: reset visibility tracks the value.</summary>
    private static EditorRow ValueRow(string label, string help, UnitFieldSpec spec, double value)
    {
        var field = new UnitNumberField { Spec = spec, Value = value };
        var row = new EditorRow
        {
            Label = label,
            LabelHelp = help,
            RowContent = field,
            CanReset = field.DiffersFromDefault,
        };
        field.ValueChanged += (_, _) => row.CanReset = field.DiffersFromDefault;
        row.ResetRequested += (_, _) => field.ResetToDefault();
        return row;
    }

    private static SectionHeader NumericSection()
    {
        var rows = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
        rows.Children.Add(ValueRow("Close gaps", "Hold a caption across gaps shorter than this.", MilliSpec, 500));
        rows.Children.Add(ValueRow("Size", "Caption font size.", PointSpec, 48));
        rows.Children.Add(ValueRow("Width", "Horizontal scale.", PercentSpec, 100));
        rows.Children.Add(ValueRow("Blur", "Text blur radius.", PixelSpec, 0));
        rows.Children.Add(ValueRow("Padding", "Changed from its default — note the reset arrow.", PixelSpec, 12));

        return new SectionHeader
        {
            SectionId = "gallery.numeric",
            Title = "Value fields",
            SectionContent = rows,
        };
    }

    private static SectionHeader ColorSectionOn()
    {
        var rows = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
        rows.Children.Add(new EditorRow
        {
            Label = "Color",
            LabelHelp = "Opens a picker. Hex lives inside the flyout.",
            RowContent = new ColorWell
            {
                Purpose = "Text color",
                ColorValue = TextClipCommands.ParseHexColor("#F5EFE4"),
            },
        });
        rows.Children.Add(new ToggleColorRow
        {
            Label = "Outline",
            LabelHelp = "Switch plus well on one row.",
            IsOn = true,
            ColorValue = TextClipCommands.ParseHexColor("#000000"),
            CanReset = true,
        });
        rows.Children.Add(new ToggleColorRow
        {
            Label = "Highlight",
            IsOn = true,
            ColorValue = TextAnimation.DefaultHighlight,
        });

        return new SectionHeader
        {
            SectionId = "gallery.color",
            Title = "Color",
            HasToggle = true,
            IsOn = true,
            SectionContent = rows,
        };
    }

    private static SectionHeader ColorSectionOff()
    {
        var rows = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
        rows.Children.Add(new ToggleColorRow
        {
            Label = "Shadow",
            IsOn = false,
            ColorValue = TextClipCommands.ParseHexColor("#00000099"),
        });
        rows.Children.Add(ValueRow("Opacity", "Greyed with the section, still readable.", PercentSpec, 60));

        return new SectionHeader
        {
            SectionId = "gallery.sectionOff",
            Title = "Section toggled off",
            HasToggle = true,
            IsOn = false,
            SectionContent = rows,
        };
    }

    private static SectionHeader EdgeCaseSection()
    {
        var rows = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
        rows.Children.Add(ValueRow("At minimum", "Clamped at 0.", MilliSpec, 0));
        rows.Children.Add(ValueRow("At maximum", "Clamped at 2000.", MilliSpec, 2000));
        rows.Children.Add(ValueRow("No unit", "A bare number.", new UnitFieldSpec { Minimum = 0, Maximum = 50, Default = 4 }, 4));
        rows.Children.Add(new EditorRow
        {
            Label = "Long label that will be trimmed",
            LabelHelp = "The label column is fixed, so values stay aligned.",
            RowContent = new UnitNumberField { Spec = PointSpec, Value = 48 },
        });

        var collapsed = new SectionHeader
        {
            SectionId = "gallery.edge",
            Title = "Edge cases (start collapsed)",
            IsExpanded = false,
            SectionContent = rows,
        };
        return collapsed;
    }
}
