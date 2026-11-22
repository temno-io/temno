using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Temno.App.Theme;
using Temno.App.Controls.Shared;
using Temno.Core.Ui;
using Temno.App.ViewModels;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Mcp;
using Temno.Media.Text;
using Windows.Foundation;
using ClipModel = Temno.Core.Models.Clip;
using TextAlign = Temno.Core.Models.TextAlignment;
using TransformModel = Temno.Core.Models.Transform;

namespace Temno.App.Controls;

/// <summary>
/// Wave D inspector: tabs, live commit, multi-clip batch, media asset, project settings,
/// keyframe stamp/nav, basic grade, text polish.
/// </summary>
public sealed partial class InspectorControl : UserControl
{
    private ProjectSession? _session;
    private string? _clipId;
    private List<string> _clipIds = [];
    // WP-183: when the selection is one video clip plus its linked audio, the pair is one logical
    // clip. The video clip is shown and edited; timing edits move both; Levels edit the partner.
    private string? _pairAudioId;
    private string? _mediaAssetPath;
    private bool _suppress;
    private bool _projectSettingsSuppress;
    private InspectorClipTab _activeTab = InspectorClipTab.Video;
    private bool _lastInspectedWasText;
    private readonly ClipLiveEdit _live = new();
    /// <summary>
    /// WP-269: whether a live gesture (a scrub, a pad/curve drag) is open. The editor's coalesced
    /// refresh defers while this is true, so a paused drag is one undo step, not two.
    /// </summary>
    public bool IsLiveEditActive => _live.IsActive;
    private readonly List<InspectorClipTab> _availableTabs = [];

    private int _playheadFrame;

    // Tracks a keyframe just auto-created by a numeric field commit (e.g. LostFocus when clicking
    // the adjacent ◆ stamp button) so RunKeyframe does not immediately toggle-delete it.
    private (string Property, int Frame, long Ticks)? _recentAutoStamped;

    // Keyframe diamond drag (lane strip)
    private bool _kfDragging;
    private string? _kfDragProperty;
    private int _kfDragFromOffset;
    private int _kfDragLiveOffset;
    private const double KfLaneLabelW = 28;

    // Color wheel pad drag
    private Canvas? _wheelPadActive;

    // RGB multi-point curve editor
    private GradeCurve _rgbCurve = new();
    private string _rgbCurveChannel = "master";
    private int _curveDragIndex = -1;

    // Hue multi-point curve editor
    private HueCurves _hueCurvesEdit = new();
    private string _hueCurveChannel = "hue";
    private int _hueDragIndex = -1;

    // WP-135: wheel pad intermediates previously stored in collapsed NumberBoxes
    private double _gradeLiftX;
    private double _gradeLiftY;
    private double _gradeGammaX;
    private double _gradeGammaY;
    private double _gradeGainX;
    private double _gradeGainY;

