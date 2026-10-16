using System.Text;
using System.Text.Json;
using FanPilot.Control;
using FanPilot.Hardware;
using FanPilot.Ipc;

namespace FanPilot.Service;

/// <summary>
/// The service's work: opens the fans (retrying while the ASUS driver comes up at boot), runs the
/// fan engine and calibration, persists config and calibration, and serves the pipe.
/// </summary>
internal sealed class FanHost : IDisposable
{
    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);
    static readonly TimeSpan ConflictInterval = TimeSpan.FromSeconds(30);
    static readonly TimeSpan ResumeDelay = TimeSpan.FromSeconds(3);

    readonly object _gate = new();
    readonly string _folder;
    readonly Func<IFanBackend> _openBackend;
    readonly Func<int?>? _readGpu;
    readonly AcpiThermalZone? _acpi;
    readonly TimeProvider _time = TimeProvider.System;
    readonly PipeServer _pipe;

    FanConfig _config;
    CalibrationProfile? _profile;
    MachineInfo _machine = MachineInfo.Unknown;
    IFanBackend? _backend;
    TemperatureReader? _temps;
    FanEngine? _engine;
    string? _error = "Starting";
    List<string> _conflicts = [];
    CancellationTokenSource? _calibration;
    Timer? _retry, _conflictCheck;
    bool _stopped;

    /// <param name="folder">Where config, calibration and logs live.</param>
    /// <param name="openBackend">Opens the fans; throws <see cref="HardwareUnavailableException"/> when it can't.</param>
    /// <param name="readGpu">GPU temperature source, if any.</param>
    /// <param name="acpiFallback">Fall back to the ACPI thermal zone when the backend has no CPU temperature.</param>
    public FanHost(string folder, Func<IFanBackend> openBackend, Func<int?>? readGpu, bool acpiFallback)
    {
        _folder = folder;
        _openBackend = openBackend;
        _readGpu = readGpu;
        _acpi = acpiFallback ? new AcpiThermalZone() : null;
        _profile = CalibrationProfile.Load(folder);
        _config = FanConfig.Load(folder, Floor);
        _pipe = new PipeServer(Greeting, OnMessage);
    }

    int Floor => _profile?.Floor ?? CalibrationProfile.UncalibratedFloor;

    public void Start()
    {
        Log.Write($"FanPilot {AppInfo.Version} starting, mode {_config.Mode}, floor {Floor}%");
        _pipe.Start();
        _retry = new Timer(_ => TryOpenHardware(), null, TimeSpan.Zero, RetryInterval);
        _conflictCheck = new Timer(_ => CheckConflicts(), null, TimeSpan.FromSeconds(2), ConflictInterval);
        Task.Run(() =>
        {
            var machine = SystemInfo.ReadMachine();
            Log.Write($"Machine: {machine.Manufacturer} {machine.Model}, BIOS {machine.Bios}, ASUS SCI driver {machine.AsusDriver}");
            lock (_gate) _machine = machine;
            BroadcastStatus();
        });
    }

    void TryOpenHardware()
    {
        lock (_gate)
        {
            if (_stopped || _engine is not null) return;
            try
            {
                _backend = _openBackend();
                _temps = new TemperatureReader(_backend, _readGpu, _acpi is null ? null : _acpi.Read);
                _engine = new FanEngine(_backend, _temps, _config, _profile);
                _engine.Updated += s => _pipe.Broadcast(new PipeMessage { Type = PipeMessage.Snapshot, FanSnapshot = s });
                _engine.Start();
                _error = null;
                _retry?.Change(Timeout.Infinite, Timeout.Infinite);
            }
            catch (Exception ex)
            {
                string message = ex is HardwareUnavailableException ? ex.Message : $"Couldn't open the ASUS driver: {ex.Message}";
                if (message != _error) Log.Write($"Hardware unavailable: {ex}");
                _error = message;
                _backend?.Dispose();
                _backend = null;
            }
        }
        BroadcastStatus();
    }

    void CheckConflicts()
    {
        var found = SystemInfo.RunningFanApps();
        bool changed;
        lock (_gate)
        {
            changed = !found.SequenceEqual(_conflicts);
            _conflicts = found;
        }
        if (!changed) return;
        if (found.Count > 0) Log.Write($"Other fan software running: {string.Join(", ", found)}");
        BroadcastStatus();
    }

    ServiceStatus BuildStatus() => new(
        _backend?.FanCount ?? 0,
        _error,
        Floor,
        _profile,
        _profile is not null && _machine != MachineInfo.Unknown && !_profile.MatchesMachine(_machine),
        _calibration is not null,
        [.. _conflicts]);

    void BroadcastStatus()
    {
        PipeMessage message;
        lock (_gate) message = new PipeMessage { Type = PipeMessage.Status, ServiceStatus = BuildStatus() };
        _pipe.Broadcast(message);
    }

    PipeMessage Greeting()
    {
        lock (_gate)
            return new PipeMessage { Type = PipeMessage.Status, ServiceStatus = BuildStatus(), FanConfig = _config };
    }

    void OnMessage(PipeMessage message)
    {
        switch (message.Type)
        {
            case PipeMessage.SetConfig when message.FanConfig is not null:
                lock (_gate)
                {
                    if (_stopped) return;
                    var config = message.FanConfig.Normalized(Floor);
                    if (config.Mode != _config.Mode) Log.Write($"Mode -> {config.Mode}");
                    ApplyConfig(config);
                }
                break;
            case PipeMessage.StartCalibration:
                StartCalibration();
                break;
            case PipeMessage.CancelCalibration:
                lock (_gate) _calibration?.Cancel();
                break;
            case PipeMessage.GetDiagnostics:
                _pipe.Broadcast(new PipeMessage { Type = PipeMessage.Diagnostics, Text = BuildDiagnostics() });
                break;
        }
    }

    /// <summary>Caller holds _gate. Saved and echoed inside the lock so edits land in order.</summary>
    void ApplyConfig(FanConfig config)
    {
        _config = config;
        _engine?.Configure(config);
        config.Save(_folder);
        _pipe.Broadcast(new PipeMessage { Type = PipeMessage.Config, FanConfig = config });
    }

    void StartCalibration()
    {
        CancellationTokenSource cts;
        IFanBackend backend;
        TemperatureReader temps;
        MachineInfo machine;
        lock (_gate)
        {
            if (_stopped || _backend is null || _temps is null || _engine is null || _calibration is not null) return;
            cts = _calibration = new CancellationTokenSource();
            backend = _backend;
            temps = _temps;
            machine = _machine;
            _engine.Pause();
        }
        Log.Write("Fan setup started");
        BroadcastStatus();

        Task.Run(async () =>
        {
            CalibrationProfile? result = null;
            string? error = null;
            try
            {
                // Fan setup can start before the startup machine-info read has finished; the stamp
                // must be real, or the profile would look stale straight away.
                if (machine == MachineInfo.Unknown)
                {
                    machine = SystemInfo.ReadMachine();
                    lock (_gate) _machine = machine;
                }
                var runner = new CalibrationRunner(backend, temps, _time);
                result = await runner.RunAsync(machine,
                    p => _pipe.Broadcast(new PipeMessage { Type = PipeMessage.CalibrationProgress, Progress = p }), cts.Token);
            }
            catch (OperationCanceledException) { error = "Fan setup was cancelled. Nothing changed."; }
            catch (CalibrationAbortedException ex) { error = ex.Message; }
            catch (Exception ex)
            {
                Log.Write($"Fan setup failed: {ex}");
                error = $"Fan setup failed: {ex.Message}";
            }

            lock (_gate)
            {
                if (result is not null && !_stopped)
                {
                    int oldFloor = Floor;
                    _profile = result;
                    result.Save(_folder);
                    _engine?.SetProfile(result);

                    // A curve still at the old default moves with the new floor; a user's own curve is only clamped.
                    var config = _config;
                    if (config.SameCurve(config with { Curve = FanCurve.CreateDefault(oldFloor) }))
                        config = config with { Curve = FanCurve.CreateDefault(Floor) };
                    ApplyConfig(config.Normalized(Floor));
                }
                _calibration = null;
                if (!_stopped) _engine?.Resume();
            }
            cts.Dispose();
            Log.Write(error ?? $"Fan setup done, floor {Floor}%");
            _pipe.Broadcast(new PipeMessage { Type = PipeMessage.CalibrationDone, Calibration = result, Error = error });
            BroadcastStatus();
        });
    }

    /// <summary>Test mode can survive sleep on some models and leave fans stopped, so hand them back first.</summary>
    public void OnSuspend()
    {
        lock (_gate)
        {
            _calibration?.Cancel();
            _engine?.Pause();
        }
        Log.Write("Going to sleep, fans handed to ASUS");
    }

    public void OnResume()
    {
        Log.Write("Resumed from sleep");
        Task.Delay(ResumeDelay).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (!_stopped && _calibration is null) _engine?.Resume();
            }
        });
    }

    string BuildDiagnostics()
    {
        var text = new StringBuilder();
        lock (_gate)
        {
            text.AppendLine($"FanPilot {AppInfo.Version}");
            text.AppendLine($"OS: {_machine.Os}");
            text.AppendLine($"Machine: {_machine.Manufacturer} {_machine.Model}");
            text.AppendLine($"BIOS: {_machine.Bios}");
            text.AppendLine($"ASUS System Control Interface driver: {_machine.AsusDriver}");
            text.AppendLine($"Backend: {_backend?.Description ?? "none"}");
            text.AppendLine($"Fans: {_backend?.FanCount ?? 0}");
            text.AppendLine($"Hardware error: {_error ?? "none"}");
            if (_temps is not null)
            {
                var t = _temps.Read();
                text.AppendLine($"CPU temp: {t.Cpu?.ToString() ?? "none"} C ({t.CpuSource ?? "no source"}), GPU temp: {t.Gpu?.ToString() ?? "none"} C");
            }
            text.AppendLine($"Other fan software running: {(_conflicts.Count > 0 ? string.Join(", ", _conflicts) : "none")}");
            text.AppendLine($"Floor: {Floor}%");
            if (_profile is null)
            {
                text.AppendLine("Fan setup: not run");
            }
            else
            {
                text.AppendLine($"Fan setup: {_profile.CalibratedUtc.ToLocalTime():yyyy-MM-dd HH:mm} on {_profile.Machine}, BIOS {_profile.Bios}, driver {_profile.Driver}, CPU temp readable {_profile.CpuTempReadable}");
                foreach (var f in _profile.Fans)
                {
                    text.AppendLine($"  Fan {f.Index + 1}: controllable {f.Controllable}, stall {f.StallPercent}%, start {f.StartPercent}%, " +
                                    $"min {f.MinPercent}%, ASUS idle {f.AsusIdleRpm} RPM, silent {f.SilentPercent}%");
                    text.AppendLine("    " + string.Join(", ", f.Table.Select(r => $"{r.Percent}%={r.Rpm}")));
                }
            }
            text.AppendLine($"Config: {JsonSerializer.Serialize(_config, Protocol.Json)}");
        }
        text.AppendLine("Recent log:");
        foreach (var line in Log.Tail(50)) text.AppendLine("  " + line);
        return SystemInfo.Scrub(text.ToString());
    }

    /// <summary>Crash path: no locks, just try to hand the fans back.</summary>
    public void EmergencyRestore() => _engine?.EmergencyRestore();

    /// <summary>Hands the fans back to ASUS. Runs on service stop and on shutdown.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
            _calibration?.Cancel();
        }
        Log.Write("Stopping, handing fans back to ASUS");
        _retry?.Dispose();
        _conflictCheck?.Dispose();
        _pipe.Dispose();
        _engine?.Stop(restoreAsus: true);
        _engine?.Dispose();
        _backend?.Dispose();
        _acpi?.Dispose();
    }

    public void Dispose() => Stop();
}
