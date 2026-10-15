using System.Text.Json;

namespace FanControl.Control;

public enum FanMode { AsusAuto, Manual, Curve }

/// <summary>What the fans should do. Owned by the service, persisted in %ProgramData%\FanControl\fan-config.json.</summary>
public sealed record FanConfig
{
    public FanMode Mode { get; init; } = FanMode.AsusAuto;
    public int ManualPercent { get; init; } = 60;
    public FanCurve Curve { get; init; } = FanCurve.CreateDefault();
    public bool ForbidUnsafe { get; init; } = true;
    public int OverrideTemp { get; init; } = 95;

    public int MinPercent => ForbidUnsafe ? FanEngine.SafeFloorPercent : FanEngine.MinPercent;

    /// <summary>Clamps anything that came off disk or over the pipe.</summary>
    public FanConfig Normalized()
    {
        var mode = Enum.IsDefined(Mode) ? Mode : FanMode.AsusAuto;
        return this with
        {
            Mode = mode,
            ManualPercent = Math.Clamp(ManualPercent, MinPercent, 100),
            Curve = (Curve ?? FanCurve.CreateDefault()).Normalized(MinPercent),
            OverrideTemp = Math.Clamp(OverrideTemp, 80, 100),
        };
    }

    static string FilePath => Path.Combine(Log.MachineFolder, "fan-config.json");

    public static FanConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return (JsonSerializer.Deserialize<FanConfig>(File.ReadAllText(FilePath), Ipc.Protocol.Json) ?? new()).Normalized();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"Fan config unreadable, using defaults: {ex.Message}");
        }
        return new FanConfig().Normalized();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.MachineFolder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Ipc.Protocol.Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Fan config save failed: {ex.Message}");
        }
    }

    public bool SameCurve(FanConfig other) =>
        Curve.Points.Count == other.Curve.Points.Count &&
        Curve.Points.Zip(other.Curve.Points).All(p => p.First.Temp == p.Second.Temp && p.First.Percent == p.Second.Percent);
}
