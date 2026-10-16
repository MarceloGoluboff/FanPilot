using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanPilot.Settings;

public enum AppTheme { System, Dark, Light }

/// <summary>Window preferences for this user, in %AppData%\FanPilot\settings.json. Fan settings live in the service.</summary>
public sealed class AppSettings
{
    public bool CloseToTray { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Show the tray icon at sign-in (the installer's entry is for every user; this opts one out).</summary>
    public bool TrayAtSignIn { get; set; } = true;

    /// <summary>The user chose "Not now" on the first-launch fan setup prompt.</summary>
    public bool FanSetupPromptDismissed { get; set; }

    static string FilePath => Path.Combine(Log.UserFolder, "settings.json");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Write($"Settings unreadable, using defaults: {ex.Message}");
        }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.UserFolder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Settings save failed: {ex.Message}");
        }
    }
}
