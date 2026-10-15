using System.Diagnostics;
using System.ServiceProcess;

namespace FanControl.Service;

/// <summary>
/// --install-service: creates or updates the FanControl service to run this exe as LocalSystem,
/// starts automatically, restarts on failure. Run elevated (the installer does).
/// </summary>
internal static class ServiceSetup
{
    public const string ServiceName = "FanControl";

    public static int Install()
    {
        string exe = Environment.ProcessPath!;
        string binPath = $"\"{exe}\" --service";

        bool exists = ServiceController.GetServices().Any(s => s.ServiceName == ServiceName);
        int code = exists
            ? Sc("config", ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem")
            : Sc("create", ServiceName, "binPath=", binPath, "start=", "auto", "obj=", "LocalSystem", "DisplayName=", "FanControl");
        if (code != 0) return code;

        Sc("description", ServiceName, "Sets ASUS laptop fan speeds for the FanControl app. Stopping it hands the fans back to ASUS.");
        Sc("failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000");

        using var service = new ServiceController(ServiceName);
        if (service.Status != ServiceControllerStatus.Running)
        {
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
        }
        Log.Write($"Service installed for {exe}");
        return 0;
    }

    static int Sc(params string[] args)
    {
        var info = new ProcessStartInfo("sc.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) Log.Write($"sc {args[0]} failed ({process.ExitCode}): {output.Trim()}");
        return process.ExitCode;
    }
}
