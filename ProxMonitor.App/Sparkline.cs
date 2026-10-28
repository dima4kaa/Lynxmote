using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ProxMonitor.App;

/// <summary>Line chart without axes: tiny in table cells, with fill and grid in the details window.</summary>
public sealed class Sparkline : Control
{
    static readonly IBrush GridBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));

    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke));

    /// <summary>Top of the scale; 0 scales to the largest value.</summary>
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum), 100);

    /// <summary>Points the width stands for; 0 fits the values. Lets history grow in from the right.</summary>
    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<Sparkline, int>(nameof(Capacity));

    public static readonly StyledProperty<bool> FillProperty =
        AvaloniaProperty.Register<Sparkline, bool>(nameof(Fill));

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<Sparkline, bool>(nameof(ShowGrid));

    static Sparkline() => AffectsRender<Sparkline>(
        ValuesProperty, StrokeProperty, MaximumProperty, CapacityProperty, FillProperty, ShowGridProperty);

    public IReadOnlyList<double>? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Capacity { get => GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public bool Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (ShowGrid)
            for (var i = 1; i < 4; i++)
            {
                var y = Math.Round(h * i / 4) + 0.5;
                context.DrawLine(new Pen(GridBrush), new Point(0, y), new Point(w, y));
            }

        var v = Values;
        if (v is null || v.Count < 2) return;

        var max = Maximum > 0 ? Maximum : Math.Max(1, v.Max());
        var slots = Math.Max(Capacity, v.Count) - 1;
        var offset = slots - (v.Count - 1);
        Point At(int i) => new((offset + i) * w / slots, h - Math.Clamp(v[i] / max, 0, 1) * (h - 1));

        var stroke = Stroke ?? Brushes.DodgerBlue;
        if (Fill && stroke is ISolidColorBrush solid)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(At(0).X, h), true);
                for (var i = 0; i < v.Count; i++) g.LineTo(At(i));
                g.LineTo(new Point(w, h));
                g.EndFigure(true);
            }
            var c = solid.Color;
            context.DrawGeometry(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(80, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1) },
            }, null, area);
        }

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(At(0), false);
            for (var i = 1; i < v.Count; i++) g.LineTo(At(i));
            g.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(stroke, 1.5, lineJoin: PenLineJoin.Round), line);
    }
}
