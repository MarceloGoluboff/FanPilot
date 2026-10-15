using System.ServiceProcess;

namespace FanControl.Service;

/// <summary>
/// Windows service, LocalSystem. ASUS System Control Interface 3.1.41.0 and later only accept
/// fan calls from LocalSystem, so this is the only process that touches the hardware.
/// </summary>
internal sealed class FanService : ServiceBase
{
    FanHost? _host;

    public FanService()
    {
        ServiceName = ServiceSetup.ServiceName;
        CanStop = true;
        CanShutdown = true;
        CanHandlePowerEvent = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        _host = new FanHost();
        _host.Start();
    }

    protected override void OnStop() => _host?.Stop();

    protected override void OnShutdown() => _host?.Stop();

    protected override bool OnPowerEvent(PowerBroadcastStatus status)
    {
        if (status is PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeAutomatic) _host?.OnResume();
        return true;
    }
}
