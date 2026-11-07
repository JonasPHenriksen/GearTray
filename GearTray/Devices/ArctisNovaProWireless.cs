using System.Text;
using GearTray.Hid;
using GearTray.Util;

namespace GearTray.Devices;

public enum NoiseMode { Off = 0, Transparency = 1, NoiseCancelling = 2 }

/// <summary>State read from the base station (status 06 B0 + audio 06 20 + display 06 80).</summary>
public sealed class HeadsetState
{
    public bool HeadsetOnline { get; set; }
    public int? BatteryPercent { get; set; }
    public bool Charging { get; set; }
    public int? SpareBatteryPercent { get; set; }
    public NoiseMode Noise { get; set; }
    public int TransparencyLevel { get; set; }   // 1–10
    public bool MicMuted { get; set; }
    public int MicLedBrightness { get; set; }    // 1–10
    public int AutoOffIndex { get; set; }        // index into AutoOffMinutes
    public int Sidetone { get; set; }            // 0–3
    public int MicVolume { get; set; }           // 1–10
    public int EqPreset { get; set; }            // 0–18, 4 = custom
    public double[] EqBandsDb { get; set; } = new double[10];
    public int ChatMixGame { get; set; }         // 0–100
    public int ChatMixChat { get; set; }         // 0–100
    public int OledBrightness { get; set; }      // 1–10
    public int VolumePercent { get; set; }       // 0–100 (the base station dial)
    public bool HighGain { get; set; }           // headphone output gain
    public bool LineOutStreamMix { get; set; }   // false = ChatMix, true = Stream Mix
    public int StreamMain { get; set; }          // 0–100
    public int StreamAux { get; set; }           // 0–100
    public int StreamMic { get; set; }           // 0–100
    public bool HomeScreenSimple { get; set; }
    public int DimTimerIndex { get; set; }       // index into DimMinutes
    public bool BluetoothAutoOn { get; set; }
    public int BluetoothCall { get; set; }       // 0 nothing, 1 lower other audio, 2 mute other audio
    public bool WirelessRange { get; set; }      // false = speed, true = range
    public int BluetoothMode { get; set; }       // 1 off, 2 pairing, 4 linked
    public int BluetoothStatus { get; set; }     // 1 ready, 2 lost, 4 busy, 8 error

    public HeadsetState Clone()
    {
        var c = (HeadsetState)MemberwiseClone();
        c.EqBandsDb = (double[])EqBandsDb.Clone();
        return c;
    }
}

/// <summary>
/// SteelSeries Arctis Nova Pro Wireless (1038:12E0, also 12E5 / 225D), protocol per HeadsetControl, Linux-Arctis-Manager,
/// Arctis-Sound-Manager, arctis-omni-linux, ggoled and HeadsetBatteryTray: 64-byte output reports with report ID 0x06 on
/// interface 4, collection 1 (usage page 0xFFC0). Queries are echoed back (06 xx …). Writes are followed by 06 09 to save
/// in the base station. The base station's 128×64 screen is drawn with 1024-byte feature reports (06 93).
/// </summary>
public sealed class ArctisNovaProWireless : GearDevice, IHasBattery
{
    public static readonly int[] AutoOffMinutes = [0, 1, 5, 10, 15, 30, 60];
    public static readonly int[] DimMinutes = [0, 1, 5, 10, 15, 30, 60];
    public static readonly string[] EqPresetNames =
    [
        "Flat", "Bass boost", "Focus", "Smiley", "Custom", "Apex Legends", "Baldur's Gate 3", "CoD MWII", "CoD Warzone 2",
        "Destiny 2", "Diablo IV", "Fortnite", "FPS footsteps", "GTA V", "Minecraft", "Overwatch 2", "PUBG", "Rainbow Six Siege", "Rocket League",
    ];
    public const int ScreenWidth = 128, ScreenHeight = 64;

    private HidConnection? _cmd;   // COL01, usage page 0xFFC0 (commands; screen as feature reports)
    private HidConnection? _feat;  // COL01 again, synchronous, for the 1024-byte screen reports
    private HidConnection? _evt;   // COL02, usage page 0xFF00 (events: dial, ChatMix, …)
    private DateTime _readyAt;

