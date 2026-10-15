using System.Collections.ObjectModel;
using System.Windows.Input;
using FanControl.Control;
using FanControl.Ipc;
using FanControl.Settings;
using FanControl.Startup;

namespace FanControl.UI;

public sealed class StatItem(string label) : ObservableObject
{
    string _value = "--";
    string _detail = "";

    public string Label { get; } = label;
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
}

public sealed record Preset(string Name, int Percent);

/// <summary>
/// Window state. Fan settings are a local copy of the service's config: edits are sent over the
/// pipe and the service echoes the accepted config back. Runs on the UI thread.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    readonly AppSettings _settings;
    readonly ServiceClient _client;

    FanConfig _config = new FanConfig().Normalized();
    bool _connected;
    bool _connectTimedOut;
    int _pendingEchoes;   // set-config messages the service hasn't echoed yet
    string? _serviceError;
    StatItem _tempStat = new("CPU");
    StatItem[] _fanStats = [];
    string _statusText = "Connecting to the FanControl service...";
    string _trayText = "FanControl";

    public MainViewModel(AppSettings settings, ServiceClient client)
    {
        _settings = settings;
        _client = client;
        Stats = [];
        BuildStats(2);

        PresetCommand = new RelayCommand(p =>
        {
            if (p is int percent || int.TryParse(p?.ToString(), out percent))
                Update(c => c with { Mode = FanMode.Manual, ManualPercent = percent });
        });
        ResetCurveCommand = new RelayCommand(_ =>
        {
            Update(c => c with { Curve = FanCurve.CreateDefault() });
            CurveReset?.Invoke();
        });
    }

    public event Action<FanSnapshot>? SnapshotReceived;
    public event Action? CurveReset;
    public event Action? FansChanged;
    public event Action<AppTheme>? ThemeChanged;

    public ObservableCollection<StatItem> Stats { get; }
    public IReadOnlyList<Preset> Presets { get; } =
        [new("Silent", 40), new("Balanced", 60), new("Performance", 80), new("Max", 100)];

    public ICommand PresetCommand { get; }
    public ICommand ResetCurveCommand { get; }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string TrayText { get => _trayText; private set => Set(ref _trayText, value); }

    // ---- Connection and errors ----

    public string? ErrorMessage =>
        !_connected
            ? (_connectTimedOut
                ? "Can't reach the FanControl service. It may still be starting. If this doesn't clear, reinstall FanControl."
                : null)
            : _serviceError;

    public bool HasError => ErrorMessage is not null;
    public bool IsControlEnabled => _connected && _serviceError is null;

    void RaiseConnectionState()
    {
        Raise(nameof(ErrorMessage));
        Raise(nameof(HasError));
        Raise(nameof(IsControlEnabled));
        UpdateStatus(null);
    }

    public void OnConnectionChanged(bool connected)
    {
        _connected = connected;
        _pendingEchoes = 0;
        if (!connected) _serviceError = null;
        RaiseConnectionState();
    }

    /// <summary>Called a few seconds after launch so a brief connect delay doesn't flash an error.</summary>
    public void MarkConnectTimeout()
    {
        _connectTimedOut = true;
        RaiseConnectionState();
    }

    public void OnMessage(PipeMessage message)
    {
        switch (message.Type)
        {
            case PipeMessage.Status:
                _serviceError = message.Error;
                if (message.FanCount > 0 && message.FanCount != _fanStats.Length) BuildStats(message.FanCount);
                if (message.FanConfig is not null) ApplyConfig(message.FanConfig);
                RaiseConnectionState();
                break;
            case PipeMessage.Config when message.FanConfig is not null:
                // While our own edits are in flight, intermediate echoes would make the slider jump back.
                if (_pendingEchoes > 0) _pendingEchoes--;
                if (_pendingEchoes == 0) ApplyConfig(message.FanConfig);
                break;
            case PipeMessage.Snapshot when message.FanSnapshot is not null:
                OnSnapshot(message.FanSnapshot);
                break;
        }
    }

    // ---- Fan config ----

    void Update(Func<FanConfig, FanConfig> change)
    {
        var next = change(_config).Normalized();
        bool curveChanged = !next.SameCurve(_config);
        SetConfig(next);
        if (curveChanged) CurveReset?.Invoke();
        if (_client.Send(new PipeMessage { Type = PipeMessage.SetConfig, FanConfig = next })) _pendingEchoes++;
    }

    /// <summary>Config pushed by the service, after our own change or another window's.</summary>
    void ApplyConfig(FanConfig config)
    {
        bool curveChanged = !config.SameCurve(_config);
        SetConfig(config);
        if (curveChanged) CurveReset?.Invoke();
    }

    void SetConfig(FanConfig config)
    {
        _config = config;
        Raise(nameof(Mode));
        Raise(nameof(IsAutoMode));
        Raise(nameof(IsManualMode));
        Raise(nameof(IsCurveMode));
        Raise(nameof(ModeDescription));
        Raise(nameof(ManualPercent));
        Raise(nameof(MinManualPercent));
        Raise(nameof(ForbidUnsafe));
        Raise(nameof(OverrideTemp));
    }

    public FanMode Mode
    {
        get => _config.Mode;
        set { if (value != _config.Mode) Update(c => c with { Mode = value }); }
    }

    public bool IsAutoMode { get => Mode == FanMode.AsusAuto; set { if (value) Mode = FanMode.AsusAuto; } }
    public bool IsManualMode { get => Mode == FanMode.Manual; set { if (value) Mode = FanMode.Manual; } }
    public bool IsCurveMode { get => Mode == FanMode.Curve; set { if (value) Mode = FanMode.Curve; } }

    public string ModeDescription => Mode switch
    {
        FanMode.AsusAuto => "The laptop's firmware controls the fans, the same as without this app.",
        FanMode.Manual => "All fans hold a fixed speed.",
        _ => "Fan speed follows CPU temperature. Drag points to shape the curve, double-click to add one, right-click to remove one.",
    };

    public int ManualPercent
    {
        get => _config.ManualPercent;
        set { if (value != _config.ManualPercent) Update(c => c with { ManualPercent = value }); }
    }

    public int MinManualPercent => _config.MinPercent;

    /// <summary>A copy for the curve editor, which edits it in place.</summary>
    public FanCurve CurveForEditor() => _config.Curve.Clone();

    /// <summary>Called by the curve editor after a drag, add or remove.</summary>
    public void CurveEdited(FanCurve curve) => Update(c => c with { Curve = curve.Clone() });

    public bool ForbidUnsafe
    {
        get => _config.ForbidUnsafe;
        set { if (value != _config.ForbidUnsafe) Update(c => c with { ForbidUnsafe = value }); }
    }

    public IReadOnlyList<int> OverrideTemps { get; } = [85, 90, 95, 100];

    public int OverrideTemp
    {
        get => _config.OverrideTemp;
        set { if (value != _config.OverrideTemp) Update(c => c with { OverrideTemp = value }); }
    }

    // ---- Window preferences ----

    public bool OpenAtSignIn
    {
        get => RunAtLogon.IsEnabled;
        set
        {
            try { RunAtLogon.Set(value); }
            catch (Exception ex) { Log.Write($"Run key change failed: {ex.Message}"); }
            Raise();
        }
    }

    public bool CloseToTray
    {
        get => _settings.CloseToTray;
        set { _settings.CloseToTray = value; Raise(); _settings.Save(); }
    }

    public IReadOnlyList<AppTheme> Themes { get; } = [AppTheme.System, AppTheme.Dark, AppTheme.Light];

    public AppTheme Theme
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value) return;
            _settings.Theme = value;
            Raise();
            _settings.Save();
            ThemeChanged?.Invoke(value);
        }
    }

    // ---- Readings ----

    void BuildStats(int fanCount)
    {
        _tempStat = new StatItem("CPU");
        _fanStats = Enumerable.Range(0, fanCount)
            .Select(i => new StatItem(fanCount == 2 ? (i == 0 ? "CPU fan" : "GPU fan") : $"Fan {i + 1}"))
            .ToArray();
        Stats.Clear();
        Stats.Add(_tempStat);
        foreach (var stat in _fanStats) Stats.Add(stat);
        FansChanged?.Invoke();
    }

    void OnSnapshot(FanSnapshot s)
    {
        _tempStat.Value = s.CpuTemp is int t ? $"{t} °C" : "--";
        _tempStat.Detail = s.OverrideActive ? "Thermal override" : "Package";

        for (int i = 0; i < _fanStats.Length; i++)
        {
            _fanStats[i].Value = i < s.Rpms.Length ? $"{s.Rpms[i]:N0} RPM" : "--";
            _fanStats[i].Detail = s.AppliedPercent is int p ? $"{p}% duty" : "ASUS auto";
        }

        UpdateStatus(s);
        SnapshotReceived?.Invoke(s);
    }

    void UpdateStatus(FanSnapshot? s)
    {
        if (!_connected)
        {
            StatusText = _connectTimedOut ? "Waiting for the FanControl service..." : "Connecting to the FanControl service...";
            TrayText = "FanControl: service not running";
            return;
        }
        if (_serviceError is not null)
        {
            StatusText = "Fan control is unavailable.";
            TrayText = "FanControl: unavailable";
            return;
        }
        if (s is null) return;

        string applied = s.AppliedPercent is int p ? $"{p}%" : "ASUS auto";
        StatusText = s.Error is not null
            ? $"Hardware error: {s.Error}"
            : s.OverrideActive
                ? $"Thermal override: CPU at {s.CpuTemp} °C, fans forced to 100% until it drops below {OverrideTemp - 10} °C."
                : $"Fans: {applied}. Fan control runs as a background service, so it keeps going when this window is closed.";

        string rpm = s.Rpms.Length > 0 ? string.Join(" / ", s.Rpms.Select(r => $"{r:N0}")) + " RPM" : "";
        TrayText = $"FanControl: {(s.CpuTemp is int t ? $"{t} °C" : "--")}, {rpm}\n{ModeName(s.Mode)} ({applied})";
    }

    static string ModeName(FanMode mode) => mode switch
    {
        FanMode.AsusAuto => "ASUS Auto",
        FanMode.Manual => "Manual",
        _ => "Curve",
    };
}
