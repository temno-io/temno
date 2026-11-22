using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Temno.App.Theme;

/// <summary>
/// Design tokens ported from Mac <c>UI/AppTheme.swift</c> and <c>Utilities/Constants.swift</c> Layout.
/// Prefer these over magic numbers in views.
/// </summary>
public static class AppTheme
{
    public static class Background
    {
        public static Color Base { get; } = Color.FromArgb(255, 11, 11, 13);
        public static Color Surface { get; } = Color.FromArgb(255, 16, 17, 20);
        public static Color Raised { get; } = Color.FromArgb(255, 25, 26, 31);
        public static Color Prominent { get; } = Color.FromArgb(255, 35, 37, 44);
        public static Color Placeholder => Raised;
        public static Color PreviewCanvas { get; } = Colors.Black;
        public static Color OverlayScrim { get; } = Color.FromArgb(0xB8, 0x0B, 0x0B, 0x0D);

        public static SolidColorBrush BaseBrush { get; } = Brush(Base);
        public static SolidColorBrush SurfaceBrush { get; } = Brush(Surface);
        public static SolidColorBrush RaisedBrush { get; } = Brush(Raised);
        public static SolidColorBrush ProminentBrush { get; } = Brush(Prominent);
        public static SolidColorBrush PreviewCanvasBrush { get; } = Brush(PreviewCanvas);
        public static SolidColorBrush OverlayScrimBrush { get; } = Brush(OverlayScrim);
    }

    public static class Border
    {
        public static Color Primary { get; } = Color.FromArgb(255, 43, 45, 53);   // 0.16
        public static Color Subtle { get; } = Color.FromArgb(255, 30, 32, 39);    // 0.12
        public static Color Divider { get; } = Color.FromArgb(255, 43, 45, 53);  // 0.44
        public static Color TimelineClip { get; } = Colors.Black;

        public static SolidColorBrush PrimaryBrush { get; } = Brush(Primary);
        public static SolidColorBrush SubtleBrush { get; } = Brush(Subtle);
        public static SolidColorBrush DividerBrush { get; } = Brush(Divider);
    }

    public static class BorderWidth
    {
        public const double Hairline = 0.5;
        public const double Thin = 1;
        public const double Medium = 1.5;
        public const double Thick = 2;
    }

    public static class Accent
    {
        public static Color Brand { get; } = Color.FromArgb(255, 99, 102, 241);
        public static Color Selected { get; } = Color.FromArgb(255, 32, 32, 57);
        public static Color Readable { get; } = Color.FromArgb(255, 181, 183, 255);
        public static SolidColorBrush BrandBrush { get; } = Brush(Brand);
        public static SolidColorBrush SelectedBrush { get; } = Brush(Selected);
        public static SolidColorBrush ReadableBrush { get; } = Brush(Readable);
        /// <summary>Indigo used for timecode and update accents.</summary>
        public static Color Timecode { get; } = Color.FromArgb(255, 99, 102, 241);

        /// <summary>Warm off-white primary accent.</summary>
        public static Color Primary { get; } = Color.FromArgb(255, 242, 237, 228); // 0.961, 0.937, 0.894

        /// <summary>Onboarding / spotlight red.</summary>
        public static Color Spotlight { get; } = Color.FromArgb(255, 255, 69, 69); // 1.0, 0.27, 0.27

        public static SolidColorBrush TimecodeBrush { get; } = Brush(Timecode);
        public static SolidColorBrush PrimaryBrush { get; } = Brush(Primary);
        public static SolidColorBrush SpotlightBrush { get; } = Brush(Spotlight);
    }

    /// <summary>Mac AppTheme.Update — title-bar chip and Home install card.</summary>
    public static class Update
    {
        public static Color AccentColor => AppTheme.Accent.Brand;
        public static SolidColorBrush AccentBrush => AppTheme.Accent.BrandBrush;
        public static Color ChipFill { get; } = Color.FromArgb(38, 99, 102, 241);
        public static SolidColorBrush ChipFillBrush { get; } = Brush(ChipFill);
    }

    public static class Status
    {
        public static Color Error { get; } = Color.FromArgb(255, 0xE5, 0x4F, 0x4F);
        public static Color Success { get; } = Color.FromArgb(255, 0x4F, 0xB8, 0x5F);
        public static Color Warning { get; } = Color.FromArgb(255, 0xF2, 0xC9, 0x4C);