    public override string Name => "Arctis Nova Pro Wireless";
    public override DeviceKind Kind => DeviceKind.Headset;
    public override bool Matches(ushort vid, ushort pid) => vid == 0x1038 && pid is 0x12E0 or 0x12E5 or 0x225D;

    /// <summary>When the user last turned the dial or ChatMix knob (the base station owns the screen for ~3 s after).</summary>
    public DateTime LastKnobUse { get; private set; }

    public override void Attach(IReadOnlyList<HidDeviceInfo> interfaces, bool hotPlug)
    {
        var col1 = interfaces.FirstOrDefault(i => i.Interface == 4 && i.UsagePage == 0xFFC0 && i.OutputLength >= 64);
        var col2 = interfaces.FirstOrDefault(i => i.Interface == 4 && i.UsagePage == 0xFF00 && i.InputLength >= 64);
        if (col1 is null) { Problem = "base station control interface not found"; return; }
        _cmd = HidConnection.OpenReadWrite(col1);
        if (col1.FeatureLength >= 1024)
        {
            try { _feat = HidConnection.OpenSync(col1); } catch (Exception ex) { Log.Warn($"Headset screen channel: {ex.Message}"); }
        }
        if (col2 is not null)
        {
            try { _evt = HidConnection.OpenReadWrite(col2); } catch (Exception ex) { Log.Warn($"Headset event channel: {ex.Message}"); }
        }
        // SteelSeries GG waits ~5 s after the base station appears before talking to it.
        _readyAt = hotPlug ? DateTime.UtcNow.AddSeconds(5) : DateTime.UtcNow;
        Detail = "base station";
        Problem = null;
        Connected = true;
    }

    public override void Detach()
    {
        _cmd?.Dispose(); _cmd = null;
        _feat?.Dispose(); _feat = null;
        _evt?.Dispose(); _evt = null;
        base.Detach();
    }

    public bool CanDrawScreen => _feat is not null;

    public BatteryInfo? ReadBattery()
    {
        try
        {
            var s = Query(0xB0);
            if (s is null) return null;
            var state = ParseStatus(s);
            return state.HeadsetOnline && state.BatteryPercent is int p ? new BatteryInfo(p, state.Charging) : null;
        }
        catch (TimeoutException) { return null; }
    }

    /// <summary>Status only (one query): battery, spare battery, noise mode, connection.</summary>
    public HeadsetState ReadStatus() =>
        ParseStatus(Query(0xB0) ?? throw new TimeoutException("Base station did not answer the status query"));

    public HeadsetState ReadState()
    {
        var h = ReadStatus();
        var a = Query(0x20);
        if (a is not null)
        {
            h.VolumePercent = (int)Math.Round((56 - Math.Min(a[3], (byte)56)) * 100 / 56.0);
            h.HighGain = a[4] == 2;
            h.EqPreset = a[6];
            for (int i = 0; i < 10; i++) h.EqBandsDb[i] = (a[7 + i] - 20) / 2.0;
            h.MicVolume = a[17];
            h.Sidetone = a[18];
            h.LineOutStreamMix = a[19] == 2;
            h.ChatMixGame = a[20];
            h.ChatMixChat = a[21];
            h.StreamMain = a[22];
            h.StreamAux = a[24];
            h.StreamMic = a[25];
        }
        var d = Query(0x80);
        if (d is not null)
        {
            h.DimTimerIndex = d[2];
            h.OledBrightness = d[3];
            h.HomeScreenSimple = d[5] == 1;
        }
        return h;
    }

