using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Windows.Foundation;
using Windows.System;
using Colors = Microsoft.UI.Colors;

namespace Temno.App.Controls;

/// <summary>
/// WP-252. Fades and cut transitions as blocks on their tracks (Premiere's transition blocks).
/// Every block is drawn at all times, labelled with its name and length. A block's inner edge
/// drags to resize it, with snapping, a readout and the frame under the edge in the monitor.
/// Corner tabs on the hovered or selected clip start a new one, and right-click, Delete and
/// the Inspector edit it. The rules (rooms, windows, lengths) live in
/// <see cref="TransitionBlocks"/> and <see cref="TransitionCommands"/>; this file draws and routes.
/// </summary>
public sealed partial class TimelineControl
{
    private const double BlockEdgeHitPx = 5;
    private const double MinFadeBlockPx = 6;
    private const double MinCutBlockPx = 8;
    private const double BlockInsetPx = 3;
    private const double GripWidthPx = 2;
    private const double TabSize = 12;
    private const double TabLabelGap = 2;
    private const int BlockZIndex = 1;
    private const int EdgeHandleZIndex = 2;
    private const int TabZIndex = 4;
    private const int ReadoutZIndex = 5;

    private TransitionIndex? _transitionIndex;
    private IReadOnlyList<TransitionBlock> _transitionBlocks = [];
    private readonly List<BlockHit> _blockHits = [];
    private readonly Dictionary<int, List<UIElement>> _blockElements = new();
    private readonly Dictionary<string, double> _labelWidths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClipLabelSlot> _clipLabels = new(StringComparer.Ordinal);
    private TransitionBlockRef? _selectedTransition;
    private bool _selectedTransitionExpandLinks = true;
    private string? _hoverTabClipId;
    private readonly List<UIElement> _tabVisuals = [];
    private TransitionDrag? _transitionDrag;
    private bool _transitionDragExpandLinks = true;
    private int _transitionGrabOffset;
    private List<SnapEngine.SnapTarget> _transitionSnapTargets = [];
    private readonly SnapEngine.SnapState _transitionSnapState = new();
    private bool _transitionSelectionPending;
    private Border? _transitionReadout;
    private bool _resizeCursor;
    private KeyEventHandler? _transitionKeyHandler;

    /// <summary>The selected block, or null. A block and clips are never selected together.</summary>
    public TransitionBlockRef? SelectedTransition => _selectedTransition;

    /// <summary>Whether edits to <see cref="SelectedTransition"/> should also update its linked partner.</summary>
    public bool SelectedTransitionExpandLinks => _selectedTransitionExpandLinks;

    /// <summary>A block edge is being dragged; the frame is the one under the dragged edge.</summary>
    public event EventHandler<int>? TransitionPreviewing;

    /// <summary>The drag ended or was cancelled; the monitor goes back to the playhead.</summary>
    public event EventHandler? TransitionPreviewEnded;

    /// <summary>Blocks and tabs react to the pointer only with the Select tool; Trim and Blade pass through.</summary>
    private static bool TransitionsInteractive => EditorEditContext.Current.ToolMode == EditorToolMode.Select;

    // ---- Drawing ----

    private void BeginTransitionPass(Timeline timeline)
    {
        _transitionIndex = new TransitionIndex(timeline);
        _transitionBlocks = TransitionBlocks.ForTimeline(_transitionIndex);
        _blockHits.Clear();
        _blockElements.Clear();
        _clipLabels.Clear();
    }

    /// <summary>How far in from one clip edge its name must keep: past that edge's block and its corner tab.</summary>
    private double ClipLabelInset(Clip clip, bool tail = false)
    {
        var inset = TransitionBlocks.SupportsFade(clip.MediaType) ? TabSize + TabLabelGap : 0;
        if (_transitionIndex is null) return inset;
        var used = tail ? TransitionBlocks.TailUsed(_transitionIndex, clip) : TransitionBlocks.HeadUsed(_transitionIndex, clip);
        return Math.Max(inset, used * _pixelsPerFrame);
    }

    /// <summary>Places a clip's name in the room its blocks leave; hides it when that room is too narrow.</summary>
    private void PlaceClipLabel(Clip clip, ClipLabelSlot slot)
    {
        var left = Math.Max(slot.Vx, slot.Cx + ClipLabelInset(clip)) + 2;
        var right = Math.Min(slot.Vx + slot.Vw, slot.Cx + slot.Cw - ClipLabelInset(clip, tail: true)) - 2;
        slot.Label.MaxWidth = Math.Max(2, right - left);
        slot.Label.Visibility = right - left > 16 ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(slot.Label, LocalX(left));
    }

    /// <summary>A block drag or an Inspector scrub moves blocks without a lane rebuild; the names follow.</summary>
    private void ReplaceClipLabels(IEnumerable<string> clipIds)
    {
        foreach (var id in clipIds)
        {
            if (_clipLabels.TryGetValue(id, out var slot) && FindClip(id) is { } clip)
                PlaceClipLabel(clip, slot);
        }
    }

    /// <summary>True when a cut transition owns this clip edge; its trim handle then stays hidden.</summary>
    private bool EdgeHasCut(Clip clip, bool left)
        => _transitionIndex is not null
           && (left ? _transitionIndex.AtHead(clip) : _transitionIndex.AtTail(clip)) is not null;

