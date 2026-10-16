using FanPilot.Control;
using FanPilot.Ipc;

namespace FanPilot.Tests;

public class CalibrationTests
{
    /// <summary>Measured on an ASUS Vivobook 15X (Q530VJ), fan 1.</summary>
    static readonly List<DutyRpm> VivobookFan =
    [
        new(100, 5500), new(80, 5000), new(60, 4382), new(45, 3700), new(35, 3058),
        new(25, 2414), new(20, 1963), new(15, 1536), new(10, 1078), new(5, 0),
    ];

    [Fact]
    public void Measured_fan_gets_a_floor_just_above_its_stall_point()
    {
        var fan = CalibrationAnalysis.Analyze(0, asusIdleRpm: 2500, VivobookFan, startPercent: 10, recheckRpm: 4488);
        Assert.True(fan.Controllable);
        Assert.Equal(5, fan.StallPercent);
        Assert.Equal(15, fan.MinPercent);
        Assert.Equal(25, fan.SilentPercent);
    }

    [Fact]
    public void A_high_start_up_duty_raises_the_floor()
    {
        var fan = CalibrationAnalysis.Analyze(0, 2500, VivobookFan, startPercent: 25, recheckRpm: 4488);
        Assert.Equal(25, fan.MinPercent);
    }

    [Fact]
    public void A_fan_that_ignores_commands_is_not_controllable()
    {
        var flat = CalibrationAnalysis.Steps.Select(d => new DutyRpm(d, 3200)).ToList();
        var fan = CalibrationAnalysis.Analyze(1, 3200, flat, 0, recheckRpm: 3200);
        Assert.False(fan.Controllable);
        Assert.Equal(CalibrationProfile.UncalibratedFloor, fan.MinPercent);
    }

    /// <summary>
    /// A real run where the firmware kept control: RPM only fell because the CPU was cooling.
    /// A downward sweep alone called this controllable and saved a 10% floor.
    /// </summary>
    [Fact]
    public void Firmware_drift_while_cooling_is_not_mistaken_for_control()
    {
        List<DutyRpm> drift =
        [
            new(100, 3168), new(80, 2896), new(60, 2815), new(45, 2510), new(35, 2519),
            new(25, 2496), new(20, 2507), new(15, 2515), new(10, 2509), new(5, 2510),
        ];
        var fan = CalibrationAnalysis.Analyze(0, asusIdleRpm: 3377, drift, startPercent: 0, recheckRpm: 2505);
        Assert.False(fan.Controllable);
        Assert.Equal(CalibrationProfile.UncalibratedFloor, fan.MinPercent);
    }

    [Fact]
    public void Silent_never_goes_above_balanced()
    {
        var fan = CalibrationAnalysis.Analyze(0, asusIdleRpm: 6000, VivobookFan, startPercent: 10, recheckRpm: 4488);
        Assert.True(fan.SilentPercent <= CalibrationAnalysis.SilentMax);
    }

    [Fact]
    public void A_fan_that_stalls_early_gets_a_rounded_up_floor()
    {
        var table = CalibrationAnalysis.Steps.Select(d => new DutyRpm(d, d >= 25 ? d * 50 : 0)).ToList();
        var fan = CalibrationAnalysis.Analyze(0, 2000, table, startPercent: 30, recheckRpm: 3000);
        Assert.Equal(20, fan.StallPercent);
        Assert.Equal(30, fan.MinPercent);
    }

    [Fact]
    public void Firmware_that_stops_the_fan_at_idle_gets_the_floor_as_silent()
    {
        var fan = CalibrationAnalysis.Analyze(0, asusIdleRpm: 0, VivobookFan, startPercent: 10, recheckRpm: 4488);
        Assert.Equal(fan.MinPercent, fan.SilentPercent);
    }

    [Fact]
    public void Profile_floor_covers_every_controllable_fan()
    {
        var profile = new CalibrationProfile(DateTime.UtcNow, "m", "b", "d", true,
        [
            CalibrationAnalysis.Analyze(0, 2500, VivobookFan, 10, 4488),
            CalibrationAnalysis.Analyze(1, 2500, VivobookFan, 25, 4488),
        ]);
        Assert.Equal(25, profile.Floor);
    }

    [Fact]
    public void Profile_goes_stale_only_when_a_known_stamp_changes()
    {
        var machine = new MachineInfo("ASUS", "Q530VJ", "306", "3.1.72.0", "Windows");
        CalibrationProfile Stamped(string model, string bios, string driver) => new(DateTime.UtcNow, model, bios, driver, true, []);

        Assert.True(Stamped("Q530VJ", "306", "3.1.72.0").MatchesMachine(machine));
        Assert.False(Stamped("Q530VJ", "306", "3.1.48.0").MatchesMachine(machine));
        Assert.True(Stamped("unknown", "unknown", "unknown").MatchesMachine(machine));
    }

    [Fact]
    public void Status_with_a_profile_survives_the_pipe()
    {
        var profile = new CalibrationProfile(new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc), "Q530VJ", "306", "3.1.72.0", true,
            [CalibrationAnalysis.Analyze(0, 2500, VivobookFan, 10, 4488)]);
        var message = new PipeMessage
        {
            Type = PipeMessage.Status,
            ServiceStatus = new ServiceStatus(2, null, profile.Floor, profile, false, false, ["G-Helper"]),
            FanConfig = new FanConfig { Mode = FanMode.Curve }.Normalized(profile.Floor),
        };

        var back = Protocol.Deserialize(Protocol.Serialize(message))!;
        Assert.Equal(PipeMessage.Status, back.Type);
        Assert.Equal(15, back.ServiceStatus!.Floor);
        Assert.Equal(15, back.ServiceStatus.Calibration!.Fans[0].MinPercent);
        Assert.Equal(["G-Helper"], back.ServiceStatus.ConflictingApps);
        Assert.Equal(FanMode.Curve, back.FanConfig!.Mode);
    }
}
