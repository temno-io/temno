using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Temno.App.Theme;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Temno.App.Controls.Shared;

/// <summary>
/// WP-136 motion primitives. Every tween duration is <see cref="AppTheme.Anim"/> —
/// <see cref="AppTheme.Anim.HoverSeconds"/> or <see cref="AppTheme.Anim.TransitionSeconds"/>.
/// WinUI <c>ThemeTransition</c> has no Duration; implicit <see cref="ScalarTransition"/> and
/// storyboards carry the two constants instead, and skip when the system animation switch is off.
/// </summary>
public static class Motion
{
    public static TimeSpan Hover { get; } = TimeSpan.FromSeconds(AppTheme.Anim.HoverSeconds);
    public static TimeSpan Transition { get; } = TimeSpan.FromSeconds(AppTheme.Anim.TransitionSeconds);

    public static bool ReduceMotion
    {
        get
        {
            try { return !new UISettings().AnimationsEnabled; }
            catch (Exception) { return false; }
        }
    }

    public static TimeSpan Effective(TimeSpan duration) => ReduceMotion ? TimeSpan.Zero : duration;

    public static ScalarTransition Scalar(TimeSpan duration) =>
        new() { Duration = Effective(duration) };

    /// <summary>
    /// Fade appearing <see cref="UIElement.Visibility"/> changes. Bind to a Duration resource so every
    /// overlay / tab tween uses <c>AppTheme.Anim.Hover</c> or <c>AppTheme.Anim.Transition</c>.
    /// An explicit storyboard animates the appearing frame. Hidden views collapse immediately
    /// so a pending exit callback cannot override a later request to show the same view.
    /// </summary>
    public static readonly DependencyProperty FadeProperty = DependencyProperty.RegisterAttached(
        "Fade",
        typeof(Duration),
        typeof(Motion),
        new PropertyMetadata(new Duration(TimeSpan.Zero), OnFadeChanged));

    public static Duration GetFade(UIElement e) => (Duration)e.GetValue(FadeProperty);
    public static void SetFade(UIElement e, Duration value) => e.SetValue(FadeProperty, value);

    private static void OnFadeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        var duration = e.NewValue is Duration dur && dur.HasTimeSpan ? dur.TimeSpan : TimeSpan.Zero;
        if (duration <= TimeSpan.Zero) return;

