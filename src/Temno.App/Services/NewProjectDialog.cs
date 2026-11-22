using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Temno.App.Theme;

namespace Temno.App.Services;

/// <summary>What the New Project dialog returns. Defaults are 16:9 1920×1080 at 30 fps.</summary>
public sealed record NewProjectRequest(string Name, int Fps, int Width, int Height);

/// <summary>
/// WP-186: the New Project dialog shared by Home and File → New Project. Name-only creation still
/// works — press Create with the defaults — and the canvas and frame-rate presets are optional.
/// </summary>
public static class NewProjectDialog
{
    public static readonly (string Label, int Width, int Height)[] CanvasPresets =
    [
        ("16:9 · 1920 × 1080", 1920, 1080),
        ("9:16 · 1080 × 1920", 1080, 1920),
        ("1:1 · 1080 × 1080", 1080, 1080),
    ];

    public static readonly int[] FrameRates = [24, 25, 30, 60];

    public static async Task<NewProjectRequest?> ShowAsync(XamlRoot? xamlRoot, string defaultName = "Untitled Project")
    {
        var nameBox = new TextBox
        {
            Header = "Project name",
            Text = defaultName,
            PlaceholderText = defaultName,
            MaxLength = 120,
        };
        AutomationProperties.SetName(nameBox, "Project name");

        var canvasBox = new ComboBox { Header = "Canvas", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var preset in CanvasPresets)
            canvasBox.Items.Add(preset.Label);
        canvasBox.SelectedIndex = 0;
        AutomationProperties.SetName(canvasBox, "Canvas preset");
        ToolTipService.SetToolTip(canvasBox, "Canvas size; change it later in the Inspector");

        var fpsBox = new ComboBox { Header = "Frame rate", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var fps in FrameRates)
            fpsBox.Items.Add($"{fps} fps");
        fpsBox.SelectedIndex = Array.IndexOf(FrameRates, 30);
        AutomationProperties.SetName(fpsBox, "Frame rate");
        ToolTipService.SetToolTip(fpsBox, "Timeline frame rate; change it later in the Inspector");

        var presets = new Grid { ColumnSpacing = AppTheme.Spacing.Md };
        presets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        presets.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(fpsBox, 1);
        presets.Children.Add(canvasBox);
        presets.Children.Add(fpsBox);

        var panel = new StackPanel { Spacing = AppTheme.Spacing.MdLg, MinWidth = 380 };
        panel.Children.Add(nameBox);
        panel.Children.Add(presets);
        panel.Children.Add(new TextBlock
        {
            Text = "Canvas and frame rate can be changed later in the Inspector.",
            FontSize = AppTheme.FontSize.Sm,
            Foreground = AppTheme.Text.TertiaryBrush,
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = AppDialog.Create(xamlRoot);
        dialog.Title = "New Project";
        dialog.PrimaryButtonText = "Create";
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.Content = panel;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;

        var name = string.IsNullOrWhiteSpace(nameBox.Text) ? defaultName : nameBox.Text.Trim();
        var canvas = CanvasPresets[Math.Clamp(canvasBox.SelectedIndex, 0, CanvasPresets.Length - 1)];
        var rate = FrameRates[Math.Clamp(fpsBox.SelectedIndex, 0, FrameRates.Length - 1)];
        return new NewProjectRequest(name, rate, canvas.Width, canvas.Height);
    }
}