    private static HeadsetState ParseStatus(byte[] s)
    {
        static int? Level(byte raw) => raw <= 8 ? (int)Math.Round(raw * 12.5) : null;
        byte state = s[15];
        return new HeadsetState
        {
            HeadsetOnline = state is 0x02 or 0x04 or 0x08,
            BatteryPercent = Level(s[6]),
            Charging = state is 0x02 or 0x04,
            SpareBatteryPercent = Level(s[7]),
            BluetoothAutoOn = s[2] == 1,
            BluetoothCall = s[3] <= 2 ? s[3] : 0,
            BluetoothMode = s[4],
            BluetoothStatus = s[5],
            TransparencyLevel = s[8],
            MicMuted = s[9] != 0,
            Noise = s[10] <= 2 ? (NoiseMode)s[10] : NoiseMode.Off,
            MicLedBrightness = s[11],
            AutoOffIndex = s[12],
            WirelessRange = s[13] == 1,
        };
    }

    /// <summary>Firmware of base DSP, base MCU, headset MCU and headset Bluetooth (06 10).</summary>
    public string[] ReadFirmware()
    {
        var r = Query(0x10) ?? throw new TimeoutException("Base station did not report its firmware");
        var parts = new string[4];
        for (int i = 0; i < 4; i++)
        {
            int o = 2 + i * 12;
            parts[i] = o + 12 <= r.Length ? Encoding.ASCII.GetString(r, o, 12).TrimEnd('\0', ' ') : "";
        }
        return parts;
    }

    /// <summary>Write only what changed, then save once (06 09).</summary>
    public void ApplyState(HeadsetState want, HeadsetState have)
    {
        bool changed = false;
        void W(params byte[] cmd) { Write(cmd); changed = true; }

        if (want.Noise != have.Noise) W(0xBD, (byte)want.Noise);
        if (want.TransparencyLevel != have.TransparencyLevel) W(0xB9, (byte)Math.Clamp(want.TransparencyLevel, 1, 10));
        if (want.Sidetone != have.Sidetone) W(0x39, (byte)Math.Clamp(want.Sidetone, 0, 3));
        if (want.MicVolume != have.MicVolume) W(0x37, (byte)Math.Clamp(want.MicVolume, 1, 10));
        if (want.MicLedBrightness != have.MicLedBrightness) W(0xBF, (byte)Math.Clamp(want.MicLedBrightness, 1, 10));
        if (want.AutoOffIndex != have.AutoOffIndex) W(0xC1, (byte)Math.Clamp(want.AutoOffIndex, 0, 6));
        if (want.OledBrightness != have.OledBrightness) W(0x85, (byte)Math.Clamp(want.OledBrightness, 1, 10));
        if (want.DimTimerIndex != have.DimTimerIndex) W(0x83, (byte)Math.Clamp(want.DimTimerIndex, 0, 6));
        if (want.HomeScreenSimple != have.HomeScreenSimple) W(0x89, (byte)(want.HomeScreenSimple ? 1 : 0));
        if (want.HighGain != have.HighGain) W(0x27, (byte)(want.HighGain ? 2 : 1));
        if (want.LineOutStreamMix != have.LineOutStreamMix) W(0x43, (byte)(want.LineOutStreamMix ? 2 : 1));
        if (want.StreamMain != have.StreamMain || want.StreamAux != have.StreamAux || want.StreamMic != have.StreamMic)
        {
            byte main = (byte)Math.Clamp(want.StreamMain, 0, 100);
            // All four bytes, as SteelSeries GG sends them (main repeated); a short write zeroes the mic level.
            W(0x47, main, main, (byte)Math.Clamp(want.StreamAux, 0, 100), (byte)Math.Clamp(want.StreamMic, 0, 100));
        }
        if (want.BluetoothAutoOn != have.BluetoothAutoOn) W(0xB2, (byte)(want.BluetoothAutoOn ? 1 : 0));
        if (want.BluetoothCall != have.BluetoothCall) W(0xB3, (byte)Math.Clamp(want.BluetoothCall, 0, 2));
        if (want.WirelessRange != have.WirelessRange) W(0xC3, (byte)(want.WirelessRange ? 1 : 0));
        if (want.VolumePercent != have.VolumePercent)
            W(0x25, (byte)Math.Clamp((int)Math.Round((100 - want.VolumePercent) * 56 / 100.0), 0, 56));

        bool bandsChanged = !want.EqBandsDb.SequenceEqual(have.EqBandsDb);
        if (want.EqPreset == 4 && (bandsChanged || have.EqPreset != 4))
        {
            W(0x2E, 0x04);
            var bands = new byte[11];
            bands[0] = 0x33;
            for (int i = 0; i < 10; i++) bands[1 + i] = (byte)Math.Clamp((int)Math.Round(0x14 + 2 * want.EqBandsDb[i]), 0, 40);
            W(bands); // always all 10 bands
        }
        else if (want.EqPreset != have.EqPreset)
        {
            W(0x2E, (byte)Math.Clamp(want.EqPreset, 0, EqPresetNames.Length - 1));
        }
        if (changed) Write([0x09]); // save to the base station
    }

