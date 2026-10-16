using FanPilot.Hardware;

namespace FanPilot.Control;

/// <summary>One poll. <see cref="Time"/> is UTC so a DST change can't reorder samples.</summary>
public sealed record FanSnapshot(
    DateTime Time,
    int? CpuTemp,
    string? CpuTempSource,
    int? GpuTemp,
    int[] Rpms,
    int? AppliedPercent,
    FanMode Mode,
    bool OverrideActive,
    bool Paused,
    string? Error);

/// <summary>
/// Polls temperatures and RPM once a second and drives the fans for the current mode. Owns all
/// writes to the fans so the rules (safety floor, thermal override, reapply) live in one place.
/// </summary>
public sealed class FanEngine : IDisposable
{
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    static readonly TimeSpan ReapplyInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan CurveDropDelay = TimeSpan.FromSeconds(5);
    static readonly TimeSpan SlowRead = TimeSpan.FromMilliseconds(1500);
    const double CurveDropHysteresis = 3;   // C below the rising point before the curve lowers speed
    const double OverrideRelease = 10;      // C below an override threshold before it lets go
    const double TempSmoothing = 0.35;      // EMA weight for curve input, so short spikes don't surge the fans
    const int MinChange = 2;                // % change worth writing
    const int MaxMissedTemps = 5;           // polls without a CPU temperature before Manual gives up

    readonly IFanBackend _fans;
    readonly TemperatureReader _temps;
    readonly TimeProvider _time;
    readonly object _gate = new();
    readonly AutoResetEvent _wake = new(false);
    Thread? _thread;
    bool _running;   // guarded by _gate

    FanConfig _config;
    CalibrationProfile? _profile;
    bool _paused;
    int? _applied;
    TimeSpan _lastApply;
    bool _forceApply = true;
    bool _cpuOverride, _gpuOverride;
    int _missedTemps;
    double? _smoothedTemp;
    double _curveOutput;
    TimeSpan? _curveDropSince;

    public event Action<FanSnapshot>? Updated;

    public FanEngine(IFanBackend fans, TemperatureReader temps, FanConfig config, CalibrationProfile? profile, TimeProvider? time = null)
    {
        _fans = fans;
        _temps = temps;
        _config = config;
        _profile = profile;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Lowest duty the engine will set: from calibration, or a conservative default.</summary>
    public int Floor
    {
        get { lock (_gate) return _profile?.Floor ?? CalibrationProfile.UncalibratedFloor; }
    }

    TimeSpan Now => _time.GetElapsedTime(0);

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

    public void SetProfile(CalibrationProfile? profile)
    {
        lock (_gate)
        {
            _profile = profile;
            _forceApply = true;
        }
        _wake.Set();
    }

    /// <summary>
    /// Hands every fan to ASUS and stops writing until <see cref="Resume"/>. Used before sleep (test
    /// mode can survive it and leave fans stopped) and while calibration drives the fans itself.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
            ReleaseAll();
            _applied = null;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
            _forceApply = true;
        }
        _wake.Set();
    }

    /// <summary>Writes the current target again on the next tick.</summary>
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
    public void EmergencyRestore() => ReleaseAll();

    void ReleaseAll()
    {
        try { _fans.ReleaseAll(); }
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

    /// <summary>One poll and apply. Public to the test project, which drives it with a fake clock.</summary>
    internal FanSnapshot? Tick()
    {
        Temperatures temps = new(null, null, null);
        int[] rpms = [];
        string? error = null;
        bool paused;
        lock (_gate) paused = _paused;

        // While paused (sleep, or fan setup driving the fans) the engine leaves the hardware alone.
        // With a second thread polling during fan setup, ASUS's library stalled for ~20 s at a time.
        if (!paused)
        {
            long readStart = _time.GetTimestamp();
            try
            {
                temps = _temps.Read();
                rpms = Enumerable.Range(0, _fans.FanCount).Select(_fans.GetRpm).ToArray();
            }
            catch (ObjectDisposedException) { return null; }
            catch (Exception ex) { error = ex.Message; }
            var readTime = _time.GetElapsedTime(readStart);
            if (readTime > SlowRead) Log.Write($"Slow sensor read: {readTime.TotalMilliseconds:0} ms");
        }

        FanSnapshot snapshot;
        lock (_gate)
        {
            if (_thread is not null && !_running) return null;
            var now = Now;
            if (!_paused)
            {
                int? target = ComputeTarget(temps, now);
                try { Apply(target, now); }
                catch (Exception ex) { error = ex.Message; }
            }
            snapshot = new FanSnapshot(_time.GetUtcNow().UtcDateTime, temps.Cpu, temps.CpuSource, temps.Gpu, rpms,
                _applied, _config.Mode, _cpuOverride || _gpuOverride, _paused, error);
        }
        Updated?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>Null means "let ASUS control the fans".</summary>
    int? ComputeTarget(Temperatures temps, TimeSpan now)
    {
        var config = _config;
        if (config.Mode == FanMode.AsusAuto)
        {
            _cpuOverride = _gpuOverride = false;
            _missedTemps = 0;
            return null;
        }

        if (temps.Gpu is int g)
        {
            if (g >= config.GpuOverrideTemp) _gpuOverride = true;
            else if (_gpuOverride && g <= config.GpuOverrideTemp - OverrideRelease) _gpuOverride = false;
        }
        else
        {
            // A GPU that stops reporting has gone idle; it can't still be overheating.
            _gpuOverride = false;
        }

        if (temps.Cpu is int c)
        {
            _missedTemps = 0;
            if (c >= config.OverrideTemp) _cpuOverride = true;
            else if (_cpuOverride && c <= config.OverrideTemp - OverrideRelease) _cpuOverride = false;

            int input = config.CurveUsesGpu && temps.Gpu is int gpu ? Math.Max(c, gpu) : c;
            _smoothedTemp = _smoothedTemp is double s ? s + TempSmoothing * (input - s) : input;
        }
        else if (++_missedTemps >= MaxMissedTemps)
        {
            // Blind: the CPU override can't fire, so a fixed speed isn't safe. Let the firmware drive.
            if (_missedTemps == MaxMissedTemps) Log.Write("No CPU temperature for 5 s, handing fans to ASUS");
            return null;
        }
        if (_cpuOverride || _gpuOverride) return 100;

        int target;
        if (config.Mode == FanMode.Manual)
        {
            target = config.ManualPercent;
        }
        else
        {
            // Without a temperature the curve is blind; the firmware is the safe choice.
            if (temps.Cpu is null || _smoothedTemp is not double smoothed) return null;
            target = CurveStep(config.Curve, smoothed, now);
        }

        return Math.Clamp(target, _profile?.Floor ?? CalibrationProfile.UncalibratedFloor, 100);
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

        for (int fan = 0; fan < _fans.FanCount; fan++)
        {
            // Some two-fan models drop the second write without a short gap (AsusFanControl #20).
            if (fan > 0) Thread.Sleep(20);
            // Fans that calibration found don't follow commands stay with the firmware.
            if (target is int percent && _profile?.IsControllable(fan) != false) _fans.SetDuty(fan, percent);
            else _fans.Release(fan);
        }

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
