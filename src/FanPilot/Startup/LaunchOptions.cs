namespace FanPilot.Startup;

public sealed record LaunchOptions(
    bool Tray,
    bool Service,
    bool Host,
    int? SimulatedFans,
    bool Diag,
    bool InstallService)
{
    public static LaunchOptions Parse(string[] args)
    {
        bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

        // --simulate or --simulate=N (1 to 3 fans, default 2).
        int? simulated = null;
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--simulate", StringComparison.OrdinalIgnoreCase)) simulated = 2;
            else if (arg.StartsWith("--simulate=", StringComparison.OrdinalIgnoreCase) && int.TryParse(arg[11..], out int n))
                simulated = Math.Clamp(n, 1, 3);
        }

        return new LaunchOptions(
            Tray: Has("--tray"),
            Service: Has("--service"),
            Host: Has("--host"),
            SimulatedFans: simulated,
            Diag: Has("--diag"),
            InstallService: Has("--install-service"));
    }
}
