namespace FanControl;

/// <summary>
/// Small append-only log, trimmed past 512 KB. The window logs to %AppData%\FanControl, the
/// service to <see cref="ServiceFolder"/>.
/// </summary>
internal static class Log
{
    static readonly object Gate = new();

    public static string UserFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FanControl");

    /// <summary>
    /// Local app data of the account running this process. For the service (LocalSystem) that is
    /// C:\Windows\System32\config\systemprofile\AppData\Local\FanControl, which standard users
    /// can't write. %ProgramData% would let them plant links for SYSTEM to follow.
    /// </summary>
    public static string ServiceFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanControl");

    // Declared after the folders: static initializers run in source order.
    static string _file = Path.Combine(UserFolder, "log.txt");

    public static void UseFile(string folder, string name) => _file = Path.Combine(folder, name);

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                var info = new FileInfo(_file);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                File.AppendAllText(_file,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