    private void DrawTransitionBlocks(int trackIndex)
    {
        _blockHits.RemoveAll(hit => hit.Block.TrackIndex == trackIndex);
        if (_blockElements.TryGetValue(trackIndex, out var old))
        {
            foreach (var element in old)
                LaneCanvas.Children.Remove(element);
        }

        var list = new List<UIElement>();
        var viewRight = _viewLeft + _viewWidth;
        foreach (var block in _transitionBlocks)
        {
            if (block.TrackIndex != trackIndex) continue;
            var (x, w) = BlockSpan(block);
            var (_, y, _, h) = _geo.ClipRect(block.Clip, trackIndex);
            _blockHits.Add(new BlockHit(block, x, y, w, h));
            if (x + w < _viewLeft - 64 || x > viewRight + 64) continue;
            var element = MakeBlock(block, x, y, w, h);
            LaneCanvas.Children.Add(element);
            list.Add(element);
        }
        _blockElements[trackIndex] = list;
    }

    /// <summary>
    /// Content-space span. A block narrower than its minimum is widened from the clip edge (fade)
    /// or around the cut (cut transition), so a one-frame fade is still a visible target.
    /// </summary>
    private (double X, double W) BlockSpan(TransitionBlock block)
    {
        var x = block.StartFrame * _pixelsPerFrame;
        var w = block.Frames * _pixelsPerFrame;
        var min = block.IsCut ? MinCutBlockPx : MinFadeBlockPx;
        if (w >= min) return (x, w);
        var clipW = Math.Max(1, block.Clip.DurationFrames * _pixelsPerFrame);
        return block.Kind switch
        {
            TransitionBlockKind.FadeIn => (block.Clip.StartFrame * _pixelsPerFrame, Math.Min(min, clipW)),
            TransitionBlockKind.FadeOut => ((block.Clip.EndFrame * _pixelsPerFrame) - Math.Min(min, clipW), Math.Min(min, clipW)),
            _ => ((block.AnchorFrame * _pixelsPerFrame) - (min / 2), min),
        };
    }

    private FrameworkElement MakeBlock(TransitionBlock block, double x, double y, double w, double h)
    {
        var selected = _selectedTransition == block.Ref;
        var locked = IsTrackLocked(block.TrackIndex);
        var audio = block.Clip.MediaType == ClipType.audio;
        var fps = Math.Max(1, _session?.ActiveTimeline.Fps ?? 30);
        var root = new Grid
        {
            Width = Math.Max(1, w),
            Height = Math.Max(4, h),
            // A transparent brush keeps the whole block hit-testable; the slab alone would be too.
            Background = new SolidColorBrush(Colors.Transparent),
            IsHitTestVisible = TransitionsInteractive && !locked,
            Tag = new BlockTag(block.Ref, block.TrackIndex),
        };

        root.Children.Add(new Border
        {
            Background = audio ? AppTheme.Transition.AudioFillBrush : AppTheme.Transition.FillBrush,
            BorderBrush = selected ? AppTheme.Accent.BrandBrush : AppTheme.Transition.StrokeBrush,
            BorderThickness = new Thickness(selected ? AppTheme.BorderWidth.Thick : AppTheme.BorderWidth.Thin),
            CornerRadius = new CornerRadius(AppTheme.Radius.XsSm),
            IsHitTestVisible = false,
        });
        root.Children.Add(MakeBlockCurve(block, w, h));

        foreach (var right in new[] { false, true })
        {
            if (!block.Resizes(right) || w < GripWidthPx * 4) continue;
            root.Children.Add(new Rectangle
            {
                Width = GripWidthPx,
                Height = Math.Max(2, h - (BlockInsetPx * 2)),
                Fill = selected ? AppTheme.Accent.ReadableBrush : AppTheme.Transition.GripIdleBrush,
                HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(right ? 0 : GripWidthPx, 0, right ? GripWidthPx : 0, 0),
                IsHitTestVisible = false,
            });
        }

        if (BlockLabel(block, fps, w) is { } text)
        {
            root.Children.Add(new Border
            {
                Background = AppTheme.Transition.LabelChipBrush,
                CornerRadius = new CornerRadius(AppTheme.Radius.Xs),
                Padding = AppTheme.Padding.HXs,
                Height = AppTheme.Spacing.Lg,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(BlockInsetPx + GripWidthPx, BlockInsetPx, 0, 0),
                MaxWidth = Math.Max(1, w - (BlockInsetPx * 2) - (GripWidthPx * 2)),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = AppTheme.FontSize.Xs,
                    FontFamily = AppTheme.FontFamily.Ui,
                    Foreground = AppTheme.Text.PrimaryBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            });
        }

        var description = TransitionBlocks.Describe(block, fps);
        AutomationProperties.SetName(root, $"{description}, {BlockPlace(block)}");
        AutomationProperties.SetAutomationId(root, $"Transition.{block.Kind}.{block.Ref.Id}");
        ToolTipService.SetToolTip(root, block.IsCut
            ? $"{description}\nDrag either edge to change the length. Right-click for the type or to remove it."
            : $"{description}\nDrag the inner edge to change the length. Right-click for Linear, Smooth or Remove.");
        root.PointerPressed += Block_PointerPressed;
        Canvas.SetLeft(root, LocalX(x));
        Canvas.SetTop(root, y);
        Canvas.SetZIndex(root, BlockZIndex);
        return root;
    }

