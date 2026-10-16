using System.Text.Json;
using System.Text.Json.Serialization;
using FanPilot.Control;

namespace FanPilot.Ipc;

/// <summary>
/// Newline-delimited JSON over the pipe "FanPilot".
/// <para>Service to window: "status" on connect and whenever hardware, calibration or conflict state
/// changes (on connect it also carries the config); "config" after any change; "snapshot" every
/// second; "calibration-progress" while calibrating; "calibration-done" when it ends (with the profile,
/// or an error); "diagnostics" in answer to "get-diagnostics".</para>
/// <para>Window to service: "set-config", "start-calibration", "cancel-calibration", "get-diagnostics".</para>
/// </summary>
public sealed class PipeMessage
{
    public const string Status = "status";
    public const string Config = "config";
    public const string Snapshot = "snapshot";
    public const string CalibrationProgress = "calibration-progress";
    public const string CalibrationDone = "calibration-done";
    public const string Diagnostics = "diagnostics";

    public const string SetConfig = "set-config";
    public const string StartCalibration = "start-calibration";
    public const string CancelCalibration = "cancel-calibration";
    public const string GetDiagnostics = "get-diagnostics";

    public string Type { get; set; } = "";
    public string? Error { get; set; }
    public FanConfig? FanConfig { get; set; }
    public FanSnapshot? FanSnapshot { get; set; }
    public ServiceStatus? ServiceStatus { get; set; }
    public CalibrationProgress? Progress { get; set; }
    public CalibrationProfile? Calibration { get; set; }
    public string? Text { get; set; }
}

/// <summary>What the window needs to know about the service beyond the config.</summary>
public sealed record ServiceStatus(
    int FanCount,
    string? HardwareError,
    int Floor,
    CalibrationProfile? Calibration,
    bool CalibrationStale,
    bool Calibrating,
    List<string> ConflictingApps);

public static class Protocol
{
    public const string PipeName = "FanPilot";

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(PipeMessage message) => JsonSerializer.Serialize(message, Json);

    public static PipeMessage? Deserialize(string line)
    {
        try { return JsonSerializer.Deserialize<PipeMessage>(line, Json); }
        catch (JsonException) { return null; }
    }
}
