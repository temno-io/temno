using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Temno.App.Theme;
using Temno.Core.Ui;
using Windows.System;
using Windows.UI.Core;

namespace Temno.App.Controls.Shared;

/// <summary>
/// A numeric field that shows its unit inline (<c>48 pt</c>, <c>500 ms</c>, <c>100%</c>) and can be
/// dragged to change. Mac <c>ScrubbableNumberField</c>; see <c>docs/DESIGN.md</c> §3.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the spinner <see cref="NumberBox"/> (192 app-wide, 47 in the Inspector). All four input
/// routes — typing, dragging, the wheel and the arrow keys — funnel through <see cref="Commit"/>, so
/// clamping, snapping and change notification cannot diverge between them.
/// </para>
/// <para>
/// Every value decision lives in <see cref="UnitFieldSpec"/> (Temno.Core, WinUI-free and unit
/// tested). This class is the shell: pointer capture, focus, and rendering.
/// </para>
/// </remarks>
public sealed partial class UnitNumberField : UserControl
{
    /// <summary>Drag must exceed this before it counts, so a click can still focus the field.</summary>
    private const double DragThresholdPixels = 3;

    private readonly TextBox _text = new()
    {
        BorderThickness = new Thickness(AppTheme.BorderWidth.Thin),
        FontSize = AppTheme.FontSize.EditorValue,
        TextAlignment = TextAlignment.Left,
        Padding = new Thickness(AppTheme.Spacing.Sm, AppTheme.Spacing.Xxs, AppTheme.Spacing.Sm, AppTheme.Spacing.Xxs),
        MinWidth = 0,
        Background = AppTheme.Background.RaisedBrush,
    };

    private readonly TextBlock _unit = new()
    {
        FontSize = AppTheme.FontSize.Sm,
        Foreground = AppTheme.Text.TertiaryBrush,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(AppTheme.Spacing.Xs, AppTheme.Spacing.None, AppTheme.Spacing.None, AppTheme.Spacing.None),
    };

    private readonly Grid _root;

