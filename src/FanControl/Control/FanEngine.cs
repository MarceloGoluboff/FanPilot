using System.Diagnostics;
using FanControl.Hardware;

namespace FanControl.Control;

/// <summary>One poll. <see cref="Time"/> is UTC so a DST change can't reorder samples.</summary>
public sealed record FanSnapshot(
    DateTime Time,
    int? CpuTemp,
    int[] Rpms,
    int? AppliedPercent,
    FanMode Mode,
    bool OverrideActive,
    string? Error);

/// <summary>
/// Polls temperature and RPM once a second and drives the fans for the current mode. Owns all
/// writes to the hardware so the rules (safety floor, thermal override, reapply) live in one place.
/// </summary>
public sealed class FanEngine : IDisposable
{
    /// <summary>
    /// Lowest duty the app will set. Measured on the Q530VJ: both fans still spin at 10%
    /// (about 1,100 RPM) and stall at 5%, so 15% leaves margin.
    /// </summary>
    public const int MinPercent = 15;

    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan ReapplyInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan CurveDropDelay = TimeSpan.FromSeconds(5);
    const double CurveDropHysteresis = 3;   // C below the rising point before the curve lowers speed
    const double OverrideRelease = 10;      // C below the override threshold before it lets go
    const double TempSmoothing = 0.35;      // EMA weight for curve input, so short spikes don't surge the fans
    const int MinChange = 2;                // % change worth writing
    const int MaxMissedTemps = 5;           // polls without a temperature before Manual gives up

    readonly FanController _hw;
    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    Thread? _thread;
    bool _running;   // guarded by _gate

    FanConfig _config;
    int? _applied;
    TimeSpan _lastApply;
    bool _forceApply = true;
    bool _override;
    int _missedTemps;
    double? _smoothedTemp;
    double _curveOutput;
    TimeSpan? _curveDropSince;

    public event Action<FanSnapshot>? Updated;

    public FanEngine(FanController hw, FanConfig config)
    {
        _hw = hw;
        _config = config;
    }

    /// <summary>Monotonic time, so clock changes (DST, NTP) can't stall the reapply or hysteresis timers.</summary>
    static TimeSpan Now => Stopwatch.GetElapsedTime(0);

