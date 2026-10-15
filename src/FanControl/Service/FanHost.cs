using FanControl.Control;
using FanControl.Hardware;
using FanControl.Ipc;

namespace FanControl.Service;

/// <summary>
/// The service's work: opens the hardware (retrying while the ASUS driver comes up at boot), runs
/// the fan engine, persists the config and serves the pipe.
/// </summary>
internal sealed class FanHost : IDisposable
{
    static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    readonly object _gate = new();
    readonly PipeServer _pipe;
    FanConfig _config;
    FanController? _hardware;
    FanEngine? _engine;
    string? _error = "Starting";
    Timer? _retry;
    bool _stopped;

    public FanHost()
    {
        _config = FanConfig.Load();
        _pipe = new PipeServer(Greeting, OnMessage);
    }

    public void Start()
    {
        Log.Write($"Service starting, mode {_config.Mode}");
        _pipe.Start();
        _retry = new Timer(_ => TryOpenHardware(), null, TimeSpan.Zero, RetryInterval);
    }

    void TryOpenHardware()
    {
        lock (_gate)
        {
            if (_stopped || _engine is not null) return;
            try
            {
                _hardware = FanController.Open();
                _engine = new FanEngine(_hardware, _config);
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
                _hardware = null;
            }
            _pipe.Broadcast(StatusMessage());
        }
    }

    PipeMessage Greeting()
    {
        lock (_gate)
        {
            var status = StatusMessage();
            status.FanConfig = _config;
            return status;
        }
    }

    PipeMessage StatusMessage() => new()
    {
        Type = PipeMessage.Status,
        FanCount = _hardware?.FanCount ?? 0,
        Error = _error,
    };

    void OnMessage(PipeMessage message)
    {
        if (message.Type != PipeMessage.SetConfig || message.FanConfig is null) return;

        var config = message.FanConfig.Normalized();
        lock (_gate)
        {
            if (_stopped) return;
            if (config.Mode != _config.Mode) Log.Write($"Mode -> {config.Mode}");
            _config = config;
            _engine?.Configure(config);
            // Inside the lock so two windows' edits are saved and echoed in the order they were applied.
            config.Save();
            _pipe.Broadcast(new PipeMessage { Type = PipeMessage.Config, FanConfig = config });
        }
    }

    /// <summary>Crash path: no locks, just try to hand the fans back.</summary>
    public void EmergencyRestore() => _engine?.EmergencyRestore();

    public void OnResume()
    {
        Log.Write("Resumed from sleep, reapplying");
        _engine?.RequestReapply();
    }

    /// <summary>Hands the fans back to ASUS. Runs on service stop and on shutdown.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _stopped = true;
        }
        Log.Write("Service stopping, handing fans back to ASUS");
        _retry?.Dispose();
        _pipe.Dispose();
        _engine?.Stop(restoreAsus: true);
        _engine?.Dispose();
        _hardware?.Dispose();
    }

    public void Dispose() => Stop();
}