    /// <summary>
    /// A fade draws its own ramp (the multiplier the renderer uses). A cut transition draws an X,
    /// the outgoing clip going down as the incoming one comes up, or a V for a dip through a colour.
    /// </summary>
    private static UIElement MakeBlockCurve(TransitionBlock block, double w, double h)
    {
        var canvas = new Canvas { IsHitTestVisible = false };
        var top = BlockInsetPx + 1;
        var bottom = Math.Max(top + 1, h - BlockInsetPx - 1);
        Polyline Line(PointCollection points) => new()
        {
            Points = points,
            Stroke = AppTheme.Transition.CurveBrush,
            StrokeThickness = AppTheme.BorderWidth.Medium,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
        };

        if (block.IsCut)
        {
            var dip = block.Transition?.Type is TransitionType.FadeToBlack or TransitionType.FadeToWhite;
            if (dip)
            {
                canvas.Children.Add(Line([new Point(0, top), new Point(w / 2, bottom), new Point(w, top)]));
            }
            else
            {
                canvas.Children.Add(Line([new Point(0, top), new Point(w, bottom)]));
                canvas.Children.Add(Line([new Point(0, bottom), new Point(w, top)]));
            }
            return canvas;
        }

        var edge = block.Ref.Edge;
        var ramp = new PointCollection();
        foreach (var sample in FadeRamp.Samples(block.Clip, edge))
        {
            var along = sample.T * w;
            var px = edge == FadeEdge.In ? along : w - along;
            ramp.Add(new Point(px, bottom - (sample.Multiplier * (bottom - top))));
        }
        if (ramp.Count >= 2) canvas.Children.Add(Line(ramp));
        return canvas;
    }

    /// <summary>The longest label that fits: name and length, then the name, then the length.</summary>
    private string? BlockLabel(TransitionBlock block, int fps, double w)
    {
        // One pixel of slack: the measured width is the text's, the chip rounds its layout.
        var room = w - (BlockInsetPx * 2) - (GripWidthPx * 2) - AppTheme.Padding.HXs.Left - AppTheme.Padding.HXs.Right - 1;
        var full = TransitionBlocks.Describe(block, fps);
        if (LabelWidth(full) <= room) return full;
        if (LabelWidth(block.Name) <= room) return block.Name;
        var seconds = TransitionBlocks.FormatSeconds(block.Frames, fps);
        return LabelWidth(seconds) <= room ? seconds : null;
    }

    private double LabelWidth(string text)
    {
        if (_labelWidths.TryGetValue(text, out var width)) return width;
        // Detached, the probe misses the implicit TextBlock style, so the face is set as on the label.
        var probe = new TextBlock { Text = text, FontSize = AppTheme.FontSize.Xs, FontFamily = AppTheme.FontFamily.Ui };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        width = probe.DesiredSize.Width;
        if (!double.IsFinite(width) || width <= 0) width = text.Length * AppTheme.FontSize.Xs * 0.6;
        _labelWidths[text] = width;
        return width;
    }

    private string BlockPlace(TransitionBlock block)
        => block.ClipB is { } b
            ? $"between {ClipLabel(block.Clip)} and {ClipLabel(b)}"
            : $"on {ClipLabel(block.Clip)}";

    private void RestyleTransitionBlocks()
    {
        foreach (var track in _blockElements.Keys.ToList())
            DrawTransitionBlocks(track);
    }

    // ---- Corner tabs ----

    private void RedrawTransitionTabs()
    {
        foreach (var element in _tabVisuals)
            OverlayCanvas.Children.Remove(element);
        _tabVisuals.Clear();
        if (_session is null || _transitionIndex is null || _dragKind != DragKind.None || !TransitionsInteractive)
            return;

        var clipIds = new HashSet<string>(_selectedClipIds, StringComparer.Ordinal);
        if (_hoverTabClipId is not null) clipIds.Add(_hoverTabClipId);
        if (clipIds.Count == 0) return;

        var drawn = new HashSet<(string, FadeEdge)>();
        var timeline = _session.ActiveTimeline;
        foreach (var id in clipIds)
        {
            var track = TrackIndexOf(id);
            if (track < 0 || timeline.Tracks[track].Locked) continue;
            foreach (var corner in TransitionBlocks.Corners(timeline, track))
            {
                if (!string.Equals(corner.Clip.Id, id, StringComparison.Ordinal)) continue;
                if (!TransitionBlocks.IsFree(_transitionIndex, corner)) continue;
                AddTab(corner, drawn);
                // A cut belongs to both clips: show the other half of its notch too.
                if (corner.Neighbour is { } other)
                {
                    var facing = TransitionBlocks.Corner(timeline, other.Id, corner.Edge == FadeEdge.In ? FadeEdge.Out : FadeEdge.In);
                    if (facing is { } f) AddTab(f, drawn);
                }
            }
        }
    }

