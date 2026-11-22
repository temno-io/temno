namespace Temno.App.Controls.Shared;

/// <summary>Icon glyphs shared by the editor controls. Accent colours are dictionary-level in AppTheme.xaml (WP-180).</summary>
internal static class SharedControlTheme
{
    /// <summary>
    /// Segoe Fluent Icons codepoints, as escapes rather than literal private-use characters —
    /// those do not survive every editor and tool that touches this file, and a lost glyph fails
    /// silently as a blank control (it did exactly that on the first run of this package).
    /// </summary>
    public const string ChevronDown = "\uE70D";
    public const string ChevronRight = "\uE76C";
    public const string Refresh = "\uE72C";
}
