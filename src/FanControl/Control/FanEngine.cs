using FanControl.Hardware;

namespace FanControl.Control;

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
    public const int MinPercent = 20;
    public const int SafeFloorPercent = 40;

    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan ReapplyInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan CurveDropDelay = TimeSpan.FromSeconds(5);
    const double CurveDropHysteresis = 3;   // C below the rising point before the curve lowers speed
    const double OverrideRelease = 10;      // C below the override threshold before it lets go
    const double TempSmoothing = 0.35;      // EMA weight for curve input, so short spikes don't surge the fans
    const int MinChange = 2;                // % change worth writing

    readonly FanController _hw;
    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    Thread? _thread;
    volatile bool _running;

    FanConfig _config;
    int? _applied;
    DateTime _lastApply;
    bool _forceApply = true;
    bool _override;
    double? _smoothedTemp;
    double _curveOutput;
    DateTime? _curveDropSince;

    public event Action<FanSnapshot>? Updated;

    public int FanCount => _hw.FanCount;

    public FanEngine(FanController hw, FanConfig config)
    {
        _hw = hw;
        _config = config;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "FanEngine" };
        _thread.Start();
    }

    public void Configure(FanConfig config)
    {
        lock (_gate)
        {
            if (config.Curve != _config.Curve || config.Mode != _config.Mode)
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

    /// <summary>Stops polling and, if asked, gives control back to the firmware.</summary>
    public void Stop(bool restoreAsus)
    {
        _running = false;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(3));
        if (restoreAsus) EmergencyRestore();
    }

    /// <summary>Best-effort hand-back to ASUS control; safe to call from crash handlers.</summary>
    public void EmergencyRestore()
    {
        try { _hw.RestoreAsusControl(); }
        catch (Exception ex) { Log.Write($"Restore failed: {ex.Message}"); }
    }

    void Loop()
    {
        while (_running)
        {
            try { Tick(DateTime.Now); }
            catch (Exception ex) { Log.Write($"Tick failed: {ex}"); }
            _wake.WaitOne(PollInterval);
        }
    }

    void Tick(DateTime now)
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
            int? target = ComputeTarget(temp, now);
            try { Apply(target, now); }
            catch (Exception ex) { error = ex.Message; }
            snapshot = new FanSnapshot(now, temp, rpms, _applied, _config.Mode, _override, error);
        }
        Updated?.Invoke(snapshot);
    }

    /// <summary>Null means "let ASUS control the fans".</summary>
    int? ComputeTarget(int? temp, DateTime now)
    {
        var config = _config;
        if (config.Mode == FanMode.AsusAuto)
        {
            _override = false;
            return null;
        }

        if (temp is int t)
        {
            if (t >= config.OverrideTemp) _override = true;
            else if (_override && t <= config.OverrideTemp - OverrideRelease) _override = false;
            _smoothedTemp = _smoothedTemp is double s ? s + TempSmoothing * (t - s) : t;
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

        int floor = config.ForbidUnsafe ? SafeFloorPercent : MinPercent;
        return Math.Clamp(target, floor, 100);
    }

    /// <summary>Rises immediately; falls only once the curve 3 C hotter is still lower, for 5 s.</summary>
    int CurveStep(FanCurve curve, double temp, DateTime now)
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

    void Apply(int? target, DateTime now)
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
        if (_running) Stop(restoreAsus: false);
        _wake.Dispose();
    }
}
