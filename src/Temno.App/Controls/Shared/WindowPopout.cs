using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Temno.App.Theme;
using Temno.Core.Ui;
using Windows.Foundation;

namespace Temno.App.Controls.Shared;

/// <summary>
/// Keeps menus, flyout popovers, context menus, and ComboBox drop-downs inside the Temno window
/// and anchored on the expected side of their trigger, even when the window is scaled smaller.
/// </summary>
public static class WindowPopout
{
    private const double DefaultComboBoxMaxDropDownHeight = 504;

    private static readonly ConditionalWeakTable<MenuFlyout, HookMarker> ContextHooks = new();
    private static readonly ConditionalWeakTable<MenuFlyoutPresenter, HookMarker> PresenterHooks = new();
    private static readonly ConditionalWeakTable<ComboBox, ComboState> ComboStates = new();

    private static double TopClearance => AppTheme.ComponentSize.HeaderHeight + AppTheme.Spacing.Xs;

    public static readonly DependencyProperty ConstrainContextMenuProperty = DependencyProperty.RegisterAttached(
        "ConstrainContextMenu",
        typeof(bool),
        typeof(WindowPopout),
        new PropertyMetadata(false, OnConstrainContextMenuChanged));

    public static bool GetConstrainContextMenu(MenuFlyout menu) => (bool)menu.GetValue(ConstrainContextMenuProperty);
    public static void SetConstrainContextMenu(MenuFlyout menu, bool value) => menu.SetValue(ConstrainContextMenuProperty, value);

    public static readonly DependencyProperty KeepInWindowProperty = DependencyProperty.RegisterAttached(
        "KeepInWindow",
        typeof(bool),
        typeof(WindowPopout),
        new PropertyMetadata(false, OnKeepInWindowChanged));

    public static bool GetKeepInWindow(DependencyObject obj) => (bool)obj.GetValue(KeepInWindowProperty);
    public static void SetKeepInWindow(DependencyObject obj, bool value) => obj.SetValue(KeepInWindowProperty, value);

    /// <summary>Configures and opens a button-anchored <see cref="MenuFlyout"/> inside the window.</summary>
    public static void ShowMenuAt(
        MenuFlyout menu,
        FrameworkElement anchor,
        PopoutSide preferredSide = PopoutSide.Bottom,
        PopoutAlignment alignment = PopoutAlignment.Right,
        double? maxCap = null)
    {
        ApplyMenuLayout(menu, anchor, preferredSide, alignment, maxCap);
        menu.ShowAt(anchor);
    }

    /// <summary>Configures and opens a pointer-anchored context <see cref="MenuFlyout"/> inside the window.</summary>
    public static void ShowContextMenuAt(
        MenuFlyout menu,
        FrameworkElement anchor,
        Point at,
        double? maxCap = null)
    {
        ApplyContextMenuLayout(menu, anchor.XamlRoot, maxCap);
        menu.ShowAt(anchor, at);
    }

    /// <summary>Wires an attached <see cref="MenuFlyout"/> (Button.Flyout / DropDownButton.Flyout) to stay in the window on every open.</summary>
    public static void AttachMenu(
        MenuFlyout menu,
        FrameworkElement anchor,
        PopoutSide preferredSide = PopoutSide.Bottom,
        PopoutAlignment alignment = PopoutAlignment.Right,
        double? maxCap = null)
    {
        menu.ShouldConstrainToRootBounds = true;
        menu.Placement = ToFlyoutPlacement(preferredSide, alignment);
        menu.Opening += (_, _) => ApplyMenuLayout(menu, anchor, preferredSide, alignment, maxCap);
    }

    /// <summary>Applies placement and window-bounded presenter height to a <see cref="MenuFlyout"/> before it opens.</summary>
    public static void ApplyMenuLayout(
        MenuFlyout menu,
        FrameworkElement anchor,
        PopoutSide preferredSide = PopoutSide.Bottom,
        PopoutAlignment alignment = PopoutAlignment.Right,
        double? maxCap = null)
    {
        menu.ShouldConstrainToRootBounds = true;
        var layout = ResolveForAnchor(anchor, preferredSide, maxCap);
        menu.Placement = ToFlyoutPlacement(layout.Side, alignment);
        ApplyMenuMaxHeight(menu, layout.MaxHeight);
    }

    /// <summary>Configures and opens a <see cref="Flyout"/> popover inside the window.</summary>
    public static void ShowFlyoutAt(
        Flyout flyout,
        FrameworkElement anchor,
        ScrollViewer? scroll = null,
        PopoutSide preferredSide = PopoutSide.Bottom,
        PopoutAlignment alignment = PopoutAlignment.Right,
        double? maxHeightCap = null,
        double? preferredWidth = null)
    {
        ApplyFlyoutLayout(flyout, anchor, scroll, preferredSide, alignment, maxHeightCap, preferredWidth);
        flyout.ShowAt(anchor);
    }

