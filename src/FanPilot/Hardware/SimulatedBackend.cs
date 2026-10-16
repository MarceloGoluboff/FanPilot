namespace FanPilot.Hardware;

/// <summary>
/// Pretend fans for working on FanPilot without ASUS hardware (<c>--host --simulate</c>) and for
/// tests. Fan RPM follows its duty with some lag, fans stall below their stall duty, and the CPU
/// temperature drifts with a slow load cycle and cools a little faster when the fans spin up.
/// With three fans the third one ignores commands, like some two-fan TUF models do.
/// </summary>
public sealed class SimulatedBackend : IFanBackend
{
    sealed class Fan
    {
        public required int MaxRpm;
        public required int StallPercent;
        public required bool Controllable;
        public int? Duty;           // null: firmware control
        public double Rpm;
    }

    readonly object _gate = new();
    readonly TimeProvider _time;
    readonly Fan[] _fans;
    long _last;
    double _temp = 50;

    public SimulatedBackend(int fanCount, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _last = _time.GetTimestamp();
        _fans = Enumerable.Range(0, Math.Clamp(fanCount, 1, 3)).Select(i => new Fan
        {
            MaxRpm = 5600 + 300 * i,
            StallPercent = 8 + 4 * i,
            Controllable = i < 2,
        }).ToArray();
        foreach (var fan in _fans) fan.Rpm = AutoRpm(fan);
    }

    public string Description => $"Simulated, {_fans.Length} fan(s)";
    public int FanCount => _fans.Length;

    public int GetRpm(int fan)
    {
        lock (_gate)
        {
            Advance();
            return (int)Math.Round(_fans[fan].Rpm);
        }
    }

    public void SetDuty(int fan, int percent)
    {
        lock (_gate)
        {
            Advance();
            if (_fans[fan].Controllable) _fans[fan].Duty = Math.Clamp(percent, 1, 100);
        }
    }

    public void Release(int fan)
    {
        lock (_gate)
        {
            Advance();
            _fans[fan].Duty = null;
        }
    }

    public int? ReadCpuTemp()
    {
        lock (_gate)
        {
            Advance();
            return (int)Math.Round(_temp);
        }
    }

    /// <summary>A GPU that tracks the CPU's load a few degrees lower.</summary>
    public int? ReadGpuTemp()
    {
        lock (_gate)
        {
            Advance();
            return (int)Math.Round(_temp - 6);
        }
    }

    void Advance()
    {
        long now = _time.GetTimestamp();
        double dt = _time.GetElapsedTime(_last, now).TotalSeconds;
        _last = now;
        if (dt <= 0) return;

        foreach (var fan in _fans)
        {
            double target = fan.Duty is int d ? DutyRpm(fan, d) : AutoRpm(fan);
            fan.Rpm += (target - fan.Rpm) * (1 - Math.Exp(-dt / 1.5));
        }

        // A gentle five-minute load cycle, minus cooling from the fans. Calm enough for fan setup
        // to finish; the curve and chart still have something to follow.
        double seconds = _time.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0;
        double load = 20 + 8 * Math.Sin(seconds * 2 * Math.PI / 300);
        double cooling = 12 * _fans.Average(f => f.Rpm / f.MaxRpm);
        double equilibrium = 50 + load - cooling;
        _temp += (equilibrium - _temp) * (1 - Math.Exp(-dt / 8));
    }

    static double DutyRpm(Fan fan, int duty) =>
        duty < fan.StallPercent ? 0 : fan.MaxRpm * (0.1 + 0.9 * duty / 100.0);

    double AutoRpm(Fan fan) =>
        fan.MaxRpm * Math.Clamp(0.42 + (_temp - 60) / 50, 0.42, 1);

    public void Dispose() { }
}
