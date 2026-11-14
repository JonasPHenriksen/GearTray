using GearTray.Hid;
using GearTray.Util;

namespace GearTray.Devices;

public enum MouseLedZone : byte { Logo = 0, Wheel = 1, Underglow = 2 }

/// <summary>Values are the mouse's own mode ids (G-Helper: "true off is actually 0xF0").</summary>
public enum MouseLightMode : byte { Static = 0, Breathing = 1, ColorCycle = 2, Rainbow = 3, Reactive = 4, Battery = 6, Off = 0xF0 }

/// <summary>What a mouse button does: a mouse function (type 1) or a key / media key (type 0), per G-Helper / libratbag.</summary>
public sealed record MouseAction(string Name, byte Code, byte Type)
{
    public override string ToString() => Name;

    public static readonly MouseAction[] All = BuildAll();

    private static MouseAction[] BuildAll()
    {
        var list = new List<MouseAction>
        {
            new("Left click", 0xF0, 1), new("Right click", 0xF1, 1), new("Middle click (wheel)", 0xF2, 1), new("Double click", 0xE3, 1),
            new("Back", 0xE4, 1), new("Forward", 0xE5, 1), new("DPI switch", 0xE6, 1), new("DPI target (hold for sniper DPI)", 0xE7, 1),
            new("Scroll up", 0xE8, 1), new("Scroll down", 0xE9, 1),
            new("Play / pause", 0xF0, 0), new("Stop", 0xF1, 0), new("Next track", 0xF2, 0), new("Previous track", 0xF3, 0),
            new("Mute", 0xF4, 0), new("Volume down", 0xF5, 0), new("Volume up", 0xF6, 0),
        };
        for (int i = 0; i < 26; i++) list.Add(new($"Key {(char)('A' + i)}", (byte)(0x04 + i), 0));
        string[] digits = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0"];
        for (int i = 0; i < 10; i++) list.Add(new($"Key {digits[i]}", (byte)(0x1E + i), 0));
        list.AddRange([new("Key Enter", 0x28, 0), new("Key Esc", 0x29, 0), new("Key Backspace", 0x2A, 0), new("Key Tab", 0x2B, 0), new("Key Space", 0x2C, 0)]);
        for (int i = 0; i < 12; i++) list.Add(new($"Key F{i + 1}", (byte)(0x3A + i), 0));
        for (int i = 0; i < 12; i++) list.Add(new($"Key F{i + 13} (use as a macro trigger)", (byte)(0x68 + i), 0));
        list.AddRange([
            new("Key Print Screen", 0x46, 0), new("Key Insert", 0x49, 0), new("Key Home", 0x4A, 0), new("Key Page Up", 0x4B, 0),
            new("Key Delete", 0x4C, 0), new("Key End", 0x4D, 0), new("Key Page Down", 0x4E, 0),
            new("Key Right", 0x4F, 0), new("Key Left", 0x50, 0), new("Key Down", 0x51, 0), new("Key Up", 0x52, 0),
            new("Key Left Ctrl", 0xE0, 0), new("Key Left Shift", 0xE1, 0), new("Key Left Alt", 0xE2, 0), new("Key Left Windows", 0xE3, 0),
        ]);
        return [.. list];
    }

    public static MouseAction Find(byte code, byte type) =>
        All.FirstOrDefault(a => a.Code == code && a.Type == type) ?? new MouseAction($"Other ({type}:{code:X2})", code, type);
}

/// <summary>A physical button and the mouse function it does by default.</summary>
public sealed record MouseButtonSlot(int Index, string Name, byte SourceCode);

/// <summary>Brightness 0–100. Color is used by Static, Breathing and Reactive.</summary>
public sealed record MouseLighting(MouseLightMode Mode, Color Color, int Brightness)
{
    public static readonly MouseLightMode[] UsesColor = [MouseLightMode.Static, MouseLightMode.Breathing, MouseLightMode.Reactive];
}

public sealed record RogMouseModel(string Name, int MinDpi, int MaxDpi, MouseLedZone[] Zones, bool Experimental = false);