    /// <summary>
    /// Transparent hit-target sitting over the text box while the field is not being edited.
    /// Without it the <see cref="TextBox"/> consumes the pointer for caret placement and text
    /// selection, and the drag gesture never reaches this control at all — which is exactly what
    /// happened on the first run of this package: the scrub math was correct and unreachable.
    /// </summary>
    private readonly Border _scrubSurface = new()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
    };

    private bool _pointerDown;
    private bool _dragging;
    private double _dragStartX;
    private double _dragStartValue;
    private bool _syncingText;
    private string _lastSyncedText = "";
    private readonly Action _flashCommit;

    public UnitNumberField()
    {
        _root = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
            },
        };
        Grid.SetColumn(_text, 0);
        Grid.SetColumn(_scrubSurface, 0);
        Grid.SetColumn(_unit, 1);
        _root.Children.Add(_text);
        _root.Children.Add(_scrubSurface);   // above the text box while not editing
        _root.Children.Add(_unit);

        _text.LostFocus += (_, _) =>
        {
            IsCommittingFromLostFocus = true;
            try
            {
                CommitFromText();
            }
            finally
            {
                IsCommittingFromLostFocus = false;
            }
            _scrubSurface.Visibility = Visibility.Visible;   // scrubbing available again
        };
        _text.AddHandler(KeyDownEvent, new KeyEventHandler(OnTextKeyDown), handledEventsToo: true);
        _text.GotFocus += (_, _) => ShowEditableText();

        _scrubSurface.PointerPressed += OnPointerPressed;
        _scrubSurface.PointerMoved += OnPointerMoved;
        _scrubSurface.PointerReleased += OnPointerReleased;
        _scrubSurface.PointerCaptureLost += OnPointerCaptureLost;
        _scrubSurface.PointerEntered += (_, _) => SetScrubCursor(true);
        _scrubSurface.PointerExited += (_, _) => SetScrubCursor(false);
        _root.PointerWheelChanged += OnPointerWheel;

        Content = _root;
        IsTabStop = false;
        _flashCommit = Motion.AttachFieldChrome(_text, _root);
        Sync();
    }

    /// <summary>Raised after any committed change, whatever the input route.</summary>
    public event EventHandler<double>? ValueChanged;
    public event RoutedEventHandler? CommitRequested;

    /// <summary>
    /// WP-269: raised once when a scrub drag ends (pointer release or capture loss after a drag).
    /// A drag raises <see cref="ValueChanged"/> on every tick but neither <c>LostFocus</c> nor
    /// <see cref="CommitRequested"/>, so without this the Inspector's live edit would stay open
    /// until focus happened to move — the Adjust tab's drags never reached the clip at all.
    /// A press that never becomes a drag is a click-to-type and does not raise this.
    /// </summary>
    public event EventHandler? ScrubCompleted;

    public static readonly DependencyProperty SpecProperty = DependencyProperty.Register(
        nameof(Spec), typeof(UnitFieldSpec), typeof(UnitNumberField),
        new PropertyMetadata(new UnitFieldSpec(), (d, _) => ((UnitNumberField)d).Sync()));

    /// <summary>Unit, range, step, drag sensitivity and default. Never null.</summary>
    public UnitFieldSpec Spec
    {
        get => (UnitFieldSpec)GetValue(SpecProperty) ?? new UnitFieldSpec();
        set => SetValue(SpecProperty, value ?? new UnitFieldSpec());
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(UnitNumberField),
        new PropertyMetadata(0d, (d, _) => ((UnitNumberField)d).Sync()));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty IsMixedProperty = DependencyProperty.Register(
        nameof(IsMixed), typeof(bool), typeof(UnitNumberField),
        new PropertyMetadata(false, (d, _) => ((UnitNumberField)d).Sync()));

    /// <summary>
    /// WP-166. Mac <c>ScrubbableNumberField</c> when <c>value == nil</c>: the field paints
    /// <see cref="MixedValue.Marker"/>, scrubbing is off, and a typed commit clears the mixed state
    /// so the new number stamps the whole selection.
    /// </summary>
    public bool IsMixed
    {
        get => (bool)GetValue(IsMixedProperty);
        set => SetValue(IsMixedProperty, value);
    }

    /// <summary>True when the value differs from the spec's default — drive an <see cref="EditorRow"/> reset with this.</summary>
    public bool DiffersFromDefault => !IsMixed && Spec.DiffersFromDefault(Value);

    /// <summary>True while <see cref="ValueChanged"/> is firing specifically due to focus loss.</summary>
    internal bool IsCommittingFromLostFocus { get; private set; }

    /// <summary>
    /// WP-253: the field holds typing that has not been committed. Only then are the undo keys the
    /// text box's own; a committed field's text is exactly what <see cref="Sync"/> writes on focus.
    /// </summary>
    public bool HasPendingText
    {
        get
        {
            if (_text.FocusState == FocusState.Unfocused) return false;
            var text = _text.Text;
            // A mixed field opens empty for typing, so empty is its committed text.
            if (IsMixed && text.Length == 0) return false;
            return !string.Equals(text, _lastSyncedText, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Flushes any uncommitted typing immediately so playhead seeks or adjacent button clicks
    /// commit at the frame where the user typed instead of losing or mis-framing the edit.
    /// </summary>
    public bool CommitPendingEdit()
    {
        var text = _text.Text;
        if (IsMixed && text.Length == 0) return false;
        if (string.Equals(text, _lastSyncedText, StringComparison.Ordinal)) return false;
        CommitFromText();
        return true;
    }

    /// <summary>Restores the spec default through the same commit path as every other route.</summary>
    public void ResetToDefault() => Commit(Spec.Default);

    /// <summary>
    /// The single write path. Everything else — typing, dragging, wheel, arrows, reset — calls this.
    /// </summary>
    private void Commit(double raw)
    {
        var clamped = Spec.Clamp(raw);
        var wasMixed = IsMixed;
        if (wasMixed) IsMixed = false;
        var changed = wasMixed || Math.Abs(clamped - Value) > double.Epsilon;
        Value = clamped;          // Sync() runs via the property callback
        if (changed)
        {
            ValueChanged?.Invoke(this, clamped);
            _flashCommit();
        }
        else Sync();              // repaint a rejected edit back to the good value
    }

    private void CommitFromText()
    {
        // Avoid spurious commits (and auto-keyframes) when a fractional interpolated value was
        // focused and blurred without the user editing the text.
        if (string.Equals(_text.Text, _lastSyncedText, StringComparison.Ordinal))
        {
            Sync();
            return;
        }
        if (Spec.TryParse(_text.Text, out var parsed)) Commit(parsed);
        else Sync();              // unparseable: revert rather than guess
    }

    private void Sync()
    {
        if (_syncingText) return;
        _syncingText = true;
        var spec = Spec;
        var id = AutomationProperties.GetAutomationId(this);
        if (!string.IsNullOrEmpty(id))
            AutomationProperties.SetAutomationId(_text, id);
        if (IsMixed && _text.FocusState == FocusState.Unfocused)
        {
            _text.Text = MixedValue.Marker;
            _lastSyncedText = _text.Text;
            _unit.Text = "";
            _unit.Visibility = Visibility.Collapsed;
            _text.Foreground = AppTheme.Text.TertiaryBrush;
            AutomationProperties.SetName(_text, MixedValue.Marker);
            _syncingText = false;
            return;
        }

        _text.ClearValue(Control.ForegroundProperty);
        _text.Text = _text.FocusState != FocusState.Unfocused
            ? spec.FormatEditable(Value)
            : spec.Format(Value);
        _lastSyncedText = _text.Text;
        // The suffix lives inside the field's own text when unfocused, so the separate unit label
        // only shows while editing — otherwise the unit would appear twice.
        _unit.Text = _text.FocusState != FocusState.Unfocused ? spec.Unit : "";
        _unit.Visibility = string.IsNullOrEmpty(_unit.Text) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(_text, spec.Format(Value));
        _syncingText = false;
    }

    private void ShowEditableText()
    {
        _syncingText = true;
        _text.ClearValue(Control.ForegroundProperty);
        _text.Text = IsMixed ? "" : Spec.FormatEditable(Value);
        _lastSyncedText = _text.Text;
        _unit.Text = Spec.Unit;
        _unit.Visibility = string.IsNullOrEmpty(Spec.Unit) ? Visibility.Collapsed : Visibility.Visible;
        _text.SelectAll();
        _syncingText = false;
    }

    private static bool ShiftHeld()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

    private void OnTextKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                CommitFromText();
                CommitRequested?.Invoke(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                Sync();
                e.Handled = true;
                break;
            case VirtualKey.Up:
                if (IsMixed) { e.Handled = true; break; }
                Commit(Spec.Nudge(Value, 1, ShiftHeld()));
                e.Handled = true;
                break;
            case VirtualKey.Down:
                if (IsMixed) { e.Handled = true; break; }
                Commit(Spec.Nudge(Value, -1, ShiftHeld()));
                e.Handled = true;
                break;
        }
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        // Only while focused, so scrolling a panel does not silently rewrite values under the cursor.
        if (_text.FocusState == FocusState.Unfocused || IsMixed) return;
        var delta = e.GetCurrentPoint(_root).Properties.MouseWheelDelta;
        if (delta == 0) return;
        Commit(Spec.Nudge(Value, Math.Sign(delta), ShiftHeld()));
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_root);
        if (!point.Properties.IsLeftButtonPressed) return;
        // A scrub owns keyboard Undo too; a closed dialog or popup may still hold focus.
        _text.Focus(FocusState.Pointer);
        _pointerDown = true;
        _dragging = false;
        _dragStartX = point.Position.X;
        _dragStartValue = Value;
        _scrubSurface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown || IsMixed) return;
        var dx = e.GetCurrentPoint(_root).Position.X - _dragStartX;
        if (!_dragging && Math.Abs(dx) < DragThresholdPixels) return;
        _dragging = true;
        Commit(Spec.Scrub(_dragStartValue, dx, ShiftHeld()));
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        var wasDragging = _dragging;
        _scrubSurface.ReleasePointerCapture(e.Pointer);
        EndDrag();
        // A press that never became a drag is a click: step aside and let the user type.
        if (!wasDragging)
        {
            _scrubSurface.Visibility = Visibility.Collapsed;
            _text.Focus(FocusState.Programmatic);
        }
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndDrag();

    private void EndDrag()
    {
        var wasDragging = _dragging;
        _pointerDown = false;
        _dragging = false;
        if (wasDragging)
            ScrubCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void SetScrubCursor(bool scrub)
        => ProtectedCursor = InputSystemCursor.Create(
            scrub ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.Arrow);
}
