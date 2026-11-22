using System;

namespace Temno.App.Controls.Shared;

/// <summary>
/// WP-201. Which rows of <see cref="TextStyleRows"/> a host shows. The Inspector shows
/// <see cref="All"/>; the Captions Style section shows only the five rows it owns
/// (Font, Traits, Size, Alignment, Color) while its Outline/Background/Shadow sections —
/// which carry extras like color and opacity — stay untouched.
/// </summary>
[Flags]
public enum TextStyleRowKind
{
    None = 0,
    Font = 1 << 0,
    Traits = 1 << 1,
    Size = 1 << 2,
    Alignment = 1 << 3,
    Color = 1 << 4,
    Background = 1 << 5,
    Outline = 1 << 6,
    Shadow = 1 << 7,
    ShadowGeometry = 1 << 8,
    All = Font | Traits | Size | Alignment | Color | Background | Outline | Shadow | ShadowGeometry,
}
