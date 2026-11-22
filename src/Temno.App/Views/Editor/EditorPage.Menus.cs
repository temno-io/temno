using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Session;
using Temno.Core.Ui;
using Windows.Foundation;
using Windows.System;

namespace Temno.App.Views.Editor;

public sealed partial class EditorPage
{
    private Storyboard? _menuRevealStoryboard;
    private bool _menusExpanded;
    private bool _updatingTimelineToolbarLayout;
    private bool _scrollActiveTabQueued;
    private IReadOnlyList<Temno.App.ViewModels.TimelineTabItem>? _renderedTimelineTabs;

    private void InitializeHeaderKeyboard()
    {
        SetMenusExpanded(false, animate: false);
        var menuKey = new KeyboardAccelerator { Key = VirtualKey.F10 };
        menuKey.Invoked += (_, args) =>
        {
            SetMenusExpanded(true, animate: false);
            EditorMenuBar.Items[0].Focus(FocusState.Keyboard);
            args.Handled = true;
        };
        RootGrid.KeyboardAccelerators.Add(menuKey);
    }

    private bool SyncTimelineTabsSource(bool force = false)
    {
        var next = _vm.TimelineTabs;
        if (!force && TimelineTabsEqual(_renderedTimelineTabs, next))
            return false;
        _renderedTimelineTabs = next;
        TimelineTabsList.ItemsSource = next;
        return true;
    }