        public static SolidColorBrush ErrorBrush { get; } = Brush(Error);
        public static SolidColorBrush SuccessBrush { get; } = Brush(Success);
        public static SolidColorBrush WarningBrush { get; } = Brush(Warning);
    }

    /// <summary>Mac AppTheme.aiGradient / aiGradientDark monochrome tokens.</summary>
    public static class Ai
    {
        public static Color GradientStop0 { get; } = Color.FromArgb(255, 255, 255, 255);
        public static Color GradientStop1 { get; } = Color.FromArgb(255, 199, 199, 199);
        public static Color GradientStop2 { get; } = Color.FromArgb(255, 153, 153, 153);
        public static Color GradientStop3 { get; } = Color.FromArgb(255, 255, 255, 255);

        public static LinearGradientBrush GradientBrush { get; } = new()
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = GradientStop0, Offset = 0 },
                new GradientStop { Color = GradientStop1, Offset = 0.45 },
                new GradientStop { Color = GradientStop2, Offset = 0.55 },
                new GradientStop { Color = GradientStop3, Offset = 1 },
            },
        };
    }

    public static class Text
    {
        public static Color Primary { get; } = Colors.White;
        public static Color Secondary { get; } = Color.FromArgb(255, 175, 178, 190); // 0.80
        public static Color Tertiary { get; } = Color.FromArgb(255, 150, 155, 168);  // 0.62
        public static Color Muted { get; } = Color.FromArgb(255, 85, 89, 101);      // 0.34

        public static SolidColorBrush PrimaryBrush { get; } = Brush(Primary);
        public static SolidColorBrush SecondaryBrush { get; } = Brush(Secondary);
        public static SolidColorBrush TertiaryBrush { get; } = Brush(Tertiary);
        public static SolidColorBrush MutedBrush { get; } = Brush(Muted);
    }

    public static class Opacity
    {
        public const double Opaque = 1;
        public const double Subtle = 0.04;
        public const double Hint = 0.06;
        public const double Faint = 0.08;
        public const double Soft = 0.10;
        public const double Muted = 0.15;
        public const double Moderate = 0.25;
        public const double Medium = 0.35;
        public const double Strong = 0.55;
        public const double High = 0.70;
        public const double Prominent = 0.80;
    }

    public static class TrackColor
    {
        public static Color Video { get; } = Color.FromArgb(255, 25, 26, 31);
        public static Color Audio { get; } = Color.FromArgb(255, 25, 26, 31);
        public static Color Image { get; } = Color.FromArgb(255, 25, 26, 31);
        public static Color Text { get; } = Color.FromArgb(255, 25, 26, 31);
        public static Color Lottie { get; } = Color.FromArgb(255, 25, 26, 31);
        public static Color Sequence { get; } = Color.FromArgb(255, 25, 26, 31);
        public static Color Multicam { get; } = Color.FromArgb(255, 25, 26, 31);

        public static SolidColorBrush VideoBrush { get; } = Brush(Video);
        public static SolidColorBrush AudioBrush { get; } = Brush(Audio);
        public static SolidColorBrush ImageBrush { get; } = Brush(Image);
        public static SolidColorBrush TextBrush { get; } = Brush(Text);
        public static SolidColorBrush LottieBrush { get; } = Brush(Lottie);
        public static SolidColorBrush SequenceBrush { get; } = Brush(Sequence);
    }

    public static class Radius
    {
        public const double Xs = 3;
        public const double XsSm = 4;
        public const double Sm = 6;
        public const double Md = 10;
        public const double MdLg = 12;
        public const double Lg = 14;
        public const double Xl = 20;

        public static double Concentric(double outer, double padding) => Math.Max(outer - padding, 0);
    }

    public static class Waveform
    {
        // Figma R3: linked source audio, node 73:1498.
        public const double BarPitch = 3;
        public const double StrokeWidth = 1.2;
    }

    public static class Spacing
    {
        public const double None = 0;
        public const double Hairline = 1;
        public const double Xxs = 2;
        public const double Xs = 4;
        public const double Sm = 6;
        public const double SmMd = 8;
        public const double Md = 10;
        public const double MdLg = 12;
        public const double Lg = 14;
        public const double LgXl = 16;
        public const double Xl = 20;
        public const double XlXxl = 24;
        public const double Xxl = 28;
    }

    public static class FontFamily
    {
        public static Microsoft.UI.Xaml.Media.FontFamily Ui { get; } = new("ms-appx:///Assets/Fonts/Inter.ttf#Inter");
        public static Microsoft.UI.Xaml.Media.FontFamily Mono { get; } = new("ms-appx:///Assets/Fonts/RobotoMono.ttf#Roboto Mono");
    }

    public static class FontSize
    {
        public const double EditorLabel = 12;
        public const double EditorValue = 13;
        public const double Micro = 8;
        public const double Xxs = 9;
        public const double Xs = 11;
        public const double Sm = 11;
        public const double SmMd = 12;
        public const double Md = 13;
        public const double MdLg = 14;
        public const double Lg = 15;
        public const double Xl = 18;
        public const double Title1 = 22;
        public const double Title2 = 28;
        public const double Display = 36;
    }

    public static class Padding
    {
        public static Microsoft.UI.Xaml.Thickness R3Page => new(64, 48, 64, 24);
        public static Microsoft.UI.Xaml.Thickness R3PageCompact => new(24, 24, 24, 16);
        public static Microsoft.UI.Xaml.Thickness R3Panel => new(16);
        public static Microsoft.UI.Xaml.Thickness None => new(0);
        public static Microsoft.UI.Xaml.Thickness Xxs => new(2);
        public static Microsoft.UI.Xaml.Thickness Xs => new(4);
        public static Microsoft.UI.Xaml.Thickness Sm => new(6);
        public static Microsoft.UI.Xaml.Thickness Md => new(10);
        public static Microsoft.UI.Xaml.Thickness Lg => new(14);
        public static Microsoft.UI.Xaml.Thickness Xl => new(20);
        public static Microsoft.UI.Xaml.Thickness Page => new(24, 20, 24, 20);
        public static Microsoft.UI.Xaml.Thickness Control => new(10, 4, 10, 4);
        public static Microsoft.UI.Xaml.Thickness SmXs => new(6, 4, 6, 4);
        public static Microsoft.UI.Xaml.Thickness HXs => new(4, 0, 4, 0);
        public static Microsoft.UI.Xaml.Thickness HSmMd => new(8, 0, 8, 0);
        public static Microsoft.UI.Xaml.Thickness HMd => new(10, 0, 10, 0);
        public static Microsoft.UI.Xaml.Thickness HHairline => new(1, 0, 1, 0);
        public static Microsoft.UI.Xaml.Thickness MdXxs => new(10, 2, 10, 2);
        public static Microsoft.UI.Xaml.Thickness SmMdXxs => new(8, 2, 8, 2);
        public static Microsoft.UI.Xaml.Thickness SmMdSm => new(8, 6, 8, 6);
        public static Microsoft.UI.Xaml.Thickness MdLgXs => new(12, 4, 12, 4);
        public static Microsoft.UI.Xaml.Thickness XsSmMd => new(4, 8, 4, 8);
        public static Microsoft.UI.Xaml.Thickness SmXxs => new(6, 2, 6, 2);
        public static Microsoft.UI.Xaml.Thickness MdLgMd => new(12, 10, 12, 10);
        public static Microsoft.UI.Xaml.Thickness InsetCaption => new(3, 8, 3, 8);
        public static Microsoft.UI.Xaml.Thickness ChipDense => new(8, 6, 8, 4);
        public static Microsoft.UI.Xaml.Thickness SmXxsRightXs => new(6, 2, 4, 2);
        public static Microsoft.UI.Xaml.Thickness LeftSmMd => new(8, 0, 0, 0);
        public static Microsoft.UI.Xaml.Thickness RightXs => new(0, 0, 4, 0);
        public static Microsoft.UI.Xaml.Thickness RightXxs => new(0, 0, 2, 0);
        public static Microsoft.UI.Xaml.Thickness RightBottomXxs => new(0, 0, 2, 2);
        public static Microsoft.UI.Xaml.Thickness RightSmBottomXs => new(0, 0, 6, 4);
        public static Microsoft.UI.Xaml.Thickness TopXxs => new(0, 2, 0, 0);
        public static Microsoft.UI.Xaml.Thickness TopSmMd => new(0, 8, 0, 0);
        public static Microsoft.UI.Xaml.Thickness BottomSmMd => new(0, 0, 0, 8);
        public static Microsoft.UI.Xaml.Thickness LeftXxsBottomXxs => new(2, 0, 0, 2);
        public static Microsoft.UI.Xaml.Thickness HXxsBottomXxs => new(2, 0, 2, 2);
        public static Microsoft.UI.Xaml.Thickness SmMdXxsTop => new(8, 2, 8, 0);
        public static Microsoft.UI.Xaml.Thickness TransportTab => new(6, 0, 10, 0);
        // WP-197: exact-geometry tokens for the raw values the ux-remaining audit counted.
        public static Microsoft.UI.Xaml.Thickness SmMd => new(8);
        public static Microsoft.UI.Xaml.Thickness MdSmMd => new(10, 8, 10, 8);
        public static Microsoft.UI.Xaml.Thickness XsXxs => new(4, 2, 4, 2);
        public static Microsoft.UI.Xaml.Thickness LeftLgXl => new(16, 0, 0, 0);
        public static Microsoft.UI.Xaml.Thickness RightLgXl => new(0, 0, 16, 0);
        public static Microsoft.UI.Xaml.Thickness TopSmMdBottomLgXl => new(0, 8, 0, 16);
        public static Microsoft.UI.Xaml.Thickness HXxs => new(2, 0, 2, 0);
        public static Microsoft.UI.Xaml.Thickness LeftXxs => new(2, 0, 0, 0);
    }

    public static class CornerRadius
    {
        public static Microsoft.UI.Xaml.CornerRadius Hairline => new(1);
        public static Microsoft.UI.Xaml.CornerRadius Xxs => new(2);
        public static Microsoft.UI.Xaml.CornerRadius Xs => new(3);
        public static Microsoft.UI.Xaml.CornerRadius XsSm => new(4);
        public static Microsoft.UI.Xaml.CornerRadius Sm => new(6);
        public static Microsoft.UI.Xaml.CornerRadius Md => new(10);
        public static Microsoft.UI.Xaml.CornerRadius MdLg => new(12);
        public static Microsoft.UI.Xaml.CornerRadius Lg => new(14);
        public static Microsoft.UI.Xaml.CornerRadius Capsule => new(13);
    }

    public static class IconSize
    {
        public const double Xxs = 12;
        public const double Xs = 14;
        public const double Sm = 18;
        public const double SmMd = 20;
        public const double Md = 22;
        public const double MdLg = 24;
        public const double Lg = 26;
        public const double LgXl = 28;
        public const double Xl = 30;
        public const double Display = 36;
        public const double Hero = 40;
    }

    public static class Window
    {
        public const double HomeDefaultWidth = 1200;
        public const double HomeDefaultHeight = 880;
        public const double HomeMinWidth = 760;
        public const double HomeMinHeight = 480;
        public const double ProjectMinWidth = 960;
        public const double ProjectMinHeight = 600;
    }

    public static class Anim
    {
        public const double HoverSeconds = 0.15;
        public const double TransitionSeconds = 0.2;
    }

    public static class ComponentSize
    {
        public const double WordmarkWidth = 220;
        public const double ControlHeight = 32;
        /// <summary>The gap a menu leaves between its bottom edge and the window's, so it never touches the edge.</summary>
        public const double MenuWindowMargin = 24;
        /// <summary>The tallest a long menu grows (the Settings default model menu); it scrolls past this.</summary>
        public const double MenuMaxHeight = 400;
        public const double HeaderHeight = 40;
        public const double SourceTabsHeight = 36;
        public const double MenuRevealWidth = 193;
        public const double EditorLabelColumnWidth = 90;
        public const double ResetSlotWidth = 18;
        public const double KeyframeButtonSize = 24;
        public const double HomeCompactBreakpoint = 1100;
        public const double ProjectCardWidth = 372;
        public const double ProjectCardHeight = 320;
        public const double TimelineClipBorderMinWidth = 8;
        public const double TimelineClipDetailMinWidth = 32;
        public const double TimelineClipLabelMinWidth = 56;
        public const double TimelineDotSize = 5;
        public const double ExportLabelWidth = 120;
        public const double ExportContentMaxWidth = 672;
        public const double ExportQueueMaxHeight = 160;
        public const double DialogMinContentWidth = 360;
        public const double DialogMinContentHeight = 280;
        public const double DialogHorizontalInset = 96;
        public const double DialogVerticalInset = 220;
        public const double SettingsVerticalInset = 280;
        public const double SettingsContentMaxHeight = 520;
        public const double TimelineTabsCompactWidth = 128;
        public const double TimelineTabsWidth = 200;
        public const double TimelineOverflowBreakpoint = 800;
    }

    /// <summary>Agent panel metrics.</summary>
    public static class AgentPanel
    {
        /// <summary>The hosted pickers' list never shrinks below about five rows, however short the panel.</summary>
        public const double DropDownMinHeight = 200;
    }

    /// <summary>Mac AppTheme.MediaPanel — icon rail metrics.</summary>
    public static class GenerationPanel
    {
        public const double PromptHeight = 48;
        public const double ToolbarHeight = 24;
        public const double MaxHeight = 320;
        public const double PopoverWidth = 304;
        public const double PopoverHeight = 360;
        public const double HeaderBreakpoint = 420;
        public const double ReferenceSize = 40;
        public const double ReferencePreviewSize = 240;
        public const double SubmitSize = 30;
        public const double CompactLabelWidth = 156;
    }

    public static class MediaPanel
    {
        /// <summary>Mac: IconSize.lg + Spacing.sm * 2 = 26 + 12 = 38.</summary>
        public const double TabRailWidth = 38;
        /// <summary>Mac selected leading capsule width (BorderWidth.thick).</summary>
        public const double RailIndicatorWidth = 2;
        /// <summary>Mac selected leading capsule height (IconSize.sm).</summary>
        public const double RailIndicatorHeight = 18;
        public const double RailButtonSize = 32;
    }

    /// <summary>
    /// WP-252. Transition blocks on the timeline: a dark slab (<see cref="Background.Prominent"/>)
    /// that reads over any thumbnail, a curve and grips in <see cref="Accent.Readable"/>, and the
    /// selection stroke in <see cref="Accent.Brand"/> like a selected clip. Audio blocks are
    /// thinner so the waveform shows through. Tabs are the corner handles that start a block.
    /// </summary>
    public static class Transition
    {
        public const double FillAlpha = 0.84;
        public const double AudioFillAlpha = 0.6;
        public const double StrokeAlpha = 0.45;
        public const double GripIdleAlpha = 0.55;

        public static Color Fill { get; } = WithAlpha(Background.Prominent, FillAlpha);
        public static Color AudioFill { get; } = WithAlpha(Background.Prominent, AudioFillAlpha);
        public static Color Stroke { get; } = WithAlpha(Accent.Readable, StrokeAlpha);
        public static Color Curve { get; } = WithAlpha(Accent.Readable, 0.95);
        public static Color GripIdle { get; } = WithAlpha(Accent.Readable, GripIdleAlpha);
        public static Color LabelChip { get; } = WithAlpha(Background.Base, 0.6);
        public static Color TabFill { get; } = WithAlpha(Accent.Primary, 0.95);
        public static Color TabStroke { get; } = WithAlpha(Background.Base, 0.7);

        public static SolidColorBrush FillBrush { get; } = Brush(Fill);
        public static SolidColorBrush AudioFillBrush { get; } = Brush(AudioFill);
        public static SolidColorBrush StrokeBrush { get; } = Brush(Stroke);
        public static SolidColorBrush CurveBrush { get; } = Brush(Curve);
        public static SolidColorBrush GripIdleBrush { get; } = Brush(GripIdle);
        public static SolidColorBrush LabelChipBrush { get; } = Brush(LabelChip);
        public static SolidColorBrush TabFillBrush { get; } = Brush(TabFill);
        public static SolidColorBrush TabStrokeBrush { get; } = Brush(TabStroke);

        private static Color WithAlpha(Color color, double alpha)
            => Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B);
    }

    private static SolidColorBrush Brush(Color color) => new(color);
}

/// <summary>Layout metrics from Mac <c>Utilities/Constants.swift</c> enum Layout.</summary>
public static class Layout
{
    public const double MediaPanelDefault = 344;
    public const double MediaPanelMin = 280;

    public const double InspectorDefault = 344;
    public const double InspectorMin = 280;

    public const double AgentPanelMin = 240;
    public const double AgentPanelMax = 640;
    public const double ChatColumnMax = 640;

    public const double PanelHeaderHeight = 36;
    public const double ToolbarHeight = 40;
    public const double PanelGap = 5;

    public const double TimelineMinHeight = 100;
    public const double TimelineMaxHeight = 700;
    public const double TrackHeight = 50;
    public const double RulerHeight = 36;
    public const double TrackHeaderWidth = 152;
    public const double DropZoneHeight = 60;
    public const double InsertThreshold = 10;
    public const double DragThreshold = 3;

    public const double PreviewMinWidth = 400;
    public const double PreviewMinHeight = 320;
}