/// <summary>Settings stored in the mouse itself (current onboard profile).</summary>
public sealed class MouseSettings
{
    public int ProfileRaw { get; set; }
    public int ActiveStage { get; set; }
    public int[] Dpi { get; set; } = new int[4];
    public Color[] DpiColors { get; set; } = new Color[4];
    public int PollingHz { get; set; }
    public int DebounceMs { get; set; }
    public bool AngleSnapping { get; set; }
    public bool LiftOffHigh { get; set; }
    /// <summary>Minutes of inactivity before the mouse sleeps; 0 = never.</summary>
    public int SleepMinutes { get; set; }
    public int LowBatteryWarning { get; set; }
    /// <summary>0–9.</summary>
    public int Acceleration { get; set; }
    /// <summary>0–9.</summary>
    public int Deceleration { get; set; }
    /// <summary>Degrees, −30…+30.</summary>
    public int AngleTuning { get; set; }

    public MouseSettings Clone()
    {
        var c = (MouseSettings)MemberwiseClone();
        c.Dpi = (int[])Dpi.Clone();
        c.DpiColors = (Color[])DpiColors.Clone();
        return c;
    }
}

public sealed class MouseAsleepException() : Exception("The mouse is asleep or out of range. Move it and try again.");

/// <summary>
/// ASUS ROG wireless mouse behind a ROG Omni receiver (0B05:1ACE), or plugged in by its cable (e.g. while charging).
/// Protocol per G-Helper and libratbag: 64-byte output reports with report ID 0x03 on usage page 0xFF01 (receiver:
/// interface 2 / collection 3; cable: interface 0); replies echo the command. Through the receiver the paired mouse
/// model is read from collection 1 (usage page 0xFF02, report ID 0x01, command A0); by cable the product ID names it.
/// Only commands those projects use are sent.
/// </summary>
public sealed class RogOmniMouse : GearDevice, IHasBattery
{
    public static readonly int[] PollingTable = [125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    public static readonly int[] DebounceTable = [4, 8, 12, 16, 20, 24, 28, 32];
    public static readonly (byte Code, int Minutes)[] SleepTable = [(0, 1), (1, 2), (2, 3), (3, 5), (4, 10), (0xFF, 0)];

    private static readonly Dictionary<int, RogMouseModel> Models = new()
    {
        [0x1A94] = new("ROG Harpe Ace Aim Lab Edition", 50, 36000, [MouseLedZone.Wheel]),
        [0x1B68] = new("ROG Harpe Ace Extreme", 100, 42000, [MouseLedZone.Wheel]),
        [0x1B69] = new("ROG Harpe Ace Extreme", 100, 42000, [MouseLedZone.Wheel]),
        [0x1B65] = new("ROG Harpe Ace Mini", 100, 42000, [MouseLedZone.Wheel]),
        [0x1B1A] = new("ROG Keris II Ace", 100, 42000, [MouseLedZone.Logo]),
        [0x1B18] = new("ROG Keris II Ace", 100, 42000, [MouseLedZone.Logo]),
        [0x1C0E] = new("ROG Keris II Origin", 100, 42000, [MouseLedZone.Logo, MouseLedZone.Wheel, MouseLedZone.Underglow]),
        [0x1D4E] = new("ROG Keris II Origin", 100, 42000, [MouseLedZone.Logo, MouseLedZone.Wheel, MouseLedZone.Underglow]),
        [0x1AD7] = new("ROG Strix Impact III Wireless", 100, 36000, [MouseLedZone.Wheel], Experimental: true),
        [0x1A72] = new("ROG Gladius III Aimpoint", 100, 36000, [MouseLedZone.Logo, MouseLedZone.Wheel, MouseLedZone.Underglow]),
        [0x1A68] = new("ROG Keris Wireless Aimpoint", 100, 36000, [MouseLedZone.Logo]),
        [0x1A6A] = new("ROG Keris Wireless Aimpoint", 100, 36000, [MouseLedZone.Logo]),
    };

    private const ushort ReceiverPid = 0x1ACE;

    /// <summary>The mouse's own product ID when it is plugged in by cable → the ID the receiver lists it under.</summary>
    private static readonly Dictionary<ushort, int> WiredPids = new()
    {
        [0x1A92] = 0x1A94, // ROG Harpe Ace Aim Lab Edition (seen on this PC, 2026-11-14)
    };

    private HidConnection? _ctl;
    /// <summary>0x03 through the receiver; by cable the control interface has no report IDs, so 0x00.</summary>
    private byte _reportId = 0x03;

    public RogMouseModel? Model { get; private set; }
    /// <summary>Connected by its cable rather than through the receiver.</summary>
    public bool Wired { get; private set; }
    public IReadOnlyList<int> PairedPids { get; private set; } = [];
    /// <summary>Polling Rate Booster detected: rates above 1000 Hz are reported differently, so polling is read-only.</summary>
    public bool Booster { get; private set; }

    public override string Name => Model?.Name ?? "ROG mouse";
    public override DeviceKind Kind => DeviceKind.Mouse;
    public override bool Matches(ushort vid, ushort pid) => vid == 0x0B05 && (pid == ReceiverPid || WiredPids.ContainsKey(pid));

    public bool CanWriteSettings => Model is { Experimental: false };
    public bool HasLighting => Model is { Zones.Length: > 0 };

    /// <summary>The cable was plugged in or pulled while connected: switch between cable and receiver.</summary>
    public override bool NeedsReattach(IReadOnlyList<HidDeviceInfo> interfaces) =>
        interfaces.Any(i => WiredPids.ContainsKey(i.ProductId)) != Wired;

    public override void Attach(IReadOnlyList<HidDeviceInfo> interfaces, bool hotPlug)
    {
        // The cable wins: while it is plugged in the mouse talks over it, not over the receiver.
        var cabled = interfaces.Where(i => WiredPids.ContainsKey(i.ProductId)).ToList();
        Wired = cabled.Count > 0;
        _reportId = Wired ? (byte)0x00 : (byte)0x03;
        int pid;
        if (Wired)
        {
            var ctl = cabled.FirstOrDefault(i => i.Interface == 0 && i.UsagePage == 0xFF01 && i.OutputLength >= 64);
            if (ctl is null) { Problem = "mouse control interface not found (cable)"; return; }
            _ctl = HidConnection.OpenReadWrite(ctl);
            pid = WiredPids[ctl.ProductId];
            PairedPids = [];
        }
        else
        {
            var col3 = interfaces.FirstOrDefault(i => i.Interface == 2 && i.UsagePage == 0xFF01 && i.OutputLength >= 64);
            var col1 = interfaces.FirstOrDefault(i => i.Interface == 2 && i.UsagePage == 0xFF02 && i.OutputLength >= 64);
            if (col3 is null) { Problem = "mouse control interface not found"; return; }
            _ctl = HidConnection.OpenReadWrite(col3);

            PairedPids = col1 is null ? [] : QueryPairedPids(col1);
            pid = PairedPids.FirstOrDefault(Models.ContainsKey);
            if (pid == 0) pid = PairedPids.FirstOrDefault();
        }
        Model = Models.GetValueOrDefault(pid);
        try
        {
            var b = Send([0x7D, 0x20, 0x02], requireReply: true);
            Booster = b![5] == 0x01;
        }
        catch (Exception ex) { Log.Warn($"Mouse booster query: {ex.Message}"); }

        string via = Wired ? "by cable" : "via Omni receiver";
        Detail = Model is not null ? $"{Model.Name} ({via})"
            : pid != 0 ? $"unknown ROG mouse, ID {pid:X4} ({via})"
            : "Omni receiver, no mouse paired";
        Problem = null;
        Connected = true;
    }

    public override void Detach()
    {
        _ctl?.Dispose();
        _ctl = null;
        Wired = false;
        base.Detach();
    }

    private static List<int> QueryPairedPids(HidDeviceInfo col1)
    {
        var pids = new List<int>();
        try
        {
            using var c = HidConnection.OpenReadWrite(col1);
            var r = c.Transact([0x01, 0xA0, 0x00, 0x00], x => x.Length > 8 && x[0] == 0x01 && x[1] == 0xA0, 600);
            if (r is null) { Log.Warn("Omni receiver did not list paired devices"); return pids; }
            for (int o = 5; o + 3 < r.Length; o += 4)
            {
                int pid = r[o] | (r[o + 1] << 8);
                if (pid == 0) break;
                pids.Add(pid);
            }
            Log.Info($"Omni receiver paired IDs: {string.Join(", ", pids.Select(p => p.ToString("X4")))}");
        }
        catch (Exception ex) { Log.Warn($"Paired device query failed: {ex.Message}"); }
        return pids;
    }

    // ---------- battery ----------

    public BatteryInfo? ReadBattery()
    {
        if (_ctl is null) return null;
        try
        {
            var r = Send([0x12, 0x07], requireReply: true)!;
            int pct = r[5];
            bool charging = r[10] > 0;
            if (pct <= 0 && !charging) return null; // not ready / asleep
            return new BatteryInfo(Math.Min(pct, 100), charging);
        }
        catch (MouseAsleepException) { return null; }
        catch (TimeoutException) { return null; }
    }

    // ---------- settings ----------

    public MouseSettings ReadSettings()
    {
        var s = new MouseSettings();
        var p = Send([0x12, 0x00], requireReply: true)!;
        s.ProfileRaw = p[11];
        s.ActiveStage = p[12];

        var a = Send([0x12, 0x04, 0x00], requireReply: true)!;
        for (int i = 0; i < 4; i++) s.Dpi[i] = (a[5 + 2 * i] | (a[6 + 2 * i] << 8)) * 50 + 50;
        int pollRaw = Booster && (a[13] >> 4) != 0 ? a[13] >> 4 : a[13];
        s.PollingHz = PollingTable[pollRaw & 0x07];
        s.DebounceMs = DebounceTable[a[15] & 0x07];
        s.AngleSnapping = a[17] == 1;
        s.AngleTuning = (short)(a[20] << 8 | a[19]);

        try
        {
            var acc = Send([0x12, 0x04, 0x01], requireReply: true)!;
            s.Acceleration = Math.Clamp((int)acc[5], 0, 9);
            s.Deceleration = Math.Clamp((int)acc[7], 0, 9);
        }
        catch (Exception ex) { Log.Warn($"Mouse acceleration read: {ex.Message}"); }

        // Mice with separate X/Y DPI keep the authoritative values in block 02 (X at 5+4i).
        try
        {
            var xy = Send([0x12, 0x04, 0x02], requireReply: true)!;
            var x = Enumerable.Range(0, 4).Select(i => (xy[5 + 4 * i] | (xy[6 + 4 * i] << 8)) * 50 + 50).ToArray();
            if (Model is not null && x.All(v => v >= Model.MinDpi && v <= Model.MaxDpi)) s.Dpi = x;
        }
        catch (Exception ex) { Log.Warn($"Mouse X/Y DPI read: {ex.Message}"); }

        var col = Send([0x12, 0x04, 0x03], requireReply: true)!;
        for (int i = 0; i < 4; i++) s.DpiColors[i] = Color.FromArgb(col[5 + 3 * i], col[6 + 3 * i], col[7 + 3 * i]);

        var lod = Send([0x12, 0x06], requireReply: true)!;
        s.LiftOffHigh = lod[8] == 1;

        var b = Send([0x12, 0x07], requireReply: true)!;
        s.SleepMinutes = SleepTable.Any(t => t.Code == b[6]) ? SleepTable.First(t => t.Code == b[6]).Minutes : 0;
        s.LowBatteryWarning = b[7];
        return s;
    }

    /// <summary>Write only what changed, then save to the mouse's onboard memory once.</summary>
    public void ApplySettings(MouseSettings want, MouseSettings have)
    {
        var model = Model ?? throw new NotSupportedException("Unknown mouse model, so settings are read-only.");
        if (model.Experimental) throw new NotSupportedException($"{model.Name} uses a different settings layout; settings are read-only for now.");
        bool changed = false;

        for (int i = 0; i < 4; i++)
        {
            if (want.Dpi[i] == have.Dpi[i] && want.DpiColors[i].ToArgb() == have.DpiColors[i].ToArgb()) continue;
            int dpi = Math.Clamp(want.Dpi[i], model.MinDpi, model.MaxDpi) / 50 * 50;
            ushort enc = (ushort)((dpi - 50) / 50);
            var c = want.DpiColors[i]; // the stage's indicator color travels with its DPI value
            Send([0x51, 0x31, (byte)i, 0x00, (byte)(enc & 0xFF), (byte)(enc >> 8), c.R, c.G, c.B]);
            changed = true;
        }
        if (want.PollingHz != have.PollingHz)
        {
            if (Booster) throw new NotSupportedException("Polling rate can't be changed while the Polling Rate Booster is in use.");
            int code = Array.IndexOf(PollingTable, want.PollingHz);
            if (code is < 0 or > 3) throw new ArgumentException("Polling rate must be 125, 250, 500 or 1000 Hz.");
            Send([0x51, 0x31, 0x04, 0x00, (byte)code]);
            changed = true;
        }
        if (want.DebounceMs != have.DebounceMs)
        {
            int code = Array.IndexOf(DebounceTable, want.DebounceMs);
            if (code < 2) throw new ArgumentException("Button response must be 12–32 ms in steps of 4.");
            Send([0x51, 0x31, 0x05, 0x00, (byte)code]);
            changed = true;
        }
        if (want.Acceleration != have.Acceleration)
        {
            Send([0x51, 0x31, 0x07, 0x00, (byte)Math.Clamp(want.Acceleration, 0, 9)]);
            changed = true;
        }
        if (want.Deceleration != have.Deceleration)
        {
            Send([0x51, 0x31, 0x08, 0x00, (byte)Math.Clamp(want.Deceleration, 0, 9)]);
            changed = true;
        }
        if (want.AngleTuning != have.AngleTuning)
        {
            short angle = (short)Math.Clamp(want.AngleTuning, -30, 30);
            Send([0x51, 0x31, 0x0B, 0x00, (byte)(angle & 0xFF), (byte)((angle >> 8) & 0xFF)]);
            changed = true;
        }
        if (want.ActiveStage != have.ActiveStage && want.ActiveStage is >= 1 and <= 4)
        {
            Send([0x51, 0x31, 0x09, 0x00, (byte)want.ActiveStage]); // 1-based
            changed = true;
        }
        if (want.AngleSnapping != have.AngleSnapping)
        {
            Send([0x51, 0x31, 0x06, 0x00, (byte)(want.AngleSnapping ? 1 : 0)]);
            changed = true;
        }
        if (want.LiftOffHigh != have.LiftOffHigh)
        {
            Send([0x51, 0x35, 0xFF, 0x00, 0xFF, (byte)(want.LiftOffHigh ? 1 : 0)]); // also resets surface calibration
            changed = true;
        }
        if (want.SleepMinutes != have.SleepMinutes || want.LowBatteryWarning != have.LowBatteryWarning)
        {
            var sleep = SleepTable.FirstOrDefault(t => t.Minutes == want.SleepMinutes);
            if (sleep == default && want.SleepMinutes != 1) throw new ArgumentException("Sleep must be 1, 2, 3, 5 or 10 minutes, or never (0).");
            int low = Math.Clamp(want.LowBatteryWarning / 10 * 10, 0, 50);
            Send([0x51, 0x37, 0x00, 0x00, sleep.Code, 0x00, (byte)low]);
            changed = true;
        }
        if (changed) Send([0x50, 0x03]);
    }

    // ---------- onboard profiles ----------

    /// <summary>Onboard profile in use, 1-based.</summary>
    public int GetProfile() => Send([0x12, 0x00], requireReply: true)![11] + 1;

    /// <summary>
    /// Switch the mouse to onboard profile 1–5 (sent 0-based). Not saved: the mouse goes back to its saved profile after a
    /// power cycle, which suits program profiles. Every setting read afterwards belongs to the new profile.
    /// </summary>
    public void SwitchProfile(int oneBased)
    {
        if (oneBased is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(oneBased), "Profiles are 1–5");
        Send([0x50, 0x02, (byte)(oneBased - 1)]);
    }

    /// <summary>Switch profile and keep it as the one the mouse starts with.</summary>
    public void SwitchProfileAndSave(int oneBased)
    {
        SwitchProfile(oneBased);
        Send([0x50, 0x03]);
    }

    // ---------- buttons ----------

    /// <summary>The buttons this kind of mouse has, in the order the mouse stores them (G-Helper / libratbag).</summary>
    public static readonly MouseButtonSlot[] ButtonSlots =
    [
        new(0, "Left button", 0xF0), new(1, "Right button", 0xF1), new(2, "Wheel click", 0xF2), new(3, "Back (side)", 0xE4),
        new(4, "Forward (side)", 0xE5), new(5, "DPI button", 0xE6), new(6, "Scroll up", 0xE8), new(7, "Scroll down", 0xE9),
    ];

    /// <summary>What each button does now, for the current onboard profile.</summary>
    public MouseAction[] ReadButtons()
    {
        var r = Send([0x12, 0x05, 0x00, 0x00, 0x00], requireReply: true)!;
        var result = new MouseAction[ButtonSlots.Length];
        for (int i = 0; i < ButtonSlots.Length; i++)
        {
            int o = 5 + i * 2;
            result[i] = o + 1 < r.Length ? MouseAction.Find(r[o], r[o + 1]) : MouseAction.Find(ButtonSlots[i].SourceCode, 1);
        }
        return result;
    }

    /// <summary>Change buttons (the left button can't be changed, so a click always works), then save once.</summary>
    public void WriteButtons(IReadOnlyDictionary<int, MouseAction> changes)
    {
        bool any = false;
        foreach (var (slot, action) in changes)
        {
            if (slot == 0) continue; // never remap the left button
            var src = ButtonSlots[slot];
            Send([0x51, 0x21, 0x00, 0x00, src.SourceCode, 0x01, action.Code, action.Type]);
            any = true;
        }
        if (any) Send([0x50, 0x03]);
    }

    /// <summary>Every button back to what it does out of the box.</summary>
    public void RestoreDefaultButtons()
    {
        foreach (var s in ButtonSlots) Send([0x51, 0x21, 0x00, 0x00, s.SourceCode, 0x01, s.SourceCode, 0x01]);
        Send([0x50, 0x03]);
    }

    /// <summary>The mouse's serial number (03 12 12 02 → 12 ASCII characters at [5]).</summary>
    public string ReadSerial()
    {
        var r = Send([0x12, 0x12, 0x02], requireReply: true)!;
        return System.Text.Encoding.ASCII.GetString(r, 5, Math.Min(12, r.Length - 5)).TrimEnd('\0', ' ');
    }

    // ---------- lighting ----------

    /// <summary>Raw lighting of one zone: mode, brightness, color, direction, random, speed (as stored in the mouse).</summary>
    public sealed record ZoneLighting(MouseLedZone Zone, byte Mode, byte Brightness, Color Color, byte Direction, byte Random, byte Speed)
    {
        public override string ToString() => $"{Zone}: mode {Mode} brightness {Brightness} #{Color.R:X2}{Color.G:X2}{Color.B:X2} dir {Direction} random {Random} speed {Speed}";
    }

    public IReadOnlyList<ZoneLighting> ReadLighting()
    {
        var model = Model ?? throw new NotSupportedException("Unknown mouse model");
        var list = new List<ZoneLighting>();
        for (int i = 0; i < model.Zones.Length; i++)
        {
            // The read takes the zone's position in the model's zone list (the write takes the zone id).
            var r = Send([0x12, 0x03, (byte)i], requireReply: true)!;
            list.Add(new ZoneLighting(model.Zones[i], r[5], r[6], Color.FromArgb(r[7], r[8], r[9]), r[11], r[12], r[13]));
        }
        return list;
    }

    /// <summary>The first zone's lighting in user terms (all zones are set together).</summary>
    public MouseLighting ReadCurrentLighting()
    {
        var z = ReadLighting()[0];
        // Modes above 6 (G-Helper sends 0xF0) and brightness 0 both mean the light is off.
        var mode = z.Mode > 6 || z.Brightness == 0 ? MouseLightMode.Off
            : Enum.IsDefined(typeof(MouseLightMode), z.Mode) ? (MouseLightMode)z.Mode : MouseLightMode.Static;
        return new MouseLighting(mode, z.Color, z.Brightness is 0 or > 100 ? 100 : z.Brightness);
    }

    /// <summary>persist = save to the mouse's onboard memory.</summary>
    public void ApplyLighting(MouseLighting l, bool persist)
    {
        var model = Model ?? throw new NotSupportedException("Unknown mouse model, so its lighting zones are unknown.");
        byte mode = (byte)l.Mode; // Off is its own mode, 0xF0
        byte brightness = l.Mode == MouseLightMode.Off ? (byte)0 : (byte)Math.Clamp(l.Brightness, 1, 100);
        var c = l.Color;
        foreach (var zone in model.Zones)
            Send([0x51, 0x28, (byte)zone, 0x00, mode, brightness, c.R, c.G, c.B, 0x00, 0x00, 0x00]);
        if (persist) Send([0x50, 0x03]);
    }

    // ---------- protocol ----------

    /// <summary>
    /// Send one command (without the report ID) and read the echoed reply. A reply of FF AA means the mouse is
    /// asleep or out of range. Writes are accepted even when the receiver sends no echo.
    /// </summary>
    private byte[]? Send(byte[] cmd, bool requireReply = false)
    {
        var ctl = _ctl ?? throw new InvalidOperationException("Mouse not connected");
        var pkt = new byte[1 + cmd.Length];
        pkt[0] = _reportId;
        cmd.CopyTo(pkt, 1);
        int match = Math.Min(pkt.Length, cmd[0] == 0x12 && cmd.Length >= 3 ? 4 : 3);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var r = ctl.Transact(pkt, x => x.Length > 3 && x[0] == pkt[0] &&
                ((x[1] == 0xFF && x[2] == 0xAA) || x.AsSpan(0, match).SequenceEqual(pkt.AsSpan(0, match))), 300);
            if (r is null) { if (!requireReply) return null; continue; }
            if (r[1] == 0xFF && r[2] == 0xAA) throw new MouseAsleepException();
            return r;
        }
        throw new TimeoutException($"Mouse did not answer command {Convert.ToHexString(cmd)}");
    }
}