    /// <summary>Wires an attached <see cref="Flyout"/> (Button.Flyout) to stay in the window on every open.</summary>
    public static void AttachFlyout(
        Flyout flyout,
        FrameworkElement anchor,
        ScrollViewer? scroll = null,
        PopoutSide preferredSide = PopoutSide.Bottom,
        PopoutAlignment alignment = PopoutAlignment.Right,
        double? maxHeightCap = null,
        double? preferredWidth = null)
    {
        flyout.ShouldConstrainToRootBounds = true;
        flyout.Placement = ToFlyoutPlacement(preferredSide, alignment);
        flyout.Opening += (_, _) => ApplyFlyoutLayout(flyout, anchor, scroll, preferredSide, alignment, maxHeightCap, preferredWidth);
    }

    /// <summary>Applies placement and window-bounded dimensions to a <see cref="Flyout"/> before it opens.</summary>
    public static void ApplyFlyoutLayout(
        Flyout flyout,
        FrameworkElement anchor,
        ScrollViewer? scroll = null,
        PopoutSide preferredSide = PopoutSide.Bottom,
        PopoutAlignment alignment = PopoutAlignment.Right,
        double? maxHeightCap = null,
        double? preferredWidth = null)
    {
        flyout.ShouldConstrainToRootBounds = true;
        var layout = ResolveForAnchor(anchor, preferredSide, maxHeightCap);
        flyout.Placement = ToFlyoutPlacement(layout.Side, alignment);
        if (scroll is not null)
        {
            scroll.MaxHeight = layout.MaxHeight;
            if (preferredWidth is > 0 && anchor.XamlRoot is { } root)
                scroll.Width = PopoutPlacement.PopoverWidth(
                    preferredWidth.Value, root.Size.Width, AppTheme.ComponentSize.MenuWindowMargin);
        }
    }

    private static void ApplyContextMenuLayout(MenuFlyout menu, XamlRoot? root, double? maxCap = null)
    {
        menu.ShouldConstrainToRootBounds = true;
        if (root is null || root.Size.Height <= 0)
            return;
        var maxHeight = PopoutPlacement.ContextMaxHeight(
            root.Size.Height, AppTheme.ComponentSize.MenuWindowMargin, maxCap);
        ApplyMenuMaxHeight(menu, maxHeight);
    }

    private static PopoutLayout ResolveForAnchor(FrameworkElement anchor, PopoutSide preferredSide, double? maxCap)
    {
        if (anchor.XamlRoot is not { Content: not null } root || root.Size.Height <= 0)
            return PopoutPlacement.Resolve(0, 0, 0, preferredSide, AppTheme.ComponentSize.MenuWindowMargin, maxCap);

        try
        {
            var top = anchor.TransformToVisual(root.Content).TransformPoint(default).Y;
            var bottom = top + anchor.ActualHeight;
            return PopoutPlacement.Resolve(
                top,
                bottom,
                root.Size.Height,
                preferredSide,
                AppTheme.ComponentSize.MenuWindowMargin,
                maxCap,
                topMargin: TopClearance);
        }
        catch (ArgumentException)
        {
            return PopoutPlacement.Resolve(0, 0, 0, preferredSide, AppTheme.ComponentSize.MenuWindowMargin, maxCap);
        }
    }