    // ---------- base station screen ----------

    /// <summary>
    /// Show a 128×64 picture (pixels[x + y*128] = lit) on the base station's screen. Sent as two 1024-byte feature
    /// reports (06 93 x y w h + column-major pixels, lowest bit = top pixel), per ggoled.
    /// </summary>
    public void DrawScreen(ReadOnlySpan<bool> pixels)
    {
        var feat = _feat ?? throw new NotSupportedException("This base station's screen can't be drawn on.");
        WaitUntilReady();
        for (int half = 0; half < 2; half++)
        {
            var buf = new byte[1024];
            buf[0] = 0x06; buf[1] = 0x93; buf[2] = (byte)(half * 64); buf[3] = 0; buf[4] = 64; buf[5] = 64;
            for (int x = 0; x < 64; x++)
                for (int y = 0; y < ScreenHeight; y++)
                {
                    if (!pixels[(half * 64 + x) + y * ScreenWidth]) continue;
                    int i = x * 64 + y;
                    buf[6 + (i >> 3)] |= (byte)(1 << (i & 7));
                }
            feat.SetFeature(buf);
        }
    }

    /// <summary>Give the screen back to the base station's own display.</summary>
    public void RestoreScreen() => Write([0x95]);

    /// <summary>Read pending events (dial, ChatMix knob) so we know when the base station is using its screen.</summary>
    public void DrainEvents()
    {
        if (_evt is null) return;
        for (int i = 0; i < 32; i++)
        {
            var e = _evt.Read(1);
            if (e is null) break;
            if (e.Length > 1 && e[0] == 0x07 && e[1] is 0x25 or 0x45 or 0x31 or 0x47) LastKnobUse = DateTime.UtcNow;
        }
    }

    // ---------- protocol ----------

    private void WaitUntilReady()
    {
        var wait = _readyAt - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) Thread.Sleep(wait);
    }

    private void Write(byte[] cmd)
    {
        var c = _cmd ?? throw new InvalidOperationException("Base station not connected");
        WaitUntilReady();
        var pkt = new byte[1 + cmd.Length];
        pkt[0] = 0x06;
        cmd.CopyTo(pkt, 1);
        c.Write(pkt);
        Thread.Sleep(50); // SteelSeries GG keeps at least 50 ms between commands
    }

    /// <summary>Send 06 &lt;cmd&gt; and wait for the echoed 06 &lt;cmd&gt; reply (on collection 1, or collection 2 as a fallback).</summary>
    private byte[]? Query(byte cmd)
    {
        var c = _cmd ?? throw new InvalidOperationException("Base station not connected");
        WaitUntilReady();
        bool IsReply(byte[] r) => r.Length > 16 && r[0] == 0x06 && r[1] == cmd;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            c.Flush();
            _evt?.Flush();
            c.Write([0x06, cmd]);
            var deadline = Environment.TickCount64 + 700;
            while (Environment.TickCount64 < deadline)
            {
                var r = c.Read(60);
                if (r is not null && IsReply(r)) return r;
                if (_evt is not null)
                {
                    var e = _evt.Read(20);
                    if (e is not null && IsReply(e)) return e;
                }
            }
        }
        Log.Warn($"Base station did not answer 06 {cmd:X2}");
        return null;
    }
}
