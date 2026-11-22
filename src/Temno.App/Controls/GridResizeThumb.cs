using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Temno.App.Controls;

/// <summary>Draggable gutter that adjusts a <see cref="ColumnDefinition"/> or <see cref="RowDefinition"/> size.</summary>
public sealed class GridResizeThumb : Control
{
    public static readonly DependencyProperty TargetColumnProperty =
        DependencyProperty.Register(nameof(TargetColumn), typeof(ColumnDefinition), typeof(GridResizeThumb), new PropertyMetadata(null));

    public static readonly DependencyProperty TargetRowProperty =
        DependencyProperty.Register(nameof(TargetRow), typeof(RowDefinition), typeof(GridResizeThumb), new PropertyMetadata(null));

    public static readonly DependencyProperty OrientationProperty =
        DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(GridResizeThumb), new PropertyMetadata(Orientation.Vertical, OnOrientationChanged));

    public static readonly DependencyProperty MinSizeProperty =
        DependencyProperty.Register(nameof(MinSize), typeof(double), typeof(GridResizeThumb), new PropertyMetadata(80.0));

    public static readonly DependencyProperty MaxSizeProperty =
        DependencyProperty.Register(nameof(MaxSize), typeof(double), typeof(GridResizeThumb), new PropertyMetadata(double.PositiveInfinity));

    public static readonly DependencyProperty InvertProperty =
        DependencyProperty.Register(nameof(Invert), typeof(bool), typeof(GridResizeThumb), new PropertyMetadata(false));

    private bool _dragging;
    private double _startPointer;
    private double _startSize;

    /// <summary>Raised when a drag ends (pointer release or capture lost) after a size change.</summary>
    public event EventHandler? ResizeCompleted;

    public GridResizeThumb()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        IsTabStop = false;
        ApplyOrientationChrome();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) =>
        {
            if (_dragging)
            {
                _dragging = false;
                ResizeCompleted?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public ColumnDefinition? TargetColumn
    {
        get => (ColumnDefinition?)GetValue(TargetColumnProperty);
        set => SetValue(TargetColumnProperty, value);
    }

    public RowDefinition? TargetRow
    {
        get => (RowDefinition?)GetValue(TargetRowProperty);
        set => SetValue(TargetRowProperty, value);
    }

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public double MinSize
    {
        get => (double)GetValue(MinSizeProperty);
        set => SetValue(MinSizeProperty, value);
    }

    public double MaxSize
    {
        get => (double)GetValue(MaxSizeProperty);
        set => SetValue(MaxSizeProperty, value);
    }

    public bool Invert
    {
        get => (bool)GetValue(InvertProperty);
        set => SetValue(InvertProperty, value);
    }

    private static void OnOrientationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is GridResizeThumb thumb)
            thumb.ApplyOrientationChrome();
    }

    private void ApplyOrientationChrome()
    {
        if (Orientation == Orientation.Vertical)
        {
            Width = 5;
            Height = double.NaN;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        }
        else
        {
            Height = 5;
            Width = double.NaN;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        CapturePointer(e.Pointer);
        var pt = e.GetCurrentPoint(this).Position;
        _startPointer = Orientation == Orientation.Vertical ? pt.X : pt.Y;

        if (Orientation == Orientation.Vertical && TargetColumn is { } col)
            _startSize = col.ActualWidth > 0 ? col.ActualWidth : (col.Width.IsAbsolute ? col.Width.Value : MinSize);
        else if (Orientation == Orientation.Horizontal && TargetRow is { } row)
            _startSize = row.ActualHeight > 0 ? row.ActualHeight : (row.Height.IsAbsolute ? row.Height.Value : MinSize);
        else
            _startSize = MinSize;

        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var pt = e.GetCurrentPoint(this).Position;
        var current = Orientation == Orientation.Vertical ? pt.X : pt.Y;
        var delta = current - _startPointer;
        if (Invert) delta = -delta;

        var next = Math.Clamp(_startSize + delta, MinSize, MaxSize);

        if (Orientation == Orientation.Vertical && TargetColumn is { } col)
            col.Width = new GridLength(next);
        else if (Orientation == Orientation.Horizontal && TargetRow is { } row)
            row.Height = new GridLength(next);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        ResizeCompleted?.Invoke(this, EventArgs.Empty);
    }
}
