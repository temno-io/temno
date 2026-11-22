using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Temno.App.Theme;
using Temno.App.Controls.Shared;
using Temno.Core.Ui;
using Temno.App.ViewModels;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Mcp;
using Temno.Media.Text;
using Windows.Foundation;
using ClipModel = Temno.Core.Models.Clip;
using TextAlign = Temno.Core.Models.TextAlignment;
using TransformModel = Temno.Core.Models.Transform;

namespace Temno.App.Controls;

public sealed partial class InspectorControl : UserControl
{
    private static TextStyle? ReadTextStyle(Clip clip)
    {
        if (clip.TextStyle is not { } el) return null;
        try
        {
            return JsonSerializer.Deserialize<TextStyle>(el.GetRawText(), JsonOptions.ForProject);
        }
        catch
        {
            return null;
        }
    }

    private static string RgbaToHex(TextStyle.RGBA c)
    {
        int R(double v) => Math.Clamp((int)Math.Round(v * 255), 0, 255);
        return $"#{R(c.R):X2}{R(c.G):X2}{R(c.B):X2}";
    }

    private static TextStyle.RGBA? HexToRgba(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6) return null;
        if (!int.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)) return null;
        if (!int.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)) return null;
        if (!int.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b)) return null;
        return new TextStyle.RGBA { R = r / 255.0, G = g / 255.0, B = b / 255.0, A = 1 };
    }
    // --- Text (WP-84; style rows shared via WP-201) ---

    private void TextNumeric_ValueChanged(object? sender, double newValue)
    {
        // Commit on LostFocus to avoid spam while spinning.
    }

    // WP-201: shared TextStyleRows owns every style edit; this LostFocus path stays for the
    // Content box and the animate rows (they keep their own widgets).
    private void TextFields_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        CommitTextContentAndAnimation();
    }

    private void CommitTextContentAndAnimation()
    {
        CommitTextPatch(includeContent: true, includeAnimation: true);
    }

    private void CommitTextPatch(bool includeContent, bool includeAnimation)
    {
        if (_session is null || _clipIds.Count == 0) return;
        var textIds = _clipIds
            .Select(FindClip)
            .Where(c => c is not null && (c.MediaType == ClipType.text || !string.IsNullOrEmpty(c.TextContent)))
            .Select(c => c!.Id)
            .ToList();
        if (textIds.Count == 0) return;

        var animTag = TextAnimationBox.SelectedItem is ComboBoxItem { Tag: string t } ? t : "off";
        TextAnimation? animation = null;
        if (!string.Equals(animTag, "off", StringComparison.OrdinalIgnoreCase))
        {
            animation = new TextAnimation
            {
                Preset = animTag,
                PerWordFrames = Math.Max(1, (int)Math.Round(TextAnimPerWordBox.Value)),
            };
        }

        var patch = new TextPatch
        {
            HasContent = includeContent,
            Content = includeContent ? (TextContentBox.Text ?? "") : null,
            Style = ReadSharedTextStyle(),
            SetAnimation = includeAnimation,
            Animation = includeAnimation ? animation : null,
        };
        var result = TextClipCommands.UpdateText(
            _session, textIds, captionGroupId: null, patch, out _, out _);
        if (result.Ok)
            ClipEdited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// WP-201. Reads the working copy out of the shared style rows. The control keeps the row
    /// widgets and the <see cref="TextStyle"/> in sync on every keystroke, so the snapshot here
    /// always matches what the form shows — the old per-widget readers are gone.
    /// </summary>
    private TextStylePatch ReadSharedTextStyle()
    {
        var style = TextStyleRows.Value;
        return new TextStylePatch
        {
            FontName = style.FontName,
            FontSize = style.FontSize,
            IsBold = style.IsBold,
            IsItalic = style.IsItalic,
            Alignment = style.Alignment,
            Color = style.Color ?? new TextStyle.RGBA { R = 1, G = 1, B = 1, A = 1 },
            BackgroundEnabled = style.Background.Enabled,
            BackgroundColor = style.Background.Color ?? new TextStyle.RGBA { R = 0, G = 0, B = 0, A = 0.6 },
            BorderEnabled = style.Border.Enabled,
            BorderColor = style.Border.Color ?? new TextStyle.RGBA { R = 0, G = 0, B = 0, A = 1 },
            ShadowEnabled = style.Shadow.Enabled,
            ShadowOffsetX = style.Shadow.OffsetX,
            ShadowOffsetY = style.Shadow.OffsetY,
            ShadowBlur = style.Shadow.Blur,
        };
    }
}
