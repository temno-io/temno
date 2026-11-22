namespace Temno.App.Services;

/// <summary>Shared editor interaction state across timeline / inspector / shortcuts.</summary>
public sealed class EditorEditContext
{
    private readonly HashSet<string> _selectedClipIds = new(StringComparer.Ordinal);
    private string? _selectedClipId;

    public static EditorEditContext Current { get; } = new();

    public EditorToolMode ToolMode { get; set; } = EditorToolMode.Select;
    public bool RippleDelete { get; set; } = true;
    public bool OverwriteOnMove { get; set; }
    public bool SnapEnabled { get; set; } = true;
    /// <summary>
    /// The focused selection member for single-clip surfaces such as the inspector. The full
    /// selection is exposed separately so a caller cannot accidentally discard a group edit.
    /// </summary>
    public string? SelectedClipId
    {
        get => _selectedClipId;
        set => SetSelectedClips(value is null ? Array.Empty<string>() : [value], value);
    }

    public IReadOnlySet<string> SelectedClipIds => _selectedClipIds;

    public event EventHandler? Changed;

    public bool SetSelectedClips(IEnumerable<string> clipIds, string? focusedClipId = null)
    {
        ArgumentNullException.ThrowIfNull(clipIds);
        var next = new HashSet<string>(clipIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.Ordinal);
        var nextFocused = !string.IsNullOrWhiteSpace(focusedClipId) && next.Contains(focusedClipId)
            ? focusedClipId
            : !string.IsNullOrWhiteSpace(_selectedClipId) && next.Contains(_selectedClipId)
                ? _selectedClipId
                : next.OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
        var changed = !_selectedClipIds.SetEquals(next)
            || !string.Equals(_selectedClipId, nextFocused, StringComparison.Ordinal);
        if (!changed) return false;

        _selectedClipIds.Clear();
        _selectedClipIds.UnionWith(next);
        _selectedClipId = nextFocused;
        Notify();
        return true;
    }

    public bool ToggleSelectedClip(string clipId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipId);
        var next = new HashSet<string>(_selectedClipIds, StringComparer.Ordinal);
        var focused = _selectedClipId;
        if (!next.Remove(clipId))
        {
            next.Add(clipId);
            focused = clipId;
        }
        else if (string.Equals(focused, clipId, StringComparison.Ordinal))
        {
            focused = next.OrderBy(id => id, StringComparer.Ordinal).FirstOrDefault();
        }

        return SetSelectedClips(next, focused);
    }

    public bool ClearSelection() => SetSelectedClips(Array.Empty<string>());

    public void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}
