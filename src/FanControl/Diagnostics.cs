using System.Runtime.InteropServices;
using FanControl.Hardware;

namespace FanControl;

/// <summary>
/// --diag: checks the hardware path end to end. Prints fan count, RPM and temp, holds 70% for
/// 5 s, then hands control back to ASUS. Output also goes to %AppData%\FanControl\diag.txt.
/// </summary>
internal static class Diagnostics
{
    public static int Run()
    {
        AttachConsole(-1);
        var lines = new List<string>();
        void Say(string line)
        {
            lines.Add(line);
            Console.WriteLine(line);
        }

        int exitCode = 0;
        try
        {
            Say($"FanControl diagnostics, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Say($"Account: {System.Security.Principal.WindowsIdentity.GetCurrent().Name} (fan calls need LocalSystem)");
            Say($"DLL: {DllLocator.Find() ?? "not found"}");

            using var hw = FanController.Open();
            Say($"Fans: {hw.FanCount}");
            Say($"CPU temp: {hw.GetCpuTemp()?.ToString() ?? "n/a"} C");
            Say($"RPM: {string.Join(", ", hw.GetRpms())}");

            Say("Setting all fans to 70% for 5 s");
            hw.SetDutyAll(70);
            for (int i = 1; i <= 5; i++)
            {
                Thread.Sleep(1000);
                Say($"  +{i}s  RPM: {string.Join(", ", hw.GetRpms())}  temp: {hw.GetCpuTemp()} C");
            }

            Say("Restoring ASUS control");
            hw.RestoreAsusControl();
            for (int i = 1; i <= 5; i++)
            {
                Thread.Sleep(1000);
                Say($"  +{i}s  RPM: {string.Join(", ", hw.GetRpms())}  temp: {hw.GetCpuTemp()} C");
            }
            Say("Done");
        }
        catch (Exception ex)
        {
            Say($"FAILED: {ex}");
            exitCode = 1;
        }

        try
        {
            // ProgramData, not AppData: this usually runs as SYSTEM.
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FanControl");
            Directory.CreateDirectory(folder);
            File.WriteAllLines(Path.Combine(folder, "diag.txt"), lines);
        }
        catch (IOException) { }
        return exitCode;
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
