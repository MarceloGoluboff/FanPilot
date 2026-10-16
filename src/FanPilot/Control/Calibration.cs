namespace FanPilot.Control;

public sealed record DutyRpm(int Percent, int Rpm);

/// <summary>What calibration learned about one fan.</summary>
public sealed record FanProfile(
    int Index,
    bool Controllable,
    int StallPercent,
    int StartPercent,
    int MinPercent,
    int AsusIdleRpm,
    int SilentPercent,
    List<DutyRpm> Table);

/// <summary>
/// Calibration result for this machine, saved by the service. Tied to the model, BIOS and ASUS
/// driver it was measured on, so a change prompts the user to run it again.
/// </summary>
public sealed record CalibrationProfile(
    DateTime CalibratedUtc,
    string Machine,
    string Bios,
    string Driver,
    bool CpuTempReadable,
    List<FanProfile> Fans)
{
    /// <summary>Lowest duty before calibration. Safe on every model reported so far.</summary>
    public const int UncalibratedFloor = 30;

    /// <summary>All controllable fans get the same duty, so the floor is the highest per-fan minimum.</summary>
    public int Floor =>
        Fans.Where(f => f.Controllable).Select(f => f.MinPercent).DefaultIfEmpty(UncalibratedFloor).Max();

    /// <summary>The duty that sounds like ASUS's own idle, for the Silent preset.</summary>
    public int? SilentPercent =>
        Fans.Where(f => f.Controllable).Select(f => (int?)f.SilentPercent).Max();

    public bool IsControllable(int fan) => Fans.FirstOrDefault(f => f.Index == fan)?.Controllable ?? true;

    /// <summary>False only when a known stamp differs; a profile stamped "unknown" can't be judged.</summary>
    public bool MatchesMachine(MachineInfo machine) =>
        Machine == MachineInfo.Unknown.Model ||
        (Machine == machine.Model && Bios == machine.Bios && Driver == machine.AsusDriver);

    public static CalibrationProfile? Load(string folder) =>
        JsonFile.Load<CalibrationProfile>(Path.Combine(folder, "calibration.json"));

    public void Save(string folder) => JsonFile.Save(Path.Combine(folder, "calibration.json"), this);
}

/// <summary>Turns one fan's measurements into a <see cref="FanProfile"/>. Pure, so it's tested directly.</summary>
public static class CalibrationAnalysis
{
    /// <summary>Duties stepped through, highest first.</summary>
    public static readonly int[] Steps = [100, 80, 60, 45, 35, 25, 20, 15, 10, 5];

    /// <summary>Below this the fan counts as stopped.</summary>
    public const int StallRpm = 300;

    /// <summary>Margin above the stall duty for the minimum.</summary>
    public const int StallMargin = 10;

    /// <summary>Silent never goes above Balanced.</summary>
    public const int SilentMax = 50;

    /// <summary>Duties tried, lowest first, to restart a stalled fan.</summary>
    public static readonly int[] StartSteps = [10, 15, 20, 25, 30, 35, 40];

    /// <summary>Duty set again after the sweep, to check the fan speeds back up on command.</summary>
    public const int RecheckPercent = 60;

    /// <param name="startPercent">
    /// Lowest duty that restarted the fan from a standstill, or 0 if it never stalled. A fan keeps
    /// spinning lower than it can start from, so the minimum must cover both.
    /// </param>
    /// <param name="recheckRpm">RPM after going back up to <see cref="RecheckPercent"/> at the end.</param>
    public static FanProfile Analyze(int index, int asusIdleRpm, IReadOnlyList<DutyRpm> table, int startPercent, int recheckRpm)
    {
        var sorted = table.OrderByDescending(t => t.Percent).ToList();
        int RpmAt(int percent) => sorted.FirstOrDefault(t => t.Percent == percent)?.Rpm ?? 0;

        // Following commands means a big spread between 100% and 25%, and speeding back up when
        // asked. A downward sweep alone can be fooled: on firmware control, RPM also falls as the
        // CPU cools, but it doesn't rise again on command.
        int full = RpmAt(100), low = RpmAt(25);
        bool controllable = full > StallRpm * 2 && full >= low * 1.6
                            && recheckRpm > StallRpm * 2 && recheckRpm >= low * 1.4;

        // The highest duty that stalls: anything at or below it is unsafe.
        int stall = sorted.FirstOrDefault(t => t.Rpm < StallRpm)?.Percent ?? 0;
        int min = RoundUpTo5(Math.Max(Math.Max(stall + StallMargin, startPercent), 10));
        if (!controllable) min = CalibrationProfile.UncalibratedFloor;

        // Silent: the measured duty closest to the firmware's own idle speed, but never above the
        // Balanced preset. A firmware that stops the fan at idle (0 RPM) gets the lowest safe duty.
        int silent = min;
        if (controllable && asusIdleRpm >= StallRpm)
        {
            silent = sorted.Where(t => t.Percent >= min && t.Percent <= SilentMax && t.Rpm >= StallRpm)
                .OrderBy(t => Math.Abs(t.Rpm - asusIdleRpm))
                .Select(t => t.Percent)
                .DefaultIfEmpty(min)
                .First();
        }

        return new FanProfile(index, controllable, stall, startPercent, min, asusIdleRpm, silent, sorted);
    }

    static int RoundUpTo5(int value) => (value + 4) / 5 * 5;
}

/// <summary>Model, BIOS and ASUS driver version, for calibration stamps and diagnostics.</summary>
public sealed record MachineInfo(string Manufacturer, string Model, string Bios, string AsusDriver, string Os)
{
    public static readonly MachineInfo Unknown = new("unknown", "unknown", "unknown", "unknown", Environment.OSVersion.VersionString);
}

/// <summary>Live progress, sent to the window while calibration runs.</summary>
public sealed record CalibrationProgress(int FanCount, int Percent, string Stage, int? DutyPercent, int[]? Rpms, int? CpuTemp);
