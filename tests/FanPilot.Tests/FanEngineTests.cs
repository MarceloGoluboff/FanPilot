using FanPilot.Control;
using FanPilot.Hardware;
using Microsoft.Extensions.Time.Testing;

namespace FanPilot.Tests;

public class FanEngineTests
{
    readonly FakeBackend _fans = new();
    readonly FakeTimeProvider _time = new();

    FanEngine Engine(FanConfig config, CalibrationProfile? profile = null) =>
        new(_fans, new TemperatureReader(_fans, () => _fans.GpuTemp), config.Normalized(profile?.Floor ?? 30), profile, _time);

    static CalibrationProfile Profile(params FanProfile[] fans) =>
        new(DateTime.UtcNow, "Test", "BIOS", "1.0", true, [.. fans]);

    static FanProfile Fan(int index, int min, bool controllable = true) =>
        new(index, controllable, min - 10, 0, min, 2500, min + 10, []);

    void Tick(FanEngine engine, int seconds = 1)
    {
        for (int i = 0; i < seconds; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            engine.Tick();
        }
    }

    [Fact]
    public void Manual_mode_holds_the_duty_on_every_fan()
    {
        var engine = Engine(new FanConfig { Mode = FanMode.Manual, ManualPercent = 60 });
        Tick(engine);
        Assert.Equal([60, 60], _fans.Duties);
    }

    [Fact]
    public void Uncalibrated_floor_is_30_percent()
    {
        var engine = Engine(new FanConfig { Mode = FanMode.Manual, ManualPercent = 10 });
        Tick(engine);
        Assert.Equal(30, _fans.Duties[0]);
    }

    [Fact]
    public void Calibrated_floor_is_the_highest_fan_minimum()
    {
        var engine = Engine(new FanConfig { Mode = FanMode.Manual, ManualPercent = 15 }, Profile(Fan(0, 15), Fan(1, 20)));
        Tick(engine);
        Assert.Equal([20, 20], _fans.Duties);
    }

    [Fact]
    public void Fans_that_do_not_follow_commands_stay_with_asus()
    {
        var engine = Engine(new FanConfig { Mode = FanMode.Manual, ManualPercent = 60 }, Profile(Fan(0, 15), Fan(1, 30, controllable: false)));
        Tick(engine);
        Assert.Equal(60, _fans.Duties[0]);
        Assert.Null(_fans.Duties[1]);
    }

    [Fact]
    public void Cpu_override_forces_full_speed_and_releases_ten_degrees_lower()
    {
        var engine = Engine(new FanConfig { Mode = FanMode.Manual, ManualPercent = 40, OverrideTemp = 90 });
        _fans.CpuTemp = 91;
        Tick(engine);
        Assert.Equal(100, _fans.Duties[0]);

        _fans.CpuTemp = 82;
        Tick(engine);
        Assert.Equal(100, _fans.Duties[0]);

        _fans.CpuTemp = 80;
        Tick(engine);
        Assert.Equal(40, _fans.Duties[0]);
    }

    [Fact]
    public void Gpu_override_forces_full_speed()
    {
        var engine = Engine(new FanConfig { Mode = FanMode.Manual, ManualPercent = 40, GpuOverrideTemp = 87 });
        _fans.GpuTemp = 88;
        Tick(engine);
        Assert.Equal(100, _fans.Duties[0]);

        // A GPU that goes to sleep stops reporting; it can't still be overheating.
        _fans.GpuTemp = null;
        Tick(engine);
        Assert.Equal(40, _fans.Duties[0]);
    }

    [Fact]
    public void Losing_the_cpu_temperature_hands_manual_mode_back_to_asus()
    {
        var engine = Engine(new FanConfig { Mode = FanMode.Manual, ManualPercent = 40 });
        Tick(engine);
        _fans.CpuTemp = null;
        Tick(engine, 4);
        Assert.Equal(40, _fans.Duties[0]);
        Tick(engine);
        Assert.Null(_fans.Duties[0]);
    }

    [Fact]
    public void Curve_rises_at_once_but_falls_only_after_the_hysteresis()
    {
        var curve = new FanCurve { Points = [new(50, 30), new(80, 90)] };
        var engine = Engine(new FanConfig { Mode = FanMode.Curve, Curve = curve, CurveUsesGpu = false });

        _fans.CpuTemp = 80;
        Tick(engine, 20);   // let the smoothing settle
        Assert.Equal(90, _fans.Duties[0]);

        _fans.CpuTemp = 50;
        Tick(engine, 3);
        Assert.Equal(90, _fans.Duties[0]);
        Tick(engine, 30);
        Assert.True(_fans.Duties[0] < 40, $"expected the curve to come down, got {_fans.Duties[0]}");
    }

    [Fact]
    public void Curve_follows_the_hotter_gpu_when_asked()
    {
        var curve = new FanCurve { Points = [new(50, 30), new(80, 90)] };
        var engine = Engine(new FanConfig { Mode = FanMode.Curve, Curve = curve, CurveUsesGpu = true, GpuOverrideTemp = 95 });
        _fans.CpuTemp = 50;
        _fans.GpuTemp = 80;
        Tick(engine, 20);
        Assert.Equal(90, _fans.Duties[0]);
    }

    [Fact]
    public void Fixed_duty_is_rewritten_every_30_seconds()
    {
        var engine = Engine(new FanConfig { Mode = FanMode.Manual, ManualPercent = 60 });
        Tick(engine);
        int writes = _fans.Writes;
        Tick(engine, 29);
        Assert.Equal(writes, _fans.Writes);
        Tick(engine, 2);
        Assert.True(_fans.Writes > writes);
    }

    [Fact]
    public void Pause_hands_fans_back_and_resume_reapplies()
    {
        var engine = Engine(new FanConfig { Mode = FanMode.Manual, ManualPercent = 60 });
        Tick(engine);
        engine.Pause();
        Assert.Equal([null, null], _fans.Duties);

        Tick(engine, 5);
        Assert.Equal([null, null], _fans.Duties);

        engine.Resume();
        Tick(engine);
        Assert.Equal([60, 60], _fans.Duties);
    }

    [Fact]
    public void Asus_auto_releases_every_fan()
    {
        var engine = Engine(new FanConfig { Mode = FanMode.Manual, ManualPercent = 60 });
        Tick(engine);
        engine.Configure(new FanConfig { Mode = FanMode.AsusAuto }.Normalized(30));
        Tick(engine);
        Assert.Equal([null, null], _fans.Duties);
    }
}
