using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using FanPilot.Control;

namespace FanPilot.Service;

/// <summary>Facts about the machine and other fan software, for calibration stamps, warnings and diagnostics.</summary>
internal static partial class SystemInfo
{
    /// <summary>Slow (WMI driver query takes seconds); call once off the startup path.</summary>
    public static MachineInfo ReadMachine()
    {
        try
        {
            string manufacturer = "unknown", model = "unknown", bios = "unknown", driver = "not found";
            foreach (var o in Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem"))
            {
                manufacturer = o["Manufacturer"]?.ToString()?.Trim() ?? manufacturer;
                model = o["Model"]?.ToString()?.Trim() ?? model;
            }
            foreach (var o in Query("SELECT SMBIOSBIOSVersion FROM Win32_BIOS"))
                bios = o["SMBIOSBIOSVersion"]?.ToString()?.Trim() ?? bios;
            foreach (var o in Query("SELECT DeviceName, DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceName LIKE '%System Control Interface%'"))
                driver = o["DriverVersion"]?.ToString()?.Trim() ?? driver;
            return new MachineInfo(manufacturer, model, bios, driver, Environment.OSVersion.VersionString);
        }
        catch (Exception ex)
        {
            Log.Write($"Machine info unavailable: {ex.Message}");
            return MachineInfo.Unknown;
        }
    }

    static IEnumerable<ManagementBaseObject> Query(string wql)
    {
        using var searcher = new ManagementObjectSearcher(wql);
        foreach (var o in searcher.Get()) yield return o;
    }

    /// <summary>Other apps that also write fan speeds and would fight FanPilot over them.</summary>
    static readonly (string Process, string Name)[] FanApps =
    [
        ("GHelper", "G-Helper"),
        ("AsusFanControl", "AsusFanControl"),
        ("AsusFanControlGUI", "AsusFanControl"),
    ];

    public static List<string> RunningFanApps()
    {
        var found = new List<string>();
        foreach (var (process, name) in FanApps)
        {
            var matches = Process.GetProcessesByName(process);
            if (matches.Length > 0 && !found.Contains(name)) found.Add(name);
            foreach (var p in matches) p.Dispose();
        }
        return found;
    }

    /// <summary>Replaces user profile paths so a pasted report doesn't carry account names.</summary>
    public static string Scrub(string text) => UserPath().Replace(text, @"C:\Users\<user>");

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\\\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UserPath();
}
