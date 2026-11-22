using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Audio;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Ui;
using Temno.Media.Audio;
using Temno.Media.Cache;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Colors = Microsoft.UI.Colors;

namespace Temno.App.Controls;

/// <summary>
/// WP-205 (Premiere pass): the marker colour contract shared by the ruler and the Index tab.
/// Premiere's marker UI is a fixed palette plus a custom colour; the stored value stays the
/// existing <c>Marker.Color</c> string (a <c>#RRGGBB</c> hex), so no Core or schema change.
/// </summary>
internal static class MarkerPalette
{
    /// <summary>Premiere's marker colour set. Green first: it is Premiere's default marker colour.</summary>
    public static readonly IReadOnlyList<(string Name, string Hex)> Swatches =
    [
        ("Green", "#3FA34D"),
        ("Red", "#E5484D"),
        ("Orange", "#ED8B33"),
        ("Yellow", "#E3B341"),
        ("Blue", "#4A90D9"),
        ("Violet", "#9B6DDE"),
        ("Pink", "#E484B7"),
        ("White", "#D8D8D8"),
    ];

    /// <summary>Premiere's default marker colour, used for new <c>M</c> markers.</summary>
    public const string DefaultHex = "#3FA34D";

    /// <summary>Half the width/height of the ruler marker head diamond, in px.</summary>
    public const double HeadHalfPx = 5.5;
    /// <summary>Width of the ruler marker stem, in px.</summary>
    public const double StemPx = 2;
    /// <summary>Width of the transparent ruler-marker hit target, in px.</summary>
    public const double HitPx = 16;

    public static SolidColorBrush BrushFor(string? color) => new(Resolve(color));

    public static Color Resolve(string? color)
    {
        if (!string.IsNullOrWhiteSpace(color) && color[0] == '#')
        {
            try
            {
                if (TextClipCommands.ParseHexColor(color) is { } rgba)
                    return RgbaToColor(rgba);
            }
            catch (FormatException)
            {
                // Fall through to named colours.
            }
        }

        // Legacy named colours (Mark Silence writes "gray") plus the palette names, so an
        // older project still renders instead of falling through to the default.
        return (color ?? "").Trim().ToLowerInvariant() switch
        {
            "red" => AppTheme.Status.Error,
            "green" => AppTheme.Status.Success,
            // WP-258: not TrackColor.Video, which became the ruler background (#191A1F) in WP-211.
            "blue" => RgbaToColor(TextClipCommands.ParseHexColor("#4A90D9")!),
            "gray" or "grey" or "white" => AppTheme.Text.Muted,
            "orange" => RgbaToColor(TextClipCommands.ParseHexColor("#ED8B33")!),
            "yellow" => RgbaToColor(TextClipCommands.ParseHexColor("#E3B341")!),
            "violet" => RgbaToColor(TextClipCommands.ParseHexColor("#9B6DDE")!),
            "pink" => RgbaToColor(TextClipCommands.ParseHexColor("#E484B7")!),
            _ => AppTheme.Accent.Timecode,
        };
    }

