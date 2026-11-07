using System.ComponentModel;
using GearTray.Hid;
using GearTray.Util;

namespace GearTray.Devices;

/// <summary>
/// Keyboard effects. The first group runs inside the keyboard; Ambient and AudioMeter are drawn by GearTray
/// and streamed to the keyboard frame by frame (they only run while GearTray is running).
/// </summary>
public enum KeyboardEffect { Off, Static, Breathing, BreathingRandom, Spectrum, Wave, Reactive, Starlight, StarlightRandom, Ambient, AudioMeter }

/// <summary>Keyboard lighting. Color is used by Static, Breathing, Reactive, Starlight and the audio meter's underglow. Speed 1 (fast) – 3 (slow).</summary>
public sealed record KeyboardLighting(KeyboardEffect Effect, Color Color, int Brightness, bool WaveRightToLeft = false, int Speed = 2)
{
    public static readonly KeyboardEffect[] UsesColor = [KeyboardEffect.Static, KeyboardEffect.Breathing, KeyboardEffect.Reactive, KeyboardEffect.Starlight, KeyboardEffect.AudioMeter];
    public bool IsSoftware => Effect is KeyboardEffect.Ambient or KeyboardEffect.AudioMeter;
}

/// <summary>
/// Razer Huntsman Elite (1532:0226). Protocol per OpenRazer / OpenRGB:
/// 91-byte feature reports (report ID 0 + 90-byte Razer report) on interface 2, the mouse-usage collection.
/// Windows only lets an elevated process open that collection, so this device needs admin rights.
/// Uses the "extended matrix" command class 0x0F on LED 0x05 (keys + underglow).
/// </summary>
public sealed class RazerHuntsmanElite : GearDevice
{
    private const int ReportLength = 91;
    private const byte TxMatrix = 0x3F, TxStandard = 0xFF;
    private const byte NoStore = 0x00, VarStore = 0x01;
    private const byte BacklightLed = 0x05, GameLed = 0x08;

    private HidConnection? _ctl;

    public override string Name => "Razer Huntsman Elite";
    public override DeviceKind Kind => DeviceKind.Keyboard;
    public override bool Matches(ushort vid, ushort pid) => vid == 0x1532 && pid == 0x0226;

    public override void Attach(IReadOnlyList<HidDeviceInfo> interfaces, bool hotPlug)
    {
        var info = interfaces.FirstOrDefault(i => i.Interface == 2);
        if (info is null) { Problem = "control interface not found"; return; }
        try
        {
            _ctl = HidConnection.OpenFeatureOnly(info);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5)
        {
            Problem = Elevation.IsAdmin
                ? "Windows denied access to the keyboard's control channel"
                : "needs admin rights (start GearTray through its startup task)";
            return;
        }
        var fw = Query(0x00, 0x81, 0x02, [], TxStandard);
        Detail = $"firmware v{fw[9]}.{fw[10]}";
        Problem = null;
        Connected = true;
    }

    public override void Detach()
    {
        _ctl?.Dispose();
        _ctl = null;
        base.Detach();
    }

    // ---------- lighting ----------

    /// <summary>persist = store in the keyboard (user changes); false = live only (re-applying at startup).</summary>
    public void Apply(KeyboardLighting l, bool persist)
    {
        byte store = persist ? VarStore : NoStore;
        var c = l.Color;
        int speed = Math.Clamp(l.Speed, 1, 3);
        switch (l.Effect)
        {
            case KeyboardEffect.Off: Effect(store, 0x06, 0x00); return; // "none" turns the LEDs off
            case KeyboardEffect.Static: Effect(store, 0x09, 0x01, 0x00, 0x00, 0x01, c.R, c.G, c.B); break;
            case KeyboardEffect.Breathing: Effect(store, 0x09, 0x02, 0x01, 0x00, 0x01, c.R, c.G, c.B); break;
            case KeyboardEffect.BreathingRandom: Effect(store, 0x06, 0x02); break;
            case KeyboardEffect.Spectrum: Effect(store, 0x06, 0x03); break;
            case KeyboardEffect.Wave: Effect(store, 0x06, 0x04, (byte)(l.WaveRightToLeft ? 0x01 : 0x00), 0x28); break;
            case KeyboardEffect.Reactive: Effect(store, 0x09, 0x05, 0x00, (byte)speed, 0x01, c.R, c.G, c.B); break;
            case KeyboardEffect.Starlight: Effect(store, 0x09, 0x07, 0x00, (byte)speed, 0x01, c.R, c.G, c.B); break;
            case KeyboardEffect.StarlightRandom: Effect(store, 0x06, 0x07, 0x00, (byte)speed); break;
            default: return; // software effects are streamed with SendFrame
        }
        SetBrightness(l.Brightness, persist);
    }

    // ---------- per-key frames (software effects) ----------

    /// <summary>Rows 0–5 are the keys, rows 6–8 the underglow; 22 columns each.</summary>
    public const int MatrixRows = 9, MatrixCols = 22;
    private bool _customModeShown;

