using System.Runtime.InteropServices;
using System.ServiceProcess;
using FanPilot.Hardware;
using FanPilot.Service;
using FanPilot.Settings;
using FanPilot.Startup;

namespace FanPilot;

public static class Program
{
    /// <summary>Shared with the installer's shortcut so the window groups under the taskbar pin.</summary>
    public const string AppUserModelId = "MarceloGoluboff.FanPilot";

    [STAThread]
    public static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);

        if (options.Service)
        {
            Log.UseFile(Log.ServiceFolder, "service.log");
            ServiceBase.Run(new FanService());
            return 0;
        }
        if (options.Host) return RunHost(options.SimulatedFans);
        if (options.InstallService)
        {
            Log.UseFile(Log.ServiceFolder, "setup.log");
            try { return ServiceSetup.Install(); }
            catch (Exception ex) { Log.Write($"Service install failed: {ex}"); return 1; }
        }
        if (options.Diag) return Diagnostics.Run();

        var settings = AppSettings.Load();
        // The installer's sign-in entry is for every user; each user can opt out in Settings.
        if (options.Tray && !settings.TrayAtSignIn) return 0;

        SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            SingleInstance.SignalExisting();
            return 0;
        }

        var app = new App(options, settings, instance);
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// --host runs the service's work in the foreground, for development. With --simulate[=N] it
    /// drives pretend fans, so the window can be worked on without ASUS hardware. Stop the real
    /// service first: both use the same pipe. Runs until Ctrl+C or the process is ended.
    /// </summary>
    static int RunHost(int? simulatedFans)
    {
        AttachConsole(-1);
        string folder = simulatedFans is null ? Log.ServiceFolder : Path.Combine(Log.ServiceFolder, "Simulator");
        Log.UseFile(folder, "host.log");

        SimulatedBackend? simulated = simulatedFans is int n ? new SimulatedBackend(n) : null;
        Func<IFanBackend> open = simulated is null ? AsusWinIoBackend.Open : () => simulated;
        Func<int?> gpu = simulated is null ? new GpuTemperature().Read : simulated.ReadGpuTemp;

        using var host = new FanHost(folder, open, gpu, acpiFallback: simulated is null);
        using var exit = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exit.Set();
        };
        host.Start();
        Console.WriteLine($"FanPilot host running ({(simulated is null ? "real fans" : $"{simulated.FanCount} simulated fans")}). Ctrl+C to stop.");
        exit.Wait();
        return 0;
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