    /// <summary>Timeline playhead (absolute frames) for keyframe stamp/nav. Set by EditorPage.</summary>
    public int PlayheadFrame
    {
        get => _playheadFrame;
        set
        {
            if (_playheadFrame == value) return;
            // Flush any uncommitted typed value (or open arrow/wheel nudge) at the OLD playhead
            // frame before moving _playheadFrame so stepping/scrubbing never loses or mis-frames it.
            var committedPending = false;
            if (!_suppress && _session is not null && !string.IsNullOrEmpty(_clipId) && _clipIds.Count > 0)
                committedPending = CommitPendingNumericFields(raiseClipEdited: false);

            _playheadFrame = value;
            _recentAutoStamped = null;
            // Keep animatable numeric values, stamp diamonds, lane tick, and graph in sync while scrubbing.
            if (!_suppress && !string.IsNullOrEmpty(_clipId) && _clipIds.Count == 1)
            {
                var clip = FindClip(_clipId);
                if (clip is not null)
                {
                    if (!_live.IsActive)
                    {
                        var levels = _pairAudioId is { } pid && FindClip(pid) is { } partner ? partner : clip;
                        if (KeyframeCommands.HasAnyActiveTrack(clip)
                            || (!ReferenceEquals(levels, clip) && KeyframeCommands.HasAnyActiveTrack(levels)))
                        {
                            _suppress = true;
                            try
                            {
                                RefreshAnimatableFields(clip, [clip], multi: false);
                            }
                            finally
                            {
                                _suppress = false;
                            }
                        }
                    }
                    RefreshKeyframeStatus(clip);
                }
                if (IsKeyframesSectionVisible)
                    KeyframeGraph?.SetPlayhead(value);
            }

            if (committedPending)
                ClipEdited?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? ClipEdited;
    public event EventHandler? MediaLibraryChanged;
    /// <summary>Lightweight preview refresh during live scrub (no full timeline rebuild).</summary>
    public event EventHandler? ClipPreviewing;
    /// <summary>Raised when project settings change so the shell can rebind preview/timeline.</summary>
    public event EventHandler? ProjectSettingsChanged;
    /// <summary>Request playhead seek (keyframe nav).</summary>
    public event EventHandler<int>? SeekPlayheadRequested;

    public InspectorControl()
    {
        InitializeComponent();
        AiEdit.MediaLibraryChanged += (_, _) => MediaLibraryChanged?.Invoke(this, EventArgs.Empty);
        InitializeFieldSpecs();
        WireFieldEvents();
        WireTransitionFields();
        WireProjectSettingsFields();
        KeyframesSection.RegisterPropertyChangedCallback(SectionHeader.IsExpandedProperty, (_, _) =>
        {
            if (!IsKeyframesSectionVisible || string.IsNullOrEmpty(_clipId) || _clipIds.Count != 1) return;
            var clip = FindClip(_clipId);
            if (clip is not null)
                RefreshKeyframeLane(clip);
            KeyframeGraph?.SetPlayhead(_playheadFrame);
            KeyframeGraph?.Refresh();
        });
                EffectCatalog.ItemsSource = EffectRegistry.CanonicalOrder
            .Where(id => EffectRegistry.GetDescriptor(id) is not null)
            .Select(id => new EffectCatalogItem(id, EffectRegistry.GetCategory(id), EffectRegistry.GetDisplayName(id)))
            .ToList();
    }

    public void Bind(ProjectSession? session)
    {
        CommitPendingNumericFields(raiseClipEdited: false);
        EndLiveEdit(commit: true);
        _recentAutoStamped = null;
        HideTransition();
        _lastInspectedWasText = false;
        _session = session;
        RefreshProjectPanel();
        if (session is not null)
            ToolExecutor.ResumePendingGenerations(session);
    }

    public void ShowSelection(IReadOnlyCollection<string> clipIds, string? focusedClipId)
    {
        ArgumentNullException.ThrowIfNull(clipIds);
        var committedPending = CommitPendingNumericFields(raiseClipEdited: false);
        EndLiveEdit(commit: true);
        if (!string.Equals(_clipId, focusedClipId ?? clipIds.FirstOrDefault(), StringComparison.Ordinal))
            _recentAutoStamped = null;
        HideTransition();
        MediaAssetPanel.Visibility = Visibility.Collapsed;
        _pairAudioId = null;

        var classification = SelectionSemantics.Classify(
            clipIds.Select(id => FindClip(id)).OfType<ClipModel>()
                .Select(c => (c.Id, c.MediaType, c.LinkGroupId)).ToList(),
            focusedClipId);
        if (classification.Kind == SelectionSemantics.Kind.LinkedPair)
        {
            _pairAudioId = classification.AudioPartnerId;
            ShowClip(classification.PrimaryId);
            if (committedPending)
                ClipEdited?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (clipIds.Count > 1)
        {
            // WP-86b: batch-edit common properties across selection.
            _clipIds = clipIds.ToList();
            _clipId = focusedClipId ?? _clipIds[0];
            var primary = FindClip(_clipId);
            if (primary is null)
            {
                _lastInspectedWasText = false;
                EmptyLabel.Text = "Selected clips are no longer available.";
                EmptyLabel.Visibility = Visibility.Visible;
                ProjectPanel.Visibility = Visibility.Collapsed;
                FieldsPanel.Visibility = Visibility.Collapsed;
                HideAiEdit();
                if (committedPending)
                    ClipEdited?.Invoke(this, EventArgs.Empty);
                return;
            }

            EmptyLabel.Visibility = Visibility.Collapsed;
            ProjectPanel.Visibility = Visibility.Collapsed;
            FieldsPanel.Visibility = Visibility.Visible;
            MultiSelectBanner.Visibility = Visibility.Visible;
            MultiSelectBanner.Text =
                $"{_clipIds.Count} clips selected · edits apply to all (timing start is per focused clip). Mixed values show {MixedValue.Marker}.";
            FillClipFields(primary, multi: true);
            HideAiEdit();
            if (committedPending)
                ClipEdited?.Invoke(this, EventArgs.Empty);
            return;
        }

        _clipIds = clipIds.Count == 1 ? [clipIds.First()] : [];
        ShowClip(focusedClipId ?? clipIds.FirstOrDefault());
        if (committedPending)
            ClipEdited?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>WP-87: show library media metadata when a media list row is selected.</summary>
    public void ShowMediaAsset(MediaEntryItem? item)
    {
        EndLiveEdit(commit: true);
        if (item is null)
        {
            MediaAssetPanel.Visibility = Visibility.Collapsed;
            if (FieldsPanel.Visibility != Visibility.Visible)
                HideAiEdit();
            return;
        }

        // Only take over when no clip is selected.
        if (!string.IsNullOrEmpty(_clipId) && FieldsPanel.Visibility == Visibility.Visible)
            return;

        HideTransition();
        _lastInspectedWasText = false;
        EmptyLabel.Visibility = Visibility.Collapsed;
        ProjectPanel.Visibility = Visibility.Collapsed;
        FieldsPanel.Visibility = Visibility.Collapsed;
        MediaAssetPanel.Visibility = Visibility.Visible;
        MediaAssetName.Text = item.Name;
        MediaAssetKind.Text = item.Kind;
        MediaAssetDuration.Text = item.DurationLabel;
        _mediaAssetPath = string.IsNullOrEmpty(item.SourcePath) ? null : item.SourcePath;
        MediaAssetPath.Text = _mediaAssetPath ?? "—";
        ToolTipService.SetToolTip(MediaAssetPathTooltipHost, _mediaAssetPath);
        ToolTipService.SetToolTip(MediaAssetPath, _mediaAssetPath);
        var hasPath = !string.IsNullOrEmpty(_mediaAssetPath);
        MediaAssetPathActions.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
        RevealMediaPathBtn.IsEnabled = hasPath && (System.IO.File.Exists(_mediaAssetPath) || System.IO.Directory.Exists(_mediaAssetPath));
        CopyMediaPathLabel.Text = "Copy";
        MediaAssetId.Text = item.Id;
        ToolTipService.SetToolTip(MediaAssetName, $"{item.Name}\nId {item.Id}");
        BindAiEdit(item.Id, item.Kind);
    }

    public void ShowClip(string? clipId)
    {
        var committedPending = CommitPendingNumericFields(raiseClipEdited: false);
        EndLiveEdit(commit: true);
        if (!string.Equals(_clipId, clipId, StringComparison.Ordinal))
            _recentAutoStamped = null;
        HideTransition();
        _clipId = clipId;
        _clipIds = string.IsNullOrEmpty(clipId) ? [] : [clipId];
        MediaAssetPanel.Visibility = Visibility.Collapsed;
        MultiSelectBanner.Visibility = Visibility.Collapsed;
        if (_session is null || string.IsNullOrEmpty(clipId))
        {
            _lastInspectedWasText = false;
            EmptyLabel.Visibility = Visibility.Collapsed;
            ProjectPanel.Visibility = Visibility.Visible;
            FieldsPanel.Visibility = Visibility.Collapsed;
            HideAiEdit();
            RefreshProjectPanel();
            if (committedPending)
                ClipEdited?.Invoke(this, EventArgs.Empty);
            return;
        }

        var clip = FindClip(clipId);
        if (clip is null)
        {
            _lastInspectedWasText = false;
            EmptyLabel.Text = "The selected clip is no longer available.";
            EmptyLabel.Visibility = Visibility.Visible;
            ProjectPanel.Visibility = Visibility.Collapsed;
            FieldsPanel.Visibility = Visibility.Collapsed;
            HideAiEdit();
            if (committedPending)
                ClipEdited?.Invoke(this, EventArgs.Empty);
            return;
        }

        EmptyLabel.Visibility = Visibility.Collapsed;
        ProjectPanel.Visibility = Visibility.Collapsed;
        FieldsPanel.Visibility = Visibility.Visible;
        FillClipFields(clip, multi: false);
        BindAiEdit(clip.MediaRef, clip.MediaType.ToString());
        if (committedPending)
            ClipEdited?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildAvailableTabs(Clip clip)
    {
        _availableTabs.Clear();
        var includeMulticam = !string.IsNullOrEmpty(clip.MulticamGroupId)
            && _session is not null
            && MulticamCommands.FindGroup(_session, clip.MulticamGroupId) is not null;
        _availableTabs.AddRange(InspectorClipTabs.ForClip(
            clip.MediaType,
            !string.IsNullOrEmpty(clip.TextContent),
            includeMulticam));

        ResolvePreferredTab(clip);
        // A field's LostFocus commits before a tab receives Click. Keep its button alive
        // across that refresh so committing a value does not swallow the user's next action.
        if (TabBar.Children.OfType<ToggleButton>().Select(button => button.Tag)
            .SequenceEqual(_availableTabs.Cast<object>()))
        {
            foreach (var button in TabBar.Children.OfType<ToggleButton>())
                button.IsChecked = button.Tag is InspectorClipTab tab && tab == _activeTab;
            TabBarHost.Visibility = _availableTabs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        TabBar.Children.Clear();
        if (_availableTabs.Count <= 1)
        {
            TabBarHost.Visibility = Visibility.Collapsed;
            return;
        }

        TabBarHost.Visibility = Visibility.Visible;
        foreach (var tab in _availableTabs)
        {
            var btn = new ToggleButton
            {
                Content = TabTitle(tab),
                Tag = tab,
                Padding = AppTheme.Padding.MdLgXs,
                MinWidth = 64,
                IsChecked = tab == _activeTab,
            };
            AutomationProperties.SetName(btn, TabTitle(tab));
            AutomationProperties.SetAutomationId(btn, $"InspectorTab.{tab}");
            btn.Click += TabButton_Click;
            TabBar.Children.Add(btn);
        }
    }

    private static string TabTitle(InspectorClipTab tab) => tab switch
    {
        InspectorClipTab.Video => "Video",
        InspectorClipTab.Adjust => "Adjust",
        InspectorClipTab.Audio => "Audio",
        InspectorClipTab.Content => "Style",
        InspectorClipTab.Animation => "Animation",
        InspectorClipTab.Multicam => "Multicam",
        _ => tab.ToString(),
    };

    private void ResolvePreferredTab(Clip clip)
    {
        var hasText = !string.IsNullOrEmpty(clip.TextContent);
        var preferred = InspectorClipTabs.ResolvePreferred(
            _availableTabs,
            _activeTab,
            clip.MediaType,
            hasText,
            _lastInspectedWasText);
        if (preferred is { } tab)
            _activeTab = tab;
        _lastInspectedWasText = clip.MediaType == ClipType.text || hasText;
    }

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InspectorClipTab tab }) return;
        EndLiveEdit(commit: true);
        _activeTab = tab;
        foreach (var child in TabBar.Children)
        {
            if (child is ToggleButton tb)
                tb.IsChecked = tb.Tag is InspectorClipTab t && t == _activeTab;
        }
        ApplyTabVisibility();
    }

    private void ApplyTabVisibility()
    {
        bool Show(InspectorClipTab tab) => _availableTabs.Contains(tab) && _activeTab == tab;
        VideoTabPane.Visibility = Show(InspectorClipTab.Video) ? Visibility.Visible : Visibility.Collapsed;
        AdjustTabPane.Visibility = Show(InspectorClipTab.Adjust) ? Visibility.Visible : Visibility.Collapsed;
        AudioTabPane.Visibility = Show(InspectorClipTab.Audio) ? Visibility.Visible : Visibility.Collapsed;
        TextTabPane.Visibility = Show(InspectorClipTab.Content) ? Visibility.Visible : Visibility.Collapsed;
        TextAnimationTabPane.Visibility = Show(InspectorClipTab.Animation) ? Visibility.Visible : Visibility.Collapsed;
        MulticamTabPane.Visibility = Show(InspectorClipTab.Multicam) ? Visibility.Visible : Visibility.Collapsed;
        RefreshAiEditVisibility();
        if (IsKeyframesSectionVisible && !string.IsNullOrEmpty(_clipId) && _clipIds.Count == 1)
        {
            var clip = FindClip(_clipId);
            if (clip is not null)
                RefreshKeyframeLane(clip);
            KeyframeGraph?.SetPlayhead(_playheadFrame);
        }
    }

    private bool IsKeyframesSectionVisible
        => _availableTabs.Contains(InspectorClipTab.Video)
           && _activeTab == InspectorClipTab.Video
           && KeyframesSection?.IsExpanded == true;

    private void MulticamAngle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        MulticamSwitchBtn.IsEnabled = MulticamAngleBox.SelectedItem is ComboBoxItem { Tag: string };
    }

    private void MulticamSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || string.IsNullOrEmpty(_clipId)) return;
        if (MulticamAngleBox.SelectedItem is not ComboBoxItem { Tag: string angle }) return;

        var clip = FindClip(_clipId);
        if (clip?.MulticamGroupId is not { Length: > 0 } groupId) return;

        var durations = _session.Manifest.Entries.ToDictionary(m => m.Id, m => m.Duration, StringComparer.Ordinal);
        var result = MulticamCommands.ChangeCam(
            _session,
            groupId,
            clip.StartFrame,
            clip.EndFrame,
            angle,
            durations,
            out _);
        if (!result.Ok) return;

        ClipEdited?.Invoke(this, EventArgs.Empty);
        ShowClip(_clipId);
    }