    /// <summary>
    /// Show one frame: rgb holds MatrixRows × MatrixCols × 3 bytes. Rows are sent without waiting for replies
    /// (as OpenRGB does), then the keyboard is switched to its custom-frame mode. Nothing is stored in the keyboard.
    /// </summary>
    public void SendFrame(ReadOnlySpan<byte> rgb)
    {
        var ctl = _ctl ?? throw new InvalidOperationException("Keyboard not connected");
        for (int row = 0; row < MatrixRows; row++)
        {
            var args = new byte[5 + MatrixCols * 3];
            args[2] = (byte)row;
            args[3] = 0;
            args[4] = MatrixCols - 1;
            rgb.Slice(row * MatrixCols * 3, MatrixCols * 3).CopyTo(args.AsSpan(5));
            ctl.SetFeature(BuildReport(0x0F, 0x03, 0x47, args, TxMatrix));
            Thread.Sleep(1);
        }
        if (!_customModeShown)
        {
            SetBrightness(100, persist: false);           // software effects scale their own colors
            _customModeShown = true;
        }
        ctl.SetFeature(BuildReport(0x0F, 0x02, 0x0C, [NoStore, 0x00, 0x08], TxMatrix)); // show the custom frame
        Thread.Sleep(1);
    }

    /// <summary>Call when a software effect stops, so the next one re-sends brightness.</summary>
    public void EndFrames() => _customModeShown = false;

    // ---------- polling rate ----------

    private static readonly (byte Code, int Hz)[] PollingCodes = [(0x01, 1000), (0x02, 500), (0x08, 125)];

    /// <summary>Polling rate in Hz, or null if the keyboard doesn't support the query.</summary>
    public int? GetPollingRate()
    {
        try
        {
            byte code = Query(0x00, 0x85, 0x01, [], TxStandard)[9];
            int hz = PollingCodes.FirstOrDefault(p => p.Code == code).Hz;
            return hz > 0 ? hz : null;
        }
        catch (Exception ex) when (ex is NotSupportedException or TimeoutException or IOException)
        {
            Log.Info($"Keyboard polling rate not available: {ex.Message}");
            return null;
        }
    }

    public void SetPollingRate(int hz)
    {
        var p = PollingCodes.FirstOrDefault(x => x.Hz == hz);
        if (p.Hz == 0) throw new ArgumentException("Polling rate must be 125, 500 or 1000 Hz");
        Query(0x00, 0x05, 0x01, [p.Code], TxStandard);
    }

    public void SetBrightness(int percent, bool persist)
    {
        byte value = (byte)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0);
        Query(0x0F, 0x04, 0x03, [persist ? VarStore : NoStore, BacklightLed, value]);
    }

    public int GetBrightness()
    {
        var r = Query(0x0F, 0x84, 0x03, [VarStore, BacklightLed]);
        return (int)Math.Round(r[11] * 100 / 255.0);
    }

    // ---------- game mode ----------

    public bool GetGameMode() => Query(0x03, 0x80, 0x03, [VarStore, GameLed], TxStandard)[11] != 0;

    public void SetGameMode(bool on) => Query(0x03, 0x00, 0x03, [VarStore, GameLed, (byte)(on ? 1 : 0)], TxStandard);

    public string GetSerial()
    {
        var r = Query(0x00, 0x82, 0x16, [], TxStandard);
        return System.Text.Encoding.ASCII.GetString(r, 9, 22).TrimEnd('\0', ' ');
    }

    // ---------- protocol ----------

    /// <summary>Extended-matrix effect: args = [storage, LED 0x05, effectId, rest...].</summary>
    private void Effect(byte storage, byte size, byte effectId, params byte[] rest)
    {
        var args = new byte[3 + rest.Length];
        args[0] = storage; args[1] = BacklightLed; args[2] = effectId;
        rest.CopyTo(args, 3);
        Query(0x0F, 0x02, size, args);
    }

    /// <summary>
    /// Send one command and wait for its reply. Buffer layout (91 bytes):
    /// [0] report id 0, [1] status, [2] transaction id, [3..4] remaining packets, [5] protocol,
    /// [6] data size, [7] command class, [8] command id, [9..88] arguments, [89] CRC (XOR of [3..88]), [90] reserved.
    /// </summary>
    private static byte[] BuildReport(byte cls, byte id, byte size, ReadOnlySpan<byte> args, byte tx)
    {
        if (args.Length > 80 || size > 80) throw new ArgumentException("Razer argument block is at most 80 bytes");
        var req = new byte[ReportLength];
        req[2] = tx; req[6] = size; req[7] = cls; req[8] = id;
        args.CopyTo(req.AsSpan(9));
        byte crc = 0;
        for (int i = 3; i < 89; i++) crc ^= req[i];
        req[89] = crc;
        return req;
    }

    private byte[] Query(byte cls, byte id, byte size, ReadOnlySpan<byte> args, byte tx = TxMatrix)
    {
        var ctl = _ctl ?? throw new InvalidOperationException("Keyboard not connected");
        var req = BuildReport(cls, id, size, args, tx);
        _customModeShown = false; // any normal command may leave custom-frame mode

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Thread.Sleep(2);
            ctl.SetFeature(req);
            for (int read = 0; read < 25; read++)
            {
                Thread.Sleep(2);
                var resp = ctl.GetFeature(0x00, ReportLength);
                if (resp[2] != tx || resp[7] != cls || resp[8] != id) continue;
                switch (resp[1])
                {
                    case 0x02: return resp;               // success
                    case 0x00 or 0x01: continue;          // new / busy
                    case 0x05: throw new NotSupportedException($"Keyboard does not support command {cls:X2}/{id:X2}");
                    default:
                        Log.Warn($"Razer {cls:X2}/{id:X2} status {resp[1]:X2}");
                        goto nextAttempt;
                }
            }
            nextAttempt:;
        }
        throw new TimeoutException($"Keyboard did not answer command {cls:X2}/{id:X2}");
    }
}
