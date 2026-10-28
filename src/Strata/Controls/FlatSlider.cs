using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Strata.Controls;

/// <summary>
/// Horizontal 0..100 slider drawn to match the dock. A real <see cref="RangeBase"/>, so screen readers get the
/// RangeValue pattern, and it works from the keyboard (arrows, Page Up/Down, Home/End).
/// </summary>
public sealed class FlatSlider : RangeBase
{
    /// <summary>Raised only for changes the user made (mouse, wheel, keyboard), not for programmatic updates.</summary>
    public event Action<double>? UserChanged;

    public FlatSlider()
    {
        Minimum = 0;
        Maximum = 100;
        SmallChange = 2;
        LargeChange = 10;
        Height = 24;
        Cursor = Cursors.Hand;
        Focusable = true;
        FocusVisualStyle = FocusVisuals.Rounded;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FlatSliderPeer(this);

    private void SetByUser(double v)
    {
        Value = Math.Clamp(v, Minimum, Maximum);
        UserChanged?.Invoke(Value);
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        CaptureMouse();
        SetFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (IsMouseCaptured) SetFromPoint(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        SetByUser(Value + (e.Delta > 0 ? SmallChange : -SmallChange));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double? next = e.Key switch
        {
            Key.Left or Key.Down => Value - SmallChange,
            Key.Right or Key.Up => Value + SmallChange,
            Key.PageDown => Value - LargeChange,
            Key.PageUp => Value + LargeChange,
            Key.Home => Minimum,
            Key.End => Maximum,
            _ => null,
        };
        if (next is { } v) { SetByUser(v); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    private void SetFromPoint(Point p)
    {
        const double pad = 8;
        double w = Math.Max(1, ActualWidth - pad * 2);
        SetByUser(Minimum + (p.X - pad) / w * (Maximum - Minimum));
    }

    protected override void OnRender(DrawingContext dc)
    {
        const double pad = 8, track = 4, thumb = 14;
        double w = ActualWidth - pad * 2, y = ActualHeight / 2;
        if (w <= 0) return;
        var accent = TryFindResource("Accent") as Brush ?? Brushes.DodgerBlue;
        var rest = TryFindResource("InputBg") as Brush ?? Brushes.Gray;
        var fg = TryFindResource("FlyoutFg") as Brush ?? Brushes.White;
        double fraction = Maximum > Minimum ? (Value - Minimum) / (Maximum - Minimum) : 0;
        double x = pad + w * fraction;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // whole area is clickable
        dc.DrawRoundedRectangle(rest, null, new Rect(pad, y - track / 2, w, track), track / 2, track / 2);
        dc.DrawRoundedRectangle(accent, null, new Rect(pad, y - track / 2, Math.Max(0, x - pad), track), track / 2, track / 2);
        dc.DrawEllipse(fg, new Pen(accent, 2), new Point(x, y), thumb / 2, thumb / 2);
    }

    private sealed class FlatSliderPeer(FlatSlider owner) : RangeBaseAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        protected override string GetClassNameCore() => nameof(FlatSlider);
    }
}
