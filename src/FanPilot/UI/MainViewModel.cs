using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using FanPilot.Control;
using FanPilot.Ipc;
using FanPilot.Settings;

namespace FanPilot.UI;

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

    FanConfig _config = new FanConfig().Normalized(CalibrationProfile.UncalibratedFloor);
    ServiceStatus? _status;
    bool _connected;
    bool _connectTimedOut;
    int _pendingEchoes;   // set-config messages the service hasn't echoed yet
    bool _awaitingDiagnostics;
    StatItem _cpuStat = new("CPU"), _gpuStat = new("GPU");
    StatItem[] _fanStats = [];
    string _statusText = "Connecting to the FanPilot service...";
    string _trayText = "FanPilot";
    CalibrationProgress? _progress;
    string? _fanSetupMessage;

    public MainViewModel(AppSettings settings, ServiceClient client)
    {
        _settings = settings;
        _client = client;

        PresetCommand = new RelayCommand(p =>
        {
            if (p is int percent || int.TryParse(p?.ToString(), out percent))
                Update(c => c with { Mode = FanMode.Manual, ManualPercent = percent });
        });
        ResetCurveCommand = new RelayCommand(_ => Update(c => c with { Curve = FanCurve.CreateDefault(Floor) }));
        StartFanSetupCommand = new RelayCommand(_ =>
        {
            FanSetupMessage = null;
            _client.Send(new PipeMessage { Type = PipeMessage.StartCalibration });
        });
        CancelFanSetupCommand = new RelayCommand(_ => _client.Send(new PipeMessage { Type = PipeMessage.CancelCalibration }));
        DismissFanSetupCommand = new RelayCommand(_ =>
        {
            _settings.FanSetupPromptDismissed = true;
            _settings.Save();
            Raise(nameof(ShowFanSetupPrompt));
        });
        CopyDiagnosticsCommand = new RelayCommand(_ =>
        {
            _awaitingDiagnostics = _client.Send(new PipeMessage { Type = PipeMessage.GetDiagnostics });
            if (!_awaitingDiagnostics) StatusText = "Can't reach the FanPilot service, so there's nothing to copy.";
        });
    }

    public event Action<FanSnapshot>? SnapshotReceived;
    public event Action? CurveReset;
    public event Action? FansChanged;
    public event Action<AppTheme>? ThemeChanged;

    public ObservableCollection<StatItem> Stats { get; } = [];
    public ObservableCollection<Preset> Presets { get; } = [];
    public IReadOnlyList<StatItem> FanStats => _fanStats;

    /// <summary>Stat cards per row: all in one row up to four, two rows beyond.</summary>
    public int StatsColumns => Stats.Count <= 4 ? Math.Max(1, Stats.Count) : 3;

    public ICommand PresetCommand { get; }
    public ICommand ResetCurveCommand { get; }
    public ICommand StartFanSetupCommand { get; }
    public ICommand CancelFanSetupCommand { get; }
    public ICommand DismissFanSetupCommand { get; }
    public ICommand CopyDiagnosticsCommand { get; }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string TrayText { get => _trayText; private set => Set(ref _trayText, value); }

    // ---- Connection, errors and warnings ----

    string? HardwareError => _status?.HardwareError;

    public string? ErrorMessage =>
        !_connected
            ? (_connectTimedOut
                ? "Can't reach the FanPilot service. It may still be starting. If this doesn't clear, reinstall FanPilot."
                : null)
            : HardwareError;

    public bool HasError => ErrorMessage is not null;
    public bool IsControlEnabled => _connected && _status is not null && HardwareError is null && !IsCalibrating;

    public string? ConflictMessage => _status?.ConflictingApps is { Count: > 0 } apps
        ? $"{string.Join(" and ", apps)} {(apps.Count == 1 ? "is" : "are")} also running. Two apps writing fan speeds will fight; close one of them."
        : null;

    public bool HasConflict => ConflictMessage is not null;

    void RaiseStatus()
    {
        Raise(nameof(ErrorMessage));
        Raise(nameof(HasError));
        Raise(nameof(IsControlEnabled));
        Raise(nameof(ConflictMessage));
        Raise(nameof(HasConflict));
        Raise(nameof(Floor));
        Raise(nameof(ShowFanSetupPrompt));
        Raise(nameof(FanSetupStale));
        Raise(nameof(IsCalibrating));
        Raise(nameof(FanSetupSummary));
        Raise(nameof(FanSetupButtonText));
        UpdateStatus(null);
    }

    public void OnConnectionChanged(bool connected)
    {
        _connected = connected;
        _pendingEchoes = 0;
        if (!connected) _status = null;
        RaiseStatus();
    }

    /// <summary>Called a few seconds after launch so a brief connect delay doesn't flash an error.</summary>
    public void MarkConnectTimeout()
    {
        _connectTimedOut = true;
        RaiseStatus();
    }

    public void OnMessage(PipeMessage message)
    {
        switch (message.Type)
        {
            case PipeMessage.Status when message.ServiceStatus is not null:
                OnStatus(message.ServiceStatus);
                if (message.FanConfig is not null) ApplyConfig(message.FanConfig);
                break;
            case PipeMessage.Config when message.FanConfig is not null:
                // While our own edits are in flight, intermediate echoes would make the slider jump back.
                if (_pendingEchoes > 0) _pendingEchoes--;
                if (_pendingEchoes == 0) ApplyConfig(message.FanConfig);
                break;
            case PipeMessage.Snapshot when message.FanSnapshot is not null:
                OnSnapshot(message.FanSnapshot);
                break;
            case PipeMessage.CalibrationProgress when message.Progress is not null:
                _progress = message.Progress;
                Raise(nameof(FanSetupPercent));
                Raise(nameof(FanSetupStage));
                break;
            case PipeMessage.CalibrationDone:
                _progress = null;
                FanSetupMessage = message.Error ?? (message.Calibration is { } p ? $"Fan setup done. {Describe(p)}" : null);
                break;
            case PipeMessage.Diagnostics when _awaitingDiagnostics && message.Text is not null:
                _awaitingDiagnostics = false;
                try
                {
                    Clipboard.SetText(message.Text);
                    StatusText = "Diagnostics copied to the clipboard. Paste them into a GitHub issue.";
                }
                catch (Exception ex) { StatusText = $"Couldn't copy diagnostics: {ex.Message}"; }
                break;
        }
    }

    void OnStatus(ServiceStatus status)
    {
        int oldFloor = Floor;
        int oldFans = _fanStats.Length;
        _status = status;
        if (status.FanCount > 0 && status.FanCount != oldFans) BuildStats(status.FanCount);
        BuildPresets();
        if (Floor != oldFloor) CurveReset?.Invoke();
        RaiseStatus();
    }

    // ---- Fan setup (calibration) ----

    public int Floor => _status?.Floor ?? CalibrationProfile.UncalibratedFloor;

    public bool IsCalibrating => _status?.Calibrating == true;

    public bool ShowFanSetupPrompt =>
        _connected && _status is { HardwareError: null, Calibration: null, Calibrating: false } && !_settings.FanSetupPromptDismissed;

    public bool FanSetupStale => _status is { CalibrationStale: true, Calibrating: false };

    public int FanSetupPercent => _progress?.Percent ?? 0;

    public string FanSetupStage => _progress is { } p
        ? p.Stage +
          (p.DutyPercent is int d && p.Rpms is { } r ? $": {d}% gives {string.Join(" / ", r.Select(x => $"{x:N0}"))} RPM" : "") +
          (p.CpuTemp is int t ? $". CPU {t} °C." : ".")
        : "Starting...";

    public string? FanSetupMessage
    {
        get => _fanSetupMessage;
        private set
        {
            Set(ref _fanSetupMessage, value);
            Raise(nameof(HasFanSetupMessage));
        }
    }

    public bool HasFanSetupMessage => _fanSetupMessage is not null;

    public string FanSetupSummary => _status?.Calibration is { } p
        ? $"Done {p.CalibratedUtc.ToLocalTime():MMM d, yyyy}. {Describe(p)}"
        : $"Not run yet. Until it is, fans never go below {CalibrationProfile.UncalibratedFloor}%.";

    public string FanSetupButtonText => _status?.Calibration is null ? "Run fan setup" : "Run again";

    static string Describe(CalibrationProfile p)
    {
        var parts = p.Fans.Select(f => f.Controllable
            ? $"Fan {f.Index + 1} keeps spinning down to {f.Table.Where(r => r.Rpm >= CalibrationAnalysis.StallRpm).Select(r => r.Percent).DefaultIfEmpty(100).Min()}%"
            : $"Fan {f.Index + 1} doesn't follow commands, so ASUS keeps it");
        return $"{string.Join("; ", parts)}. Lowest speed is now {p.Floor}%.";
    }

    // ---- Fan config ----

    void Update(Func<FanConfig, FanConfig> change)
    {
        var next = change(_config).Normalized(Floor);
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
        Raise(nameof(OverrideTemp));
        Raise(nameof(GpuOverrideTemp));
        Raise(nameof(CurveUsesGpu));
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
        FanMode.AsusAuto => "The laptop's firmware controls the fans, the same as without FanPilot.",
        FanMode.Manual => "All fans hold a fixed speed.",
        _ => $"Fan speed follows {(CurveUsesGpu ? "the hotter of CPU and GPU" : "CPU temperature")}. " +
             "Drag points to shape the curve, double-click to add one, right-click to remove one.",
    };

    public int ManualPercent
    {
        get => _config.ManualPercent;
        set { if (value != _config.ManualPercent) Update(c => c with { ManualPercent = value }); }
    }

    /// <summary>A copy for the curve editor, which edits it in place.</summary>
    public FanCurve CurveForEditor() => _config.Curve.Clone();

    /// <summary>Called by the curve editor after a drag, add or remove.</summary>
    public void CurveEdited(FanCurve curve) => Update(c => c with { Curve = curve.Clone() });

    public IReadOnlyList<int> OverrideTemps { get; } = [85, 90, 95, 100];
    public IReadOnlyList<int> GpuOverrideTemps { get; } = [80, 83, 85, 87, 90, 93, 95];

    public int OverrideTemp
    {
        get => _config.OverrideTemp;
        set { if (value != _config.OverrideTemp) Update(c => c with { OverrideTemp = value }); }
    }

    public int GpuOverrideTemp
    {
        get => _config.GpuOverrideTemp;
        set { if (value != _config.GpuOverrideTemp) Update(c => c with { GpuOverrideTemp = value }); }
    }

    public bool CurveUsesGpu
    {
        get => _config.CurveUsesGpu;
        set
        {
            if (value == _config.CurveUsesGpu) return;
            Update(c => c with { CurveUsesGpu = value });
            Raise(nameof(ModeDescription));
        }
    }

    // ---- Window preferences ----

    public bool TrayAtSignIn
    {
        get => _settings.TrayAtSignIn;
        set { _settings.TrayAtSignIn = value; Raise(); _settings.Save(); }
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

    public string VersionText => $"FanPilot {AppInfo.Version}";

    // ---- Readings ----

    void BuildStats(int fanCount)
    {
        _cpuStat = new StatItem("CPU");
        _gpuStat = new StatItem("GPU");
        _fanStats = Enumerable.Range(0, fanCount)
            .Select(i => new StatItem(fanCount switch
            {
                1 => "Fan",
                2 => i == 0 ? "Fan 1 (CPU)" : "Fan 2 (GPU)",
                _ => $"Fan {i + 1}",
            }))
            .ToArray();
        Stats.Clear();
        Stats.Add(_cpuStat);
        Stats.Add(_gpuStat);
        foreach (var stat in _fanStats) Stats.Add(stat);
        Raise(nameof(StatsColumns));
        FansChanged?.Invoke();
    }

    void BuildPresets()
    {
        var presets = new List<Preset>();
        if (_status?.Calibration?.SilentPercent is int silent) presets.Add(new Preset("Silent", Math.Max(silent, Floor)));
        presets.Add(new Preset("Balanced", Math.Max(50, Floor)));
        presets.Add(new Preset("Performance", 75));
        presets.Add(new Preset("Max", 100));
        if (presets.SequenceEqual(Presets)) return;
        Presets.Clear();
        foreach (var p in presets) Presets.Add(p);
    }

    void OnSnapshot(FanSnapshot s)
    {
        _cpuStat.Value = s.CpuTemp is int t ? $"{t} °C" : "--";
        _cpuStat.Detail = s.CpuTemp is null ? "No reading" : s.CpuTempSource == "ASUS" ? "Package" : s.CpuTempSource ?? "";
        _gpuStat.Value = s.GpuTemp is int g ? $"{g} °C" : "--";
        _gpuStat.Detail = s.GpuTemp is null ? "Idle or no reading" : "Core";
        if (s.OverrideActive) _cpuStat.Detail = _gpuStat.Detail = "Thermal override";

        for (int i = 0; i < _fanStats.Length; i++)
        {
            _fanStats[i].Value = i < s.Rpms.Length ? $"{s.Rpms[i]:N0} RPM" : "--";
            bool controllable = _status?.Calibration?.IsControllable(i) != false;
            _fanStats[i].Detail = s.Paused ? (IsCalibrating ? "Fan setup" : "ASUS auto")
                : !controllable ? "ASUS (not controllable)"
                : s.AppliedPercent is int p ? $"{p}% duty" : "ASUS auto";
        }

        UpdateStatus(s);
        SnapshotReceived?.Invoke(s);
    }

    void UpdateStatus(FanSnapshot? s)
    {
        if (!_connected)
        {
            StatusText = _connectTimedOut ? "Waiting for the FanPilot service..." : "Connecting to the FanPilot service...";
            TrayText = "FanPilot: service not running";
            return;
        }
        if (HardwareError is not null)
        {
            StatusText = "Fan control is unavailable.";
            TrayText = "FanPilot: unavailable";
            return;
        }
        if (s is null) return;

        string applied = s.AppliedPercent is int p ? $"{p}%" : "ASUS auto";
        string hot = string.Join(" and ", new[]
        {
            s.CpuTemp is int c && c >= OverrideTemp - 10 ? $"CPU at {c} °C" : null,
            s.GpuTemp is int g && g >= GpuOverrideTemp - 10 ? $"GPU at {g} °C" : null,
        }.Where(x => x is not null));
        StatusText = s.Error is not null
            ? $"Hardware error: {s.Error}"
            : s.OverrideActive
                ? $"Thermal override: {hot}, fans forced to 100% until it cools down."
                : IsCalibrating
                    ? "Fan setup is running. Fan speeds will change; this is expected."
                    : $"Fans: {applied}. FanPilot runs as a background service, so it keeps going when this window is closed.";

        string rpm = s.Rpms.Length > 0 ? string.Join(" / ", s.Rpms.Select(r => $"{r:N0}")) + " RPM" : "";
        string temps = $"CPU {(s.CpuTemp is int t ? $"{t} °C" : "--")}" + (s.GpuTemp is int gt ? $", GPU {gt} °C" : "");
        TrayText = $"FanPilot: {temps}, {rpm}\n{ModeName(s.Mode)} ({applied})";
    }

    static string ModeName(FanMode mode) => mode switch
    {
        FanMode.AsusAuto => "ASUS Auto",
        FanMode.Manual => "Manual",
        _ => "Curve",
    };
}
