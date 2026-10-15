namespace FanControl.Startup;

public sealed record LaunchOptions(
    bool Tray,
    bool Service,
    bool Diag,
    bool InstallService)
{
    public static LaunchOptions Parse(string[] args)
    {
        bool Has(string flag) => args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
        return new LaunchOptions(
            Tray: Has("--tray"),
            Service: Has("--service"),
            Diag: Has("--diag"),
            InstallService: Has("--install-service"));
    }
}
