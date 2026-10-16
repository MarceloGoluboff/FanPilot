using System.Diagnostics;

namespace FanPilot.Hardware;

/// <summary>
/// Fans through ASUS's AsusWinIO64.dll, the library MyASUS Fan Diagnosis uses. Every call runs on
/// one dedicated thread (see <see cref="DllThread"/>), which also keeps each "select fan index, then
/// act" sequence together.
/// </summary>
public sealed class AsusWinIoBackend : IFanBackend
{
    /// <summary>
    /// Duty a fan is parked at before release. After release a fan holds its last test duty until
    /// the EC's next update (several seconds); releasing from a low duty can leave it stopped.
    /// </summary>
    const int ParkPercent = 40;

    readonly DllThread _dll;
    bool _disposed;

    public string Description { get; }
    public int FanCount { get; }

    AsusWinIoBackend(DllThread dll, string dllPath, int fanCount)
    {
        _dll = dll;
        Description = $"AsusWinIO64 {FileVersionInfo.GetVersionInfo(dllPath).FileVersion} ({dllPath})";
        FanCount = fanCount;
    }

    public static AsusWinIoBackend Open()
    {
        var path = DllLocator.Find() ?? throw new HardwareUnavailableException(
            "AsusWinIO64.dll was not found. Install MyASUS so the ASUS System Control Interface driver is present.");

        var dll = new DllThread("AsusWinIO");
        try
        {
            int count = dll.Invoke(() =>
            {
                AsusWinIo.Load(path);
                AsusWinIo.InitializeWinIo();
                int n = AsusWinIo.HealthyTable_FanCounts();
                if (n is <= 0 or > 8) AsusWinIo.ShutdownWinIo();
                return n;
            });
            if (count is <= 0 or > 8)
            {
                throw new HardwareUnavailableException(count == -1
                    ? "The ASUS driver refused access (-1). FanPilot's service must run as LocalSystem."
                    : $"The ASUS driver reported {count} fans. Check that MyASUS Fan Diagnosis works on this laptop.");
            }

            Log.Write($"Opened {path}, {count} fan(s)");
            return new AsusWinIoBackend(dll, path, count);
        }
        catch
        {
            dll.Dispose();
            throw;
        }
    }

    public int GetRpm(int fan) => Call(() =>
    {
        AsusWinIo.HealthyTable_SetFanIndex((byte)fan);
        return AsusWinIo.HealthyTable_FanRPM();
    });

    public int? ReadCpuTemp() => Call(() =>
    {
        ulong t = AsusWinIo.Thermal_Read_Cpu_Temperature();
        // Some models report 0 here; treat anything implausible as "no reading".
        return t is > 0 and < 150 ? (int?)(int)t : null;
    });

    /// <summary>Puts the fan in test mode at a fixed duty. 100% maps to PWM 255.</summary>
    public void SetDuty(int fan, int percent) => Call(() =>
    {
        Write(fan, percent);
        return 0;
    });

    public void Release(int fan)
    {
        if (_disposed) return;
        try
        {
            Call(() =>
            {
                Write(fan, ParkPercent);
                AsusWinIo.HealthyTable_SetFanTestMode((char)0);
                return 0;
            });
        }
        catch (ObjectDisposedException) { }
    }

    static void Write(int fan, int percent)
    {
        short duty = (short)Math.Round(Math.Clamp(percent, 1, 100) * 255 / 100.0);
        AsusWinIo.HealthyTable_SetFanIndex((byte)fan);
        AsusWinIo.HealthyTable_SetFanTestMode((char)1);
        AsusWinIo.HealthyTable_SetFanPwmDuty(duty);
    }

    T Call<T>(Func<T> func)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _dll.Invoke(func);
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { _dll.Invoke(AsusWinIo.ShutdownWinIo); }
        catch (ObjectDisposedException) { }
        _disposed = true;
        _dll.Dispose();
    }
}
