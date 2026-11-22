using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Temno.App.Controls.Shared;
using Temno.App.Theme;
using Temno.Core.Models;
using Temno.Core.Ui;
using Temno.Media.Cache;

namespace Temno.App.Controls;

// Source pickers and chips show what a source looks like, so a reference or first/last frame is
// recognisable without searching the media library. An image is decoded from its file and shows
// larger on hover; a video shows the frame the media library already made. Nothing here extracts
// frames, so opening the sources list never starts ffmpeg.
public sealed partial class GenerationComposer
{
    private static readonly int[] LibraryThumbnailHeights =
        [.. new[] { MediaThumbnailSize.ExtraLarge, MediaThumbnailSize.Large, MediaThumbnailSize.Medium, MediaThumbnailSize.Small }
            .Select(size => size.ToPixels())];
    private readonly ThumbnailCache _libraryThumbnails = new(MediaCachePaths.Default);
    private readonly Dictionary<string, BitmapImage> _sourceImages = [];

    // A drop-down row keeps the picker's own text style and trims; a check box row keeps the
    // secondary wrapped label it had before thumbnails.
    private FrameworkElement SourceRow(MediaManifestEntry asset, bool pickerText = false)
    {
        var name = new TextBlock { Text = asset.Name, VerticalAlignment = VerticalAlignment.Center };
        if (pickerText) name.TextTrimming = TextTrimming.CharacterEllipsis;
        else
        {
            name.FontSize = AppTheme.FontSize.SmMd;
            name.Foreground = AppTheme.Text.SecondaryBrush;
            name.TextWrapping = TextWrapping.WrapWholeWords;
        }
        var row = new Grid { ColumnSpacing = AppTheme.Spacing.Sm };
        row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(SourceThumbnail(asset));
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        return row;
    }

    private ComboBoxItem SourceItem(MediaManifestEntry asset)
    {
        var item = new ComboBoxItem { Content = SourceRow(asset, pickerText: true), Tag = asset.Id };
        AutomationProperties.SetName(item, asset.Name);
        return item;
    }

    private FrameworkElement SourceThumbnail(MediaManifestEntry asset)
    {
        var tile = new Border
        {
            Width = AppTheme.GenerationPanel.ReferenceSize, Height = AppTheme.GenerationPanel.ReferenceSize,
            CornerRadius = AppTheme.CornerRadius.XsSm, Background = AppTheme.Background.RaisedBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (SourceImage(asset) is not { } image)
        {
            // Audio, and a video the library has no frame for yet, keep their type glyph.
            tile.Child = new FluentIcon
            {
                Icon = asset.Type switch { ClipType.audio => Icons.MusicNote2, ClipType.image => Icons.Image, _ => Icons.Video },
                Width = AppTheme.IconSize.Sm, Height = AppTheme.IconSize.Sm, Fill = AppTheme.Text.SecondaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            return tile;
        }
        tile.Child = new Image { Source = image, Stretch = Stretch.UniformToFill };
        // The library's video frame is too small to enlarge, so only an image gets the hover preview.
        if (asset.Type == ClipType.image)
        {
            var preview = new Image { Source = image, Stretch = Stretch.Uniform,
                MaxWidth = AppTheme.GenerationPanel.ReferencePreviewSize, MaxHeight = AppTheme.GenerationPanel.ReferencePreviewSize };
            AutomationProperties.SetName(preview, asset.Name);
            ToolTipService.SetToolTip(tile, new ToolTip { Content = preview });
        }
        return tile;
    }

    // A miss is not kept, so a frame the library makes later appears the next time the list opens.
    private BitmapImage? SourceImage(MediaManifestEntry asset)
    {
        if (_sourceImages.TryGetValue(asset.Id, out var cached)) return cached;
        if (asset.Type is not (ClipType.image or ClipType.video)) return null;
        try
        {
            var path = _project?.Resolver.ResolvePath(asset);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            BitmapImage image;
            if (asset.Type == ClipType.image)
                // One decode at preview width serves both the tile and the hover preview.
                image = new BitmapImage(new Uri(path))
                {
                    DecodePixelType = DecodePixelType.Logical,
                    DecodePixelWidth = (int)AppTheme.GenerationPanel.ReferencePreviewSize
                };
            else if (LibraryFrame(path, asset.Duration) is { } frame) image = new BitmapImage(new Uri(frame));
            else return null;
            _sourceImages[asset.Id] = image;
            return image;
        }
        catch (Exception)
        {
            // Offline or unreadable media keeps its glyph; the upload path reports the file itself.
            return null;
        }
    }

    // The frame the media library shows for a video, at the largest size it has made, read from its
    // disk cache; null when it has made none.
    private string? LibraryFrame(string path, double duration)
    {
        var primary = MediaPosterTimes.Primary(duration);
        var fallbacks = MediaPosterTimes.Fallbacks(duration, primary).ToArray();
        foreach (var height in LibraryThumbnailHeights)
        {
            if (!_libraryThumbnails.TryGet(path, primary, height, out var frame)) continue;
            // The library makes a fallback only when the frame before it was black and stops at the
            // first that is not, so the last fallback it made is the one it shows.
            foreach (var seconds in fallbacks)
                if (_libraryThumbnails.TryGet(path, seconds, height, out var later)) frame = later;
            return frame;
        }
        return null;
    }
}
