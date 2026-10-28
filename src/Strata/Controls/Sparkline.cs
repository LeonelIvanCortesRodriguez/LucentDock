using System.Windows;
using System.Windows.Media;

namespace Strata.Controls;

/// <summary>A tiny line chart of recent values in 0..1, drawn directly for speed.</summary>
public sealed class Sparkline : FrameworkElement
{
    private readonly Queue<double> _values = new();
    public int Capacity { get; set; } = 40;
    public Brush? Stroke { get; set; }
    public double StrokeThickness { get; set; } = 1.5;
    public bool Filled { get; set; } = true;

    public void Push(double value)
    {
        _values.Enqueue(Math.Clamp(value, 0, 1));
        while (_values.Count > Capacity) _values.Dequeue();
        InvalidateVisual();
    }

    public void SetValues(IEnumerable<double> values)
    {
        _values.Clear();
        foreach (var v in values) _values.Enqueue(Math.Clamp(v, 0, 1));
        while (_values.Count > Capacity) _values.Dequeue();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || _values.Count < 2) return;
        var brush = Stroke ?? (TryFindResource("Accent") as Brush) ?? Brushes.DodgerBlue;
        var pts = _values.ToArray();
        double step = w / (Capacity - 1);
        double x0 = w - (pts.Length - 1) * step;

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(x0, h - pts[0] * h), false, false);
            for (int i = 1; i < pts.Length; i++) ctx.LineTo(new Point(x0 + i * step, h - pts[i] * h), true, true);
        }
        line.Freeze();

        if (Filled)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(x0, h), true, true);
                for (int i = 0; i < pts.Length; i++) ctx.LineTo(new Point(x0 + i * step, h - pts[i] * h), true, true);
                ctx.LineTo(new Point(w, h), true, true);
            }
            area.Freeze();
            var fill = brush.Clone();
            fill.Opacity = 0.22;
            dc.DrawGeometry(fill, null, area);
        }
        dc.DrawGeometry(null, new Pen(brush, StrokeThickness) { LineJoin = PenLineJoin.Round }, line);
    }
}
