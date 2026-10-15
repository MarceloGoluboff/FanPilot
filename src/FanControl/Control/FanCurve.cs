namespace FanControl.Control;

public sealed class CurvePoint
{
    public double Temp { get; set; }
    public double Percent { get; set; }

    public CurvePoint() { }
    public CurvePoint(double temp, double percent) => (Temp, Percent) = (temp, percent);
}

/// <summary>Piecewise-linear map from CPU temperature (C) to fan duty (%).</summary>
public sealed class FanCurve
{
    public const double MinTemp = 30, MaxTemp = 100;

    public List<CurvePoint> Points { get; set; } = [];

    public static FanCurve CreateDefault() => new()
    {
        Points = [new(40, 30), new(60, 50), new(75, 75), new(85, 100)],
    };

    public FanCurve Clone() => new() { Points = Points.Select(p => new CurvePoint(p.Temp, p.Percent)).ToList() };

    /// <summary>Flat below the first point and above the last, linear in between.</summary>
    public double Evaluate(double temp)
    {
        if (Points.Count == 0) return 100;
        var sorted = Points.OrderBy(p => p.Temp).ToList();
        if (temp <= sorted[0].Temp) return sorted[0].Percent;
        if (temp >= sorted[^1].Temp) return sorted[^1].Percent;

        for (int i = 1; i < sorted.Count; i++)
        {
            var (a, b) = (sorted[i - 1], sorted[i]);
            if (temp <= b.Temp)
            {
                double span = b.Temp - a.Temp;
                return span <= 0 ? b.Percent : a.Percent + (b.Percent - a.Percent) * (temp - a.Temp) / span;
            }
        }
        return sorted[^1].Percent;
    }

    /// <summary>Repairs a curve loaded from disk: in range, sorted, at least two points.</summary>
    public FanCurve Normalized(int minPercent)
    {
        var points = Points
            .Where(p => double.IsFinite(p.Temp) && double.IsFinite(p.Percent))
            .Select(p => new CurvePoint(
                Math.Clamp(p.Temp, MinTemp, MaxTemp),
                Math.Clamp(p.Percent, minPercent, 100)))
            .OrderBy(p => p.Temp)
            .ToList();
        return points.Count >= 2 ? new FanCurve { Points = points } : CreateDefault();
    }
}
