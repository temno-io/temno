using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Temno.App.Controls.Shared;
using Temno.App.Services;
using Temno.App.Theme;
using Temno.Core.Audio;
using Temno.Core.Editing;
using Temno.Core.Models;
using Temno.Core.Session;
using Temno.Core.Ui;
using Temno.Media.Audio;
using Temno.Media.Cache;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Colors = Microsoft.UI.Colors;

namespace Temno.App.Controls;

public sealed partial class TimelineControl : UserControl
{
    /// <summary>Raised when the user starts scrubbing the playhead (so preview can pause).</summary>
    public event EventHandler? ScrubStarted;

    /// <summary>Raised when playhead scrub ends.</summary>
    public event EventHandler? ScrubEnded;

    private void Playhead_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragKind != DragKind.None || _marqueeSelecting || _draggingPlayhead || _seeking) return;
        if (!e.GetCurrentPoint(RulerHost).Properties.IsLeftButtonPressed) return;
        ClearBladeIndication();
        _draggingPlayhead = true;
        ScrubStarted?.Invoke(this, EventArgs.Empty);
        if ((sender as UIElement)?.CapturePointer(e.Pointer) != true)
        {
            _draggingPlayhead = false;
            PersistPlayhead();
            ScrubEnded?.Invoke(this, EventArgs.Empty);
        }
        e.Handled = true;
    }

    private void Playhead_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingPlayhead) return;
        var viewX = e.GetCurrentPoint(RulerHost).Position.X;
        SeekFromViewportX(viewX);
        MaybeAutoScrollViewportX(viewX);
        e.Handled = true;
    }

    private void Playhead_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingPlayhead) return;
        _draggingPlayhead = false;
        PersistPlayhead();
        ScrubEnded?.Invoke(this, EventArgs.Empty);
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void Playhead_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingPlayhead) return;
        _draggingPlayhead = false;
        PersistPlayhead();
        ScrubEnded?.Invoke(this, EventArgs.Empty);
    }

    private void RulerCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragKind != DragKind.None || _marqueeSelecting || _draggingPlayhead || _seeking) return;
        var current = e.GetCurrentPoint(RulerHost);
        if (!current.Properties.IsLeftButtonPressed) return;
        ClearBladeIndication();
        _seeking = true;
        ScrubStarted?.Invoke(this, EventArgs.Empty);
        SeekFromViewportX(current.Position.X);
        if (!RulerHost.CapturePointer(e.Pointer))
        {
            _seeking = false;
            PersistPlayhead();
            ScrubEnded?.Invoke(this, EventArgs.Empty);
        }
        e.Handled = true;
    }

    private void RulerCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_seeking) return;
        var point = e.GetCurrentPoint(RulerHost).Position;
        SeekFromViewportX(point.X);
        MaybeAutoScrollViewportX(point.X);
        e.Handled = true;
    }

    private void RulerCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        PersistPlayhead();
        ScrubEnded?.Invoke(this, EventArgs.Empty);
        RulerHost.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void RulerCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_seeking) return;
        _seeking = false;
        PersistPlayhead();
        ScrubEnded?.Invoke(this, EventArgs.Empty);
    }

    private void SeekFromViewportX(double viewX)
        => PlayheadFrame = TimelineGeometry.FrameAtViewportX(viewX, BodyScroll.HorizontalOffset, _pixelsPerFrame);
    private int FrameAtContentX(double contentX)
        => TimelineGeometry.FrameAtViewportX(contentX, horizontalOffset: 0, _pixelsPerFrame);
    private int FrameAt(double laneX) => FrameAtContentX(ContentX(laneX));
    private int DropFrameAtViewportX(double viewX)
        => TimelineGeometry.DropFrameAtViewportX(viewX, BodyScroll.HorizontalOffset, _pixelsPerFrame);

    private Clip? FindClip(string id)
    {
        if (_session is null) return null;
        foreach (var t in _session.ActiveTimeline.Tracks)
        {
            var c = t.Clips.FirstOrDefault(x => x.Id == id);
            if (c is not null) return c;
        }
        return null;
    }

    private bool TryClipAt(Point lanePoint, out Clip clip, out int trackIndex)
    {
        clip = null!;
        trackIndex = -1;
        if (_session is null)
            return false;
        var contentX = ContentX(lanePoint.X);
        var tracks = _session.ActiveTimeline.Tracks;
        for (var ti = tracks.Count - 1; ti >= 0; ti--)
        {
            foreach (var candidate in tracks[ti].Clips)
            {
                var (clipX, clipY, clipW, clipH) = _geo.ClipRect(candidate, ti);
                if (contentX < clipX || contentX >= clipX + clipW || lanePoint.Y < clipY || lanePoint.Y >= clipY + clipH)
                    continue;
                clip = candidate;
                trackIndex = ti;
                return true;
            }
        }
        return false;
    }

    private int TrackIndexOf(string clipId)
    {
        if (_session is null)
            return -1;
        var tracks = _session.ActiveTimeline.Tracks;
        for (var i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].Clips.Any(clip => string.Equals(clip.Id, clipId, StringComparison.Ordinal)))
                return i;
        }
        return -1;
    }
}
