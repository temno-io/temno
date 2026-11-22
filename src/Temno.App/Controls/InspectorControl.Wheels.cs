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

public sealed partial class InspectorControl : UserControl
{
    // --- Wheel pads ---

    private void RefreshWheelPads()
    {
        DrawWheelPad(LiftWheelPad, _gradeLiftX, _gradeLiftY);
        DrawWheelPad(GammaWheelPad, _gradeGammaX, _gradeGammaY);
        DrawWheelPad(GainWheelPad, _gradeGainX, _gradeGainY);
    }

    private static void DrawWheelPad(Canvas pad, double x, double y)
    {
        if (pad is null) return;
        pad.Children.Clear();
        // The pad stretches to its border (which self-squares); draw at the arranged size.
        var size = Math.Min(pad.ActualWidth, pad.ActualHeight);
        if (size < 10) size = 88; // Not yet arranged (initial bind before first layout).
        var cx = size / 2;
        var cy = size / 2;
        var r = size / 2 - 4;

        // Crosshair
        pad.Children.Add(new Line
        {
            X1 = 4, Y1 = cy, X2 = size - 4, Y2 = cy,
            Stroke = AppTheme.Border.SubtleBrush, StrokeThickness = 1, IsHitTestVisible = false,
        });
        pad.Children.Add(new Line
        {
            X1 = cx, Y1 = 4, X2 = cx, Y2 = size - 4,
            Stroke = AppTheme.Border.SubtleBrush, StrokeThickness = 1, IsHitTestVisible = false,
        });

        var px = cx + Math.Clamp(x, -1, 1) * r;
        var py = cy - Math.Clamp(y, -1, 1) * r; // UI Y up
        var kn = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = AppTheme.Accent.TimecodeBrush,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(kn, px - 5);
        Canvas.SetTop(kn, py - 5);
        pad.Children.Add(kn);
    }

    private void WheelPad_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Canvas pad || _session is null || _clipIds.Count == 0) return;
        Focus(FocusState.Pointer);
        _wheelPadActive = pad;
        pad.CapturePointer(e.Pointer);
        // A press repositions the knob, so it is already a change: join (or open) the gesture.
        UpdateWheelFromPoint(pad, e.GetCurrentPoint(pad).Position);
        e.Handled = true;
    }

    private void WheelPad_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_wheelPadActive is null || sender is not Canvas pad || pad != _wheelPadActive) return;
        UpdateWheelFromPoint(pad, e.GetCurrentPoint(pad).Position);
        e.Handled = true;
    }

    private void WheelPad_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_wheelPadActive is not null)
        {
            try { _wheelPadActive.ReleasePointerCapture(e.Pointer); } catch { /* ignore */ }
            EndLiveEdit(commit: true);
        }
        _wheelPadActive = null;
    }

    private void WheelPad_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_wheelPadActive is not null)
            EndLiveEdit(commit: true);
        _wheelPadActive = null;
    }

    private void UpdateWheelFromPoint(Canvas pad, Point pos)
    {
        var size = Math.Min(pad.ActualWidth, pad.ActualHeight);
        if (size < 10) size = 88;
        var cx = size / 2;
        var cy = size / 2;
        var r = size / 2 - 4;
        var nx = Math.Clamp((pos.X - cx) / r, -1, 1);
        var ny = Math.Clamp((cy - pos.Y) / r, -1, 1);
        // Constrain to unit circle
        var len = Math.Sqrt(nx * nx + ny * ny);
        if (len > 1)
        {
            nx /= len;
            ny /= len;
        }

        _suppress = true;
        if (pad == LiftWheelPad)
        {
            _gradeLiftX = nx;
            _gradeLiftY = ny;
        }
        else if (pad == GammaWheelPad)
        {
            _gradeGammaX = nx;
            _gradeGammaY = ny;
        }
        else if (pad == GainWheelPad)
        {
            _gradeGainX = nx;
            _gradeGainY = ny;
        }
        _suppress = false;
        DrawWheelPad(pad, nx, ny);
        if (_session is not null && _clipIds.Count > 0)
        {
            if (!_live.IsActive)
                _live.Begin(_session);
            IReadOnlyDictionary<string, double> changed = pad == LiftWheelPad
                ? new Dictionary<string, double> { ["lift_x"] = _gradeLiftX, ["lift_y"] = _gradeLiftY }
                : pad == GammaWheelPad
                    ? new Dictionary<string, double> { ["gamma_x"] = _gradeGammaX, ["gamma_y"] = _gradeGammaY }
                    : new Dictionary<string, double> { ["gain_x"] = _gradeGainX, ["gain_y"] = _gradeGainY };
            _live.Mutate(() => ApplyLiveGradeValues("color.wheels", changed));
        }
        ClipPreviewing?.Invoke(this, EventArgs.Empty);
    }

    // --- WP-269 responsive layout: three pads side by side when they fit, stacked below ---
    //
    // The wheels live in a VariableSizedWrapGrid with three static children in fixed
    // 96 px cells (ItemWidth): pad 88 + caption/M rows wrap inside the cell. The grid's own
    // wrapping does the responsive switch with NO code: 3 cells fit side by side at wide
    // widths (3x96 = 288 <= section width), fewer fit as the panel narrows. No SizeChanged
    // handler: an earlier Grid version restructured columns/rows in code and froze the
    // subtree's composition at the pre-mutation frame (side-by-side pixels over a stacked
    // live layout, 140 px apart), and an ItemWidth-setting version measured the grid at 44 px
    // (unarranged at first layout) and pinned every cell to 44 px. Static XAML keeps
    // composition, hit-test and UIA in sync by construction. Each pad border is an explicit
    // 88 px square: a Canvas child reports zero desired size, so the borders cannot
    // self-size from content.

    private void WheelBorder_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Redraw the pad crosshair/knob when the border's arranged size settles. Never writes
        // sizes back: explicit 88 px squares in XAML converge in one relayout with no feedback.
        if (sender is not Border) return;
        RefreshWheelPads();
    }
}