    private static void ApplyMenuMaxHeight(MenuFlyout menu, double maxHeight)
    {
        var style = new Style(typeof(MenuFlyoutPresenter));
        if (Application.Current.Resources.TryGetValue("DefaultMenuFlyoutPresenterStyle", out var found)
            && found is Style basis)
        {
            style.BasedOn = basis;
        }
        style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, maxHeight));
        menu.MenuFlyoutPresenterStyle = style;
    }

    private static FlyoutPlacementMode ToFlyoutPlacement(PopoutSide side, PopoutAlignment alignment) => (side, alignment) switch
    {
        (PopoutSide.Bottom, PopoutAlignment.Left) => FlyoutPlacementMode.BottomEdgeAlignedLeft,
        (PopoutSide.Bottom, PopoutAlignment.Right) => FlyoutPlacementMode.BottomEdgeAlignedRight,
        (PopoutSide.Top, PopoutAlignment.Left) => FlyoutPlacementMode.TopEdgeAlignedLeft,
        (PopoutSide.Top, PopoutAlignment.Right) => FlyoutPlacementMode.TopEdgeAlignedRight,
        (PopoutSide.Top, _) => FlyoutPlacementMode.Top,
        _ => FlyoutPlacementMode.Bottom,
    };

    private static void OnConstrainContextMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MenuFlyout menu || e.NewValue is not true)
            return;

        menu.ShouldConstrainToRootBounds = true;
        var marker = ContextHooks.GetOrCreateValue(menu);
        if (marker.Hooked)
            return;
        marker.Hooked = true;
        menu.Opening += static (sender, _) =>
        {
            if (sender is MenuFlyout flyout)
                ApplyContextMenuLayout(flyout, flyout.Target?.XamlRoot);
        };
    }

    private static void OnKeepInWindowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
            return;

        if (d is MenuFlyoutPresenter presenter)
        {
            HookMenuFlyoutPresenter(presenter);
            return;
        }

        if (d is not ComboBox box)
            return;

        var state = ComboStates.GetOrCreateValue(box);
        if (state.Hooked)
            return;
        state.Hooked = true;

        void KeepOpen(object? _, object __) => KeepOpenComboBoxInWindow(box);
        box.Loaded += (_, _) => ConstrainComboBox(box, state);
        box.PointerEntered += (_, _) => ConstrainComboBox(box, state);
        box.GotFocus += (_, _) => ConstrainComboBox(box, state);
        box.DropDownOpened += (_, _) =>
        {
            box.LayoutUpdated -= KeepOpen;
            box.LayoutUpdated += KeepOpen;
            KeepOpenComboBoxInWindow(box);
        };
        box.DropDownClosed += (_, _) => box.LayoutUpdated -= KeepOpen;
        box.Unloaded += (_, _) => box.LayoutUpdated -= KeepOpen;

        if (box.IsLoaded)
            ConstrainComboBox(box, state);
    }

    private static void HookMenuFlyoutPresenter(MenuFlyoutPresenter presenter)
    {
        var marker = PresenterHooks.GetOrCreateValue(presenter);
        if (marker.Hooked)
            return;
        marker.Hooked = true;

        void ApplyCap()
        {
            // Menus managed by WindowPopout already have a finite MaxHeight from their anchor geometry.
            // Unbounded presenters (notably WinUI's internal MenuBarItemFlyout) get capped to the room
            // below the title bar so long menus like Edit scroll inside compact windows instead of
            // overflowing past the window edge or flipping upward above the app.
            if (!marker.ManagedByFallback && !double.IsPositiveInfinity(presenter.MaxHeight))
                return;
            if (presenter.Parent is Popup popup && !popup.IsOpen && !popup.ShouldConstrainToRootBounds)
                popup.ShouldConstrainToRootBounds = true;
            if (presenter.XamlRoot is not { } root || root.Size.Height <= 0)
                return;

            var room = PopoutPlacement.Resolve(
                anchorTop: 0,
                anchorBottom: AppTheme.ComponentSize.HeaderHeight,
                windowHeight: root.Size.Height,
                preferredSide: PopoutSide.Bottom,
                margin: AppTheme.ComponentSize.MenuWindowMargin).MaxHeight;
            presenter.MaxHeight = room;
            marker.ManagedByFallback = true;
        }

        presenter.Loading += (_, _) => ApplyCap();
        presenter.Loaded += (_, _) => ApplyCap();
        if (presenter.IsLoaded)
            ApplyCap();
    }

    private static void ConstrainComboBox(ComboBox box, ComboState state)
    {
        if (box.IsDropDownOpen)
            return;

        if (!state.PopupConstrained)
        {
            box.ApplyTemplate();
            if (FindPopup(box) is { } popup)
            {
                if (!popup.ShouldConstrainToRootBounds)
                    popup.ShouldConstrainToRootBounds = true;
                state.PopupConstrained = true;
            }
        }

        if (box.XamlRoot is not { } root || root.Size.Height <= 0)
            return;

        var hasLocal = box.ReadLocalValue(ComboBox.MaxDropDownHeightProperty) != DependencyProperty.UnsetValue;
        var ownerManaged = hasLocal && !(state.WeSetMaxHeight && Math.Abs(box.MaxDropDownHeight - state.LastAppliedMaxHeight) < 0.5);
        if (ownerManaged)
            return;

        var cap = PopoutPlacement.ContextMaxHeight(
            root.Size.Height, AppTheme.ComponentSize.MenuWindowMargin, maxCap: DefaultComboBoxMaxDropDownHeight);
        if (!state.WeSetMaxHeight || Math.Abs(cap - state.LastAppliedMaxHeight) >= 1)
        {
            box.MaxDropDownHeight = cap;
            state.WeSetMaxHeight = true;
            state.LastAppliedMaxHeight = cap;
        }
    }

    private static void KeepOpenComboBoxInWindow(ComboBox box)
    {
        if (!box.IsDropDownOpen || box.XamlRoot is not { Content: not null } root || root.Size.Height <= 0)
            return;
        if (FindPopup(box) is not { Child: FrameworkElement list } popup || list.ActualHeight <= 0)
            return;

        try
        {
            var top = list.TransformToVisual(root.Content).TransformPoint(default).Y;
            var bottom = top + list.ActualHeight;
            double edge = AppTheme.Spacing.Md, shift = 0;
            if (bottom > root.Size.Height - edge)
                shift = root.Size.Height - edge - bottom;
            if (top + shift < edge)
                shift = edge - top;
            if (Math.Abs(shift) >= 1)
                popup.VerticalOffset += shift;
        }
        catch (ArgumentException)
        {
        }
    }

    private static Popup? FindPopup(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Popup popup)
                return popup;
            if (FindPopup(child) is { } found)
                return found;
        }
        return null;
    }

    private sealed class HookMarker
    {
        public bool Hooked;
        public bool ManagedByFallback;
    }

    private sealed class ComboState
    {
        public bool Hooked;
        public bool PopupConstrained;
        public bool WeSetMaxHeight;
        public double LastAppliedMaxHeight;
    }
}
