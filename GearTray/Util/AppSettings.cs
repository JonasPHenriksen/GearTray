using GearTray.Devices;
using GearTray.Effects;
using GearTray.Input;

namespace GearTray.Util;

public enum TrayBatterySource { Lowest, Mouse, Headset, Controller }

/// <summary>
/// App preferences (%APPDATA%\GearTray\settings.json). Mouse and headset settings live in the devices themselves;
/// keyboard lighting is kept here because the keyboard can't report its current effect.
/// </summary>
public sealed class AppSettings
{
    public KeyboardLightingSettings Keyboard { get; set; } = new();
    public KeyboardFeatureSettings KeyboardFeatures { get; set; } = new();
    public MouseBatteryLightSettings MouseBatteryLight { get; set; } = new();
    public ControllerSettings Controller { get; set; } = new();
    public HeadsetScreenSettings HeadsetScreen { get; set; } = new();
    public List<ProgramProfile> ProgramProfiles { get; set; } = [];
    public bool LightsOff { get; set; }
    public MouseLightingSettings? MouseLightingBeforeOff { get; set; }
    public TrayBatterySource TrayBattery { get; set; } = TrayBatterySource.Lowest;
    public int LowBatteryPercent { get; set; } = 15;
    public bool LowBatteryNotifications { get; set; } = true;
    /// <summary>Settings blocks (e.g. "keyboard.macros") and whole tabs ("tab.controller") hidden by the user.</summary>
    public List<string> HiddenSections { get; set; } = [];

    public bool IsHidden(string id) => HiddenSections.Contains(id, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Arctis base station screen.</summary>
public sealed class HeadsetScreenSettings
{
    /// <summary>Show GearTray's dashboard (clock + batteries) instead of the base station's own screen.</summary>
    public bool Dashboard { get; set; }
    /// <summary>Add a "now playing" page while music or video plays.</summary>
    public bool ShowNowPlaying { get; set; } = true;
    /// <summary>Add a PC stats page while a game runs.</summary>
    public bool ShowPcStats { get; set; } = true;
    /// <summary>Short pop-ups for game mode, program profiles and the controller connecting.</summary>
    public bool ShowPopups { get; set; } = true;
    /// <summary>How long each page stays up before the next one, in seconds (when more than one page applies).</summary>
    public int ClockSeconds { get; set; } = 8;
    public int NowPlayingSeconds { get; set; } = 6;
    public int PcStatsSeconds { get; set; } = 8;
    /// <summary>The dashboard was showing when GearTray last ran (so the screen gets handed back at startup if needed).</summary>
    public bool WasShowing { get; set; }
}

/// <summary>Xbox controller preferences.</summary>
public sealed class ControllerSettings
{
    public bool LowBatteryNotification { get; set; } = true;
    /// <summary>Rumble strength for the test buttons, 0–100.</summary>
    public int TestStrength { get; set; } = 60;
    /// <summary>Set the Xbox button light whenever a controller connects. Off: Windows' own light is left alone.</summary>
    public bool LightCustom { get; set; }
    public XboxLedMode LightMode { get; set; } = XboxLedMode.On;
    /// <summary>0–47 (XboxGip.MaxBrightness); Windows uses 20.</summary>
    public int LightBrightness { get; set; } = XboxGip.DefaultBrightness;
}

/// <summary>
/// While one of Programs is the program in front, these settings apply; when it leaves, the normal ones come back.
/// Any part left null is not changed.
/// </summary>
public sealed class ProgramProfile
{
    public string Name { get; set; } = "Profile";
    public bool Enabled { get; set; } = true;
    public List<string> Programs { get; set; } = [];
    public KeyboardLightingSettings? KeyboardLighting { get; set; }
    public bool? GameMode { get; set; }
    /// <summary>Mouse onboard profile (1-based) to switch to.</summary>
    public int? MouseProfile { get; set; }
    /// <summary>Headset EQ preset id to switch to.</summary>
    public int? HeadsetEqPreset { get; set; }

    public override string ToString()
    {
        var parts = new List<string>();
        if (KeyboardLighting is not null) parts.Add($"keyboard {KeyboardLighting.Effect}");
        if (GameMode is bool g) parts.Add(g ? "game mode on" : "game mode off");
        if (MouseProfile is int p) parts.Add($"mouse profile {p}");
        if (HeadsetEqPreset is int e) parts.Add($"EQ {e}");
        return $"{(Enabled ? "" : "(off) ")}{Name}: {string.Join(", ", Programs)} → {(parts.Count == 0 ? "nothing" : string.Join(", ", parts))}";
    }
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
    /// <summary>Per-key colors for the Custom effect: 9 rows × 22 columns as "RRGGBB" strings (row-major); empty = all off.</summary>
    public List<string> CustomColors { get; set; } = [];
    /// <summary>Ripple: the background color the waves travel over.</summary>
    public string Color2 { get; set; } = "#000000";

    public KeyboardLighting ToLighting() => new(Effect, ColorUtil.Parse(Color), Brightness, WaveRightToLeft, Speed);
    public AmbientOptions ToAmbient() => new(AmbientMonitor, AmbientFollowPicture, AmbientSaturation);

    /// <summary>The Custom layout as frame bytes (rows × cols × RGB).</summary>
    public byte[] CustomFrame()
    {
        var frame = new byte[RazerHuntsmanElite.MatrixRows * RazerHuntsmanElite.MatrixCols * 3];
        for (int i = 0; i < CustomColors.Count && i * 3 + 2 < frame.Length; i++)
        {
            var c = ColorUtil.Parse("#" + CustomColors[i].TrimStart('#'));
            frame[i * 3] = c.R; frame[i * 3 + 1] = c.G; frame[i * 3 + 2] = c.B;
        }
        return frame;
    }

    public KeyboardLightingSettings With(KeyboardLighting l) => new()
    {
        Configured = true, Effect = l.Effect, Color = ColorUtil.ToHex(l.Color), Brightness = l.Brightness,
        WaveRightToLeft = l.WaveRightToLeft, Speed = l.Speed,
        AmbientMonitor = AmbientMonitor, AmbientFollowPicture = AmbientFollowPicture, AmbientSaturation = AmbientSaturation,
        CustomColors = [.. CustomColors], Color2 = Color2,
    };

    public KeyboardLightingSettings Clone()
    {
        var c = With(ToLighting());
        c.Configured = Configured;
        return c;
    }
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
    public List<Macro> Macros { get; set; } = [];
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