        var state = FadeTable.GetOrCreateValue(el);
        if (state.Hooked) return;
        state.Hooked = true;
        state.Duration = duration;
        state.Last = el.Visibility;
        el.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, OnFadeVisibility);
        if (el is FrameworkElement fe)
            fe.Unloaded += OnFadeUnloaded;
    }

    private static void OnFadeUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement el && FadeTable.TryGetValue(el, out var state))
            state.Generation++;
    }

    private static void OnFadeVisibility(DependencyObject d, DependencyProperty _)
    {
        if (d is not UIElement el || !FadeTable.TryGetValue(el, out var state))
            return;

        var now = el.Visibility;
        if (now == state.Last) return;

        state.Generation++;
        var gen = state.Generation;
        var duration = Effective(state.Duration);
        StopStoryboard(el);

        if (now == Visibility.Visible)
        {
            state.Last = Visibility.Visible;
            el.IsHitTestVisible = true;
            if (duration <= TimeSpan.Zero)
            {
                el.Opacity = 1;
                return;
            }

            el.Opacity = 0;
            FadeOpacity(el, 1, duration, gen, state, null);
            return;
        }

        // Visibility is the caller's requested state. Temporarily setting it back to Visible
        // swallows a subsequent show request in the same frame, then a stale completion hides
        // the panel. Hide immediately; appearing panels retain their opacity transition.
        state.Last = Visibility.Collapsed;
        el.Opacity = 1;
        el.IsHitTestVisible = true;
    }

    /// <summary>
    /// Explicit opacity storyboard shared by the <see cref="FadeProperty"/> engine and
    /// <see cref="FadeIn"/>. The storyboard (not an implicit transition) carries the
    /// <c>AppTheme.Anim</c> duration so freshly-shown visuals fade too. <paramref name="done"/>
    /// runs only if no newer fade superseded this one; pass null for fire-and-forget.
    /// </summary>
    private static void FadeOpacity(UIElement el, double to, TimeSpan duration, int gen, FadeState state, Action? done)
    {
        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(duration),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, el);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        var box = StoryTable.GetOrCreateValue(el);
        sb.Completed += (_, _) =>
        {
            if (ReferenceEquals(box.Storyboard, sb)) box.Storyboard = null;
            if (state.Generation != gen) return;
            done?.Invoke();
        };
        box.Storyboard = sb;
        sb.Begin();
    }

    /// <summary>
    /// Height + fade expand/collapse. <paramref name="host"/> should clip its child (a Border).
    /// </summary>
    public static void SetExpanded(FrameworkElement host, bool expanded, bool animate)
    {
        var duration = animate ? Effective(Transition) : TimeSpan.Zero;
        if (expanded) Expand(host, duration);
        else Collapse(host, duration);
    }

    public static void AttachHoverFill(Control control)
    {
        SuppressDefaultChrome(control);
        var brush = new SolidColorBrush(White(0));
        control.Background = brush;
        var over = false;
        void ApplyIdleOrHover()
        {
            var on = over || control.FocusState != FocusState.Unfocused;
            Tint(brush, on ? White(AppTheme.Opacity.Faint) : White(0), Hover);
        }

        control.PointerEntered += (_, _) =>
        {
            over = true;
            if (control.FocusState == FocusState.Unfocused)
                Tint(brush, White(AppTheme.Opacity.Faint), Hover);
        };
        control.PointerExited += (_, _) =>
        {
            over = false;
            if (control.FocusState == FocusState.Unfocused)
                Tint(brush, White(0), Hover);
        };
        control.PointerPressed += (_, _) => Tint(brush, White(AppTheme.Opacity.Muted), Hover);
        control.PointerReleased += (_, _) => ApplyIdleOrHover();
        control.PointerCaptureLost += (_, _) => ApplyIdleOrHover();
        control.GotFocus += (_, _) => Tint(brush, White(AppTheme.Opacity.Faint), Hover);
        control.LostFocus += (_, _) => ApplyIdleOrHover();
    }

    /// <summary>Idle / hover / focus border on a value field. Returned action is the commit flash.</summary>
    public static Action AttachFieldChrome(Control field, UIElement? hoverHost = null)
    {
        var idle = AppTheme.Border.Subtle;
        var hover = AppTheme.Border.Primary;
        var focus = Color.FromArgb(
            (byte)Math.Clamp((int)Math.Round(AppTheme.Opacity.Strong * 255), 0, 255),
            AppTheme.Accent.Brand.R,
            AppTheme.Accent.Brand.G,
            AppTheme.Accent.Brand.B);
        var brush = new SolidColorBrush(idle);
        field.BorderBrush = brush;
        field.Resources["TextControlBorderBrush"] = brush;
        field.Resources["TextControlBorderBrushPointerOver"] = brush;
        field.Resources["TextControlBorderBrushFocused"] = brush;
        field.Resources["TextControlBorderBrushDisabled"] = brush;

        var chrome = new FieldChrome();
        void Apply()
        {
            var color = chrome.Focused ? focus : chrome.PointerOver ? hover : idle;
            Tint(brush, color, Hover);
        }

        var host = hoverHost ?? field;
        host.PointerEntered += (_, _) => { chrome.PointerOver = true; if (!chrome.Focused) Apply(); };
        host.PointerExited += (_, _) => { chrome.PointerOver = false; if (!chrome.Focused) Apply(); };
        field.GotFocus += (_, _) => { chrome.Focused = true; Apply(); };
        field.LostFocus += (_, _) => { chrome.Focused = false; Apply(); };

        return () =>
        {
            if (ReduceMotion) return;
            var back = chrome.Focused ? focus : chrome.PointerOver ? hover : idle;
            Tint(brush, AppTheme.Accent.Timecode, Hover, () => Tint(brush, back, Hover));
        };
    }

    public static Action AttachCommitFlash(Border swatch)
    {
        var idle = AppTheme.Border.Primary;
        var brush = new SolidColorBrush(idle);
        swatch.BorderBrush = brush;
        return () =>
        {
            if (ReduceMotion) return;
            Tint(brush, AppTheme.Accent.Timecode, Hover, () => Tint(brush, idle, Hover));
        };
    }

    /// <summary>
    /// Dialog-open fade. <see cref="Services.AppDialog"/> pre-hides the content on
    /// <c>Loading</c> and calls this on <c>Opened</c>; opacity is independent animation,
    /// so no <c>EnableDependentAnimation</c> is needed. Instant under reduce-motion.
    /// Never call on the preview canvas, the playhead or caption render (DESIGN §5).
    /// </summary>
    public static void FadeIn(UIElement element)
    {
        var duration = Effective(Transition);
        if (duration <= TimeSpan.Zero)
        {
            element.Opacity = 1;
            return;
        }

        StopStoryboard(element);
        var sb = new Storyboard();
        var fade = new DoubleAnimation
        {
            To = 1,
            Duration = new Duration(duration),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        var box = StoryTable.GetOrCreateValue(element);
        sb.Completed += (_, _) =>
        {
            if (ReferenceEquals(box.Storyboard, sb)) box.Storyboard = null;
        };
        box.Storyboard = sb;
        sb.Begin();
    }

    public static void AttachSelectionLabel(TextBlock label, FrameworkElement focusScope)
    {
        var idle = AppTheme.Text.Secondary;
        var active = AppTheme.Text.Primary;
        var brush = new SolidColorBrush(idle);
        label.Foreground = brush;
        void SetSelected(bool on)
        {
            label.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            Tint(brush, on ? active : idle, Hover);
        }

        focusScope.AddHandler(UIElement.GettingFocusEvent, new TypedEventHandler<UIElement, GettingFocusEventArgs>((_, _) => SetSelected(true)), true);
        focusScope.AddHandler(UIElement.LosingFocusEvent, new TypedEventHandler<UIElement, LosingFocusEventArgs>((_, args) =>
        {
            if (args.NewFocusedElement is DependencyObject next && IsInside(focusScope, next))
                return;
            SetSelected(false);
        }), true);
    }

    private static bool IsInside(DependencyObject scope, DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, scope)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private static void Expand(FrameworkElement host, TimeSpan duration)
    {
        host.Visibility = Visibility.Visible;
        host.IsHitTestVisible = true;
        if (duration <= TimeSpan.Zero)
        {
            StopStoryboard(host);
            host.Height = double.NaN;
            host.Opacity = 1;
            return;
        }

        var width = MeasureWidth(host);
        host.Height = double.NaN;
        host.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        var target = Math.Max(0, host.DesiredSize.Height);
        host.Height = 0;
        host.Opacity = 0;
        AnimateClip(host, 0, target, 1, duration, () => host.Height = double.NaN);
    }

    private static void Collapse(FrameworkElement host, TimeSpan duration)
    {
        host.IsHitTestVisible = false;
        var from = host.ActualHeight;
        if (from <= 0.5 || duration <= TimeSpan.Zero)
        {
            StopStoryboard(host);
            host.Visibility = Visibility.Collapsed;
            host.Height = double.NaN;
            host.Opacity = 0;
            return;
        }

        AnimateClip(host, from, 0, 0, duration, () =>
        {
            host.Visibility = Visibility.Collapsed;
            host.Height = double.NaN;
        });
    }

    private static double MeasureWidth(FrameworkElement host)
    {
        if (host.ActualWidth > 1) return host.ActualWidth;
        if (host.Parent is FrameworkElement parent && parent.ActualWidth > 1)
            return parent.ActualWidth;
        return 400;
    }

    private static void AnimateClip(
        FrameworkElement host,
        double fromHeight,
        double toHeight,
        double toOpacity,
        TimeSpan duration,
        Action done)
    {
        StopStoryboard(host);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        var box = StoryTable.GetOrCreateValue(host);
        var sb = new Storyboard();
        var height = new DoubleAnimation
        {
            From = fromHeight,
            To = toHeight,
            Duration = new Duration(duration),
            EasingFunction = ease,
        };
        var opacity = new DoubleAnimation
        {
            To = toOpacity,
            Duration = new Duration(duration),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(height, host);
        Storyboard.SetTargetProperty(height, "Height");
        Storyboard.SetTarget(opacity, host);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        sb.Children.Add(height);
        sb.Children.Add(opacity);
        sb.Completed += (_, _) =>
        {
            if (ReferenceEquals(box.Storyboard, sb)) box.Storyboard = null;
            done();
        };
        box.Storyboard = sb;
        sb.Begin();
    }

    private static void Tint(SolidColorBrush brush, Color to, TimeSpan duration, Action? then = null)
    {
        StopStoryboard(brush);
        duration = Effective(duration);
        if (duration <= TimeSpan.Zero)
        {
            brush.Color = to;
            then?.Invoke();
            return;
        }

        var box = StoryTable.GetOrCreateValue(brush);
        var anim = new ColorAnimation
        {
            To = to,
            Duration = new Duration(duration),
            EnableDependentAnimation = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, brush);
        Storyboard.SetTargetProperty(anim, "Color");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Completed += (_, _) =>
        {
            if (ReferenceEquals(box.Storyboard, sb)) box.Storyboard = null;
            then?.Invoke();
        };
        box.Storyboard = sb;
        sb.Begin();
    }

    private static void SuppressDefaultChrome(Control control)
    {
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        control.Resources["ButtonBackground"] = clear;
        control.Resources["ButtonBackgroundPointerOver"] = clear;
        control.Resources["ButtonBackgroundPressed"] = clear;
        control.Resources["ButtonBackgroundDisabled"] = clear;
        control.Resources["ButtonBorderBrush"] = clear;
        control.Resources["ButtonBorderBrushPointerOver"] = clear;
        control.Resources["ButtonBorderBrushPressed"] = clear;
        control.Resources["ButtonBorderBrushDisabled"] = clear;
    }

    private static Color White(double opacity) =>
        Color.FromArgb((byte)Math.Clamp((int)Math.Round(opacity * 255), 0, 255), 255, 255, 255);

    private static void StopStoryboard(DependencyObject target)
    {
        if (!StoryTable.TryGetValue(target, out var box) || box.Storyboard is null) return;
        box.Storyboard.Stop();
        box.Storyboard = null;
    }

    private static readonly ConditionalWeakTable<UIElement, FadeState> FadeTable = new();
    private static readonly ConditionalWeakTable<DependencyObject, StoryBox> StoryTable = new();

    private sealed class FadeState
    {
        public bool Hooked;
        public int Generation;
        public TimeSpan Duration;
        public Visibility Last;
    }

    private sealed class StoryBox
    {
        public Storyboard? Storyboard;
    }

    private sealed class FieldChrome
    {
        public bool PointerOver;
        public bool Focused;
    }
}
