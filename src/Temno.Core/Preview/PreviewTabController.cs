using Temno.Core.Models;

namespace Temno.Core.Preview;

/// <summary>
/// Mac <c>EditorViewModel+PreviewTabs</c>: timeline tab plus media source tabs, with back/forward history.
/// </summary>
public sealed class PreviewTabController
{
    private readonly List<PreviewMonitorTab> _tabs = [];
    private readonly List<string> _history = [];
    private int _historyIndex;

    public PreviewTabController(string timelineName = "Timeline")
    {
        Reset(timelineName);
    }

    public IReadOnlyList<PreviewMonitorTab> Tabs => _tabs;

    public string ActiveId { get; private set; } = PreviewMonitorTab.TimelineId;

    public PreviewMonitorTab Active
        => _tabs.FirstOrDefault(t => t.Id == ActiveId) ?? _tabs[0];

    public bool IsTimeline => Active.Kind == PreviewMonitorKind.Timeline;

    public bool HasSourceTabs => _tabs.Any(tab => tab.Kind == PreviewMonitorKind.MediaAsset);

    public bool CanGoBack => _historyIndex > 0;

    public bool CanGoForward => _historyIndex < _history.Count - 1;

    public void Reset(string timelineName)
    {
        _tabs.Clear();
        _tabs.Add(PreviewMonitorTab.Timeline(timelineName));
        ActiveId = PreviewMonitorTab.TimelineId;
        _history.Clear();
        _history.Add(PreviewMonitorTab.TimelineId);
        _historyIndex = 0;
    }

    public void RenameTimeline(string name)
    {
        var index = _tabs.FindIndex(t => t.Id == PreviewMonitorTab.TimelineId);
        if (index >= 0)
            _tabs[index] = PreviewMonitorTab.Timeline(name);
    }

    /// <summary>Opens or focuses a source tab. Always activates it (Mac <c>openPreviewTab</c>).</summary>
    public PreviewMonitorTab OpenMedia(string mediaRef, string name, ClipType type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaRef);
        var tab = PreviewMonitorTab.ForMedia(mediaRef, name, type);
        if (_tabs.All(t => t.Id != tab.Id))
            _tabs.Add(tab);
        else
        {
            var index = _tabs.FindIndex(t => t.Id == tab.Id);
            _tabs[index] = tab;
        }

        ActiveId = tab.Id;
        PushHistory(tab.Id);
        return tab;
    }

    public bool Select(string id)
    {
        if (_tabs.All(t => t.Id != id) || ActiveId == id)
            return false;
        ActiveId = id;
        PushHistory(id);
        return true;
    }

    /// <returns>True when the active tab changed.</returns>
    public bool Close(string id)
    {
        if (id == PreviewMonitorTab.TimelineId)
            return false;

        var removedActive = ActiveId == id;
        _tabs.RemoveAll(t => t.Id == id);
        _history.RemoveAll(h => h == id);
        if (_history.Count == 0)
        {
            _history.Add(PreviewMonitorTab.TimelineId);
            _historyIndex = 0;
        }
        else
        {
            _historyIndex = Math.Min(_historyIndex, _history.Count - 1);
        }

        if (removedActive)
            ActiveId = _history[_historyIndex];
        return removedActive;
    }

    public void CloseAllMedia()
    {
        var name = _tabs.FirstOrDefault(t => t.Id == PreviewMonitorTab.TimelineId)?.Title ?? "Timeline";
        Reset(name);
    }

    public bool Back() => StepHistory(-1);

    public bool Forward() => StepHistory(1);

    public bool ContainsMedia(string mediaRef)
        => _tabs.Any(t => t.Kind == PreviewMonitorKind.MediaAsset && t.MediaRef == mediaRef);

    /// <summary>Drops source tabs whose media is no longer in the project. Returns true if the active tab changed.</summary>
    public bool PruneMissing(IReadOnlySet<string> liveMediaIds)
    {
        ArgumentNullException.ThrowIfNull(liveMediaIds);
        var stale = _tabs
            .Where(t => t.Kind == PreviewMonitorKind.MediaAsset && (t.MediaRef is null || !liveMediaIds.Contains(t.MediaRef)))
            .Select(t => t.Id)
            .ToList();
        var activeChanged = false;
        foreach (var id in stale)
            activeChanged |= Close(id);
        return activeChanged;
    }

    private bool StepHistory(int delta)
    {
        var next = _historyIndex + delta;
        if (next < 0 || next >= _history.Count)
            return false;
        _historyIndex = next;
        var id = _history[next];
        if (ActiveId == id)
            return false;
        ActiveId = id;
        return true;
    }

    private void PushHistory(string id)
    {
        var tail = _historyIndex + 1;
        if (tail < _history.Count)
            _history.RemoveRange(tail, _history.Count - tail);
        if (_history.Count > 0 && _history[^1] == id)
            return;
        _history.Add(id);
        _historyIndex = _history.Count - 1;
    }
}
