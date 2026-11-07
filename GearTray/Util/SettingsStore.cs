using System.Text.Json;
using System.Text.Json.Serialization;

namespace GearTray.Util;

/// <summary>Loads and saves a settings object as JSON next to the log in %APPDATA%\GearTray.</summary>
public static class SettingsStore
{
    public static string FilePath { get; } = Path.Combine(Log.Directory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static T Load<T>() where T : new()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(FilePath), Options) ?? new T();
        }
        catch (Exception ex) { Log.Error("Settings file unreadable, using defaults", ex); }
        return new T();
    }

    public static void Save<T>(T settings)
    {
        try
        {
            Directory.CreateDirectory(Log.Directory);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Error("Could not save settings", ex); }
    }
}