    /// <summary>Canonical opaque hex for palette matching, e.g. <c>#3FA34D</c>.</summary>
    public static string NormalizeHex(string? color)
    {
        var c = Resolve(color);
        return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    public static TextStyle.RGBA ToRgba(Color color) => new()
    {
        R = color.R / 255.0,
        G = color.G / 255.0,
        B = color.B / 255.0,
        A = color.A / 255.0,
    };

    private static Color RgbaToColor(TextStyle.RGBA rgba)
    {
        static byte Channel(double v) => (byte)Math.Clamp((int)Math.Round(v * 255), 0, 255);
        return Color.FromArgb(Channel(rgba.A), Channel(rgba.R), Channel(rgba.G), Channel(rgba.B));
    }

    /// <summary>
    /// Premiere's Edit Marker dialog: name, colour palette + custom colour, comments, and a
    /// read-only position. Returns null on Cancel or an empty name (changes nothing).
    /// The caller commits through <see cref="EditCommands.UpdateMarker"/> in one undo step.
    /// </summary>
    public static async Task<MarkerEdit?> ShowEditDialogAsync(
        XamlRoot? xamlRoot, string? name, string? color, string? note, int frame, int fps)
    {
        var currentHex = NormalizeHex(color);
        var selectedHex = currentHex;

        var nameBox = new TextBox
        {
            Header = "Name",
            Text = string.IsNullOrWhiteSpace(name) ? "Marker" : name,
            MaxLength = 120,
        };
        AutomationProperties.SetName(nameBox, "Marker name");
        var noteBox = new TextBox
        {
            Header = "Comments",
            Text = note ?? "",
            MaxLength = 500,
            AcceptsReturn = true,
        };
        AutomationProperties.SetName(noteBox, "Marker comments");

        var swatchRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = AppTheme.Spacing.Xxs,
        };
        var well = new ColorWell
        {
            Purpose = "Custom marker color",
            ColorValue = ToRgba(Resolve(color)),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var swatchButtons = new List<Button>();
        void PaintSwatches()
        {
            for (var i = 0; i < swatchButtons.Count; i++)
            {
                var hex = Swatches[i].Hex;
                var selected = string.Equals(hex, selectedHex, StringComparison.OrdinalIgnoreCase);
                swatchButtons[i].BorderBrush = selected
                    ? AppTheme.Text.PrimaryBrush
                    : AppTheme.Border.SubtleBrush;
                swatchButtons[i].BorderThickness = new Thickness(selected ? 2 : 1);
            }
        }
        foreach (var (swatchName, hex) in Swatches)
        {
            var captured = hex;
            var swatch = new Button
            {
                Width = 28,
                Height = 20,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(AppTheme.Radius.XsSm),
                Background = new SolidColorBrush(RgbaToColor(TextClipCommands.ParseHexColor(captured)!)),
                BorderBrush = AppTheme.Border.SubtleBrush,
                BorderThickness = new Thickness(1),
            };
            AutomationProperties.SetName(swatch, "Marker color " + swatchName);
            ToolTipService.SetToolTip(swatch, swatchName);
            swatch.Click += (_, _) =>
            {
                selectedHex = captured;
                well.ColorValue = ToRgba(RgbaToColor(TextClipCommands.ParseHexColor(captured)!));
                PaintSwatches();
            };
            swatchButtons.Add(swatch);
            swatchRow.Children.Add(swatch);
        }

        well.ColorChanged += (_, rgba) =>
        {
            selectedHex = TextClipCommands.ToHexColor(rgba);
            PaintSwatches();
        };
        PaintSwatches();

        var colorLabel = new TextBlock
        {
            Text = "Color",
            FontSize = AppTheme.FontSize.Sm,
            Foreground = AppTheme.Text.SecondaryBrush,
        };
        var colorRow = new StackPanel { Spacing = AppTheme.Spacing.Xxs };
        colorRow.Children.Add(swatchRow);
        colorRow.Children.Add(well);

        var position = new TextBlock
        {
            Text = $"Position {FormatMarkerTimecode(frame, fps)} · frame {Math.Max(0, frame)}",
            FontSize = AppTheme.FontSize.Sm,
            Foreground = AppTheme.Text.SecondaryBrush,
        };
        AutomationProperties.SetName(position, "Marker position");

        var content = new StackPanel { Spacing = AppTheme.Spacing.SmMd };
        content.Children.Add(nameBox);
        content.Children.Add(colorLabel);
        content.Children.Add(colorRow);
        content.Children.Add(noteBox);
        content.Children.Add(position);

        var dialog = AppDialog.Create(xamlRoot);
        dialog.Title = "Edit Marker";
        dialog.Content = content;
        dialog.PrimaryButtonText = "OK";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        var finalName = nameBox.Text.Trim();
        if (finalName.Length == 0)
            return null;
        return new MarkerEdit(finalName, selectedHex, noteBox.Text);
    }

    private static string FormatMarkerTimecode(int frame, int fps)
    {
        if (fps <= 0) fps = 30;
        frame = Math.Max(0, frame);
        var ff = frame % fps;
        var totalSeconds = frame / fps;
        var ss = totalSeconds % 60;
        var mm = (totalSeconds / 60) % 60;
        var hh = totalSeconds / 3600;
        return $"{hh:D2}:{mm:D2}:{ss:D2}:{ff:D2}";
    }
}
