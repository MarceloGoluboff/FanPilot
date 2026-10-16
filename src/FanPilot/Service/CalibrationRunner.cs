using FanPilot.Control;
using FanPilot.Hardware;

namespace FanPilot.Service;

public sealed class CalibrationAbortedException(string message) : Exception(message);

/// <summary>
/// Measures the fans the way the engine drives them: all at the same duty, at once. Records the
/// firmware's idle speed, each fan's RPM at every step of <see cref="CalibrationAnalysis.Steps"/>,
/// and the lowest duty that restarts each fan once stalled. Aborts if the CPU gets hot, and always
/// hands every fan back to ASUS when it ends, however it ends. About 70 seconds.
/// </summary>
/// <remarks>
/// Measuring one fan at a time with the others on ASUS control gave wrong results on a Vivobook
/// Q530VJ: the second fan ignored most duties on its own, but followed them when both fans were set.
/// </remarks>
internal sealed class CalibrationRunner(IFanBackend backend, TemperatureReader temps, TimeProvider time)
{
    // ASUS firmware can override test-mode speeds when the CPU is hot (seen above about 70 C on a
    // Vivobook Q530VJ), which makes every fan look like it ignores commands. So start cool and stop
    // before readings stop meaning anything.
    const int MaxStartTemp = 70;
    const int AbortTemp = 80;
    // Slowing the fans to a crawl warms the CPU a little (6 C in testing); more means real load.
    const int MaxRise = 15;
    static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);
    static readonly TimeSpan IdleSample = TimeSpan.FromSeconds(5);
    static readonly TimeSpan StepSettle = TimeSpan.FromSeconds(3);
    static readonly TimeSpan StepSample = TimeSpan.FromSeconds(3);
    static readonly TimeSpan StartHold = TimeSpan.FromSeconds(4);
    // Gentle on ASUS's library, which stalls when called too often.
    static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(1);

    int? _startTemp;

    public async Task<CalibrationProfile> RunAsync(MachineInfo machine, Action<CalibrationProgress> report, CancellationToken ct)
    {
        int fanCount = backend.FanCount;
        _startTemp = temps.Read().Cpu;
        if (_startTemp >= MaxStartTemp)
            throw new CalibrationAbortedException(
                $"The CPU is at {_startTemp} C. Fan setup needs it below {MaxStartTemp} C, because ASUS firmware takes over the fans " +
                "when it's hot. Close heavy apps, wait a minute, and try again.");

        int totalSteps = 1 + CalibrationAnalysis.Steps.Length + CalibrationAnalysis.StartSteps.Length;
        int done = 0;
        long started = time.GetTimestamp();

        // Temperatures are read once per step (in CheckTemperature) and passed along.
        void Report(string stage, int? duty, int[]? rpms, int? cpu)
        {
            if (duty is int d && rpms is not null)
                Log.Write($"  {time.GetElapsedTime(started).TotalSeconds,5:0.0}s {stage} {d}%: {string.Join(" / ", rpms)} RPM, CPU {cpu} C");
            report(new CalibrationProgress(fanCount, Math.Min(99, done * 100 / totalSteps), stage, duty, rpms, cpu));
        }

        try
        {
            backend.ReleaseAll();
            Report("Measuring the ASUS idle speed", null, null, _startTemp);
            await Delay(Settle, ct);
            int[] idle = await AverageRpms(IdleSample, ct);
            done++;

            var tables = Enumerable.Range(0, fanCount).Select(_ => new List<DutyRpm>()).ToArray();
            foreach (int duty in CalibrationAnalysis.Steps)
            {
                int? cpu = CheckTemperature();
                SetAll(duty);
                await Delay(StepSettle, ct);
                int[] rpms = await AverageRpms(StepSample, ct);
                for (int fan = 0; fan < fanCount; fan++) tables[fan].Add(new DutyRpm(duty, rpms[fan]));
                done++;
                Report("Stepping down", duty, rpms, cpu);
            }

            // Restart the fans that stalled at the lowest step, from a standstill.
            var start = new int[fanCount];
            var stalled = Enumerable.Range(0, fanCount).Where(f => tables[f][^1].Rpm < CalibrationAnalysis.StallRpm).ToHashSet();
            foreach (int fan in stalled) start[fan] = CalibrationAnalysis.StartSteps[^1] + 5;
            foreach (int duty in CalibrationAnalysis.StartSteps)
            {
                if (stalled.Count == 0) break;
                int? cpu = CheckTemperature();
                SetAll(duty);
                await Delay(StartHold, ct);
                var rpms = Enumerable.Range(0, fanCount).Select(backend.GetRpm).ToArray();
                foreach (int fan in stalled.Where(f => rpms[f] >= CalibrationAnalysis.StallRpm).ToList())
                {
                    start[fan] = duty;
                    stalled.Remove(fan);
                }
                done++;
                Report("Finding the start-up speed", duty, rpms, cpu);
            }

            // Back up to a mid speed: a fan that's really following commands speeds up again.
            int? recheckCpu = CheckTemperature();
            SetAll(CalibrationAnalysis.RecheckPercent);
            await Delay(StepSettle, ct);
            int[] recheck = await AverageRpms(StepSample, ct);
            done = totalSteps;
            Report("Checking the fans speed back up", CalibrationAnalysis.RecheckPercent, recheck, recheckCpu);

            var fans = new List<FanProfile>();
            for (int fan = 0; fan < fanCount; fan++)
            {
                var profile = CalibrationAnalysis.Analyze(fan, idle[fan], tables[fan], start[fan], recheck[fan]);
                Log.Write($"Calibrated fan {fan + 1}: controllable {profile.Controllable}, stall {profile.StallPercent}%, " +
                          $"start {profile.StartPercent}%, min {profile.MinPercent}%, idle {idle[fan]} RPM, silent {profile.SilentPercent}%");
                fans.Add(profile);
            }

            // Saving this would hand every fan to ASUS for good. More likely the firmware overrode us.
            if (!fans.Any(f => f.Controllable))
                throw new CalibrationAbortedException(
                    "None of the fans followed speed commands, so nothing was saved. If the laptop was warm or busy, " +
                    "let it cool and run fan setup again. If it keeps happening, please report your model.");

            return new CalibrationProfile(time.GetUtcNow().UtcDateTime, machine.Model, machine.Bios, machine.AsusDriver,
                _startTemp is not null, fans);
        }
        finally
        {
            backend.ReleaseAll();
        }
    }

    void SetAll(int duty)
    {
        for (int fan = 0; fan < backend.FanCount; fan++)
        {
            // Some two-fan models drop the second write without a short gap (AsusFanControl #20).
            if (fan > 0) Thread.Sleep(20);
            backend.SetDuty(fan, duty);
        }
    }

    /// <summary>Reads the CPU temperature, throwing if it's too hot to go on. Returns it for progress.</summary>
    int? CheckTemperature()
    {
        int? t = temps.Read().Cpu;
        if (t is null) return null;
        if (t >= AbortTemp) throw new CalibrationAbortedException($"Stopped: the CPU reached {t} C. Let it cool down and try again.");
        if (_startTemp is int start && t - start >= MaxRise)
            throw new CalibrationAbortedException($"Stopped: the CPU warmed up {t - start} C. Close heavy apps and try again.");
        return t;
    }

    async Task<int[]> AverageRpms(TimeSpan duration, CancellationToken ct)
    {
        int fanCount = backend.FanCount;
        var sums = new long[fanCount];
        int samples = 0;
        var end = time.GetUtcNow() + duration;
        do
        {
            for (int fan = 0; fan < fanCount; fan++) sums[fan] += backend.GetRpm(fan);
            samples++;
            await Delay(SampleEvery, ct);
        }
        while (time.GetUtcNow() < end);
        return sums.Select(s => (int)Math.Round((double)s / samples)).ToArray();
    }

    Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, time, ct);
}
