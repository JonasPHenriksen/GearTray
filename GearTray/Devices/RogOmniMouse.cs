using GearTray.Hid;
using GearTray.Util;

namespace GearTray.Devices;

public enum MouseLedZone : byte { Logo = 0, Wheel = 1, Underglow = 2 }

/// <summary>Values are the mouse's own mode ids (Off is sent as Static at brightness 0).</summary>
public enum MouseLightMode : byte { Static = 0, Breathing = 1, ColorCycle = 2, Rainbow = 3, Reactive = 4, Battery = 6, Off = 0xFE }

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
/// ASUS ROG wireless mouse behind a ROG Omni receiver (0B05:1ACE). Protocol per G-Helper and libratbag:
/// 64-byte output reports with report ID 0x03 on interface 2 / collection 3 (usage page 0xFF01); replies echo the command.
/// The paired mouse model is read from collection 1 (usage page 0xFF02, report ID 0x01, command A0).
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

    private HidConnection? _ctl;

    public RogMouseModel? Model { get; private set; }
    public IReadOnlyList<int> PairedPids { get; private set; } = [];
    /// <summary>Polling Rate Booster detected: rates above 1000 Hz are reported differently, so polling is read-only.</summary>
    public bool Booster { get; private set; }

    public override string Name => Model?.Name ?? "ROG mouse";
    public override DeviceKind Kind => DeviceKind.Mouse;
    public override bool Matches(ushort vid, ushort pid) => vid == 0x0B05 && pid == 0x1ACE;

    public bool CanWriteSettings => Model is { Experimental: false };
    public bool HasLighting => Model is { Zones.Length: > 0 };

    public override void Attach(IReadOnlyList<HidDeviceInfo> interfaces, bool hotPlug)
    {
        var col3 = interfaces.FirstOrDefault(i => i.Interface == 2 && i.UsagePage == 0xFF01 && i.OutputLength >= 64);
        var col1 = interfaces.FirstOrDefault(i => i.Interface == 2 && i.UsagePage == 0xFF02 && i.OutputLength >= 64);
        if (col3 is null) { Problem = "mouse control interface not found"; return; }
        _ctl = HidConnection.OpenReadWrite(col3);

        PairedPids = col1 is null ? [] : QueryPairedPids(col1);
        int pid = PairedPids.FirstOrDefault(Models.ContainsKey);
        if (pid == 0) pid = PairedPids.FirstOrDefault();
        Model = Models.GetValueOrDefault(pid);
        try
        {
            var b = Send([0x7D, 0x20, 0x02], requireReply: true);
            Booster = b![5] == 0x01;
        }
        catch (Exception ex) { Log.Warn($"Mouse booster query: {ex.Message}"); }

        Detail = Model is not null ? $"{Model.Name} (via Omni receiver)"
            : pid != 0 ? $"unknown ROG mouse, ID {pid:X4} (via Omni receiver)"
            : "Omni receiver, no mouse paired";
        Problem = null;
        Connected = true;
    }

    public override void Detach()
    {
        _ctl?.Dispose();
        _ctl = null;
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
        int pollRaw = Booster ? a[13] >> 4 : a[13];
        s.PollingHz = PollingTable[pollRaw & 0x07];
        s.DebounceMs = DebounceTable[a[15] & 0x07];
        s.AngleSnapping = a[17] == 1;

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
            if (code < 1) throw new ArgumentException("Button response must be 8–32 ms in steps of 4.");
            Send([0x51, 0x31, 0x05, 0x00, (byte)code]);
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
        var mode = z.Brightness == 0 ? MouseLightMode.Off
            : Enum.IsDefined(typeof(MouseLightMode), z.Mode) ? (MouseLightMode)z.Mode : MouseLightMode.Static;
        return new MouseLighting(mode, z.Color, z.Brightness == 0 ? 100 : z.Brightness);
    }

    /// <summary>persist = save to the mouse's onboard memory.</summary>
    public void ApplyLighting(MouseLighting l, bool persist)
    {
        var model = Model ?? throw new NotSupportedException("Unknown mouse model, so its lighting zones are unknown.");
        byte mode = l.Mode == MouseLightMode.Off ? (byte)MouseLightMode.Static : (byte)l.Mode;
        byte brightness = l.Mode == MouseLightMode.Off ? (byte)0 : (byte)Math.Clamp(l.Brightness, 1, 100);
        var c = l.Color;
        foreach (var zone in model.Zones)
            Send([0x51, 0x28, (byte)zone, 0x00, mode, brightness, c.R, c.G, c.B, 0x00, 0x00, 0x07]);
        if (persist) Send([0x50, 0x03]);
    }

    // ---------- protocol ----------

    /// <summary>
    /// Send one command (without the report ID) and read the echoed reply. A reply of FF AA means the mouse is
    /// asleep or out of range. Writes are accepted even when the receiver sends no echo.
    /// </summary>
    private byte[]? Send(byte[] cmd, bool requireReply = false)
    {
        var ctl = _ctl ?? throw new InvalidOperationException("Mouse receiver not connected");
        var pkt = new byte[1 + cmd.Length];
        pkt[0] = 0x03;
        cmd.CopyTo(pkt, 1);
        int match = Math.Min(pkt.Length, cmd[0] == 0x12 && cmd.Length >= 3 ? 4 : 3);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            var r = ctl.Transact(pkt, x => x.Length > 3 && x[0] == 0x03 &&
                ((x[1] == 0xFF && x[2] == 0xAA) || x.AsSpan(0, match).SequenceEqual(pkt.AsSpan(0, match))), 300);
            if (r is null) { if (!requireReply) return null; continue; }
            if (r[1] == 0xFF && r[2] == 0xAA) throw new MouseAsleepException();
            return r;
        }
        throw new TimeoutException($"Mouse did not answer command {Convert.ToHexString(cmd)}");
    }
}
