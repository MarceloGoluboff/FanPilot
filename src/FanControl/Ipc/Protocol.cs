using System.Text.Json;
using System.Text.Json.Serialization;
using FanControl.Control;

namespace FanControl.Ipc;

/// <summary>
/// Newline-delimited JSON over the pipe "MarceloGoluboff.FanControl".
/// Service to window: "status" on connect and whenever hardware state changes, "config" after any
/// change, "snapshot" every second. Window to service: "set-config".
/// </summary>
public sealed class PipeMessage
{
    public const string Status = "status";
    public const string Config = "config";
    public const string Snapshot = "snapshot";
    public const string SetConfig = "set-config";

    public string Type { get; set; } = "";
    public int FanCount { get; set; }
    public string? Error { get; set; }
    public FanConfig? FanConfig { get; set; }
    public FanSnapshot? FanSnapshot { get; set; }
}

public static class Protocol
{
    public const string PipeName = "MarceloGoluboff.FanControl";

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
