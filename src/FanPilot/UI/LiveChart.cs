using System.Globalization;
using System.Windows;
using System.Windows.Media;
using FanPilot.Control;

namespace FanPilot.UI;

/// <summary>Two-minute history: CPU temperature on the left axis, fan RPM on the right.</summary>
public sealed class LiveChart : FrameworkElement
{
    static readonly TimeSpan Window = TimeSpan.FromMinutes(2);
    const double TempMin = 30, TempMax = 100;
    const double PadLeft = 34, PadRight = 46, PadTop = 8, PadBottom = 8;

    readonly List<FanSnapshot> _samples = [];
    double _rpmScale = 6000;

    public void Add(FanSnapshot snapshot)
    {
        // A clock correction can put old samples "in the future"; they'd draw past the right edge.
        _samples.RemoveAll(s => s.Time > snapshot.Time);
        _samples.Add(snapshot);
        var cutoff = snapshot.Time - Window;
        int stale = _samples.FindIndex(s => s.Time >= cutoff);
        if (stale > 0) _samples.RemoveRange(0, stale);

        int maxRpm = _samples.SelectMany(s => s.Rpms).DefaultIfEmpty(0).Max();
        _rpmScale = Math.Max(3000, Math.Ceiling(maxRpm / 1000.0) * 1000);

        if (IsVisible) InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= PadLeft + PadRight || h <= PadTop + PadBottom) return;

        var plot = new Rect(PadLeft, PadTop, w - PadLeft - PadRight, h - PadTop - PadBottom);
        var grid = new Pen(ChartColors.Resource(this, "ControlStrokeColorDefaultBrush", Colors.Gray), 1);
        var label = ChartColors.Resource(this, "TextFillColorTertiaryBrush", Colors.Gray);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        foreach (double t in new double[] { 40, 60, 80, 100 })
        {
            double y = TempY(t, plot);
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(dc, $"{t:0}°", label, new Point(plot.Left - 6, y), dpi, alignRight: true);
            double rpm = (t - TempMin) / (TempMax - TempMin) * _rpmScale;
            DrawText(dc, $"{rpm / 1000:0.#}k", label, new Point(plot.Right + 6, y), dpi, alignRight: false);
        }

        if (_samples.Count < 2) return;
        var end = _samples[^1].Time;
        double X(DateTime time) => plot.Right - (end - time).TotalSeconds / Window.TotalSeconds * plot.Width;

        int fans = _samples.Max(s => s.Rpms.Length);
        for (int f = 0; f < fans; f++)
        {
            DrawSeries(dc, new Pen(ChartColors.Fan(f), 1.6),
                _samples.Where(s => f < s.Rpms.Length)
                    .Select(s => new Point(X(s.Time), plot.Bottom - Math.Min(1, s.Rpms[f] / _rpmScale) * plot.Height)));
        }

        // The GPU line breaks while the GPU sleeps instead of bridging the gap.
        var gpuPen = new Pen(ChartColors.GpuTemp, 1.6);
        var segment = new List<Point>();
        foreach (var s in _samples)
        {
            if (s.GpuTemp is int g)
            {
                segment.Add(new Point(X(s.Time), TempY(g, plot)));
                continue;
            }
            DrawSeries(dc, gpuPen, segment);
            segment = [];
        }
        DrawSeries(dc, gpuPen, segment);

        DrawSeries(dc, new Pen(ChartColors.Temp, 2),
            _samples.Where(s => s.CpuTemp is not null).Select(s => new Point(X(s.Time), TempY(s.CpuTemp!.Value, plot))));
    }

    static double TempY(double temp, Rect plot) =>
        plot.Bottom - (Math.Clamp(temp, TempMin, TempMax) - TempMin) / (TempMax - TempMin) * plot.Height;

    static void DrawSeries(DrawingContext dc, Pen pen, IEnumerable<Point> points)
    {
        var list = points.ToList();
        if (list.Count < 2) return;
        pen.LineJoin = PenLineJoin.Round;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(list[0], false, false);
            ctx.PolyLineTo(list.Skip(1).ToList(), true, true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    internal static void DrawText(DrawingContext dc, string text, Brush brush, Point anchor, double dpi, bool alignRight)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 11, brush, dpi);
        double x = alignRight ? anchor.X - formatted.Width : anchor.X;
        dc.DrawText(formatted, new Point(x, anchor.Y - formatted.Height / 2));
    }
}