    private void AddTab(ClipCorner corner, HashSet<(string, FadeEdge)> drawn)
    {
        if (!drawn.Add((corner.Clip.Id, corner.Edge))) return;
        var (cx, cy, cw, _) = _geo.ClipRect(corner.Clip, corner.TrackIndex);
        if (cw < (TabSize * 2) + (EdgeHitPx * 2)) return;
        var x = corner.Edge == FadeEdge.In ? cx : cx + cw - TabSize;
        if (x + TabSize < _viewLeft || x > _viewLeft + _viewWidth) return;

        var head = corner.Edge == FadeEdge.In;
        var tab = new Canvas
        {
            Width = TabSize,
            Height = TabSize,
            Background = new SolidColorBrush(Colors.Transparent),
            Tag = new TabTag(corner.Clip.Id, corner.Edge),
        };
        tab.Children.Add(new Polygon
        {
            Points = head
                ? [new Point(0, 0), new Point(TabSize, 0), new Point(0, TabSize)]
                : [new Point(0, 0), new Point(TabSize, 0), new Point(TabSize, TabSize)],
            Fill = AppTheme.Transition.TabFillBrush,
            Stroke = AppTheme.Transition.TabStrokeBrush,
            StrokeThickness = AppTheme.BorderWidth.Hairline,
            IsHitTestVisible = false,
        });

        var what = corner.CreatesCut ? TransitionBlocks.DisplayName(TransitionBlocks.DefaultType) : head ? "fade in" : "fade out";
        var name = corner.Pair is { } pair
            ? $"Add {what} between {ClipLabel(pair.A)} and {ClipLabel(pair.B)}"
            : $"Add {what} to {ClipLabel(corner.Clip)}";
        AutomationProperties.SetName(tab, name);
        AutomationProperties.SetAutomationId(tab, $"TransitionTab.{corner.Edge}.{corner.Clip.Id}");
        ToolTipService.SetToolTip(tab, corner.CreatesCut
            ? $"Drag to add a {what}. Ctrl+D adds one at the playhead."
            : $"Drag inward to add a {what}. Ctrl+D adds one at the playhead.");
        tab.PointerPressed += Tab_PointerPressed;
        tab.PointerEntered += (_, _) => SetResizeCursor(true);
        tab.PointerExited += (_, _) => { if (_dragKind == DragKind.None) SetResizeCursor(false); };
        Canvas.SetLeft(tab, LocalX(x));
        Canvas.SetTop(tab, cy);
        Canvas.SetZIndex(tab, TabZIndex);
        OverlayCanvas.Children.Add(tab);
        _tabVisuals.Add(tab);
    }

    private void UpdateTransitionHover(Point lanePoint)
    {
        if (_session is null || !TransitionsInteractive)
        {
            ClearTransitionHover();
            return;
        }

        SetResizeCursor(HitResizeEdge(lanePoint) is not null);
        string? clipId = TryClipAt(lanePoint, out var clip, out var ti)
            && !IsTrackLocked(ti)
            && TransitionBlocks.SupportsFade(clip.MediaType)
            ? clip.Id
            : null;
        if (string.Equals(clipId, _hoverTabClipId, StringComparison.Ordinal)) return;
        _hoverTabClipId = clipId;
        RedrawTransitionTabs();
    }

    private void ClearTransitionHover()
    {
        if (_dragKind == DragKind.Transition) return;
        SetResizeCursor(false);
        if (_hoverTabClipId is null) return;
        _hoverTabClipId = null;
        RedrawTransitionTabs();
    }

    // ---- Hit testing ----

    /// <summary>
    /// The resize edge under the pointer: within <see cref="BlockEdgeHitPx"/> of a block's
    /// resizable edge and inside its height. A fade's outer edge is the clip's own edge, so the
    /// clip's trim zone there wins over it.
    /// </summary>
    private (TransitionBlock Block, bool RightEdge)? HitResizeEdge(Point lanePoint)
    {
        var x = ContentX(lanePoint.X);
        (TransitionBlock Block, bool RightEdge)? best = null;
        var bestDistance = double.MaxValue;
        foreach (var hit in _blockHits)
        {
            if (IsTrackLocked(hit.Block.TrackIndex)) continue;
            if (lanePoint.Y < hit.Y || lanePoint.Y >= hit.Y + hit.H) continue;
            foreach (var right in new[] { false, true })
            {
                if (!hit.Block.Resizes(right)) continue;
                var edgeX = right ? hit.X + hit.W : hit.X;
                var distance = Math.Abs(x - edgeX);
                if (distance > BlockEdgeHitPx || distance >= bestDistance) continue;
                if (InOwnTrimZone(hit.Block, x)) continue;
                best = (hit.Block, right);
                bestDistance = distance;
            }
        }
        return best;
    }

    private bool InOwnTrimZone(TransitionBlock block, double contentX)
    {
        if (block.IsCut) return false;
        var clipX = block.Clip.StartFrame * _pixelsPerFrame;
        var clipRight = block.Clip.EndFrame * _pixelsPerFrame;
        return block.Kind == TransitionBlockKind.FadeIn
            ? contentX <= clipX + EdgeHitPx
            : contentX >= clipRight - EdgeHitPx;
    }

    private TransitionBlock? BlockAt(Point lanePoint)
    {
        var x = ContentX(lanePoint.X);
        foreach (var hit in _blockHits)
        {
            if (IsTrackLocked(hit.Block.TrackIndex)) continue;
            if (x >= hit.X && x < hit.X + hit.W && lanePoint.Y >= hit.Y && lanePoint.Y < hit.Y + hit.H)
                return hit.Block;
        }
        return null;
    }

    // ---- Pointer ----

