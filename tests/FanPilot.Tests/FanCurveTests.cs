using FanPilot.Control;

namespace FanPilot.Tests;

public class FanCurveTests
{
    static FanCurve Curve(params (double Temp, double Percent)[] points) =>
        new() { Points = points.Select(p => new CurvePoint(p.Temp, p.Percent)).ToList() };

    [Fact]
    public void Evaluate_is_flat_outside_the_points_and_linear_between()
    {
        var curve = Curve((50, 20), (70, 60));
        Assert.Equal(20, curve.Evaluate(30));
        Assert.Equal(40, curve.Evaluate(60));
        Assert.Equal(60, curve.Evaluate(95));
    }

    [Fact]
    public void Normalized_drops_nulls_and_clamps_to_range_and_floor()
    {
        var curve = new FanCurve { Points = [new(20, 5), null!, new(150, 120), new(60, 40)] }.Normalized(15);
        Assert.Equal([30.0, 60.0, 100.0], curve.Points.Select(p => p.Temp));
        Assert.Equal([15.0, 40.0, 100.0], curve.Points.Select(p => p.Percent));
    }

    [Fact]
    public void Normalized_keeps_points_at_least_two_degrees_apart()
    {
        var curve = Curve((50, 20), (51, 30), (53, 40), (80, 90)).Normalized(10);
        Assert.Equal([50.0, 53.0, 80.0], curve.Points.Select(p => p.Temp));
    }

    [Fact]
    public void Normalized_falls_back_to_the_default_when_too_few_points_remain()
    {
        var curve = new FanCurve { Points = null! }.Normalized(20);
        Assert.Equal(FanCurve.CreateDefault(20).Points.Select(p => p.Percent), curve.Points.Select(p => p.Percent));
    }

    [Fact]
    public void Default_curve_starts_at_the_floor()
    {
        Assert.Equal(15, FanCurve.CreateDefault(15).Points[0].Percent);
        Assert.Equal(30, FanCurve.CreateDefault(30).Points[0].Percent);
    }

    [Fact]
    public void Config_normalization_clamps_manual_speed_and_overrides()
    {
        var config = new FanConfig { ManualPercent = 5, OverrideTemp = 200, GpuOverrideTemp = 10, Mode = (FanMode)42 }.Normalized(25);
        Assert.Equal(25, config.ManualPercent);
        Assert.Equal(100, config.OverrideTemp);
        Assert.Equal(75, config.GpuOverrideTemp);
        Assert.Equal(FanMode.AsusAuto, config.Mode);
    }
}