    /// <summary>The transform the clip was placed with; unknown source dimensions fit as the full canvas.</summary>
    private TransformModel FitFor(ClipModel clip)
        => _session is null
            ? new TransformModel()
            : LayoutEngine.FitTransform(
                _session.Manifest.Entries.FirstOrDefault(e => e.Id == clip.MediaRef), _session.ActiveTimeline);

    /// <summary>
    /// Scale relative to the placed fit, so a freshly dropped clip reads 100% whatever its aspect.
    /// Mac shows the raw canvas-relative width (a fitted 9:16 still on 16:9 reads 32%); the owner
    /// asked for 100% on placement (2026-09-23). The stored transform is unchanged either way.
    /// Text clips scale via <see cref="TextStyle.FontScale"/> so 100% matches natural font size.
    /// </summary>
    private double ScalePercent(ClipModel clip) => ScalePercentAt(clip, PlayheadFrame);

    private double ScalePercentAt(ClipModel clip, int frame)
    {
        if (clip.MediaType == ClipType.text)
        {
            var style = ReadTextStyle(clip) ?? new TextStyle();
            var baseScale = style.FontScale > 0 ? style.FontScale : 1.0;
            if (clip.ScaleTrack is { IsActive: true } && clip.Transform.Width > 1e-6)
                return baseScale * (clip.SizeAt(frame).X / clip.Transform.Width) * 100;
            return baseScale * 100;
        }

        var fit = FitFor(clip);
        if (fit.Width <= 1e-6) return 100;
        var sampled = clip.ScaleTrack is { IsActive: true } ? clip.TransformAt(frame) : clip.Transform;
        return DrawnFor(clip, sampled).Width / fit.Width * 100;
    }

