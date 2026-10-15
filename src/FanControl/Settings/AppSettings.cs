using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanControl.Settings;

public enum AppTheme { System, Dark, Light }

/// <summary>Window preferences for this user, in %AppData%\FanControl\settings.json. Fan settings live in the service.</summary>
public sealed class AppSettings
{
    public bool CloseToTray { get; set; } = true;
    public AppTheme Theme { get; set; } = AppTheme.System;

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
