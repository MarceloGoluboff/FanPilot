using System.Runtime.InteropServices;
using System.ServiceProcess;
using FanControl.Service;
using FanControl.Startup;

namespace FanControl;

public static class Program
{
    /// <summary>Shared with the installer's shortcut so the window groups under the taskbar pin.</summary>
    public const string AppUserModelId = "MarceloGoluboff.FanControl";

    [STAThread]
    public static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);

        if (options.Service)
        {
            Log.UseFile(Log.MachineFolder, "service.log");
            ServiceBase.Run(new FanService());
            return 0;
        }
        if (options.InstallService)
        {
            Log.UseFile(Log.MachineFolder, "setup.log");
            try { return ServiceSetup.Install(); }
            catch (Exception ex) { Log.Write($"Service install failed: {ex}"); return 1; }
        }
        if (options.Diag) return Diagnostics.Run();

        SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

        var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            SingleInstance.SignalExisting();
            return 0;
        }

        var app = new App(options, instance);
        app.InitializeComponent();
        return app.Run();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