    public void Start()
    {
        lock (_gate) _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "FanEngine" };
        _thread.Start();
    }

    public void Configure(FanConfig config)
    {
        lock (_gate)
        {
            if (config.Mode != _config.Mode)
            {
                // The smoothed temp isn't updated in ASUS Auto, so it may be stale.
                _smoothedTemp = null;
            }
            if (config.Mode != _config.Mode || !config.SameCurve(_config))
            {
                _curveOutput = 0;
                _curveDropSince = null;
            }
            _config = config;
            _forceApply = true;
        }
        _wake.Set();
    }

    /// <summary>Writes the current target again on the next tick, e.g. after resume from sleep.</summary>
    public void RequestReapply()
    {
        lock (_gate) _forceApply = true;
        _wake.Set();
    }

    /// <summary>
    /// Stops polling and, if asked, gives control back to the firmware. Taking the gate after
    /// clearing _running means no write can land after the restore, even if the thread is stuck.
    /// </summary>
    public void Stop(bool restoreAsus)
    {
        lock (_gate) _running = false;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(3));
        lock (_gate)
        {
            if (restoreAsus) EmergencyRestore();
        }
    }

    /// <summary>Best-effort hand-back to ASUS control; safe to call from crash handlers.</summary>
    public void EmergencyRestore()
    {
        try { _hw.RestoreAsusControl(); }
        catch (Exception ex) { Log.Write($"Restore failed: {ex.Message}"); }
    }

    void Loop()
    {
        try
        {
            while (true)
            {
                lock (_gate)
                {
                    if (!_running) return;
                }
                try { Tick(); }
                catch (Exception ex) { Log.Write($"Tick failed: {ex}"); }
                _wake.WaitOne(PollInterval);
            }
        }
        catch (ObjectDisposedException) { }
    }

    void Tick()
    {
        int? temp = null;
        int[] rpms = [];
        string? error = null;
        try
        {
            temp = _hw.GetCpuTemp();
            rpms = _hw.GetRpms();
        }
        catch (ObjectDisposedException) { return; }
        catch (Exception ex) { error = ex.Message; }

        FanSnapshot snapshot;
        lock (_gate)
        {
            if (!_running) return;
            var now = Now;
            int? target = ComputeTarget(temp, now);
            try { Apply(target, now); }
            catch (Exception ex) { error = ex.Message; }
            snapshot = new FanSnapshot(DateTime.UtcNow, temp, rpms, _applied, _config.Mode, _override, error);
        }
        Updated?.Invoke(snapshot);
    }

    /// <summary>Null means "let ASUS control the fans".</summary>
    int? ComputeTarget(int? temp, TimeSpan now)
    {
        var config = _config;
        if (config.Mode == FanMode.AsusAuto)
        {
            _override = false;
            _missedTemps = 0;
            return null;
        }

        if (temp is int t)
        {
            _missedTemps = 0;
            if (t >= config.OverrideTemp) _override = true;
            else if (_override && t <= config.OverrideTemp - OverrideRelease) _override = false;
            _smoothedTemp = _smoothedTemp is double s ? s + TempSmoothing * (t - s) : t;
        }
        else if (++_missedTemps >= MaxMissedTemps)
        {
            // Blind: the thermal override can't fire, so a fixed low speed isn't safe. Let the firmware drive.
            if (_missedTemps == MaxMissedTemps) Log.Write("No CPU temperature for 5 s, handing fans to ASUS");
            return null;
        }
        if (_override) return 100;

        int target;
        if (config.Mode == FanMode.Manual)
        {
            target = config.ManualPercent;
        }
        else
        {
            // Without a temperature the curve is blind; the firmware is the safe choice.
            if (temp is null || _smoothedTemp is not double smoothed) return null;
            target = CurveStep(config.Curve, smoothed, now);
        }

        return Math.Clamp(target, MinPercent, 100);
    }

    /// <summary>Rises immediately; falls only once the curve 3 C hotter is still lower, for 5 s.</summary>
    int CurveStep(FanCurve curve, double temp, TimeSpan now)
    {
        double raw = curve.Evaluate(temp);
        if (raw >= _curveOutput)
        {
            _curveOutput = raw;
            _curveDropSince = null;
        }
        else
        {
            double relaxed = curve.Evaluate(temp + CurveDropHysteresis);
            if (relaxed < _curveOutput)
            {
                _curveDropSince ??= now;
                if (now - _curveDropSince >= CurveDropDelay)
                {
                    _curveOutput = relaxed;
                    _curveDropSince = null;
                }
            }
            else
            {
                _curveDropSince = null;
            }
        }
        return (int)Math.Round(_curveOutput);
    }

    void Apply(int? target, TimeSpan now)
    {
        bool changed = target != _applied && (
            target is null || _applied is null ||
            Math.Abs(target.Value - _applied.Value) >= MinChange ||
            target is 100);
        // The EC can silently drop test mode, so a fixed duty is rewritten periodically.
        bool stale = target is not null && now - _lastApply >= ReapplyInterval;

        if (!_forceApply && !changed && !stale) return;

        if (target is int percent) _hw.SetDutyAll(percent);
        else _hw.RestoreAsusControl();

        if (_applied != target) Log.Write($"Fans -> {(target is int p ? $"{p}%" : "ASUS auto")}");
        _applied = target;
        _lastApply = now;
        _forceApply = false;
    }

    public void Dispose()
    {
        bool running;
        lock (_gate) running = _running;
        if (running) Stop(restoreAsus: false);
        // A thread stuck past the join timeout may still wait on _wake; Loop tolerates its disposal.
        _wake.Dispose();
    }
}
