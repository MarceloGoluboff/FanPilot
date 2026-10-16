namespace FanPilot.Hardware;

public sealed class HardwareUnavailableException(string message) : Exception(message);

/// <summary>
/// Something that can read and drive the laptop's fans. Implementations must be thread-safe:
/// the engine, calibration and diagnostics may call in from different threads.
/// </summary>
public interface IFanBackend : IDisposable
{
    /// <summary>Shown in diagnostics, e.g. the DLL path and version.</summary>
    string Description { get; }

    int FanCount { get; }

    int GetRpm(int fan);

    /// <summary>Holds the fan at a fixed duty (1 to 100%).</summary>
    void SetDuty(int fan, int percent);

    /// <summary>Hands the fan back to the firmware's own control.</summary>
    void Release(int fan);

    /// <summary>CPU temperature in C, or null when this backend can't read one.</summary>
    int? ReadCpuTemp();
}

public static class FanBackendExtensions
{
    public static void ReleaseAll(this IFanBackend backend)
    {
        for (int i = 0; i < backend.FanCount; i++) backend.Release(i);
    }
}
