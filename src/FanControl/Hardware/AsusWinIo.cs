using System.Reflection;
using System.Runtime.InteropServices;

namespace FanControl.Hardware;

/// <summary>
/// P/Invoke surface of ASUS's AsusWinIO64.dll, the library MyASUS uses for Fan Diagnosis.
/// Same entry points as Karmel0x/AsusFanControl.
/// </summary>
internal static class AsusWinIo
{
    public const string DllName = "AsusWinIO64.dll";

    static IntPtr _handle;

    [DllImport(DllName)] public static extern void InitializeWinIo();
    [DllImport(DllName)] public static extern void ShutdownWinIo();
    [DllImport(DllName)] public static extern int HealthyTable_FanCounts();
    [DllImport(DllName)] public static extern void HealthyTable_SetFanIndex(byte index);
    [DllImport(DllName)] public static extern int HealthyTable_FanRPM();
    [DllImport(DllName)] public static extern void HealthyTable_SetFanTestMode(char mode);
    [DllImport(DllName)] public static extern void HealthyTable_SetFanPwmDuty(short duty);
    [DllImport(DllName)] public static extern ulong Thermal_Read_Cpu_Temperature();

    /// <summary>Loads the DLL from an absolute path and routes the imports above to it.</summary>
    public static void Load(string path)
    {
        if (_handle != IntPtr.Zero) return;

        // The DLL may load siblings from its own folder.
        SetDllDirectory(Path.GetDirectoryName(path));
        _handle = NativeLibrary.Load(path);
        NativeLibrary.SetDllImportResolver(typeof(AsusWinIo).Assembly, Resolve);
    }

    static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        string.Equals(name, DllName, StringComparison.OrdinalIgnoreCase) ? _handle : IntPtr.Zero;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetDllDirectory(string? path);
}
