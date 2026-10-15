namespace FanControl.Hardware;

public sealed class HardwareUnavailableException(string message) : Exception(message);

/// <summary>
/// Thread-safe wrapper over AsusWinIO64. Fan reads and writes select a fan index first, so every
/// call holds one lock to keep index and operation together.
/// </summary>
public sealed class FanController : IDisposable
{
    readonly object _gate = new();
    bool _disposed;

    public string DllPath { get; }
    public int FanCount { get; }

    FanController(string dllPath, int fanCount)
    {
        DllPath = dllPath;
        FanCount = fanCount;
    }

    public static FanController Open()
    {
        var path = DllLocator.Find() ?? throw new HardwareUnavailableException(
            "AsusWinIO64.dll was not found. Install MyASUS so the ASUS System Control Interface driver is present.");

        AsusWinIo.Load(path);
        AsusWinIo.InitializeWinIo();

        int count = AsusWinIo.HealthyTable_FanCounts();
        if (count is <= 0 or > 8)
        {
            AsusWinIo.ShutdownWinIo();
            throw new HardwareUnavailableException(
                $"The ASUS driver reported {count} fans. Check that the ASUS System Analysis service is running.");
        }

        Log.Write($"Opened {path}, {count} fan(s)");
        return new FanController(path, count);
    }

    public int GetRpm(int fan)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            AsusWinIo.HealthyTable_SetFanIndex((byte)fan);
            return AsusWinIo.HealthyTable_FanRPM();
        }
    }

    public int[] GetRpms()
    {
        var rpms = new int[FanCount];
        for (int i = 0; i < FanCount; i++) rpms[i] = GetRpm(i);
        return rpms;
    }

    /// <summary>CPU package temperature in C, or null when the reading is implausible.</summary>
    public int? GetCpuTemp()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ulong t = AsusWinIo.Thermal_Read_Cpu_Temperature();
            return t is > 0 and < 150 ? (int)t : null;
        }
    }

    /// <summary>Puts the fan in test mode at a fixed duty. 100% maps to PWM 255.</summary>
    public void SetDuty(int fan, int percent)
    {
        short duty = (short)Math.Round(Math.Clamp(percent, 1, 100) * 255 / 100.0);
        lock (_gate)
        {
            ThrowIfDisposed();
            AsusWinIo.HealthyTable_SetFanIndex((byte)fan);
            AsusWinIo.HealthyTable_SetFanTestMode((char)1);
            AsusWinIo.HealthyTable_SetFanPwmDuty(duty);
        }
    }

    public void SetDutyAll(int percent)
    {
        for (int i = 0; i < FanCount; i++)
        {
            if (i > 0) Thread.Sleep(20);
            SetDuty(i, percent);
        }
    }

    /// <summary>
    /// Leaves test mode so the firmware controls the fans again. After release a fan holds its last
    /// test duty until the EC's next update (several seconds), so it is parked at a moderate duty
    /// first; releasing from a low duty left both fans stopped at 0 RPM in testing.
    /// </summary>
    public void RestoreAsusControl()
    {
        const short parkDuty = 102;   // 40%
        for (int i = 0; i < FanCount; i++)
        {
            lock (_gate)
            {
                if (_disposed) return;
                AsusWinIo.HealthyTable_SetFanIndex((byte)i);
                AsusWinIo.HealthyTable_SetFanTestMode((char)1);
                AsusWinIo.HealthyTable_SetFanPwmDuty(parkDuty);
                AsusWinIo.HealthyTable_SetFanTestMode((char)0);
            }
        }
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            AsusWinIo.ShutdownWinIo();
        }
    }
}
