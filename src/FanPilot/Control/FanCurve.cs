namespace FanPilot.Control;

public sealed class CurvePoint
{
    public double Temp { get; set; }
    public double Percent { get; set; }

    public CurvePoint() { }
    public CurvePoint(double temp, double percent) => (Temp, Percent) = (temp, percent);
}

/// <summary>Piecewise-linear map from temperature (C) to fan duty (%).</summary>
public sealed class FanCurve
{
    public const double MinTemp = 30, MaxTemp = 100;

    public List<CurvePoint> Points { get; set; } = [];

    /// <summary>The lowest safe duty through 60 C, then up to full speed by 90 C.</summary>
    public static FanCurve CreateDefault(int floor) => new()
    {
        Points = [new(60, floor), new(72, Math.Max(floor, 50)), new(82, Math.Max(floor, 75)), new(90, 100)],
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

    public const double MinGap = 2;   // C between points; the editor relies on it to keep drag bounds valid

    /// <summary>
    /// Repairs a curve from disk or the pipe: no nulls, in range, sorted, points at least 2 C
    /// apart, two to eight points. Anything unusable becomes the default curve.
    /// </summary>
    public FanCurve Normalized(int minPercent)
    {
        var points = new List<CurvePoint>();
        foreach (var p in (Points ?? []).Where(p => p is not null && double.IsFinite(p.Temp) && double.IsFinite(p.Percent))
                                        .OrderBy(p => p.Temp))
        {
            var point = new CurvePoint(
                Math.Round(Math.Clamp(p.Temp, MinTemp, MaxTemp)),
                Math.Round(Math.Clamp(p.Percent, minPercent, 100)));
            if (points.Count > 0 && point.Temp - points[^1].Temp < MinGap) continue;
            points.Add(point);
        }
        return points.Count is >= 2 and <= 8 ? new FanCurve { Points = points } : CreateDefault(minPercent);
    }
}
