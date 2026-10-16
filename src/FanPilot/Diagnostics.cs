using System.Runtime.InteropServices;
using FanPilot.Control;
using FanPilot.Hardware;
using FanPilot.Service;

namespace FanPilot;

/// <summary>
/// --diag: runs the fan setup measurement from the command line and prints what it finds,
/// without saving it. Must run as SYSTEM with the service stopped. Output also goes to diag.txt
/// in <see cref="Log.ServiceFolder"/>.
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
            Say($"FanPilot {AppInfo.Version} diagnostics, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Say($"Account: {System.Security.Principal.WindowsIdentity.GetCurrent().Name} (fan calls need LocalSystem)");
            var machine = SystemInfo.ReadMachine();
            Say($"Machine: {machine.Manufacturer} {machine.Model}, BIOS {machine.Bios}, ASUS SCI driver {machine.AsusDriver}");

            // Two processes selecting fan indexes at once can write one fan's duty to the other.
            try
            {
                using var service = new System.ServiceProcess.ServiceController(ServiceSetup.ServiceName);
                if (service.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
                {
                    Say($"The {ServiceSetup.ServiceName} service is running. Stop it first: net stop {ServiceSetup.ServiceName}");
                    return Finish(lines, 1);
                }
            }
            catch (InvalidOperationException) { }   // service not installed

            using var backend = AsusWinIoBackend.Open();
            using var acpi = new AcpiThermalZone();
            var temps = new TemperatureReader(backend, new GpuTemperature().Read, acpi.Read);
            var t = temps.Read();
            Say(backend.Description);
            Say($"Fans: {backend.FanCount}");
            Say($"CPU temp: {t.Cpu?.ToString() ?? "n/a"} C ({t.CpuSource ?? "no source"}), GPU temp: {t.Gpu?.ToString() ?? "n/a"} C");
            Say($"RPM: {string.Join(", ", Enumerable.Range(0, backend.FanCount).Select(backend.GetRpm))}");

            var runner = new CalibrationRunner(backend, temps, TimeProvider.System);
            var profile = runner.RunAsync(machine,
                p => Say($"  {p.Percent,3}%  {p.Stage}{(p.DutyPercent is int d && p.Rpms is { } r ? $" {d}% -> {string.Join(" / ", r)} RPM" : "")}  CPU {p.CpuTemp} C"),
                CancellationToken.None).GetAwaiter().GetResult();

            foreach (var f in profile.Fans)
                Say($"Fan {f.Index + 1}: controllable {f.Controllable}, stall {f.StallPercent}%, start {f.StartPercent}%, " +
                    $"min {f.MinPercent}%, ASUS idle {f.AsusIdleRpm} RPM, silent {f.SilentPercent}%");
            Say($"Floor: {profile.Floor}%");
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
            Directory.CreateDirectory(Log.ServiceFolder);
            File.WriteAllLines(Path.Combine(Log.ServiceFolder, "diag.txt"), lines.Select(SystemInfo.Scrub));
        }
        catch (IOException) { }
        return exitCode;
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
