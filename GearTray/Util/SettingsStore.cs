using System.Text.Json;
using System.Text.Json.Serialization;

namespace GearTray.Util;

/// <summary>Loads and saves a settings object as JSON next to the log (see Log.Directory for where that is).</summary>
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
            if (ImportFromUserFolder<T>() is { } imported) return imported;
        }
        catch (Exception ex) { Log.Error("Settings file unreadable, using defaults", ex); }
        return new T();
    }

    /// <summary>
    /// First start with admin rights since 0.7.0: carry over the settings kept in %APPDATA%\GearTray until then.
    /// They're read into the settings type and written out again, so only GearTray's own settings come along.
    /// </summary>
    private static T? ImportFromUserFolder<T>() where T : new()
    {
        string old = Path.Combine(Log.UserDirectory, "settings.json");
        if (string.Equals(Log.Directory, Log.UserDirectory, StringComparison.OrdinalIgnoreCase) || !File.Exists(old)) return default;
        if (new FileInfo(old).Length > 1_000_000) { Log.Warn($"Not importing {old}: too big for a settings file"); return default; }
        var settings = JsonSerializer.Deserialize<T>(File.ReadAllText(old), Options);
        if (settings is null) return default;
        Save(settings);
        Log.Info($"Settings imported from {old}");
        return settings;
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
