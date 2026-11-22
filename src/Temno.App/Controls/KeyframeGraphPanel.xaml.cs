using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Temno.App.Theme;
using Temno.Core.Models;
using Temno.Core.Session;
using Windows.Foundation;
using ClipModel = Temno.Core.Models.Clip;

namespace Temno.App.Controls;

/// <summary>Value×time keyframe graph for scalar channels (WP-82 deep polish).</summary>
public sealed partial class KeyframeGraphPanel : UserControl
{
    private ProjectSession? _session;
    private string? _clipId;
    private string? _audioClipId;
    private int _playheadFrame;
    private string _channel = "opacity";
    private int? _selectedOffset;
    private bool _dragging;
    private bool _dragMoved;
    private Point _dragStartPos;
    private int _dragFromOffset;
    private double _dragStartValue;
    private double _dragValue;
    private int _dragToOffset;
    private double _dragVmin;
    private double _dragVmax = 1;
    private Line? _playheadLine;
    private string? _renderedClipId;
    private string? _renderedChannel;
    private int _renderedDurationFrames;
    private double _renderedWidth;
    private double _renderedHeight;

    public event EventHandler? Edited;
    public event EventHandler<int>? SeekPlayheadRequested;

    public KeyframeGraphPanel()
    {
        InitializeComponent();
    }

    public void Bind(ProjectSession? session, string? clipId, int playheadFrame, string? audioClipId = null)
    {
        var clipChanged = !ReferenceEquals(_session, session)
            || !string.Equals(_clipId, clipId, StringComparison.Ordinal)
            || !string.Equals(_audioClipId, audioClipId, StringComparison.Ordinal);
        _session = session;
        _clipId = clipId;
        _audioClipId = audioClipId;
        _playheadFrame = playheadFrame;
        if (clipChanged)
        {
            _selectedOffset = null;
            _dragging = false;
            _dragMoved = false;
        }
        Refresh();
    }

    public void SetPlayhead(int frame)
    {
        if (_playheadFrame == frame) return;
        _playheadFrame = frame;
        if (_dragging || GraphCanvas is null) return;

        var w = GraphCanvas.ActualWidth;
        var h = GraphCanvas.ActualHeight;
        if (w < 16 || h < 16) return;

        var clip = FindClip();
        if (clip is not null
            && _playheadLine is not null
            && string.Equals(_renderedClipId, clip.Id, StringComparison.Ordinal)
            && string.Equals(_renderedChannel, _channel, StringComparison.Ordinal)
            && _renderedDurationFrames == clip.DurationFrames
            && Math.Abs(_renderedWidth - w) < 0.5
            && Math.Abs(_renderedHeight - h) < 0.5)
        {
            UpdatePlayheadVisual(clip, w, h);
            return;
        }

        Refresh();
    }

    private void UpdatePlayheadVisual(ClipModel clip, double w, double h)
    {
        if (_playheadLine is null) return;
        const double pad = 8;
        var inClip = clip.Contains(_playheadFrame);
        if (inClip)
        {
            var span = Math.Max(1, clip.DurationFrames - 1);
            var playOff = Math.Clamp(_playheadFrame - clip.StartFrame, 0, span);
            var px = pad + playOff / (double)span * (w - 2 * pad);
            _playheadLine.X1 = px;
            _playheadLine.X2 = px;
            _playheadLine.Y1 = pad;
            _playheadLine.Y2 = h - pad;
            _playheadLine.Visibility = Visibility.Visible;
        }
        else
        {
            _playheadLine.Visibility = Visibility.Collapsed;
        }

        if (AddAtPlayheadBtn is not null)
            AddAtPlayheadBtn.IsEnabled = inClip;
    }

