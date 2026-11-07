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
    public int EqPreset { get; set; }            // 0–3, 4 = custom
    public double[] EqBandsDb { get; set; } = new double[10];
    public int ChatMixGame { get; set; }         // 0–100
    public int ChatMixChat { get; set; }         // 0–100
    public int OledBrightness { get; set; }      // 1–10

    public HeadsetState Clone()
    {
        var c = (HeadsetState)MemberwiseClone();
        c.EqBandsDb = (double[])EqBandsDb.Clone();
        return c;
    }
}

/// <summary>
/// SteelSeries Arctis Nova Pro Wireless (1038:12E0, also 12E5 / 225D), protocol per HeadsetControl, Linux-Arctis-Manager,
/// arctis-omni-linux and HeadsetBatteryTray: 64-byte output reports with report ID 0x06 on interface 4, collection 1
/// (usage page 0xFFC0). Queries are echoed back (06 xx …). Writes are followed by 06 09 to save in the base station.
/// </summary>
public sealed class ArctisNovaProWireless : GearDevice, IHasBattery
{
    public static readonly int[] AutoOffMinutes = [0, 1, 5, 10, 15, 30, 60];
    public static readonly string[] EqPresetNames = ["Flat", "Bass boost", "Focus", "Smiley", "Custom"];

    private HidConnection? _cmd;   // COL01, usage page 0xFFC0
    private HidConnection? _evt;   // COL02, usage page 0xFF00 (fallback for replies)
    private DateTime _readyAt;

    public override string Name => "Arctis Nova Pro Wireless";
    public override DeviceKind Kind => DeviceKind.Headset;
    public override bool Matches(ushort vid, ushort pid) => vid == 0x1038 && pid is 0x12E0 or 0x12E5 or 0x225D;

    public override void Attach(IReadOnlyList<HidDeviceInfo> interfaces, bool hotPlug)
    {
        var col1 = interfaces.FirstOrDefault(i => i.Interface == 4 && i.UsagePage == 0xFFC0 && i.OutputLength >= 64);
        var col2 = interfaces.FirstOrDefault(i => i.Interface == 4 && i.UsagePage == 0xFF00 && i.InputLength >= 64);
        if (col1 is null) { Problem = "base station control interface not found"; return; }
        _cmd = HidConnection.OpenReadWrite(col1);
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
        _evt?.Dispose(); _evt = null;
        base.Detach();
    }

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
            h.EqPreset = a[6];
            for (int i = 0; i < 10; i++) h.EqBandsDb[i] = (a[7 + i] - 20) / 2.0;
            h.MicVolume = a[17];
            h.Sidetone = a[18];
            h.ChatMixGame = a[20];
            h.ChatMixChat = a[21];
        }
        var d = Query(0x80);
        if (d is not null) h.OledBrightness = d[3];
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
            TransparencyLevel = s[8],
            MicMuted = s[9] != 0,
            Noise = s[10] <= 2 ? (NoiseMode)s[10] : NoiseMode.Off,
            MicLedBrightness = s[11],
            AutoOffIndex = s[12],
        };
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

        bool bandsChanged = !want.EqBandsDb.SequenceEqual(have.EqBandsDb);
        if (want.EqPreset == 4 && (bandsChanged || have.EqPreset != 4))
        {
            W(0x2E, 0x04);
            var bands = new byte[11];
            bands[0] = 0x33;
            for (int i = 0; i < 10; i++) bands[1 + i] = (byte)Math.Clamp((int)Math.Round(0x14 + 2 * want.EqBandsDb[i]), 0, 40);
            W(bands);
        }
        else if (want.EqPreset != have.EqPreset)
        {
            W(0x2E, (byte)Math.Clamp(want.EqPreset, 0, 3));
        }
        if (changed) Write([0x09]); // save to the base station
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
        Thread.Sleep(10);
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
