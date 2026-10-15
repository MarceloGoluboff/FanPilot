using System.Runtime.InteropServices;
using FanControl.Hardware;

namespace FanControl;

/// <summary>
/// --diag: checks the hardware path end to end. Prints fan count, RPM and temp, holds 70% for
/// 5 s (with --sweep, steps down to find the stall point), then hands control back to ASUS.
/// Must run as SYSTEM with the service stopped. Output also goes to diag.txt in <see cref="Log.ServiceFolder"/>.
/// </summary>
internal static class Diagnostics
{
    public static int Run(bool sweep)
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

            // Two processes selecting fan indexes at once can write one fan's duty to the other.
            try
            {
                using var service = new System.ServiceProcess.ServiceController(Service.ServiceSetup.ServiceName);
                if (service.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                {
                    Say("The FanControl service is running. Stop it first: net stop FanControl");
                    return Finish(lines, 1);
                }
            }
            catch (InvalidOperationException) { }   // service not installed

            using var hw = FanController.Open();
            Say($"Fans: {hw.FanCount}");
            Say($"CPU temp: {hw.GetCpuTemp()?.ToString() ?? "n/a"} C");
            Say($"RPM: {string.Join(", ", hw.GetRpms())}");

            bool restored = false;
            try
            {
                Say("Setting all fans to 70% for 5 s");
                hw.SetDutyAll(70);
                for (int i = 1; i <= 5; i++)
                {
                    Thread.Sleep(1000);
                    Say($"  +{i}s  RPM: {string.Join(", ", hw.GetRpms())}  temp: {hw.GetCpuTemp()} C");
                }

                if (sweep)
                {
                    // Steps down to find where the fans stall. Holds each duty 6 s so the RPM settles.
                    Say("Sweep: duty -> RPM after 6 s");
                    foreach (int duty in new[] { 60, 50, 40, 35, 30, 25, 20, 15, 10, 5 })
                    {
                        if (hw.GetCpuTemp() is > 80)
                        {
                            Say("  stopping sweep, CPU above 80 C");
                            break;
                        }
                        hw.SetDutyAll(duty);
                        Thread.Sleep(6000);
                        Say($"  {duty,3}%  RPM: {string.Join(", ", hw.GetRpms())}  temp: {hw.GetCpuTemp()} C");
                    }
                }

                Say("Restoring ASUS control");
                hw.RestoreAsusControl();
                restored = true;
                for (int i = 1; i <= 5; i++)
                {
                    Thread.Sleep(1000);
                    Say($"  +{i}s  RPM: {string.Join(", ", hw.GetRpms())}  temp: {hw.GetCpuTemp()} C");
                }
            }
            finally
            {
                // Never leave the fans parked at a test duty.
                if (!restored) hw.RestoreAsusControl();
            }
            Say("Done");
        }
        catch (Exception ex)
        {
            Say($"FAILED: {ex}");
            exitCode = 1;
        }
        return Finish(lines, exitCode);
    }

    static int Finish(List<string> lines, int exitCode)
    {
        try
        {
            // Runs as SYSTEM, so this is the service's data folder.
            Directory.CreateDirectory(Log.ServiceFolder);
            File.WriteAllLines(Path.Combine(Log.ServiceFolder, "diag.txt"), lines);
        }
        catch (IOException) { }
        return exitCode;
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
