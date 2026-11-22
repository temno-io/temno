using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Temno.App.Audio;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Captions;
using Temno.Core.Editing;
using Temno.Core.IO;
using Temno.Core.Models;
using Temno.Core.Preview;
using Temno.Core.Session;
using Temno.Core.Ui;
using Temno.Media;
using Temno.Media.Cache;
using Temno.Media.Audio;
using Temno.Media.Playback;
using Windows.Foundation;
using Windows.Media.Playback;
using NativeMediaSource = Windows.Media.Core.MediaSource;

using Temno.App.ViewModels;

namespace Temno.App.Controls;

public sealed partial class PreviewHostControl : UserControl
{
    private void GuideOverlay_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawGuides();

    /// <summary>Raised when the user captures the displayed frame. Payload is the written PNG path.</summary>
    internal event EventHandler<string>? FrameCaptured;

    private void SyncGuideMenu()
    {
        if (GuideHideItem is null) return;
        GuideHideItem.IsChecked = _guides.Hidden;
        GridNoneItem.IsChecked = _guides.Grid == PreviewGridMode.None;
        Grid2Item.IsChecked = _guides.Grid == PreviewGridMode.Grid2;
        Grid3Item.IsChecked = _guides.Grid == PreviewGridMode.Grid3;
        Grid4Item.IsChecked = _guides.Grid == PreviewGridMode.Grid4;
        Grid5Item.IsChecked = _guides.Grid == PreviewGridMode.Grid5;
        SafeActionItem.IsChecked = _guides.SafeZones.HasFlag(PreviewSafeZones.Action);
        SafeTitleItem.IsChecked = _guides.SafeZones.HasFlag(PreviewSafeZones.Title);
        SafeCenterItem.IsChecked = _guides.SafeZones.HasFlag(PreviewSafeZones.Center);
        FormatNoneItem.IsChecked = _guides.Format == PreviewFormatReference.None;
        FormatScopeItem.IsChecked = _guides.Format == PreviewFormatReference.Scope;
        FormatWideItem.IsChecked = _guides.Format == PreviewFormatReference.Wide;
        FormatSquareItem.IsChecked = _guides.Format == PreviewFormatReference.Square;
        FormatPortraitItem.IsChecked = _guides.Format == PreviewFormatReference.Portrait;
    }

    private void GuideFlyout_Opening(object sender, object e)
    {
        SyncGuideMenu();
        WindowPopout.ApplyMenuLayout(GuideFlyout, BtnGuides, PopoutSide.Top, PopoutAlignment.Right);
    }

    private void GuideFlyout_Closed(object sender, object e) => ApplyFromMenu();

    private void GuideHide_Click(object sender, RoutedEventArgs e) => QueueApplyFromMenu();

    private void GuideGrid_Click(object sender, RoutedEventArgs e)
    {
        // Picking a guide while hidden is a request to see it.
        GuideHideItem.IsChecked = false;
        QueueApplyFromMenu();
    }

    private void GuideSafe_Click(object sender, RoutedEventArgs e)
    {
        GuideHideItem.IsChecked = false;
        QueueApplyFromMenu();
    }

    private void GuideFormat_Click(object sender, RoutedEventArgs e)
    {
        GuideHideItem.IsChecked = false;
        QueueApplyFromMenu();
    }

    /// <summary>
    /// WinUI can raise <c>Click</c> before <c>IsChecked</c> flips. Apply on the next dispatcher
    /// tick so <see cref="ApplyFromMenu"/> reads the flags the user actually sees. Radios that
    /// never fire <c>Click</c> still land via <see cref="GuideFlyout_Closed"/>.
    /// </summary>
    private void QueueApplyFromMenu()
        => DispatcherQueue.TryEnqueue(ApplyFromMenu);

    /// <summary>
    /// Reads the flyout's <c>IsChecked</c> flags after the control has applied the click.
    /// Radio items sometimes never fire <c>Click</c>; <see cref="GuideFlyout_Closed"/> still
    /// lands the selection the user saw.
    /// </summary>
    private void ApplyFromMenu()
    {
        if (GuideHideItem is null) return;
        var grid = PreviewGridMode.None;
        if (Grid2Item.IsChecked) grid = PreviewGridMode.Grid2;
        else if (Grid3Item.IsChecked) grid = PreviewGridMode.Grid3;
        else if (Grid4Item.IsChecked) grid = PreviewGridMode.Grid4;
        else if (Grid5Item.IsChecked) grid = PreviewGridMode.Grid5;

        var safe = PreviewSafeZones.None;
        if (SafeActionItem.IsChecked) safe |= PreviewSafeZones.Action;
        if (SafeTitleItem.IsChecked) safe |= PreviewSafeZones.Title;
        if (SafeCenterItem.IsChecked) safe |= PreviewSafeZones.Center;

        var format = PreviewFormatReference.None;
        if (FormatScopeItem.IsChecked) format = PreviewFormatReference.Scope;
        else if (FormatWideItem.IsChecked) format = PreviewFormatReference.Wide;
        else if (FormatSquareItem.IsChecked) format = PreviewFormatReference.Square;
        else if (FormatPortraitItem.IsChecked) format = PreviewFormatReference.Portrait;

        var next = new PreviewGuideState(grid, safe, format, GuideHideItem.IsChecked);
        if (next == _guides) return;
        Guides = next;
    }
}