    private static bool TimelineTabsEqual(
        IReadOnlyList<Temno.App.ViewModels.TimelineTabItem>? a,
        IReadOnlyList<Temno.App.ViewModels.TimelineTabItem> b)
    {
        if (a is null || a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i].Id, b[i].Id, StringComparison.Ordinal)
                || !string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal)
                || a[i].IsActive != b[i].IsActive
                || a[i].ShowClose != b[i].ShowClose)
            {
                return false;
            }
        }
        return true;
    }

    private void TimelineToolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateTimelineToolbarLayout(e.NewSize.Width);
        if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
            ScrollActiveTimelineTabIntoView();
    }

    private void TimelineTabsList_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateTimelineToolbarLayout();

    private void UpdateTimelineToolbarLayout(double? overrideToolbarWidth = null)
    {
        if (_updatingTimelineToolbarLayout
            || TimelineToolbar is null
            || TimelineTabsScroll is null
            || ToolOverflowButton is null)
            return;

        var toolbarWidth = overrideToolbarWidth ?? TimelineToolbar.ActualWidth;
        if (toolbarWidth <= 0) return;

        _updatingTimelineToolbarLayout = true;
        try
        {
            var openTabs = _vm.TimelineTabs;
            var openCount = openTabs.Count;
            var totalTimelines = AppSession.Current.Session?.ProjectFile.Timelines.Count ?? openCount;

            // Use the realized unconstrained strip width (ScrollViewer measures ItemsControl with
            // infinite horizontal space) once every tab container has been realized; fall back to
            // the character-based estimate immediately after an ItemsSource replacement so adding
            // a tab never briefly constrains the strip to the previous tab count's width.
            var estimated = TimelineToolbarLayout.EstimateTabsWidth(
                openTabs.Select(t => t.Name),
                showClose: openCount > 1);
            var realized = TimelineTabsList is not null
                && openCount > 0
                && TimelineTabsList.Items.Count == openCount
                && TimelineTabsList.ActualWidth > 1
                && TimelineTabsList.ContainerFromIndex(openCount - 1) is FrameworkElement { ActualWidth: > 0 };
            var desiredTabsWidth = realized
                ? Math.Max(Math.Ceiling(TimelineTabsList!.ActualWidth) + 2, TimelineToolbarLayout.MinTabsScrollWidth)
                : estimated;

            var metrics = TimelineToolbarLayout.Compute(toolbarWidth, desiredTabsWidth, openCount, totalTimelines);

            TimelineTabsColumn.Width = GridLength.Auto;
            TimelineTabsHost.MaxWidth = double.PositiveInfinity;
            TimelineTabsScroll.MaxWidth = metrics.MaxTabsScrollWidth;

            var secondaryVis = metrics.OverflowSecondaryTools ? Visibility.Collapsed : Visibility.Visible;
            foreach (var element in new FrameworkElement[]
            {
                ToolRipple, ToolOverwrite, ToolSnap, ToolFit, ToolAddText,
                TimelineModesDivider, TimelineTextDivider,
            })
            {
                element.Visibility = secondaryVis;
            }
            ToolOverflowButton.Visibility = metrics.OverflowSecondaryTools ? Visibility.Visible : Visibility.Collapsed;

            if (TimelineZoomCluster is not null)
                TimelineZoomCluster.Visibility = metrics.HideZoomControls ? Visibility.Collapsed : Visibility.Visible;
            if (TimelineZoomValue is not null)
                TimelineZoomValue.Visibility = metrics.HideZoomReadout ? Visibility.Collapsed : Visibility.Visible;
            if (TimelineZoomSlider is not null)
                TimelineZoomSlider.Width = metrics.CompactZoomSlider
                    ? TimelineToolbarLayout.ZoomSliderCompactWidth
                    : TimelineToolbarLayout.ZoomSliderFullWidth;
            if (TimelineAllTabsButton is not null)
                TimelineAllTabsButton.Visibility = metrics.ShowAllTimelinesButton ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _updatingTimelineToolbarLayout = false;
        }
    }

    private void TimelineTabsScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (TimelineTabsScroll is null || TimelineTabsScroll.ScrollableWidth <= 0) return;
        var delta = e.GetCurrentPoint(TimelineTabsScroll).Properties.MouseWheelDelta;
        if (delta == 0) return;
        var next = Math.Clamp(TimelineTabsScroll.HorizontalOffset - delta * 0.5, 0, TimelineTabsScroll.ScrollableWidth);
        TimelineTabsScroll.ChangeView(next, null, null, disableAnimation: true);
        e.Handled = true;
    }

    private void ScrollActiveTimelineTabIntoView()
    {
        if (TimelineTabsScroll is null || TimelineTabsList is null || _scrollActiveTabQueued) return;
        _scrollActiveTabQueued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _scrollActiveTabQueued = false;
            if (TimelineTabsScroll is null || TimelineTabsList is null) return;
            UpdateTimelineToolbarLayout();
            TimelineTabsScroll.UpdateLayout();

            var activeIndex = -1;
            for (var i = 0; i < _vm.TimelineTabs.Count; i++)
            {
                if (_vm.TimelineTabs[i].IsActive)
                {
                    activeIndex = i;
                    break;
                }
            }
            if (activeIndex < 0) return;

            var scrollable = TimelineTabsScroll.ScrollableWidth;
            if (scrollable <= 0)
            {
                if (TimelineTabsScroll.HorizontalOffset > 0)
                    TimelineTabsScroll.ChangeView(0, null, null, disableAnimation: true);
                return;
            }

            if (activeIndex == _vm.TimelineTabs.Count - 1)
            {
                TimelineTabsScroll.ChangeView(scrollable, null, null, disableAnimation: true);
                return;
            }

            if (TimelineTabsList.ContainerFromIndex(activeIndex) is FrameworkElement item)
            {
                var viewport = TimelineTabsScroll.ViewportWidth;
                if (item.ActualWidth > 0 && viewport > 0)
                {
                    var pos = item.TransformToVisual(TimelineTabsList).TransformPoint(new Point(0, 0));
                    var itemLeft = pos.X;
                    var itemRight = pos.X + item.ActualWidth;
                    var viewLeft = TimelineTabsScroll.HorizontalOffset;
                    var viewRight = viewLeft + viewport;
                    if (itemLeft < viewLeft)
                        TimelineTabsScroll.ChangeView(Math.Max(0, itemLeft), null, null, disableAnimation: true);
                    else if (itemRight > viewRight)
                        TimelineTabsScroll.ChangeView(Math.Min(scrollable, itemRight - viewport), null, null, disableAnimation: true);
                    return;
                }
                item.StartBringIntoView();
            }
        });
    }

    private void TimelineAllTabs_Click(object sender, RoutedEventArgs e)
    {
        var session = AppSession.Current.Session;
        if (session is null || TimelineAllTabsButton is null) return;

        TimelineCommands.EnsureOpenTimelineIds(session);
        var openIds = session.ProjectFile.OpenTimelineIds ?? [];
        var activeId = session.ProjectFile.ActiveTimelineId;
        var menu = new MenuFlyout();

        foreach (var tl in session.ProjectFile.Timelines)
        {
            var isOpen = openIds.Contains(tl.Id);
            var item = new ToggleMenuFlyoutItem
            {
                Text = isOpen ? tl.Name : $"{tl.Name} (closed)",
                IsChecked = string.Equals(tl.Id, activeId, StringComparison.Ordinal),
            };
            var targetId = tl.Id;
            item.Click += (_, _) =>
            {
                var current = AppSession.Current.Session;
                if (current is null) return;
                if (!TimelineCommands.SetActiveTimeline(current, targetId).Ok) return;
                BindSession();
                StatusText.Text = "Timeline · " + current.ActiveTimeline.Name;
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        var addItem = new MenuFlyoutItem { Text = "New Timeline" };
        addItem.Click += AddTimeline_Click;
        menu.Items.Add(addItem);

        WindowPopout.ShowMenuAt(menu, TimelineAllTabsButton, PopoutSide.Bottom, PopoutAlignment.Left);
    }

    private void ToolOverflow_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        void Toggle(string label, Microsoft.UI.Xaml.Controls.Primitives.ToggleButton source, RoutedEventHandler handler)
        {
            var item = new ToggleMenuFlyoutItem { Text = label, IsChecked = source.IsChecked == true };
            item.Click += (_, args) => { source.IsChecked = item.IsChecked; handler(source, args); };
            menu.Items.Add(item);
        }
        Toggle("Ripple delete", ToolRipple, ToolRipple_Click);
        Toggle("Overwrite on move", ToolOverwrite, ToolOverwrite_Click);
        Toggle("Snap", ToolSnap, ToolSnap_Click);
        menu.Items.Add(new MenuFlyoutSeparator());
        var fit = new MenuFlyoutItem { Text = "Fit timeline" };
        fit.Click += ToolFit_Click;
        menu.Items.Add(fit);
        if (TimelineZoomCluster is { Visibility: Visibility.Collapsed })
        {
            var zoomIn = new MenuFlyoutItem { Text = "Zoom in" };
            zoomIn.Click += (_, _) => Timeline.PixelsPerFrame *= 1.25;
            menu.Items.Add(zoomIn);
            var zoomOut = new MenuFlyoutItem { Text = "Zoom out" };
            zoomOut.Click += (_, _) => Timeline.PixelsPerFrame /= 1.25;
            menu.Items.Add(zoomOut);
        }
        var text = new MenuFlyoutItem { Text = "Add text" };
        text.Click += AddText_Click;
        menu.Items.Add(text);
        WindowPopout.ShowMenuAt(menu, ToolOverflowButton, PopoutSide.Bottom, PopoutAlignment.Right);
    }

    private void MenuDisclosure_Click(object sender, RoutedEventArgs e)
        => SetMenusExpanded(MenuDisclosureButton.IsChecked == true);

    private void MenuRevealHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => MenuRevealClip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);

    private bool HandleMenuKey(KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Menu or VirtualKey.F10)
        {
            // Expose native access keys before WinUI resolves the Alt key sequence.
            SetMenusExpanded(true, animate: false);
            if (e.Key == VirtualKey.F10)
            {
                EditorMenuBar.Items[0].Focus(FocusState.Keyboard);
                e.Handled = true;
            }
            return true;
        }
        if (e.Key == VirtualKey.Escape && _menusExpanded
            && VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Count == 0)
        {
            SetMenusExpanded(false);
            MenuDisclosureButton.Focus(FocusState.Keyboard);
            e.Handled = true;
            return true;
        }
        return false;
    }

    private void SetMenusExpanded(bool expanded, bool animate = true)
    {
        var width = MenuRevealHost.Width;
        var opacity = EditorMenuBar.Opacity;
        var offset = MenuRevealTransform.TranslateX;
        var angle = MenuChevronTransform.Rotation;
        _menuRevealStoryboard?.Stop();
        _menusExpanded = expanded;
        MenuDisclosureButton.IsChecked = expanded;
        AutomationProperties.SetName(MenuDisclosureButton, expanded ? "Hide application menus" : "Show application menus");
        EditorMenuBar.IsHitTestVisible = expanded;
        foreach (var item in EditorMenuBar.Items) item.IsTabStop = expanded;

        var targetWidth = expanded ? AppTheme.ComponentSize.MenuRevealWidth : AppTheme.BorderWidth.Thin;
        var targetOpacity = expanded ? 1d : 0d;
        var targetOffset = expanded ? 0d : -AppTheme.Spacing.SmMd;
        var targetAngle = expanded ? 180d : 0d;
        MenuRevealHost.Width = targetWidth;
        EditorMenuBar.Opacity = targetOpacity;
        MenuRevealTransform.TranslateX = targetOffset;
        MenuChevronTransform.Rotation = targetAngle;
        if (!animate || Motion.ReduceMotion) return;

        var storyboard = new Storyboard();
        AddMenuTrack(storyboard, MenuRevealHost, "Width", width, targetWidth, dependent: true);
        AddMenuTrack(storyboard, EditorMenuBar, "Opacity", opacity, targetOpacity);
        AddMenuTrack(storyboard, MenuRevealTransform, "TranslateX", offset, targetOffset);
        AddMenuTrack(storyboard, MenuChevronTransform, "Rotation", angle, targetAngle);
        _menuRevealStoryboard = storyboard;
        storyboard.Begin();
    }

    private static void AddMenuTrack(Storyboard storyboard, DependencyObject target, string property,
        double from, double to, bool dependent = false)
    {
        var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = dependent };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = from });
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime = Motion.Transition,
            Value = to,
            KeySpline = new KeySpline { ControlPoint1 = new Point(0, 0), ControlPoint2 = new Point(.58, 1) },
        });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }
}
