using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FanPilot.Control;

namespace FanPilot.UI;

/// <summary>
/// Temperature to duty curve. Drag a point to move it, double-click empty space to add one,
/// right-click a point to remove it. A vertical marker shows the current CPU temperature.
/// </summary>
public sealed class CurveEditor : FrameworkElement
{
    const double PadLeft = 40, PadRight = 14, PadTop = 12, PadBottom = 26;
    const double HitRadius = 10;
    const int MaxPoints = 8;

    FanCurve _curve = FanCurve.CreateDefault(CalibrationProfile.UncalibratedFloor);
    CurvePoint? _dragging;
    double? _currentTemp;
    int _minPercent = CalibrationProfile.UncalibratedFloor;

    public event Action? CurveChanged;

    public CurveEditor()
    {
        Focusable = false;
        Cursor = Cursors.Arrow;
    }

    public FanCurve Curve
    {
        get => _curve;
        set
        {
            // A replacement mid-drag (another window's edit) orphans the dragged point; drop the drag.
            if (_dragging is not null)
            {
                _dragging = null;
                ReleaseMouseCapture();
            }
            _curve = value;
            InvalidateVisual();
        }
    }

    public double? CurrentTemp
    {
        get => _currentTemp;
        set { _currentTemp = value; if (IsVisible) InvalidateVisual(); }
    }

    public int MinPercent
    {
        get => _minPercent;
        set
        {
            _minPercent = value;
            foreach (var p in _curve.Points) p.Percent = Math.Max(p.Percent, value);
            InvalidateVisual();
        }
    }

    Rect Plot => new(PadLeft, PadTop,
        Math.Max(1, ActualWidth - PadLeft - PadRight), Math.Max(1, ActualHeight - PadTop - PadBottom));

    Point ToScreen(double temp, double percent)
    {
        var plot = Plot;
        return new Point(
            plot.Left + (temp - FanCurve.MinTemp) / (FanCurve.MaxTemp - FanCurve.MinTemp) * plot.Width,
            plot.Bottom - percent / 100 * plot.Height);
    }

    (double Temp, double Percent) FromScreen(Point p)
    {
        var plot = Plot;
        double temp = FanCurve.MinTemp + (p.X - plot.Left) / plot.Width * (FanCurve.MaxTemp - FanCurve.MinTemp);
        double percent = (plot.Bottom - p.Y) / plot.Height * 100;
        return (Math.Round(temp), Math.Round(percent));
    }

    CurvePoint? HitTest(Point p) => _curve.Points
        .Select(cp => (cp, d: (ToScreen(cp.Temp, cp.Percent) - p).Length))
        .Where(x => x.d <= HitRadius)
        .OrderBy(x => x.d)
        .Select(x => x.cp)
        .FirstOrDefault();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(this);
        var hit = HitTest(pos);

