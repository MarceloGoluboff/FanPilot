using System.Diagnostics;

namespace FanControl.Hardware;

/// <summary>
/// Finds AsusWinIO64.dll. ASUS licenses the DLL, so we don't ship it; the ASUS System Control
/// Interface driver package puts a copy in the driver store.
/// </summary>
internal static class DllLocator
{
    public static string? Find()
    {
        // A copy next to the exe wins, which is handy for testing a specific version.
        var local = Path.Combine(AppContext.BaseDirectory, AsusWinIo.DllName);
        if (File.Exists(local)) return local;

        var repository = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "DriverStore", "FileRepository");
        try
        {
            return Directory.EnumerateDirectories(repository, "asussci*.inf_amd64_*")
                .Select(dir => Path.Combine(dir, "ASUSSystemAnalysis", AsusWinIo.DllName))
                .Where(path => File.Exists(path))
                .OrderByDescending(FileVersion)
                .ThenByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Driver store search failed: {ex.Message}");
            return null;
        }
    }

    static Version FileVersion(string path) =>
        Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out var v) ? v : new Version(0, 0);
}
