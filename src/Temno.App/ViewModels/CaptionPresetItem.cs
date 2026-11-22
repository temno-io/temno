using System.ComponentModel;
using System.Runtime.CompilerServices;
using Temno.Core.Models;

namespace Temno.App.ViewModels;

/// <summary>
/// One cell in the caption preset gallery. Composition comes from
/// <see cref="TextAnimation.GalleryPerLine"/> / <see cref="TextAnimation.GalleryPerWord"/> —
/// never a hand-copied array in the view.
/// </summary>
public sealed class CaptionPresetItem : INotifyPropertyChanged
{
    public const string PerLineGroup = "Per line";
    public const string PerWordGroup = "Per word";

    private bool _isSelected;
    private bool _isPointerOver;
    private bool _isFocused;
    private bool _isLive;

    public CaptionPresetItem(string preset, string group)
    {
        Preset = preset;
        Group = group;
        DisplayName = TextAnimation.DisplayName(preset);
    }

    public string Preset { get; }
    public string Group { get; }
    public string DisplayName { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    public bool IsPointerOver
    {
        get => _isPointerOver;
        set => Set(ref _isPointerOver, value);
    }

    public bool IsFocused
    {
        get => _isFocused;
        set => Set(ref _isFocused, value);
    }

    /// <summary>False when the Captions panel is hidden or the window is deactivated.</summary>
    public bool IsLive
    {
        get => _isLive;
        set => Set(ref _isLive, value);
    }

    public bool ShouldAnimate =>
        TextAnimation.GalleryCellAnimates(Preset, IsLive, IsSelected, IsPointerOver, IsFocused);

    public static IReadOnlyList<CaptionPresetItem> FromGalleryLists()
    {
        var cells = new List<CaptionPresetItem>(
            TextAnimation.GalleryPerLine.Length + TextAnimation.GalleryPerWord.Length);
        foreach (var preset in TextAnimation.GalleryPerLine)
            cells.Add(new CaptionPresetItem(preset, PerLineGroup));
        foreach (var preset in TextAnimation.GalleryPerWord)
            cells.Add(new CaptionPresetItem(preset, PerWordGroup));
        return cells;
    }

    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name is not nameof(ShouldAnimate))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShouldAnimate)));
    }
}