        if (e.ClickCount == 2 && hit is null && _curve.Points.Count < MaxPoints)
        {
            var (t, pct) = FromScreen(pos);
            if (t > FanCurve.MinTemp && t < FanCurve.MaxTemp && _curve.Points.All(p => Math.Abs(p.Temp - t) >= FanCurve.MinGap))
            {
                _curve.Points.Add(new CurvePoint(t, Math.Clamp(pct, _minPercent, 100)));
                _curve.Points.Sort((a, b) => a.Temp.CompareTo(b.Temp));
                InvalidateVisual();
                CurveChanged?.Invoke();
            }
        }
        else if (hit is not null)
        {
            _dragging = hit;
            CaptureMouse();
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (_dragging is null)
        {
            Cursor = HitTest(pos) is null ? Cursors.Arrow : Cursors.Hand;
            return;
        }

        // A point stays between its neighbours so the curve remains a function of temperature.
        var sorted = _curve.Points.OrderBy(p => p.Temp).ToList();
        int i = sorted.IndexOf(_dragging);
        if (i < 0) return;
        // Same spacing FanCurve.Normalized enforces, or the service would drop the point.
        double lo = i > 0 ? sorted[i - 1].Temp + FanCurve.MinGap : FanCurve.MinTemp;
        double hi = i < sorted.Count - 1 ? sorted[i + 1].Temp - FanCurve.MinGap : FanCurve.MaxTemp;

        var (t, pct) = FromScreen(pos);
        _dragging.Temp = Math.Clamp(t, lo, hi);
        _dragging.Percent = Math.Clamp(pct, _minPercent, 100);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => EndDrag();

    /// <summary>Alt-Tab mid-drag takes capture away without a button-up; commit what was dragged.</summary>
    protected override void OnLostMouseCapture(MouseEventArgs e) => EndDrag();

    void EndDrag()
    {
        if (_dragging is null) return;
        _dragging = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
        CurveChanged?.Invoke();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        var hit = HitTest(e.GetPosition(this));
        if (hit is not null && _curve.Points.Count > 2)
        {
            _curve.Points.Remove(hit);
            InvalidateVisual();
            CurveChanged?.Invoke();
        }
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < PadLeft + PadRight + 10 || ActualHeight < PadTop + PadBottom + 10) return;

        var plot = Plot;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gridPen = new Pen(ChartColors.Resource(this, "ControlStrokeColorDefaultBrush", Colors.Gray), 1);
        var label = ChartColors.Resource(this, "TextFillColorTertiaryBrush", Colors.Gray);
        var accent = ChartColors.Resource(this, "AccentFillColorDefaultBrush", Color.FromRgb(0x4C, 0xC2, 0xFF));
        var pointFill = ChartColors.Resource(this, "SolidBackgroundFillColorBaseBrush", Colors.Black);

        // Transparent fill so empty space receives mouse input.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        for (int pct = 0; pct <= 100; pct += 25)
        {
            var a = ToScreen(FanCurve.MinTemp, pct);
            dc.DrawLine(gridPen, a, ToScreen(FanCurve.MaxTemp, pct));
            LiveChart.DrawText(dc, $"{pct}%", label, new Point(plot.Left - 6, a.Y), dpi, alignRight: true);
        }
        for (int t = 30; t <= 100; t += 10)
        {
            var a = ToScreen(t, 0);
            dc.DrawLine(gridPen, a, ToScreen(t, 100));
            LiveChart.DrawText(dc, $"{t}°", label, new Point(a.X - 8, plot.Bottom + 13), dpi, alignRight: false);
        }

        var points = _curve.Points.OrderBy(p => p.Temp).ToList();
        if (points.Count == 0) return;

        // Flat run-in and run-out make the plotted line match FanCurve.Evaluate.
        var line = new List<Point> { ToScreen(FanCurve.MinTemp, points[0].Percent) };
        line.AddRange(points.Select(p => ToScreen(p.Temp, p.Percent)));
        line.Add(ToScreen(FanCurve.MaxTemp, points[^1].Percent));

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(line[0].X, plot.Bottom), true, true);
            ctx.PolyLineTo(line, false, true);
            ctx.LineTo(new Point(line[^1].X, plot.Bottom), false, true);
        }
        area.Freeze();
        dc.DrawGeometry(ChartColors.WithOpacity(accent, 0.18), null, area);

        var stroke = new StreamGeometry();
        using (var ctx = stroke.Open())
        {
            ctx.BeginFigure(line[0], false, false);
            ctx.PolyLineTo(line.Skip(1).ToList(), true, true);
        }
        stroke.Freeze();
        dc.DrawGeometry(null, new Pen(accent, 2) { LineJoin = PenLineJoin.Round }, stroke);

        if (_currentTemp is double temp)
        {
            double clamped = Math.Clamp(temp, FanCurve.MinTemp, FanCurve.MaxTemp);
            var top = ToScreen(clamped, 100);
            var bottom = ToScreen(clamped, 0);
            dc.DrawLine(new Pen(ChartColors.Temp, 1) { DashStyle = DashStyles.Dash }, top, bottom);
            var at = ToScreen(clamped, _curve.Evaluate(temp));
            dc.DrawEllipse(ChartColors.Temp, null, at, 4, 4);
            LiveChart.DrawText(dc, $"{temp:0}°", ChartColors.Temp, new Point(top.X + 5, top.Y + 8), dpi, alignRight: false);
        }

        foreach (var p in points)
        {
            var c = ToScreen(p.Temp, p.Percent);
            dc.DrawEllipse(pointFill, new Pen(accent, 2.5), c, 6, 6);
            if (p == _dragging)
            {
                LiveChart.DrawText(dc, $"{p.Temp:0}° → {p.Percent:0}%", label, new Point(c.X + 10, c.Y - 14), dpi, alignRight: false);
            }
        }
    }
}
