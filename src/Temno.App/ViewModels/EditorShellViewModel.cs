using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Temno.App.Services;
using Temno.Core.Models;
using Temno.Core.Session;
using Windows.UI.Text;

namespace Temno.App.ViewModels;

/// <summary>Shell state for the editor layout (panel visibility, session snapshot for bindings).</summary>
public sealed class EditorShellViewModel : INotifyPropertyChanged
{
    private bool _agentVisible;
    private bool _mediaVisible = true;
    private bool _inspectorVisible = true;
    private string _projectName = "Project";
    private string _projectPath = "";
    private bool _isDirty;
    private string _timelineSummary = "";
    private string _statusText = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProjectSession? Session => AppSession.Current.Session;

    public bool AgentVisible
    {
        get => _agentVisible;
        set => Set(ref _agentVisible, value);
    }

    public bool MediaVisible
    {
        get => _mediaVisible;
        set => Set(ref _mediaVisible, value);
    }

    public bool InspectorVisible
    {
        get => _inspectorVisible;
        set => Set(ref _inspectorVisible, value);
    }

    public string ProjectName
    {
        get => _projectName;
        private set => Set(ref _projectName, value);
    }

    public string ProjectPath
    {
        get => _projectPath;
        private set => Set(ref _projectPath, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (Set(ref _isDirty, value))
                OnPropertyChanged(nameof(TitleBarLabel));
        }
    }

    public string TitleBarLabel => IsDirty ? $"{ProjectName} •" : ProjectName;

