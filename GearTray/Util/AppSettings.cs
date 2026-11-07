using GearTray.Devices;
using GearTray.Effects;
using GearTray.Input;

namespace GearTray.Util;

public enum TrayBatterySource { Lowest, Mouse, Headset }

/// <summary>
/// App preferences (%APPDATA%\GearTray\settings.json). Mouse and headset settings live in the devices themselves;
/// keyboard lighting is kept here because the keyboard can't report its current effect.
/// </summary>
public sealed class AppSettings
{
    public KeyboardLightingSettings Keyboard { get; set; } = new();
    public KeyboardFeatureSettings KeyboardFeatures { get; set; } = new();
    public MouseBatteryLightSettings MouseBatteryLight { get; set; } = new();
    public bool LightsOff { get; set; }
    public MouseLightingSettings? MouseLightingBeforeOff { get; set; }
    public TrayBatterySource TrayBattery { get; set; } = TrayBatterySource.Lowest;
    public int LowBatteryPercent { get; set; } = 15;
    public bool LowBatteryNotifications { get; set; } = true;
}

public sealed class KeyboardLightingSettings
{
    /// <summary>False until the user picks lighting in GearTray; until then the keyboard is left alone at startup.</summary>
    public bool Configured { get; set; }
    public KeyboardEffect Effect { get; set; } = KeyboardEffect.Spectrum;
    public string Color { get; set; } = "#FF0000";
    public int Brightness { get; set; } = 100;
    public bool WaveRightToLeft { get; set; }
    public int Speed { get; set; } = 2;
    /// <summary>Ambient: monitor device name (\\.\DISPLAY1); null = primary.</summary>
    public string? AmbientMonitor { get; set; }
    /// <summary>Ambient: true = keys show the picture's layout, false = whole keyboard in the average color.</summary>
    public bool AmbientFollowPicture { get; set; } = true;
    /// <summary>Ambient: extra color saturation in percent.</summary>
    public int AmbientSaturation { get; set; } = 40;

    public KeyboardLighting ToLighting() => new(Effect, ColorUtil.Parse(Color), Brightness, WaveRightToLeft, Speed);
    public AmbientOptions ToAmbient() => new(AmbientMonitor, AmbientFollowPicture, AmbientSaturation);

    public KeyboardLightingSettings With(KeyboardLighting l) => new()
    {
        Configured = true, Effect = l.Effect, Color = ColorUtil.ToHex(l.Color), Brightness = l.Brightness,
        WaveRightToLeft = l.WaveRightToLeft, Speed = l.Speed,
        AmbientMonitor = AmbientMonitor, AmbientFollowPicture = AmbientFollowPicture, AmbientSaturation = AmbientSaturation,
    };
}

/// <summary>Keyboard features that GearTray provides in software (they work with any keyboard).</summary>
public sealed class KeyboardFeatureSettings
{
    /// <summary>Game mode switched on by hand (tray menu or settings).</summary>
    public bool GameModeManual { get; set; }
    /// <summary>Game mode switches on by itself while a game is the program in front.</summary>
    public bool GameModeAuto { get; set; }
    public bool BlockWindowsKey { get; set; } = true;
    public bool BlockAltTab { get; set; }
    public bool BlockAltF4 { get; set; }
    /// <summary>Program names counted as games besides the Steam/Epic/Xbox/GOG library folders.</summary>
    public List<string> ExtraGames { get; set; } = [];
    /// <summary>Program names never counted as games.</summary>
    public List<string> IgnoredApps { get; set; } = [];
    public List<KeyRemap> Remaps { get; set; } = [];
    public bool LightsOffWhenDisplayOff { get; set; } = true;
    /// <summary>Turn keyboard lights off after this many idle minutes; 0 = never.</summary>
    public int LightsOffIdleMinutes { get; set; }
}

/// <summary>Mouse wheel dark normally, lit only when the battery runs low.</summary>
public sealed class MouseBatteryLightSettings
{
    public bool Enabled { get; set; }
    public int Threshold { get; set; } = 20;
    public string Color { get; set; } = "#FF0000";
}

public sealed class MouseLightingSettings
{
    public MouseLightMode Mode { get; set; }
    public string Color { get; set; } = "#000000";
    public int Brightness { get; set; }

    public MouseLighting ToLighting() => new(Mode, ColorUtil.Parse(Color), Brightness);
    public static MouseLightingSettings From(MouseLighting l) => new() { Mode = l.Mode, Color = ColorUtil.ToHex(l.Color), Brightness = l.Brightness };
}

public static class ColorUtil
{
    /// <summary>Plain ARGB color (never a "known" named color), so equality checks are reliable.</summary>
    public static Color Plain(Color c) => System.Drawing.Color.FromArgb(255, c.R, c.G, c.B);
    public static Color Parse(string hex) => Plain(ColorTranslator.FromHtml(hex));
    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