    private string? ActiveClipId()
        => string.Equals(_channel, "volume", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(_audioClipId)
            ? _audioClipId
            : _clipId;

    private ClipModel? FindClip()
    {
        var id = ActiveClipId();
        if (_session is null || string.IsNullOrEmpty(id)) return null;
        foreach (var t in _session.ActiveTimeline.Tracks)
        {
            var c = t.Clips.FirstOrDefault(x => x.Id == id);
            if (c is not null) return c;
        }
        return null;
    }

    private void ChannelBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChannelBox.SelectedItem is ComboBoxItem { Tag: string tag })
            _channel = tag;
        _selectedOffset = null;
        Refresh();
    }

    private void GraphCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => Refresh();

    public void Refresh()
    {
        // ChannelBox selects its default item while InitializeComponent is still constructing
        // the later canvas and status elements.
        if (GraphCanvas is null || StatusLabel is null) return;

        GraphCanvas.Children.Clear();
        _playheadLine = null;
        _renderedClipId = null;
        var clip = FindClip();
        var w = GraphCanvas.ActualWidth;
        var h = GraphCanvas.ActualHeight;
        if (clip is null || w < 16 || h < 16 || clip.DurationFrames <= 0)
        {
            StatusLabel.Text = clip is null ? "Select a clip to edit keyframe graph." : "…";
            if (AddAtPlayheadBtn is not null) AddAtPlayheadBtn.IsEnabled = false;
            if (DeleteKeyBtn is not null) DeleteKeyBtn.IsEnabled = false;
            return;
        }

        _renderedClipId = clip.Id;
        _renderedChannel = _channel;
        _renderedDurationFrames = clip.DurationFrames;
        _renderedWidth = w;
        _renderedHeight = h;

        var (vmin, vmax) = _dragging ? (_dragVmin, _dragVmax) : KeyframeCommands.DisplayRange(clip, _channel);
        if (Math.Abs(vmax - vmin) < 1e-9) vmax = vmin + 1;
        var span = Math.Max(1, clip.DurationFrames - 1);
        const double pad = 8;

        // Grid
        for (var i = 0; i <= 4; i++)
        {
            var y = pad + i / 4.0 * (h - 2 * pad);
            GraphCanvas.Children.Add(new Line
            {
                X1 = pad, Y1 = y, X2 = w - pad, Y2 = y,
                Stroke = AppTheme.Border.SubtleBrush, StrokeThickness = 1, IsHitTestVisible = false,
            });
        }

        // Sampled curve
        var poly = new Polyline
        {
            Stroke = AppTheme.Accent.SpotlightBrush,
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
        };
        var samples = Math.Clamp(clip.DurationFrames, 2, 200);
        for (var i = 0; i < samples; i++)
        {
            var off = (int)Math.Round(i / (double)(samples - 1) * span);
            var val = KeyframeCommands.SampleScalarAtClip(clip, _channel, off);
            poly.Points.Add(Map(off, val, span, vmin, vmax, w, h, pad));
        }
        GraphCanvas.Children.Add(poly);

        // Playhead (only shown when inside the clip; moved in-place by SetPlayhead during playback)
        var inClip = clip.Contains(_playheadFrame);
        _playheadLine = new Line
        {
            Stroke = AppTheme.Accent.SpotlightBrush,
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 },
            IsHitTestVisible = false,
        };
        GraphCanvas.Children.Add(_playheadLine);
        UpdatePlayheadVisual(clip, w, h);

        // WP-183: an empty channel says so, in the graph, with the two ways to add a key.
        var allKeys = KeyframeCommands.GetScalarKeys(clip, _channel);
        var keys = allKeys.Where(k => k.Offset >= 0 && k.Offset < clip.DurationFrames).ToList();
        if (_selectedOffset is int selOff && keys.All(k => k.Offset != selOff))
            _selectedOffset = null;
        if (AddAtPlayheadBtn is not null)
            AddAtPlayheadBtn.IsEnabled = inClip;
        if (DeleteKeyBtn is not null)
            DeleteKeyBtn.IsEnabled = _selectedOffset is not null;
        if (keys.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = "No keyframes on this channel.\nDouble-tap the graph or press Add at playhead.",
                FontSize = AppTheme.FontSize.Sm,
                Foreground = AppTheme.Text.TertiaryBrush,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Width = Math.Max(40, w - 2 * pad),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(empty, pad);
            Canvas.SetTop(empty, Math.Max(pad, h / 2 - 16));
            GraphCanvas.Children.Add(empty);
        }
        // Keys
        foreach (var key in keys)
        {
            var drawOff = _dragging && key.Offset == _dragFromOffset ? _dragToOffset : key.Offset;
            var drawVal = _dragging && key.Offset == _dragFromOffset ? _dragValue : key.Value;
            var pt = Map(drawOff, drawVal, span, vmin, vmax, w, h, pad);
            var selected = _selectedOffset == key.Offset || (_dragging && key.Offset == _dragFromOffset);
            var kn = new Ellipse
            {
                Width = selected ? 12 : 10,
                Height = selected ? 12 : 10,
                Fill = selected ? AppTheme.Accent.TimecodeBrush : AppTheme.Text.SecondaryBrush,
                Stroke = AppTheme.Text.PrimaryBrush,
                StrokeThickness = selected ? 1.5 : 0,
                Tag = key.Offset,
            };
            Canvas.SetLeft(kn, pt.X - kn.Width / 2);
            Canvas.SetTop(kn, pt.Y - kn.Height / 2);
            GraphCanvas.Children.Add(kn);
        }

        var n = keys.Count;
        StatusLabel.Text = n == 0
            ? "No keys — Add at playhead or double-tap the graph."
            : $"{n} key(s) · {_channel} · range [{vmin:0.##}…{vmax:0.##}]";
    }

    private static Point Map(int offset, double value, int span, double vmin, double vmax, double w, double h, double pad)
    {
        var tx = offset / (double)span;
        var ty = (value - vmin) / (vmax - vmin);
        ty = Math.Clamp(ty, 0, 1);
        return new Point(pad + tx * (w - 2 * pad), h - pad - ty * (h - 2 * pad));
    }

    private static (int Offset, double Value) Unmap(Point p, int span, double vmin, double vmax, double w, double h, double pad)
    {
        var tx = Math.Clamp((p.X - pad) / Math.Max(1, w - 2 * pad), 0, 1);
        var ty = Math.Clamp(1 - (p.Y - pad) / Math.Max(1, h - 2 * pad), 0, 1);
        var off = (int)Math.Round(tx * span);
        var val = vmin + ty * (vmax - vmin);
        return (Math.Clamp(off, 0, span), val);
    }

    private void Graph_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var clip = FindClip();
        if (_session is null || clip is null) return;
        var pos = e.GetCurrentPoint(GraphCanvas).Position;
        // Hit key
        FrameworkElement? el = e.OriginalSource as FrameworkElement;
        while (el is not null && el is not Ellipse { Tag: int })
            el = el.Parent as FrameworkElement;
        if (el is Ellipse { Tag: int off })
        {
            _selectedOffset = off;
            _dragging = true;
            _dragMoved = false;
            _dragStartPos = pos;
            _dragFromOffset = off;
            _dragToOffset = off;
            var key = KeyframeCommands.GetScalarKeys(clip, _channel).FirstOrDefault(k => k.Offset == off);
            _dragStartValue = key?.Value ?? 0;
            _dragValue = _dragStartValue;
            (_dragVmin, _dragVmax) = KeyframeCommands.DisplayRange(clip, _channel);
            if (Math.Abs(_dragVmax - _dragVmin) < 1e-9) _dragVmax = _dragVmin + 1;
            SelectInterp(key?.InterpolationOut ?? Interpolation.smooth);
            GraphCanvas.CapturePointer(e.Pointer);
            Refresh();
            e.Handled = true;
        }
    }

    private void Graph_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || FindClip() is not { } clip) return;
        var pos = e.GetCurrentPoint(GraphCanvas).Position;
        if (!_dragMoved && Math.Abs(pos.X - _dragStartPos.X) < 3 && Math.Abs(pos.Y - _dragStartPos.Y) < 3)
            return;
        _dragMoved = true;
        var span = Math.Max(1, clip.DurationFrames - 1);
        var (off, val) = Unmap(pos, span, _dragVmin, _dragVmax,
            GraphCanvas.ActualWidth, GraphCanvas.ActualHeight, 8);
        _dragToOffset = off;
        _dragValue = val;
        Refresh();
        e.Handled = true;
    }

    private void Graph_PointerReleased(object sender, PointerRoutedEventArgs e)
        => EndDrag(e.Pointer);

    private void Graph_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => EndDrag(null);

    private void EndDrag(Pointer? pointer)
    {
        if (!_dragging) return;
        var moved = _dragMoved;
        _dragging = false;
        _dragMoved = false;
        if (pointer is not null)
        {
            try { GraphCanvas.ReleasePointerCapture(pointer); } catch { /* ignore */ }
        }
        var targetId = ActiveClipId();
        if (_session is null || string.IsNullOrEmpty(targetId)) return;

        if (!moved || (_dragToOffset == _dragFromOffset && Math.Abs(_dragValue - _dragStartValue) <= 1e-6))
        {
            _selectedOffset = _dragFromOffset;
            if (FindClip() is { } tappedClip)
                SeekPlayheadRequested?.Invoke(this, tappedClip.StartFrame + _dragFromOffset);
            Refresh();
            return;
        }

        var interp = ReadInterp();
        var result = KeyframeCommands.MoveScalarKey(
            _session, targetId, _channel, _dragFromOffset, _dragToOffset, _dragValue, interp);
        if (result.Ok)
        {
            _selectedOffset = _dragToOffset;
            Edited?.Invoke(this, EventArgs.Empty);
            if (FindClip() is { } c)
                SeekPlayheadRequested?.Invoke(this, c.StartFrame + _dragToOffset);
        }
        Refresh();
    }

    private void Graph_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var clip = FindClip();
        var targetId = ActiveClipId();
        if (_session is null || clip is null || string.IsNullOrEmpty(targetId)) return;
        var (vmin, vmax) = KeyframeCommands.DisplayRange(clip, _channel);
        if (Math.Abs(vmax - vmin) < 1e-9) vmax = vmin + 1;
        var span = Math.Max(1, clip.DurationFrames - 1);
        var (off, val) = Unmap(e.GetPosition(GraphCanvas), span, vmin, vmax,
            GraphCanvas.ActualWidth, GraphCanvas.ActualHeight, 8);
        var r = KeyframeCommands.UpsertScalarKey(_session, targetId, _channel, off, val, ReadInterp());
        if (r.Ok)
        {
            _selectedOffset = off;
            Edited?.Invoke(this, EventArgs.Empty);
        }
        Refresh();
    }

    private void AddAtPlayhead_Click(object sender, RoutedEventArgs e)
    {
        var clip = FindClip();
        var targetId = ActiveClipId();
        if (_session is null || clip is null || string.IsNullOrEmpty(targetId) || !clip.Contains(_playheadFrame)) return;
        var off = _playheadFrame - clip.StartFrame;
        var val = KeyframeCommands.SampleScalarAtClip(clip, _channel, off);
        var r = KeyframeCommands.UpsertScalarKey(_session, targetId, _channel, off, val, ReadInterp());
        if (r.Ok)
        {
            _selectedOffset = off;
            Edited?.Invoke(this, EventArgs.Empty);
        }
        Refresh();
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var clip = FindClip();
        var targetId = ActiveClipId();
        if (_session is null || clip is null || string.IsNullOrEmpty(targetId) || _selectedOffset is not int off)
            return;
        var baseProp = _channel is "scale.a" or "scale.b" ? "scale"
            : _channel is "position.a" or "position.b" ? "position"
            : _channel.StartsWith("crop.", StringComparison.Ordinal) ? "crop"
            : _channel;
        var r = KeyframeCommands.RemoveAt(_session, targetId, baseProp, clip.StartFrame + off);
        if (r.Ok)
        {
            _selectedOffset = null;
            Edited?.Invoke(this, EventArgs.Empty);
        }
        Refresh();
    }

    private void InterpBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var targetId = ActiveClipId();
        if (_session is null || string.IsNullOrEmpty(targetId) || _selectedOffset is not int off) return;
        if (_dragging) return;
        var r = KeyframeCommands.SetInterpolation(_session, targetId, _channel, off, ReadInterp());
        if (r.Ok) Edited?.Invoke(this, EventArgs.Empty);
        Refresh();
    }

    private Interpolation ReadInterp()
    {
        if (InterpBox.SelectedItem is ComboBoxItem { Tag: string t })
        {
            return t switch
            {
                "linear" => Interpolation.linear,
                "hold" => Interpolation.hold,
                _ => Interpolation.smooth,
            };
        }
        return Interpolation.smooth;
    }

    private void SelectInterp(Interpolation interp)
    {
        for (var i = 0; i < InterpBox.Items.Count; i++)
        {
            if (InterpBox.Items[i] is ComboBoxItem { Tag: string t }
                && string.Equals(t, interp.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                InterpBox.SelectedIndex = i;
                return;
            }
        }
    }
}