    private TransformModel TextBaseFit(ClipModel clip, TextStyle style)
    {
        var canvasW = Math.Max(1, _session?.ActiveTimeline.Width ?? 1920);
        var canvasH = Math.Max(1, _session?.ActiveTimeline.Height ?? 1080);
        var unscaled = new TextStyle
        {
            FontName = style.FontName,
            FontSize = style.FontSize,
            FontScale = 1.0,
            IsBold = style.IsBold,
            IsItalic = style.IsItalic,
            Alignment = style.Alignment,
        };
        return TextClipCommands.FitTransform(
            clip.TextContent ?? "", unscaled, canvasW, canvasH,
            clip.Transform.CenterX, clip.Transform.CenterY);
    }

    /// <summary>
    /// WP-405: what the clip draws, its picture fitted inside its box, so a box saved at another shape
    /// (a full-canvas box holding a portrait still) reads as the size it shows, not as 316%.
    /// </summary>
    private TransformModel DrawnFor(ClipModel clip, TransformModel? overrideTransform = null)
    {
        var transform = overrideTransform ?? clip.Transform;
        if (_session is null) return transform;
        return ClipRefit.SourceSize(_session.Manifest, _session.ProjectFile.Timelines, clip) is { } size
            ? ClipPlacement.FitInBox(
                transform, size.Width, size.Height, _session.ActiveTimeline.Width, _session.ActiveTimeline.Height)
            : transform;
    }

    private ClipModel? FindClip(string id)
    {
        if (_session is null) return null;
        foreach (var t in _session.ActiveTimeline.Tracks)
        {
            var c = t.Clips.FirstOrDefault(x => x.Id == id);
            if (c is not null) return c;
        }
        return null;
    }
    private TransitionBlockRef? _transitionRef;
    private bool _transitionExpandLinks = true;
    private TransitionDrag? _transitionEdit;
    private sealed record EffectCatalogItem(string Id, string Category, string DisplayName)
    {
        public string Label => $"{Category} · {DisplayName}";
    }

    private sealed record EffectIdTag(string EffectId);
    private sealed record EffectMoveTag(string EffectId, int DestinationIndex);

    private sealed class EffectParameterInputPair(string effectId, string key, Slider slider, UnitNumberField field)
    {
        public string EffectId { get; } = effectId;
        public string Key { get; } = key;
        public Slider Slider { get; } = slider;
        public UnitNumberField Field { get; } = field;
        public bool Syncing { get; set; }
    }

}