    private void Block_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null || sender is not FrameworkElement { Tag: BlockTag tag } anchor) return;
        e.Handled = true;
        if (_dragKind != DragKind.None || IsTrackLocked(tag.TrackIndex)) return;
        var props = e.GetCurrentPoint(LaneCanvas).Properties;
        var point = e.GetCurrentPoint(LaneCanvas).Position;
        var block = TransitionBlocks.Find(_transitionIndex ?? new TransitionIndex(_session.ActiveTimeline), tag.Ref);
        if (block is null) return;

        if (props.IsRightButtonPressed)
        {
            var at = e.GetCurrentPoint(anchor).Position;
            var expand = ResolveTransitionExpandLinks(tag.Ref);
            SelectTransition(tag.Ref, expandLinks: expand);
            // Selecting redraws the blocks, so the pressed element may have left the tree; a flyout
            // shown at a detached element never opens. Its replacement sits in the same place.
            ShowTransitionMenu(BlockElement(tag.Ref, tag.TrackIndex) ?? anchor, at, block);
            return;
        }
        if (!props.IsLeftButtonPressed) return;

        // The fade's outer edge is its clip's trim edge.
        if (InOwnTrimZone(block, ContentX(point.X)))
        {
            if (AltIgnoresLinks() || !_selectedClipIds.Contains(block.Clip.Id))
                SetSelectedClips(LinkedUnlessAlt(block.Clip.Id), block.Clip.Id);
            var left = block.Kind == TransitionBlockKind.FadeIn;
            BeginDrag(left ? DragKind.TrimLeft : DragKind.TrimRight, new ClipTag(block.Clip.Id, tag.TrackIndex), FrameAt(point.X));
            if (_dragKind != DragKind.None) LaneCanvas.CapturePointer(e.Pointer);
            return;
        }

        if (TryBeginTransitionResize(point, e)) return;
        SelectTransition(tag.Ref);
    }

    private void Tab_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null || sender is not FrameworkElement { Tag: TabTag tag }) return;
        e.Handled = true;
        if (_dragKind != DragKind.None || !e.GetCurrentPoint(LaneCanvas).Properties.IsLeftButtonPressed) return;
        if (TransitionBlocks.Corner(_session.ActiveTimeline, tag.ClipId, tag.Edge) is not { } corner) return;
        var expand = ShouldExpandLinksForClip(corner.Clip.Id);
        var drag = TransitionDrag.BeginCreate(_session, corner, expandLinks: expand);
        if (drag is null) return;
        var point = e.GetCurrentPoint(LaneCanvas).Position;
        StartTransitionDrag(drag, BlockFrameAt(point.X) - corner.Frame, e, expand);
        UpdateTransitionDrag(point);
    }

    /// <summary>Starts a resize when the pointer is on a block's resize edge.</summary>
    private bool TryBeginTransitionResize(Point lanePoint, PointerRoutedEventArgs e)
    {
        if (_session is null || _dragKind != DragKind.None || !TransitionsInteractive) return false;
        if (!e.GetCurrentPoint(LaneCanvas).Properties.IsLeftButtonPressed) return false;
        if (HitResizeEdge(lanePoint) is not { } hit) return false;
        var expand = ResolveTransitionExpandLinks(hit.Block.Ref);
        var drag = TransitionDrag.BeginResize(_session, hit.Block.Ref, hit.RightEdge, expandLinks: expand);
        if (drag is null) return false;
        SelectTransition(hit.Block.Ref, deferNotify: true, expandLinks: expand);
        StartTransitionDrag(drag, BlockFrameAt(lanePoint.X) - drag.EdgeFrame, e, expand);
        return true;
    }

    private void StartTransitionDrag(TransitionDrag drag, int grabOffset, PointerRoutedEventArgs e, bool expandLinks)
    {
        _transitionDrag = drag;
        _transitionDragExpandLinks = expandLinks;
        _transitionGrabOffset = grabOffset;
        _dragKind = DragKind.Transition;
        _transitionSnapState.CurrentlySnappedTo = null;
        _transitionSnapTargets = TransitionSnapTargets(drag);
        ClearTabsForDrag();
        SetResizeCursor(true);
        HookTransitionKeys(true);
        LaneCanvas.CapturePointer(e.Pointer);
    }

    private void UpdateTransitionDrag(Point lanePoint)
    {
        var drag = _transitionDrag;
        if (drag is null || _session is null) return;

        var candidate = BlockFrameAt(lanePoint.X) - _transitionGrabOffset;
        var snapped = false;
        if (EditorEditContext.Current.SnapEnabled
            && SnapEngine.FindSnap(candidate, _transitionSnapTargets, _transitionSnapState, SnapEngine.ThresholdPixels, _pixelsPerFrame) is { } snap)
        {
            candidate = snap.Frame;
            snapped = true;
        }

        drag.DragTo(candidate);
        if (snapped && drag.EdgeFrame == candidate)
            ShowSnapIndicator(candidate * _pixelsPerFrame);
        else
            HideSnapIndicator();

        RefreshDraggedBlocks(drag);
        ShowTransitionReadout(lanePoint, drag);
        TransitionPreviewing?.Invoke(this, drag.PreviewFrame);
    }

    /// <summary>Redraws only the tracks the drag touches: no lane rebuild while the pointer moves.</summary>
    private void RefreshDraggedBlocks(TransitionDrag drag)
    {
        if (_session is null) return;
        _transitionIndex = new TransitionIndex(_session.ActiveTimeline);
        _transitionBlocks = TransitionBlocks.ForTimeline(_transitionIndex);
        if (drag.Ref is { } current) _selectedTransition = current;
        foreach (var track in drag.ClipIds.Select(TrackIndexOf).Where(t => t >= 0).Distinct())
            DrawTransitionBlocks(track);
        ReplaceClipLabels(drag.ClipIds);
    }

    /// <summary>
    /// Redraws every track's blocks from the live timeline without a lane rebuild — the Inspector's
    /// duration scrub mutates silently, so the timeline would otherwise show the old length until release.
    /// </summary>
    public void RedrawTransitionBlocks()
    {
        if (_session is null || _transitionDrag is not null) return;
        var timeline = _session.ActiveTimeline;
        _transitionIndex = new TransitionIndex(timeline);
        _transitionBlocks = TransitionBlocks.ForTimeline(_transitionIndex);
        foreach (var track in _blockElements.Keys.Concat(_transitionBlocks.Select(b => b.TrackIndex)).Distinct().ToList())
            DrawTransitionBlocks(track);
        ReplaceClipLabels(_clipLabels.Keys.ToList());
    }

    private void CommitTransitionDrag()
    {
        var drag = _transitionDrag;
        var dragExpand = _transitionDragExpandLinks;
        EndTransitionGesture();
        if (drag is null) return;
        var made = drag.Ref;
        var kept = drag.Commit();
        TransitionPreviewEnded?.Invoke(this, EventArgs.Empty);
        if (kept && made is not null)
        {
            SelectTransition(made, deferNotify: true, expandLinks: dragExpand);
            _transitionSelectionPending = false;
            NotifyEdited();
            SelectionChanged?.Invoke(this, null);
            return;
        }

        // Nothing changed (a click on an edge, or a new block released at zero length).
        if (!kept && drag.Ref is null && _selectedTransition is { } selected && TransitionBlocks.Find(_session!.ActiveTimeline, selected) is null)
            _selectedTransition = null;
        Rebuild();
        if (_transitionSelectionPending)
        {
            _transitionSelectionPending = false;
            SelectionChanged?.Invoke(this, null);
        }
    }

    private void CancelTransitionDrag()
    {
        var drag = _transitionDrag;
        EndTransitionGesture();
        drag?.Cancel();
        TransitionPreviewEnded?.Invoke(this, EventArgs.Empty);
        if (_selectedTransition is { } selected && _session is not null && TransitionBlocks.Find(_session.ActiveTimeline, selected) is null)
            _selectedTransition = null;
        Rebuild();
        if (_transitionSelectionPending)
        {
            _transitionSelectionPending = false;
            SelectionChanged?.Invoke(this, null);
        }
    }

    private void EndTransitionGesture()
    {
        _transitionDrag = null;
        _dragKind = DragKind.None;
        _transitionSnapState.CurrentlySnappedTo = null;
        HideSnapIndicator();
        HideTransitionReadout();
        SetResizeCursor(false);
        HookTransitionKeys(false);
        LaneCanvas.ReleasePointerCaptures();
    }

    private void ClearTabsForDrag()
    {
        foreach (var element in _tabVisuals)
            OverlayCanvas.Children.Remove(element);
        _tabVisuals.Clear();
    }

    /// <summary>Playhead, every clip edge and every marker; a transition may end on any of them.</summary>
    /// <summary>
    /// The playhead, markers, beats and every other clip's edges. The dragged clips' own edges are
    /// left out, and so is anything on the block's anchor: snapping there means a zero-length block,
    /// which read as a dead zone at the start of every new fade.
    /// </summary>
    private List<SnapEngine.SnapTarget> TransitionSnapTargets(TransitionDrag drag)
    {
        if (_session is null) return [];
        var timeline = _session.ActiveTimeline;
        var fps = Math.Max(1, timeline.Fps);
        var targets = SnapEngine.CollectTargets(
            timeline.Tracks,
            _playheadFrame,
            excludeClipIds: drag.ClipIds.ToHashSet(StringComparer.Ordinal),
            includePlayhead: true,
            beatFrames: clip => BeatSnapFrames(clip, fps),
            includeExcludedClipBeats: true);
        foreach (var marker in timeline.Markers)
            targets.Add(new SnapEngine.SnapTarget(marker.Frame, SnapEngine.Kind.ClipEdge));
        targets.RemoveAll(target => target.Frame == drag.AnchorFrame);
        return targets;
    }

    private int BlockFrameAt(double laneX)
        => (int)Math.Round(ContentX(laneX) / Math.Max(_pixelsPerFrame, 0.0001), MidpointRounding.AwayFromZero);

    private void ShowTransitionReadout(Point lanePoint, TransitionDrag drag)
    {
        var fps = Math.Max(1, _session?.ActiveTimeline.Fps ?? 30);
        var name = drag.Ref is { } current && _transitionIndex is not null && TransitionBlocks.Find(_transitionIndex, current) is { } block
            ? block.Name
            : drag.IsCut ? TransitionBlocks.DisplayName(TransitionBlocks.DefaultType) : "Fade";
        var text = drag.Frames <= 0
            ? $"{name} · drag to set the length"
            : $"{name} · {TransitionBlocks.FormatSeconds(drag.Frames, fps)} · {drag.Frames} f{(drag.Frames >= drag.MaxFrames ? " · longest" : "")}";
        _transitionReadout ??= new Border
        {
            Background = AppTheme.Background.ProminentBrush,
            BorderBrush = AppTheme.Border.PrimaryBrush,
            BorderThickness = new Thickness(AppTheme.BorderWidth.Thin),
            CornerRadius = new CornerRadius(AppTheme.Radius.Xs),
            Padding = AppTheme.Padding.SmXxs,
            IsHitTestVisible = false,
            Child = new TextBlock { FontSize = AppTheme.FontSize.Xs, Foreground = AppTheme.Text.PrimaryBrush },
        };
        if (_transitionReadout.Child is TextBlock readout) readout.Text = text;
        AutomationProperties.SetName(_transitionReadout, text);
        if (!OverlayCanvas.Children.Contains(_transitionReadout))
        {
            Canvas.SetZIndex(_transitionReadout, ReadoutZIndex);
            OverlayCanvas.Children.Add(_transitionReadout);
        }
        Canvas.SetLeft(_transitionReadout, lanePoint.X + AppTheme.Spacing.Md);
        Canvas.SetTop(_transitionReadout, Math.Max(_geo.RulerHeight, lanePoint.Y - AppTheme.Spacing.Xl));
    }

    private void HideTransitionReadout()
    {
        if (_transitionReadout is not null)
            OverlayCanvas.Children.Remove(_transitionReadout);
    }

    private void SetResizeCursor(bool on)
    {
        if (EditorEditContext.Current.ToolMode == EditorToolMode.Blade)
        {
            _resizeCursor = false;
            return;
        }
        if (_resizeCursor == on) return;
        _resizeCursor = on;
        ProtectedCursor = InputSystemCursor.Create(on ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.Arrow);
    }

    private void HookTransitionKeys(bool on)
    {
        if (XamlRoot?.Content is not UIElement root) return;
        _transitionKeyHandler ??= OnTransitionDragKeyDown;
        if (on)
            root.AddHandler(UIElement.PreviewKeyDownEvent, _transitionKeyHandler, true);
        else
            root.RemoveHandler(UIElement.PreviewKeyDownEvent, _transitionKeyHandler);
    }

    private void OnTransitionDragKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || _transitionDrag is null) return;
        CancelTransitionDrag();
        e.Handled = true;
    }

    // ---- Selection, menu, commands ----

    private bool ResolveTransitionExpandLinks(TransitionBlockRef? blockRef)
    {
        if (blockRef is not { } r) return true;
        if (AltIgnoresLinks()) return false;
        if (_selectedTransition == r)
            return _selectedTransitionExpandLinks;
        if (r.IsFade && !string.IsNullOrEmpty(r.Id))
            return ShouldExpandLinksForClip(r.Id);
        return true;
    }

    /// <summary>
    /// Selects a block (or none) and clears the clip selection. <paramref name="deferNotify"/> is for
    /// a press that starts a drag: the lane rebuild and the Inspector wait for the release, so the
    /// grab does not stall.
    /// </summary>
    private void SelectTransition(TransitionBlockRef? blockRef, bool deferNotify = false, bool? expandLinks = null)
    {
        var resolvedExpand = blockRef is null || (expandLinks ?? ResolveTransitionExpandLinks(blockRef));
        var clipsCleared = blockRef is not null && _selectedClipIds.Count > 0;
        if (clipsCleared)
            ApplySelection([], null, notify: false, rebuild: false);
        var expandChanged = _selectedTransitionExpandLinks != resolvedExpand;
        if (_selectedTransition == blockRef && !clipsCleared && !expandChanged) return;
        _selectedTransition = blockRef;
        _selectedTransitionExpandLinks = resolvedExpand;

        if (deferNotify)
        {
            _transitionSelectionPending = true;
            RestyleTransitionBlocks();
            return;
        }

        if (clipsCleared) Rebuild();
        else RestyleTransitionBlocks();
        RedrawTransitionTabs();
        SelectionChanged?.Invoke(this, null);
    }

    private void DropStaleTransitionSelection(Timeline timeline)
    {
        if (_selectedTransition is not { } selected || _dragKind == DragKind.Transition) return;
        if (TransitionBlocks.Find(timeline, selected) is { } found && !timeline.Tracks[found.TrackIndex].Locked) return;
        _selectedTransition = null;
        _selectedTransitionExpandLinks = true;
        DispatcherQueue.TryEnqueue(() => SelectionChanged?.Invoke(this, null));
    }

    public bool TryClearTransitionSelection()
    {
        if (_selectedTransition is null) return false;
        SelectTransition(null);
        return true;
    }

    public bool TryRemoveSelectedTransition()
    {
        if (_session is null || _selectedTransition is not { } selected) return false;
        var expand = _selectedTransitionExpandLinks && !AltIgnoresLinks();
        var result = TransitionCommands.Remove(_session, selected, expandLinks: expand);
        _selectedTransition = null;
        _selectedTransitionExpandLinks = true;
        if (!result.Ok) return false;
        NotifyEdited();
        SelectionChanged?.Invoke(this, null);
        return true;
    }

    /// <summary><c>Ctrl+D</c>: the default block at the free edge nearest the playhead; selects it.</summary>
    public bool TryAddDefaultTransition(out string message)
    {
        message = "";
        if (_session is null) return false;
        var expand = _selectedClipId is not null
            ? ShouldExpandLinksForClip(_selectedClipId)
            : ShouldExpandCurrentSelection();
        var result = TransitionCommands.AddDefault(_session, _selectedClipId, _playheadFrame, expandLinks: expand, out var created);
        if (!result.Ok || created is null)
        {
            message = result.Error ?? "No free clip edge for a transition.";
            return false;
        }

        SelectNewBlock(created.Value, expandLinks: expand);
        if (TransitionBlocks.Find(_session.ActiveTimeline, created.Value) is { } block)
            message = $"Added {TransitionBlocks.Describe(block, _session.ActiveTimeline.Fps)} {BlockPlace(block)}";
        return true;
    }

    /// <summary>
    /// WP-252: the clip menu's way in. A fade at either end of the right-clicked clip, or the default cut
    /// transition where that end meets another picture clip; greyed when that edge already has a
    /// block. Then the <c>Ctrl+D</c> default at the playhead, with its shortcut shown.
    /// </summary>
    private void AddTransitionItems(MenuFlyout flyout, string clipId)
    {
        if (_session is null) return;
        var timeline = _session.ActiveTimeline;
        var index = new TransitionIndex(timeline);
        foreach (var edge in new[] { FadeEdge.In, FadeEdge.Out })
        {
            if (TransitionBlocks.Corner(timeline, clipId, edge) is not { } corner) continue;
            var text = corner.CreatesCut
                ? $"Add {TransitionBlocks.DisplayName(TransitionBlocks.DefaultType)} at {(edge == FadeEdge.In ? "Start" : "End")}"
                : edge == FadeEdge.In ? "Add Fade In" : "Add Fade Out";
            var item = new MenuFlyoutItem { Text = text, IsEnabled = TransitionBlocks.IsFree(index, corner) };
            item.Click += (_, _) => AddTransitionAt(clipId, edge);
            flyout.Items.Add(item);
        }

        var atPlayhead = new MenuFlyoutItem { Text = "Add Transition at Playhead", KeyboardAcceleratorTextOverride = "Ctrl+D" };
        atPlayhead.Click += (_, _) => ContextActionRequested?.Invoke(this, "add-transition");
        flyout.Items.Add(atPlayhead);
    }

    /// <summary>A one-second block at a clip edge, looked up again at click time; selected like <c>Ctrl+D</c>'s.</summary>
    private void AddTransitionAt(string clipId, FadeEdge edge)
    {
        if (_session is null || TransitionBlocks.Corner(_session.ActiveTimeline, clipId, edge) is not { } corner) return;
        var frames = Math.Max(1, _session.ActiveTimeline.Fps);
        var expand = ShouldExpandLinksForClip(clipId);
        if (TransitionCommands.Add(_session, corner, frames, expandLinks: expand, out var created).Ok && created is { } made)
            SelectNewBlock(made, expandLinks: expand);
    }

    /// <summary>A block just added is the selection, so the Inspector shows it at once.</summary>
    private void SelectNewBlock(TransitionBlockRef created, bool expandLinks = true)
    {
        ApplySelection([], null, notify: false, rebuild: false);
        _selectedTransition = created;
        _selectedTransitionExpandLinks = expandLinks;
        NotifyEdited();
        SelectionChanged?.Invoke(this, null);
    }

    /// <summary>The Menu key opens the selected block's menu at the block.</summary>
    public bool TryShowSelectedTransitionMenu()
    {
        if (_session is null || _selectedTransition is not { } selected) return false;
        if (TransitionBlocks.Find(_session.ActiveTimeline, selected) is not { } block) return false;
        FrameworkElement anchor = this;
        var at = new Point(0, 0);
        if (BlockElement(selected, block.TrackIndex) is { } element)
        {
            anchor = element;
            at = new Point(AppTheme.Spacing.Sm, element.Height);
        }
        ShowTransitionMenu(anchor, at, block);
        return true;
    }

    private FrameworkElement? BlockElement(TransitionBlockRef blockRef, int trackIndex)
        => _blockElements.TryGetValue(trackIndex, out var elements)
            ? elements.OfType<FrameworkElement>().FirstOrDefault(element => element.Tag is BlockTag t && t.Ref == blockRef)
            : null;

    private void ShowTransitionMenu(FrameworkElement anchor, Point at, TransitionBlock block)
    {
        if (_session is null) return;
        var session = _session;
        var expand = ResolveTransitionExpandLinks(block.Ref);
        var flyout = new MenuFlyout();
        void Run(Func<EditResult> command)
        {
            if (command().Ok) NotifyEdited();
        }

        if (block.IsCut && block.Transition is { } transition)
        {
            foreach (var type in Enum.GetValues<TransitionType>())
            {
                var item = new ToggleMenuFlyoutItem { Text = TransitionBlocks.DisplayName(type), IsChecked = transition.Type == type };
                item.Click += (_, _) => Run(() => TransitionCommands.SetType(session, block.Ref, type));
                flyout.Items.Add(item);
            }
        }
        else
        {
            foreach (var (text, curve) in new[] { ("Linear", Interpolation.linear), ("Smooth", Interpolation.smooth) })
            {
                var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = block.Curve == curve };
                item.Click += (_, _) => Run(() => TransitionCommands.SetCurve(session, block.Ref, curve, expandLinks: expand && !AltIgnoresLinks()));
                flyout.Items.Add(item);
            }
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        var remove = new MenuFlyoutItem
        {
            Text = block.IsCut ? "Remove transition" : "Remove fade",
            KeyboardAcceleratorTextOverride = "Delete",
        };
        remove.Click += (_, _) =>
        {
            if (TransitionCommands.Remove(session, block.Ref, expandLinks: expand && !AltIgnoresLinks()).Ok)
            {
                _selectedTransition = null;
                _selectedTransitionExpandLinks = true;
                NotifyEdited();
                SelectionChanged?.Invoke(this, null);
            }
        };
        flyout.Items.Add(remove);
        WindowPopout.ShowContextMenuAt(flyout, anchor, at);
    }

    private sealed record BlockHit(TransitionBlock Block, double X, double Y, double W, double H);
    private sealed record ClipLabelSlot(Border Label, double Vx, double Vw, double Cx, double Cw);
    private sealed record BlockTag(TransitionBlockRef Ref, int TrackIndex);
    private sealed record TabTag(string ClipId, FadeEdge Edge);
}