    public string TimelineSummary
    {
        get => _timelineSummary;
        private set => Set(ref _timelineSummary, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public IReadOnlyList<MediaEntryItem> MediaItems { get; private set; } = [];
    public IReadOnlyList<TrackRowItem> TrackRows { get; private set; } = [];
    public IReadOnlyList<TimelineTabItem> TimelineTabs { get; private set; } = [];

    public void RefreshFromSession()
    {
        var session = Session;
        if (session is null)
        {
            ProjectName = "No project";
            ProjectPath = "";
            IsDirty = false;
            TimelineSummary = "";
            MediaItems = [];
            TrackRows = [];
            TimelineTabs = [];
            NotifyLists();
            return;
        }

        var path = session.PackagePath ?? "";
        ProjectPath = path;
        ProjectName = string.IsNullOrEmpty(path)
            ? "Untitled Project"
            : Path.GetFileNameWithoutExtension(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        IsDirty = session.IsDirty;

        var active = session.ActiveTimeline;
        TimelineSummary = $"{active.Name}  ·  {active.Width}×{active.Height}  ·  {active.Fps} fps  ·  {active.TotalFrames} frames";

        TimelineCommands.EnsureOpenTimelineIds(session);
        var openIds = session.ProjectFile.OpenTimelineIds ?? session.ProjectFile.Timelines.Select(t => t.Id).ToList();
        var openCount = openIds.Count;
        TimelineTabs = openIds
            .Select(id => session.ProjectFile.Timelines.FirstOrDefault(t => t.Id == id))
            .Where(t => t is not null)
            .Select(t => new TimelineTabItem(
                t!.Id,
                t.Name,
                t.Id == session.ProjectFile.ActiveTimelineId,
                showClose: openCount > 1))
            .ToList<TimelineTabItem>();

        MediaItems = session.Manifest.Entries
            .Select(e => new MediaEntryItem(
                e.Id,
                string.IsNullOrWhiteSpace(e.Name) ? e.Id : e.Name,
                e.Type.ToString(),
                FormatDuration(e),
                session.Resolver.ResolvePath(e),
                e.Type is ClipType.video or ClipType.image,
                e.Duration,
                e.FolderId,
                e.Source is MediaSource.ExternalSource,
                e.ImportInput is not null ? "import_media" : e.GenerationInput?.Operation,
                e.GenerationStatus,
                isBlocked: session.Resolver.IsBlocked(e)))
            .ToList();

        TrackRows = active.Tracks
            .Select((t, i) => new TrackRowItem(
                Index: i,
                Label: $"{t.Type} {i + 1}",
                TypeName: t.Type.ToString(),
                ClipCount: t.Clips.Count,
                Muted: t.Muted,
                Hidden: t.Hidden))
            .ToList();

        StatusText = $"{MediaItems.Count} media  ·  {TrackRows.Count} tracks  ·  {active.TotalFrames} frames";
        NotifyLists();
    }

    private void NotifyLists()
    {
        OnPropertyChanged(nameof(MediaItems));
        OnPropertyChanged(nameof(TrackRows));
        OnPropertyChanged(nameof(TimelineTabs));
        OnPropertyChanged(nameof(TitleBarLabel));
    }

    private static string FormatDuration(MediaManifestEntry e)
    {
        if (e.Duration <= 0) return "—";
        var ts = TimeSpan.FromSeconds(e.Duration);
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MediaEntryItem : INotifyPropertyChanged
{
    public MediaEntryItem(string id, string name, string kind, string durationLabel, string? sourcePath, bool isVisual, double durationSeconds, string? folderId, bool isLinked = false, string? generationOperation = null, string? generationStatus = null, bool isBlocked = false)
    {
        IsLinked = isLinked;
        IsBlocked = isBlocked;
        Id = id;
        Name = name;
        Kind = kind;
        DurationLabel = durationLabel;
        SourcePath = sourcePath;
        IsVisual = isVisual;
        DurationSeconds = durationSeconds;
        FolderId = folderId;
        _generationOperation = generationOperation;
        _generationStatus = generationStatus;
    }

    public string Id { get; }
    public string Name { get; }
    public string Kind { get; }
    public string DurationLabel { get; }
    public string? SourcePath { get; }
    public bool IsVisual { get; }
    public double DurationSeconds { get; }
    public string? FolderId { get; }

    /// <summary>
    /// WP-160: true when the manifest points at the file where it already lives
    /// (<c>external.absolutePath</c>) rather than at a copy inside the package. The distinction is
    /// worth showing: a linked asset goes offline if the original moves, and the project is no
    /// longer self-contained.
    /// </summary>
    public bool IsLinked { get; }

    /// <summary>
    /// WP-399: a linked file outside the project that the user has not allowed this project to use. It
    /// reads as offline everywhere until the media panel's Review allows it.
    /// </summary>
    public bool IsBlocked { get; }

    /// <summary>The kind line under the name, plus the link marker when there is one.</summary>
    private readonly string? _generationOperation;
    private readonly string? _generationStatus;
    public string KindLabel => Temno.Core.Ui.ImageGenerationStatusLabel.Format(Kind, IsLinked, _generationOperation, _generationStatus, IsBlocked);

    /// <summary>Segoe MDL2 glyph for the media kind, shown until (or when no) thumbnail loads.</summary>
    public string TypeGlyph => Kind?.ToLowerInvariant() switch
    {
        "video" => "\uE714",
        "audio" => "\uE8D6",
        "image" => "\uEB9F",
        _ => "\uE8B9",
    };

    public Uri TypeIconUri => new Uri(Kind?.ToLowerInvariant() switch
    {
        "video" => "ms-appx:///Assets/icons/video.svg",
        "audio" => "ms-appx:///Assets/icons/audio.svg",
        "image" => "ms-appx:///Assets/icons/image.svg",
        _ => "ms-appx:///Assets/icons/file-generic.svg",
    });

    public Temno.App.Controls.Shared.IconDefinition TypeIcon => Kind?.ToLowerInvariant() switch
    {
        "video" => Temno.App.Controls.Shared.Icons.Video,
        "audio" => Temno.App.Controls.Shared.Icons.MusicNote2,
        "image" => Temno.App.Controls.Shared.Icons.Image,
        _ => Temno.App.Controls.Shared.Icons.Document,
    };

    private Microsoft.UI.Xaml.Media.ImageSource? _thumbnail;
    public Microsoft.UI.Xaml.Media.ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasThumbnail)));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record MediaFolderItem(string Id, string Name);

public sealed record MediaFolderDestination(string? Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record TrackRowItem(int Index, string Label, string TypeName, int ClipCount, bool Muted, bool Hidden);

public sealed class TimelineTabItem
{
    private static readonly Microsoft.UI.Xaml.Media.SolidColorBrush TransparentBrush =
        new(Microsoft.UI.Colors.Transparent);

    public TimelineTabItem(string id, string name, bool isActive, bool showClose = false, string details = "")
    {
        Id = id;
        Name = name;
        IsActive = isActive;
        ShowClose = showClose;
        Details = details;
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsActive { get; }
    public string Details { get; }
    /// <summary>Mac: × when open tab count &gt; 1 (on every open tab).</summary>
    public bool ShowClose { get; }
    public double ActiveOpacity => IsActive ? 1.0 : 0.72;
    public FontWeight ActiveWeight => IsActive ? FontWeights.SemiBold : FontWeights.Normal;
    public Microsoft.UI.Xaml.Media.Brush ActiveBackground => IsActive ? Temno.App.Theme.AppTheme.Background.ProminentBrush : TransparentBrush;
    public Microsoft.UI.Xaml.Media.Brush ActiveForeground => IsActive ? Temno.App.Theme.AppTheme.Text.PrimaryBrush : Temno.App.Theme.AppTheme.Text.SecondaryBrush;
    public Visibility ActiveIndicatorVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CloseVisibility => ShowClose ? Visibility.Visible : Visibility.Collapsed;
    public string TabToolTip => $"{Name}\nClick to switch · double-click to rename";
    public string CloseAutomationName => $"Close {Name}";
}
