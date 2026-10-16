using System.Text.Json;

namespace FanPilot.Control;

public enum FanMode { AsusAuto, Manual, Curve }

/// <summary>What the fans should do. Owned by the service and saved in its data folder.</summary>
public sealed record FanConfig
{
    public FanMode Mode { get; init; } = FanMode.AsusAuto;
    public int ManualPercent { get; init; } = 50;
    public FanCurve Curve { get; init; } = FanCurve.CreateDefault(CalibrationProfile.UncalibratedFloor);

    /// <summary>CPU temperature that forces 100%.</summary>
    public int OverrideTemp { get; init; } = 95;

    /// <summary>GPU temperature that forces 100%.</summary>
    public int GpuOverrideTemp { get; init; } = 87;

    /// <summary>The curve follows the hotter of CPU and GPU, when the GPU reports a temperature.</summary>
    public bool CurveUsesGpu { get; init; } = true;

    /// <summary>Clamps anything that came off disk or over the pipe. <paramref name="floor"/> is the lowest safe duty.</summary>
    public FanConfig Normalized(int floor)
    {
        var mode = Enum.IsDefined(Mode) ? Mode : FanMode.AsusAuto;
        return this with
        {
            Mode = mode,
            ManualPercent = Math.Clamp(ManualPercent, floor, 100),
            Curve = (Curve ?? FanCurve.CreateDefault(floor)).Normalized(floor),
            OverrideTemp = Math.Clamp(OverrideTemp, 80, 100),
            GpuOverrideTemp = Math.Clamp(GpuOverrideTemp, 75, 95),
        };
    }

    public static FanConfig Load(string folder, int floor) =>
        (JsonFile.Load<FanConfig>(Path.Combine(folder, "fan-config.json")) ?? new()).Normalized(floor);

    public void Save(string folder) => JsonFile.Save(Path.Combine(folder, "fan-config.json"), this);

    public bool SameCurve(FanConfig other) =>
        Curve.Points.Count == other.Curve.Points.Count &&
        Curve.Points.Zip(other.Curve.Points).All(p => p.First.Temp == p.Second.Temp && p.First.Percent == p.Second.Percent);
}

/// <summary>Atomic JSON load and save. A file that can't be read loads as null instead of throwing.</summary>
internal static class JsonFile
{
    public static T? Load<T>(string path) where T : class
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Ipc.Protocol.Json);
        }
        catch (Exception ex)
        {
            // Anything thrown here would stop the service from starting.
            Log.Write($"{Path.GetFileName(path)} unreadable, using defaults: {ex.Message}");
        }
        return null;
    }

    public static void Save<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Ipc.Protocol.Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Saving {Path.GetFileName(path)} failed: {ex.Message}");
        }
    }
}
