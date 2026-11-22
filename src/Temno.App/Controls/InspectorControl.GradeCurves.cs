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
    // --- Multi-point RGB curve canvas ---

    private List<CurvePoint> ActiveRgbPoints()
    {
        return _rgbCurveChannel switch
        {
            "red" => _rgbCurve.Red.Count > 0 ? _rgbCurve.Red : PointCurveEditor.Identity,
            "green" => _rgbCurve.Green.Count > 0 ? _rgbCurve.Green : PointCurveEditor.Identity,
            "blue" => _rgbCurve.Blue.Count > 0 ? _rgbCurve.Blue : PointCurveEditor.Identity,
            _ => _rgbCurve.Master.Count > 0 ? _rgbCurve.Master : PointCurveEditor.Identity,
        };
    }

    private void SetActiveRgbPoints(List<CurvePoint> pts)
    {
        switch (_rgbCurveChannel)
        {
            case "red": _rgbCurve.Red = pts; break;
            case "green": _rgbCurve.Green = pts; break;
            case "blue": _rgbCurve.Blue = pts; break;
            default: _rgbCurve.Master = pts; break;
        }
    }

    private void RgbCurveChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RgbCurveChannelBox?.SelectedItem is ComboBoxItem { Tag: string t })
            _rgbCurveChannel = t;
        // Switching the edited channel mid-drag folds the open gesture as one step.
        EndLiveEdit(commit: true);
        RefreshMasterCurveCanvas();
    }

    private void MasterCurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => RefreshMasterCurveCanvas();

    private void RefreshMasterCurveCanvas()
        => DrawPointCurveCanvas(MasterCurveCanvas, ActiveRgbPoints(), identityDiagonal: true, neutralLine: false);

    private void MasterCurve_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // A canvas cannot take focus itself; leaving it on a closed channel popup swallows Undo.
        Focus(FocusState.Pointer);
        var pts = ActiveRgbPoints();
        var (nx, ny) = CanvasToNorm(MasterCurveCanvas, e.GetCurrentPoint(MasterCurveCanvas).Position);
        var hit = PointCurveEditor.HitTest(pts, nx, ny, 0.05);
        var alt = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu);
        if (alt && hit >= 0)
        {
            SetActiveRgbPoints(PointCurveEditor.RemoveAt(pts, hit));
            RefreshMasterCurveCanvas();
            CommitGradeCurvesSelection();
            e.Handled = true;
            return;
        }
        if (hit < 0) return;
        _curveDragIndex = hit;
        MasterCurveCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void MasterCurve_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_curveDragIndex < 0) return;
        var pts = ActiveRgbPoints();
        var (nx, ny) = CanvasToNorm(MasterCurveCanvas, e.GetCurrentPoint(MasterCurveCanvas).Position);
        SetActiveRgbPoints(PointCurveEditor.Move(pts, _curveDragIndex, nx, ny));
        // Keep drag index on nearest after sort
        var next = ActiveRgbPoints();
        _curveDragIndex = PointCurveEditor.HitTest(next, nx, ny, 0.08);
        RefreshMasterCurveCanvas();
        ApplyLiveMasterCurve();
        ClipPreviewing?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>WP-269: RGB curve drags mutate the open project inside the gesture (no undo step).</summary>
    private void ApplyLiveMasterCurve()
    {
        if (_session is null || _clipIds.Count == 0) return;
        if (!_live.IsActive)
            _live.Begin(_session);
        _live.Mutate(() =>
        {
            foreach (var id in _clipIds)
            {
                var clip = FindClip(id);
                if (clip is null || clip.MediaType is not (ClipType.video or ClipType.image)) continue;
                CommitMasterCurve(id);
            }
        });
    }

    private void MasterCurve_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_curveDragIndex < 0) return;
        _curveDragIndex = -1;
        try { MasterCurveCanvas.ReleasePointerCapture(e.Pointer); } catch { /* ignore */ }
        EndLiveEdit(commit: true);
    }

    private void MasterCurve_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_curveDragIndex < 0) return;
        _curveDragIndex = -1;
        EndLiveEdit(commit: true);
    }

    private void RgbCurve_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var pts = ActiveRgbPoints();
        var (nx, ny) = CanvasToNorm(MasterCurveCanvas, e.GetPosition(MasterCurveCanvas));
        if (PointCurveEditor.HitTest(pts, nx, ny, 0.04) >= 0) return;
        SetActiveRgbPoints(PointCurveEditor.Upsert(pts, nx, ny));
        RefreshMasterCurveCanvas();
        CommitGradeCurvesSelection();
    }

    private bool CommitMasterCurve(string clipId)
    {
        if (_session is null) return false;
        // Ensure master has at least identity endpoints if empty
        if (_rgbCurve.Master.Count == 0)
            _rgbCurve.Master = PointCurveEditor.Identity;

        var clip = FindClip(clipId);
        if (clip is null) return false;
        var existing = clip.Effects?.FirstOrDefault(e => e.Type == "color.curves");
        if (_rgbCurve.IsIdentity)
        {
            if (existing is not null)
            {
                var remove = EffectStackCommands.RemoveEffectInPlace(_session, clipId, existing.Id);
                return remove.Ok;
            }
            return false;
        }
        var json = _rgbCurve.Encoded();
        if (json is null) return false;
        if (existing is not null && string.Equals(existing.Params.GetValueOrDefault("curve")?.String, json, StringComparison.Ordinal))
            return false;
        existing = EnsureEffectInPlace(clipId, "color.curves");
        if (existing is null) return false;
        var set = EffectStackCommands.SetEffectStringParamInPlace(_session, clipId, existing.Id, "curve", json);
        return set.Ok;
    }

    // --- Multi-point hue curve canvas ---

    private List<CurvePoint> ActiveHuePoints()
    {
        return _hueCurveChannel switch
        {
            "sat" => _hueCurvesEdit.HueVsSat.Count > 0 ? _hueCurvesEdit.HueVsSat : PointCurveEditor.NeutralHue,
            "lum" => _hueCurvesEdit.HueVsLum.Count > 0 ? _hueCurvesEdit.HueVsLum : PointCurveEditor.NeutralHue,
            _ => _hueCurvesEdit.HueVsHue.Count > 0 ? _hueCurvesEdit.HueVsHue : PointCurveEditor.NeutralHue,
        };
    }

    private void SetActiveHuePoints(List<CurvePoint> pts)
    {
        switch (_hueCurveChannel)
        {
            case "sat": _hueCurvesEdit.HueVsSat = pts; break;
            case "lum": _hueCurvesEdit.HueVsLum = pts; break;
            default: _hueCurvesEdit.HueVsHue = pts; break;
        }
    }

    private void HueCurveChannel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HueCurveChannelBox?.SelectedItem is ComboBoxItem { Tag: string t })
            _hueCurveChannel = t;
        EndLiveEdit(commit: true);
        RefreshHueCurveCanvas();
    }

    private void HueCurveCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => RefreshHueCurveCanvas();

    private void RefreshHueCurveCanvas()
        => DrawPointCurveCanvas(HueCurveCanvas, ActiveHuePoints(), identityDiagonal: false, neutralLine: true);

    private void HueCurve_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (HueCurveCanvas is null) return;
        Focus(FocusState.Pointer);
        var pts = ActiveHuePoints();
        var (nx, ny) = CanvasToNorm(HueCurveCanvas, e.GetCurrentPoint(HueCurveCanvas).Position);
        var hit = PointCurveEditor.HitTest(pts, nx, ny, 0.05);
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Menu) && hit >= 0)
        {
            SetActiveHuePoints(PointCurveEditor.RemoveAt(pts, hit));
            RefreshHueCurveCanvas();
            CommitGradeHueSelection();
            e.Handled = true;
            return;
        }
        if (hit < 0) return;
        _hueDragIndex = hit;
        HueCurveCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void HueCurve_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_hueDragIndex < 0 || HueCurveCanvas is null) return;
        var pts = ActiveHuePoints();
        var (nx, ny) = CanvasToNorm(HueCurveCanvas, e.GetCurrentPoint(HueCurveCanvas).Position);
        SetActiveHuePoints(PointCurveEditor.Move(pts, _hueDragIndex, nx, ny));
        _hueDragIndex = PointCurveEditor.HitTest(ActiveHuePoints(), nx, ny, 0.08);
        RefreshHueCurveCanvas();
        ApplyLiveHueCurves();
        ClipPreviewing?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    /// <summary>WP-269: hue curve drags mutate the open project inside the gesture (no undo step).</summary>
    private void ApplyLiveHueCurves()
    {
        if (_session is null || _clipIds.Count == 0) return;
        if (!_live.IsActive)
            _live.Begin(_session);
        _live.Mutate(() =>
        {
            foreach (var id in _clipIds)
            {
                var clip = FindClip(id);
                if (clip is null || clip.MediaType is not (ClipType.video or ClipType.image)) continue;
                CommitHueCurves(id);
            }
        });
    }

    private void HueCurve_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_hueDragIndex < 0) return;
        _hueDragIndex = -1;
        try { HueCurveCanvas.ReleasePointerCapture(e.Pointer); } catch { /* ignore */ }
        EndLiveEdit(commit: true);
    }

    private void HueCurve_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_hueDragIndex < 0) return;
        _hueDragIndex = -1;
        EndLiveEdit(commit: true);
    }

    private void HueCurve_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (HueCurveCanvas is null) return;
        var pts = ActiveHuePoints();
        var (nx, ny) = CanvasToNorm(HueCurveCanvas, e.GetPosition(HueCurveCanvas));
        if (PointCurveEditor.HitTest(pts, nx, ny, 0.04) >= 0) return;
        SetActiveHuePoints(PointCurveEditor.Upsert(pts, nx, ny));
        RefreshHueCurveCanvas();
        CommitGradeHueSelection();
    }

    private static (double X, double Y) CanvasToNorm(Canvas canvas, Point pt)
    {
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        var x = Math.Clamp((pt.X - 4) / Math.Max(1, w - 8), 0, 1);
        var y = Math.Clamp(1.0 - (pt.Y - 4) / Math.Max(1, h - 8), 0, 1);
        return (x, y);
    }

    private static void DrawPointCurveCanvas(Canvas? canvas, IReadOnlyList<CurvePoint> points, bool identityDiagonal, bool neutralLine)
    {
        if (canvas is null) return;
        canvas.Children.Clear();
        var w = canvas.ActualWidth;
        var h = canvas.ActualHeight;
        if (w < 8 || h < 8) return;

        if (identityDiagonal)
        {
            canvas.Children.Add(new Line
            {
                X1 = 4, Y1 = h - 4, X2 = w - 4, Y2 = 4,
                Stroke = AppTheme.Border.SubtleBrush, StrokeThickness = 1, IsHitTestVisible = false,
            });
        }
        if (neutralLine)
        {
            var ny = h - 4 - 0.5 * (h - 8);
            canvas.Children.Add(new Line
            {
                X1 = 4, Y1 = ny, X2 = w - 4, Y2 = ny,
                Stroke = AppTheme.Border.SubtleBrush, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 2 },
                IsHitTestVisible = false,
            });
        }

        var poly = new Polyline
        {
            Stroke = AppTheme.Accent.SpotlightBrush,
            StrokeThickness = 1.5,
            IsHitTestVisible = false,
        };
        // Dense sample for smooth display
        for (var i = 0; i <= 64; i++)
        {
            var x = i / 64.0;
            var y = GradeCurve.Sample(points, x);
            poly.Points.Add(new Point(4 + x * (w - 8), h - 4 - y * (h - 8)));
        }
        canvas.Children.Add(poly);

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var kn = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = AppTheme.Accent.TimecodeBrush,
                Tag = i,
            };
            Canvas.SetLeft(kn, 4 + p.X * (w - 8) - 5);
            Canvas.SetTop(kn, h - 4 - p.Y * (h - 8) - 5);
            canvas.Children.Add(kn);
        }
    }
}
