using System.Reflection;

namespace FanPilot;

internal static class AppInfo
{
    /// <summary>Version from Directory.Build.props, without the source-revision suffix the SDK appends.</summary>
    public static string Version { get; } =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}
